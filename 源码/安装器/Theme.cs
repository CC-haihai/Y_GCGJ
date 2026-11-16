using System;
using System.Drawing;

namespace YGuiCeng.Setup
{
    /// <summary>
    /// 与插件本体 UiTheme.cs / SettingsWindow.xaml 的 §2 令牌同一套深色皮肤。
    /// WinForms 画不了半透明线，Line* 用「叠在 BgDeep 上的实色近似值」代替。
    /// 字号单独走更小的一套：安装器是紧凑工具窗，信息密度比插件面板高，
    /// 按网页正文的观感（9pt ≈ 12px）排，不套用插件面板那套 12pt 档。
    /// </summary>
    internal static class Theme
    {
        // ======== §2.1 底色 ========
        public static readonly Color WindowBg = C(0x14, 0x15, 0x19);   // BgDeep
        public static readonly Color TitleBg = C(0x14, 0x15, 0x19);
        public static readonly Color CardBg = C(0x20, 0x21, 0x28);     // Surface
        public static readonly Color CardBgOff = C(0x1A, 0x1B, 0x20);   // Sunken
        public static readonly Color CardBgHover = C(0x26, 0x28, 0x2F); // Raised
        public static readonly Color CardBorder = C(0x3A, 0x3D, 0x45);  // ~ LineStrong
        public static readonly Color CardBorderOff = C(0x2C, 0x2E, 0x35);
        public static readonly Color Divider = C(0x2B, 0x2D, 0x34);     // ~ Line

        // ======== §2.3 文字 ========
        public static readonly Color Text = C(0xF1, 0xF5, 0xF9);
        public static readonly Color TextDim = C(0x98, 0xA0, 0xAC);
        public static readonly Color TextMuted = C(0x8A, 0x93, 0xA0);   // TextFaint
        public static readonly Color TextFaint = C(0x6B, 0x72, 0x7E);    // 三级说明，仅安装器用
        public static readonly Color TextDisabled = C(0x56, 0x5A, 0x62);

        // ======== §2.4 强调 ========
        public static readonly Color Accent = C(0x25, 0x63, 0xEB);
        public static readonly Color AccentHi = C(0x3B, 0x82, 0xF6);
        public static readonly Color AccentLo = C(0x1D, 0x4E, 0xD8);
        public static readonly Color AccentText = C(0xFF, 0xFF, 0xFF);   // TextOnAccent
        public static readonly Color AccentSoft = C(0x1E, 0x2A, 0x44);   // 选中卡片底
        public static readonly Color Link = C(0x60, 0xA5, 0xFA);         // AccentLine

        // ======== §2.5 语义 ========
        public static readonly Color OkBg = C(0x18, 0x33, 0x2B);
        public static readonly Color OkText = C(0x34, 0xD3, 0x99);
        public static readonly Color OffBg = C(0x26, 0x28, 0x2F);
        public static readonly Color Warn = C(0xF5, 0x9E, 0x0B);
        public static readonly Color WarnSoft = C(0x2F, 0x25, 0x17);
        public static readonly Color Danger = C(0xF8, 0x71, 0x71);
        public static readonly Color DangerSoft = C(0x2A, 0x1A, 0x1C);
        public static readonly Color DangerSolid = C(0xDC, 0x26, 0x26);

        // 字号档位：整体比插件面板小一档，接近网页正文
        public const float Xs = 8f;      // 徽标 / R 版本 / 滚动提示
        public const float Sm = 9f;      // 正文 / 按钮 / 链接 / 状态 / 说明
        public const float Md = 10f;     // 卡片标题 / 标题栏产品名
        public const float Lg = 11f;     // 备用大号（安装器不用）

        private static readonly string[] Families =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"
        };

        private static FontFamily _family;

        private static FontFamily Family
        {
            get
            {
                if (_family != null) return _family;
                foreach (var n in Families)
                {
                    try
                    {
                        var f = new FontFamily(n);
                        _family = f;
                        return f;
                    }
                    catch { }
                }
                _family = FontFamily.GenericSansSerif;
                return _family;
            }
        }

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font(Family, size, style, GraphicsUnit.Point);
        }

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        /// <summary>用 Graphics 画圆角矩形路径（WinForms 原生无圆角面板）。</summary>
        public static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
