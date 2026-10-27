using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AutoLayer.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoLayer.UI
{
    /// <summary>状态条要显示的一屏内容。</summary>
    internal sealed class HudState
    {
        public bool On;
        public string Profile = "";
        public string Layer = "";
        public Color LayerColor = Color.FromRgb(0x6E, 0xA8, 0xE0);
        public bool HasLayer;
    }

    /// <summary>
    /// 屏幕上的状态胶囊。回答的是"我现在到底开着自动归层没有、用的是哪个配置、下一条命令会落到哪一层"。
    ///
    /// 只读，不接收任何点击 —— 点不到就不会误触，也不会抢焦点。
    /// 位置跟着 CAD 主窗口的画图区走，最小化/切图纸都不会跑到屏幕外面。
    /// </summary>
    public static class StatusHud
    {
        static HudWindow _win;
        static DispatcherTimer _follow;
        static DispatcherTimer _flashTimer;
        static string _flashText;
        static bool _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            try { LayerEvents.Changed += OnLayerChanged; } catch { }

            _follow = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _follow.Tick += (s, e) => Tick();
            _follow.Start();
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;

            try { LayerEvents.Changed -= OnLayerChanged; } catch { }
            try { ProfileMenu.Close(); } catch { }     // 清单也是这个状态条的一部分，一起收
            if (_follow != null) { _follow.Stop(); _follow = null; }
            if (_flashTimer != null) { _flashTimer.Stop(); _flashTimer = null; }
            try { if (_win != null) { _win.Close(); _win = null; } } catch { }
        }

        static void OnLayerChanged()
        {
            // Raise 有可能来自别的线程，统一扔回界面线程
            try
            {
                if (_win != null && _win.Dispatcher != null && !_win.Dispatcher.CheckAccess())
                    _win.Dispatcher.BeginInvoke(new Action(Tick));
                else
                    Tick();
            }
            catch { }
        }

        /// <summary>闪一下，显示一条临时提示（例如"线段 → E-05 辅助线"），1.4 秒后回到常态。</summary>
        public static void Flash(string text)
        {
            try
            {
                _flashText = text;

                if (_flashTimer == null)
                {
                    _flashTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(1400)
                    };
                    _flashTimer.Tick += (s, e) =>
                    {
                        _flashTimer.Stop();
                        _flashText = null;
                        Tick();
                    };
                }
                _flashTimer.Stop();
                _flashTimer.Start();

                Tick();
            }
            catch { }
        }

        /// <summary>立刻按当前配置重画一次（改完设置后调）。</summary>
        public static void Refresh()
        {
            Tick();
        }

        /// <summary>
        /// 把某一条胶囊按「画图区顶部居中」摆好。
        /// 单独抽出来是因为清单展开时会把胶囊撑宽，撑完必须按新尺寸重新居中
        /// （否则它只往右长，中心跑掉）。
        /// </summary>
        internal static void PlaceWindow(HudWindow win)
        {
            if (win == null) return;

            Native.RECT area;
            if (!CadWindow.TryDrawingArea(out area)) return;

            Matrix m = Matrix.Identity;
            try
            {
                PresentationSource src = PresentationSource.FromVisual(win);
                if (src != null && src.CompositionTarget != null)
                    m = src.CompositionTarget.TransformFromDevice;
            }
            catch { }

            double w = win.ActualWidth;
            double h = win.ActualHeight;
            if (w <= 1 || h <= 1) return;   // 布局还没算完，下一拍再说

            double left = area.Left * m.M11;
            double right = area.Right * m.M11;
            double top = area.Top * m.M22;

            win.Left = left + (right - left - w) / 2;
            win.Top = top + 12;
        }

        // ------------------------------------------------------------------ 刷新

        static void Tick()
        {
            try
            {
                Config cfg = ConfigStore.Current;

                EnsureWindow();

                _win.Apply(BuildState(cfg), _flashText);
                _win.UpdateLayout();
                Place();
            }
            catch { }
        }

        static void EnsureWindow()
        {
            if (_win != null) return;

            _win = new HudWindow();
            _win.Closed += (s, e) => { _win = null; try { ProfileMenu.Close(); } catch { } };

            // 不激活、不进任务栏、不接收鼠标 —— 就是个贴在 CAD 上的标签
            CadWindow.AttachToCad(_win);

            _win.Show();
        }

        static HudState BuildState(Config cfg)
        {
            var st = new HudState();
            if (cfg == null) return st;

            st.On = cfg.Enabled;
            st.Profile = string.IsNullOrEmpty(cfg.ActiveProfile) ? "(未选配置)" : cfg.ActiveProfile;

            Profile p = cfg.Active;
            if (p == null) return st;

            Autodesk.AutoCAD.ApplicationServices.Document doc = null;
            try { doc = AcApp.DocumentManager.MdiActiveDocument; } catch { }

            string cat = "";
            try { cat = CommandHook.LastCategoryOf(doc); } catch { }

            if (string.IsNullOrEmpty(cat) || p.LayerCount(cat) == 0)
            {
                // 还没画过：挑第一个配了图层的类别，至少让人看到"会归到哪"
                foreach (string c in Categories.All)
                    if (p.LayerCount(c) > 0) { cat = c; break; }
            }
            if (string.IsNullOrEmpty(cat)) return st;

            string layer = p.LayerFor(cat);
            if (string.IsNullOrEmpty(layer)) return st;

            st.Layer = layer;
            st.HasLayer = true;

            int cv = p.LayerColorFor(cat);
            if (cv == LayerColorCode.Unset && doc != null)
                LayerService.TryGetLayerColor(doc.Database, layer, out cv);
            if (cv != LayerColorCode.Unset) st.LayerColor = ToMediaColor(cv);

            return st;
        }

        /// <summary>
        /// 固定在画图区**顶部居中**。
        ///
        /// 1.6 收尾时把位置选项整个去掉了（用户："只保留顶部居中，不要切换了，全部取消"）：
        /// 右下角压命令行、左下贴着「模型 / 布局」页签，只有顶上那条空带是一直在视野里、
        /// 又什么都不挡的地方。既然只有一处是对的，就别留"选错了再切回来"的路。
        /// </summary>
        static void Place()
        {
            PlaceWindow(_win);
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

    /// <summary>状态胶囊本体。纯代码构建，样式对齐演示页。</summary>
    internal sealed class HudWindow : Window
    {
        static readonly Brush TextMain = UiTheme.B(UiTheme.Text);
        static readonly Brush OkBrush = UiTheme.B(UiTheme.Ok);
        static readonly Brush InfoBrush = UiTheme.B(UiTheme.AccentLine);
        static readonly Brush OffText = UiTheme.B(Color.FromRgb(0xC9, 0xD1, 0xDC));
        static readonly Brush OffDim = UiTheme.B(UiTheme.TextDim);
        static readonly Brush OffLayer = UiTheme.B(UiTheme.Warn);
        static readonly Brush FlashBrush = UiTheme.B(UiTheme.AccentLine);

        readonly Border _shell;
        readonly Ellipse _dot;
        readonly TextBlock _title;
        readonly Border _profileChip;     // 配置名外面那层：整条状态条上**唯一**能点的东西
        readonly TextBlock _profile;
        readonly Rectangle _sep2;
        readonly Border _swatch;
        readonly TextBlock _layer;
        readonly StackPanel _layerBox;

        /// <summary>配置名当前能不能点（关掉自动归层时显示的是「已关闭」，点了没意义）。</summary>
        bool _profileClickable;

        /// <summary>鼠标是不是正压在配置名上。用来在光标滑走时把高亮收回去。</summary>
        bool _hovering;

        public HudWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            // 1.13 起不能整窗设 IsHitTestVisible=false 了：配置名要能点。
            // 改成在 WM_NCHITTEST 里逐点判断 —— 只有配置名那块返回 HTCLIENT，
            // 其余一律 HTTRANSPARENT，点击照旧落到下面的画图区。
            SizeToContent = SizeToContent.WidthAndHeight;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };

            _title = new TextBlock
            {
                Text = "Y_归层工具",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextMain,
                VerticalAlignment = VerticalAlignment.Center
            };

            _profile = new TextBlock
            {
                FontSize = 11.5,
                Foreground = InfoBrush,
                VerticalAlignment = VerticalAlignment.Center,
                // 1.13：不再卡死 130 了 —— 配置名长就让整条自己变宽（窗口是 SizeToContent），
                // 只是给个上限，免得名字长得离谱时横跨半个屏幕。位置由 Place() 重新居中。
                MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // 配置名做成一块"芯片"：平时看不出来，鼠标移上去浮一层淡底 + 手型光标，
            // 才知道这里能点（不然谁知道状态条上还能点）。
            _profileChip = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 2, 5, 2),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _profile,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _profileChip.MouseLeftButtonUp += Chip_Click;
            _profileChip.MouseEnter += (s, e) => RefreshChipVisual(true);
            _profileChip.MouseLeave += (s, e) => RefreshChipVisual(false);

            _swatch = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = InfoBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _layer = new TextBlock
            {
                FontSize = 11.5,
                Foreground = TextMain,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 190,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            _layerBox = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _layerBox.Children.Add(_swatch);
            _layerBox.Children.Add(new Border { Width = 5 });
            _layerBox.Children.Add(_layer);

            var sep1 = MakeSep();
            _sep2 = MakeSep();

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // 胶囊被撑宽时（清单展开）内容仍居中，不然文字会一直贴在左边
                HorizontalAlignment = HorizontalAlignment.Center
            };
            row.Children.Add(_dot);
            row.Children.Add(new Border { Width = 7 });
            row.Children.Add(_title);
            // 芯片自带左右各 5 的内边距，这里的间距相应收到 2，视觉上还是原来的 7
            row.Children.Add(new Border { Width = 2 });
            row.Children.Add(sep1);
            row.Children.Add(new Border { Width = 2 });
            row.Children.Add(_profileChip);
            row.Children.Add(new Border { Width = 2 });
            row.Children.Add(_sep2);
            row.Children.Add(new Border { Width = 7 });
            row.Children.Add(_layerBox);

            _shell = new Border
            {
                // 药丸（圆角 = 高的一半）。用户明确说过这个形状好看，别动。
                // 只有它下面弹出来的清单是直角 —— 那是他自己要的对比。
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                BorderBrush = UiTheme.White(0x33),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x21, 0x28)),
                Padding = new Thickness(11, 0, 11, 0),
                Height = 29,
                Child = row
            };
            _shell.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black
            };

            Content = _shell;

            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                // 注意这里**没有** WS_EX_TRANSPARENT：整窗穿透的话配置名也就点不到了。
                // 穿透交给下面的 WM_NCHITTEST 逐点决定。
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                    ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);

                HwndSource src = HwndSource.FromHwnd(hwnd);
                if (src != null) src.AddHook(HitTestHook);
            };
        }

        // ---------------------------------------------------------------- 命中测试

        /// <summary>
        /// 只有配置名那块返回 HTCLIENT，其它位置返回 HTTRANSPARENT（= 这一下交给下层窗口）。
        /// 这样状态条看着还是一条"贴在图纸上的标签"，点别处照常画图，
        /// 只有点配置名才会被它接住。
        /// </summary>
        IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_NCHITTEST) return IntPtr.Zero;

            handled = true;   // 必须自己答，否则 WPF 默认整窗都是 HTCLIENT，等于把画图区挡了一条

            if (!_profileClickable)
            {
                ClearHover();
                return new IntPtr(Native.HTTRANSPARENT);
            }

            try
            {
                long lp = lParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);

                // PointFromScreen 给的就是**窗口客户区**坐标，正好能直接跟芯片的位置比
                Point p = PointFromScreen(new Point(sx, sy));
                Point origin = _profileChip.TranslatePoint(new Point(0, 0), this);

                if (p.X >= origin.X && p.X <= origin.X + _profileChip.ActualWidth &&
                    p.Y >= origin.Y && p.Y <= origin.Y + _profileChip.ActualHeight)
                    return new IntPtr(Native.HTCLIENT);
            }
            catch { }

            // 光标已经离开配置名了：这里的消息不会再交给 WPF（下面返回穿透），
            // 所以 WPF 的 MouseLeave 等不到，高亮得在这里自己收。
            ClearHover();
            return new IntPtr(Native.HTTRANSPARENT);
        }

        void ClearHover()
        {
            if (!_hovering) return;
            RefreshChipVisual(false);
        }

        void RefreshChipVisual(bool hovering)
        {
            _hovering = hovering && _profileClickable;
            if (!_profileClickable) { _profileChip.Background = Brushes.Transparent; return; }
            _profileChip.Background = _hovering ? UiTheme.White(0x14) : Brushes.Transparent;
        }

        /// <summary>
        /// 点配置名 = 切到下一个配置（顺序跟面板下拉框一致），并且存盘。
        /// 只切配置、不动当前图层 —— 手上这条命令画到一半不会被撕成两层。
        /// </summary>
        void Chip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_profileClickable) return;

            // 点一下展开清单，自己挑一个 —— 不做"轮换到下一个"。
            // （用户原话：「不是点击轮换，而是点击展开我可以选择哪一个配置」）
            try
            {
                ProfileMenu.Toggle(this, _profileChip);
                // 没弹出来又没报错，用户只会觉得"点了没反应" —— 把原因说出来
                if (!ProfileMenu.IsOpen && !string.IsNullOrEmpty(ProfileMenu.LastError))
                    StatusHud.Flash("打不开配置清单：" + ProfileMenu.LastError);
            }
            catch (Exception ex)
            {
                try { StatusHud.Flash("打不开配置清单：" + ex.GetType().Name); } catch { }
            }
        }

        static Rectangle MakeSep()
        {
            return new Rectangle
            {
                Width = 1,
                Height = 13,
                Fill = UiTheme.White(0x1F),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>
        /// 把胶囊撑到至少这么宽；0 = 恢复自然宽度（由内容决定）。
        /// 只动 MinWidth，不动内容 —— 内容靠 row 的 HorizontalAlignment=Center 保持居中。
        /// </summary>
        public void SetMinContentWidth(double w)
        {
            if (_shell == null) return;
            if (w > 1) _shell.MinWidth = w;
            else _shell.ClearValue(FrameworkElement.MinWidthProperty);
        }

        /// <summary>
        /// 清单展开时把胶囊撑到跟清单一样宽（用户原话：「如果上面的胶囊不够长
        /// 应该随着展开跟下面一样宽」）。传 0 = 收回自然宽度。
        /// 宽度是**卡片**宽度，不含清单窗口那圈阴影留白。
        /// 撑完必须重排 + 重新居中：胶囊是 SizeToContent，变宽后不重摆就只往右长。
        /// </summary>
        public void WidenTo(double contentWidth)
        {
            SetMinContentWidth(contentWidth);
            UpdateLayout();
            StatusHud.PlaceWindow(this);
        }

        /// <summary>
        /// 把状态刷到界面上。<paramref name="flashText"/> 非空时用它顶替图层名并点亮边框 ——
        /// 切层后那 1.4 秒里，用户要看的是"切到哪了"，不是"原来在哪"。
        /// </summary>
        public void Apply(HudState st, string flashText)
        {
            if (st == null) return;

            bool flash = !string.IsNullOrEmpty(flashText);

            if (st.On)
            {
                _dot.Fill = OkBrush;
                _shell.BorderBrush = UiTheme.White(0x33);
                _shell.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x21, 0x28));
                _title.Foreground = TextMain;
                _profile.Foreground = InfoBrush;
                _layer.Foreground = TextMain;
                _swatch.Opacity = 1.0;
                _profile.Text = st.Profile;
                // 开着才让点：这时候点一下是"换个配置继续画"，有意义
                _profileClickable = true;
                RefreshChipVisual(_profileChip.IsMouseOver);
            }
            else
            {
                // 关着的时候把图层那一段藏掉 —— 显示一个不会生效的图层名只会误导
                _dot.Fill = UiTheme.B(UiTheme.TextFaint);
                _shell.BorderBrush = UiTheme.White(0x40);
                _shell.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x26, 0x28, 0x2F));
                _title.Foreground = OffText;
                _layer.Foreground = OffLayer;
                _swatch.Opacity = 0.55;
                _profile.Foreground = UiTheme.B(UiTheme.Warn);
                _profile.Text = "已关闭";
                // 关着的时候配置名位置显示的是「已关闭」，点它没意义，也不该挡住画图
                _profileClickable = false;
                RefreshChipVisual(false);
                // 清单开着的时候被关掉自动归层（ALOFF / 面板里取消勾选）——
                // 别让一份"选配置"的浮层孤零零挂在一个已经关掉的标签下面
                ProfileMenu.Close();
                _sep2.Visibility = Visibility.Collapsed;
                _layerBox.Visibility = Visibility.Collapsed;
                return;
            }

            if (flash)
            {
                _shell.BorderBrush = UiTheme.B(UiTheme.AccentLine);
                _shell.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x2A, 0x44));
                _sep2.Visibility = Visibility.Visible;
                _layerBox.Visibility = Visibility.Visible;
                _layer.Text = flashText;
                _layer.Foreground = FlashBrush;
                return;
            }

            if (!st.HasLayer)
            {
                _sep2.Visibility = Visibility.Collapsed;
                _layerBox.Visibility = Visibility.Collapsed;
                return;
            }

            _sep2.Visibility = Visibility.Visible;
            _layerBox.Visibility = Visibility.Visible;
            _swatch.Background = new SolidColorBrush(st.LayerColor);
            _layer.Text = st.Layer;
        }
    }
}
