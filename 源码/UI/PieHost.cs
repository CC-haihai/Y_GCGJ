using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AutoLayer.Core;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoLayer.UI
{
    /// <summary>
    /// 鼠标键绑定：右键长按 / 侧键后退 / 侧键前进，各自绑一个动作
    /// （圆盘 / 图层切换 / 视口工具 / 主面板 / 无，见 <see cref="MouseBind"/>）。
    ///
    /// 最早只有「右键长按弹圆盘」，后来加了一个"用哪个键弹圆盘"的全局选项。
    /// 2.1 起改成三个键各绑各的 —— 有人不想敲命令，一个侧键就能叫出视口工具。
    ///
    /// 为什么用鼠标钩子：AutoCAD 的 .NET API 完全不给鼠标事件，
    /// 而右键菜单是 CAD 自己在 WM_RBUTTONUP 里弹的 —— 想在它之前插一脚，只能挂钩子。
    ///
    /// 三条铁律（踩过的坑都在这）：
    /// <list type="number">
    /// <item>**绝不吞掉 WM_RBUTTONDOWN**。CAD 的右键菜单在 UP 里弹，吞了 DOWN 会让它
    ///   认为按钮没按下，后面的状态全乱。所以 DOWN 一律原样放行。</item>
    /// <item>只有"动作真的触发了"这一次才吞掉配对的抬起，其余全部放行。
    ///   短按、右键拖动、在面板上右键 —— 一律不介入，原生行为一个字节都不变。</item>
    /// <item>钩子过程里所有代码整段 try/catch，并且无论如何都要 CallNextHookEx。
    ///   异常逃出去会把 CAD 的整个消息循环带崩。</item>
    /// </list>
    ///
    /// 钩子是线程级的（WH_MOUSE + 当前线程），只能挂在 CAD 主线程上，
    /// 所以必须在主线程装 —— Startup 走 Application.Idle，命令里再兜一次。
    /// </summary>
    public static class PieHost
    {
        // ---------------------------------------------------------------- 状态

        enum Mode
        {
            None,     // 什么都没发生
            Armed,    // 右键按下、还在等长按阈值
            Shown     // 圆盘已弹出
        }

        static Native.HookProc _proc;          // 必须留引用，否则会被 GC 掉，钩子变野指针
        static IntPtr _hook = IntPtr.Zero;
        static DispatcherTimer _timer;

        static Mode _mode = Mode.None;
        static bool _manual;                   // 由 ALPIE 命令触发（左键确认、右键取消）
        static int _sideWhich;                 // 正在按着的侧键（0 无 / 1 X1 / 2 X2），抬起要配对吞掉
        static int _sideTick;                  // 侧键按下的时刻 —— 抬起丢了也能自己解开
        static int _holdAct;                   // 这次长按要触发什么（MouseBind）
        static bool _rightFired;               // 右键长按已经触发了动作，抬起要吞掉
        static int _downX, _downY;
        static int _downTick;

        static PieWindow _pie;
        static Document _doc;
        static string _category = "";
        static List<string> _names;
        static List<int> _colors;

        static bool _started;

        // ---------------------------------------------------------------- 诊断
        // 最近几条"按键"消息（移动消息不记）。ALMOUSE 用它回答一个问题：
        // 你按侧键的时候，钩子到底有没有收到东西、收到的是哪个键。
        // 三种坏法表现一模一样（按了没反应），但看这个一眼就能分开：
        //   ① 一条新记录都没有 → 鼠标驱动没发 XBUTTON（很多鼠标侧键被驱动映射成"后退/前进"应用命令）
        //   ② 有记录但是 XBUTTON2，而你配的是"后退" → 两个键反了，换个选项就行
        //   ③ 有记录、键也对 → 那是 GateOn() 挡住了（总开关停用 / 命令进行中 / 光标不在画图区）
        const int MouseLogCap = 8;
        static readonly string[] _mouseLog = new string[MouseLogCap];
        static int _mouseLogCount;
        static int _mouseLogWrite;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>AutoCAD 起来后调一次。首次空闲时才真正建窗口，避免在 Initialize 里碰 WPF。</summary>
        public static void Startup()
        {
            if (_started) return;
            _started = true;

            try
            {
                AcApp.Idle += OnFirstIdle;
            }
            catch { }
        }

        static void OnFirstIdle(object sender, EventArgs e)
        {
            try { AcApp.Idle -= OnFirstIdle; } catch { }
            Begin();
        }

        static void Begin()
        {
            // 三个各管各的：任何一个起不来都不该把另外两个带下水。
            // （以前是一整个 try 包着，状态条万一抛异常，快捷键就静默失效了。）
            try { StatusHud.Start(); } catch { }
            try { EnsureHook(); } catch { }
            // 面板快捷键和圆盘钩子是同一套思路（都是钩子），一起起来
            try { HotkeyHost.Start(); } catch { }
        }

        public static void Shutdown()
        {
            try { AcApp.Idle -= OnFirstIdle; } catch { }
            try { HotkeyHost.Stop(); } catch { }

            StopTimer();
            if (_hook != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            _proc = null;

            try { if (_pie != null) { _pie.Close(); _pie = null; } } catch { }
            try { StatusHud.Stop(); } catch { }

            _mode = Mode.None;
            _started = false;
        }

        // ---------------------------------------------------------------- 钩子

        static void EnsureHook()
        {
            if (_hook != IntPtr.Zero) return;

            try
            {
                _proc = OnMouse;
                _hook = Native.SetWindowsHookEx(
                    Native.WH_MOUSE, _proc, Native.GetModuleHandle(null), Native.GetCurrentThreadId());
            }
            catch
            {
                _hook = IntPtr.Zero;
            }
        }

        static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            bool swallow = false;

            if (nCode >= 0)
            {
                try { swallow = Handle((int)wParam.ToInt64(), wParam, lParam); }
                catch { swallow = false; }
            }

            if (swallow) return new IntPtr(1);
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>返回 true 表示要吞掉这条消息。</summary>
        static bool Handle(int msg, IntPtr wParam, IntPtr lParam)
        {
            // 移动消息一秒几十上百条，先分流掉 —— 别每条都去读一遍配置
            if (msg == Native.WM_MOUSEMOVE)
            {
                if (_mode == Mode.Shown) Track();
                else if (_mode == Mode.Armed)
                {
                    int x, y;
                    if (Cursor(out x, out y))
                    {
                        int dx = x - _downX, dy = y - _downY;
                        // 按住拖动超过 10px：这是在右键拖，别抢
                        if (dx * dx + dy * dy > 100) { _mode = Mode.None; StopTimer(); }
                    }
                }
                return false;
            }

            LogMouse(msg, lParam);      // 诊断用，只记按键，几字节开销

            // 侧键两个键各自绑动作，必须按"是哪个侧键"分流；
            // 右键长按绑一个动作。绑「无」的键一律原样放行，一点原生行为都不改。
            if (msg == Native.WM_XBUTTONDOWN || msg == Native.WM_XBUTTONUP)
                return HandleSide(msg, lParam);

            return HandleRight(msg, wParam);
        }

        // ---------------------------------------------------------------- 诊断：鼠标消息

        /// <summary>
        /// 取侧键编号（1 = XBUTTON1 后退，2 = XBUTTON2 前进）。
        ///
        /// **不能从 wParam 取。** 窗口消息里的 WM_XBUTTONDOWN 是 wParam 高字放 XBUTTON 号，
        /// 但在 **WH_MOUSE 钩子里 wParam 就是消息号本身**（0x020B），高字恒为 0。
        /// 之前照窗口消息的写法取 wParam 高字，结果永远是 0、永远匹配不上，
        /// 侧键从做出来那天起就不可能触发。
        ///
        /// 钩子里正确的来源是 lParam 指向的 MOUSEHOOKSTRUCTEX.mouseData（高字）。
        /// 结构：POINT(8) + HWND(IntPtr) + UINT wHitTestCode(4) + 对齐 + ULONG_PTR dwExtraInfo + DWORD mouseData
        ///   x64：8 + 8 + 4 → 补 4 → 24 + 8 = 32，mouseData 在 32
        ///   x86：8 + 4 + 4 → 16 + 4 = 20，mouseData 在 20
        /// </summary>
        static int XButtonOf(IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return 0;
            try
            {
                int off = IntPtr.Size == 8 ? 32 : 20;
                int data = Marshal.ReadInt32(lParam, off);
                return (data >> 16) & 0xFFFF;
            }
            catch { return 0; }
        }

        static string MouseName(int msg, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN: return "左键 按下";
                case Native.WM_LBUTTONUP: return "左键 抬起";
                case Native.WM_RBUTTONDOWN: return "右键 按下";
                case Native.WM_RBUTTONUP: return "右键 抬起";
                case Native.WM_RBUTTONDBLCLK: return "右键 双击";
                case Native.WM_MBUTTONDOWN: return "中键 按下";
                case Native.WM_MBUTTONUP: return "中键 抬起";
                case Native.WM_XBUTTONDOWN: return "侧键 按下 XBUTTON" + XButtonOf(lParam);
                case Native.WM_XBUTTONUP: return "侧键 抬起 XBUTTON" + XButtonOf(lParam);
                default: return null;       // 滚轮之类的不记，吵
            }
        }

        static void LogMouse(int msg, IntPtr lParam)
        {
            string s = MouseName(msg, lParam);
            if (s == null) return;

            _mouseLog[_mouseLogWrite] = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
            _mouseLogWrite = (_mouseLogWrite + 1) % MouseLogCap;
            if (_mouseLogCount < MouseLogCap) _mouseLogCount++;
        }

        /// <summary>
        /// 侧键（鼠标后退 / 前进）：按下即触发，松手确认。
        ///
        /// 为什么侧键不用长按：侧键在 CAD 里没有原生用途，按下时不会像中键那样
        /// 立刻开始平移，所以可以"按下即弹"，手感比右键长按更快。
        /// （2026-11-14 用户明确：侧键不用长按，只有右键有。所以这里不读任何时长配置。）
        ///
        /// 吞 DOWN 就必须吞配对的 UP —— 只吞一半会让系统以为这个键还按着。
        ///
        /// 两个侧键<b>各自独立绑动作</b>（可以一个弹圆盘、一个开视口工具），
        /// 所以吞不吞要看"这个键上有没有绑东西"，不能像以前那样只认一个全局键。
        ///
        /// **侧键不检查"光标在不在画图区"**：那个检查是给右键用的 ——
        /// 右键在面板/命令行上有原生用途（右键菜单），不能乱抢；
        /// 侧键在 CAD 里按在哪都什么都不做，限死画图区只会多出一个"按了没反应"的理由。
        /// </summary>
        static bool HandleSide(int msg, IntPtr lParam)
        {
            int which = XButtonOf(lParam);
            if (which != 1 && which != 2) return false;

            int act = ActionOf(which);
            if (act == MouseBind.None) return false;      // 这个键没绑东西：完全原生

            if (msg == Native.WM_XBUTTONDOWN)
            {
                // 按住不放时系统会重发，已经在处理了。
                // 但抬起消息万一丢了（换了前台窗口、钩子被摘过），_sideWhich 会永远卡住，
                // 之后每一下都只吞不开 —— 表现就是"侧键彻底失灵"。所以超时自己解开。
                if (_sideWhich != 0)
                {
                    if (Environment.TickCount - _sideTick < 3000) return true;
                    _sideWhich = 0;
                }

                bool taken = false;

                if (_mode == Mode.Shown) { Cancel(); taken = true; }
                else if (_manual) { Cancel(); taken = true; }
                else if (GateOn())
                {
                    int x, y;
                    if (Cursor(out x, out y))
                    {
                        if (act == MouseBind.Pie)
                        {
                            if (OpenAt(x, y, false)) _mode = Mode.Shown;
                        }
                        else FireAction(act);

                        // 圆盘没弹成（比如这一类只配了一个图层）也照样吞掉：
                        // 侧键在 CAD 里本来什么都不做，放过去反而会让"按下没反应"更像失灵。
                        taken = true;
                    }
                }

                _sideWhich = taken ? which : 0;
                _sideTick = Environment.TickCount;
                return taken;
            }

            if (msg == Native.WM_XBUTTONUP)
            {
                if (_sideWhich == 0 || which != _sideWhich) return false;
                _sideWhich = 0;
                if (_mode == Mode.Shown && !_manual) Commit();
                return true;
            }

            return false;
        }

        static bool HandleRight(int msg, IntPtr wParam)
        {
            switch (msg)
            {
                // ---- 右键按下：只观察，不拦截
                case Native.WM_RBUTTONDOWN:
                    if (_mode == Mode.Shown) { Cancel(); return true; }
                    if (_manual) { Cancel(); return false; }

                    // 右键绑「无」= 右键彻底原生，连计时都不开
                    _holdAct = ActionOf(0);
                    if (_holdAct == MouseBind.None) return false;

                    if (!GateOn()) return false;
                    if (!Cursor(out _downX, out _downY)) return false;
                    if (!CadWindow.InDrawingArea(_downX, _downY)) return false;

                    _rightFired = false;
                    _mode = Mode.Armed;
                    _downTick = Environment.TickCount;
                    StartTimer();
                    return false;

                // ---- 左键按下：手动模式下确认选择
                // （移动消息在 Handle 里就分流掉了，走不到这儿 —— 那里一秒几十条，
                //   不能每条都去读一次配置判断用哪个键）
                case Native.WM_LBUTTONDOWN:
                    if (_mode == Mode.Shown && _manual) { Commit(); return true; }
                    return false;

                // ---- 右键抬起
                case Native.WM_RBUTTONUP:
                    if (_mode == Mode.Shown && !_manual)
                    {
                        Commit();
                        StopTimer();
                        // 只吞这一条，把 CAD 的右键菜单挡掉；DOWN 已经放行，状态不会错
                        return true;
                    }
                    // 长按已经触发了非圆盘动作（切层 / 视口 / 面板）：这一下抬起也得吞，
                    // 否则动作弹出的同时 CAD 还会再弹一次右键菜单，盖在面板上面。
                    if (_rightFired)
                    {
                        _rightFired = false;
                        StopTimer();
                        return true;
                    }
                    if (_mode == Mode.Armed) { _mode = Mode.None; StopTimer(); }
                    return false;

                case Native.WM_RBUTTONDBLCLK:
                    if (_mode == Mode.Shown) return true;
                    return false;

                case Native.WM_KEYDOWN:
                case Native.WM_SYSKEYDOWN:
                    // 键盘消息 wParam 低字就是虚拟键码
                    if (_mode == Mode.Shown && ((int)(long)wParam & 0xFFFF) == Native.VK_ESCAPE)
                    {
                        Cancel();
                        return true;
                    }
                    return false;
            }

            return false;
        }

        // ---------------------------------------------------------------- 计时

        static void StartTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                _timer.Tick += OnTick;
            }
            _timer.Start();
        }

        static void StopTimer()
        {
            if (_timer != null) _timer.Stop();
        }

        static void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (_mode != Mode.Armed) { StopTimer(); return; }

                if (Environment.TickCount - _downTick < PieHoldMs()) return;

                StopTimer();

                int x, y;
                if (!Cursor(out x, out y)) { _mode = Mode.None; return; }

                // 右键被拖走了（拉菜单 / 框选）就别弹
                int dx = x - _downX, dy = y - _downY;
                if (dx * dx + dy * dy > 400) { _mode = Mode.None; return; }

                if (_holdAct == MouseBind.Pie)
                {
                    if (OpenAt(_downX, _downY, false)) _mode = Mode.Shown;
                    else _mode = Mode.None;
                }
                else
                {
                    // 切层 / 视口 / 面板：长按到点就开，不等松手。
                    // 标记一下，让配对的抬起被吞掉（见 WM_RBUTTONUP 分支）。
                    FireAction(_holdAct);
                    _rightFired = true;
                    _mode = Mode.None;
                }
            }
            catch
            {
                _mode = Mode.None;
                StopTimer();
            }
        }

        // ---------------------------------------------------------------- 打开 / 关闭

        /// <summary>手动弹一次（ALPIE 命令）。返回 true 表示圆盘已经出来。</summary>
        public static bool ShowNow()
        {
            EnsureHook();
            EnsurePie();

            int x, y;
            if (!Cursor(out x, out y)) return false;

            if (OpenAt(x, y, true))
            {
                _mode = Mode.Shown;
                return true;
            }
            return false;
        }

        static void EnsurePie()
        {
            if (_pie != null) return;
            _pie = new PieWindow();
            _pie.Closed += (s, e) => { _pie = null; };
            CadWindow.AttachToCad(_pie);
        }

        static bool OpenAt(int x, int y, bool manual)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return false;

            string cat;
            List<string> names;
            List<int> colors;
            if (!BuildItems(doc, out cat, out names, out colors)) return false;

            EnsurePie();
            if (_pie == null) return false;

            try
            {
                _pie.Build(names, colors);
                // 顺序不能乱：先摆位 + 抓底（都在显示之前，此时不会抓到圆盘自己），
                // 再 Show，最后用 PointFromScreen 精确校正 —— PointFromScreen 在 Show
                // 之前必定抛异常，老版本就是栽在这里，异常被 catch 吞掉，圆盘一声不响地不出现。
                _pie.PrepareAt(x, y);
                if (!_pie.IsVisible) _pie.Show();
                _pie.CenterAtScreen(x, y);
                _pie.Hover(-1);
            }
            catch (Exception ex)
            {
                // 别静默失败：ALTRACE 里能看到到底哪一步炸的
                Trace.Add("圆盘弹出失败: " + ex.GetType().Name + " " + ex.Message);
                try { StatusHud.Flash("圆盘弹出失败，ALTRACE 看原因"); } catch { }
                return false;
            }

            _doc = doc;
            _category = cat;
            _names = names;
            _colors = colors;
            _manual = manual;

            // 明确回一声"圆盘开出来了，装的是哪一类"。
            // 圆盘是逐像素透明、不接收鼠标的浮窗，弹出来时如果光标偏了（或者被屏幕边缘切了），
            // 光看圆盘本身根本分不清是"没弹"还是"弹在别处"。状态条闪一下就没这个歧义。
            try { StatusHud.Flash("圆盘 · " + cat + "  " + names.Count + " 层"); } catch { }
            return true;
        }

        /// <summary>
        /// 决定圆盘里装哪些图层：优先"最近一次归层用的那个类别"（刚画完线，长按就该出线那一池）；
        /// 它不足两个就挑池子最大的类别。都没有就不弹 —— 只有一层弹圆盘纯属添乱。
        /// </summary>
        static bool BuildItems(Document doc, out string category, out List<string> names, out List<int> colors)
        {
            category = "";
            names = null;
            colors = null;

            Config cfg = ConfigStore.Current;
            if (cfg == null || !cfg.Enabled) return false;
            Profile p = cfg.Active;
            if (p == null) return false;

            string cat = CommandHook.LastCategoryOf(doc);
            if (string.IsNullOrEmpty(cat) || p.LayerCount(cat) < 2)
            {
                cat = null;
                int best = 1;
                foreach (string c in Categories.All)
                {
                    int n = p.LayerCount(c);
                    if (n > best) { best = n; cat = c; }
                }
                if (cat == null) return false;
            }

            var pool = p.LayersFor(cat);
            if (pool.Count < 2) return false;

            names = new List<string>();
            colors = new List<int>();
            // 最多 16 格。配得更多就只取前 16 个 —— 常用的本来就会写在前面，
            // 总比"干脆不弹"强。
            for (int i = 0; i < pool.Count && names.Count < 16; i++)
            {
                LayerRef l = pool[i];
                if (l == null) continue;
                names.Add(l.Name);
                colors.Add(l.ColorValue);
            }
            if (names.Count < 2) return false;

            category = cat;
            return true;
        }

        static void Track()
        {
            if (_pie == null || !_pie.IsVisible) return;
            int x, y;
            if (!Cursor(out x, out y)) return;

            System.Windows.Point local = _pie.LocalFromScreen(x, y);
            _pie.Hover(_pie.HitTestLocal(local));
        }

        static void Commit()
        {
            if (_pie != null && _pie.IsVisible)
            {
                int x, y;
                if (Cursor(out x, out y))
                {
                    System.Windows.Point local = _pie.LocalFromScreen(x, y);
                    _pie.Hover(_pie.HitTestLocal(local));
                }
            }

            int idx = _pie == null ? -1 : _pie.Hovered;
            string cat = _category;
            List<string> names = _names;
            Document doc = _doc;

            HidePie();

            if (idx < 0 || names == null || idx >= names.Count)
            {
                StatusHud.Flash("已取消");
                return;
            }

            string picked = names[idx];
            // 按**名字**切，不按下标：池超过 16 个时圆盘只取前 16 个，
            // 下标和真实池对不上，名字永远不会错。
            bool changed = CommandHook.SwitchToLayer(doc, cat, picked);

            Trace.Add("圆盘 " + cat + " → " + picked);
            // 状态条只闪图层名：胶囊宽度有限，"线段 → E-05立面造型内分割线" 会被截成
            // "线段 → E-05立面造…"，最该看清的后半段反而没了。类别在命令行那行里。
            StatusHud.Flash(picked);

            try
            {
                Document d = doc ?? AcApp.DocumentManager.MdiActiveDocument;
                if (d != null)
                    d.Editor.WriteMessage("\n[Y_归层工具] " + cat + " → " + picked
                        + (changed ? "（下一条命令生效）" : "（本来就是这一层）"));
            }
            catch { }
        }

        static void Cancel()
        {
            HidePie();
            StatusHud.Flash("已取消");
        }

        static void HidePie()
        {
            _mode = Mode.None;
            _manual = false;
            _sideWhich = 0;
            _rightFired = false;
            StopTimer();
            try { if (_pie != null && _pie.IsVisible) _pie.Hide(); } catch { }
        }

        // ---------------------------------------------------------------- 杂项

        static bool Cursor(out int x, out int y)
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) { x = 0; y = 0; return false; }
            x = p.X;
            y = p.Y;
            return true;
        }

        /// <summary>
        /// 现在能不能介入鼠标。两条：插件总开关开着、命令行是干净的。
        ///
        /// 不再看 PieEnabled —— 圆盘开不开由「哪个键绑了圆盘」决定，
        /// 多一个独立开关只会出现"键绑着圆盘、开关却关着、按了没反应"的鬼状态。
        /// </summary>
        static bool GateOn()
        {
            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null || !cfg.Enabled) return false;

                // 命令正在跑的时候绝对不介入：那会儿右键菜单里是「取消 / 闭合 / 端点」这类
                // 命令选项，抢了就等于把人卡在命令里出不来。CMDACTIVE = 0 才是干净的命令行状态。
                object v = AcApp.GetSystemVariable("CMDACTIVE");
                int active = 0;
                if (v != null) int.TryParse(v.ToString(), out active);
                return active == 0;
            }
            catch { return false; }
        }

        static int PieHoldMs()
        {
            try
            {
                Config cfg = ConfigStore.Current;
                int ms = cfg == null || cfg.Settings == null ? 180 : cfg.Settings.PieHoldMs;
                if (ms < 100) ms = 100;
                if (ms > 800) ms = 800;
                return ms;
            }
            catch { return 180; }
        }

        /// <summary>哪个鼠标键绑了什么：0 = 右键长按，1 = 侧键后退，2 = 侧键前进。</summary>
        static int ActionOf(int which)
        {
            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null || cfg.Settings == null) return MouseBind.None;

                Settings st = cfg.Settings;
                int a = which == 1 ? st.MouseX1 : which == 2 ? st.MouseX2 : st.MouseRight;
                return MouseBind.Valid(a) ? a : MouseBind.None;
            }
            catch { return MouseBind.None; }
        }

        /// <summary>
        /// 执行一个非圆盘动作。**必须异步**：这里在鼠标钩子回调里，
        /// 建面板 / 读 ini 动辄几十毫秒，同步做完会把整个输入链卡住，
        /// 表现就是"按了侧键，CAD 卡一下，鼠标也不跟手"。
        /// </summary>
        static void FireAction(int act)
        {
            try
            {
                Dispatcher d = Dispatcher.CurrentDispatcher;   // 钩子挂在 CAD 主线程上
                d.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                {
                    try
                    {
                        switch (act)
                        {
                            case MouseBind.QuickSwitch: PanelHost.ShowQuickSwitch(); break;
                            case MouseBind.Viewport: VpLayer.Ui.VpPanel.Open(); break;
                            case MouseBind.Panel: PanelHost.Show(); break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.Add("鼠标绑定动作失败: " + ex.GetType().Name + " " + ex.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                Trace.Add("鼠标绑定派发失败: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 诊断输出

        /// <summary>ALMOUSE 用：鼠标绑定这条路，钩子到底看到了什么。</summary>
        public static string DescribeMouse()
        {
            var sb = new System.Text.StringBuilder();

            int r = ActionOf(0), x1 = ActionOf(1), x2 = ActionOf(2);

            sb.Append("\n  鼠标绑定：");
            sb.Append("\n    右键长按  → ").Append(MouseBind.Full(r)).Append("　（按住 ").Append(PieHoldMs()).Append(" ms）");
            sb.Append("\n    侧键后退  → ").Append(MouseBind.Full(x1)).Append("　（按下即触发）");
            sb.Append("\n    侧键前进  → ").Append(MouseBind.Full(x2)).Append("　（按下即触发）");

            if (r == MouseBind.None && x1 == MouseBind.None && x2 == MouseBind.None)
                sb.Append("\n    **三个键都没绑**：鼠标在 CAD 里完全是原生的，钩子不会拦任何东西");

            try
            {
                Config cfg = ConfigStore.Current;
                bool master = cfg != null && cfg.Enabled;
                sb.Append("\n  总开关：").Append(master ? "启用" : "**停用**");

                object v = AcApp.GetSystemVariable("CMDACTIVE");
                int active = 0;
                if (v != null) int.TryParse(v.ToString(), out active);
                sb.Append("　CMDACTIVE：").Append(active)
                  .Append(active == 0 ? "（干净，可以接管）" : "（命令进行中，鼠标绑定不介入）");
            }
            catch { }

            sb.Append("\n  鼠标钩子：").Append(_hook != IntPtr.Zero ? "已装上" : "**没装上**（侧键和右键都不会有反应）");
            sb.Append("　状态：").Append(_mode == Mode.Shown ? "圆盘开着" : _mode == Mode.Armed ? "右键已按下，在等长按" : "空闲");

            sb.Append("\n  最近收到的按键（按一下侧键再看这条）：");
            if (_mouseLogCount == 0)
            {
                sb.Append("\n    （一条都没有）");
            }
            else
            {
                // 从最旧的开始打
                int start = (_mouseLogWrite - _mouseLogCount + MouseLogCap) % MouseLogCap;
                for (int i = 0; i < _mouseLogCount; i++)
                {
                    string line = _mouseLog[(start + i) % MouseLogCap];
                    if (line != null) sb.Append("\n    ").Append(line);
                }
            }

            if (x1 != MouseBind.None || x2 != MouseBind.None)
            {
                sb.Append("\n  怎么看：按侧键后这里**没有新行** → 鼠标驱动没发 XBUTTON 消息，");
                sb.Append("\n    去鼠标驱动里把侧键设成「后退 / 前进（默认）」而不是自定义宏；");
                sb.Append("\n    有新行但 XBUTTON 号对不上 → 两个键反了，设置里换一列。");
            }
            return sb.ToString();
        }
    }
}
