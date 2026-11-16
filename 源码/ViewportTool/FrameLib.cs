using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace VpLayer
{
    /// <summary>
    /// 图框库目录：用户指定一个文件夹，把图框 dwg 丢进去，扫描后每个 dwg 变成一个图框模板。
    ///
    /// 一个 dwg 算一个图框，识别规则：
    ///   · 模型空间里只有一个块参照 → 用它的块定义（块名、缩放都记下来）
    ///   · 其它情况 → 整张图当一个图框，生成时 db.Insert 把整图做成块插进来
    ///
    /// 内框（视口该铺满的绘图区）用「最大空白矩形」算：
    ///   把图框里所有图形当障碍，求外框内部面积最大的、不被任何图形挡住的轴对齐矩形。
    ///   闭合矩形只算四条边、不算实心 —— 否则图框线自己就把内部封死了。
    ///   这样图签栏、盖章区、散落的文字都会自然被绕开，剩下的最大空白就是绘图区。
    ///
    /// 识别不准不要紧：面板里可以手动框一次内框，框过之后重新扫描不会覆盖
    /// （FrameTemplate.InnerManual）。
    /// </summary>
    public static class FrameLib
    {
        public const string Pattern = "*.dwg";

        /// <summary>线/边的最小厚度。水平线、垂直线的外框高或宽是 0，不给厚度就等于不存在。</summary>
        const double MinThick = 0.3;

        /// <summary>障碍太多时只保留最大的这么多个，避免算法退化成卡死。</summary>
        const int MaxObstacles = 1200;

        /// <summary>识别出的空白区小于外框这个比例，就当没识别到（图框里塞满了东西）。</summary>
        const double MinInnerRatio = 0.02;

        // ------------------------------------------------------------------ 目录

        /// <summary>生效的图框库目录。配置里没填就用插件目录（老行为）。</summary>
        public static string Dir(VpConfig cfg)
        {
            try
            {
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.FrameLibDir))
                    return cfg.FrameLibDir.Trim();
            }
            catch { }

            return VpStore.Folder;
        }

        public static bool DirExists(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && Directory.Exists(dir); }
            catch { return false; }
        }

        /// <summary>目录下的所有 dwg，按文件名排。只看顶层，不钻子目录（钻下去容易扫到一堆无关文件）。</summary>
        public static List<string> Files(string dir)
        {
            var list = new List<string>();

            try
            {
                if (!DirExists(dir)) return list;

                string[] all = Directory.GetFiles(dir, Pattern);
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                list.AddRange(all);
            }
            catch { }

            return list;
        }

        // ------------------------------------------------------------------ 扫描

        /// <summary>
        /// 扫一遍目录，把结果合并进配置。
        ///
        /// 合并规则（重要，别改坏）：
        ///   · 按 LibFile 认人 —— 同一个文件永远对应同一个模板，重复扫描不会越加越多
        ///   · 用户改过的图框名保留，不用文件名覆盖
        ///   · 用户手动框过的内框保留，不用自动识别的覆盖
        ///   · 文件被移走/删掉的模板不自动删 —— 用户可能只是临时挪开，删了要重新扫回来
        /// </summary>
        public static int Scan(VpConfig cfg, out List<string> added, out List<string> updated,
                               out List<string> errors)
        {
            added = new List<string>();
            updated = new List<string>();
            errors = new List<string>();

            if (cfg == null) return 0;

            List<string> files = Files(Dir(cfg));
            if (files.Count == 0) return 0;

            int ok = 0;

            foreach (string path in files)
            {
                FrameTemplate f;
                string err;

                if (!ReadFrame(path, out f, out err))
                {
                    errors.Add(Path.GetFileName(path) + "：" + err);
                    continue;
                }

                FrameTemplate exist = cfg.FindByLibFile(path);

                if (exist == null)
                {
                    f.Label = UniqueLabel(cfg, f.Label);
                    cfg.Frames.Add(f);
                    added.Add(f.Label);
                }
                else
                {
                    // 名字是用户起的，保留；只有用户没改过（还等于文件名）时才跟着文件走
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(exist.Label)
                        || string.Equals(exist.Label, fileName, StringComparison.OrdinalIgnoreCase))
                        exist.Label = fileName;

                    exist.IsBlock = f.IsBlock;
                    exist.WholeFile = f.WholeFile;
                    exist.BlockName = f.BlockName;
                    exist.Width = f.Width;
                    exist.Height = f.Height;
                    exist.ScaleX = f.ScaleX;
                    exist.ScaleY = f.ScaleY;

                    if (!exist.InnerManual)
                    {
                        exist.InnerX = f.InnerX;
                        exist.InnerY = f.InnerY;
                        exist.InnerWidth = f.InnerWidth;
                        exist.InnerHeight = f.InnerHeight;
                        exist.InnerManual = false;
                    }

                    updated.Add(exist.DisplayName);
                }

                ok++;
            }

            if (cfg.FrameIndex < 0 || cfg.FrameIndex >= cfg.Frames.Count) cfg.FrameIndex = 0;
            return ok;
        }

        static string UniqueLabel(VpConfig cfg, string baseName)
        {
            string b = string.IsNullOrWhiteSpace(baseName) ? "图框" : baseName.Trim();

            Func<string, bool> Taken = delegate(string n)
            {
                for (int i = 0; i < cfg.Frames.Count; i++)
                    if (cfg.Frames[i] != null
                        && string.Equals(cfg.Frames[i].DisplayName, n, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            };

            if (!Taken(b)) return b;

            for (int i = 2; i < 1000; i++)
            {
                string n = b + "(" + i + ")";
                if (!Taken(n)) return n;
            }

            return b + DateTime.Now.Ticks;
        }

        // ------------------------------------------------------------------ 单个 dwg

        /// <summary>读一个 dwg，识别成图框模板。只读打开，不会动原文件。</summary>
        public static bool ReadFrame(string path, out FrameTemplate f, out string error)
        {
            f = new FrameTemplate();
            error = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "文件不存在";
                return false;
            }

            Database db = null;

            try
            {
                db = new Database(false, true);
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");

                f.LibFile = path;
                f.Label = Path.GetFileNameWithoutExtension(path);
                f.SourceDwg = "";

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                    List<ObjectId> ents = SpaceEntities(tr, ms);

                    if (ents.Count == 0)
                    {
                        error = "模型空间是空的";
                        return false;
                    }

                    // ---- 情况 A：整图就一个块参照 → 用它的块定义 ----
                    BlockReference only = ents.Count == 1
                        ? tr.GetObject(ents[0], OpenMode.ForRead) as BlockReference
                        : null;

                    if (only != null)
                    {
                        BlockTableRecord bdef = tr.GetObject(only.BlockTableRecord, OpenMode.ForRead)
                                                 as BlockTableRecord;
                        if (bdef == null)
                        {
                            error = "块定义读不出来";
                            return false;
                        }

                        Scale3d sf = only.ScaleFactors;
                        double sx = Math.Abs(sf.X) > 1e-9 ? Math.Abs(sf.X) : 1.0;
                        double sy = Math.Abs(sf.Y) > 1e-9 ? Math.Abs(sf.Y) : 1.0;

                        Extents3d ext;
                        if (!VpService.BlockOuterRef(tr, bdef, out ext))
                        {
                            error = "块里没有有效图形";
                            return false;
                        }

                        f.IsBlock = true;
                        f.WholeFile = false;
                        f.BlockName = bdef.Name;
                        f.ScaleX = sx;
                        f.ScaleY = sy;
                        f.Width = (ext.MaxPoint.X - ext.MinPoint.X) * sx;
                        f.Height = (ext.MaxPoint.Y - ext.MinPoint.Y) * sy;

                        // 内框在块定义坐标里算，结果是「相对外框左下角」的偏移，与缩放无关，
                        // 但要乘上缩放才是插入后的实际尺寸
                        double ix, iy, iw, ih;
                        if (FindInner(tr, bdef, ext, out ix, out iy, out iw, out ih))
                        {
                            f.InnerX = ix * sx;
                            f.InnerY = iy * sy;
                            f.InnerWidth = iw * sx;
                            f.InnerHeight = ih * sy;
                        }
                        else
                        {
                            f.ClearInner();
                        }
                    }
                    else
                    {
                        // ---- 情况 B：整张图当一个图框 ----
                        Extents3d ext;
                        if (!TryExtents(tr, ents, out ext))
                        {
                            error = "图形没有有效范围";
                            return false;
                        }

                        f.IsBlock = true;
                        f.WholeFile = true;
                        f.BlockName = SafeBlockName(Path.GetFileNameWithoutExtension(path), path);
                        f.ScaleX = 1.0;
                        f.ScaleY = 1.0;
                        f.Width = ext.MaxPoint.X - ext.MinPoint.X;
                        f.Height = ext.MaxPoint.Y - ext.MinPoint.Y;

                        double ix, iy, iw, ih;
                        if (FindInner(tr, ms, ext, out ix, out iy, out iw, out ih))
                        {
                            f.InnerX = ix;
                            f.InnerY = iy;
                            f.InnerWidth = iw;
                            f.InnerHeight = ih;
                        }
                        else
                        {
                            f.ClearInner();
                        }
                    }

                    tr.Commit();
                }

                if (!f.Valid)
                {
                    error = "外框尺寸为 0";
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (db != null) { try { db.Dispose(); } catch { } }
            }
        }

        /// <summary>块表记录里的实体，跳过视口和空的属性定义。</summary>
        static List<ObjectId> SpaceEntities(Transaction tr, BlockTableRecord btr)
        {
            var list = new List<ObjectId>();

            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased) continue;

                try
                {
                    if (id.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Viewport)))) continue;
                }
                catch { }

                list.Add(id);
            }

            return list;
        }

        static bool TryExtents(Transaction tr, List<ObjectId> ids, out Extents3d ext)
        {
            ext = new Extents3d();
            bool any = false;

            foreach (ObjectId id in ids)
            {
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

        // ------------------------------------------------------------------ 内框：最大空白矩形

        struct Rect
        {
            public double X0, Y0, X1, Y1;

            public Rect(double x0, double y0, double x1, double y1)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            }

            public double W { get { return X1 - X0; } }
            public double H { get { return Y1 - Y0; } }
            public double Area { get { return W * H; } }
        }

        /// <summary>
        /// 算一个块（或模型空间）在 outer 内部的最大空白矩形，
        /// 返回相对 outer 左下角的偏移与尺寸。
        ///
        /// 公开出来是为了能离线校验：探针和自检直接用这一份，不复制逻辑，
        /// 免得「测的是一套、跑的是另一套」。
        /// </summary>
        public static bool MeasureInner(Transaction tr, BlockTableRecord btr, Extents3d outer,
                                        out double ix, out double iy, out double iw, out double ih)
        {
            return FindInner(tr, btr, outer, out ix, out iy, out iw, out ih);
        }

        /// <summary>
        /// 在 outer 内部找面积最大的、不被任何障碍挡住的矩形。
        /// 结果用「相对 outer 左下角的偏移 + 尺寸」返回。
        /// </summary>
        static bool FindInner(Transaction tr, BlockTableRecord btr, Extents3d outer,
                              out double ix, out double iy, out double iw, out double ih)
        {
            ix = iy = iw = ih = 0;

            double x0 = outer.MinPoint.X, y0 = outer.MinPoint.Y;
            double x1 = outer.MaxPoint.X, y1 = outer.MaxPoint.Y;
            if (x1 - x0 <= 1e-6 || y1 - y0 <= 1e-6) return false;

            var obs = new List<Rect>();
            double outerArea = (x1 - x0) * (y1 - y0);

            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased) continue;

                try
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null || e is AttributeDefinition || e is Viewport) continue;

                    // 多段线一律按线段拆 —— 图框的外框/内框线经常是 5 个顶点、
                    // 甚至没设闭合标志（首尾点重合但 Closed=false），
                    // 按 extents 当实心算会把它们圈住的整块区域封死，
                    // 最大空白就永远只剩一条缝。拆成线以后内部才是空的。
                    List<Rect> segs;
                    if (TryPolylineEdges(e, out segs))
                    {
                        for (int k = 0; k < segs.Count; k++)
                            AddSolid(obs, segs[k], x0, y0, x1, y1);

                        continue;
                    }

                    // 块参照如果几乎铺满整个外框，它就是图框本身（或图框所在的那个块），
                    // 当障碍会把内部全封死。跳过它，让里面的结构交给外层判断。
                    if (e is BlockReference)
                    {
                        try
                        {
                            Extents3d be = e.GeometricExtents;
                            double ba = (be.MaxPoint.X - be.MinPoint.X) * (be.MaxPoint.Y - be.MinPoint.Y);
                            if (ba >= outerArea * 0.95) continue;
                        }
                        catch { }
                    }

                    Extents3d e2 = e.GeometricExtents;
                    AddSolid(obs, new Rect(e2.MinPoint.X, e2.MinPoint.Y, e2.MaxPoint.X, e2.MaxPoint.Y),
                             x0, y0, x1, y1);
                }
                catch { }
            }

            if (obs.Count > MaxObstacles) obs = KeepBiggest(obs, MaxObstacles);

            double rx, ry, rw, rh;
            if (!LargestEmpty(obs, x0, y0, x1, y1, out rx, out ry, out rw, out rh)) return false;

            // 空白区太小 → 图框里塞满了东西，或者识别偏了。宁可不给，也别给个错的
            if (rw * rh < outerArea * MinInnerRatio) return false;

            ix = rx - x0;
            iy = ry - y0;
            iw = rw;
            ih = rh;
            return true;
        }

        /// <summary>
        /// 多段线 → 一堆细条障碍（每条线段一个）。
        ///
        /// 为什么不管闭合、不管顶点数：
        ///   实际图框的外框经常是「5 个顶点、Closed=false、首尾点重合」的多段线，
        ///   内框也常是多段线。按 extents 当实心算，它们会把圈住的区域整个封死。
        ///   拆成线段之后，线还是挡的，但它围住的内部是空的 —— 这才是我们要的空白区。
        ///
        /// 圆弧段（bulge != 0）用弦 + 拱高的包围盒近似，偏保守（宁可多挡一点）。
        /// </summary>
        static bool TryPolylineEdges(Entity e, out List<Rect> segs)
        {
            segs = null;

            Polyline pl = e as Polyline;
            if (pl == null) return false;

            int n = pl.NumberOfVertices;
            if (n < 2) return false;

            segs = new List<Rect>(n);

            try
            {
                // 没闭合也要把最后一段连回起点：图框外框常见「首尾重合但 Closed=false」，
                // 这种情况下 n 个顶点之间有 n 段，不补就漏掉一条边
                int count = n;

                for (int i = 0; i < count; i++)
                {
                    Point2d a = pl.GetPoint2dAt(i);
                    Point2d b = pl.GetPoint2dAt((i + 1) % n);

                    double bulge = 0;
                    try { bulge = pl.GetBulgeAt(i); } catch { }

                    segs.Add(Math.Abs(bulge) > 1e-9 ? ArcBox(a, b, bulge) : SegBox(a, b));
                }
            }
            catch { return false; }

            return segs.Count > 0;
        }

        static Rect SegBox(Point2d a, Point2d b)
        {
            return new Rect(
                Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        /// <summary>圆弧段的包围盒：弦的包围盒向外扩一个拱高。</summary>
        static Rect ArcBox(Point2d a, Point2d b, double bulge)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            double sag = Math.Abs(bulge) * chord / 2.0;

            return new Rect(
                Math.Min(a.X, b.X) - sag, Math.Min(a.Y, b.Y) - sag,
                Math.Max(a.X, b.X) + sag, Math.Max(a.Y, b.Y) + sag);
        }

        static void AddSolid(List<Rect> obs, Rect r, double x0, double y0, double x1, double y1)
        {
            // 水平线 / 垂直线的外框是退化的（高或宽为 0），不补厚度等于没算
            if (r.W < MinThick)
            {
                double c = (r.X0 + r.X1) / 2;
                r.X0 = c - MinThick / 2;
                r.X1 = c + MinThick / 2;
            }

            if (r.H < MinThick)
            {
                double c = (r.Y0 + r.Y1) / 2;
                r.Y0 = c - MinThick / 2;
                r.Y1 = c + MinThick / 2;
            }

            // 裁到外框里面
            if (r.X0 < x0) r.X0 = x0;
            if (r.Y0 < y0) r.Y0 = y0;
            if (r.X1 > x1) r.X1 = x1;
            if (r.Y1 > y1) r.Y1 = y1;

            if (r.W <= 1e-9 || r.H <= 1e-9) return;

            obs.Add(r);
        }

        static List<Rect> KeepBiggest(List<Rect> obs, int keep)
        {
            var copy = new List<Rect>(obs);
            copy.Sort(delegate(Rect a, Rect b) { return b.Area.CompareTo(a.Area); });

            var r = new List<Rect>(keep);
            for (int i = 0; i < keep && i < copy.Count; i++) r.Add(copy[i]);
            return r;
        }

        /// <summary>
        /// 最大空白矩形。做法：以所有障碍的左右边界把外框切成若干竖条，
        /// 每个竖条里再找纵向最大的空隙 —— 最大的那块就是答案。
        /// </summary>
        static bool LargestEmpty(List<Rect> obs, double x0, double y0, double x1, double y1,
                                 out double rx, out double ry, out double rw, out double rh)
        {
            rx = ry = rw = rh = 0;

            var xs = new List<double>(obs.Count * 2 + 2);
            xs.Add(x0);
            xs.Add(x1);

            foreach (Rect o in obs)
            {
                xs.Add(o.X0);
                xs.Add(o.X1);
            }

            xs.Sort();
            xs = Dedup(xs);

            double best = 0;

            for (int i = 0; i + 1 < xs.Count; i++)
            {
                double a = xs[i], b = xs[i + 1];
                if (b - a <= 1e-9) continue;

                // 与这个竖条有交叠的障碍，按下边界排序
                var spans = new List<Rect>();
                foreach (Rect o in obs)
                {
                    if (o.X1 <= a + 1e-9 || o.X0 >= b - 1e-9) continue;
                    spans.Add(o);
                }

                spans.Sort(delegate(Rect p, Rect q) { return p.Y0.CompareTo(q.Y0); });

                double cur = y0;

                for (int k = 0; k < spans.Count; k++)
                {
                    double s = spans[k].Y0, t = spans[k].Y1;

                    if (s > cur + 1e-9)
                    {
                        double h = s - cur;
                        double area = (b - a) * h;
                        if (area > best)
                        {
                            best = area;
                            rx = a; ry = cur; rw = b - a; rh = h;
                        }
                    }

                    if (t > cur) cur = t;
                    if (cur >= y1 - 1e-9) break;
                }

                if (y1 > cur + 1e-9)
                {
                    double h = y1 - cur;
                    double area = (b - a) * h;
                    if (area > best)
                    {
                        best = area;
                        rx = a; ry = cur; rw = b - a; rh = h;
                    }
                }
            }

            return best > 0;
        }

        static List<double> Dedup(List<double> sorted)
        {
            var r = new List<double>(sorted.Count);

            for (int i = 0; i < sorted.Count; i++)
            {
                if (r.Count == 0 || sorted[i] - r[r.Count - 1] > 1e-6) r.Add(sorted[i]);
            }

            return r;
        }

        // ------------------------------------------------------------------ 块名

        /// <summary>
        /// 文件名 → 合法块名。整图图框生成时用这个名做块定义。
        ///
        /// 带上路径的短哈希：不同目录下可能有两个同名的 dwg（比如都是「A1.dwg」），
        /// 只用文件名的话它们会共用一个块名，后扫的那个把前一个顶掉。
        /// </summary>
        public static string SafeBlockName(string s, string path)
        {
            var sb = new StringBuilder("FRAME_");

            foreach (char c in (s ?? ""))
            {
                if (c < ' ') continue;
                if ("<>/\\\":;?*|,=`".IndexOf(c) >= 0) continue;
                sb.Append(c);
            }

            if (sb.Length <= 6) sb.Append("A");

            sb.Append('_').Append(ShortHash(path));
            return sb.ToString();
        }

        /// <summary>路径的短哈希（6 位十六进制）。不用 GetHashCode —— 那个跨进程不稳定。</summary>
        static string ShortHash(string s)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] b = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                    return b[0].ToString("x2") + b[1].ToString("x2") + b[2].ToString("x2");
                }
            }
            catch { return "000000"; }
        }
    }
}
