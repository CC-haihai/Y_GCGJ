using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace VpLayer
{
    /// <summary>
    /// 视口图层状态（VPLAYER 冻结）的读取与应用。
    ///
    /// 只用 2016 就有的接口：
    ///   Viewport.GetFrozenLayers / IsLayerFrozenInViewport
    ///   Viewport.FreezeLayersInViewport / ThawLayersInViewport / ThawAllLayersInViewport
    /// 这套接口只对图纸空间布局里的视口（MVIEW）有效；模型空间多视口没有对应 API。
    /// </summary>
    public static class VpService
    {
        public const string DxfName = "VIEWPORT";

        /// <summary>ObjectId 是不是一个视口实体（按 DXF 名判断，块参照里的同名类不会误判）。</summary>
        public static bool IsViewport(ObjectId id)
        {
            if (id.IsNull || !id.IsValid) return false;
            try
            {
                Autodesk.AutoCAD.Runtime.RXClass cls = id.ObjectClass;
                return cls != null && string.Equals(cls.DxfName, DxfName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 是不是图纸空间的主视口（Number = 1）。
        /// 主视口代表图纸空间本身，没有 VPLAYER 冻结可记 —— 刷它会存下一个空状态，
        /// 所以在刷取阶段就挡掉，避免用户以为自己记住了什么。
        /// </summary>
        public static bool IsMainViewport(Database db, ObjectId vpId)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Viewport vp = tr.GetObject(vpId, OpenMode.ForRead) as Viewport;
                    bool main = vp != null && vp.Number == 1;
                    tr.Commit();
                    return main;
                }
            }
            catch { return false; }
        }

        /// <summary>读视口里被冻结的图层名（按名字返回，便于跨图纸匹配）。</summary>
        public static List<string> ReadFrozenNames(Database db, ObjectId vpId, out string error)
        {
            var names = new List<string>();
            error = null;
            if (db == null || vpId.IsNull) { error = "无效的视口"; return names; }

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Viewport vp = tr.GetObject(vpId, OpenMode.ForRead) as Viewport;
                    if (vp == null) { error = "选中的不是视口"; return names; }

                    ObjectIdCollection frozen = vp.GetFrozenLayers();
                    if (frozen != null)
                    {
                        foreach (ObjectId layerId in frozen)
                        {
                            LayerTableRecord ltr = tr.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
                            if (ltr != null) names.Add(ltr.Name);
                        }
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public sealed class ApplyResult
        {
            public bool Ok;
            public int Frozen;                              // 实际冻结了几层
            public int Missing;                             // 本图纸没有、被跳过的层数
            public readonly List<string> MissingNames = new List<string>();
            public string Error;
        }

        /// <summary>
        /// 把一套状态刷到视口上。
        ///
        /// 顺序很重要：先 ThawAllLayersInViewport 把所有层解冻，再冻结集合里的层。
        /// 这样结果是「与状态完全一致」，而不是「在目标视口原状态上叠加」——
        /// 后者会出现「模板里没有、目标视口里还冻着」的残留，统一就失效了。
        /// </summary>
        public static ApplyResult Apply(Database db, ObjectId vpId, VpState state)
        {
            var res = new ApplyResult();
            if (db == null || vpId.IsNull) { res.Error = "无效的视口"; return res; }
            if (state == null) { res.Error = "状态为空"; return res; }

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Viewport vp = tr.GetObject(vpId, OpenMode.ForWrite) as Viewport;
                    if (vp == null) { res.Error = "选中的不是视口"; return res; }

                    res = ApplyIn(tr, db, vp, state);
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                res.Error = ex.Message;
                res.Ok = false;
            }

            return res;
        }

        /// <summary>
        /// 事务内版本。批量生成时外层已经有事务，AutoCAD 不支持嵌套事务，
        /// 所以必须走这个重载 —— 直接在外部事务里把状态刷到刚建好的视口上。
        /// </summary>
        public static ApplyResult ApplyIn(Transaction tr, Database db, Viewport vp, VpState state)
        {
            var res = new ApplyResult();
            if (tr == null || db == null || vp == null || state == null)
            {
                res.Error = "参数无效";
                return res;
            }

            try
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                vp.ThawAllLayersInViewport();

                var ids = new ObjectIdCollection();
                foreach (string n in state.FrozenLayers)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    string name = n.Trim();

                    if (lt.Has(name)) ids.Add(lt[name]);
                    else { res.Missing++; res.MissingNames.Add(name); }
                }

                if (ids.Count > 0)
                    vp.FreezeLayersInViewport((IEnumerator)ids.GetEnumerator());

                res.Frozen = ids.Count;
                res.Ok = true;
            }
            catch (System.Exception ex)
            {
                res.Error = ex.Message;
                res.Ok = false;
            }

            return res;
        }

        /// <summary>当前布局里所有视口。includeMain=false 时跳过图纸空间那个主视口（Number=1）。</summary>
        public static List<ObjectId> ViewportsInLayout(Database db, ObjectId layoutBtrId, bool includeMain)
        {
            var list = new List<ObjectId>();
            if (db == null || layoutBtrId.IsNull) return list;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord btr = tr.GetObject(layoutBtrId, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null) { tr.Commit(); return list; }

                    foreach (ObjectId id in btr)
                    {
                        if (!IsViewport(id)) continue;

                        if (!includeMain)
                        {
                            Viewport vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                            if (vp != null && vp.Number == 1) continue;
                        }
                        list.Add(id);
                    }
                    tr.Commit();
                }
            }
            catch { }

            return list;
        }

        /// <summary>
        /// 全图所有布局里的浮视口（跳过图纸空间主视口）。
        /// 体检用：看一共有多少视口、多少打了标记、多少是关着的。
        /// </summary>
        public static List<ObjectId> AllViewports(Database db, out int closedCount)
        {
            closedCount = 0;
            var list = new List<ObjectId>();
            if (db == null) return list;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary layouts = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (layouts == null) { tr.Commit(); return list; }

                    foreach (DBDictionaryEntry entry in layouts)
                    {
                        Layout lay = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                        if (lay == null) continue;

                        // 模型空间不是"布局视口"，里面的东西不参与体检
                        if (lay.BlockTableRecordId == SymbolUtilityServices.GetBlockModelSpaceId(db)) continue;

                        BlockTableRecord btr = tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr == null) continue;

                        foreach (ObjectId id in btr)
                        {
                            if (!IsViewport(id)) continue;

                            Viewport vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                            if (vp == null || vp.Number == 1) continue;

                            list.Add(id);
                            if (!vp.On) closedCount++;
                        }
                    }

                    tr.Commit();
                }
            }
            catch { }

            return list;
        }

        /// <summary>视口是不是被关掉了（On=false）。读不到时按"没关"处理，不误报。</summary>
        public static bool IsViewportClosed(Database db, ObjectId vpId)
        {
            if (db == null || vpId.IsNull) return false;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Viewport vp = tr.GetObject(vpId, OpenMode.ForRead) as Viewport;
                    bool off = vp != null && !vp.On;
                    tr.Commit();
                    return off;
                }
            }
            catch { return false; }
        }

        /// <summary>当前布局的 BlockTableRecord；取不到返回 ObjectId.Null。</summary>
        public static ObjectId CurrentLayoutBtr(Database db)
        {
            try
            {
                string name = LayoutManager.Current.CurrentLayout;
                if (string.IsNullOrEmpty(name)) return ObjectId.Null;

                ObjectId layoutId = LayoutManager.Current.GetLayoutId(name);
                if (layoutId.IsNull) return ObjectId.Null;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Layout lay = tr.GetObject(layoutId, OpenMode.ForRead) as Layout;
                    ObjectId btrId = lay == null ? ObjectId.Null : lay.BlockTableRecordId;
                    tr.Commit();
                    return btrId;
                }
            }
            catch { return ObjectId.Null; }
        }

        // ------------------------------------------------------------------ 图框拾取

        /// <summary>
        /// 把「用户点/框选的对象」变成一个可复用的图框块。
        ///
        /// 三种情况：
        ///   1. 选中的正好是一个块参照（含属性块）—— 直接用，记块名和缩放；
        ///   2. 选中的是一组散线 —— 自动组成一个块，块基点定在外框左下角，
        ///      然后在原位置插回一个块参照，图纸看起来没变化；
        ///   3. 组块失败 —— 返回 false，调用方提示，图纸不变（事务回滚）。
        ///
        /// 统一成块的意义：生成时一律插块，属性块的标题栏属性也能跟着带过去。
        /// </summary>
        public static bool PrepareFrameBlock(Database db, ObjectId[] ids, FrameTemplate f,
                                             out Point3d outerMin, out string error)
        {
            outerMin = Point3d.Origin;
            error = null;

            if (db == null || ids == null || ids.Length == 0) { error = "没有选中对象"; return false; }

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    // ---- 1) 单个块参照 ----
                    if (ids.Length == 1)
                    {
                        BlockReference br = tr.GetObject(ids[0], OpenMode.ForRead) as BlockReference;
                        if (br != null)
                        {
                            BlockTableRecord bdef = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;

                            Scale3d sf = br.ScaleFactors;
                            double sx = Math.Abs(sf.X) > 1e-9 ? Math.Abs(sf.X) : 1.0;
                            double sy = Math.Abs(sf.Y) > 1e-9 ? Math.Abs(sf.Y) : 1.0;

                            f.IsBlock = true;
                            f.BlockName = bdef != null ? bdef.Name : br.Name;
                            f.ScaleX = sx;
                            f.ScaleY = sy;
                            f.ClearInner();

                            // 基准必须和 AddFrame 插入时用的完全一致 —— 都用 BlockOuterRef。
                            // 以前这里用 br.GeometricExtents（块里所有东西的并集），插入时却按
                            // 「最大的闭合矩形」对齐，两个基准差一个边框厚度，插出来就是歪的。
                            Extents3d refExt;
                            if (bdef != null && BlockOuterRef(tr, bdef, out refExt))
                            {
                                Point3d org = Point3d.Origin;
                                try { org = bdef.Origin; } catch { }

                                f.Width = (refExt.MaxPoint.X - refExt.MinPoint.X) * sx;
                                f.Height = (refExt.MaxPoint.Y - refExt.MinPoint.Y) * sy;

                                outerMin = new Point3d(
                                    br.Position.X + (refExt.MinPoint.X - org.X) * sx,
                                    br.Position.Y + (refExt.MinPoint.Y - org.Y) * sy,
                                    0);
                            }
                            else
                            {
                                // 块里没有闭合矩形，退回所有实体的并集
                                Extents3d be;
                                try { be = br.GeometricExtents; }
                                catch { error = "这个块没有有效范围"; return false; }

                                f.Width = be.MaxPoint.X - be.MinPoint.X;
                                f.Height = be.MaxPoint.Y - be.MinPoint.Y;
                                outerMin = be.MinPoint;
                            }

                            tr.Commit();
                            return true;
                        }
                    }

                    // ---- 2) 散线：自动组块 ----
                    Extents3d ext;
                    if (!TryGetExtents(tr, ids, out ext)) { error = "选中的对象没有有效范围"; return false; }

                    double w = ext.MaxPoint.X - ext.MinPoint.X;
                    double h = ext.MaxPoint.Y - ext.MinPoint.Y;
                    if (w <= 0 || h <= 0) { error = "选中的对象范围为空"; return false; }

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);

                    string baseName = "VPFRAME";
                    string name = baseName;
                    for (int i = 2; bt.Has(name) && i < 100000; i++) name = baseName + i;

                    var newDef = new BlockTableRecord();
                    newDef.Name = name;
                    bt.Add(newDef);
                    tr.AddNewlyCreatedDBObject(newDef, true);

                    // 必须深拷贝，不能把还挂在模型空间的实体直接 AppendEntity 到块定义：
                    // 那样实体会同时挂在两个所有者下面，数据库当场就坏了，
                    // 表现就是随后点取角点时 AutoCAD 卡住不动。
                    var src = new ObjectIdCollection(ids);
                    var map = new IdMapping();
                    db.DeepCloneObjects(src, newDef.ObjectId, map, false);

                    // 块内图形平移到原点 —— 这样块基点就是外框左下角，插入时不用再算偏移
                    var shift = Matrix3d.Displacement(new Vector3d(-ext.MinPoint.X, -ext.MinPoint.Y, 0));

                    foreach (ObjectId cid in newDef)
                    {
                        try
                        {
                            Entity e = tr.GetObject(cid, OpenMode.ForWrite) as Entity;
                            if (e != null) e.TransformBy(shift);
                        }
                        catch { }
                    }

                    // 在原位置插回一个块参照，用户看到的图形位置不变
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    var br2 = new BlockReference(ext.MinPoint, newDef.ObjectId);
                    space.AppendEntity(br2);
                    tr.AddNewlyCreatedDBObject(br2, true);

                    // 原来的散线删掉 —— 图形由块参照顶替，画面上看不出变化
                    foreach (ObjectId id in ids)
                    {
                        if (id.IsNull || id.IsErased) continue;

                        try
                        {
                            Entity old = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                            if (old != null && !old.IsErased) old.Erase();
                        }
                        catch { }
                    }

                    f.IsBlock = true;
                    f.BlockName = name;
                    f.ScaleX = 1.0;
                    f.ScaleY = 1.0;
                    f.ClearInner();

                    // 和上面一样：尺寸和基准都取 BlockOuterRef，保证和 AddFrame 插入时对齐。
                    // 块内图形已经平移到原点，所以定义空间里的 refExt 直接加上 ext.MinPoint 就是世界坐标。
                    Extents3d newRef;
                    if (BlockOuterRef(tr, newDef, out newRef))
                    {
                        f.Width = newRef.MaxPoint.X - newRef.MinPoint.X;
                        f.Height = newRef.MaxPoint.Y - newRef.MinPoint.Y;
                        outerMin = new Point3d(
                            ext.MinPoint.X + newRef.MinPoint.X,
                            ext.MinPoint.Y + newRef.MinPoint.Y,
                            0);
                    }
                    else
                    {
                        f.Width = w;
                        f.Height = h;
                        outerMin = ext.MinPoint;
                    }

                    tr.Commit();
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                error = "组块失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从别的图纸把一个块定义导进当前图纸。
        /// 用途：图框块只存在于「拾取图框」那张图纸里，换一张图纸生成时块表里没有它，
        /// 只能退回画个空矩形 —— 用户看到的就是「图框没出现」。
        /// 用 ReadDwgFile 只读打开源图纸，不会动源文件。
        /// </summary>
        public static bool ImportBlock(Database db, string blockName, string sourceDwg, out string error)
        {
            error = null;

            if (db == null || string.IsNullOrEmpty(blockName) || string.IsNullOrEmpty(sourceDwg))
            {
                error = "参数为空";
                return false;
            }

            if (!File.Exists(sourceDwg))
            {
                error = "源图纸不在了：" + sourceDwg;
                return false;
            }

            try
            {
                using (Database src = new Database(false, true))
                {
                    src.ReadDwgFile(sourceDwg, FileOpenMode.OpenForReadAndAllShare, true, "");

                    BlockTable sbt = (BlockTable)src.BlockTableId.GetObject(OpenMode.ForRead);
                    if (!sbt.Has(blockName))
                    {
                        error = "源图纸里也没有块「" + blockName + "」";
                        return false;
                    }

                    db.Insert(blockName, src, false);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ 图框库

        /// <summary>
        /// 插件自己的图框库：一个 dwg，里面放着所有拾取过的图框块定义。
        ///
        /// 为什么需要它：图框模板原来只记「块名 + 源图纸路径」，
        /// 换图纸生成时得从源图纸把块导回来。源图纸没保存过、被移走、改名，
        /// 块就导不进来，只能降级画个矩形 —— 用户看到的就是「白线，不是图框」。
        /// 有了这个库，图框真正跟着插件走，跟任何一张图纸都没关系。
        /// </summary>
        public static string FrameLibPath
        {
            get { return Path.Combine(VpStore.Folder, "FrameLib.dwg"); }
        }

        /// <summary>当前图纸的块表里有没有这个块。</summary>
        public static bool HasBlock(Database db, string blockName)
        {
            if (db == null || string.IsNullOrEmpty(blockName)) return false;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    bool has = bt.Has(blockName);
                    tr.Commit();
                    return has;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 把一个块定义存进图框库。库文件不存在就新建；同名的覆盖。
        /// 源图纸只在这里被读一次，之后图框就与它无关了。
        /// </summary>
        public static bool SaveBlockToLib(Database srcDb, string blockName, out string error)
        {
            error = null;

            if (srcDb == null || string.IsNullOrEmpty(blockName))
            {
                error = "参数为空";
                return false;
            }

            Database lib = null;

            try
            {
                ObjectId defId;

                using (Transaction tr = srcDb.TransactionManager.StartTransaction())
                {
                    BlockTable sbt = (BlockTable)tr.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    if (!sbt.Has(blockName))
                    {
                        error = "源图纸里没有块「" + blockName + "」";
                        return false;
                    }
                    defId = sbt[blockName];
                    tr.Commit();
                }

                string path = FrameLibPath;

                if (File.Exists(path))
                {
                    lib = new Database(false, true);
                    lib.ReadDwgFile(path, FileOpenMode.OpenForReadAndWriteNoShare, true, "");
                }
                else
                {
                    lib = new Database(true, true);
                }

                var ids = new ObjectIdCollection();
                ids.Add(defId);

                var map = new IdMapping();

                // Replace：同名块直接覆盖，这样重新拾取同一个图框不会越攒越多
                srcDb.WblockCloneObjects(ids, lib.BlockTableId, map,
                                         DuplicateRecordCloning.Replace, false);

                lib.SaveAs(path, DwgVersion.Current);
                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (lib != null)
                {
                    try { lib.Dispose(); } catch { }
                }
            }
        }

        /// <summary>图框库里有没有这个块。用来判断要不要补写，避免每次生成都写一遍库文件。</summary>
        public static bool LibHasBlock(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return false;

            try
            {
                string path = FrameLibPath;
                if (!File.Exists(path)) return false;

                using (Database lib = new Database(false, true))
                {
                    lib.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    BlockTable bt = (BlockTable)lib.BlockTableId.GetObject(OpenMode.ForRead);
                    return bt.Has(blockName);
                }
            }
            catch { return false; }
        }

        /// <summary>从图框库把一个块定义导进当前图纸。</summary>
        public static bool ImportBlockFromLib(Database db, string blockName, out string error)
        {
            error = null;

            if (db == null || string.IsNullOrEmpty(blockName))
            {
                error = "参数为空";
                return false;
            }

            string path = FrameLibPath;
            if (!File.Exists(path))
            {
                error = "图框库还没建（" + path + "）";
                return false;
            }

            try
            {
                using (Database lib = new Database(false, true))
                {
                    lib.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");

                    BlockTable lbt = (BlockTable)lib.BlockTableId.GetObject(OpenMode.ForRead);
                    if (!lbt.Has(blockName))
                    {
                        error = "图框库里没有块「" + blockName + "」";
                        return false;
                    }

                    db.Insert(blockName, lib, false);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 把整张 dwg 的模型空间内容做成一个块，插进当前图纸。
        /// 图框库目录里那种「一整个图框文件」的图框走这条路 ——
        /// 它的图框可能是散线 + 图签，没有一个现成的块定义可用。
        /// </summary>
        public static bool InsertWholeFile(Database db, string blockName, string sourceDwg, out string error)
        {
            error = null;

            if (db == null || string.IsNullOrEmpty(blockName) || string.IsNullOrEmpty(sourceDwg))
            {
                error = "参数为空";
                return false;
            }

            if (!File.Exists(sourceDwg))
            {
                error = "文件不存在（" + sourceDwg + "）";
                return false;
            }

            try
            {
                using (Database src = new Database(false, true))
                {
                    src.ReadDwgFile(sourceDwg, FileOpenMode.OpenForReadAndAllShare, true, "");

                    // Insert 把源库模型空间里的实体整体做成名为 blockName 的块定义，
                    // 图形坐标保持不变（块基点是 WCS 原点）—— 插入时由 AddFrame 按
                    // BlockOuterRef 补偿偏移，和普通图框块走同一套逻辑。
                    db.Insert(blockName, src, false);
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static bool TryGetExtents(Transaction tr, ObjectId[] ids, out Extents3d ext)
        {
            ext = new Extents3d();
            bool any = false;

            foreach (ObjectId id in ids)
            {
                if (id.IsNull || id.IsErased || !id.IsValid) continue;

                try
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) continue;

                    Extents3d e2 = e.GeometricExtents;
                    if (!any) { ext = e2; any = true; }
                    else ext.AddExtents(e2);
                }
                catch { }
            }

            return any;
        }

        // ------------------------------------------------------------------ 块内外框基准

        /// <summary>
        /// 块定义里的「外框参考范围」= 块内所有非属性实体的并集。
        ///
        /// 这个基准必须和「拾取图框」时用的完全一致，否则插入的图框会整体偏移。
        /// 拾取时用的是块参照的 GeometricExtents（也就是这个并集），所以这里也取并集。
        ///
        /// 曾经这里取「面积最大的闭合多段线」，结果图框外框如果是 4 条独立直线、
        /// 内框才是闭合多段线，基准就落到内框上，插入后整体偏一个边框厚度 —— 看着就是歪的。
        /// </summary>
        public static bool BlockOuterRef(Transaction tr, BlockTableRecord bdef, out Extents3d ext)
        {
            ext = new Extents3d();
            if (tr == null || bdef == null) return false;

            bool any = false;

            foreach (ObjectId id in bdef)
            {
                if (id.IsErased || id.IsNull) continue;

                try
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null || e is AttributeDefinition) continue;

                    Extents3d e2 = e.GeometricExtents;
                    if (!any) { ext = e2; any = true; }
                    else ext.AddExtents(e2);
                }
                catch { }
            }

            return any;
        }

        /// <summary>视口属于哪个布局（用于提示与排障）。</summary>
        public static string LayoutNameOf(Database db, ObjectId vpId)
        {
            try
            {
                // 一个事务读完 owner 和布局字典。原来是两段嵌套事务（读 owner 一段、
                // 查布局一段），多出来的那次提交纯属白做。
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBObject obj = tr.GetObject(vpId, OpenMode.ForRead);
                    ObjectId owner = obj == null ? ObjectId.Null : obj.OwnerId;
                    if (owner.IsNull) { tr.Commit(); return ""; }

                    string name = LayoutNameIn(tr, db, owner);
                    tr.Commit();
                    return name;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 「布局块表记录 Id → 布局名」全表。一次事务读全，供面板一次列出多个标签时用 ——
        /// 逐个调 <see cref="LayoutNameOf"/> 是每个标签一次事务。
        /// </summary>
        public static Dictionary<ObjectId, string> LayoutNamesByBlock(Database db)
        {
            var map = new Dictionary<ObjectId, string>();
            if (db == null) return map;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict)
                    {
                        try
                        {
                            Layout lay = tr.GetObject(e.Value, OpenMode.ForRead) as Layout;
                            if (lay == null) continue;
                            ObjectId btr = lay.BlockTableRecordId;
                            if (!btr.IsNull && !map.ContainsKey(btr)) map[btr] = lay.LayoutName;
                        }
                        catch { }
                    }
                    tr.Commit();
                }
            }
            catch { }

            return map;
        }

        /// <summary>事务内版本：owner 块表记录对应的布局名，找不到返回空串。</summary>
        static string LayoutNameIn(Transaction tr, Database db, ObjectId owner)
        {
            try
            {
                DBDictionary layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in layoutDict)
                {
                    Layout lay = tr.GetObject(e.Value, OpenMode.ForRead) as Layout;
                    if (lay != null && lay.BlockTableRecordId == owner) return lay.LayoutName;
                }
            }
            catch { }
            return "";
        }
    }
}
