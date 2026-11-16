using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace VpLayer
{
    /// <summary>
    /// 盯住「当前在哪个视口里」，进出视口时自动切主插件的归层配置。
    ///
    /// 触发方式：200ms 轮询 CVPORT。
    ///   为什么不用 SystemVariableChanged 事件：20.1 里这个事件对哪些系统变量
    ///   真的会发，官方没有明确列表，CVPORT 实测不可靠。轮询只比对两个整数
    ///   （文档引用 + CVPORT），开销可以忽略；只有值真的变了才开事务读图纸。
    ///
    /// 切换语义（用户敲定）：
    ///   进带标签的视口 → 切到标签写的配置；
    ///   退回图纸空间 / 切到模型空间 / 换图纸 → 恢复进入前的配置。
    ///   只改内存，不写 AutoLayer.ini —— 不能因为进了个视口就把默认配置改掉。
    /// </summary>
    public static class VpProfileWatcher
    {
        /// <summary>轮询间隔。200ms 是"人感觉不到延迟"和"不打扰 CAD"之间的折中。</summary>
        const int IntervalMs = 200;

        static DispatcherTimer _timer;
        static Document _doc;
        static int _cvport = -1;

        /// <summary>进入视口前的配置名，离开时恢复它。null 表示当前没有待恢复的切换。</summary>
        static string _restore;

        static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前视口绑的配置名；不在视口里或没标签时为 null。</summary>
        public static string CurrentTagProfile { get; private set; }

        /// <summary>当前视口实体；不在视口里时为 ObjectId.Null。</summary>
        public static ObjectId CurrentViewportId { get; private set; }

        /// <summary>当前视口所在布局名；不在布局里时为空。</summary>
        public static string CurrentLayoutName { get; private set; }

        /// <summary>状态变了（进了视口 / 出了视口 / 标签变了）→ 面板自己刷新。</summary>
        public static event Action Changed;

        // ------------------------------------------------------------------ 生命周期

        public static void Start()
        {
            if (_timer != null || _idleHooked) return;

            try
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(IntervalMs)
                };
                _timer.Tick += OnTick;
                _timer.Start();
                return;
            }
            catch { _timer = null; }

            // 兜底：极少数环境里拿不到 WPF 调度器，就挂 Idle 自己节流。
            // 没有这条，定时器建不起来就等于功能静默失效，最难排查。
            try
            {
                AcApp.Idle += OnIdleTick;
                _idleHooked = true;
            }
            catch { }
        }

        public static void Stop()
        {
            try
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTick;
                    _timer = null;
                }
            }
            catch { }

            if (_idleHooked)
            {
                try { AcApp.Idle -= OnIdleTick; } catch { }
                _idleHooked = false;
            }

            _doc = null;
            _cvport = -1;
            _restore = null;
            _suspended = false;
        }

        static bool _idleHooked;
        static int _lastMs;

        // 命令正在跑时的挂起窗口。
        //
        // 原来命令运行期间每一拍都要先读 CVPORT、再读 CMDNAMES（两次 COM 调用），
        // 读完整套发现"命令还在跑"就 return，紧接着下一拍又来一遍 —— 画图时
        // 5 次/秒的空转。改成记一个挂起截止时刻，窗口内的拍子连 COM 都不碰。
        const int SuspendMs = 400;
        static bool _suspended;
        static int _suspendAt;

        static void OnIdleTick(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastMs) < IntervalMs) return;
            _lastMs = now;
            OnTick(null, EventArgs.Empty);
        }

        /// <summary>强制重新判定一次（改完标签、切完图纸时调）。</summary>
        public static void Refresh()
        {
            _cvport = -1;
            OnTick(null, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ 轮询

        static void OnTick(object sender, EventArgs e)
        {
            try
            {
                int now = Environment.TickCount;

                // 刚判定过"命令还在跑"：这段时间内什么都不查，连系统变量都不读
                if (_suspended)
                {
                    if (unchecked(now - _suspendAt) < SuspendMs) return;
                    _suspended = false;
                }

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                int cv;
                try { cv = Convert.ToInt16(AcApp.GetSystemVariable("CVPORT")); }
                catch { return; }

                bool sameDoc = ReferenceEquals(doc, _doc);
                if (sameDoc && cv == _cvport) return;

                _doc = doc;
                _cvport = cv;

                // 换图纸：上一张图纸的"进入前配置"没意义了
                if (!sameDoc) _restore = null;

                // 命令还在跑的时候不动配置，免得把用户正在做的事打断。
                // 把 _cvport 复位，命令结束后下一拍还会重新判定，不会漏掉这次进出。
                if (CommandRunning(doc))
                {
                    _cvport = -1;
                    _suspended = true;
                    _suspendAt = now;
                    return;
                }

                Resolve(doc);
            }
            catch { }
        }

        static bool CommandRunning(Document doc)
        {
            try
            {
                string names = AcApp.GetSystemVariable("CMDNAMES") as string;
                return !string.IsNullOrEmpty(names);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ 判定

        static void Resolve(Document doc)
        {
            ObjectId vpId = ObjectId.Null;
            string profile = null;
            string layout = "";

            try
            {
                bool model = Convert.ToInt16(AcApp.GetSystemVariable("TILEMODE")) == 1;

                if (!model)
                {
                    try { layout = LayoutManager.Current.CurrentLayout; } catch { }

                    vpId = doc.Editor.CurrentViewportObjectId;

                    if (!vpId.IsNull && !vpId.IsErased)
                    {
                        bool paper;
                        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                        {
                            Viewport vp = tr.GetObject(vpId, OpenMode.ForRead) as Viewport;
                            // Number = 1 是图纸空间本身（主视口），它没有 VPLAYER 冻结
                            paper = vp == null || vp.Number <= 1;
                            tr.Commit();
                        }

                        if (paper) vpId = ObjectId.Null;
                        else
                        {
                            profile = VpTagStore.GetProfile(doc.Database, vpId);

                            // 兜底：这个视口没有标签，但它的冻结层正好和某个状态一模一样
                            // → 用那个状态的配置。老视口没刷过标签也能自动切。
                            // 只在「没标签」时才走这条路，开销是一次冻结层读取 + 集合比对。
                            if (profile == null)
                                profile = MatchByFrozen(doc, doc.Database, vpId);
                        }
                    }
                }
            }
            catch { }

            CurrentViewportId = vpId;
            CurrentTagProfile = profile;
            CurrentLayoutName = layout;

            if (profile == null) Restore(doc);
            else Enter(doc, profile);

            RaiseChanged();
        }

        /// <summary>
        /// 没有标签的视口：拿它冻结的图层去状态列表里找完全一致的那个状态，
        /// 套用那个状态的配置。开关在配置文件的「图层兜底」，面板上也能关。
        ///
        /// 只在冻结层集合一模一样时才认（数量相同 + 每个名字都对得上）——
        /// 差一层就说明是另一套东西，宁可不切。
        /// </summary>
        static string MatchByFrozen(Document doc, Database db, ObjectId vpId)
        {
            try
            {
                VpConfig cfg = VpStore.Current;
                if (cfg == null || !cfg.LayerFallback || cfg.States.Count == 0) return null;

                string err;
                List<string> frozen = VpService.ReadFrozenNames(db, vpId, out err);
                if (err != null || frozen == null) return null;

                for (int i = 0; i < cfg.States.Count; i++)
                {
                    VpState st = cfg.States[i];
                    if (st == null) continue;
                    if (!SameLayers(st.FrozenLayers, frozen)) continue;

                    string p = ProfileMatch.Resolve(st.Name, st.Profile).Profile;
                    if (string.IsNullOrEmpty(p)) continue;

                    Say(doc, "○ 视口没标记，按冻结层匹配到状态「" + st.Name + "」");
                    return p;
                }
            }
            catch { }

            return null;
        }

        static bool SameLayers(List<string> a, List<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;

            var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < b.Count; i++)
                if (!set.Contains(b[i])) return false;

            return true;
        }

        static void Enter(Document doc, string profile)
        {
            if (!LayerProfile.Available)
            {
                WarnOnce("noplugin", "● 视口已标记「" + profile + "」，但" + LayerProfile.UnavailableReason);
                return;
            }

            if (!LayerProfile.HasProfile(profile))
            {
                WarnOnce("missing:" + profile,
                    "● 视口标记的配置「" + profile + "」在主插件里不存在，已跳过（检查是不是改名或删了）");
                return;
            }

            string cur = LayerProfile.CurrentProfile;

            // 「进入视口前的配置」只记一次：从视口 A 直接点到视口 B 时，
            // 要恢复的是进 A 之前那个，不是 A 自己。
            if (_restore == null) _restore = cur;

            // 已经是这个配置，不用切（但 _restore 已经记下了，退出时照样恢复）
            if (string.Equals(cur, profile, StringComparison.OrdinalIgnoreCase)) return;

            if (LayerProfile.SetProfile(profile))
            {
                string extra = LayerProfile.IsEnabled ? "" : "（注意：归层工具总开关是关的）";
                Say(doc, "● 已切到归层配置「" + profile + "」" + extra);
            }
            else
            {
                Say(doc, "● 切配置失败：「" + profile + "」（主插件没响应，可能版本不匹配）");
            }
        }

        static void Restore(Document doc)
        {
            if (_restore == null) return;

            string back = _restore;
            _restore = null;

            if (string.IsNullOrEmpty(back) || !LayerProfile.Available) return;
            if (!LayerProfile.HasProfile(back)) return;

            // 没真的切过（进视口时本来就是这套配置）就别在命令行刷一句废话
            if (string.Equals(back, LayerProfile.CurrentProfile, StringComparison.OrdinalIgnoreCase)) return;

            if (LayerProfile.SetProfile(back))
                Say(doc, "○ 已恢复归层配置「" + back + "」");
        }

        // ------------------------------------------------------------------ 提示

        static void Say(Document doc, string text)
        {
            try { doc.Editor.WriteMessage("\n" + text); } catch { }
        }

        static void WarnOnce(string key, string text)
        {
            if (!Warned.Add(key)) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null) Say(doc, text);
        }

        static void RaiseChanged()
        {
            Action h = Changed;
            if (h == null) return;
            try { h(); } catch { }
        }
    }
}
