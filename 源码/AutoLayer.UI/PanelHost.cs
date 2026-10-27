using System;
using System.Windows.Interop;
using AcGuiApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoLayer.UI
{
    /// <summary>
    /// 界面总入口。Core 通过反射调这里的静态方法，
    /// 这样 Core 不用引用 acmgd / WPF，accoreconsole 里也能跑。
    /// </summary>
    public static class PanelHost
    {
        static PanelWindow _window;

        // ------------------------------------------------------------------ 生命周期

        /// <summary>
        /// AutoCAD 加载插件时调一次：状态条 + 右键圆盘在这里起来。
        /// 真正建窗口推迟到第一次 Idle —— Initialize 阶段碰 WPF 容易撞上 CAD 还没起完。
        /// </summary>
        public static void Startup()
        {
            try { PieHost.Startup(); } catch { }
        }

        /// <summary>卸载插件时调一次，把钩子和窗口全部收回。</summary>
        public static void Shutdown()
        {
            try { PieHost.Shutdown(); } catch { }
            try { if (_window != null) { _window.Close(); _window = null; } } catch { }
        }

        // ------------------------------------------------------------------ 面板

        public static void Show()
        {
            if (_window == null)
            {
                _window = new PanelWindow();
                _window.Closed += (s, e) => { _window = null; };
            }

            _window.Reload();

            if (_window.IsVisible)
            {
                _window.Activate();
                return;
            }

            // 把面板挂到 AutoCAD 主窗口上：始终浮在 CAD 之上，最小化 CAD 时一起收起来。
            try
            {
                var helper = new WindowInteropHelper(_window);
                helper.Owner = AcGuiApp.MainWindow.Handle;
            }
            catch { }

            _window.Show();
            _window.Activate();
        }

        // ------------------------------------------------------------------ 设置

        /// <summary>ALSET 命令：直接打开设置窗口（面板没开就先把面板开起来）。</summary>
        public static void ShowSettings()
        {
            Show();
            if (_window != null) _window.OpenSettings();
        }

        // ------------------------------------------------------------------ 诊断

        /// <summary>ALKEY 用：把快捷键钩子的状态摊开。</summary>
        public static string DescribeHotkey()
        {
            try { return HotkeyHost.Describe(); }
            catch (Exception ex) { return "\n  读取失败：" + ex.Message; }
        }

        /// <summary>ALMOUSE 用：把圆盘触发（右键 / 侧键）这条路摊开。</summary>
        public static string DescribeMouse()
        {
            try { return PieHost.DescribeMouse(); }
            catch (Exception ex) { return "\n  读取失败：" + ex.Message; }
        }

        // ------------------------------------------------------------------ 圆盘

        /// <summary>ALPIE 命令：手动弹一次圆盘（正常用法是右键长按）。</summary>
        public static bool ShowPieNow()
        {
            try { return PieHost.ShowNow(); }
            catch { return false; }
        }

        /// <summary>ALHUD 之类改完设置后，让状态条立刻刷新。</summary>
        public static void RefreshHud()
        {
            try { StatusHud.Refresh(); } catch { }
        }

        // ------------------------------------------------------------------ 状态条

        /// <summary>
        /// 状态条上点了配置名、配置已经切过去了：面板要是开着，让它跟着换过去。
        ///
        /// 有未保存的改动就不动面板 —— 重载会把用户刚改的东西扔掉，
        /// 而他点状态条只是想换配置，不是想丢弃编辑。
        /// </summary>
        public static void NotifyProfileChanged()
        {
            try
            {
                if (_window == null || !_window.IsVisible) return;
                if (_window.IsDirty) return;
                _window.Reload();
            }
            catch { }
        }
    }
}
