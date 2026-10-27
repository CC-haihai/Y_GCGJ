using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace AutoLayer.UI
{
    /// <summary>
    /// 全局配色与排版令牌 —— 面板、对话框、状态条、圆盘全用这一套。
    ///
    /// 视觉基调照 StarPie（星盘）：深色不透明底 + 一道发丝描边 + 单一蓝色强调，
    /// 不用渐变、不用投影堆层次，靠"底色差 + 1px 线"分层。
    ///
    /// 这里的配色数值和渲染代码都是本项目自己写的，只参考了 StarPie 的整体观感；
    /// 真正从 StarPie 移植的代码只有 SectorGeometry.cs（MIT，声明见该文件头）。
    ///
    /// ⚠ 这里的数值和三个 XAML 里的样式是同一套，改一处必须同步：
    ///     PanelWindow.xaml / ConfirmDialog.xaml / NameDialog.xaml
    /// （XAML 里没法引用 C# 常量，只能靠这行注释互相盯着。）
    /// </summary>
    internal static class UiTheme
    {
        // ---- 底色：三层就够，多了反而脏 ----
        public static readonly Color BgDeep = C(0xFF141519);      // 窗口底
        public static readonly Color Surface = C(0xFF202128);     // 卡片、标题栏、圆盘扇区
        public static readonly Color Raised = C(0xFF26282F);      // 悬停行、输入框
        public static readonly Color Sunken = C(0xFF1A1B20);      // 表格体、状态条

        // ---- 线 ----
        public static readonly Color Line = C(0x1FFFFFFF);        // 分隔线
        public static readonly Color LineStrong = C(0x33FFFFFF);  // 控件描边

        // ---- 文字 ----
        public static readonly Color Text = C(0xFFF1F5F9);
        public static readonly Color TextDim = C(0xFF98A0AC);
        public static readonly Color TextFaint = C(0xFF6E7683);

        // ---- 强调色：全场只有一个 ----
        public static readonly Color Accent = C(0xFF2563EB);
        public static readonly Color AccentHi = C(0xFF3B82F6);
        public static readonly Color AccentLo = C(0xFF1D4ED8);
        public static readonly Color AccentLine = C(0xFF60A5FA);
        public static readonly Color AccentSoft = C(0xFF1E2A44);

        // ---- 语义色 ----
        public static readonly Color Ok = C(0xFF34D399);
        public static readonly Color Warn = C(0xFFF59E0B);
        public static readonly Color Danger = C(0xFFF87171);

        // ---- 排版 ----
        public static readonly FontFamily Font = new FontFamily("Segoe UI, Microsoft YaHei UI");

        /// <summary>圆角：控件 6，卡片 8。</summary>
        public const double RadiusControl = 6;
        public const double RadiusCard = 8;

        static Color C(uint argb)
        {
            return Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        // 冻结的画刷缓存：面板每次刷新会重新建行，别反复 new
        static readonly Dictionary<uint, SolidColorBrush> Cache = new Dictionary<uint, SolidColorBrush>();

        /// <summary>取一个冻结画刷（同一颜色只建一次）。</summary>
        public static SolidColorBrush B(Color c)
        {
            uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            SolidColorBrush b;
            if (Cache.TryGetValue(key, out b)) return b;
            b = new SolidColorBrush(c);
            b.Freeze();
            Cache[key] = b;
            return b;
        }

        /// <summary>同一个颜色换个透明度 —— 圆盘要半透明的盘面，别另起一个色号。</summary>
        public static Color Glass(Color c, byte alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        /// <summary>带透明度的白/黑，用来叠层次，别去调新的灰。</summary>
        public static SolidColorBrush White(byte alpha) { return B(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)); }
        public static SolidColorBrush Black(byte alpha) { return B(Color.FromArgb(alpha, 0x00, 0x00, 0x00)); }
    }
}
