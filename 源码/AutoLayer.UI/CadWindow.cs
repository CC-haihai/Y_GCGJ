using System;
using System.Windows;
using System.Windows.Interop;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoLayer.UI
{
    /// <summary>
    /// 找 AutoCAD 主窗口和"画图区"的屏幕矩形。
    ///
    /// 没有官方 API 能拿到画图区 HWND，所以用启发式：从主窗口往下，
    /// 每一层挑面积最大的可见子窗口，只要它还占满父窗口的 70% 以上就继续往里走。
    /// 停在"再往里就明显变小"的那一层 —— 那就是画图区（下面接着命令行，右边可能挂着调色板）。
    /// 拿不到就退回主窗口矩形，绝不让状态条凭空消失。
    /// </summary>
    internal static class CadWindow
    {
        static IntPtr _main;
        static Native.RECT _area;
        static bool _areaValid;
        static DateTime _areaAt = DateTime.MinValue;

        /// <summary>AutoCAD 主窗口句柄。没有主窗口（内核版）返回 Zero。</summary>
        public static IntPtr Main()
        {
            try
            {
                IntPtr h = AcApp.MainWindow.Handle;
                if (h != _main)
                {
                    _main = h;
                    _areaValid = false;
                }
                return h;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>画图区在屏幕上的矩形（物理像素）。</summary>
        public static bool TryDrawingArea(out Native.RECT rect)
        {
            rect = new Native.RECT();

            // 缓存 800ms：CAD 拖窗口时不必每帧重新枚举
            if (_areaValid && (DateTime.UtcNow - _areaAt).TotalMilliseconds < 800)
            {
                rect = _area;
                return true;
            }

            IntPtr main = Main();
            if (main == IntPtr.Zero || !Native.IsWindow(main)) return false;

            Native.RECT client;
            if (!Native.GetClientRect(main, out client)) return false;
            var origin = new Native.POINT(0, 0);
            if (!Native.ClientToScreen(main, ref origin)) return false;

            long clientArea = (long)client.Width * client.Height;

            IntPtr deep = DeepestBigChild(main);
            Native.RECT dr;
            if (deep != IntPtr.Zero && deep != main && Native.GetWindowRect(deep, out dr))
            {
                long a = (long)dr.Width * dr.Height;
                // 太小说明探到工具栏里去了，不如退回主窗口
                if (a > clientArea * 0.2)
                {
                    _area = dr;
                    _areaValid = true;
                    _areaAt = DateTime.UtcNow;
                    rect = dr;
                    return true;
                }
            }

            // 兜底：主窗口客户区，底部留出命令行 + 状态栏
            rect = new Native.RECT
            {
                Left = origin.X,
                Top = origin.Y + 40,
                Right = origin.X + client.Width,
                Bottom = origin.Y + client.Height - 96
            };
            _area = rect;
            _areaValid = true;
            _areaAt = DateTime.UtcNow;
            return rect.Bottom > rect.Top;
        }

        static IntPtr DeepestBigChild(IntPtr root)
        {
            IntPtr cur = root;

            for (int depth = 0; depth < 6; depth++)
            {
                Native.RECT pr;
                if (!Native.GetWindowRect(cur, out pr)) break;
                long parentArea = (long)pr.Width * pr.Height;
                if (parentArea <= 0) break;

                IntPtr best = IntPtr.Zero;
                long bestArea = 0;

                IntPtr child = Native.GetWindow(cur, Native.GW_CHILD);
                int guard = 0;
                while (child != IntPtr.Zero && guard++ < 512)
                {
                    if (Native.IsWindowVisible(child))
                    {
                        Native.RECT cr;
                        if (Native.GetWindowRect(child, out cr))
                        {
                            long a = (long)cr.Width * cr.Height;
                            if (a > bestArea) { bestArea = a; best = child; }
                        }
                    }
                    child = Native.GetWindow(child, Native.GW_HWNDNEXT);
                }

                if (best == IntPtr.Zero) break;
                // 子窗口已经明显比父窗口小 —— 说明父窗口就是画图区，停
                if (bestArea < parentArea * 0.7) break;
                cur = best;
            }

            return cur;
        }

        /// <summary>
        /// 把我们的浮窗挂成 CAD 主窗口的"附属窗口"。
        ///
        /// 为什么不用 Topmost：Topmost 是压在所有程序之上，用户切到别的软件时状态条还赖在屏幕上，
        /// 很碍事。设 Owner 之后它就只在 CAD 之上 —— CAD 最小化时自动收起，切走时跟着退到后面，
        /// 这才是贴边工具该有的行为。
        /// </summary>
        public static void AttachToCad(Window w)
        {
            if (w == null) return;
            IntPtr main = Main();
            if (main == IntPtr.Zero) return;

            try
            {
                var helper = new WindowInteropHelper(w);
                helper.EnsureHandle();      // 先建 HWND，之后才能改 Owner
                helper.Owner = main;
            }
            catch { }
        }

        /// <summary>这个屏幕点是否落在画图区里（圆盘只在画图区响应右键长按）。</summary>
        public static bool InDrawingArea(int x, int y)
        {
            Native.RECT r;
            if (!TryDrawingArea(out r)) return false;
            return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
        }
    }
}
