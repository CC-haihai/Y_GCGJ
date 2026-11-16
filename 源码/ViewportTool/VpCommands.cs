using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace VpLayer
{
    /// <summary>
    /// 视口工具的启动钩子。
    ///
    /// 合并进主插件后**不再自己实现 IExtensionApplication** —— 一个程序集只能有一个入口，
    /// 挂两个会有一个不执行。现在由主插件的 AutoLayerApp 调 Start()/Stop()。
    /// </summary>
    public static class VpStartup
    {
        static int _ribbonTries;
        static bool _idleHooked;

        public static void Start()
        {
            // 进视口自动切归层配置 —— 这个要一直在跑，不能等用户敲命令
            try { VpProfileWatcher.Start(); } catch { }

            if (_idleHooked) return;
            try { AcApp.Idle += OnIdle; _idleHooked = true; } catch { }
        }

        public static void Stop()
        {
            try { AcApp.Idle -= OnIdle; } catch { }
            _idleHooked = false;
            try { VpProfileWatcher.Stop(); } catch { }
        }

        static void OnIdle(object sender, EventArgs e)
        {
            // 功能区可能在 CAD 启动时还没建好，多试几拍再放弃
            if (!VpRibbon.TryCreate())
            {
                if (++_ribbonTries < 30) return;
            }

            try { AcApp.Idle -= OnIdle; } catch { }
            _idleHooked = false;

            // 加载提示统一由主插件的 banner 打（见 AutoLayerApp.PrintBanner），这里不重复刷屏
        }
    }

    public sealed class VpCommands
    {
        const string Tag = "[视口图层]";

        /// <summary>
        /// 面板点「刷到视口」时先把选中项的序号放这里，命令就不再问序号。
        /// 走静态字段而不是命令行参数，是因为 SendStringToExecute 传参容易被当成回车。
        /// </summary>
        internal static int PendingStateIndex = -1;

        /// <summary>面板传入的比例。&lt; 0 表示没传（走命令行询问），0 表示自动。</summary>
        internal static double PendingScale = -1;

        /// <summary>
        /// 面板「标记视口」传进来的归层配置名。空 = 没传，命令里按当前配置走。
        /// 面板里那个下拉已经点选过了，命令就不用再列清单让人输序号。
        /// </summary>
        internal static string PendingTagProfile = null;

        // ------------------------------------------------------------------ 刷取

        /// <summary>
        /// 刷取：点一个已经调好的视口，把它「冻结了哪些层」记下来。
        /// 只记冻结集合，不记颜色/线型 —— 出图要统一的就是可见性。
        /// </summary>
        [CommandMethod("VLCAP")]
        public void Capture()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            if (InModelSpace())
            {
                ed.WriteMessage("\n" + Tag + " 请先切换到布局（图纸空间），在布局里选视口");
                return;
            }

            PromptEntityResult per = ed.GetEntity("\n选择要刷取的视口（点视口边框）: ");
            if (per.Status != PromptStatus.OK) return;

            if (!VpService.IsViewport(per.ObjectId))
            {
                ed.WriteMessage("\n" + Tag + " 选中的不是视口，请点视口边框");
                return;
            }

            if (VpService.IsMainViewport(doc.Database, per.ObjectId))
            {
                ed.WriteMessage("\n" + Tag + " 这是图纸空间主视口（视口 1），它没有视口冻结可记。"
                              + "\n  请点带边框的那个视口（用 MVIEW 建出来的），或者在视口内部先调好图层再刷。");
                return;
            }

            string err;
            List<string> frozen = VpService.ReadFrozenNames(doc.Database, per.ObjectId, out err);
            if (err != null)
            {
                ed.WriteMessage("\n" + Tag + " 读取失败：" + err);
                return;
            }

            VpConfig cfg = VpStore.Current;

            string def = "状态" + (cfg.States.Count + 1);

            // 名字来源一：点一个文字对象直接取它的内容（图框里的「平面布置图」这类标题最省事）
            string name = null;

            var peo = new PromptEntityOptions(
                "\n状态名称：点一个文字对象取名，或直接回车手动输入 <" + def + ">: ");
            peo.AllowNone = true;

            PromptEntityResult pn = ed.GetEntity(peo);
            if (pn.Status == PromptStatus.OK)
            {
                if (VpService.IsViewport(pn.ObjectId))
                {
                    // 图纸空间里点视口内部，选中的是视口本身，读不到里面的字
                    ed.WriteMessage("\n" + Tag + " 点到的是视口，读不出文字。"
                                  + "\n  请点图框标题栏里的文字，或直接回车手动输入");
                }
                else
                {
                    name = ReadTextContent(doc.Database, pn.ObjectId);
                    if (string.IsNullOrEmpty(name))
                        ed.WriteMessage("\n" + Tag + " 这个对象里没有文字，改用手动输入");
                }
            }

            // 名字来源二：手动输入
            if (string.IsNullOrEmpty(name))
            {
                PromptResult pr = ed.GetString("\n状态名称 <" + def + ">: ");
                name = pr.Status == PromptStatus.OK ? (pr.StringResult ?? "").Trim() : def;
            }

            name = (name ?? "").Trim();
            if (name.Length == 0) name = def;

            // 同名 = 更新，不新建。这样调完图层直接在原位置刷新，不用删了重刷
            VpState exist = cfg.Find(name);
            if (exist != null)
            {
                PromptResult kr = ed.GetString(
                    "\n已有同名状态「" + name + "」（冻结 " + exist.FrozenLayers.Count
                    + " 层），用这次刷的覆盖它？[是/否] <是>: ");

                string ans = kr.Status == PromptStatus.OK ? (kr.StringResult ?? "").Trim() : "";
                bool overwrite = ans.Length == 0
                    || ans == "是" || ans == "y" || ans == "Y"
                    || string.Equals(ans, "yes", StringComparison.OrdinalIgnoreCase);

                if (!overwrite)
                {
                    ed.WriteMessage("\n" + Tag + " 已取消，没有改动");
                    return;
                }

                exist.FrozenLayers.Clear();
                exist.FrozenLayers.AddRange(frozen);

                // 顺手把当前归层配置记进去；主插件没装/没配置时保留原来的，别清空
                string curProf = LayerProfile.CurrentProfile;
                if (!string.IsNullOrEmpty(curProf)) exist.Profile = curProf;

                VpStore.Save(cfg);

                ed.WriteMessage("\n" + Tag + " 已更新「" + name + "」：冻结 " + frozen.Count + " 层"
                    + (string.IsNullOrEmpty(exist.Profile) ? "" : "，归层配置「" + exist.Profile + "」")
                    + "\n  名字和序号都没变（第 " + (cfg.IndexOf(name) + 1) + " 个），批量生成时位置不变");

                // 刷取即绑定：你从这个视口刷的状态，就是「这个视口的标准」，
                // 顺手把标签打上，进这个视口自动切配置，不用再单独 VLTAG 一次
                BindViewport(doc, per.ObjectId, exist.Name, exist.Profile, ed);
                return;
            }

            name = cfg.UniqueName(name);

            var st = new VpState(name);
            st.FrozenLayers.AddRange(frozen);
            st.Profile = LayerProfile.CurrentProfile;

            int idx = cfg.States.Count;
            cfg.States.Add(st);

            if (!VpStore.Save(cfg))
            {
                ed.WriteMessage("\n" + Tag + " 已加入列表，但配置文件写盘失败（检查插件目录权限）");
            }

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 已记住「").Append(name).Append("」，序号 ")
              .Append(idx + 1).Append("，冻结 ").Append(frozen.Count).Append(" 层");
            if (!string.IsNullOrEmpty(st.Profile))
                sb.Append("，归层配置「").Append(st.Profile).Append("」");
            if (frozen.Count > 0)
            {
                int show = Math.Min(frozen.Count, 10);
                sb.Append("\n  ").Append(string.Join("、", frozen.GetRange(0, show).ToArray()));
                if (frozen.Count > show) sb.Append(" 等");
            }
            ed.WriteMessage(sb.ToString());

            // 刷取即绑定：这个视口就是这套状态的标准，顺手打上标签
            BindViewport(doc, per.ObjectId, st.Name, st.Profile, ed);
        }

        // ------------------------------------------------------------------ 单刷

        /// <summary>把某个已记住的状态刷到选中的视口上。面板传了序号就不再询问。</summary>
        [CommandMethod("VLAPPLY")]
        public void ApplyToViewport()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;
            if (cfg.States.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 还没有记住任何状态，先刷取一个");
                return;
            }

            if (InModelSpace())
            {
                ed.WriteMessage("\n" + Tag + " 请先切换到布局（图纸空间），在布局里选视口");
                return;
            }

            VpState st = null;

            int pending = PendingStateIndex;
            PendingStateIndex = -1;
            if (pending >= 0 && pending < cfg.States.Count) st = cfg.States[pending];

            if (st == null) st = PickState(ed, cfg, "要刷哪个状态");
            if (st == null) return;

            PromptEntityResult per = ed.GetEntity("\n点要刷的视口（点视口边框）: ");
            if (per.Status != PromptStatus.OK) return;

            if (!VpService.IsViewport(per.ObjectId))
            {
                ed.WriteMessage("\n" + Tag + " 选中的不是视口");
                return;
            }

            VpService.ApplyResult ar = VpService.Apply(doc.Database, per.ObjectId, st);
            ReportApply(ed, st, ar);
            WriteTag(doc, per.ObjectId, st, ed);
        }

        // ------------------------------------------------------------------ 更新已有状态

        /// <summary>
        /// 更新一个已有状态：点一个视口，用它的冻结层覆盖这个状态，名字和序号都不变。
        /// 用途是调完图层后原地刷新，不用「删掉再刷一遍」。
        /// </summary>
        [CommandMethod("VLUPDATE")]
        public void UpdateState()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;
            if (cfg.States.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 还没有记住任何状态，先刷取一个");
                return;
            }

            if (InModelSpace())
            {
                ed.WriteMessage("\n" + Tag + " 请先切换到布局（图纸空间），在布局里选视口");
                return;
            }

            VpState st = null;

            int pending = PendingStateIndex;
            PendingStateIndex = -1;
            if (pending >= 0 && pending < cfg.States.Count) st = cfg.States[pending];

            if (st == null) st = PickState(ed, cfg, "要更新哪个状态");
            if (st == null) return;

            PromptEntityResult per = ed.GetEntity("\n点视口（用它的冻结层覆盖「" + st.Name + "」）: ");
            if (per.Status != PromptStatus.OK) return;

            if (!VpService.IsViewport(per.ObjectId))
            {
                ed.WriteMessage("\n" + Tag + " 选中的不是视口");
                return;
            }

            string err;
            List<string> frozen = VpService.ReadFrozenNames(doc.Database, per.ObjectId, out err);
            if (err != null)
            {
                ed.WriteMessage("\n" + Tag + " 读取失败：" + err);
                return;
            }

            int before = st.FrozenLayers.Count;
            st.FrozenLayers.Clear();
            st.FrozenLayers.AddRange(frozen);

            if (!VpStore.Save(cfg))
                ed.WriteMessage("\n" + Tag + " 已更新，但配置文件写盘失败（检查插件目录权限）");

            ed.WriteMessage("\n" + Tag + " 「" + st.Name + "」已更新：冻结 " + frozen.Count + " 层"
                + "（原 " + before + " 层），名字和序号不变");
        }

        // ------------------------------------------------------------------ 图框

        /// <summary>
        /// 拾取图框。只有一个入口，三步走：
        ///   1. 选对象 —— 点一个块/属性块直接用；框选一组散线则自动组成块
        ///      （原位插回块参照，图纸上看到的图形不变）；
        ///   2. 外框 —— 默认用块的实际尺寸，回车确认；想改就选「重框」框两点；
        ///   3. 内框 —— 框两个对角点圈出绘图区，也可以直接跳过。
        /// 每一步都可以回车走默认值、Esc 取消，不会停在提示上等。
        /// 不限定模型空间还是布局空间 —— 图框在哪都能拾取。
        /// </summary>
        [CommandMethod("VLFRAME", CommandFlags.Redraw)]
        public void SaveFrame()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;

            try
            {
                // ---- 第 1 步：选对象 ----
                var so = new PromptSelectionOptions();
                so.MessageForAdding = "\n选择图框对象 <回车结束>: ";

                ed.WriteMessage("\n" + Tag + " 第 1 步：选图框（点块，或框选整组线 —— 散线会自动组成块）");

                PromptSelectionResult psr = ed.GetSelection(so);
                if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                {
                    ed.WriteMessage("\n" + Tag + " 没有选中对象，已取消");
                    return;
                }

                var temp = new FrameTemplate();
                Point3d outerMin;
                string err;

                if (!VpService.PrepareFrameBlock(doc.Database, psr.Value.GetObjectIds(), temp, out outerMin, out err))
                {
                    ed.WriteMessage("\n" + Tag + " " + err);
                    return;
                }

                ed.WriteMessage("\n" + Tag + " 已识别：" + (temp.IsBlock ? "块「" + temp.BlockName + "」" : "矩形")
                    + "　外框 " + Math.Round(temp.Width) + " × " + Math.Round(temp.Height));

                // ---- 第 2 步：外框 ----
                var ko = new PromptKeywordOptions("\n外框 "
                    + Math.Round(temp.Width) + " × " + Math.Round(temp.Height)
                    + " 对吗？回车＝是，R＝重新框");
                ko.Keywords.Add("Y");
                ko.Keywords.Add("R");
                ko.Keywords.Default = "Y";
                ko.AllowNone = true;

                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status == PromptStatus.Cancel)
                {
                    ed.WriteMessage("\n" + Tag + " 已取消，图框没保存");
                    return;
                }

                if (kr.Status == PromptStatus.OK
                    && string.Equals(kr.StringResult, "R", StringComparison.OrdinalIgnoreCase))
                {
                    Point3d a, b;
                    if (!PickRect(ed, "外框", out a, out b))
                    {
                        ed.WriteMessage("\n" + Tag + " 没框到外框，已取消，图框没保存");
                        return;
                    }

                    double nw = Math.Abs(b.X - a.X);
                    double nh = Math.Abs(b.Y - a.Y);
                    if (nw <= 0 || nh <= 0)
                    {
                        ed.WriteMessage("\n" + Tag + " 外框尺寸为 0，已取消");
                        return;
                    }

                    // 块按新尺寸缩放，保证插入后和模板尺寸一致
                    if (temp.Width > 0) temp.ScaleX *= nw / temp.Width;
                    if (temp.Height > 0) temp.ScaleY *= nh / temp.Height;

                    temp.Width = nw;
                    temp.Height = nh;
                    outerMin = new Point3d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), 0);
                }

                // ---- 第 3 步：内框（绘图区）----
                var ko2 = new PromptKeywordOptions("\n框内框（视口铺满的区域）？回车＝框，S＝跳过");
                ko2.Keywords.Add("B");
                ko2.Keywords.Add("S");
                ko2.Keywords.Default = "B";
                ko2.AllowNone = true;

                PromptResult kr2 = ed.GetKeywords(ko2);
                if (kr2.Status == PromptStatus.Cancel)
                {
                    ed.WriteMessage("\n" + Tag + " 已取消，图框没保存");
                    return;
                }

                bool skipInner = kr2.Status == PromptStatus.OK
                    && string.Equals(kr2.StringResult, "S", StringComparison.OrdinalIgnoreCase);

                if (!skipInner)
                {
                    Point3d a, b;
                    if (PickRect(ed, "内框", out a, out b))
                    {
                        double ix = Math.Min(a.X, b.X) - outerMin.X;
                        double iy = Math.Min(a.Y, b.Y) - outerMin.Y;
                        double iw = Math.Abs(b.X - a.X);
                        double ih = Math.Abs(b.Y - a.Y);

                        if (iw > 0 && ih > 0 && iw <= temp.Width + 0.01 && ih <= temp.Height + 0.01)
                        {
                            temp.InnerX = ix;
                            temp.InnerY = iy;
                            temp.InnerWidth = iw;
                            temp.InnerHeight = ih;
                        }
                        else
                        {
                            ed.WriteMessage("\n" + Tag + " 内框为空或比外框大，已忽略");
                        }
                    }
                    else
                    {
                        ed.WriteMessage("\n" + Tag + " 内框没框，按外框内缩 2 处理");
                    }
                }

                // ---- 写进当前图框 ----
                FrameTemplate f = cfg.CurrentOrNew();

                f.IsBlock = temp.IsBlock;
                f.BlockName = temp.BlockName;
                f.Width = temp.Width;
                f.Height = temp.Height;
                f.ScaleX = temp.ScaleX;
                f.ScaleY = temp.ScaleY;
                f.InnerX = temp.InnerX;
                f.InnerY = temp.InnerY;
                f.InnerWidth = temp.InnerWidth;
                f.InnerHeight = temp.InnerHeight;

                if (string.IsNullOrWhiteSpace(f.Label))
                    f.Label = f.IsBlock && !string.IsNullOrEmpty(f.BlockName)
                            ? f.BlockName
                            : "图框" + cfg.Frames.Count;

                // 记下这个块来自哪张图纸 —— 老数据靠它兜底，不是主路径了
                f.SourceDwg = f.IsBlock ? (doc.Name ?? "") : "";

                // 手动拾取的图框不属于「图框库目录」，把文件来源清掉，
                // 否则下次扫描目录时会被同名文件当成同一个人、被覆盖
                f.LibFile = "";
                f.WholeFile = false;
                f.InnerManual = true;

                // 存进插件自己的图框库：之后这个图框就跟源图纸没关系了。
                // 源图纸没保存过、被移走、改名，都不影响生成 —— 这是「白线无图框」的根治。
                string libMsg = "";
                if (f.IsBlock && !string.IsNullOrEmpty(f.BlockName))
                {
                    string libErr;
                    libMsg = VpService.SaveBlockToLib(doc.Database, f.BlockName, out libErr)
                        ? "\n  ● 已存进图框库，以后换任何图纸都能用（" + VpService.FrameLibPath + "）"
                        : "\n  ⚠ 图框库写入失败（" + libErr + "）—— 换图纸生成时可能只能画矩形";
                }

                bool ok = VpStore.Save(cfg);

                ed.WriteMessage("\n" + Tag + " 图框已保存：" + f.Describe()
                    + (f.HasInner ? "" : "\n  内框没框，视口会按外框内缩 2 铺")
                    + libMsg
                    + (ok ? "" : "\n  但配置文件写盘失败（检查插件目录权限）"));
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n" + Tag + " 拾取图框出错：" + ex.Message);
            }
        }

        /// <summary>
        /// 扫一遍图框库目录：目录里每个 dwg 认成一个图框。
        ///
        /// 合并规则见 FrameLib.Scan —— 认人靠文件路径，用户改过的名字和手动框过的内框都不会被冲掉。
        /// </summary>
        [CommandMethod("VLLIB")]
        public void ScanFrameLibrary()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;
            string dir = FrameLib.Dir(cfg);

            if (!FrameLib.DirExists(dir))
            {
                ed.WriteMessage("\n" + Tag + " 图框库目录不存在：" + dir
                    + "\n  在面板「图框」里点「浏览」选一个文件夹，把图框 dwg 丢进去");
                return;
            }

            List<string> added, updated, errors;
            int ok = FrameLib.Scan(cfg, out added, out updated, out errors);

            if (ok > 0) VpStore.Save(cfg);

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 图框库：").Append(dir);
            sb.Append("\n   认出 ").Append(ok).Append(" 个图框");

            if (added.Count > 0)
                sb.Append("\n   新增：").Append(string.Join("、", added.ToArray()));

            if (updated.Count > 0)
                sb.Append("\n   更新：").Append(string.Join("、", updated.ToArray()));

            if (ok == 0)
                sb.Append("\n   目录里没有 dwg —— 把图框文件复制进去再扫一次");

            if (errors.Count > 0)
            {
                sb.Append("\n   ⚠ 有 ").Append(errors.Count).Append(" 个读不了：");
                int show = Math.Min(errors.Count, 5);
                for (int i = 0; i < show; i++) sb.Append("\n     · ").Append(errors[i]);
                if (errors.Count > show) sb.Append("\n     …等");
            }

            ed.WriteMessage(sb.ToString());
        }

        /// <summary>
        /// 重框当前图框的内框。自动识别不准、或者图框改了，用它手动框一次。
        ///
        /// 基准是「块在当前图纸里的外框左下角」，不是世界原点 ——
        /// 图框插在哪儿都不影响结果，框出来的偏移换图纸也照样用。
        /// </summary>
        [CommandMethod("VLINNER", CommandFlags.Redraw)]
        public void ReframeInner()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;
            FrameTemplate f = cfg.Frame;

            if (f == null || !f.Valid)
            {
                ed.WriteMessage("\n" + Tag + " 还没有图框，先扫描图框库或拾取一个");
                return;
            }

            if (!f.IsBlock || string.IsNullOrEmpty(f.BlockName))
            {
                ed.WriteMessage("\n" + Tag + " 这个图框没有块，没法定位外框 —— 请用「拾取图框」重新拾一次");
                return;
            }

            // 在当前图纸里找这个图框的块参照，拿它的外框左下角当基准
            Point3d outerMin;
            if (!FindFrameCorner(doc.Database, f, out outerMin))
            {
                ed.WriteMessage("\n" + Tag + " 当前图纸里找不到图框块「" + f.BlockName + "」"
                    + "\n  先把图框插进这张图纸（或跑一次批量生成），再框内框");
                return;
            }

            Point3d a, b;
            if (!PickRect(ed, "内框", out a, out b))
            {
                ed.WriteMessage("\n" + Tag + " 没框到，内框没变");
                return;
            }

            double ix = Math.Min(a.X, b.X) - outerMin.X;
            double iy = Math.Min(a.Y, b.Y) - outerMin.Y;
            double iw = Math.Abs(b.X - a.X);
            double ih = Math.Abs(b.Y - a.Y);

            if (iw <= 0 || ih <= 0 || iw > f.Width + 0.01 || ih > f.Height + 0.01)
            {
                ed.WriteMessage("\n" + Tag + " 内框为空或比外框大，已忽略");
                return;
            }

            f.InnerX = ix;
            f.InnerY = iy;
            f.InnerWidth = iw;
            f.InnerHeight = ih;
            f.InnerManual = true;

            bool ok = VpStore.Save(cfg);

            ed.WriteMessage("\n" + Tag + " 内框已更新：" + Math.Round(iw) + " × " + Math.Round(ih)
                + "　（相对外框左下角 " + Math.Round(ix) + ", " + Math.Round(iy) + "）"
                + "\n  以后重新扫描图框库也不会覆盖它"
                + (ok ? "" : "\n  但配置文件写盘失败（检查插件目录权限）"));
        }

        /// <summary>在当前图纸里找图框块参照，返回它的外框左下角（世界坐标）。</summary>
        static bool FindFrameCorner(Database db, FrameTemplate f, out Point3d corner)
        {
            corner = Point3d.Origin;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(f.BlockName)) return false;

                    BlockTableRecord bdef = (BlockTableRecord)tr.GetObject(bt[f.BlockName], OpenMode.ForRead);

                    Extents3d defExt;
                    if (!VpService.BlockOuterRef(tr, bdef, out defExt)) return false;

                    Point3d org = Point3d.Origin;
                    try { org = bdef.Origin; } catch { }

                    // 只找布局（模型空间 + 各图纸空间）里的块参照，
                    // 块定义内部不算 —— 图框块自己嵌自己那种情况不该匹配
                    foreach (ObjectId btrId in bt)
                    {
                        BlockTableRecord space = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                        if (space == null || !space.IsLayout) continue;

                        foreach (ObjectId id in space)
                        {
                            BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null || br.BlockTableRecord != bt[f.BlockName]) continue;

                            Scale3d sf = br.ScaleFactors;
                            double sx = Math.Abs(sf.X) > 1e-9 ? Math.Abs(sf.X) : 1.0;
                            double sy = Math.Abs(sf.Y) > 1e-9 ? Math.Abs(sf.Y) : 1.0;

                            corner = new Point3d(
                                br.Position.X + (defExt.MinPoint.X - org.X) * sx,
                                br.Position.Y + (defExt.MinPoint.Y - org.Y) * sy,
                                0);

                            tr.Commit();
                            return true;
                        }
                    }

                    tr.Commit();
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 框一个矩形：点第一个角点，再点对角点（拉出橡皮筋框）。
        /// 回车或 Esc 都直接返回 false —— 提示不会卡住不往下走。
        /// </summary>
        static bool PickRect(Editor ed, string what, out Point3d a, out Point3d b)
        {
            a = Point3d.Origin;
            b = Point3d.Origin;

            var p1 = new PromptPointOptions("\n" + what + "第一个角点（回车取消）: ");
            p1.AllowNone = true;

            PromptPointResult r1 = ed.GetPoint(p1);
            if (r1.Status != PromptStatus.OK) return false;

            var p2 = new PromptCornerOptions("\n" + what + "对角点: ", r1.Value);
            p2.AllowNone = true;

            PromptPointResult r2 = ed.GetCorner(p2);
            if (r2.Status != PromptStatus.OK) return false;

            a = r1.Value;
            b = r2.Value;
            return true;
        }

        // ------------------------------------------------------------------ 批量

        /// <summary>
        /// 批量生成：模型空间框选一片图元 → 输比例 → 新建布局 →
        /// 按状态列表顺序生成 N 个「图框 + 视口」。
        /// </summary>
        [CommandMethod("VLBATCH", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Batch()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;

            // 面板传进来的比例先取走，避免提前 return 时留在字段里影响下一次
            double pendingScale = PendingScale;
            PendingScale = -1;

            if (cfg.States.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 还没有记住任何视口状态，先刷取一个");
                return;
            }
            if (!cfg.Frame.Valid)
            {
                ed.WriteMessage("\n" + Tag + " 还没有图框，先点「拾取图框」");
                return;
            }

            // 框选必须在模型空间
            if (!InModelSpace())
            {
                // 优先走 LayoutManager 切「Model」——它比直接写 TILEMODE 稳，
                // 会连带把当前布局、编辑器状态一起切过去
                bool switched = false;
                try { LayoutManager.Current.CurrentLayout = "Model"; switched = true; } catch { }
                if (!switched)
                {
                    try { AcApp.SetSystemVariable("TILEMODE", 1); } catch { }
                }
                ed.WriteMessage("\n" + Tag + " 已切到模型空间，请框选要出图的内容");
            }

            var bsel = new PromptSelectionOptions();
            bsel.MessageForAdding = "\n框选要出图的模型内容 <回车结束>: ";

            PromptSelectionResult psr = ed.GetSelection(bsel);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n" + Tag + " 没有选中对象，已取消");
                return;
            }

            Extents3d ext;
            string err;
            if (!VpBatch.TryGetExtents(doc.Database, psr.Value.GetObjectIds(), out ext, out err))
            {
                ed.WriteMessage("\n" + Tag + " 无法计算范围：" + err);
                return;
            }

            // 比例：面板传了就用面板的（0 = 自动），否则问一次
            double scale;
            if (pendingScale >= 0)
            {
                scale = pendingScale;
            }
            else
            {
                PromptResult pr = ed.GetString("\n出图比例 1:<n>（直接输 n，如 50 表示 1:50；回车 = 按内框自动）: ");
                string scaleText = pr.Status == PromptStatus.OK ? pr.StringResult : "";
                if (!TryParseScale(scaleText, out scale))
                {
                    ed.WriteMessage("\n" + Tag + " 比例看不懂，已取消（输入 50 或 1:50）");
                    return;
                }
            }

            ed.WriteMessage("\n" + Tag + " 正在生成 " + cfg.States.Count
                + " 张图纸（每个状态一张），请稍候…");

            VpBatch.Result res = VpBatch.Generate(doc, cfg, ext, scale);

            if (!res.Ok)
            {
                ed.WriteMessage("\n" + Tag + " 生成失败：" + res.Error);
                for (int i = 0; i < res.Notes.Count; i++)
                    ed.WriteMessage("\n  · " + res.Notes[i]);
                return;
            }

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 已生成 ").Append(res.Layouts).Append(" 张图纸");
            sb.Append("，视口 ").Append(res.Viewports).Append(" 个");
            if (res.Tagged > 0) sb.Append("，已绑定归层配置 ").Append(res.Tagged).Append(" 个");
            sb.Append("，图框 ").Append(res.FramesBlock).Append(" 个块");
            if (res.FramesRect > 0) sb.Append(" + ").Append(res.FramesRect).Append(" 个矩形");
            sb.Append("\n  比例 1:").Append(Math.Round(1.0 / res.Scale));
            sb.Append("　内容 ").Append(Math.Round(ext.MaxPoint.X - ext.MinPoint.X))
              .Append(" × ").Append(Math.Round(ext.MaxPoint.Y - ext.MinPoint.Y));

            if (res.LayoutNames.Count > 0)
            {
                int show = Math.Min(res.LayoutNames.Count, 8);
                sb.Append("\n  布局：").Append(string.Join("、", res.LayoutNames.GetRange(0, show).ToArray()));
                if (res.LayoutNames.Count > show) sb.Append(" 等");
            }

            if (res.MissingLayerTotal > 0)
                sb.Append("\n  有 ").Append(res.MissingLayerTotal).Append(" 个图层在本图纸不存在，已跳过");

            if (res.FramesRect > 0)
                sb.Append("\n  ⚠ 有 ").Append(res.FramesRect)
                  .Append(" 个图框没插进块，只画了空矩形 —— 看下面的原因");

            for (int i = 0; i < res.Notes.Count; i++)
                sb.Append("\n  · ").Append(res.Notes[i]);

            ed.WriteMessage(sb.ToString());
        }

        // ------------------------------------------------------------------ 列表 / 删除

        [CommandMethod("VLLIST")]
        public void List()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;
            var sb = new StringBuilder();

            sb.Append("\n").Append(Tag).Append(" 图框 ").Append(cfg.Frames.Count).Append(" 个，当前第 ")
              .Append(cfg.FrameIndex + 1).Append(" 个");
            for (int i = 0; i < cfg.Frames.Count; i++)
            {
                FrameTemplate f = cfg.Frames[i];
                if (f == null) continue;
                sb.Append("\n   ").Append(i + 1).Append(". ").Append(f.Describe());
                if (i == cfg.FrameIndex) sb.Append("　←当前");
            }

            sb.Append("\n  比例 ").Append(cfg.Scale > 0 ? "1:" + Math.Round(1.0 / cfg.Scale) : "自动");

            if (cfg.States.Count == 0)
            {
                sb.Append("\n  状态集「").Append(cfg.ActiveGroup.DisplayName)
                  .Append("」里还没有记住任何视口状态（VLCAP）");
            }
            else
            {
                sb.Append("\n  状态集「").Append(cfg.ActiveGroup.DisplayName)
                  .Append("」· 批量生成按这个顺序套用：");
                for (int i = 0; i < cfg.States.Count; i++)
                {
                    VpState st = cfg.States[i];
                    sb.Append("\n   ").Append(i + 1).Append(". ").Append(st.Name)
                      .Append("　冻结 ").Append(st.FrozenLayers.Count).Append(" 层");
                }
            }

            sb.Append("\n  配置文件：").Append(VpStore.FilePath);
            ed.WriteMessage(sb.ToString());
        }

        [CommandMethod("VLDEL")]
        public void Delete()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;
            if (cfg.States.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 列表是空的");
                return;
            }

            VpState st = PickState(ed, cfg, "要删除哪个状态");
            if (st == null) return;

            cfg.States.Remove(st);
            VpStore.Save(cfg);
            ed.WriteMessage("\n" + Tag + " 已删除「" + st.Name + "」，剩余 " + cfg.States.Count + " 个");
        }

        // ------------------------------------------------------------------ 视口标记（归层配置绑定）

        /// <summary>
        /// 给视口打标记：把「这个视口用哪套归层配置」写进图纸。
        ///
        /// 用途是外来图纸 —— 图框和标题栏不是自家标准，没法靠批量生成统一，
        /// 就手动点一下视口、指定配置。标记写进视口的 Xrecord，
        /// 不出现在图纸任何可见处，没装插件的人看不出来。
        /// </summary>
        [CommandMethod("VLTAG", CommandFlags.UsePickSet)]
        public void TagViewport()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            if (InModelSpace())
            {
                ed.WriteMessage("\n" + Tag + " 请先切换到布局（图纸空间），视口标记只能打在布局视口上");
                return;
            }

            if (!LayerProfile.Available)
            {
                ed.WriteMessage("\n" + Tag + " " + LayerProfile.UnavailableReason);
                return;
            }

            List<string> names = LayerProfile.ProfileNames();
            if (names.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 主插件里还没有任何归层配置，先在归层工具里建一个");
                return;
            }

            // ---- 1) 定配置：面板预设 > 当前配置（回车即用）> 列清单选 ----
            string pick = PendingTagProfile;
            PendingTagProfile = null;

            if (string.IsNullOrEmpty(pick))
            {
                string cur = LayerProfile.CurrentProfile;

                if (!string.IsNullOrEmpty(cur) && LayerProfile.HasProfile(cur))
                {
                    var ko = new PromptKeywordOptions(
                        "\n用当前归层配置「" + cur + "」标记视口？回车＝是，S＝换一个");
                    ko.Keywords.Add("S");
                    ko.Keywords.Default = "Y";
                    ko.AllowNone = true;

                    PromptResult kr = ed.GetKeywords(ko);
                    if (kr.Status == PromptStatus.Cancel) return;

                    if (kr.Status == PromptStatus.OK
                        && string.Equals(kr.StringResult, "S", StringComparison.OrdinalIgnoreCase))
                        pick = PickProfile(ed, names, cur);
                    else
                        pick = cur;
                }
                else
                {
                    pick = PickProfile(ed, names, null);
                }
            }

            if (string.IsNullOrEmpty(pick)) return;

            // ---- 2) 选视口：点选一个，也可以框选一片，回车结束 ----
            var filter = new SelectionFilter(new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, "VIEWPORT")
            });

            var so = new PromptSelectionOptions();
            so.MessageForAdding = "\n选择要标记的视口（可点选，也可框选一片）<回车结束>: ";

            PromptSelectionResult psr = ed.GetSelection(so, filter);
            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 没选到视口，已取消");
                return;
            }

            // ---- 3) 逐个写标签 ----
            int done = 0, skipped = 0, failed = 0;

            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                if (!VpService.IsViewport(id)) { skipped++; continue; }

                // 主视口（Number=1）是图纸空间本身，没有 VPLAYER 冻结，标了也没意义
                if (VpService.IsMainViewport(doc.Database, id)) { skipped++; continue; }

                if (VpTagStore.Set(doc.Database, id, pick)) done++;
                else failed++;
            }

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 已标记 ").Append(done).Append(" 个视口 → 归层配置「")
              .Append(pick).Append("」");

            if (skipped > 0) sb.Append("\n  跳过 ").Append(skipped).Append(" 个（主视口或不是视口）");
            if (failed > 0) sb.Append("\n  ⚠ ").Append(failed).Append(" 个没写进图纸（图纸可能是只读的，或文件被占用）");

            if (done > 0)
                sb.Append("\n  进这些视口会自动切到「").Append(pick).Append("」，退出自动恢复")
                  .Append("\n  标记存在图纸里，但图纸上没有任何可见痕迹（别人打开看不到）");

            ed.WriteMessage(sb.ToString());

            try { VpProfileWatcher.Refresh(); } catch { }
        }

        /// <summary>去掉视口标记。支持点选一个，也支持框选一片一起取消。</summary>
        [CommandMethod("VLUNTAG", CommandFlags.UsePickSet)]
        public void UntagViewport()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            if (InModelSpace())
            {
                ed.WriteMessage("\n" + Tag + " 请先切换到布局（图纸空间）");
                return;
            }

            var filter = new SelectionFilter(new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, "VIEWPORT")
            });

            var so = new PromptSelectionOptions();
            so.MessageForAdding = "\n选择要取消标记的视口（可点选，也可框选）<回车结束>: ";

            PromptSelectionResult psr = ed.GetSelection(so, filter);
            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 没选到视口，已取消");
                return;
            }

            int done = 0, none = 0, failed = 0;

            foreach (ObjectId id in psr.Value.GetObjectIds())
            {
                if (!VpService.IsViewport(id)) continue;

                if (VpTagStore.GetProfile(doc.Database, id) == null) { none++; continue; }

                if (VpTagStore.Remove(doc.Database, id)) done++;
                else failed++;
            }

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 已取消 ").Append(done).Append(" 个视口的标记");
            if (none > 0) sb.Append("\n  ").Append(none).Append(" 个本来就没标记");
            if (failed > 0) sb.Append("\n  ⚠ ").Append(failed).Append(" 个取消失败（图纸可能是只读的）");

            ed.WriteMessage(sb.ToString());

            try { VpProfileWatcher.Refresh(); } catch { }
        }

        /// <summary>
        /// 列出当前图纸里所有视口标记，并做一遍体检。
        ///
        /// 体检查三类「标了但不会生效」的情况 —— 提前发现，不用等进了视口发现没切换：
        ///   1. 标签里的配置名在主插件里不存在（改名或删了）
        ///   2. 标签指向的视口是关着的（On=false），根本进不去
        ///   3. 还有多少视口没打标记（外来图纸容易漏）
        /// </summary>
        [CommandMethod("VLTAGS")]
        public void ListTags()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            int pruned = VpTagStore.Prune(db);
            Dictionary<string, string> map = VpTagStore.Read(db);

            bool hasProfiles = LayerProfile.ProfileNames().Count > 0;
            var missing = new List<string>();
            var closed = new List<string>();
            int alive = 0;

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 本图纸视口标记 ").Append(map.Count).Append(" 个");

            if (map.Count == 0)
            {
                sb.Append("\n  还没有标记。外来图纸用 VLTAG 点视口指定归层配置");
            }
            else
            {
                ObjectId cur = VpProfileWatcher.CurrentViewportId;

                foreach (KeyValuePair<string, string> kv in map)
                {
                    ObjectId vpId;
                    bool ok = VpTagStore.TryGetViewport(db, kv.Key, out vpId);

                    string where = "";
                    if (ok) where = VpService.LayoutNameOf(db, vpId);

                    sb.Append("\n   ").Append(ok ? "●" : "×")
                      .Append(" 视口 ").Append(kv.Key)
                      .Append("　").Append(kv.Value);
                    if (!string.IsNullOrEmpty(where)) sb.Append("　（").Append(where).Append("）");
                    if (ok && !cur.IsNull && vpId == cur) sb.Append("　←当前");

                    if (!ok) continue;

                    alive++;

                    if (hasProfiles && !LayerProfile.HasProfile(kv.Value) && !missing.Contains(kv.Value))
                        missing.Add(kv.Value);

                    if (VpService.IsViewportClosed(db, vpId)) closed.Add(kv.Key);
                }
            }

            if (pruned > 0) sb.Append("\n  已清掉 ").Append(pruned).Append(" 条指向已删除视口的记录");

            // ---- 体检 ----
            int closedAll;
            List<ObjectId> all = VpService.AllViewports(db, out closedAll);
            int untagged = Math.Max(0, all.Count - alive);

            sb.Append("\n  ── 体检 ──");
            sb.Append("\n   全图视口 ").Append(all.Count).Append(" 个：已标记 ").Append(alive)
              .Append("，未标记 ").Append(untagged);
            if (closedAll > 0) sb.Append("；其中关着的 ").Append(closedAll).Append(" 个");

            if (missing.Count > 0)
            {
                sb.Append("\n   ⚠ 这些配置在主插件里找不到，进视口不会切：")
                  .Append(string.Join("、", missing.ToArray()));
            }

            if (closed.Count > 0)
            {
                sb.Append("\n   ⚠ 这些标记的视口是关着的，进不去：")
                  .Append(string.Join("、", closed.ToArray()));
            }

            if (untagged > 0)
                sb.Append("\n   未标记的视口进去不会切配置（外来图纸可用 VLTAG 补）");

            if (!hasProfiles)
                sb.Append("\n   主插件里还没有任何归层配置，标记一律不会生效");

            ed.WriteMessage(sb.ToString());
        }

        /// <summary>
        /// 名字对齐：把状态和归层配置的名字/绑定关系理顺，让「进视口自动切」真的生效。
        ///
        /// 三步：
        ///   1. 能确定的直接绑 —— 状态名和配置名完全一样，就把绑定设成它，不用问
        ///   2. 打印一张对齐表：谁绑上了、谁差一点、谁完全没对应
        ///   3. 差一点的逐条问：改状态名 / 改绑定 / 跳过
        ///
        /// 只动插件自己的配置文件，不碰图纸 —— 已经写进图纸的标签是另一回事，
        /// 想让老视口也跟上，用 VLTAG 重标或重新刷一次状态。
        /// </summary>
        [CommandMethod("VLALIGN")]
        public void AlignProfiles()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            VpConfig cfg = VpStore.Current;

            if (cfg.States.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 还没有视口状态，先 VLCAP 刷一个");
                return;
            }

            List<string> names = LayerProfile.ProfileNames();
            if (names.Count == 0)
            {
                ed.WriteMessage("\n" + Tag + " 主插件里还没有任何归层配置，没东西可对");
                return;
            }

            // ---- 第 1 步：同名直接绑 ----
            int auto = 0;

            for (int i = 0; i < cfg.States.Count; i++)
            {
                VpState st = cfg.States[i];
                if (st == null) continue;

                ProfileMatch.Hit hit = ProfileMatch.Resolve(st.Name, st.Profile);
                if (hit.Kind != ProfileMatch.Kind.SameName) continue;
                if (string.Equals(st.Profile, hit.Profile, StringComparison.Ordinal)) continue;

                st.Profile = hit.Profile;
                auto++;
            }

            if (auto > 0) VpStore.Save(cfg);

            // ---- 第 2 步：对齐表 ----
            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 状态 ↔ 归层配置");

            int need = 0;

            for (int i = 0; i < cfg.States.Count; i++)
            {
                VpState st = cfg.States[i];
                if (st == null) continue;

                ProfileMatch.Hit hit = ProfileMatch.Resolve(st.Name, st.Profile);

                sb.Append("\n   ").Append(i + 1).Append(". ").Append(Pad(st.Name, 14));

                switch (hit.Kind)
                {
                    case ProfileMatch.Kind.Bound:
                        sb.Append(" → ").Append(hit.Profile).Append("　✓ 已绑");
                        break;

                    case ProfileMatch.Kind.SameName:
                        sb.Append(" → ").Append(hit.Profile).Append("　✓ 同名");
                        break;

                    case ProfileMatch.Kind.Fuzzy:
                        sb.Append(" → （").Append(hit.Suggest).Append("）　~ 名字接近");
                        need++;
                        break;

                    default:
                        sb.Append(" → —　✗ 没有对应配置");
                        need++;
                        break;
                }
            }

            sb.Append("\n   ── ");
            sb.Append(auto > 0 ? "已自动绑上 " + auto + " 个（同名）" : "没有需要自动绑的");
            sb.Append(need > 0 ? "，还有 " + need + " 个要处理" : "，全部对上了");

            ed.WriteMessage(sb.ToString());

            if (need == 0) return;

            // ---- 第 3 步：逐条处理 ----
            int bound = 0, renamed = 0, skipped = 0;

            for (int i = 0; i < cfg.States.Count; i++)
            {
                VpState st = cfg.States[i];
                if (st == null) continue;

                ProfileMatch.Hit hit = ProfileMatch.Resolve(st.Name, st.Profile);
                if (hit.Kind == ProfileMatch.Kind.Bound || hit.Kind == ProfileMatch.Kind.SameName) continue;

                string hint = hit.Kind == ProfileMatch.Kind.Fuzzy
                    ? "名字接近「" + hit.Suggest + "」"
                    : "没有对应配置";

                var ko = new PromptKeywordOptions("\n[" + st.Name + "] " + hint
                    + "　B=绑定某个配置  R=把状态名改成配置名  回车=跳过");
                ko.Keywords.Add("B");
                ko.Keywords.Add("R");
                ko.Keywords.Add("S");
                ko.AllowNone = true;

                PromptResult kr = ed.GetKeywords(ko);

                if (kr.Status == PromptStatus.Cancel)
                {
                    ed.WriteMessage("\n" + Tag + " 已停止，后面的没处理");
                    break;
                }

                string k = kr.Status == PromptStatus.OK ? (kr.StringResult ?? "").ToUpperInvariant() : "S";

                if (k == "S" || k.Length == 0)
                {
                    skipped++;
                    continue;
                }

                string pick = PickProfile(ed, names, hit.Suggest);
                if (string.IsNullOrEmpty(pick))
                {
                    skipped++;
                    continue;
                }

                if (k == "R")
                {
                    string newName = pick;

                    // 撞名就加后缀，别把别的状态顶掉
                    if (cfg.Find(pick) != null && !string.Equals(st.Name, pick, StringComparison.OrdinalIgnoreCase))
                        newName = cfg.UniqueName(pick);

                    string old = st.Name;
                    st.Name = newName;
                    st.Profile = pick;

                    ed.WriteMessage("\n   ● 状态名「" + old + "」→「" + newName + "」，绑定「" + pick + "」");
                    renamed++;
                }
                else
                {
                    st.Profile = pick;
                    ed.WriteMessage("\n   ● 状态「" + st.Name + "」绑定「" + pick + "」");
                    bound++;
                }
            }

            VpStore.Save(cfg);

            ed.WriteMessage("\n" + Tag + " 对齐完成：绑定 " + bound + " 个，改名 " + renamed
                + " 个，跳过 " + skipped + " 个"
                + "\n  图纸里已有的标签不会跟着变 —— 想同步就 VLTAG 重标，或对老视口重新刷一次状态");
        }

        /// <summary>命令行表格对齐用：不够长补空格，超长截断。</summary>
        static string Pad(string s, int width)
        {
            s = s ?? "";

            // 中文按两格算，否则命令行里参差不齐
            int w = 0;
            foreach (char c in s) w += c > 127 ? 2 : 1;

            if (w > width) return s;

            return s + new string(' ', width - w);
        }

        /// <summary>让用户从主插件的配置里挑一个；回车 = 保持原值或当前配置。</summary>
        static string PickProfile(Editor ed, List<string> names, string current)
        {
            string def = string.IsNullOrEmpty(current) ? LayerProfile.CurrentProfile : current;
            if (string.IsNullOrEmpty(def) && names.Count > 0) def = names[0];

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 可用的归层配置：");
            for (int i = 0; i < names.Count; i++)
            {
                sb.Append("\n   ").Append(i + 1).Append(". ").Append(names[i]);
                if (string.Equals(names[i], LayerProfile.CurrentProfile, StringComparison.OrdinalIgnoreCase))
                    sb.Append("　（当前）");
            }
            ed.WriteMessage(sb.ToString());

            PromptResult pr = ed.GetString("\n选归层配置（序号或名称，回车 = " + def + "）: ");
            if (pr.Status != PromptStatus.OK) return null;

            string s = (pr.StringResult ?? "").Trim();
            if (s.Length == 0) return def;

            int idx;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)
                && idx >= 1 && idx <= names.Count)
                return names[idx - 1];

            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase)) return names[i];

            ed.WriteMessage("\n" + Tag + " 没有叫「" + s + "」的配置");
            return null;
        }

        // ------------------------------------------------------------------ 面板 / 帮助

        /// <summary>主入口：一个命令打开面板，其余操作都在面板里点。</summary>
        [CommandMethod("VPT")]
        public void OpenMainPanel()
        {
            ShowPanel();
        }

        [CommandMethod("VLPANEL")]
        public void ShowPanel()
        {
            try { Ui.VpPanel.Open(); }
            catch (System.Exception ex)
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage("\n" + Tag + " 面板打不开：" + ex.Message);
            }
        }

        [CommandMethod("VLHELP")]
        public void Help()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 用法（全部在面板里点，不用记命令）");
            sb.Append("\n  VPT 打开面板，按面板上的 1 → 2 → 3 走：");
            sb.Append("\n   1  图框：设定「图框库目录」，把图框 dwg 丢进去，点「重新扫描」");
            sb.Append("\n      一个 dwg = 一个图框（文件名就是图框名），内框自动识别");
            sb.Append("\n      整图只有一个块就用那个块，否则把整张图当一个图框插");
            sb.Append("\n      也可以点「拾取图框」从当前图纸直接点/框选（存进插件图框库）");
            sb.Append("\n      自动识别的内框不准，点「重框内框」手动框一次，以后扫描不覆盖");
            sb.Append("\n   2  视口状态：点「+ 刷取状态」，点一个调好的视口");
            sb.Append("\n      然后点一个文字对象取名，或直接输；同名会问你是否覆盖");
            sb.Append("\n      刷取时会顺手把这个视口绑到当前归层配置 —— 进它自动切过去");
            sb.Append("\n      选中状态点「更新」，点新视口即可原地刷新，名字和序号不变");
            sb.Append("\n      选中状态点「刷到视口」，点目标视口即可单独刷，不用输序号");
            sb.Append("\n   3  批量生成：点「框选并生成」，在模型空间框选图元");
            sb.Append("\n      方式选「每个状态一张图纸」= 布局名用状态名，一图一框");
            sb.Append("\n      方式选「一个布局，一排视口」= 一个布局里横排，图框之间留 50 净距");
            sb.Append("\n      比例填「自动」按内框算，也可填 50 表示 1:50");
            sb.Append("\n");
            sb.Append("\n  命令行也行（面板按钮就是发这些命令）：");
            sb.Append("\n   VPT 面板 | VLFRAME 拾取图框 | VLCAP 刷取 | VLUPDATE 更新");
            sb.Append("\n   VLAPPLY 单刷 | VLBATCH 批量 | VLLIST 列表 | VLDEL 删除 | VLHELP 本说明");
            sb.Append("\n   VLALIGN 状态名与配置名对齐（同名自动绑，接近的逐条确认）");
            sb.Append("\n");
            sb.Append("\n  外来图纸 / 手动绑定归层配置：");
            sb.Append("\n   面板「视口标记」里选配置 → 点「标记视口」→ 图纸里点选或框选多个视口");
            sb.Append("\n   VLTAG   命令行版（回车用当前配置）｜ VLUNTAG 取消（同样可框选）");
            sb.Append("\n   VLTAGS  列出本图所有标记 + 体检");
            sb.Append("\n   标记写在图纸的 Xrecord 里，图纸上没有任何可见痕迹，");
            sb.Append("\n   没装本插件的人打开看不出区别；退出视口会自动恢复原来的配置。");
            sb.Append("\n   没标记的视口也会兜底：冻结层和某个状态完全一致时按那个状态的配置切");
            sb.Append("\n   （面板上「图层兜底」可以关）");
            sb.Append("\n   前提：主插件「Y_归层工具」已加载，否则只记状态、不切配置。");
            sb.Append("\n  说明：VPLAYER 冻结只对布局视口有效，模型空间多视口用不了");
            sb.Append("\n  配置文件：").Append(VpStore.FilePath);
            doc.Editor.WriteMessage(sb.ToString());
        }

        // ------------------------------------------------------------------ 工具

        /// <summary>
        /// 取一个对象的文字内容，用来给状态命名。
        /// 支持单行文字、多行文字、块属性（图框标题栏通常就是块属性）。
        /// </summary>
        static string ReadTextContent(Database db, ObjectId id)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    string s = null;

                    DBText t = o as DBText;
                    if (t != null) s = t.TextString;

                    MText m = o as MText;
                    if (m != null) s = m.Text;

                    AttributeReference ar = o as AttributeReference;
                    if (ar != null) s = ar.TextString;

                    BlockReference br = o as BlockReference;
                    if (br != null)
                    {
                        // 块参照本身没有文字，取它第一个有内容的属性
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            AttributeReference a = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            if (a != null && !string.IsNullOrEmpty(a.TextString)) { s = a.TextString; break; }
                        }
                    }

                    tr.Commit();
                    return CleanText(s);
                }
            }
            catch { return null; }
        }

        /// <summary>清掉多行文字的格式符（\P 换行、\fSimSun|b0|i0|c134|p2; 这类），只留可读文字。</summary>
        static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            s = s.Replace("\\P", " ").Replace("\\p", " ").Replace("\\~", " ");

            // \f...; \H...; \C...; 这类控制序列整段去掉
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[A-Za-z][^;\\{}]*;", "");

            s = s.Replace("{", "").Replace("}", "").Replace("\\", "");
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

            while (s.IndexOf("  ") >= 0) s = s.Replace("  ", " ");

            return s.Trim();
        }

        static bool InModelSpace()
        {
            try
            {
                object v = AcApp.GetSystemVariable("TILEMODE");
                return Convert.ToInt16(v) == 1;
            }
            catch { return true; }
        }

        /// <summary>按序号或名字选一个状态；直接回车 = 第 1 个。</summary>
        static VpState PickState(Editor ed, VpConfig cfg, string prompt)
        {
            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" [").Append(cfg.ActiveGroup.DisplayName).Append("] ")
              .Append(prompt).Append("：");
            for (int i = 0; i < cfg.States.Count; i++)
                sb.Append("\n   ").Append(i + 1).Append(". ").Append(cfg.States[i].Summary);
            ed.WriteMessage(sb.ToString());

            PromptResult pr = ed.GetString("\n输入序号或名称 <1>: ");
            if (pr.Status != PromptStatus.OK) return null;

            string s = (pr.StringResult ?? "").Trim();
            if (s.Length == 0) return cfg.States[0];

            int idx;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)
                && idx >= 1 && idx <= cfg.States.Count)
                return cfg.States[idx - 1];

            VpState st = cfg.Find(s);
            if (st == null) ed.WriteMessage("\n" + Tag + " 找不到「" + s + "」");
            return st;
        }

        static void ReportApply(Editor ed, VpState st, VpService.ApplyResult ar)
        {
            if (!ar.Ok)
            {
                ed.WriteMessage("\n" + Tag + " 应用失败：" + ar.Error);
                return;
            }

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 已套用「").Append(st.Name).Append("」：冻结 ")
              .Append(ar.Frozen).Append(" 层");
            if (ar.Missing > 0)
            {
                int show = Math.Min(ar.MissingNames.Count, 5);
                sb.Append("\n  本图纸缺少 ").Append(ar.Missing).Append(" 个图层，已跳过：")
                  .Append(string.Join("、", ar.MissingNames.GetRange(0, show).ToArray()));
                if (ar.Missing > show) sb.Append(" 等");
            }
            ed.WriteMessage(sb.ToString());
        }

        /// <summary>
        /// 套用状态后，把状态对应的归层配置写进视口标签。
        /// 这是「进视口自动切配置」的数据来源 —— 状态和配置一起走，不用再单独点一次。
        ///
        /// 配置名不靠状态里存的死名字，走 ProfileMatch.Resolve：
        /// 存了就用存的，没存就看状态名是不是正好等于某个配置名。
        /// </summary>
        static void WriteTag(Document doc, ObjectId vpId, VpState st, Editor ed)
        {
            if (st == null) return;

            ProfileMatch.Hit hit = ProfileMatch.Resolve(st.Name, st.Profile);
            string profile = hit.Profile;

            if (string.IsNullOrEmpty(profile))
            {
                if (hit.Kind == ProfileMatch.Kind.Fuzzy)
                    ed.WriteMessage("\n  · 没绑定配置，但名字接近「" + hit.Suggest + "」（VLALIGN 可以对齐）");
                return;
            }

            if (!LayerProfile.Available)
            {
                ed.WriteMessage("\n  · 配置「" + profile + "」没写进图纸：" + LayerProfile.UnavailableReason);
                return;
            }

            if (VpTagStore.Set(doc.Database, vpId, profile))
                ed.WriteMessage("\n  ● 已绑定归层配置「" + profile + "」"
                    + (hit.Kind == ProfileMatch.Kind.SameName ? "（按同名自动绑）" : "")
                    + "，进这个视口会自动切过去");
            else
                ed.WriteMessage("\n  ⚠ 归层配置没写进图纸（图纸可能是只读的）");
        }

        /// <summary>
        /// 刷取即绑定：给刚刷过的视口打上归层配置标签。
        ///
        /// 和 WriteTag 的区别是这里不依赖 VpState —— 刷取时状态可能刚新建、也可能刚被覆盖，
        /// 两条路径都要绑，所以单独抽出来。解析不出配置就只存状态、不打标签。
        /// </summary>
        static void BindViewport(Document doc, ObjectId vpId, string stateName, string profile, Editor ed)
        {
            ProfileMatch.Hit hit = ProfileMatch.Resolve(stateName, profile);
            string use = hit.Profile;

            if (string.IsNullOrEmpty(use))
            {
                if (hit.Kind == ProfileMatch.Kind.Fuzzy)
                    ed.WriteMessage("\n  · 这个视口没绑定归层配置，但状态名接近「" + hit.Suggest
                                  + "」（VLALIGN 可以对齐）");
                return;
            }

            if (!LayerProfile.Available)
            {
                ed.WriteMessage("\n  · 视口没绑定归层配置：" + LayerProfile.UnavailableReason);
                return;
            }

            if (VpTagStore.Set(doc.Database, vpId, use))
                ed.WriteMessage("\n  ● 已把这个视口绑到归层配置「" + use + "」"
                    + (hit.Kind == ProfileMatch.Kind.SameName ? "（按同名自动绑）" : "")
                    + "，进它自动切过去");
            else
                ed.WriteMessage("\n  ⚠ 视口绑定没写进图纸（图纸可能是只读的）");
        }

        /// <summary>支持 "50"、"1:50"、"0.02"；空输入或「自动」= 按内框自适应（返回 0）。</summary>
        static bool TryParseScale(string s, out double scale)
        {
            scale = 0;
            if (string.IsNullOrWhiteSpace(s)) return true;   // 回车 = 自动

            s = s.Trim().Replace("：", ":");

            if (s == "自动" || string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return true;

            if (s.IndexOf(':') >= 0)
            {
                string[] parts = s.Split(':');
                if (parts.Length != 2) return false;

                double a, b;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return false;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return false;
                if (a <= 0 || b <= 0) return false;

                scale = a / b;
                return true;
            }

            double d;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return false;
            if (d <= 0) return false;

            // 大于等于 1 当作分母（50 = 1:50），小于 1 当作系数（0.02 = 1:50）
            scale = d >= 1 ? 1.0 / d : d;
            return true;
        }
    }
}
