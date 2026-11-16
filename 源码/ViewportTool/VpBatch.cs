using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace VpLayer
{
    /// <summary>
    /// 批量生成。两种排布方式：
    ///
    ///   · 单张（默认）：一个状态 = 一个布局 = 一个图框 = 一个视口，
    ///     布局名直接用状态名。适合系统图一图一框的出图习惯。
    ///
    ///   · 一排：一个布局里横排 N 个图框，每个图框一个视口、套一个状态。
    ///     图框之间按 **外框尺寸 + 50** 的步距分开（50 是净间距，图纸单位），
    ///     避免两个图框贴在一起看着像一张。
    ///
    /// 两种模式共同点：
    ///   · 视口铺满图框内框（绘图区），不铺外框 —— 否则会压到图签栏上
    ///   · 布局自带的主视口必须关掉，否则它铺满整张纸显示模型内容，把图框全盖住
    /// </summary>
    public static class VpBatch
    {
        public sealed class Result
        {
            public bool Ok;
            public string Error;
            public int Layouts;
            public int Viewports;
            public int FramesBlock;
            public int FramesRect;
            public int MissingLayerTotal;
            /// <summary>成功打上归层配置标记的视口数。</summary>
            public int Tagged;
            public double Scale;
            public readonly List<string> LayoutNames = new List<string>();
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>出图比例的标准分母档位。自动比例会落到这些档上。</summary>
        static readonly double[] StdDenoms =
        {
            1, 2, 5, 10, 20, 25, 30, 40, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000
        };

        /// <summary>选择集 → 包围盒。空选择/全是无效对象返回 false。</summary>
        public static bool TryGetExtents(Database db, ObjectId[] ids, out Extents3d ext, out string error)
        {
            ext = new Extents3d();
            error = null;
            if (db == null || ids == null || ids.Length == 0) { error = "没有选中任何对象"; return false; }

            bool any = false;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        if (id.IsNull || id.IsErased || !id.IsValid) continue;

                        try
                        {
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            Extents3d e = ent.GeometricExtents;
                            if (!any) { ext = e; any = true; }
                            else ext.AddExtents(e);
                        }
                        catch
                        {
                            // 个别图元没有有效范围（空块、退化对象），跳过即可，不拖垮整批
                        }
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (!any) { error = "选中的对象没有可用范围"; return false; }
            return true;
        }

        /// <summary>
        /// 自动比例：内容正好放进内框的最大标准比例。
        /// 留 2% 余量，避免图元贴着视口边框被切掉。
        /// </summary>
        public static double AutoScale(VpConfig cfg, double mw, double mh)
        {
            if (cfg == null || cfg.Frame == null || !cfg.Frame.Valid || mw <= 0 || mh <= 0) return 0.02;

            double availW = cfg.Frame.DrawW * 0.98;
            double availH = cfg.Frame.DrawH * 0.98;

            double auto = Math.Min(availW / mw, availH / mh);
            if (auto <= 0) return 0.02;

            // 分母往上取标准档：比例只能更小，不能更大，否则内容放不下
            double denom = 1.0 / auto;
            for (int i = 0; i < StdDenoms.Length; i++)
                if (StdDenoms[i] >= denom - 1e-9) return 1.0 / StdDenoms[i];

            return 1.0 / StdDenoms[StdDenoms.Length - 1];
        }

        /// <summary>批量生成。scale &lt;= 0 表示按内框自动算。</summary>
        public static Result Generate(Document doc, VpConfig cfg, Extents3d modelExt, double scale)
        {
            var res = new Result();
            if (doc == null) { res.Error = "没有活动图纸"; return res; }
            if (cfg == null || cfg.States == null || cfg.States.Count == 0)
            {
                res.Error = "还没有保存任何视口状态（先用「刷取状态」记一个）";
                return res;
            }
            if (cfg.Frame == null || !cfg.Frame.Valid)
            {
                res.Error = "还没有图框（先点「拾取图框」）";
                return res;
            }

            double mw = modelExt.MaxPoint.X - modelExt.MinPoint.X;
            double mh = modelExt.MaxPoint.Y - modelExt.MinPoint.Y;
            if (mw <= 0 || mh <= 0) { res.Error = "框选范围为空"; return res; }

            if (scale <= 0) scale = AutoScale(cfg, mw, mh);
            if (scale <= 0) { res.Error = "比例无效"; return res; }
            res.Scale = scale;

            double cx = (modelExt.MinPoint.X + modelExt.MaxPoint.X) / 2.0;
            double cy = (modelExt.MinPoint.Y + modelExt.MaxPoint.Y) / 2.0;

            // 图框块只在「拾取图框」那张图纸里 —— 换图纸生成时先导回来，
            // 否则 AddFrame 只能画个空矩形，看起来就是「图框没出现」。
            EnsureFrameBlock(doc, cfg.Frame, res);

            // 内容超出内框时提示：视口会被裁切
            double contentW = mw * scale;
            double contentH = mh * scale;
            if (contentW > cfg.Frame.DrawW + 0.01 || contentH > cfg.Frame.DrawH + 0.01)
            {
                res.Notes.Add("内容 " + Round(contentW) + "×" + Round(contentH)
                            + " 超出内框 " + Round(cfg.Frame.DrawW) + "×" + Round(cfg.Frame.DrawH)
                            + "，视口会裁切（比例 1:" + Round(1.0 / scale) + "）");
            }

            return cfg.LayoutMode == VpConfig.ModeRow
                ? GenerateRow(doc, cfg, scale, cx, cy, res)
                : GeneratePerSheet(doc, cfg, scale, cx, cy, res);
        }

        /// <summary>
        /// 生成前确认图框块在当前图纸里存在。
        ///
        /// 三级兜底（顺序有讲究）：
        ///   1. 插件自己的图框库 FrameLib.dwg —— 与任何图纸无关，最可靠；
        ///   2. 拾取时那张源图纸 —— 老数据（库还没建起来时）靠它；
        ///   3. 都没有才画矩形，并且明确告诉用户为什么、怎么补。
        /// 只靠第 2 级就是「白线无图框」的来源：源图纸没存过或挪走了，块就导不进来。
        /// </summary>
        static void EnsureFrameBlock(Document doc, FrameTemplate f, Result res)
        {
            if (f == null || !f.IsBlock || string.IsNullOrEmpty(f.BlockName)) return;

            Database db = doc.Database;

            if (VpService.HasBlock(db, f.BlockName))
            {
                // 块已经在这张图纸里了。顺手补进库 —— 老数据（拾取时还没有图框库这个功能）
                // 在图框所在的那张图纸里生成一次，就自动补齐了，不用重新拾取。
                // 整图图框（WholeFile）不进 FrameLib.dwg，它的来源永远是那个文件本身。
                if (!f.WholeFile && !VpService.LibHasBlock(f.BlockName))
                {
                    string ignore;
                    VpService.SaveBlockToLib(db, f.BlockName, out ignore);
                }
                return;
            }

            using (DocumentLock lk = doc.LockDocument())
            {
                string err;

                // 1) 图框库目录里那个 dwg —— 现在的主路径
                if (!string.IsNullOrEmpty(f.LibFile) && System.IO.File.Exists(f.LibFile))
                {
                    bool ok = f.WholeFile
                        ? VpService.InsertWholeFile(db, f.BlockName, f.LibFile, out err)
                        : VpService.ImportBlock(db, f.BlockName, f.LibFile, out err);

                    if (ok) return;
                }
                else
                {
                    err = "没记录图框库文件";
                }

                string dirErr = err;

                // 2) 插件自带的 FrameLib.dwg（手动拾取的图框存在这里）
                if (VpService.ImportBlockFromLib(db, f.BlockName, out err)) return;
                string libErr = err;

                // 3) 拾取时那张源图纸
                string srcErr = "没记录源图纸";

                if (!string.IsNullOrEmpty(f.SourceDwg))
                {
                    if (VpService.ImportBlock(db, f.BlockName, f.SourceDwg, out err)) return;
                    srcErr = err;
                }

                res.Notes.Add("图框块「" + f.BlockName + "」导不进来（图框库文件：" + dirErr
                            + "；插件图框库：" + libErr + "；源图纸：" + srcErr + "）"
                            + "\n  这次只能用矩形代替。把图框 dwg 放回图框库目录再「重新扫描」一次，"
                            + "以后换图纸也能用");
            }
        }

        // ------------------------------------------------------------------ 单张模式

        static Result GeneratePerSheet(Document doc, VpConfig cfg, double scale,
                                       double cx, double cy, Result res)
        {
            Database db = doc.Database;

            using (DocumentLock lk = doc.LockDocument())
            {
                for (int i = 0; i < cfg.States.Count; i++)
                {
                    VpState st = cfg.States[i];
                    if (st == null) continue;

                    string name;
                    ObjectId layoutId;

                    // 1) 新建布局。CreateLayout 必须在事务外调用。
                    try
                    {
                        name = UniqueLayoutName(db, string.IsNullOrWhiteSpace(st.Name) ? "系统图" : st.Name);
                        layoutId = LayoutManager.Current.CreateLayout(name);
                        if (layoutId.IsNull)
                        {
                            res.Notes.Add("「" + st.Name + "」新建布局失败，已跳过");
                            continue;
                        }

                        // 切到该布局：VPLAYER 冻结只对活动布局里的视口生效
                        LayoutManager.Current.CurrentLayout = name;
                    }
                    catch (System.Exception ex)
                    {
                        res.Notes.Add("「" + st.Name + "」新建布局失败：" + ex.Message);
                        continue;
                    }

                    res.Layouts++;
                    res.LayoutNames.Add(name);

                    try
                    {
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            Layout lay = tr.GetObject(layoutId, OpenMode.ForWrite) as Layout;
                            if (lay == null) { res.Notes.Add("「" + st.Name + "」布局打不开"); continue; }

                            // 2) 图纸幅面 = 图框尺寸
                            double paperW = cfg.Frame.Width;
                            double paperH = cfg.Frame.Height;
                            ApplyPaper(lay, ref paperW, ref paperH, res);

                            BlockTableRecord btr = tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite) as BlockTableRecord;
                            if (btr == null) { res.Notes.Add("「" + st.Name + "」布局空间打不开"); continue; }

                            CloseMainViewport(tr, btr);

                            // 3) 图框放在图纸原点
                            AddFrame(tr, btr, db, cfg.Frame, Point3d.Origin, res);

                            // 4) 视口铺满内框
                            Viewport vp;
                            try
                            {
                                vp = CreateViewport(tr, btr, cfg.Frame, 0, 0, cx, cy, scale);
                                res.Viewports++;
                            }
                            catch (System.Exception ex)
                            {
                                res.Notes.Add("「" + st.Name + "」视口创建失败：" + ex.Message);
                                continue;
                            }

                            // 5) 套状态（外层事务里做，不能另开事务）
                            ApplyState(tr, db, vp, st, res);

                            tr.Commit();
                        }
                    }
                    catch (System.Exception ex)
                    {
                        res.Notes.Add("「" + st.Name + "」生成失败：" + ex.Message);
                    }
                }
            }

            res.Ok = res.Layouts > 0;
            if (!res.Ok && string.IsNullOrEmpty(res.Error)) res.Error = "一张图纸都没生成成功";
            return res;
        }

        // ------------------------------------------------------------------ 一排模式

        static Result GenerateRow(Document doc, VpConfig cfg, double scale,
                                  double cx, double cy, Result res)
        {
            Database db = doc.Database;

            double fw = cfg.Frame.Width;
            double fh = cfg.Frame.Height;
            double gap = VpConfig.RowGap;                       // 图框之间的净间距

            int n = cfg.States.Count;
            double totalW = n * fw + (n - 1) * gap;

            const double Margin = 10.0;
            double needW = totalW + Margin * 2;
            double needH = fh + Margin * 2;

            using (DocumentLock lk = doc.LockDocument())
            {
                string name;
                ObjectId layoutId;

                try
                {
                    name = UniqueLayoutName(db, "系统图");
                    layoutId = LayoutManager.Current.CreateLayout(name);
                    if (layoutId.IsNull) { res.Error = "新建布局失败"; return res; }

                    LayoutManager.Current.CurrentLayout = name;
                }
                catch (System.Exception ex)
                {
                    res.Error = "新建布局失败：" + ex.Message;
                    return res;
                }

                res.Layouts = 1;
                res.LayoutNames.Add(name);

                try
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        Layout lay = tr.GetObject(layoutId, OpenMode.ForWrite) as Layout;
                        if (lay == null) { res.Error = "布局打不开"; return res; }

                        double paperW = needW;
                        double paperH = needH;
                        ApplyPaper(lay, ref paperW, ref paperH, res);

                        if (paperW + 0.5 < needW)
                        {
                            res.Notes.Add("一排要 " + Round(needW) + " 宽，最大幅面只有 " + Round(paperW)
                                        + "，后面的图框会超出纸边界（减少状态数量或换竖排图框）");
                        }

                        BlockTableRecord btr = tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite) as BlockTableRecord;
                        if (btr == null) { res.Error = "布局空间打不开"; return res; }

                        CloseMainViewport(tr, btr);

                        // 整排在纸内居中；幅面不够时贴着左边起排，至少能看到
                        double startX = paperW > totalW + 1 ? (paperW - totalW) / 2.0 : Margin;
                        double baseY = paperH > fh + 1 ? (paperH - fh) / 2.0 : Margin;

                        for (int i = 0; i < n; i++)
                        {
                            VpState st = cfg.States[i];
                            if (st == null) continue;

                            double fx = startX + i * (fw + gap);

                            AddFrame(tr, btr, db, cfg.Frame, new Point3d(fx, baseY, 0), res);

                            Viewport vp;
                            try
                            {
                                vp = CreateViewport(tr, btr, cfg.Frame, fx, baseY, cx, cy, scale);
                                res.Viewports++;
                            }
                            catch (System.Exception ex)
                            {
                                res.Notes.Add("第 " + (i + 1) + " 个视口创建失败：" + ex.Message);
                                continue;
                            }

                            ApplyState(tr, db, vp, st, res);
                        }

                        tr.Commit();
                    }
                }
                catch (System.Exception ex)
                {
                    res.Error = ex.Message;
                    return res;
                }
            }

            res.Ok = res.Viewports > 0;
            if (!res.Ok && string.IsNullOrEmpty(res.Error)) res.Error = "一个视口都没生成成功";
            return res;
        }

        // ------------------------------------------------------------------ 公用

        /// <summary>视口铺满图框内框，中心对准模型范围中心。</summary>
        static Viewport CreateViewport(Transaction tr, BlockTableRecord btr, FrameTemplate f,
                                       double frameX, double frameY, double cx, double cy, double scale)
        {
            var vp = new Viewport();
            btr.AppendEntity(vp);
            tr.AddNewlyCreatedDBObject(vp, true);

            double ix = frameX + f.DrawX;
            double iy = frameY + f.DrawY;
            double iw = f.DrawW;
            double ih = f.DrawH;

            vp.CenterPoint = new Point3d(ix + iw / 2.0, iy + ih / 2.0, 0);
            vp.Width = iw;
            vp.Height = ih;
            vp.ViewCenter = new Point2d(cx, cy);
            vp.CustomScale = scale;
            vp.On = true;

            return vp;
        }

        /// <summary>
        /// 关掉布局自带的主视口（Number=1）。
        /// 它删不掉，且铺满整张纸显示模型内容，会把图框全盖住 ——
        /// 这是之前「图框生成了却看不见」的真正原因。
        /// </summary>
        static void CloseMainViewport(Transaction tr, BlockTableRecord btr)
        {
            var existing = new List<ObjectId>();
            foreach (ObjectId id in btr)
                if (VpService.IsViewport(id)) existing.Add(id);

            foreach (ObjectId id in existing)
            {
                try
                {
                    Viewport v = tr.GetObject(id, OpenMode.ForWrite) as Viewport;
                    if (v == null) continue;

                    if (v.Number == 1) { v.On = false; continue; }

                    v.Erase();
                }
                catch { }
            }
        }

        static void ApplyState(Transaction tr, Database db, Viewport vp, VpState st, Result res)
        {
            VpService.ApplyResult ar = VpService.ApplyIn(tr, db, vp, st);

            if (!ar.Ok)
            {
                res.Notes.Add("「" + st.Name + "」应用失败：" + ar.Error);
                return;
            }

            res.MissingLayerTotal += ar.Missing;
            if (ar.Missing > 0)
            {
                int show = Math.Min(ar.MissingNames.Count, 5);
                res.Notes.Add("「" + st.Name + "」有 " + ar.Missing
                    + " 个图层本图纸没有：" + string.Join("、", ar.MissingNames.GetRange(0, show).ToArray())
                    + (ar.Missing > show ? " 等" : ""));
            }

            // 生成出来的视口同样打上标记 —— 进这些视口就自动切到对应的归层配置。
            // 配置名走 ProfileMatch.Resolve：状态上没显式绑，但名字和配置同名，也自动绑上。
            // 复用外层事务，不能另起一个（AutoCAD 不支持嵌套事务）。
            string tagProfile = ProfileMatch.Resolve(st.Name, st.Profile).Profile;
            if (!string.IsNullOrEmpty(tagProfile))
            {
                if (VpTagStore.SetIn(tr, db, vp.ObjectId, tagProfile)) res.Tagged++;
                else res.Notes.Add("「" + st.Name + "」的归层配置没写进视口标记");
            }
        }

        /// <summary>
        /// 把图纸幅面设成需要的尺寸，并回读 CAD 实际给出的幅面。
        /// PlotSettings 上的幅面属性都是只读的，2016 只能通过 PlotSettingsValidator 改；
        /// 其中没有「直接设尺寸」的接口，是按尺寸找最接近的标准图幅（SetClosestMediaName）。
        /// </summary>
        static void ApplyPaper(Layout lay, ref double w, ref double h, Result res)
        {
            try
            {
                PlotSettings ps = lay;
                PlotSettingsValidator psv = PlotSettingsValidator.Current;

                psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
                psv.SetPlotRotation(ps, PlotRotation.Degrees000);
                psv.SetClosestMediaName(ps, w, h, PlotPaperUnit.Millimeters, false);
                psv.SetPlotOrigin(ps, new Point2d(0, 0));
                psv.SetPlotCentered(ps, false);

                Point2d actual = ps.PlotPaperSize;
                double ax = actual.X, ay = actual.Y;

                // 单位没切成毫米时，回读的是英寸
                if (ps.PlotPaperUnits != PlotPaperUnit.Millimeters) { ax *= 25.4; ay *= 25.4; }

                if (ax > 0 && ay > 0)
                {
                    w = ax;
                    h = ay;
                }
            }
            catch (System.Exception ex)
            {
                res.Notes.Add("图纸幅面设置失败（" + ex.Message + "），图框仍在图纸原点，需手动调图幅");
            }
        }

        /// <summary>插入图框：能插块就插块（属性块连属性一起带过去），插不了就按尺寸画矩形。</summary>
        static void AddFrame(Transaction tr, BlockTableRecord btr, Database db,
                             FrameTemplate f, Point3d lowerLeft, Result res)
        {
            if (f.IsBlock && !string.IsNullOrEmpty(f.BlockName))
            {
                try
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (bt.Has(f.BlockName))
                    {
                        BlockTableRecord bdef = tr.GetObject(bt[f.BlockName], OpenMode.ForRead) as BlockTableRecord;

                        Point3d pos = lowerLeft;

                        // 块基点多半不在外框左下角，必须补偿，否则图框整体偏移。
                        // 基准用 VpService.BlockOuterRef，和识别内框时是同一套算法。
                        Extents3d defExt;
                        if (bdef != null && VpService.BlockOuterRef(tr, bdef, out defExt))
                        {
                            double dx = 0, dy = 0;
                            try
                            {
                                dx = (defExt.MinPoint.X - bdef.Origin.X) * f.ScaleX;
                                dy = (defExt.MinPoint.Y - bdef.Origin.Y) * f.ScaleY;
                            }
                            catch { }

                            pos = new Point3d(lowerLeft.X - dx, lowerLeft.Y - dy, 0);
                        }

                        var br = new BlockReference(pos, bt[f.BlockName]);
                        br.ScaleFactors = new Scale3d(f.ScaleX, f.ScaleY, 1.0);

                        btr.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);

                        // 属性块：把属性定义实例化，否则插进去标题栏是空的
                        if (bdef != null) AddAttributes(tr, br, bdef);

                        res.FramesBlock++;
                        return;
                    }
                    res.Notes.Add("块「" + f.BlockName + "」在本图纸里不存在，已用矩形代替图框");
                }
                catch (System.Exception ex)
                {
                    res.Notes.Add("插入图框块失败：" + ex.Message + "，已用矩形代替");
                }
            }

            try
            {
                var pl = new Polyline();
                pl.AddVertexAt(0, new Point2d(lowerLeft.X, lowerLeft.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(lowerLeft.X + f.Width, lowerLeft.Y), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(lowerLeft.X + f.Width, lowerLeft.Y + f.Height), 0, 0, 0);
                pl.AddVertexAt(3, new Point2d(lowerLeft.X, lowerLeft.Y + f.Height), 0, 0, 0);
                pl.Closed = true;

                btr.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                res.FramesRect++;
            }
            catch (System.Exception ex)
            {
                res.Notes.Add("图框矩形绘制失败：" + ex.Message);
            }
        }

        /// <summary>把块定义里的属性定义实例化到新插入的块参照上。</summary>
        static void AddAttributes(Transaction tr, BlockReference br, BlockTableRecord bdef)
        {
            foreach (ObjectId id in bdef)
            {
                try
                {
                    AttributeDefinition ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                    if (ad == null || ad.Constant) continue;

                    var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                    ar.TextString = ad.TextString;

                    br.AttributeCollection.AppendAttribute(ar);
                    tr.AddNewlyCreatedDBObject(ar, true);
                }
                catch { }
            }
        }

        /// <summary>布局名不能重复，也不能含 CAD 不允许的字符。</summary>
        public static string UniqueLayoutName(Database db, string baseName)
        {
            string b = SanitizeLayoutName(baseName);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict) names.Add(e.Key);
                    tr.Commit();
                }
            }
            catch { }

            if (!names.Contains(b)) return b;
            for (int i = 2; i < 1000; i++)
            {
                string n = b + "(" + i + ")";
                if (!names.Contains(n)) return n;
            }
            return b + DateTime.Now.ToString("HHmmss");
        }

        static string SanitizeLayoutName(string name)
        {
            string s = string.IsNullOrWhiteSpace(name) ? "系统图" : name.Trim();

            const string bad = "<>/\\\":;?*|,=`";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
                sb.Append(bad.IndexOf(c) >= 0 ? '-' : c);

            s = sb.ToString().Trim();
            if (s.Length > 200) s = s.Substring(0, 200);
            return s.Length == 0 ? "系统图" : s;
        }

        static string Round(double v) { return Math.Round(v).ToString("0"); }
    }
}
