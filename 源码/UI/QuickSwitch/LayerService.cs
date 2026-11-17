using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AutoLayer.UI.QuickSwitch;

public static class LayerService
{
	public static List<LayerInfo> GetAllLayers(Database db)
	{
		List<LayerInfo> list = new List<LayerInfo>();
		using (Transaction transaction = db.TransactionManager.StartTransaction())
		{
			foreach (ObjectId item in (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead))
			{
				if (!item.IsErased)
				{
					LayerTableRecord layerTableRecord = (LayerTableRecord)transaction.GetObject(item, OpenMode.ForRead);
					if (!layerTableRecord.IsDependent)
					{
						list.Add(new LayerInfo
						{
							Name = layerTableRecord.Name,
							AcadColor = layerTableRecord.Color.ColorValue,
							IsLocked = layerTableRecord.IsLocked,
							IsFrozen = layerTableRecord.IsFrozen,
							IsOff = layerTableRecord.IsOff,
							Prefix = LayerCategoryService.ExtractPrefix(layerTableRecord.Name)
						});
					}
				}
			}
			transaction.Commit();
		}
		list.Sort();
		return list;
	}

	/// <summary>
	/// 逐图元移动结果（交互 §3.8：部分成功/失败时面板不关，模式条给出可读原因）。
	/// moved = 成功改层；skipped = 所在图层被锁定等原因开写失败；erased = 图元已删除/失效。
	/// </summary>
	public sealed class MoveResult
	{
		public int Moved;
		public int Skipped;
		public int Erased;
		public string FirstError;
	}

	/// <summary>
	/// 把 AutoCAD 的 ErrorStatus 翻成面板上能看懂的中文。
	///
	/// 直接抛 <c>ex.Message</c> 只会得到 "eLockViolation" 这种枚举名 —— 用户既不知道
	/// 是图层锁还是文档锁，也不知道下一步该干什么。面板是给画图的人用的，不是给
	/// ObjectARX 开发者用的，错误文案必须能直接指导操作。
	/// </summary>
	public static string DescribeError(Exception ex)
	{
		if (ex == null) return "未知错误";

		if (ex is Autodesk.AutoCAD.Runtime.Exception acEx)
		{
			switch (acEx.ErrorStatus)
			{
				case Autodesk.AutoCAD.Runtime.ErrorStatus.LockViolation:
					return "图元所在图层被锁定，请先解锁该图层";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.NotOpenForWrite:
					return "图元不可编辑（可能被锁定或在外部参照中）";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.WasOpenForWrite:
				case Autodesk.AutoCAD.Runtime.ErrorStatus.WasOpenForRead:
					return "图元正被其他操作占用，请重试";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased:
				case Autodesk.AutoCAD.Runtime.ErrorStatus.NullObjectId:
				case Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidObjectId:
					return "图元已删除或失效";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.WrongDatabase:
					return "图元不在当前图纸中";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.NotApplicable:
					return "该图元不支持此操作";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidInput:
					return "参数无效";
				case Autodesk.AutoCAD.Runtime.ErrorStatus.FileNotFound:
					return "文件未找到";
				default:
					return acEx.ErrorStatus.ToString();
			}
		}

		return ex.Message;
	}

	/// <summary>
	/// 把一批图元移到目标层，逐个开写、失败不拖垮整批。
	/// 一把事务全开、遇到锁定图元直接抛异常整批回滚的写法无法表达
	/// 「2 个已移动，1 个被跳过」——CC 的部分成功反馈依赖本方法。
	/// </summary>
	public static MoveResult MoveObjectsToLayerEx(ObjectId[] objectIds, string layerName)
	{
		MoveResult result = new MoveResult();
		Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
		if (mdiActiveDocument == null) { result.FirstError = "没有活动文档"; return result; }
		Database database = mdiActiveDocument.Database;
		// CC 面板是模态窗口，改层由 WPF 事件触发：此时 AutoCAD 不认为当前线程持有
		// 文档写锁，Commit 会抛 eLockViolation —— 报出来就是「没能移动: eLockViolation」，
		// 和图层有没有被锁无关（用户查图层会白查一遍）。所有写库路径都必须先锁文档。
		using DocumentLock documentLock = mdiActiveDocument.LockDocument();
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		if (!layerTable.Has(layerName))
		{
			result.FirstError = "图层 '" + layerName + "' 不存在";
			transaction.Abort();
			return result;
		}
		ObjectId layerId = layerTable[layerName];
		// 目标层被锁/冻结时改 Clayer 会抛 —— 记为整体失败原因
		for (int i = 0; i < objectIds.Length; i++)
		{
			ObjectId id = objectIds[i];
			if (id.IsErased || !id.IsValid) { result.Erased++; continue; }
			try
			{
				Entity entity = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
				if (entity == null) { result.Erased++; continue; }
				entity.LayerId = layerId;
				result.Moved++;
			}
			catch (Exception ex)
			{
				result.Skipped++;
				if (result.FirstError == null) result.FirstError = DescribeError(ex);
			}
		}
		try { transaction.Commit(); }
		catch (Exception ex)
		{
			// 整体提交失败：全部计为跳过，把原因带出去
			result.Skipped += result.Moved;
			result.Moved = 0;
			result.FirstError = DescribeError(ex);
		}
		return result;
	}

	/// <summary>
	/// 把当前图层切到 layerName。
	///
	/// **必须走 CLAYER 系统变量通道，不能只写 db.Clayer**：
	/// CC 面板在模态对话框里应用，直接给 Database.Clayer 赋值只改了数据库，
	/// 编辑器侧的系统变量缓存不会跟上 —— 之后绘图命令拿到的"当前图层"可能还是旧值，
	/// 表现就是「CC 切完层，归层工具画线不归层 / 线落在别的层」。
	/// SetSystemVariable 是官方同步通道，数据库、命令行、图层下拉、编辑器缓存一起动。
	/// </summary>
	public static void SetCurrentLayer(string layerName)
	{
		Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
		if (mdiActiveDocument == null) return;
		Database database = mdiActiveDocument.Database;
		// 同 MoveObjectsToLayerEx：模态面板里切 Clayer / 写 CLAYER 也要先锁文档，
		// 否则 CLAYER 系统变量写入同样抛 eLockViolation（被下面的 catch 吞掉后
		// 表现为「当前层没切过去」）。
		using DocumentLock documentLock = mdiActiveDocument.LockDocument();
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		if (layerTable.Has(layerName))
		{
			ObjectId id = layerTable[layerName];
			database.Clayer = id;
			// 系统变量通道同步一遍；层被冻结时它会抛（db.Clayer 那次也被静默忽略），吞掉即可
			try { Application.SetSystemVariable("CLAYER", layerName); }
			catch { }
			if (database.Clayer != id)
			{
				AutoLayer.Core.Trace.Add("[图层切换] 当前层没切到 " + layerName + "（被 CAD 忽略，目标层可能被冻结）");
			}
		}
		transaction.Commit();
	}

	/// <summary>
	/// 当前活动图纸的数据库；没有活动图纸（CAD 正在切换/关闭图纸）时返回 null。
	/// 这些方法原来直接 <c>MdiActiveDocument.Database</c>，图纸关掉的那一瞬间会 NRE，
	/// 而调用点在 WPF 事件里，NRE 冒出去会打断整个面板的收尾流程。
	/// </summary>
	static Database ActiveDb()
	{
		Document doc = Application.DocumentManager.MdiActiveDocument;
		return doc == null ? null : doc.Database;
	}

	public static bool IsLayerOff(string layerName)
	{
		Database database = ActiveDb();
		if (database == null) return false;
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		if (layerTable.Has(layerName))
		{
			bool isOff = ((LayerTableRecord)transaction.GetObject(layerTable[layerName], OpenMode.ForRead)).IsOff;
			transaction.Commit();
			return isOff;
		}
		transaction.Commit();
		return false;
	}

	/// <summary>目标层已冻结时的内联提示用（交互 §3.8：只提示不阻止）。</summary>
	public static bool IsLayerFrozen(string layerName)
	{
		Database database = ActiveDb();
		if (database == null) return false;
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		if (layerTable.Has(layerName))
		{
			bool isFrozen = ((LayerTableRecord)transaction.GetObject(layerTable[layerName], OpenMode.ForRead)).IsFrozen;
			transaction.Commit();
			return isFrozen;
		}
		transaction.Commit();
		return false;
	}

	public static void TurnOnLayer(string layerName)
	{
		Document doc = Application.DocumentManager.MdiActiveDocument;
		if (doc == null) return;
		Database database = doc.Database;
		using DocumentLock documentLock = doc.LockDocument();
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		if (layerTable.Has(layerName))
		{
			((LayerTableRecord)transaction.GetObject(layerTable[layerName], OpenMode.ForWrite)).IsOff = false;
		}
		transaction.Commit();
	}

	/// <summary>
	/// 一次性建立「图层名 → 该层图元 ID」索引（模型空间 + 当前布局）。
	///
	/// 为什么要有它：图层切换面板的"点一行亮一层"原来是每换一行调一次
	/// <see cref="GetObjectsOnLayer"/>，那个方法必须把整张图纸的图元逐个 GetObject
	/// 再比 LayerId —— 十万图元的图，点一次行就是十万次对象打开；目标层若是空层，
	/// 连 maxCount 的提前退出都触发不到，等于每次点击都全表扫描一遍。
	///
	/// 面板一次会话只需要这份索引一次：建索引一次全扫，之后每行 O(1)。
	/// 索引在面板关闭时由 <c>PreviewHighlighter.EndSession</c> 丢弃，
	/// 所以面板开着期间用户把图元移到别的层不会让索引长期失真。
	/// </summary>
	public static Dictionary<string, List<ObjectId>> BuildLayerIndex(Database db, int maxPerLayer = 500)
	{
		var index = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
		if (db == null) return index;

		try
		{
			using (Transaction tr = db.TransactionManager.StartTransaction())
			{
				// 图层 ObjectId → 名字只查一次，后面每个图元都靠它归桶
				var names = new Dictionary<ObjectId, string>();
				var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
				foreach (ObjectId lid in lt)
				{
					try
					{
						var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
						if (ltr != null) names[lid] = ltr.Name;
					}
					catch { }
				}

				var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
				ObjectId msId = bt[BlockTableRecord.ModelSpace];
				Bucket(tr, (BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead), names, index, maxPerLayer);

				ObjectId layoutId = ObjectId.Null;
				try
				{
					LayoutManager lm = LayoutManager.Current;
					layoutId = lm.GetLayoutId(lm.CurrentLayout);
				}
				catch { }

				if (!layoutId.IsNull && layoutId != msId)
				{
					try
					{
						Bucket(tr, (BlockTableRecord)tr.GetObject(layoutId, OpenMode.ForRead), names, index, maxPerLayer);
					}
					catch { }
				}

				tr.Commit();
			}
		}
		catch { }

		return index;
	}

	static void Bucket(Transaction tr, BlockTableRecord btr,
		Dictionary<ObjectId, string> names,
		Dictionary<string, List<ObjectId>> index, int maxPerLayer)
	{
		if (btr == null) return;

		foreach (ObjectId id in btr)
		{
			try
			{
				if (id.IsErased) continue;
				Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
				if (ent == null) continue;

				string name;
				if (!names.TryGetValue(ent.LayerId, out name) || string.IsNullOrEmpty(name)) continue;

				List<ObjectId> list;
				if (!index.TryGetValue(name, out list))
				{
					list = new List<ObjectId>();
					index[name] = list;
				}
				if (list.Count < maxPerLayer) list.Add(id);
			}
			catch { }
		}
	}

	public static List<ObjectId> GetObjectsOnLayer(string layerName, int maxCount = 500)
	{
		List<ObjectId> list = new List<ObjectId>();
		Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
		if (mdiActiveDocument == null) return list;
		Database database = mdiActiveDocument.Database;
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		if (!layerTable.Has(layerName))
		{
			transaction.Commit();
			return list;
		}
		ObjectId objectId = layerTable[layerName];
		ObjectId objectId2 = ((BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace];
		foreach (ObjectId item in (BlockTableRecord)transaction.GetObject(objectId2, OpenMode.ForRead))
		{
			if (list.Count >= maxCount) break;
			Entity entity = transaction.GetObject(item, OpenMode.ForRead) as Entity;
			if (entity != null && entity.LayerId == objectId) list.Add(item);
		}
		ObjectId layoutId = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
		if (layoutId != objectId2)
		{
			foreach (ObjectId item2 in (BlockTableRecord)transaction.GetObject(layoutId, OpenMode.ForRead))
			{
				if (list.Count >= maxCount) break;
				if (!item2.IsErased)
				{
					Entity entity2 = transaction.GetObject(item2, OpenMode.ForRead) as Entity;
					if (entity2 != null && entity2.LayerId == objectId) list.Add(item2);
				}
			}
		}
		transaction.Commit();
		return list;
	}

	/// <summary>
	/// 图层名 → ObjectId。
	///
	/// 原来是把整张图层表遍历一遍、每层 GetObject 一次再比名字 —— 图层一多就是几百次
	/// 对象打开。符号表的索引器本身就是大小写不敏感的哈希查找，Has + 索引两步就够，
	/// 行为和原来完全一致（AutoCAD 图层名不区分大小写）。
	/// </summary>
	private static ObjectId FindLayerId(LayerTable lt, string name)
	{
		if (string.IsNullOrEmpty(name) || !lt.Has(name)) return ObjectId.Null;
		return lt[name];
	}

	public static void ToggleLock(string layerName)
	{
		Document doc = Application.DocumentManager.MdiActiveDocument;
		if (doc == null) return;
		Database database = doc.Database;
		using DocumentLock documentLock = doc.LockDocument();
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		ObjectId id = FindLayerId(lt, layerName);
		if (!id.IsNull)
		{
			LayerTableRecord obj = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite);
			obj.IsLocked = !obj.IsLocked;
		}
		transaction.Commit();
	}

	public static void ToggleOnOff(string layerName)
	{
		Document doc = Application.DocumentManager.MdiActiveDocument;
		if (doc == null) return;
		Database database = doc.Database;
		using DocumentLock documentLock = doc.LockDocument();
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		ObjectId id = FindLayerId(lt, layerName);
		if (!id.IsNull)
		{
			LayerTableRecord obj = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite);
			obj.IsOff = !obj.IsOff;
		}
		transaction.Commit();
	}

	public static void ToggleFreeze(string layerName)
	{
		Document doc = Application.DocumentManager.MdiActiveDocument;
		if (doc == null) return;
		Database database = doc.Database;
		using DocumentLock documentLock = doc.LockDocument();
		using Transaction transaction = database.TransactionManager.StartTransaction();
		LayerTable lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
		ObjectId id = FindLayerId(lt, layerName);
		if (!id.IsNull)
		{
			LayerTableRecord obj = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite);
			obj.IsFrozen = !obj.IsFrozen;
		}
		transaction.Commit();
	}
}
