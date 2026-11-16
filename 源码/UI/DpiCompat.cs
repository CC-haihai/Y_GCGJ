using System;
using System.Windows;
using System.Windows.Media;

namespace AutoLayer.UI
{
    /// <summary>
    /// WPF 的 DpiScale / VisualTreeHelper.GetDpi 是 .NET Framework 4.6.2 才有的 API。
    /// 合并版要兼容 AutoCAD 2016（.NET 4.5），所以自己补一份等价实现。
    ///
    /// 取值顺序：已显示的窗口走 CompositionTarget.TransformToDevice（最准）；
    /// 还没 Show 的窗口拿不到 PresentationSource，退回 Win32 的屏幕 DPI。
    /// </summary>
    public struct DpiScale
    {
        public double DpiScaleX;
        public double DpiScaleY;
    }

    public static class DpiHelper
    {
        public static DpiScale GetDpi(Visual visual)
        {
            var result = new DpiScale { DpiScaleX = 1.0, DpiScaleY = 1.0 };

            try
            {
                PresentationSource src = (visual == null) ? null : PresentationSource.FromVisual(visual);
                if (src != null && src.CompositionTarget != null)
                {
                    Matrix m = src.CompositionTarget.TransformToDevice;
                    if (m.M11 > 0.1) result.DpiScaleX = m.M11;
                    if (m.M22 > 0.1) result.DpiScaleY = m.M22;
                    return result;
                }
            }
            catch { }

            // 未显示：用桌面 DC 的水平/垂直 DPI
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    result.DpiScaleX = g.DpiX / 96.0;
                    result.DpiScaleY = g.DpiY / 96.0;
                }
            }
            catch { }

            if (result.DpiScaleX <= 0.1) result.DpiScaleX = 1.0;
            if (result.DpiScaleY <= 0.1) result.DpiScaleY = 1.0;
            return result;
        }
    }
}
