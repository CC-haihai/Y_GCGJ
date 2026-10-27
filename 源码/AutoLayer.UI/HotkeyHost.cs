using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using AutoLayer.Core;
using AcGuiApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoLayer.UI
{
    /// <summary>
    /// 面板快捷键（默认 Ctrl+Shift+Q）。
    ///
    /// 为什么要挂钩子：AutoCAD 的命令名是编译期用 [CommandMethod] 写死的，
    /// 运行时改不了 —— 想让它"用户自己挑一个键"，只能截键盘。
    ///
    /// **用全局低层钩子 WH_KEYBOARD_LL，不是线程级 WH_KEYBOARD。**
    /// 一开始写的是线程级（跟圆盘的 WH_MOUSE 同一个套路），用户在 CAD 里按了完全没反应。
    /// 用 cadkit/keytest 最小复现出来：线程级钩子 SetWindowsHookEx 返回成功、
    /// 按键也确实进了消息队列（窗口自己收到了 KeyDown），但回调一次都没被调用；
    /// 换成 WH_KEYBOARD_LL 立刻就收到了。所以这版改成全局低层钩子。
    ///
    /// **钩子挂在自己的后台线程上，不挂 CAD 主线程。**
    /// 低层钩子的回调是由"装钩子的那个线程"取消息时执行的。挂在 CAD 主线程上的话，
    /// 一旦 CAD 忙起来（大图 REGEN、批量操作）几秒钟不取消息，Windows 就会认为钩子超时，
    /// **悄悄把钩子摘掉**，表现就是"用着用着快捷键突然不灵了，重启 CAD 才好"。
    /// 所以这里单开一个带消息泵的 STA 线程专门装钩子，它永远闲着，回调永远及时。
    ///
    /// 因为钩子是全局的，回调里**必须**判断"前台是不是 CAD 自己"（比进程 id），
    /// 否则在 Word 里按同一个组合也会弹 CAD 面板。这条不是可选检查。
    ///
    /// 又因为回调跑在别的线程上，回调里**一句 CAD API 都不能碰**
    /// （GetSystemVariable、MainWindow 这些都只能从 CAD 线程调）。
    /// 需要的两个值（本进程 id、CAD 主窗口句柄）在 Start() 时就取好存下来。
    ///
    /// 三条纪律：
    /// <list type="number">
    /// <item>组合键必须带 Ctrl / Alt / Win 之一。只有 Shift 的组合会跟正常打字抢键，
    ///   所以就算 ini 里手改成那样，这里也不认（宁可没反应，不可抢输入）。</item>
    /// <item>只有"键对上了、修饰键也全对上、前台是 CAD"才吞。其它情况一律 CallNextHookEx 原样放行。</item>
    /// <item>吞 DOWN 就必须吞配对的 UP —— 只吞一半会让 CAD 以为这个键还按着。</item>
    /// </list>
    ///
    /// 开面板走 CAD 线程的 Dispatcher.BeginInvoke 延后一拍：
    /// 钩子回调里直接建 WPF 窗口会把输入链卡在钩子里，严重时会死锁。
    /// </summary>
    public static class HotkeyHost
    {
        static Native.HookProc _proc;       // 留引用，否则被 GC 掉，钩子变野指针
        static IntPtr _hook = IntPtr.Zero;

        static volatile int _mods;
        static volatile int _key;
        static volatile bool _swallowUp;    // 这次按下是我们认领的，抬起也要吞

        static bool _started;

        // 下面两个在 Start() 里从 CAD 线程取好，钩子线程只读 —— 钩子线程不许调 CAD API
        static int _myPid;
        static IntPtr _cadHwnd = IntPtr.Zero;

        // CAD 主线程的 Dispatcher，用来把"开面板"派发回去
        static Dispatcher _cadDisp;

        // 钩子线程
        static Thread _thread;
        static Dispatcher _hookDisp;
        static DispatcherTimer _retry;

        /// <summary>上一次按键为什么没触发。ALKEY 会把它打出来 —— 快捷键不响应时全靠这一句。</summary>
        public static volatile string LastSkip = "";

        /// <summary>
        /// 设置窗口正在"抓键"时置 true。
        /// 不挂起的话，用户想录 Ctrl+Shift+Q，钩子会先把这一下吞掉，输入框里什么也录不到。
        /// </summary>
        public static volatile bool Suspended;

        /// <summary>键盘钩子有没有真的装上。</summary>
        public static bool HookInstalled { get { return _hook != IntPtr.Zero; } }

        /// <summary>
        /// 演练模式：命中组合键后不开面板，只往 Trace 记一条。
        /// 给 cadkit/pieprobe 做端到端验证用 —— 探针里真把面板开出来会抢焦点、干扰后面的截图。
        /// 生产代码里永远是 false。
        /// </summary>
        public static volatile bool DryRun;

        /// <summary>钩子线程还活着吗。</summary>
        public static bool ThreadAlive { get { return _thread != null && _thread.IsAlive; } }

        // ---------------------------------------------------------------- 生命周期

        /// <summary>必须在 CAD 主线程调用（要取 CAD 主窗口句柄和 Dispatcher）。</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            try { _cadDisp = Dispatcher.CurrentDispatcher; } catch { }
            try { _myPid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            try { _cadHwnd = AcGuiApp.MainWindow.Handle; } catch { }

            Reload();
            StartThread();
        }

        public static void Stop()
        {
            _started = false;

            try
            {
                Dispatcher d = _hookDisp;
                if (d != null)
                {
                    d.BeginInvoke(new Action(() =>
                    {
                        try { Uninstall(); } catch { }
                        try { d.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
                    }));
                }
            }
            catch { }

            try { if (_thread != null) _thread.Join(600); } catch { }

            _thread = null;
            _hookDisp = null;
            _hook = IntPtr.Zero;
            _proc = null;
            _swallowUp = false;
        }

        /// <summary>设置里改完快捷键后调一次，让钩子里的缓存跟上。不用重装钩子。</summary>
        public static void Refresh()
        {
            Reload();
            if (_started && !ThreadAlive) StartThread();
        }

        static void Reload()
        {
            try
            {
                Config cfg = ConfigStore.Current;
                Settings st = cfg == null ? null : cfg.Settings;
                _mods = st == null ? 0 : st.HotkeyMods;
                _key = st == null ? 0 : st.HotkeyKey;
            }
            catch
            {
                _mods = 0;
                _key = 0;
            }
        }

        // ---------------------------------------------------------------- 钩子线程

        static void StartThread()
        {
            if (_thread != null) return;

            _thread = new Thread(() =>
            {
                try
                {
                    _hookDisp = Dispatcher.CurrentDispatcher;
                    Ensure();
                    Dispatcher.Run();
                }
                catch { }
            });
            _thread.IsBackground = true;
            _thread.Name = "AutoLayer.Hotkey";
            try { _thread.SetApartmentState(ApartmentState.STA); } catch { }
            _thread.Start();
        }

        /// <summary>只在钩子线程上调用。</summary>
        static void Ensure()
        {
            if (_hook != IntPtr.Zero) return;

            try
            {
                _proc = OnKey;
                // 低层钩子只能全局装：dwThreadId 给线程 id 会失败 err=1429 ERROR_GLOBAL_ONLY_HOOK
                _hook = Native.SetWindowsHookEx(
                    Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            }
            catch
            {
                _hook = IntPtr.Zero;
            }

            if (_hook == IntPtr.Zero) ScheduleRetry();
            else if (_retry != null) { try { _retry.Stop(); _retry = null; } catch { } }
        }

        /// <summary>
        /// 装不上就隔两秒再试 —— 绝不能"第一次没装上，从此静默失效"。
        /// （系统里低层钩子有数量限制，偶尔别的程序占满了会失败。）
        /// </summary>
        static void ScheduleRetry()
        {
            if (_retry != null) return;
            Dispatcher d = _hookDisp;
            if (d == null) return;

            _retry = new DispatcherTimer(DispatcherPriority.Normal, d) { Interval = TimeSpan.FromSeconds(2) };
            _retry.Tick += (s, e) =>
            {
                if (_hook != IntPtr.Zero) { try { _retry.Stop(); _retry = null; } catch { } return; }
                if (!_started) { try { _retry.Stop(); _retry = null; } catch { } return; }
                try { _proc = OnKey; _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0); }
                catch { _hook = IntPtr.Zero; }
            };
            _retry.Start();
        }

        /// <summary>只在钩子线程上调用。</summary>
        static void Uninstall()
        {
            if (_retry != null) { try { _retry.Stop(); } catch { } _retry = null; }
            if (_hook != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            _proc = null;
            _swallowUp = false;
        }

        // ---------------------------------------------------------------- 回调

        static IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            bool swallow = false;

            if (nCode >= 0)
            {
                try { swallow = Handle((int)wParam.ToInt64(), lParam); }
                catch { swallow = false; }
            }

            if (swallow) return new IntPtr(1);
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        static bool Handle(int msg, IntPtr lParam)
        {
            // 认领过的按键：抬起一定吞掉，哪怕中途松了 Ctrl
            if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
            {
                if (_swallowUp) { _swallowUp = false; return true; }
                return false;
            }

            if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN) return false;

            // KBDLLHOOKSTRUCT 的第一个字段就是 vkCode
            int vk = Marshal.ReadInt32(lParam);

            if (_key <= 0) return false;                 // 没配快捷键：直接放行，别记录（每按一下都记会刷屏）
            if (Suspended) { Skip("设置窗口正在抓键"); return false; }

            // 只认 Ctrl / Alt / Win 参与的组合：Shift+字母 是在打字，不能抢
            if ((_mods & (Hotkey.Ctrl | Hotkey.Alt | Hotkey.Win)) == 0)
            {
                Skip("配置里没有 Ctrl/Alt/Win，不接管");
                return false;
            }

            // 记原因要挑着记：正常打字（键不对、修饰键也不对）一律不记，
            // 否则 LastSkip 永远停在"按的是 X 不是 Q"上，等于没用。
            if (vk != _key)
            {
                if (ModsMatch()) Skip("组合键按对了，但主键不是 " + Hotkey.KeyName(_key));
                return false;
            }
            if (!ModsMatch())
            {
                Skip("按了 " + Hotkey.KeyName(_key) + "，但修饰键不是 " + Hotkey.Text(_mods, _key));
                return false;
            }
            if (!CadFocused()) { Skip("前台不是 CAD"); return false; }

            LastSkip = "";
            _swallowUp = true;
            Fire();
            return true;
        }

        static void Skip(string why)
        {
            if (!string.Equals(LastSkip, why, StringComparison.Ordinal)) LastSkip = why;
        }

        static bool ModsMatch()
        {
            bool ctrl = Down(Native.VK_CONTROL);
            bool alt = Down(Native.VK_MENU);
            bool shift = Down(Native.VK_SHIFT);
            bool win = Down(Native.VK_LWIN) || Down(Native.VK_RWIN);

            // 全等而不是"包含"：配的是 Ctrl+Q，那 Ctrl+Shift+Q 就不该弹
            return ctrl == ((_mods & Hotkey.Ctrl) != 0)
                && alt == ((_mods & Hotkey.Alt) != 0)
                && shift == ((_mods & Hotkey.Shift) != 0)
                && win == ((_mods & Hotkey.Win) != 0);
        }

        static bool Down(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>
        /// 前台窗口是不是 CAD 自己。**跑在钩子线程上，不许调 CAD API。**
        ///
        /// 比进程 id：CAD 是进程内插件，前台窗口属于本进程就说明是 CAD 的
        /// （包括它自己的面板、对话框）。比句柄不可靠 —— 面板是独立顶层窗口，
        /// 拿它的句柄和主窗口比根窗口是对不上的。
        /// </summary>
        static bool CadFocused()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;

                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                if (pid != 0 && _myPid != 0) return pid == (uint)_myPid;

                // 拿不到进程 id 时兜底：比根窗口
                if (_cadHwnd != IntPtr.Zero)
                    return Native.GetAncestor(fg, Native.GA_ROOT) == Native.GetAncestor(_cadHwnd, Native.GA_ROOT);

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 派发到 CAD 主线程去开面板。
        /// 钩子里绝不能直接建窗口 —— 会把输入链卡在回调里，严重时死锁。
        /// </summary>
        static void Fire()
        {
            if (DryRun)
            {
                Trace.Add("快捷键 " + Hotkey.Text(_mods, _key) + " → 命中（演练，不开面板）");
                return;
            }

            Dispatcher d = _cadDisp;
            if (d == null) return;

            try
            {
                d.BeginInvoke(new Action(() =>
                {
                    try { PanelHost.Show(); }
                    catch (Exception ex) { Trace.Add("快捷键开面板失败: " + ex.GetType().Name + " " + ex.Message); }
                }));

                Trace.Add("快捷键 " + Hotkey.Text(_mods, _key) + " → 打开面板");
            }
            catch (Exception ex)
            {
                Trace.Add("快捷键派发失败: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 诊断

        /// <summary>ALKEY 用：把钩子和当前按键状态摊开。在 CAD 主线程上调用，可以碰 CAD API。</summary>
        public static string Describe()
        {
            // 先读一遍配置：钩子压根没起来的时候（比如界面组件没加载），
            // 缓存里还是 0，直接打出来会变成"快捷键：无"，看着像没配。
            Reload();
            if (_myPid == 0) { try { _myPid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { } }

            var sb = new System.Text.StringBuilder();
            sb.Append("\n  快捷键：").Append(_key > 0 ? Hotkey.Text(_mods, _key) : "无");
            sb.Append("（主键 VK ").Append(_key).Append("，修饰 ")
              .Append((_mods & Hotkey.Ctrl) != 0 ? "Ctrl " : "")
              .Append((_mods & Hotkey.Alt) != 0 ? "Alt " : "")
              .Append((_mods & Hotkey.Shift) != 0 ? "Shift " : "")
              .Append((_mods & Hotkey.Win) != 0 ? "Win" : "").Append("）");

            sb.Append("\n  键盘钩子：").Append(HookInstalled ? "已装上（全局低层 WH_KEYBOARD_LL）" : "**没装上**（这就是不响应的原因，每 2 秒会自动重试）");
            sb.Append("　钩子线程：").Append(ThreadAlive ? "在跑" : "**没起来**");
            sb.Append("　挂起：").Append(Suspended ? "是（设置窗口还开着抓键）" : "否");

            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                uint pid = 0;
                if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out pid);
                sb.Append("\n  前台窗口：0x").Append(fg.ToInt64().ToString("X"))
                  .Append("（进程 ").Append(pid).Append("，本进程 ").Append(_myPid).Append("）　")
                  .Append(CadFocused() ? "是 CAD，可以接管" : "不是 CAD，不接管");
            }
            catch { }

            sb.Append("\n  上次按键没触发的原因：")
              .Append(string.IsNullOrEmpty(LastSkip) ? "(没有记录)" : LastSkip);
            return sb.ToString();
        }
    }
}
