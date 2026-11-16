using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AutoLayer.UI
{
    /// <summary>
    /// 把系统标题栏刷成深色。不这么做的话，深灰面板顶着一个白色标题栏，很突兀。
    /// 只在 Win10 1809+ 生效；老系统或调用失败时静默跳过，不影响功能。
    /// </summary>
    internal static class DarkTitleBar
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window window)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(window).Handle;
                if (h == IntPtr.Zero) return;

                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 2004+）；19 是更早版本的同一属性
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
            }
            catch { }
        }
    }
}
