using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace AutoLayer.UI
{
    /// <summary>
    /// 全局配色与排版令牌 —— 面板、对话框、状态条、圆盘、图层切换面板全用这一套。
    ///
    /// 令牌来源：merged/design/01-visual-spec.md §2（v1.5 定稿，共 40 个值）。
    ///   视觉基调：深色不透明底 + 一道发丝描边 + 单一蓝色强调，
    ///   不用渐变、不用投影堆层次，靠"底色差 + 1px 线"分层。
    ///
    /// 两条硬规则（红线 1 / 红线 7）：
    ///   1. 任何 #RRGGBB 必须能在 §2 查到名字；新颜色只能加在这里，不许写在 XAML / CS 里。
    ///   2. 字号只有 6 档：8.5 / 10.5 / 11.5 / 12 / 13.5 / 20。
    ///
    /// ⚠ 这里的数值和几个 XAML 里的画刷是同一套，改一处必须同步：
    ///     PanelWindow.xaml / SettingsWindow.xaml / ConfirmDialog.xaml /
    ///     NameDialog.xaml / QuickSwitch/LayerSelectionWindow.xaml
    /// （XAML 里没法引用 C# 常量，只能靠这行注释互相盯着。）
    /// </summary>
    internal static class UiTheme
    {
        // ======== §2.1 底色（4）========
        public static readonly Color BgDeep = C(0xFF141519);      // 窗口底
        public static readonly Color Surface = C(0xFF202128);     // 卡片、标题栏、圆盘扇区
        public static readonly Color Raised = C(0xFF26282F);      // 输入框、下拉框、chip 常态、悬停行
        public static readonly Color Sunken = C(0xFF1A1B20);      // 表格体、列表底、分类栏容器

        // ======== §2.2 线（4）========
        // 结构线求"几乎看不见"，控件边界求"必须看得见"，两类不许混用。
        public static readonly Color Line = C(0x1FFFFFFF);        // 结构性：行底线、标题栏底边
        public static readonly Color LineStrong = C(0x33FFFFFF);  // 结构性：卡片 / 弹层外框
        public static readonly Color LineControl = C(0x66FFFFFF); // 功能性：输入框/次按钮静止边界 3.60:1
        public static readonly Color LineHover = C(0x80FFFFFF);   // 功能性：上述控件悬停边界 4.79:1

        // ======== §2.3 文字（6）========
        public static readonly Color Text = C(0xFFF1F5F9);
        public static readonly Color TextDim = C(0xFF98A0AC);
        public static readonly Color TextFaint = C(0xFF8A93A0);   // v1.5：原 #6E7683 未过 AA，已提亮
        public static readonly Color TextDisabled = C(0xFF565A62);
        public static readonly Color TextOnAccent = C(0xFFFFFFFF);
        public static readonly Color SelectionText = C(0xFFFFFFFF);

        // ======== §2.4 强调（6）========
        public static readonly Color Accent = C(0xFF2563EB);
        public static readonly Color AccentHi = C(0xFF3B82F6);
        public static readonly Color AccentLo = C(0xFF1D4ED8);
        public static readonly Color AccentLine = C(0xFF60A5FA);
        public static readonly Color AccentSoft = C(0xFF1E2A44);
        public static readonly Color AccentInk = C(0xFFDBEAFE);

        // ======== §2.5 语义（9）========
        public static readonly Color Ok = C(0xFF34D399);
        public static readonly Color Warn = C(0xFFF59E0B);
        public static readonly Color WarnSoft = C(0xFF2F2517);
        public static readonly Color WarnLine = C(0xFF4C3716);
        public static readonly Color Danger = C(0xFFF87171);
        public static readonly Color DangerSolid = C(0xFFDC2626);
        public static readonly Color DangerSolidLo = C(0xFFB91C1C);
        public static readonly Color DangerSoft = C(0xFF2A1A1C);
        public static readonly Color DangerLine = C(0xFF4D3A3C);

        // ======== §2.6 状态叠加（7）========
        public static readonly Color Hover = C(0x14FFFFFF);
        public static readonly Color HoverRow = C(0x0FFFFFFF);
        public static readonly Color Pressed = C(0x0AFFFFFF);
        public static readonly Color SelectionBg = C(0xFF1E2A44);
        public static readonly Color SelectionMark = C(0xFF60A5FA);
        public static readonly Color FocusRing = C(0xFF60A5FA);
        public static readonly Color FocusHalo = C(0x4060A5FA);

        // ======== §2.7 控件专用（5）========
        public static readonly Color Track = C(0xFF2E313A);
        public static readonly Color SwitchTrack = C(0xFF3A3D45);
        public static readonly Color SwitchKnob = C(0xFFF1F5F9);
        public static readonly Color ScrollThumb = C(0x4DFFFFFF);
        public static readonly Color ScrollThumbHover = C(0x66FFFFFF);

        // ======== §2.8 空状态（2）========
        public static readonly Color EmptyArt = C(0xFF2E313A);
        public static readonly Color EmptyArtFill = C(0xFF1A1B20);

        // ======== §3.1 排版阶（6 档字号，禁止别的小数）========
        public const double FsMicro = 8.5;    // 圆盘中心提示、chip 角标、ACI 号
        public const double FsCaption = 10.5; // 表头、状态列、单位
        public const double FsBodyS = 11.5;   // 密集正文：胶囊、标签、提示行、输入框
        public const double FsBody = 12;      // 默认正文、列表主字段、按钮
        public const double FsTitle = 13.5;   // 窗口标题
        public const double FsMetric = 20;    // 预留：单值统计（当前无使用点）

        // ======== §4 尺寸阶 ========
        /// <summary>圆角：控件 4 / 按钮与输入 6 / 卡片与弹层 8 / 胶囊 15。</summary>
        public const double RadiusSmall = 4;
        public const double RadiusControl = 6;
        public const double RadiusCard = 8;
        public const double RadiusPill = 15;

        /// <summary>标准列表行高（150%+ 缩放到 26，下限 24）。</summary>
        public const double RowHeight = 28;
        public const double RowHeightCompact = 26;

        /// <summary>两档标题栏：主面板 48（带 3px 四态状态条）/ 弹窗与图层切换面板 32。</summary>
        public const double TitleBarMain = 48;
        public const double TitleBarCompact = 32;
        public const double TitleBarStatusStrip = 3;

        /// <summary>控件高度阶里常用的几个。</summary>
        public const double HInput = 30;
        public const double HButton = 32;
        public const double HChip = 22;
        public const double HBar = 26;
        public const double HFooter = 44;

        /// <summary>内容区左右内边距（§4.3：sp-7 = 18）。</summary>
        public const double PadX = 18;

        // ---- 排版 ----
        public static readonly FontFamily Font = new FontFamily("Segoe UI, Microsoft YaHei UI");

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
