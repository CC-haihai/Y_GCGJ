using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoLayer.Core;

namespace AutoLayer.UI.QuickSwitch;

public static class PreviewHighlighter
{
	private static List<ObjectId> _currentHighlighted = new List<ObjectId>();

	private static string _lastLayerName = null;

	// 「直接亮显」：Entity.Highlight() 在图元图形上直接画高亮。
	// v1.9e 实测：即便加了 Highlight，模态面板开着时图上还是不显示 ——
	// Highlight 只是把图元标成"待重绘"，真正的绘制通常要等编辑器空闲时刷一帧。
	// 所以「回到上次选中」现在做四件套（全在 LockDocument 里）：
	//   1. implied selection（面板关掉后 grips 靠它）
	//   2. Entity.Highlight() 逐个亮显
	//   3. 不在当前视野时自动平移视图到这批图元（看得见是"回到上次选中"的前提）
	//   4. Editor.Regen() 强制立刻刷一帧，不等空闲
	// 异常一律写进 ALTRACE（回归亮显失败不再是黑盒）。列表行预览仍只用
	// implied selection（层上图元可能成千上万，逐个开图元太重，且那条路一直正常）。
	private static List<ObjectId> _directHighlighted = new List<ObjectId>();

	// 图层 → 图元索引（面板一次会话内有效）。
	//
	// 原来每次换行都调 LayerService.GetObjectsOnLayer：它要把模型空间和当前布局的
	// 每个图元逐个 GetObject 再比 LayerId。十万图元的图点一次行就是十万次对象打开，
	// 目标层是空层时连 500 的提前退出都触发不到 —— 等于每次点击全表扫描一遍。
	// 现在改成会话内建一次索引，之后每行 O(1)。面板关闭时由 EndSession 丢弃。
	private static Dictionary<string, List<ObjectId>> _layerIndex;
	private static Document _layerIndexDoc;

	/// <summary>丢掉图层索引。面板关闭 / 图元被移动后调，下次预览会重建。</summary>
	public static void EndSession()
	{
		_layerIndex = null;
		_layerIndexDoc = null;
	}

	/// <summary>索引失效（图元刚被移过层），下次预览重建。</summary>
	public static void InvalidateIndex()
	{
		_layerIndex = null;
	}

	/// <summary>
	/// 预热索引：面板显示之后在后台（Background 优先级）先建好，
	/// 用户点第一行时就不用现场扫一遍全图 —— 大图上那一下能有 100~300ms。
	/// 建失败或已经建过都直接放过，真正的兜底仍在 <see cref="ObjectsOnLayerCached"/> 里。
	/// </summary>
	public static void WarmIndex()
	{
		try
		{
			Document doc = Application.DocumentManager.MdiActiveDocument;
			if (doc == null) return;
			if (_layerIndex != null && ReferenceEquals(_layerIndexDoc, doc)) return;

			_layerIndex = LayerService.BuildLayerIndex(doc.Database);
			_layerIndexDoc = doc;
		}
		catch { }
	}

	static List<ObjectId> ObjectsOnLayerCached(Document doc, string layerName)
	{
		if (_layerIndex == null || !ReferenceEquals(_layerIndexDoc, doc))
		{
			_layerIndex = LayerService.BuildLayerIndex(doc.Database);
			_layerIndexDoc = doc;
		}

		List<ObjectId> list;
		if (_layerIndex.TryGetValue(layerName, out list) && list != null && list.Count > 0)
			return list;

		return new List<ObjectId>();
	}

	public static void HighlightLayer(string layerName)
	{
		Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
		ClearDirectHighlight();
		if (mdiActiveDocument == null || (_lastLayerName == layerName && _currentHighlighted.Count > 0)) return;
		if (string.IsNullOrEmpty(layerName))
		{
			ClearHighlight();
			return;
		}

		List<ObjectId> objectsOnLayer = ObjectsOnLayerCached(mdiActiveDocument, layerName);
		if (objectsOnLayer.Count == 0)
		{
			// 这一层没有图元：撤掉上一次的预览，别让用户以为新选的那层亮着
			ClearHighlight();
			return;
		}

		// 直接整体替换隐含选择集：不必先清空再设置（那是两次 CAD 调用）。
		// 注意必须复制一份 —— ClearHighlight 会 Clear 这个列表，
		// 直接引用索引里那份就会把索引本身清空，下次预览同一层就空了。
		_currentHighlighted = new List<ObjectId>(objectsOnLayer);
		_lastLayerName = layerName;
		try
		{
			ObjectId[] array = new ObjectId[objectsOnLayer.Count];
			for (int i = 0; i < objectsOnLayer.Count; i++) array[i] = objectsOnLayer[i];
			mdiActiveDocument.Editor.SetImpliedSelection(SelectionSet.FromObjectIds(array));
		}
		catch
		{
		}
	}

	public static void ClearHighlight()
	{
		if (_currentHighlighted.Count > 0)
		{
			Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
			if (mdiActiveDocument != null)
			{
				try
				{
					mdiActiveDocument.Editor.SetImpliedSelection(new ObjectId[0]);
				}
				catch
				{
				}
			}
			_currentHighlighted.Clear();
			_lastLayerName = null;
		}
		ClearDirectHighlight();
	}

	public static void RestoreSelection(ObjectId[] selectedObjects)
	{
		ClearHighlight();
		Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
		if (mdiActiveDocument == null || selectedObjects == null || selectedObjects.Length == 0) return;
		try
		{
			SelectionSet impliedSelection = SelectionSet.FromObjectIds(selectedObjects);
			mdiActiveDocument.Editor.SetImpliedSelection(impliedSelection);
		}
		catch
		{
		}
	}

	/// <summary>
	/// 「回到上次选中」专用：implied selection + 直接亮显 + 视野定位 + 强制重绘。
	/// 之后的行预览（HighlightLayer）/ 清理（ClearHighlight）都会把直接亮显一并撤掉。
	/// </summary>
	public static void RestoreSelectionWithHighlight(ObjectId[] selectedObjects)
	{
		RestoreSelection(selectedObjects);
		HighlightObjects(selectedObjects);
	}

	private static void HighlightObjects(ObjectId[] objects)
	{
		ClearDirectHighlight();
		Document doc = Application.DocumentManager.MdiActiveDocument;
		if (doc == null || objects == null || objects.Length == 0) return;
		List<ObjectId> highlighted = new List<ObjectId>();
		string error = null;
		try
		{
			using (doc.LockDocument())
			{
				var tm = doc.Database.TransactionManager;
				Extents3d? box = null;
				using (Transaction tr = tm.StartTransaction())
				{
					foreach (ObjectId id in objects)
					{
						try
						{
							if (!id.IsValid || id.IsErased || id.IsEffectivelyErased) continue;
							Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
							if (entity == null) continue;
							entity.Highlight();
							highlighted.Add(id);
							try
							{
								Extents3d ext = entity.GeometricExtents;
								box = box == null
									? ext
									: new Extents3d(
										new Point3d(
											Math.Min(box.Value.MinPoint.X, ext.MinPoint.X),
											Math.Min(box.Value.MinPoint.Y, ext.MinPoint.Y),
											Math.Min(box.Value.MinPoint.Z, ext.MinPoint.Z)),
										new Point3d(
											Math.Max(box.Value.MaxPoint.X, ext.MaxPoint.X),
											Math.Max(box.Value.MaxPoint.Y, ext.MaxPoint.Y),
											Math.Max(box.Value.MaxPoint.Z, ext.MaxPoint.Z)));
							}
							catch { }
						}
						catch (Exception ex)
						{
							error = (error == null ? "" : error + "; ") + "亮显:" + ex.Message;
						}
					}
					tr.Commit();
				}
				_directHighlighted = highlighted;
				if (box != null) ZoomIfNeeded(doc, box.Value);
				try
				{
					doc.Editor.Regen(); // 强制刷一帧：模态面板开着时 Highlight 的重绘不等空闲就不会出现
				}
				catch (Exception ex)
				{
					error = (error == null ? "" : error + "; ") + "重绘:" + ex.Message;
				}
			}
		}
		catch (Exception ex)
		{
			error = (error ?? "") + "; " + ex.Message;
		}
		_directHighlighted = highlighted;
		Core.Trace.Add("[图层切换] 回归亮显 " + highlighted.Count + "/" + objects.Length
			+ (error == null ? "" : "（异常：" + error + "）"));
	}

	/// <summary>这批图元没完全落在当前视野里时，平移（不缩放比例）视图把它们框进来。</summary>
	private static void ZoomIfNeeded(Document doc, Extents3d box)
	{
		try
		{
			Editor ed = doc.Editor;
			ViewTableRecord view = ed.GetCurrentView();
			// WCS 包围盒 → 当前视图（DCS）坐标
			Matrix3d mat = Matrix3d.WorldToPlane(view.ViewDirection)
				* Matrix3d.Displacement(view.Target - Point3d.Origin);
			Extents3d dcs = new Extents3d(box.MinPoint.TransformBy(mat), box.MaxPoint.TransformBy(mat));
			double halfW = view.Width / 2.0, halfH = view.Height / 2.0;
			double vcx = view.CenterPoint.X, vcy = view.CenterPoint.Y;
			bool fullyVisible = dcs.MinPoint.X >= vcx - halfW && dcs.MaxPoint.X <= vcx + halfW
				&& dcs.MinPoint.Y >= vcy - halfH && dcs.MaxPoint.Y <= vcy + halfH;
			if (fullyVisible) return;

			double w = Math.Abs(dcs.MaxPoint.X - dcs.MinPoint.X);
			double h = Math.Abs(dcs.MaxPoint.Y - dcs.MinPoint.Y);
			if (w < 1e-6) w = view.Width * 0.1; // 薄线 / 单点：给个最小视野
			if (h < 1e-6) h = view.Height * 0.1;
			double ratio = view.Width / view.Height;
			if (w / h > ratio) h = w / ratio; else w = h * ratio; // 保持视口宽高比
			view.Width = w * 1.3;
			view.Height = h * 1.3;
			view.CenterPoint = new Point2d((dcs.MinPoint.X + dcs.MaxPoint.X) / 2.0, (dcs.MinPoint.Y + dcs.MaxPoint.Y) / 2.0);
			ed.SetCurrentView(view);
		}
		catch (Exception ex)
		{
			Core.Trace.Add("[图层切换] 回归定位异常: " + ex.Message);
		}
	}

	private static void ClearDirectHighlight()
	{
		if (_directHighlighted.Count == 0) return;
		Document mdiActiveDocument = Application.DocumentManager.MdiActiveDocument;
		if (mdiActiveDocument != null)
		{
			try
			{
				using (mdiActiveDocument.LockDocument())
				{
					var tm = mdiActiveDocument.Database.TransactionManager;
					using (Transaction tr = tm.StartTransaction())
					{
						foreach (ObjectId id in _directHighlighted)
						{
							try
							{
								if (!id.IsValid || id.IsErased || id.IsEffectivelyErased) continue;
								Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
								entity?.Unhighlight();
							}
							catch
							{
							}
						}
						tr.Commit();
					}
				}
			}
			catch
			{
			}
		}
		_directHighlighted.Clear();
	}
}
