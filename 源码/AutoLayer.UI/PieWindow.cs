using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AutoLayer.Core;

namespace AutoLayer.UI
{
    /// <summary>圆盘皮肤：深色 / 白色 / 毛玻璃（照 StarPie 的三套观感）。</summary>
    public enum PieSkin
    {
        Dark = 0,
        Light = 1,
        Glass = 2
    }

    /// <summary>
    /// 圆盘窗口。纯代码构建，不写 XAML —— 合并版里 BAML 资源名容易和程序集名对不上，
    /// 少一个 XAML 就少一处坑。
    ///
    /// ── 视觉设计（2026-10-22 第四版，照 StarPie 的路子）────────────────
    /// 说明：下面这些是**照着观感重写**的渲染代码，不是从 StarPie 拷来的；
    ///       从 StarPie 移植的只有扇区几何，见 SectorGeometry.cs（MIT，声明在文件头）。
    /// 前三版做的是"深色玻璃盘 + 挖出扇区"：扇区是尖楔形，几道黑缝从中心劈到盘边，又硬又脏；
    /// 盘外还多一圈发丝圆环 + 12px 空边当镜圈，像画框套画框。
    ///
    /// 第四版照 StarPie（WinPieGestures/ 下的 Renderers + AppConfig 默认值）重做，四条：
    ///   1. **紧密拼合**。StarPie 的默认形态 Original 就是"扇区贴死、无间隙"，
    ///      分界靠每格一道 0.9px 描边（#4AFFFFFF），缝只留 0.8px —— 看着是一道刻线，不是黑沟。
    ///   2. **四角圆角**，但只磨掉尖，不做成胶囊（圆角一大，五格就摆成一朵花瓣）。
    ///   3. **高亮 = 整格实色 + 亮边 + 投影光晕**（blur 22 / opacity 0.8）。
    ///      只亮一格，所以饱和色不会糊成一片；光晕让人一眼看出"松手就是它"。
    ///   4. **底圆同色填缝**。少了它，浅色图纸上每条缝都透出一道白光，整盘像被切开。
    ///
    /// 圆角是真的四角圆角：外弧两端沿弧长内收 c，再用一段以原尖角为控制点的二次贝塞尔
    /// 过渡到径向直线上距尖角 c 的位置。（试过"缩小几何 + 2c 粗圆角描边"的偏方：
    /// 等距外扩会让扇区在内圈被撑得更宽，五个格子长成一朵花瓣，已弃用。）
    ///
    /// 布局：2~9 个单环均分；10~16 个分两环，外环 8 格、内环装剩下的（内环错开半格）。
    /// 数量少就少画几格，不留空扇区。
    /// </summary>
    internal sealed class PieWindow : Window
    {
        // ---- 几何（单位 DIP）----
        // 1.6：整盘从 284 缩到 264。原来那张在 1080p 上占四分之一屏高，用户嫌大。
        // 只缩 7% 而不是更多，是因为文字块的横向预算约等于「环厚的一半」——
        // 再薄下去「立面造型 / 内分割线」这种 4 字名就会被省略号截掉。
        const double S_R0 = 52, S_R1 = 110;      // 单环
        const double O_R0 = 72, O_R1 = 110;      // 双环：外环 8 格
        const double I_R0 = 38, I_R1 = 65;       // 双环：内环装剩下的
        const double Pad = 22;                   // 留白给投影

        const double CrSingle = 5;               // 单环圆角（只把尖角磨一下，不切出花瓣）
        const double CrOuter = 4;
        const double CrInner = 3.5;
        const double GapPx = 0.8;                // 缝隙像素宽：几乎贴死，靠描边分界（StarPie 的 Original 形态就是无间隙）

        // ---- 配色：三套皮肤，字段全由 ApplySkin 填，别在别处写死颜色 ----
        static class Skin
        {
            public static Color Bg, Line, AccentFill, AccentLine;
            public static Color Name, Code, Hub, HubHint, HubHintHot;
            public static Color HubHot;             // 悬停时中心那行大字 —— 中心底色不跟着变，所以不能写死白
            public static Color CodeHot;            // 悬停格子的编号行：压在蓝底上，必须提亮
            public static DropShadowEffect Glow;    // 悬停光晕
            public static DropShadowEffect Idle;    // 常态投影（毛玻璃靠它浮起来）
            public static DropShadowEffect Shell;   // 整盘一层影（白盘要收得紧，不然在白图纸上糊成一团灰晕）
            public static DropShadowEffect LabelShadow; // 给文字垫一层暗影（毛玻璃压在浅色图纸上时靠它保可读性）
            public static bool PerSectorShadow;     // 每格都投一层影，还是整盘投一层
            public static bool InnerGlassRing;      // 中心外面再描一圈玻璃环
            public static bool BackdropBlur;        // 弹盘前抓一帧屏幕糊掉垫在底下（只有毛玻璃要）
            public static double BlurRadius = 14;   // 抓屏模糊半径（DIP）
            public static bool BaseDisc = true;     // 底下垫一层同色圆把缝填死（毛玻璃不垫，缝要透）
            public static double LineThickness = 0.9;
        }

        // 默认毛玻璃（2026-10-22 用户定的）：深色图纸上是墨蓝玻璃，浅色图纸上自动换白霜
        static PieSkin _skin = PieSkin.Glass;

        // 玻璃只有一套：墨蓝。
        // 1.6 收尾用户说"颜色也只保留深"，于是白霜那套调色板连同"按底图亮度自动切"
        // 一起删了 —— 黑底上本来也切不出白，留着只是让人以为有得选。

        // 上一次 Show 之后量到的 DPI 缩放：Show 之前摆位置时用它，
        // 免得每次都在 100% 和 150% 之间猜（第一次是 1.0，Show 之后自动校准）
        double _lastScale = 1.0;

        /// <summary>当前皮肤。改这个要重新 Build 才生效（PieHost 每次弹盘都会 Build）。</summary>
        public static PieSkin CurrentSkin { get { return _skin; } }

        public static void SetSkin(PieSkin skin)
        {
            _skin = skin;
            ApplySkin();
        }

        static PieWindow()
        {
            ApplySkin();
        }

        static void ApplySkin()
        {
            switch (_skin)
            {
                case PieSkin.Light:
                    // 白盘：不透明奶白 + 深色发丝线 + 深色文字
                    // 白盘最大的麻烦是"压在白图纸上就没了"：所以描边比深盘更实（0x3A 而不是 0x2E），
                    // 整盘投影也收紧（blur 14 / 0.22），不然边界散成一片灰晕，像没擦干净的污渍。
                    Skin.Bg = Color.FromArgb(0xFA, 0xF7, 0xF8, 0xFA);
                    Skin.Line = Color.FromArgb(0x3A, 0x0F, 0x17, 0x2A);
                    Skin.AccentFill = Color.FromArgb(0xFF, 0x25, 0x63, 0xEB);
                    Skin.AccentLine = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                    Skin.Name = Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A);
                    Skin.Code = Color.FromArgb(0xFF, 0x64, 0x74, 0x8B);
                    Skin.Hub = Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A);
                    Skin.HubHint = Color.FromArgb(0xFF, 0x7C, 0x87, 0x98);
                    Skin.HubHintHot = Color.FromArgb(0xFF, 0x64, 0x74, 0x8B);
                    Skin.HubHot = Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A);   // 中心还是白的，大字必须深
                    Skin.CodeHot = Color.FromArgb(0xFF, 0xDB, 0xEA, 0xFE);
                    Skin.Glow = Glow(Color.FromArgb(0xFF, 0x25, 0x63, 0xEB), 22, 0.45);
                    Skin.Idle = null;
                    Skin.Shell = new DropShadowEffect
                    {
                        BlurRadius = 14, ShadowDepth = 1, Direction = 270,
                        Opacity = 0.22, Color = Colors.Black
                    };
                    Skin.LabelShadow = null;
                    Skin.PerSectorShadow = false;
                    Skin.InnerGlassRing = false;
                    Skin.BackdropBlur = false;
                    Skin.BaseDisc = true;
                    Skin.LineThickness = 1.0;
                    break;

                case PieSkin.Glass:
                    // 毛玻璃：照 StarPie GlassmorphismRenderer 的路子
                    //   扇区半透明、细描边、高亮实色 + 亮边、中心外面再描一圈淡玻璃环
                    //
                    // 玻璃跟深浅底是两套配色 —— 不是矫情：压在浅色图纸上，深色玻璃会变成
                    // 一块灰糊，而白描边在浅底上直接消失。既然抓屏那一步已经拿到了底图，
                    // 顺手算个平均亮度（IsLightBackdrop），按底选配色是免费的。
                    Skin.AccentFill = Color.FromArgb(0xE6, 0x25, 0x63, 0xEB);
                    Skin.AccentLine = Color.FromArgb(0xFF, 0xF5, 0xF3, 0xFF);
                    Skin.Glow = Glow(Color.FromArgb(0xFF, 0xF5, 0xF3, 0xFF), 26, 0.95);
                    Skin.Idle = null;
                    Skin.PerSectorShadow = false;
                    Skin.InnerGlassRing = true;
                    Skin.BackdropBlur = true;       // 不糊底的话，白线会直接从字上穿过去
                    Skin.BlurRadius = 14;
                    Skin.BaseDisc = false;          // 缝要透出模糊的图纸，才像玻璃
                    Skin.LineThickness = 0.9;

                    // 深底：墨蓝玻璃 + 白字 + 白描边
                    // 底色取 0x7A：StarPie 的 0x40 太透，白字压不住；
                    // 0x7A 还能看出后面是模糊的线稿，同时把白字对比度拉到能用的程度。
                    Skin.Bg = Color.FromArgb(0x7A, 0x1A, 0x20, 0x36);
                    Skin.Line = Color.FromArgb(0x5A, 0xE2, 0xE8, 0xF0);
                    Skin.Name = Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC);
                    Skin.Code = Color.FromArgb(0xFF, 0xC7, 0xD2, 0xE0);
                    Skin.Hub = Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC);
                    Skin.HubHint = Color.FromArgb(0xFF, 0xC7, 0xD2, 0xE0);
                    Skin.HubHintHot = Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0);
                    Skin.HubHot = Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC);
                    Skin.CodeHot = Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0);
                    Skin.Shell = new DropShadowEffect
                    {
                        BlurRadius = 24, ShadowDepth = 2, Direction = 270,
                        Opacity = 0.45, Color = Colors.Black
                    };
                    // 白字垫一层暗影：玻璃底下是浅色线稿时，没有它就飘
                    Skin.LabelShadow = Glow(Color.FromArgb(0xCC, 0x00, 0x00, 0x00), 4, 0.75);
                    break;

                default:
                    // 深盘：跟面板/状态条同一套色（UiTheme）
                    Skin.Bg = UiTheme.Glass(UiTheme.Surface, 0xF2);
                    Skin.Line = Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF);
                    Skin.AccentFill = UiTheme.Accent;
                    Skin.AccentLine = UiTheme.AccentLine;
                    Skin.Name = UiTheme.Text;
                    Skin.Code = UiTheme.TextDim;
                    Skin.Hub = UiTheme.Text;
                    Skin.HubHint = UiTheme.TextFaint;
                    Skin.HubHintHot = UiTheme.TextDim;
                    Skin.HubHot = UiTheme.Text;
                    Skin.CodeHot = Color.FromArgb(0xFF, 0xDB, 0xEA, 0xFE);
                    Skin.Glow = Glow(UiTheme.AccentLine, 22, 0.80);
                    Skin.Idle = null;
                    Skin.Shell = new DropShadowEffect
                    {
                        BlurRadius = 20, ShadowDepth = 0, Direction = 270,
                        Opacity = 0.50, Color = Colors.Black
                    };
                    Skin.LabelShadow = null;
                    Skin.PerSectorShadow = false;
                    Skin.InnerGlassRing = false;
                    Skin.BackdropBlur = false;
                    Skin.BaseDisc = true;
                    Skin.LineThickness = 0.9;
                    break;
            }
        }

        /// <summary>投影只建一次、冻结复用 —— 悬停是高频路径，别每次都 new Effect。</summary>
        static DropShadowEffect Glow(Color color, double blur, double opacity, double depth = 0)
        {
            var e = new DropShadowEffect
            {
                Color = color,
                BlurRadius = blur,
                ShadowDepth = depth,
                Direction = 270,
                Opacity = opacity
            };
            e.Freeze();
            return e;
        }

        static readonly FontFamily UiFont = UiTheme.Font;

        struct Ring
        {
            public double R0, R1, Cr;
            public int From, Count;
            public double Start;     // 起始角（度），内环要错开半格，否则缝连成放射状暗条
        }

        sealed class Item
        {
            public string Name;      // 完整图层名
            public string Prefix;    // 第一段，例如 E-04
            public string Suffix;    // 剩下的，例如 造型线
            public Color Color;
            public TextBlock Label;  // 名字那一行，悬停时要提亮
            public TextBlock Code;   // 编号那一行，悬停时也要提亮（蓝底上深灰编号基本看不见）
            public Border Dot;       // 色点，悬停时恢复饱和度
        }

        readonly Canvas _root = new Canvas();
        readonly Canvas _wheel = new Canvas();          // 所有扇区 + 中心，整体投一次影
        readonly List<Item> _items = new List<Item>();
        readonly List<Path> _paths = new List<Path>();
        readonly List<Ring> _rings = new List<Ring>();

        TextBlock _hubName;
        TextBlock _hubHint;
        bool _hubTwoLine;             // 双环时中心太小，只放得下一行
        double _cx, _cy;
        int _hover = -1;

        // 记一份最近一次 Build 的入参：毛玻璃判定出"浅底"之后要按新配色重画一遍
        IList<string> _lastNames;
        IList<int> _lastColors;

        public PieWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;          // 绝不能抢走命令行的焦点
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;       // 鼠标事件全靠钩子，窗口本身不接收
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            Content = _root;
            _root.Children.Add(_wheel);

            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                    ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
            };
        }

        // ------------------------------------------------------------------ 构建

        /// <summary>按图层池重建扇区。<paramref name="names"/> 至少两个，否则不该调到这里。</summary>
        public void Build(IList<string> names, IList<int> colorValues)
        {
            _root.Children.Clear();
            _root.Children.Add(_wheel);
            _wheel.Children.Clear();
            _wheel.Effect = null;
            _items.Clear();
            _paths.Clear();
            _rings.Clear();
            _hover = -1;
            _hubName = null;
            _hubHint = null;

            int n = names == null ? 0 : names.Count;
            if (n < 2) return;

            _lastNames = names;
            _lastColors = colorValues;

            for (int i = 0; i < n; i++)
            {
                string full = names[i] ?? "";
                string pre = full, suf = "";

                // 先按空格拆：E-04 立面造型线 → 「E-04」+「立面造型线」
                int sp = full.IndexOf(' ');
                if (sp > 0 && sp < full.Length - 1)
                {
                    pre = full.Substring(0, sp);
                    suf = full.Substring(sp + 1).Trim();
                }
                else
                {
                    // 没空格也得拆。图层名常写成「E-05立面造型内分割线」，
                    // 不拆的话扇区里会把这串长名字上下重复印两遍，还横着溢出扇区
                    // （用户截图里左上那格就是这样）。
                    int cut = LeadingCodeLength(full);
                    if (cut > 0 && cut < full.Length)
                    {
                        pre = full.Substring(0, cut);
                        suf = full.Substring(cut).Trim();
                    }
                }

                Color col = Color.FromRgb(0x8A, 0x8A, 0x8A);
                if (colorValues != null && i < colorValues.Count)
                    col = ToMediaColor(colorValues[i]);

                _items.Add(new Item { Name = full, Prefix = pre, Suffix = suf, Color = col });
            }

            // 起始角从 0 起（第一格的中心落在半格处）。
            // 1.6 试过「往回转半格、让第一个图层正对 12 点」，用户看过之后说变丑了，
            // 所以还原成原来的角度 —— 别自作聪明动这里。
            if (n <= 9)
            {
                _rings.Add(new Ring
                {
                    R0 = S_R0, R1 = S_R1, Cr = CrSingle, From = 0, Count = n, Start = 0
                });
            }
            else
            {
                const int outer = 8;
                double outerStep = 360.0 / outer;
                _rings.Add(new Ring
                {
                    R0 = O_R0, R1 = O_R1, Cr = CrOuter, From = 0, Count = outer,
                    Start = 0
                });
                // 内环错开外环半格：缝不会从中心一直连到盘边，两圈读起来是两层而不是一张网
                _rings.Add(new Ring
                {
                    R0 = I_R0, R1 = I_R1, Cr = CrInner, From = outer, Count = n - outer,
                    Start = outerStep / 2
                });
            }

            double rmax = 0;
            foreach (Ring r in _rings) rmax = Math.Max(rmax, r.R1);

            double side = (rmax + Pad) * 2;
            Width = side;
            Height = side;
            _root.Width = side;
            _root.Height = side;
            _wheel.Width = side;
            _wheel.Height = side;
            _cx = side / 2;
            _cy = side / 2;

            // 整体投一次影（每套皮肤的松紧不一样，参数在 ApplySkin 里）
            // 深/白盘整盘投一层影；毛玻璃靠整盘一层大影托起来，但格与格之间不各投
            _wheel.Effect = Skin.PerSectorShadow ? null : Skin.Shell;

            // 底圆：跟扇区同色，把缝填死。少了它，浅色图纸上每条缝都会透出一道白光，
            // 整盘看着像被切开了。毛玻璃不垫这层 —— 缝里透出模糊的图纸才是玻璃。
            if (Skin.BaseDisc)
            {
                var baseDisc = new Ellipse
                {
                    Width = rmax * 2,
                    Height = rmax * 2,
                    Fill = new SolidColorBrush(Skin.Bg)
                };
                Canvas.SetLeft(baseDisc, _cx - rmax);
                Canvas.SetTop(baseDisc, _cy - rmax);
                _wheel.Children.Add(baseDisc);
            }

            // 中心圆半径要在画扇区之前定下来：算文字宽度时要用它当"内边界"
            double hubR = _rings[_rings.Count - 1].R0 - 12;
            if (hubR < 24) hubR = 24;

            foreach (Ring rg in _rings)
            {
                double step = 360.0 / rg.Count;
                double mid = (rg.R0 + rg.R1) / 2;
                double fs = FontFor(rg);
                bool twoLine = (rg.R1 - rg.R0) >= 40;
                // 内环格子多的时候横向很挤，把色点让出来给文字
                bool withDot = !(rg.R0 < 45 && rg.Count > 5);

                // 每一格能放多宽的文字先算出来，再让整圈统一取"最短的那个"字号。
                // 一格一格各自缩会大小不一，看着像出错。
                double halfH = twoLine ? fs + 4 : fs * 0.72;
                var widths = new double[rg.Count];
                for (int j = 0; j < rg.Count; j++)
                {
                    double a = rg.Start + j * step + step / 2;
                    widths[j] = LabelMaxWidth(step, a, mid, halfH, hubR);
                }
                fs = FitRingFont(rg, fs, widths, withDot);
                // 压到 9.5 还塞不下，就把色点交出去换 9px 宽度。
                // 满盘 16 格时正东/正西那两格的径向余量只有 19px，一个 5px 的圆点
                // 正好卡在「辅…」和「辅助线」之间。名字比色点重要，先保名字。
                if (withDot && !RingFits(rg, fs, widths, withDot))
                {
                    withDot = false;
                    fs = FitRingFont(rg, FontFor(rg), widths, withDot);
                }

                for (int j = 0; j < rg.Count; j++)
                {
                    int idx = rg.From + j;
                    if (idx >= _items.Count) break;

                    Item it = _items[idx];
                    // 角度交给 SectorGeometry 处理：缝隙和圆角都是它的活，
                    // 这里只给原始的格边界（0° 朝上 → 减 90° 换成 StarPie 的正东约定）
                    double raw0 = rg.Start + j * step;
                    double raw1 = rg.Start + (j + 1) * step;

                    var p = new Path
                    {
                        Data = SectorGeometry.CreateAdvancedSectorGeometry(
                            _cx, _cy, raw0 - 90, raw1 - 90, rg.R0, rg.R1, "Original", GapPx, rg.Cr),
                        Fill = new SolidColorBrush(Skin.Bg),
                        Stroke = new SolidColorBrush(Skin.Line),
                        StrokeThickness = Skin.LineThickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        IsHitTestVisible = false
                    };
                    if (Skin.PerSectorShadow) p.Effect = Skin.Idle;
                    _wheel.Children.Add(p);
                    _paths.Add(p);

                    double ang = rg.Start + j * step + step / 2;
                    Point tp = Polar(_cx, _cy, mid, ang);

                    AddLabelBlock(tp, it, fs, twoLine, withDot, widths[j]);
                }
            }

            // ---- 中心：跟扇区同一套材质，不用挖坑（hubR 上面已经算过了）
            _hubTwoLine = hubR >= 34;

            var hub = new Ellipse
            {
                Width = hubR * 2,
                Height = hubR * 2,
                Fill = new SolidColorBrush(Skin.Bg),
                Stroke = new SolidColorBrush(Skin.Line),
                StrokeThickness = Skin.LineThickness
            };
            Canvas.SetLeft(hub, _cx - hubR);
            Canvas.SetTop(hub, _cy - hubR);
            _wheel.Children.Add(hub);

            // 毛玻璃在中心外面再描一圈更淡的玻璃环（StarPie 的 Deco_InnerGlassRing）
            if (Skin.InnerGlassRing)
            {
                double ringR = hubR + 2;
                var ring = new Ellipse
                {
                    Width = ringR * 2,
                    Height = ringR * 2,
                    Fill = Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x23, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 0.8,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ring, _cx - ringR);
                Canvas.SetTop(ring, _cy - ringR);
                _wheel.Children.Insert(_wheel.Children.IndexOf(hub), ring);
            }

            _hubName = new TextBlock
            {
                Text = "取消",
                FontFamily = UiFont,
                FontSize = _hubTwoLine ? 11.5 : 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Skin.Hub),
                TextAlignment = TextAlignment.Center,
                Width = hubR * 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };
            _hubName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (Skin.LabelShadow != null) _hubName.Effect = Skin.LabelShadow;

            _hubHint = new TextBlock
            {
                Text = "松手在中间",
                FontFamily = UiFont,
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Skin.HubHint),
                TextAlignment = TextAlignment.Center,
                Width = hubR * 2,
                IsHitTestVisible = false
            };
            _hubHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (Skin.LabelShadow != null) _hubHint.Effect = Skin.LabelShadow;

            if (_hubTwoLine)
            {
                Place(_hubName, _cx - hubR, _cy - 13);
                Place(_hubHint, _cx - hubR, _cy + 3);
            }
            else
            {
                // 双环时中心只有 ~64px 宽，两行挤成一团，干脆只放一行
                _hubHint.Visibility = Visibility.Collapsed;
                Place(_hubHint, _cx - hubR, _cy + 2);
                Place(_hubName, _cx - hubR, _cy - _hubName.DesiredSize.Height / 2);
            }

            PlayEnter();
        }

        static double FontFor(Ring rg)
        {
            if (rg.Count <= 4) return 12.5;
            if (rg.Count <= 8) return 11.5;
            return rg.R0 < 45 ? 9.5 : 10.5;
        }

        /// <summary>
        /// 名字开头那段「编号」的长度（字母 / 数字 / - _ . 组成，例如 E-05、PUB_TEXT、A1）。
        /// 返回 0 表示不值得拆 —— 整串都是编号，或者开头就是中文。
        /// </summary>
        static int LeadingCodeLength(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;

            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                bool code = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                            || (c >= '0' && c <= '9')
                            || c == '-' || c == '_' || c == '.';
                if (!code) break;
                i++;
            }

            if (i >= s.Length) return 0;    // 整串都是编号（如 PUB_TEXT），拆了反而劈成两半
            if (i < 2) return 0;            // 单个字符多半是名字的一部分
            return i;
        }

        /// <summary>
        /// 一格里的文字最多能占多宽。
        ///
        /// 做法很笨但很准：从中心往两边一档档加宽，直到标签的某个角**戳出外圆**
        /// 或者**越过相邻格子的分界线**就停。长图层名横着戳到圆盘外面，就是这么来的
        /// （用户截图里「E-05立面造型内分割线」直接压到桌面图标上）。
        ///
        /// 内边界用的是**中心圆**（R0-12），不是扇区内沿：中心圆和扇区之间本来就有
        /// 一圈 12px 的缝，文字压进那条缝不显脏，戳进中心圆才难看。
        /// </summary>
        double LabelMaxWidth(double step, double ang, double mid, double halfH, double hubR)
        {
            double theta = (ang - 90) * Math.PI / 180.0;
            double bx = mid * Math.Cos(theta);
            double by = mid * Math.Sin(theta);
            double halfStep = step / 2.0;

            double best = 8;
            for (double a = 10; a < mid * 2; a += 2)
            {
                if (!BoxFits(bx, by, a, halfH, ang, halfStep, hubR)) break;
                best = a;
            }
            return best * 2;
        }

        bool BoxFits(double bx, double by, double a, double b, double ang, double halfStep, double hubR)
        {
            // 外圆留 2px 富余：这里卡的是**文字块的包围盒**，行高里那段上下留白
            // 本来就没有笔画，真让它压到外沿上也看不出来。
            double rOuter = _rings[0].R1 + 2;
            b *= 0.85;

            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    double px = bx + sx * a;
                    double py = by + sy * b;

                    double r = Math.Sqrt(px * px + py * py);
                    if (r > rOuter) return false;
                    if (r < hubR + 2) return false;

                    // 相对格中心的角度差（0° 朝上、顺时针，跟 Polar 同一套）
                    double deg = Math.Atan2(py, px) * 180.0 / Math.PI + 90;
                    double off = deg - ang;
                    while (off > 180) off -= 360;
                    while (off < -180) off += 360;
                    if (Math.Abs(off) > halfStep - 2.5) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 整圈统一字号：从大往小试，第一个"每一格都塞得下"的字号就是它。
        ///
        /// 塞得下的意思：短名字（≤5 字）要一行放完；长名字允许**对半折成两行**，
        /// 按折完之后最长的那半来算。45° 的格子里一行只放得下 4~5 个汉字，
        /// 与其截成「立面造型…」，不如折成「立面造型 / 内分割线」，一个字都不少。
        /// </summary>
        double FitRingFont(Ring rg, double startFs, double[] widths, bool withDot)
        {
            // 兜底到 9.5 而不是 10：满盘 16 格时外环在正东/正西那两格的径向余量
            // 只有 19px，三字名（辅助线）在 10 号字下差 3px 就被截成「辅…」。
            // 再小就糊了，9.5 是最后一个还能认的字号。
            for (double fs = startFs; fs >= 9.5; fs -= 0.5)
                if (RingFits(rg, fs, widths, withDot)) return fs;
            return 9.5;
        }

        /// <summary>这一圈按 <paramref name="fs"/> 排下来，每一格是不是都塞得下。</summary>
        bool RingFits(Ring rg, double fs, double[] widths, bool withDot)
        {
            double dotW = withDot ? 9 : 0;
            for (int j = 0; j < rg.Count; j++)
            {
                int idx = rg.From + j;
                if (idx >= _items.Count) break;

                Item it = _items[idx];
                string txt = string.IsNullOrEmpty(it.Suffix) ? it.Prefix : it.Suffix;
                double budget = Math.Max(24, widths[j] - dotW);
                double need = txt.Length >= 4 ? HalfWidth(txt, fs) : TextWidth(txt, fs);
                if (need > budget) return false;
            }
            return true;
        }

        /// <summary>
        /// 对半折之后，最长那一半有多宽。折法必须跟 <see cref="BreakInHalf"/> 一模一样，
        /// 否则这里按 3+2 算、那边按 2+3 折，量出来的宽度对不上，文字就会被省略号咬掉。
        /// </summary>
        static double HalfWidth(string s, double fs)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return TextWidth(s, fs);
            int mid = SplitAt(s);
            double a = TextWidth(s.Substring(0, mid), fs);
            double b = TextWidth(s.Substring(mid), fs);
            return a > b ? a : b;
        }

        /// <summary>
        /// 名字从第几个字劈开。中文图层名几乎都是「定语 + 三字名词」的结构
        /// （立面造型线 / 建筑结构轮廓线 / 立面造型内分割线），所以短名固定留三字给后半行：
        /// 5 字折成 2+3、7 字折成 4+3；真按正中间劈会得到「立面造 / 型线」这种断词。
        /// 8 字以上再回到对半。
        /// </summary>
        static int SplitAt(string s)
        {
            int n = s.Length;
            int mid = n <= 7 ? n - 3 : n / 2;
            if (mid < 2) mid = 2;
            if (mid > n - 2) mid = n / 2;
            return mid;
        }

        /// <summary>折成两行（中文名没有词边界，按 <see cref="SplitAt"/> 劈最稳）。</summary>
        static string BreakInHalf(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 4) return s;
            int mid = SplitAt(s);
            return s.Substring(0, mid) + "\n" + s.Substring(mid);
        }

        /// <summary>裸文本宽度（不带 MaxWidth，否则永远量不出真实需求）。</summary>
        static double TextWidth(string text, double fs)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var probe = new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = fs,
                FontWeight = FontWeights.SemiBold
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }

        /// <summary>一格的内容：小圆点 + 名字（大字），下面一行小字是图层编号。</summary>
        void AddLabelBlock(Point center, Item it, double fs, bool twoLine, bool withDot, double maxW)
        {
            bool hasName = !string.IsNullOrEmpty(it.Suffix);
            string main = hasName ? it.Suffix : it.Prefix;

            // 一行放不下就折成两行。折了就不再画编号那行 —— 名字两行 + 编号一行
            // 三行叠起来比整个环还厚，格子放不下。
            // 门槛是 4 个字而不是 6：4 个字在窄格里本来就放不下（「立面造型线」这种
            // 5 字名最容易被截成「立面造…」），折开总比丢字强。
            double budget = Math.Max(24, maxW - (withDot ? 9 : 0));
            bool wrapped = main.Length >= 4 && TextWidth(main, fs) > budget;
            string shown = wrapped ? BreakInHalf(main) : main;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                IsHitTestVisible = false
            };

            if (withDot)
            {
                // 色点平时往灰里揉 45%：还是那个色相，但像墨，不像 LED 灯珠。
                const double dot = 5;
                var db = new Border
                {
                    Width = dot,
                    Height = dot,
                    CornerRadius = new CornerRadius(dot / 2),
                    Background = new SolidColorBrush(Soften(it.Color, 0.45)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                };
                it.Dot = db;
                row.Children.Add(db);
            }

            var tb = new TextBlock
            {
                Text = shown,
                FontFamily = UiFont,
                FontSize = fs,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Skin.Name),
                VerticalAlignment = VerticalAlignment.Center
            };
            // 毛玻璃压在浅色图纸上时，白字全靠这层暗影托住（深/白盘不需要）
            if (Skin.LabelShadow != null) tb.Effect = Skin.LabelShadow;

            // 兜底：字号已经压到 9 还塞不下（名字实在太长）就省略号。
            // 宁可少几个字，也不能让文字横着戳出圆盘。
            tb.MaxWidth = budget;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.TextAlignment = TextAlignment.Center;

            it.Label = tb;
            row.Children.Add(tb);

            if (twoLine && hasName && !wrapped)
            {
                // 名字 + 编号两行，整体作为一个块居中
                var stack = new StackPanel { IsHitTestVisible = false };
                stack.Children.Add(row);

                var code = new TextBlock
                {
                    Text = it.Prefix,
                    FontFamily = UiFont,
                    FontSize = fs - 2.5,
                    Foreground = new SolidColorBrush(Skin.Code),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0),
                    IsHitTestVisible = false
                };
                if (Skin.LabelShadow != null) code.Effect = Skin.LabelShadow;
                it.Code = code;
                stack.Children.Add(code);
                CenterAt(stack, center, hasName && !wrapped ? -2 : 0);
            }
            else
            {
                CenterAt(row, center, 0);
            }
        }

        /// <summary>把一个元素按中心点摆好（先量再定位）。</summary>
        void CenterAt(FrameworkElement el, Point center, double dy)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(el, center.X - el.DesiredSize.Width / 2, center.Y - el.DesiredSize.Height / 2 + dy);
        }

        void Place(UIElement el, double x, double y)
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            _root.Children.Add(el);
        }

        /// <summary>出来的时候轻微放大 + 淡入，跟演示页的 pieIn 一致。</summary>
        void PlayEnter()
        {
            var scale = new ScaleTransform(0.92, 0.92);
            _root.RenderTransformOrigin = new Point(0.5, 0.5);
            _root.RenderTransform = scale;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var ms = TimeSpan.FromMilliseconds(150);

            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.92, 1.0, ms) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.92, 1.0, ms) { EasingFunction = ease });
            _root.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120)));
        }

        // ------------------------------------------------------------------ 交互

        /// <summary>圆盘窗口本地坐标（相对左上角）落在哪一格；-1 表示中心/空隙，即取消。</summary>
        public int HitTestLocal(Point local)
        {
            double dx = local.X - _cx;
            double dy = local.Y - _cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            Ring hit = default(Ring);
            bool found = false;
            foreach (Ring rg in _rings)
            {
                // 容差往两边放一点：贴着边按下去也算选中，别让人"怎么点都没反应"
                if (dist >= rg.R0 - 3 && dist <= rg.R1 + 8) { hit = rg; found = true; break; }
            }
            if (!found) return -1;

            double deg = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (deg < 0) deg += 360;
            deg -= hit.Start;
            if (deg < 0) deg += 360;

            double step = 360.0 / hit.Count;
            int j = (int)Math.Floor(deg / step);
            if (j < 0) j = 0;
            if (j >= hit.Count) j = hit.Count - 1;

            int idx = hit.From + j;
            return idx < _items.Count ? idx : -1;
        }

        /// <summary>高亮某一格，-1 表示回到"取消"。</summary>
        public void Hover(int index)
        {
            if (index == _hover) return;
            _hover = index;

            for (int i = 0; i < _paths.Count; i++)
            {
                bool on = i == index;
                Item it = _items[i];

                // 整格实色 + 亮边 + 光晕，这是 StarPie 的高亮做法：
                // 只亮一格，所以饱和色不会糊成一片；光晕让人一眼看出"松手就是它"。
                Color fill = on ? Skin.AccentFill : Skin.Bg;
                _paths[i].Fill = new SolidColorBrush(fill);
                _paths[i].Stroke = new SolidColorBrush(on ? Skin.AccentLine : Skin.Line);
                _paths[i].Effect = on ? Skin.Glow : Skin.Idle;

                if (it.Label != null)
                {
                    it.Label.Foreground = new SolidColorBrush(on ? Colors.White : Skin.Name);
                    it.Label.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
                }
                if (it.Code != null)
                {
                    // 编号那行压在蓝底上：深灰会直接糊掉，跟着提亮
                    it.Code.Foreground = new SolidColorBrush(on ? Skin.CodeHot : Skin.Code);
                }
                if (it.Dot != null)
                {
                    // 指到哪一格，那一格的色点才"上色"；其余保持哑光
                    it.Dot.Background = new SolidColorBrush(
                        on ? Lighten(it.Color) : Soften(it.Color, 0.45));
                }
            }

            if (_hubName == null || _hubHint == null) return;

            if (index >= 0 && index < _items.Count)
            {
                Item it = _items[index];
                bool hasName = !string.IsNullOrEmpty(it.Suffix);

                _hubName.Text = hasName ? it.Suffix : it.Prefix;
                _hubName.FontSize = _hubTwoLine ? 11.5 : 10.5;
                // 中心底色不跟着悬停变（只有那一格变蓝），所以这行大字不能写死白色 ——
                // 白盘上白字就是"隐身"，之前的探针里能看到一团鬼影
                _hubName.Foreground = new SolidColorBrush(Skin.HubHot);

                _hubHint.Text = hasName ? it.Prefix : it.Name;
                // 强调色已经是蓝的了，中心这行小字就保持中性，别再掺图层色
                _hubHint.Foreground = new SolidColorBrush(Skin.HubHintHot);
            }
            else
            {
                _hubName.Text = "取消";
                _hubName.FontSize = _hubTwoLine ? 11.5 : 10.5;
                _hubName.Foreground = new SolidColorBrush(Skin.Hub);
                _hubHint.Text = "松手在中间";
                _hubHint.Foreground = new SolidColorBrush(Skin.HubHint);
            }

            if (!_hubTwoLine)
            {
                // 单行模式：字号会变，重新按中心摆一次
                _hubName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetTop(_hubName, _cy - _hubName.DesiredSize.Height / 2);
            }
        }

        public int Hovered { get { return _hover; } }

        public int Count { get { return _items.Count; } }

        /// <summary>
        /// Show 之前把圆盘摆好 + 抓底。
        ///
        /// 这里**不能**用 <see cref="CenterAtScreen"/>：PointFromScreen 要求窗口已经连上
        /// PresentationSource（也就是 Show 过），没 Show 之前一律抛
        /// 「此 Visual 未连接到 PresentationSource」。以前 OpenAt 直接调它，
        /// 异常被 catch 吞掉、返回 false —— 圆盘就永远弹不出来，而且一声不响。
        /// </summary>
        public void PrepareAt(int screenX, int screenY)
        {
            PlaceAtScreen(screenX, screenY);
            CaptureBackdrop(screenX, screenY);
        }

        /// <summary>按屏幕物理像素摆位置（不依赖 PresentationSource，Show 之前也能用）。</summary>
        void PlaceAtScreen(int screenX, int screenY)
        {
            double sx = _lastScale, sy = _lastScale;
            if (sx <= 0.1) sx = 1.0;
            if (sy <= 0.1) sy = 1.0;

            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0.1 && dpi.DpiScaleY > 0.1)
                {
                    sx = dpi.DpiScaleX;
                    sy = dpi.DpiScaleY;
                }
            }
            catch { }

            Left = screenX / sx - Width / 2;
            Top = screenY / sy - Height / 2;
        }

        /// <summary>
        /// 把圆盘中心摆到屏幕上的某个物理像素点（精确版）。必须在 Show 之后调。
        ///
        /// **踩过的坑，别再犯**：<c>PointFromScreen</c> 返回的是**窗口客户区坐标**，
        /// 不是屏幕坐标。老代码把它当屏幕坐标用（<c>Left = p.X - Width/2</c>），
        /// 只要窗口本来就在正确位置，算出来必然是 0 —— 于是圆盘每次都跳到屏幕左上角，
        /// 还被屏幕边缘切掉一半（1.4 里就是这个表现）。
        ///
        /// 正确做法是**增量**：p 是"那个屏幕点现在落在窗口里的哪个位置"，
        /// 它离窗口中心差多少，窗口就反向挪多少。这样跟当前摆在哪、DPI 是多少都无关。
        /// </summary>
        public void CenterAtScreen(int screenX, int screenY)
        {
            try
            {
                double w = double.IsNaN(Width) ? ActualWidth : Width;
                double h = double.IsNaN(Height) ? ActualHeight : Height;
                if (w <= 0 || h <= 0) return;

                Point p = PointFromScreen(new Point(screenX, screenY));
                Left += p.X - w / 2;
                Top += p.Y - h / 2;

                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0.1) _lastScale = dpi.DpiScaleX;
            }
            catch
            {
                PlaceAtScreen(screenX, screenY);
            }
        }

        // ------------------------------------------------------------------ 毛玻璃底

        /// <summary>
        /// 抓一帧圆盘底下的屏幕，糊掉，垫在扇区下面。
        ///
        /// 为什么非抓不可：圆盘是逐像素透明的窗口，半透明扇区底下就是**锐利的**图纸，
        /// 白线会直接穿字（实测"门窗""取消"只要有根线压过去就散了）。DWM 的毛玻璃
        /// (ACCENT_ENABLE_ACRYLICBLURBEHIND) 要求窗口不开 AllowsTransparency，跟圆盘
        /// 要的四角透明冲突，所以只能自己抓屏 + BlurEffect。
        ///
        /// 必须在 Show() 之前调：窗口一旦可见，抓到的就是圆盘自己。
        /// </summary>
        void CaptureBackdrop(int screenX, int screenY)
        {
            if (!Skin.BackdropBlur) return;
            if (IsVisible) return;                      // 已经显示着就别再抓（会抓到自己）

            IntPtr bmp = IntPtr.Zero;
            try
            {
                int left, top, pw, ph;
                if (!WindowRectPhysical(screenX, screenY, out left, out top, out pw, out ph)) return;
                if (pw <= 0 || ph <= 0) return;

                IntPtr screen = Native.GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return;

                IntPtr mem = Native.CreateCompatibleDC(screen);
                bmp = Native.CreateCompatibleBitmap(screen, pw, ph);
                if (mem == IntPtr.Zero || bmp == IntPtr.Zero)
                {
                    if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                    return;
                }

                IntPtr old = Native.SelectObject(mem, bmp);
                // 不带 CAPTUREBLT：那会把分层窗口（包括光标）一起抓进来
                bool ok = Native.BitBlt(mem, 0, 0, pw, ph, screen, left, top, Native.SRCCOPY);
                Native.SelectObject(mem, old);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);

                if (!ok) return;

                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                Native.DeleteObject(bmp);
                bmp = IntPtr.Zero;

                // 以前这里会按底图亮度切深/浅两套玻璃配色。
                // 1.6 收尾用户说"颜色也只保留深"，就把这一步去掉了 ——
                // 少一次重画，也少一种"这块怎么突然变白了"的意外。
                ShowFrost(src);
            }
            catch
            {
                // 抓不到就抓不到：圆盘照弹，只是没有那么"毛"而已
            }
            finally
            {
                if (bmp != IntPtr.Zero) { try { Native.DeleteObject(bmp); } catch { } }
            }
        }

        /// <summary>
        /// 圆盘窗口在屏幕上的物理像素矩形。
        ///
        /// 优先问 GetWindowRect：那是 DWM 眼里的真实位置和大小，混合 DPI 多屏也不会错。
        /// 但窗口还没 Show 过时，WPF 可能还没把位置推下去 —— 所以拿"矩形中心对不对得上
        /// 给定的那个屏幕点"验一下，对不上就退回按 DPI 算。
        /// </summary>
        bool WindowRectPhysical(int screenX, int screenY, out int left, out int top, out int pw, out int ph)
        {
            left = top = pw = ph = 0;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Native.RECT r;
            if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out r) && r.Width > 0 && r.Height > 0)
            {
                int cx = r.Left + r.Width / 2;
                int cy = r.Top + r.Height / 2;
                if (Math.Abs(cx - screenX) <= 4 && Math.Abs(cy - screenY) <= 4)
                {
                    left = r.Left; top = r.Top; pw = r.Width; ph = r.Height;
                    return true;
                }
            }

            DpiScale dpi;
            try { dpi = VisualTreeHelper.GetDpi(this); }
            catch { return false; }

            pw = (int)Math.Round(Width * dpi.DpiScaleX);
            ph = (int)Math.Round(Height * dpi.DpiScaleY);
            // 窗口中心就是给定的那个屏幕点，所以左上角 = 中心 - 半个窗口
            left = screenX - pw / 2;
            top = screenY - ph / 2;
            return pw > 0 && ph > 0;
        }

        /// <summary>抓到的底图是浅色还是深色（Rec.601 亮度，0~255，隔 8 个像素采一个点）。</summary>
        static bool IsLightBackdrop(BitmapSource src)
        {
            try
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                if (w <= 0 || h <= 0) return false;

                int stride = w * 4;
                var buf = new byte[stride * h];
                conv.CopyPixels(buf, stride, 0);

                long sum = 0;
                int n = 0;
                for (int y = 0; y < h; y += 8)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += 8)
                    {
                        int i = row + x * 4;      // Bgra32：+0 B、+1 G、+2 R
                        sum += (buf[i + 2] * 299 + buf[i + 1] * 587 + buf[i] * 114) / 1000;
                        n++;
                    }
                }
                // 140 大约就是"中灰往上"：AutoCAD 的 #212830 是 40，白图纸是 250，隔得很远
                return n > 0 && sum / n > 140;
            }
            catch { return false; }
        }

        void ShowFrost(BitmapSource src)
        {
            double rmax = 0;
            foreach (Ring r in _rings) rmax = Math.Max(rmax, r.R1);
            if (rmax <= 0) return;

            var blur = new BlurEffect { Radius = Skin.BlurRadius, KernelType = KernelType.Gaussian };
            blur.Freeze();

            var img = new Image
            {
                Source = src,
                Width = Width,
                Height = Height,
                Stretch = Stretch.Fill,
                Effect = blur,
                IsHitTestVisible = false
            };

            // 模糊要连窗一起裁成圆：Clip 放在外层容器上，直接放在 Image 上的话
            // 会先裁后模糊，圆边上还是会渗出方块
            var frost = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
            frost.Children.Add(img);
            frost.Clip = new EllipseGeometry(new Point(_cx, _cy), rmax, rmax);

            _root.Children.Insert(0, frost);    // 0 号是 _wheel，插在它前面就是垫在底下
        }

        /// <summary>屏幕物理坐标 → 窗口本地坐标。</summary>
        public Point LocalFromScreen(int screenX, int screenY)
        {
            try { return PointFromScreen(new Point(screenX, screenY)); }
            catch { return new Point(-1, -1); }
        }

        // ------------------------------------------------------------------ 几何

        static Point Polar(double cx, double cy, double r, double deg)
        {
            double a = (deg - 90) * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        /// <summary>
        /// 把 ACI 的纯色往灰里揉。CAD 的索引色大量是 (255,0,0)/(0,255,255) 这种满饱和，
        /// 原样铺到深色盘面上会像 LED 灯板；揉一下还是那个色相，但不再刺眼。
        /// <paramref name="k"/> 越大越灰。
        /// </summary>
        static Color Soften(Color c, double k = 0.3)
        {
            const double g = 0x88;
            return Color.FromRgb(
                (byte)(c.R * (1 - k) + g * k),
                (byte)(c.G * (1 - k) + g * k),
                (byte)(c.B * (1 - k) + g * k));
        }

        /// <summary>往白里提一点，让饱和色当小字时也看得清。</summary>
        static Color Lighten(Color c)
        {
            const double k = 0.42;
            return Color.FromRgb(
                (byte)Math.Min(255, c.R + (255 - c.R) * k),
                (byte)Math.Min(255, c.G + (255 - c.G) * k),
                (byte)Math.Min(255, c.B + (255 - c.B) * k));
        }

        static Color ToMediaColor(int colorValue)
        {
            if (LayerColorCode.IsRgb(colorValue))
                return Color.FromRgb(LayerColorCode.R(colorValue),
                                     LayerColorCode.G(colorValue),
                                     LayerColorCode.B(colorValue));

            return AciPalette.Get((short)(LayerColorCode.IsAci(colorValue) ? colorValue : 7));
        }
    }
}
