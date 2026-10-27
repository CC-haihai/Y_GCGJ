using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AutoLayer.Core;

namespace AutoLayer.UI
{
    /// <summary>
    /// 点状态条上的配置名弹出来的那份清单 —— **自己挑一个**，不是一下一下轮。
    ///
    /// 为什么不用 WPF 的 Popup / ContextMenu：
    ///   · Popup 会自己抢鼠标捕获、自己管关闭，出问题时是"整个 CAD 点不动"，代价太大；
    ///   · 这里要的只是"贴着一个 29px 高的小标签往下挂一列"，自己写反而更好控位置和配色。
    ///
    /// 关闭时机：点清单外面（60ms 轮询左键，见 <see cref="Watch"/>）、
    /// 或者再点一次状态条上的配置名（toggle）、或者自动归层被关掉。
    ///
    /// 窗口是 <c>WS_EX_NOACTIVATE</c>：点它**不会**把焦点从 CAD 抢走，
    /// 所以画到一半（命令进行中）也能放心点开换个配置，不会把当前命令打断。
    /// </summary>
    internal static class ProfileMenu
    {
        static ProfileMenuWindow _win;

        /// <summary>上一次 <see cref="Show"/> 失败的原因（成功就清空）。出问题时能说清"为什么没弹出来"。</summary>
        public static string LastError { get; private set; }

        public static bool IsOpen { get { return _win != null; } }

        /// <summary>点配置名：开着就关，没开就弹。</summary>
        public static void Toggle(Window anchor, FrameworkElement chip)
        {
            if (_win != null) { Close(); return; }
            Show(anchor, chip);
        }

        /// <param name="anchor">状态条窗口（用来定上下位置、当 Owner）。</param>
        /// <param name="chip">配置名那一格（保留参数：探针/将来可能用，摆位本身已不看它）。</param>
        public static void Show(Window anchor, FrameworkElement chip)
        {
            Close();
            if (anchor == null) return;
            try
            {
                var w = new ProfileMenuWindow();
                w.Build(anchor, chip);

                // 用户原话：「如果上面的胶囊不够长应该随着展开跟下面一样宽」。
                // 顺序不能反：先把胶囊撑到清单的自然宽度（撑完会重新居中），
                // 再按撑开后的胶囊居中摆清单 —— 否则清单会按旧宽度算，偏一边。
                var hud = anchor as HudWindow;
                if (hud != null)
                {
                    hud.WidenTo(w.NaturalCardWidth());
                    try { anchor.UpdateLayout(); } catch { }
                }

                w.PlaceUnder(anchor);
                w.Show();
                w.PlayEnter();
                _win = w;
                LastError = null;
            }
            catch (Exception ex)
            {
                _win = null;
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Close()
        {
            ProfileMenuWindow w = _win;
            _win = null;
            if (w == null) return;
            try { w.Close(); } catch { }
            // 收起来时胶囊回到自然宽度（不然会一直横着一条）
            try { w.ResetAnchorWidth(); } catch { }
        }
    }

    /// <summary>那份清单本体。纯代码构建，没有 XAML —— 少一处 BAML 坑。</summary>
    internal sealed class ProfileMenuWindow : Window
    {
        /// <summary>整块卡片外圈留 8px 给阴影（BlurRadius 10 撑出来的量）；阴影区是透明的，点上去不算点到清单。</summary>
        const double ShadowPad = 8;

        readonly DispatcherTimer _watch;
        readonly List<Border> _items = new List<Border>();   // 每条配置对应的那一行（探针要按名字点它）
        Border _root;    // 含阴影留白的外层（动画作用在它身上）
        Border _card;
        FrameworkElement _chip;   // 配置名那一格：清单以它为准展开
        Window _anchor;
        bool _wasDown;

        public ProfileMenuWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;      // 不抢焦点
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            // 左键轮询：点清单外面就收起来。
            // 不用鼠标钩子（圆盘那套）是因为这里不需要"吞掉"这一下 ——
            // 点画图区就该让 CAD 收到，用户只是在关掉一个浮层而已。
            _watch = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _watch.Tick += (s, e) => Watch();

            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                    ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            };

            Closed += (s, e) => { try { _watch.Stop(); } catch { } };
        }

        // ---------------------------------------------------------------- 构建

        /// <summary>按当前配置列表把整份清单搭出来。每次弹出都重建，不做增量更新。</summary>
        public void Build(Window anchor, FrameworkElement chip)
        {
            _anchor = anchor;
            _chip = chip;

            // 认状态条当"主人"：附属窗口永远压在主人上面，
            // 而状态条本身已经挂在 CAD 主窗口下面了 —— 所以清单一定看得见。
            // 不这么写的话，CAD 在前台时新窗口有可能被排到它后面，表现就是"点了没反应"。
            try { if (anchor != null) Owner = anchor; } catch { }

            List<string> names = ConfigStore.ProfileNames();
            Config cfg = ConfigStore.Current;
            string active = cfg == null ? "" : (cfg.ActiveProfile ?? "");

            var col = new StackPanel { Orientation = Orientation.Vertical };
            _items.Clear();
            foreach (string name in names)
            {
                bool cur = string.Equals(name, active, StringComparison.OrdinalIgnoreCase);
                Border item = MakeItem(name, cur);
                _items.Add(item);
                col.Children.Add(item);
            }
            if (col.Children.Count == 0)
            {
                col.Children.Add(new TextBlock
                {
                    Text = "没有配置",
                    FontFamily = UiTheme.Font,
                    FontSize = 12,
                    Foreground = UiTheme.B(UiTheme.TextFaint),
                    Margin = new Thickness(9, 6, 9, 6)
                });
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 240,          // 跟面板那个下拉框一样高（PanelWindow.xaml 的 PART_Popup）
                Content = col
            };

            // 这块板就是"面板里那个配置下拉框"的翻版，颜色/描边/内边距/行高全照它抄：
            //   PanelWindow.xaml → PART_Popup 的 Border：Background #26282F、BorderBrush #33FFFFFF、
            //   Padding 0,3、MaxHeight 240；下拉项 ComboBoxItem：Padding 9,6、Margin 3,1。
            // 圆角 6（用户定的），行 4 —— 跟面板原来那个下拉框一个规格。
            _card = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = UiTheme.B(UiTheme.LineStrong),
                Background = UiTheme.B(UiTheme.Raised),
                Padding = new Thickness(0, 3, 0, 3),
                Child = scroll,
                Effect = new DropShadowEffect
                {
                    // 跟状态条那条胶囊同一套阴影参数（BlurRadius 10 / Depth 2 / 0.45），
                    // 浮层和标签看起来才像一套东西。
                    BlurRadius = 10,
                    ShadowDepth = 2,
                    Opacity = 0.45,
                    Color = Colors.Black
                }
            };

            _root = new Border { Padding = new Thickness(ShadowPad), Child = _card };

            // 至少跟状态条一样宽 —— 清单一整块挂在条子下面，比它窄会显得错位；
            // 内容更长（配置名更长）就自己撑开。
            if (anchor != null && anchor.ActualWidth > 1)
                _root.MinWidth = anchor.ActualWidth + ShadowPad * 2;

            Content = _root;

            _wasDown = true;   // 打开它的那一下左键可能还按着，别立刻判成"点了外面"
            _watch.Start();
        }

        /// <summary>
        /// 一行配置。规格照面板的下拉项（PanelWindow.xaml 的 ComboBoxItem）：
        ///   内边距 9,6 · 外边距 3,1 · 悬停 #14FFFFFF · 选中 #2563EB + 白字。
        /// 圆角比卡片小一档（卡片 6 / 行 4），看起来是"卡片里嵌了一条"。
        /// </summary>
        Border MakeItem(string name, bool current)
        {
            Brush onText = UiTheme.B(Colors.White);

            var text = new TextBlock
            {
                Text = name,
                FontFamily = UiTheme.Font,
                FontSize = 12,                 // 面板基准字号
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = current ? onText : UiTheme.B(UiTheme.Text)
            };

            var mark = new TextBlock
            {
                Text = current ? "✓" : "",
                FontFamily = UiTheme.Font,
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = onText
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(mark, 1);
            grid.Children.Add(text);
            grid.Children.Add(mark);

            // 选中 = 面板下拉那个实心蓝；悬停 = 通用的 #14FFFFFF（选中的那行悬停时提亮一档）
            Brush rest = current ? UiTheme.B(UiTheme.Accent) : Brushes.Transparent;
            Brush hover = current ? UiTheme.B(UiTheme.AccentHi) : UiTheme.White(0x14);

            var item = new Border
            {
                CornerRadius = new CornerRadius(4),   // 比卡片小一档
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(3, 1, 3, 1),
                Background = rest,
                Cursor = Cursors.Hand,
                Child = grid
            };
            item.MouseEnter += (s, e) => item.Background = hover;
            item.MouseLeave += (s, e) => item.Background = rest;
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                Pick(name);
            };
            return item;
        }

        // ---------------------------------------------------------------- 展开动画

        /// <summary>
        /// 弹出来的动画 —— 数值跟圆盘 <c>PieWindow.PlayEnter</c> 完全一样：
        /// 0.92 起、150ms、CubicEase EaseOut，透明度 120ms 淡入。
        ///
        /// 差别只在缩放原点：圆盘是圆心，这里放在**顶部正中** ——
        /// 看着就像从状态条底下"拉"出来一列，而不是凭空出现在半空。
        /// </summary>
        public void PlayEnter()
        {
            try
            {
                var scale = new ScaleTransform(0.92, 0.92);
                // 缩放原点在顶部正中：看着像从状态条底下"拉"出来一列
                _root.RenderTransformOrigin = new Point(0.5, 0);
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
            catch { }
        }

        // ---------------------------------------------------------------- 位置

        /// <summary>收起清单时把胶囊收回自然宽度（撑宽是清单开的头，也得由它收尾）。</summary>
        public void ResetAnchorWidth()
        {
            var hud = _anchor as HudWindow;
            if (hud != null) hud.WidenTo(0);
        }

        /// <summary>
        /// 清单卡片"内容自然宽度"（**不含**那圈阴影留白）。
        /// 量的时候得先把 MinWidth 撤掉 —— 它平时被"至少跟状态条一样宽"撑着，量出来的是撑过的值。
        /// 状态条要按这个数撑宽（<see cref="StatusHud.WidenTo"/>），所以必须是纯内容宽度。
        /// </summary>
        public double NaturalCardWidth()
        {
            FrameworkElement root = Content as FrameworkElement;
            if (root == null || _root == null) return 0;
            double keep = _root.MinWidth;
            _root.MinWidth = 0;
            root.InvalidateMeasure();
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = root.DesiredSize.Width - ShadowPad * 2;
            _root.MinWidth = keep;
            return w > 0 ? w : 0;
        }

        /// <summary>
        /// 从**状态条底下**展开，并且**左右居中**：清单卡片的中心压在状态条中心上。
        ///
        /// 用户原话：「展开面板应该居中的」「如果上面的胶囊不够长应该随着展开跟下面一样宽」。
        /// 所以正常情况（胶囊已经撑到同宽）两边等宽、居中 = 齐平；
        /// 万一手动改宽度没撑成，也还是居中，不会偏到一边。
        ///
        /// 越界会夹回屏幕内；下面放不下就翻到状态条上面。
        /// 这里量的是**内容**而不是窗口：窗口还没 Show，量窗口会拿到模板前的尺寸。
        /// </summary>
        public void PlaceUnder(Window anchor)
        {
            FrameworkElement root = Content as FrameworkElement;
            if (root == null || anchor == null) return;
            root.InvalidateMeasure();
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = root.DesiredSize.Width;
            double h = root.DesiredSize.Height;
            if (w <= 0 || h <= 0) return;

            Rect wa = SystemParameters.WorkArea;

            // 窗口比卡片大一圈（ShadowPad 是给阴影留的透明边），
            // 扣掉它，卡片才是真正的宽/高；再让卡片中心对准状态条中心。
            double cardW = w - ShadowPad * 2;
            double left = anchor.Left + (anchor.ActualWidth - cardW) / 2 - ShadowPad;
            if (left + w > wa.Right - 8) left = wa.Right - w - 8;
            if (left < wa.Left + 8) left = wa.Left + 8;

            double top = anchor.Top + anchor.ActualHeight + 4 - ShadowPad;
            if (top + h > wa.Bottom - 8) top = anchor.Top - h - 4;
            if (top < wa.Top + 8) top = wa.Top + 8;

            Left = left;
            Top = top;
        }

        // ---------------------------------------------------------------- 选一个

        void Pick(string name)
        {
            ProfileMenu.Close();
            try
            {
                if (ConfigStore.SwitchProfile(name))
                {
                    // 状态条上闪一下，确认"刚才那下确实换成了这个"
                    StatusHud.Flash("配置 → " + name);
                    try { PanelHost.NotifyProfileChanged(); } catch { }
                }
                else
                {
                    StatusHud.Flash("已经是「" + name + "」");
                }
            }
            catch (Exception ex)
            {
                try { StatusHud.Flash("切配置失败：" + ex.GetType().Name); } catch { }
            }
        }

        // ---------------------------------------------------------------- 点外面就关

        void Watch()
        {
            bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (down && !_wasDown)
            {
                Native.POINT p;
                if (Native.GetCursorPos(out p) && !OnSelf(p))
                {
                    ProfileMenu.Close();
                    return;
                }
            }
            _wasDown = down;
        }

        /// <summary>
        /// 这个屏幕点算不算"点在自己身上"：
        /// 卡片本身（不含 6px 阴影圈）或者状态条那条 —— 点状态条是 toggle，交给它自己处理，
        /// 不然会"先关掉再被点开"。
        /// </summary>
        bool OnSelf(Native.POINT screenPt)
        {
            try
            {
                if (_card != null && _card.IsVisible)
                {
                    Point origin = _card.PointToScreen(new Point(0, 0));
                    if (screenPt.X >= origin.X && screenPt.X <= origin.X + _card.ActualWidth &&
                        screenPt.Y >= origin.Y && screenPt.Y <= origin.Y + _card.ActualHeight)
                        return true;
                }
            }
            catch { }

            if (_anchor != null)
            {
                try
                {
                    Native.RECT r;
                    IntPtr h = new WindowInteropHelper(_anchor).Handle;
                    if (h != IntPtr.Zero && Native.GetWindowRect(h, out r) &&
                        screenPt.X >= r.Left && screenPt.X <= r.Right &&
                        screenPt.Y >= r.Top && screenPt.Y <= r.Bottom)
                        return true;
                }
                catch { }
            }
            return false;
        }
    }
}
