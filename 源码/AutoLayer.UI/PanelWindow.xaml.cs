using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AutoLayer.Core;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using ShapeEllipse = System.Windows.Shapes.Ellipse;

namespace AutoLayer.UI
{
    public partial class PanelWindow : Window
    {
        // 统一从 UiTheme 取色，别在这里另起一套灰
        static readonly Brush TextDim = UiTheme.B(UiTheme.TextDim);
        static readonly Brush TextMain = UiTheme.B(UiTheme.Text);
        static readonly Brush OkBrush = UiTheme.B(UiTheme.Ok);
        static readonly Brush WarnBrush = UiTheme.B(UiTheme.Warn);
        static readonly Brush InfoBrush = UiTheme.B(UiTheme.AccentLine);
        static readonly Brush ErrBrush = UiTheme.B(UiTheme.Danger);
        static readonly Brush GrayBrush = UiTheme.B(UiTheme.TextFaint);

        /// <summary>一行右侧的「色块+色号」和「状态点 + 文字」。</summary>
        sealed class StateView
        {
            public ShapeEllipse Dot;
            public TextBlock Text;
        }

        readonly Dictionary<string, ComboBox> _boxes = new Dictionary<string, ComboBox>();
        readonly Dictionary<string, StateView> _states = new Dictionary<string, StateView>();
        readonly Dictionary<string, WrapPanel> _chipPanels = new Dictionary<string, WrapPanel>();
        readonly Dictionary<string, StackPanel> _poolHeads = new Dictionary<string, StackPanel>();

        /// <summary>
        /// 哪些类别的图层池是**展开**的。
        ///
        /// 默认全收起：一类配了四五个长名字的图层，chip 会折成两三行，
        /// 六类加起来面板一半高度都是 chip，可那一堆只在"要换层"的那一下才有用。
        /// 收起后每类只占一行摘要（「4 层 · 立面轮廓线」），点一下才铺开。
        ///
        /// 状态存在这里而不是控件上：RefreshChips 每次敲键盘都会重建一遍控件，
        /// 状态挂在控件上会被一起扔掉，表现就是"一改输入框就自己收回去"。
        /// 只存内存，不写 ini —— 这是"我现在想看"，不是配置。
        /// </summary>
        readonly HashSet<string> _poolOpen = new HashSet<string>(StringComparer.Ordinal);

        // ---- chip 拖动排序 ----
        // 拖 chip 改的是 ini 里「A ; B ; C」的先后，也就是圆盘扇区的顺序。
        // 状态存在字段里而不是控件上：RefreshChips 会重建控件，挂在控件上会被扔掉。
        string _dragCat;
        Point _dragFrom;
        bool _dragging;
        bool _moving;      // 正在把被拖的 chip 摘下来再插回去（这段会丢鼠标捕获）

        /// <summary>池里多个图层之间的分隔符。用户既可以打半角也可以打全角分号。</summary>
        const char LayerSep = ';';

        static List<string> SplitLayers(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string seg in text.Replace('；', LayerSep).Split(LayerSep))
            {
                string t = seg.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        Config _cfg;
        bool _loading;
        bool _dirty;
        string _shownProfile;
        DispatcherTimer _flash;

        // 配置列表拖拽排序（拖拽那套状态都在这里）
        bool _dragArmed;                 // 左键按在某一项上，还没超过阈值
        bool _draggingProfile;           // 真在拖了（已经捕获鼠标）
        Point _dragStart;
        int _dragFromIndex = -1;         // 从第几条开始拖
        FrameworkElement _dragRoot;      // Popup 里的内容根（列表项都长在这棵树上）
        ComboBoxItem _dropMark;          // 当前画着落点线的那一条
        KeyValuePair<Thickness, Brush> _dropMarkKeep;   // 它原来的边框，松手要还原

        public PanelWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Apply(this);
            Closing += OnClosing;
            // 窗口关掉后别让计时器还拿着这个窗口；
            // 顺便兜底把拖拽光标还回去（拖到一半关窗口不至于留个上下箭头）
            Closed += (s, e) =>
            {
                if (_flash != null) _flash.Stop();
                Mouse.OverrideCursor = null;
            };
            HookProfileDrag();
            Reload();
        }

        public void Reload()
        {
            _loading = true;
            try
            {
                _cfg = ConfigStore.Current;
                TglEnabled.IsChecked = _cfg.Enabled;

                CmbProfile.ItemsSource = _cfg.Profiles.Select(p => p.Name).ToList();
                CmbProfile.SelectedItem = _cfg.ActiveProfile;
                CmbProfile.ToolTip = _cfg.ActiveProfile;

                BuildRows();
                ClearDirty();
            }
            finally
            {
                _loading = false;
                RefreshChrome();
            }
        }

        // ------------------------------------------------------------------ 行

        void BuildRows()
        {
            bool prevLoading = _loading;
            _loading = true;
            try
            {
                RowsPanel.Children.Clear();
                _boxes.Clear();
                _states.Clear();
                _chipPanels.Clear();
                _poolHeads.Clear();

                Profile profile = _cfg.Active;
                if (profile == null) return;
                _shownProfile = profile.Name;

                var layers = new List<string> { "" };
                Database db = HostDb();
                if (db != null) layers.AddRange(LayerService.AllNames(db));

                Style rowStyle = (Style)FindResource("RowBorder");

                foreach (string cat in Categories.All)
                {
                    var border = new Border { Style = rowStyle };

                    var grid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    // 第 2 行：图层池摘要（收起时只有这一行）
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    // 第 3 行：展开后的 chip，默认不占高度
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    // 状态列按内容自适应，并跟表头/其它行共用宽度，
                    // 免得"正常"两个字占着 82px 空出一大块。
                    // 颜色列已经撤掉 —— 颜色跟着每个图层 chip 走（见 RefreshChips）
                    grid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto,
                        SharedSizeGroup = "StatusCol"
                    });

                    var label = new TextBlock
                    {
                        Text = cat,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = TextMain
                    };
                    Grid.SetColumn(label, 0);
                    grid.Children.Add(label);

                    var box = new ComboBox
                    {
                        IsEditable = true,
                        Height = 26,
                        ItemsSource = layers,
                        Text = PoolText(profile, cat),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "一类可以配多个图层：点下面的「＋」直接从图纸里勾，\n也可以在这里用分号手打，例如：\nE-04 造型线 ; E-05 辅助线\n配了多个时下面每个都能点，点一下换「下一条命令用哪个」。"
                    };
                    string captured = cat;

                    // 事件必须等模板真正应用完再挂。
                    // 可编辑 ComboBox 的模板是「布局阶段」才应用的，应用时会把 Text 灌进
                    // 内部 TextBox，触发一次 TextChanged / SelectionChanged。
                    // 那时 Reload 的 finally 已经把 _loading 复位了，屏蔽失效，
                    // 于是刚打开面板什么都没动也会被标成「● 未保存」。
                    bool wired = false;
                    box.Loaded += (s, e) =>
                    {
                        if (wired) return;
                        wired = true;
                        box.SelectionChanged += (s2, e2) => { RefreshState(captured); RefreshChips(captured); SyncMemory(); };
                        box.LostFocus += (s2, e2) => { RefreshState(captured); RefreshChips(captured); SyncMemory(); };
                        // 直接敲图层名时也要立刻反映到状态列与「未保存」
                        box.AddHandler(TextBoxBase.TextChangedEvent,
                            new TextChangedEventHandler((s2, e2) => { RefreshState(captured); RefreshChips(captured); MarkDirty(); }));
                    };
                    Grid.SetColumn(box, 1);
                    grid.Children.Add(box);
                    _boxes[cat] = box;

                    var pill = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var dot = new ShapeEllipse
                    {
                        Width = 6,
                        Height = 6,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var text = new TextBlock
                    {
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    pill.Children.Add(dot);
                    pill.Children.Add(text);
                    Grid.SetColumn(pill, 2);
                    grid.Children.Add(pill);
                    _states[cat] = new StateView { Dot = dot, Text = text };

                    // 池的摘要行：收起时就是它，点一下铺开成 chip。
                    // 池里一个图层都没有的时候它也负责挂「＋ 选图层」，
                    // 否则想加第二个图层的人找不到入口，只能回输入框里手打分号。
                    var head = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 3, 0, 0)
                    };
                    Grid.SetRow(head, 1);
                    Grid.SetColumn(head, 1);
                    Grid.SetColumnSpan(head, 2);
                    grid.Children.Add(head);
                    _poolHeads[cat] = head;

                    // 展开后铺开的 chip
                    var chips = new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 4, 0, 0),
                        Visibility = System.Windows.Visibility.Collapsed
                    };
                    Grid.SetRow(chips, 2);
                    Grid.SetColumn(chips, 1);
                    Grid.SetColumnSpan(chips, 2);
                    grid.Children.Add(chips);
                    _chipPanels[cat] = chips;

                    border.Child = grid;
                    RowsPanel.Children.Add(border);

                    RefreshState(cat);
                    RefreshChips(cat);
                }
            }
            finally { _loading = prevLoading; }
        }

        // ------------------------------------------------------------------ 池

        /// <summary>池里所有图层拼成输入框里那串「A ; B ; C」。</summary>
        static string PoolText(Profile p, string cat)
        {
            var list = p.LayersFor(cat);
            var names = new List<string>();
            foreach (LayerRef l in list)
                if (l != null && !string.IsNullOrEmpty(l.Name)) names.Add(l.Name);
            return string.Join(" ; ", names.ToArray());
        }

        /// <summary>这一行现在该按哪个图层去查状态（池里 Active 指的那个）。</summary>
        string ActiveLayerOf(string category)
        {
            ComboBox box;
            if (!_boxes.TryGetValue(category, out box)) return "";

            List<string> names = SplitLayers(box.Text);
            if (names.Count == 0) return "";

            Profile p = _cfg == null ? null : _cfg.FindProfile(_shownProfile);
            int i = p == null ? 0 : p.ActiveIndexOf(category);
            if (i < 0 || i >= names.Count) i = 0;
            return names[i];
        }

        /// <summary>
        /// 重画某一行的图层池：一行摘要 + （展开时）铺开的 chip。
        /// 收起时摘要显示「4 层 · 立面轮廓线」，展开时高亮"下一条命令会用"的那个。
        /// </summary>
        void RefreshChips(string category)
        {
            WrapPanel host;
            ComboBox box;
            StackPanel head;
            if (!_chipPanels.TryGetValue(category, out host)) return;
            if (!_boxes.TryGetValue(category, out box)) return;
            if (!_poolHeads.TryGetValue(category, out head)) return;

            host.Children.Clear();
            head.Children.Clear();
            _dragging = false;
            _dragCat = null;

            List<string> names = SplitLayers(box.Text);
            Profile p = _cfg == null ? null : _cfg.FindProfile(_shownProfile);
            int active = p == null ? 0 : p.ActiveIndexOf(category);
            if (active < 0 || active >= names.Count) active = 0;

            // 池里不到两个图层：没有可展开的东西，摘要行就只剩「＋」。
            // 这一格**永远在**：以前只在有两个以上图层时才出现，
            // 结果"想加第二个图层"的人根本找不到入口，只能去输入框里手打分号。
            if (names.Count < 2)
            {
                head.Children.Add(PoolButton(
                    names.Count == 0 ? "＋ 选图层" : "＋ 再加一层",
                    "从当前图纸的图层里勾选，可以一次勾多个",
                    () => OpenLayerPicker(category)));
                host.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            bool open = _poolOpen.Contains(category);
            head.Children.Add(PoolToggle(category, names, active, open));
            // 「＋」挂在摘要行末尾而不是 chip 末尾：
            // 挂 chip 末尾的话，三个 chip 一占满宽度它就自己折到下一行去，
            // 看着像多出来一行空的；而且收起时它也跟着消失，等于没入口。
            Border plus = PoolButton("＋",
                "从当前图纸的图层里勾选，可以一次勾多个",
                () => OpenLayerPicker(category));
            // 摘要是 10.5 号字，＋ 按钮按默认内边距会把整行撑高 9px，收窄一点让它跟文字齐平
            plus.Padding = new Thickness(6, 0, 6, 0);
            plus.Margin = new Thickness(5, 0, 0, 0);
            plus.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(plus);

            host.Visibility = open
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            if (!open) return;

            for (int i = 0; i < names.Count; i++)
            {
                int idx = i;
                string name = names[i];
                bool on = i == active;

                var chip = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(7, 1, 6, 1),
                    Margin = new Thickness(0, 0, 5, 0),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(on
                        ? UiTheme.AccentSoft
                        : UiTheme.Raised),
                    BorderBrush = on ? UiTheme.White(0x66) : UiTheme.White(0x26),
                    Cursor = Cursors.Hand,
                    ToolTip = (on ? "下一条命令就用这一层" : "点一下，下一条命令换成这一层")
                            + "\n按住左右拖动可以调顺序（圆盘扇区也跟着这个顺序）"
                };

                var line = new StackPanel { Orientation = Orientation.Horizontal };

                // 序号去掉了（1.6）：chip 本来就是按顺序排的，位置已经说明先后；
                // 现在 chip 里还多了一个色号，两个小数字挨着只会让人读串。

                var tx = new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = on ? Brushes.White : TextMain
                };
                line.Children.Add(tx);

                // ---- 颜色：色块 + 色号，跟着这个图层走 ----
                // 一类能配好几个图层以后，"这一类的颜色"没有意义了：颜色本来就是图层的属性。
                // 所以原来右边那一列撤掉，塞进每个 chip 里，点色块就改这一层的色。
                int cv = ShownColorAt(category, idx, name);
                // 记的色和图纸里的实际色不一样时两个都写出来 ——
                // 否则用户改完色发现图里没变，会以为没生效
                string cvTip = "「" + name + "」的颜色：" + LayerService.DescribeColor(cv, null);
                int realCv;
                Database chipDb = HostDb();
                if (chipDb != null && LayerService.TryGetLayerColor(chipDb, name, out realCv) && realCv != cv)
                    cvTip += " · 图纸里实际为 " + LayerService.DescribeColor(realCv, null);
                cvTip += "\n点一下改色（只改这一层）";

                var sw = new Border
                {
                    Width = 9,
                    Height = 9,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(6, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = UiTheme.White(0x40),
                    Background = new SolidColorBrush(ToMediaColor(cv)),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = cvTip
                };
                var cvTx = new TextBlock
                {
                    Text = ColorLabel(cv),
                    FontSize = 9.5,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(on ? UiTheme.AccentLine : UiTheme.TextDim),
                    ToolTip = sw.ToolTip
                };
                sw.MouseLeftButtonUp += (s2, e2) => { e2.Handled = true; PickColorAt(category, idx); };
                cvTx.MouseLeftButtonUp += (s2, e2) => { e2.Handled = true; PickColorAt(category, idx); };
                line.Children.Add(sw);
                line.Children.Add(cvTx);

                // 去掉这一层：以前只能回输入框里删字，很容易删错
                var del = new TextBlock
                {
                    Text = "✕",
                    FontSize = 9,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(on ? UiTheme.AccentLine : UiTheme.TextFaint),
                    ToolTip = "从这一类的图层池里去掉「" + name + "」"
                };
                del.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;      // 别让父级的"切成当前层"跟着触发
                    RemovePoolIndex(category, idx);
                };
                del.MouseEnter += (s, e) => del.Foreground = UiTheme.B(UiTheme.Danger);
                del.MouseLeave += (s, e) => del.Foreground = new SolidColorBrush(on
                    ? UiTheme.AccentLine
                    : UiTheme.TextFaint);
                line.Children.Add(del);

                chip.Child = line;
                chip.Tag = name;

                // ---- 拖动排序 ----
                // 只有**移动超过 5px** 才算拖动，否则照旧是"点一下切成当前层"。
                // 不设这个门槛的话，点的时候手抖一两像素就会被判成拖动，换层会变得很难点中。
                chip.MouseLeftButtonDown += (s, e) =>
                {
                    _dragCat = category;
                    _dragFrom = e.GetPosition(this);
                    _dragging = false;
                };
                chip.MouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || _dragCat != category) return;

                    if (!_dragging)
                    {
                        Point now = e.GetPosition(this);
                        if (Math.Abs(now.X - _dragFrom.X) < 5 && Math.Abs(now.Y - _dragFrom.Y) < 5) return;

                        _dragging = true;
                        chip.CaptureMouse();     // 拖出 chip 之外也要继续收到移动消息
                        chip.Opacity = 0.55;
                        chip.Cursor = Cursors.SizeAll;
                    }

                    // 边拖边把控件挪到该在的位置：用户看到的顺序就是最后存下去的顺序，
                    // 不需要额外的"插入位置指示线"。
                    int to = InsertIndex(host, chip, e.GetPosition(host));

                    // 把被拖的控件从树上摘下来会**丢失鼠标捕获**，而捕获是拖动唯一的凭据。
                    // 摘/插这一段用 _moving 打个标记，LostMouseCapture 里看到标记就不当成
                    // "真的松手了"，插回去以后再补一次捕获。
                    _moving = true;
                    host.Children.Remove(chip);
                    host.Children.Insert(to, chip);
                    _moving = false;
                    if (!chip.IsMouseCaptured)
                    {
                        try { chip.CaptureMouse(); } catch { }
                    }
                };
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;

                    if (_dragging)
                    {
                        _dragging = false;
                        _dragCat = null;
                        chip.Cursor = Cursors.Hand;
                        chip.Opacity = 1.0;
                        try { chip.ReleaseMouseCapture(); } catch { }

                        // 控件顺序 = 用户看到的顺序，按它重排图层池
                        var order = new List<string>();
                        foreach (UIElement c in host.Children)
                        {
                            var b = c as Border;
                            if (b != null && b.Tag is string) order.Add((string)b.Tag);
                        }
                        ApplyPool(category, order);
                        return;
                    }

                    PickPoolIndex(category, idx);
                    _dragCat = null;
                };
                // 真丢了捕获（拖到别的窗口去了）：收尾，否则 _dragging 一直为真，
                // 之后点谁都变成拖动，chip 再也点不动。
                chip.LostMouseCapture += (s, e) =>
                {
                    if (_moving || !_dragging) return;
                    _dragging = false;
                    _dragCat = null;
                    chip.Cursor = Cursors.Hand;
                    chip.Opacity = 1.0;
                };

                host.Children.Add(chip);
            }
        }

        /// <summary>
        /// 光标落在第几个 chip 前面（0 = 最前面，等于格数 = 最后面）。
        /// 按"阅读顺序"比：整格在光标上方就算过去了，同一行则看有没有越过中线。
        /// </summary>
        static int InsertIndex(WrapPanel host, UIElement skip, Point p)
        {
            int idx = 0;
            foreach (UIElement c in host.Children)
            {
                if (c == skip) continue;
                var fe = c as FrameworkElement;
                if (fe == null) continue;

                Point tl = fe.TranslatePoint(new Point(0, 0), host);
                double w = fe.ActualWidth, h = fe.ActualHeight;

                if (p.Y > tl.Y + h) { idx++; continue; }                       // 这一格整格在光标上方
                if (p.Y >= tl.Y) { if (p.X > tl.X + w / 2) idx++; continue; }  // 同一行：过中线才算排它后面
                break;                                                          // 光标在第一格上方 → 插最前
            }
            return idx;
        }

        // ------------------------------------------------------------------ 池：摘要行与按钮

        /// <summary>
        /// 收起 / 展开这一类的图层池。
        /// 收起时只留一行「▸ 4 层 · 立面轮廓线」——
        /// 长名字的 chip 折两三行，六类铺下来面板一半高度都是 chip，
        /// 可那一堆只在"要换层"的那一下才用得上。
        /// </summary>
        Border PoolToggle(string category, List<string> names, int active, bool open)
        {
            string activeName = active >= 0 && active < names.Count ? names[active] : "";

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            // 箭头用 10.5 而不是 9：9 号字的 ▸ 在这个深色底上就是一个点，
            // 跟后面的分隔点分不出来，看着不像"能点开"。
            var arrow = new TextBlock
            {
                Text = open ? "▾" : "▸",
                FontSize = 10.5,
                Width = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiTheme.B(UiTheme.TextDim)
            };
            var cnt = new TextBlock
            {
                Text = names.Count + " 层",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiTheme.B(UiTheme.TextDim)
            };
            line.Children.Add(arrow);
            line.Children.Add(cnt);

            // 摘要里带上"现在用的是哪一层"：收起之后不用展开也知道下一条命令落哪儿
            if (!string.IsNullOrEmpty(activeName))
            {
                line.Children.Add(new TextBlock
                {
                    Text = activeName,
                    FontSize = 10.5,
                    Margin = new Thickness(8, 0, 0, 0),
                    MaxWidth = 190,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = UiTheme.B(UiTheme.AccentLine)
                });
            }

            var b = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 8, 1),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = line,
                ToolTip = open ? "收起图层池" : "展开图层池（换层 / 改色 / 删层都在这儿）"
            };
            b.MouseEnter += (s, e) => b.BorderBrush = UiTheme.White(0x26);
            b.MouseLeave += (s, e) => b.BorderBrush = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_poolOpen.Contains(category)) _poolOpen.Remove(category);
                else _poolOpen.Add(category);
                RefreshChips(category);
            };
            return b;
        }

        /// <summary>虚线小按钮（「＋ 选图层」「＋ 再加一层」），行为和原来的「＋」一样。</summary>
        Border PoolButton(string label, string tip, Action onClick)
        {
            var tx = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = UiTheme.B(UiTheme.TextFaint)
            };
            var b = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                BorderThickness = new Thickness(1),
                BorderBrush = UiTheme.White(0x2E),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = tx,
                ToolTip = tip
            };
            b.MouseEnter += (s, e) =>
            {
                b.BorderBrush = UiTheme.B(UiTheme.AccentLine);
                tx.Foreground = UiTheme.B(UiTheme.AccentLine);
            };
            b.MouseLeave += (s, e) =>
            {
                b.BorderBrush = UiTheme.White(0x2E);
                tx.Foreground = UiTheme.B(UiTheme.TextFaint);
            };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            return b;
        }

        /// <summary>把这一类的图层池整体换成 <paramref name="names"/>（走输入框，保证和手打同一条路）。</summary>
        void ApplyPool(string category, List<string> names)
        {
            ComboBox box;
            if (!_boxes.TryGetValue(category, out box)) return;

            var clean = new List<string>();
            if (names != null)
                foreach (string n in names)
                    if (!string.IsNullOrWhiteSpace(n)) clean.Add(n.Trim());

            box.Text = string.Join(" ; ", clean.ToArray());
            // 刚勾完图层就把这一行铺开：接下来多半要挑「当前用哪个」或者改色，
            // 收着的话还得再点一下，白费一次点击。
            if (clean.Count >= 2) _poolOpen.Add(category);
            RefreshState(category);
            RefreshChips(category);
            MarkDirty();
            SyncMemory();
        }

        void RemovePoolIndex(string category, int index)
        {
            ComboBox box;
            if (!_boxes.TryGetValue(category, out box)) return;

            List<string> names = SplitLayers(box.Text);
            if (index < 0 || index >= names.Count) return;
            names.RemoveAt(index);
            ApplyPool(category, names);
        }

        /// <summary>列出当前图纸的图层让用户勾（图纸没开就只列配置里已有的）。</summary>
        void OpenLayerPicker(string category)
        {
            ComboBox box;
            if (!_boxes.TryGetValue(category, out box)) return;

            List<string> current = SplitLayers(box.Text);
            var entries = new List<LayerPicker.Entry>();

            Database db = HostDb();
            if (db != null)
            {
                foreach (string name in LayerService.AllNames(db))
                {
                    int cv = LayerColorCode.Unset;
                    bool hasColor = LayerService.TryGetLayerColor(db, name, out cv);
                    entries.Add(new LayerPicker.Entry
                    {
                        Name = name,
                        // 拿不到颜色就不画色块（画个默认白反而误导）
                        Color = hasColor ? ToMediaColor(cv) : Colors.Transparent,
                        State = LayerService.StateOf(db, name)
                    });
                }
            }

            List<string> picked = LayerPicker.Pick(this, entries, current);
            if (picked == null) return;      // 取消：一个字节都不动

            ApplyPool(category, picked);
        }

        /// <summary>点池里的某个按钮：把它设成"下一条命令用的那层"。</summary>
        void PickPoolIndex(string category, int index)
        {
            if (_loading || _cfg == null) return;
            Profile p = _cfg.FindProfile(_shownProfile);
            if (p == null) return;

            CaptureIntoProfile(_shownProfile);
            if (!p.SetActiveIndex(category, index)) return;

            RefreshChips(category);
            RefreshState(category);
            MarkDirty();
            SyncMemory();
        }

        void RefreshState(string category)
        {
            ComboBox box;
            StateView view;
            if (!_boxes.TryGetValue(category, out box) || !_states.TryGetValue(category, out view)) return;

            string layer = ActiveLayerOf(category);
            if (layer.Length == 0)
            {
                SetPill(view, "不归层", TextDim, GrayBrush);
                return;
            }

            // 和 CAD 保持一致：不认识的字符直接拒绝，不擅自改名
            if (!LayerService.IsValidName(layer))
            {
                SetPill(view, "名称非法", ErrBrush, ErrBrush);
                return;
            }

            Database db = HostDb();
            string s = db == null ? "?" : LayerService.StateOf(db, layer);

            if (s == "正常")
            {
                SetPill(view, s, OkBrush, OkBrush);
                view.Text.ToolTip = "图纸里已有这个图层，当前颜色：" + CurrentColorHint(db, layer);
            }
            else if (s == "不存在")
            {
                bool auto = _cfg != null && _cfg.Settings != null && _cfg.Settings.AutoCreateLayer;
                SetPill(view, auto ? "将新建" : "不存在", InfoBrush, InfoBrush);
                view.Text.ToolTip = auto
                    ? "图纸里没有这个图层，画图时会自动新建，颜色：" + RecordedColorHint(category)
                    : "图纸里没有这个图层，且未开启「自动新建图层」，这条不会归层。";
            }
            else if (s == "?") { SetPill(view, s, TextDim, GrayBrush); view.Text.ToolTip = null; }
            else { SetPill(view, s, WarnBrush, WarnBrush); view.Text.ToolTip = null; }
        }

        /// <summary>图纸里这个图层现在是什么颜色。</summary>
        static string CurrentColorHint(Database db, string layer)
        {
            int cv;
            if (!LayerService.TryGetLayerColor(db, layer, out cv)) return "未知";
            return LayerService.DescribeColor(cv, null);
        }

        /// <summary>这个类别自动新建图层时，会用什么颜色（配置里记住的，没记住就是全局默认）。</summary>
        string RecordedColorHint(string category)
        {
            Profile p = _cfg == null ? null : _cfg.FindProfile(_shownProfile);
            int cv = p == null ? LayerColorCode.Unset : p.LayerColorFor(category);
            return LayerService.DescribeColor(cv, _cfg == null ? null : _cfg.Settings);
        }

        /// <summary>
        /// 池里第 <paramref name="index"/> 个图层现在该显示什么颜色：
        /// 配置里记的优先，其次图纸里的实际色，最后退到「新建图层默认颜色」。
        /// 配置优先是有意的 —— 用户在面板里明确选过的颜色，就该显示他选的那个。
        /// </summary>
        int ShownColorAt(string category, int index, string layerName)
        {
            Profile p = _cfg == null ? null : _cfg.FindProfile(_shownProfile);
            int recorded = p == null ? LayerColorCode.Unset : p.LayerColorAt(category, index);
            if (recorded != LayerColorCode.Unset) return recorded;

            int actual;
            Database db = HostDb();
            if (db != null && LayerService.TryGetLayerColor(db, layerName, out actual)) return actual;

            Settings st = _cfg == null ? null : _cfg.Settings;
            short fallback = (st == null || st.NewLayerColorIndex <= 0) ? (short)7 : st.NewLayerColorIndex;
            return LayerColorCode.FromAci(fallback);
        }

        /// <summary>色号：索引色直接写数字，真彩色写「真彩」（用户基本只用索引色）。</summary>
        static string ColorLabel(int colorValue)
        {
            if (LayerColorCode.IsRgb(colorValue)) return "真彩";
            if (LayerColorCode.IsAci(colorValue)) return colorValue.ToString();
            return "—";
        }

        static Color ToMediaColor(int colorValue)
        {
            if (LayerColorCode.IsRgb(colorValue))
                return Color.FromRgb(LayerColorCode.R(colorValue),
                                     LayerColorCode.G(colorValue),
                                     LayerColorCode.B(colorValue));

            return AciPalette.Get((short)(LayerColorCode.IsAci(colorValue) ? colorValue : 7));
        }

        // ------------------------------------------------------------------ 改色

        /// <summary>
        /// 点色块 → 叫出 CAD 原生的「选择颜色」对话框（索引色 / 真彩色 / 配色系统都在），
        /// 选完写进配置，之后在没有这个图层的图纸里自动建层就用这个色。
        /// 只改配置，不动当前图纸里已有的图层 —— 改图纸属于用户自己的操作，插件不越界。
        /// </summary>
        void PickColorAt(string category, int index)
        {
            if (_cfg == null) return;

            ComboBox box;
            if (!_boxes.TryGetValue(category, out box)) return;

            // 改的是**点中的那个图层**，池里其它层各记各的
            List<string> names = SplitLayers(box.Text);
            if (index < 0 || index >= names.Count) return;
            string layer = names[index];

            Profile p = _cfg.FindProfile(_shownProfile);
            if (p == null) return;

            int current = ShownColorAt(category, index, layer);

            try
            {
                var dlg = new Autodesk.AutoCAD.Windows.ColorDialog();
                dlg.Color = ToAcadColor(current);

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                int picked = FromAcadColor(dlg.Color);
                if (picked == LayerColorCode.Unset)
                {
                    TxtHint.Text = "请选一个具体颜色 —— 随层 / 随块不能当图层颜色。";
                    return;
                }

                p.SetLayerColorAt(category, index, picked);
                MarkDirty();
                RefreshState(category);
                RefreshChips(category);
                TxtHint.Text = "「" + layer + "」的颜色已设为 "
                    + LayerService.DescribeColor(picked, _cfg.Settings) + "，点「完成」写入配置。";
            }
            catch (System.Exception ex)
            {
                TxtHint.Text = "打开颜色选择器失败：" + ex.Message;
            }
        }

        static Autodesk.AutoCAD.Colors.Color ToAcadColor(int colorValue)
        {
            if (LayerColorCode.IsRgb(colorValue))
                return Autodesk.AutoCAD.Colors.Color.FromRgb(
                    LayerColorCode.R(colorValue),
                    LayerColorCode.G(colorValue),
                    LayerColorCode.B(colorValue));

            short idx = (short)(LayerColorCode.IsAci(colorValue) ? colorValue : 7);
            return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, idx);
        }

        static int FromAcadColor(Autodesk.AutoCAD.Colors.Color c)
        {
            if (c == null) return LayerColorCode.Unset;

            if (c.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor)
                return LayerColorCode.FromRgb(c.Red, c.Green, c.Blue);

            if (c.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci)
                return LayerColorCode.FromAci(c.ColorIndex);

            return LayerColorCode.Unset;   // ByLayer / ByBlock / None
        }

        static void SetPill(StateView view, string text, Brush fg, Brush dot)
        {
            view.Text.Text = text;
            view.Text.Foreground = fg;
            view.Dot.Fill = dot;
        }

        /// <summary>状态条 / 停用压暗 / 提示文案，跟着总开关走。</summary>
        void RefreshChrome()
        {
            bool on = TglEnabled.IsChecked == true;

            StatusAccent.Fill = on ? OkBrush : GrayBrush;
            StatusDot.Fill = on ? OkBrush : GrayBrush;
            TxtSwitchLabel.Text = on ? "已启用" : "已停用";

            double op = on ? 1.0 : 0.6;
            TableHead.Opacity = op;
            RowsPanel.Opacity = op;

            if (!_dirty)
            {
                // 这句是常态提示，顺手把圆盘怎么触发写在这儿 —— 1.3 之前用户根本不知道有圆盘。
                // 1.6 起压成一行：两行提示白占 16px 高度，而面板本来就是嫌高才改的。
                // 触发方式现在能改（设置窗口），所以这行不能写死，得跟着配置走。
                TxtHint.Text = on
                    ? "命令前后自动切层 / 还原 · " + TriggerText()
                    : "未启用。不介入画图，图元落在你当前图层。";
            }

            BtnDelete.IsEnabled = _cfg != null && _cfg.Profiles != null && _cfg.Profiles.Count > 1;
        }

        // ------------------------------------------------------------------ 脏标记

        /// <summary>
        /// 面板上有没有还没保存的改动。状态条上点配置名切配置时要用它挡一下 ——
        /// 有未保存的改动就别偷偷把面板重载掉，那是把用户刚改的东西扔掉。
        /// </summary>
        public bool IsDirty { get { return _dirty; } }

        void MarkDirty()
        {
            if (_loading || _cfg == null) return;
            _dirty = true;
            if (_flash != null) _flash.Stop();
            TxtDirty.Text = "● 未保存";
            TxtDirty.Foreground = WarnBrush;
            TxtDirty.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>
        /// 存成功后亮一下「✓ 已保存」再淡出。
        /// 否则用户只看到「未保存」在关窗瞬间消失，永远等不到一个"存好了"的确认。
        /// </summary>
        void FlashSaved()
        {
            _dirty = false;
            TxtDirty.Text = "✓ 已保存";
            TxtDirty.Foreground = OkBrush;
            TxtDirty.Visibility = System.Windows.Visibility.Visible;

            if (_flash == null)
            {
                _flash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
                _flash.Tick += (s, e) =>
                {
                    _flash.Stop();
                    if (!_dirty) TxtDirty.Visibility = System.Windows.Visibility.Collapsed;
                };
            }
            _flash.Stop();
            _flash.Start();
        }

        /// <summary>
        /// 把当前输入立刻写进内存里的配置，不等保存。
        /// 这样面板开着时改完一行、切到图纸就能按新配置画，符合"改完就用"的预期。
        /// 落盘仍然只在完成 / Ctrl+S / 关窗时发生。
        /// </summary>
        void SyncMemory()
        {
            if (_loading || _cfg == null) return;
            CaptureIntoProfile(_shownProfile);
            MarkDirty();
        }

        void ClearDirty()
        {
            _dirty = false;
            if (_flash != null) _flash.Stop();
            TxtDirty.Visibility = System.Windows.Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ 交互

        void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings();
        }

        /// <summary>打开设置窗口（齿轮按钮和 ALSET 命令都走这里）。</summary>
        public void OpenSettings()
        {
            if (_cfg == null) return;

            // 先把手上的改动落到内存里的配置，再开设置窗口 ——
            // 否则设置里保存时会拿着旧对象，把刚敲的图层名冲掉。
            CaptureIntoProfile(_shownProfile);

            if (!SettingsWindow.Edit(this, _cfg)) return;

            // 自定义命令名是靠发一句 LISP 定义的，改完要立刻发出去 ——
            // 等下一次启动就太晚了，用户马上就会去命令行敲它。
            try { CommandAliasHost.Sync(); } catch { }

            // 快捷键 / 圆盘触发方式都是"改完就该立刻能用"的东西，所以只标脏不落盘，
            // 落盘交给「完成」/ 关窗那一套（和上面那个启用开关一个规矩）。
            MarkDirty();

            // 这里原来会 Trace.Clear() 再刷一遍底部的「最近一次」。
            // 1.11 那条状态条撤了，就不再清记录了 —— 留着反而好：
            // 设置改完用 ALTRACE 还能看到改之前发生过什么。

            RefreshChrome();
            TxtHint.Text = "设置已更新 · " + TriggerText();
        }

        /// <summary>提示行里那句"怎么弹圆盘"，跟着设置走，别写死。</summary>
        string TriggerText()
        {
            Settings st = _cfg == null ? null : _cfg.Settings;
            if (st == null) return "";

            string pie;
            if (!st.PieEnabled) pie = "圆盘已关";
            else if (st.PieButton == 1) pie = "侧键后退弹圆盘";
            else if (st.PieButton == 2) pie = "侧键前进弹圆盘";
            else pie = "右键长按 " + (st.PieHoldMs / 1000.0).ToString("0.00") + " 秒弹圆盘";

            // 开面板的方式只提一个，行宽就这么多：自定义命令名优先（它最稳），
            // 没配才提快捷键，两个都没配就不提。
            if (!string.IsNullOrEmpty(st.CommandAlias))
                return pie + " · " + st.CommandAlias + " 开面板";

            return st.HotkeyKey > 0
                ? pie + " · " + Hotkey.Text(st.HotkeyMods, st.HotkeyKey) + " 开面板"
                : pie;
        }

        void TglEnabled_Changed(object sender, RoutedEventArgs e)
        {
            RefreshChrome();
            if (_loading || _cfg == null) return;

            // 开关也立即生效，用户不必先点保存
            _cfg.Enabled = TglEnabled.IsChecked == true;
            MarkDirty();
        }

        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+S：存下来但不关窗，方便连续调几行
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                bool ok = SaveNow();
                RefreshChrome();
                TxtHint.Text = ok ? "已保存，面板保持打开。" : "保存失败：配置文件写不进去。";
                return;
            }

            if (e.Key == Key.Escape)
            {
                // 焦点在图层输入框里时，Esc 应该是"放弃这次输入"，不该把整个面板关掉
                if (Keyboard.FocusedElement is TextBox) return;

                e.Handled = true;
                Close();
            }
        }

        void CmbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _cfg == null) return;

            string name = CmbProfile.SelectedItem as string;
            if (string.IsNullOrEmpty(name) || name == _shownProfile) return;

            CaptureIntoProfile(_shownProfile);
            _cfg.ActiveProfile = name;
            CmbProfile.ToolTip = name;
            bool ok = ConfigStore.Save(_cfg);
            BuildRows();
            if (ok) FlashSaved(); else ClearDirty();
        }

        /// <summary>
        /// 把面板上的图层名和"当时的图层颜色"一起写回配置。
        /// 颜色只在**还没记录过**时补上：这样配置一存下来就固定了，
        /// 之后打开别的图纸不会因为那边图层是别的颜色就把记录改掉。
        /// 换了图层名（SetLayer 会清空颜色）就会重新记一次。
        /// </summary>
        void CaptureIntoProfile(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return;
            Profile p = _cfg.FindProfile(profileName);
            if (p == null) return;

            Database db = HostDb();
            foreach (KeyValuePair<string, ComboBox> kv in _boxes)
            {
                // 输入框里可能是「A ; B ; C」—— 分号隔开的就是这一类的一组备选图层
                List<string> names = SplitLayers(kv.Value.Text);
                if (names.Count == 0) { p.SetLayers(kv.Key, null); continue; }

                // 已经记住的颜色跟着图层名走，换名字才作废
                var old = p.LayersFor(kv.Key);
                var list = new List<LayerRef>();

                foreach (string name in names)
                {
                    int cv = LayerColorCode.Unset;
                    foreach (LayerRef o in old)
                        if (o != null && string.Equals(o.Name, name, StringComparison.Ordinal))
                        { cv = o.ColorValue; break; }

                    if (cv == LayerColorCode.Unset && db != null)
                        LayerService.TryGetLayerColor(db, name, out cv);

                    list.Add(new LayerRef(name, cv));
                }

                p.SetLayerRefs(kv.Key, list);
            }
        }

        /// <summary>
        /// 找出第一个不合法的图层名，返回「类别 → 名字」方便直接提示。
        /// 注意必须**逐个**校验：输入框里是「A ; B ; C」，分号本身是分隔符，
        /// 整串拿去校验会因为含分号被判非法，等于配了池就永远存不了。
        /// </summary>
        bool TryFindInvalidName(out string detail)
        {
            detail = null;
            foreach (KeyValuePair<string, ComboBox> kv in _boxes)
            {
                foreach (string name in SplitLayers(kv.Value.Text))
                {
                    if (LayerService.IsValidName(name)) continue;
                    detail = kv.Key + " → " + name;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把面板上的内容写回配置并落盘。点「完成」、按 Ctrl+S、关窗（标题栏 X / Esc）都走这里，
        /// 保证「改了就走」不会丢改动。返回 false 表示没写进磁盘。
        /// </summary>
        bool SaveNow()
        {
            if (_cfg == null) return true;

            // 和 CAD 一致：带不允许字符的图层名直接拒绝保存，不替用户改名
            string bad;
            if (TryFindInvalidName(out bad))
            {
                TxtHint.Text = "「" + bad + "」含 CAD 不允许的字符（< > / \\ \" : ? * | , =），改掉再保存。"
                    + "分号 ; 是分隔多个图层的符号，也不能出现在图层名里。";
                return false;
            }

            try
            {
                CaptureIntoProfile(_shownProfile);
                _cfg.Enabled = TglEnabled.IsChecked == true;
                if (CmbProfile.SelectedItem is string) _cfg.ActiveProfile = (string)CmbProfile.SelectedItem;

                if (!ConfigStore.Save(_cfg))
                {
                    TxtHint.Text = "保存失败：配置文件写不进去（可能被占用或没有权限）。";
                    return false;
                }

                FlashSaved();
                return true;
            }
            catch
            {
                return false;
            }
        }

        void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 写不进去就别关窗，否则用户以为存好了
            if (SaveNow()) Close();
            else TxtHint.Text = "保存失败，窗口先留着。请检查 " + ConfigStore.FileName + " 是否被占用。";
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            // 用户点标题栏 X 或按 Esc 直接关窗，也必须先落盘。
            // 存不进去就不能装作存好了 —— 否则改动静默消失，用户还以为存上了。
            if (SaveNow()) return;

            // 图层名不合法：留在面板上让用户当场改，不弹框打断
            string bad;
            if (TryFindInvalidName(out bad))
            {
                e.Cancel = true;
                TxtHint.Text = "「" + bad + "」含 CAD 不允许的字符（< > / \\ \" : ? * | , =），改掉才能保存。"
                    + "分号 ; 是分隔多个图层的符号，也不能出现在图层名里。";
                return;
            }

            // 磁盘写不进去（被占用 / 没权限）：给个明确选择，默认不关
            bool quitAnyway = ConfirmDialog.Ask(this, "没能保存",
                "配置写不进 " + ConfigStore.FileName + "（可能被别的 CAD 实例或杀毒软件占用，也可能没有写入权限）。\n\n" +
                "关闭后，这次的改动会丢失。",
                "仍然关闭");
            if (!quitAnyway) e.Cancel = true;
        }

        // ------------------------------------------------------------ 配置拖拽排序

        /// <summary>
        /// 在下拉列表里按住某条配置往上/下拖，松手就落到新位置。
        ///
        /// 用户先看到的是"配置框外面两个小箭头"，当场否掉：「箭头怎么在外面啊，
        /// 那还不如栏里拖拽呢，别扭」。所以排序手势放在**列表里**：
        /// 按住一条、拖到目标位置、松手，列表当场重排并存盘。
        ///
        /// ⚠ 两个坑，1.13 第一版全踩了，拖拽一点反应都没有：
        ///   1. 别用系统 DragDrop.DoDragDrop —— 下拉列表住在 Popup 里，Popup 是独立的
        ///      HWND，OLE 拖放的落点注册不到它上面。拖拽能起来，但 DragOver / Drop
        ///      一次都不来，松手什么也不会发生。
        ///   2. 别把鼠标事件挂在 ComboBox 上 —— Popup 里的列表项跟面板不在同一棵
        ///      可视树上，事件冒不冒得上去全看 Popup 那一层的实现，靠不住。
        ///      改成挂 Popup 的内容根（列表项和它是同一棵树，冒泡必然到）。
        /// 做法跟"图层 chip 左右拖"同一套：自己捕获鼠标、自己算落点，全程不碰 OLE。
        /// </summary>
        void HookProfileDrag()
        {
            // 模板里的 PART_Popup 要等模板套用后才有，所以等下拉打开（或窗口 Loaded）再挂
            CmbProfile.Loaded += (s, e) => AttachProfileDragRoot();
            CmbProfile.DropDownOpened += (s, e) => AttachProfileDragRoot();
        }

        void AttachProfileDragRoot()
        {
            var popup = CmbProfile.Template == null
                ? null
                : CmbProfile.Template.FindName("PART_Popup", CmbProfile) as Popup;
            var root = popup == null ? null : popup.Child as FrameworkElement;
            if (root == null || ReferenceEquals(root, _dragRoot)) return;

            _dragRoot = root;
            root.PreviewMouseLeftButtonDown += OnProfileMouseDown;
            root.PreviewMouseMove += OnProfileMouseMove;
            root.PreviewMouseLeftButtonUp += OnProfileMouseUp;
            root.LostMouseCapture += OnProfileCaptureLost;
        }

        void OnProfileMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragFromIndex = IndexOfItemAt(e.OriginalSource);
            _dragArmed = _dragFromIndex >= 0;
            _draggingProfile = false;
            _dragStart = e.GetPosition(_dragRoot);
        }

        void OnProfileMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

            Point p = e.GetPosition(_dragRoot);

            if (!_draggingProfile)
            {
                // 阈值 5px：小于这个数算"点一下选中"，不算拖
                if (Math.Abs(p.Y - _dragStart.Y) < 5 && Math.Abs(p.X - _dragStart.X) < 5) return;

                _draggingProfile = true;
                try { _dragRoot.CaptureMouse(); } catch { }
                // 拖出列表也要继续收到移动消息；光标改成上下箭头提示"能挪"
                Mouse.OverrideCursor = Cursors.SizeNS;
            }

            // 列表被意外收起来了（捕获被抢 / 别处点了）：就此打住，
            // 否则后面按 0 高度算落点，会莫名其妙把配置甩到最后一条。
            if (!CmbProfile.IsDropDownOpen)
            {
                EndProfileDrag();
                ClearDropMark();
                return;
            }

            e.Handled = true;
            MarkDropAt(_dragRoot.PointToScreen(p));
        }

        void OnProfileMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingProfile)
            {
                _dragArmed = false;
                _dragFromIndex = -1;
                ClearDropMark();
                return;
            }

            int from = _dragFromIndex;
            Point at = _dragRoot.PointToScreen(e.GetPosition(_dragRoot));
            EndProfileDrag();

            e.Handled = true;      // 别让这次抬手被当成"选中这一条"
            int to = ProfileDropIndexAt(at);
            ClearDropMark();
            if (from >= 0 && to >= 0 && to != from) MoveProfileToIndex(from, to);
        }

        void OnProfileCaptureLost(object sender, MouseEventArgs e)
        {
            // 捕获被别人抢走（拖到别的窗口去了）：收尾。否则 _draggingProfile
            // 一直为真，之后点谁都变成拖动，列表再也点不动。
            if (!_draggingProfile) return;
            EndProfileDrag();
            ClearDropMark();
        }

        /// <summary>拖拽收尾：清状态、还光标、放捕获。三处退出路径共用。</summary>
        void EndProfileDrag()
        {
            _draggingProfile = false;
            _dragArmed = false;
            _dragFromIndex = -1;
            Mouse.OverrideCursor = null;
            try { if (_dragRoot != null) _dragRoot.ReleaseMouseCapture(); } catch { }
        }

        /// <summary>
        /// 屏幕坐标落在第几条上（按每条的竖直中线分界：拖到谁的上半截就落在谁那儿）。
        /// 用屏幕坐标而不是相对坐标 —— 列表项住在 Popup 里，跟面板不在同一棵可视树上，
        /// TransformToAncestor 会直接抛异常。
        /// </summary>
        internal int ProfileDropIndexAt(Point screenPt)
        {
            int n = CmbProfile.Items.Count;
            if (n == 0) return -1;

            bool any = false;
            for (int i = 0; i < n; i++)
            {
                var item = CmbProfile.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem;
                if (item == null || item.ActualHeight <= 0) continue;
                any = true;
                Point c = item.PointToScreen(new Point(0, item.ActualHeight / 2));
                if (screenPt.Y < c.Y) return i;
            }
            return any ? n - 1 : -1;
        }

        void MarkDropAt(Point screenPt)
        {
            int i = ProfileDropIndexAt(screenPt);
            if (i < 0) { ClearDropMark(); return; }
            MarkDrop(CmbProfile.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem);
        }

        /// <summary>鼠标底下是第几条（点在内层 TextBlock 上也能找到）。找不到返回 -1。</summary>
        int IndexOfItemAt(object src)
        {
            var d = src as DependencyObject;
            while (d != null && !(d is ComboBoxItem))
            {
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            var item = d as ComboBoxItem;
            if (item == null) return -1;
            return CmbProfile.ItemContainerGenerator.IndexFromContainer(item);
        }

        /// <summary>拖动时给"将要落到的那一条"上边画一道强调线，看得见落点。</summary>
        void MarkDrop(ComboBoxItem item)
        {
            if (_dropMark == item) return;
            ClearDropMark();
            _dropMark = item;
            if (item == null) return;
            try
            {
                item.ApplyTemplate();
                var bd = item.Template.FindName("Bd", item) as Border;
                if (bd != null)
                {
                    _dropMarkKeep = new KeyValuePair<Thickness, Brush>(bd.BorderThickness, bd.BorderBrush);
                    bd.BorderBrush = UiTheme.B(UiTheme.AccentLine);
                    bd.BorderThickness = new Thickness(0, 2, 0, 0);
                }
            }
            catch { }
        }

        void ClearDropMark()
        {
            if (_dropMark == null) return;
            try
            {
                var bd = _dropMark.Template == null ? null : _dropMark.Template.FindName("Bd", _dropMark) as Border;
                if (bd != null)
                {
                    bd.BorderThickness = _dropMarkKeep.Key;
                    bd.BorderBrush = _dropMarkKeep.Value;
                }
            }
            catch { }
            _dropMark = null;
        }

        /// <summary>
        /// 把第 from 条挪到第 to 条。走 UI 这条路：先收面板改动、再挪、存盘、重建列表
        /// （重建时下拉框保持展开，方便接着调）。
        /// </summary>
        internal bool MoveProfileToIndex(int from, int to)
        {
            if (_cfg == null || _cfg.Profiles == null) return false;
            if (from < 0 || from >= _cfg.Profiles.Count) return false;
            if (to < 0 || to >= _cfg.Profiles.Count) return false;
            if (from == to) return false;

            Profile moving = _cfg.Profiles[from];
            if (moving == null || string.IsNullOrEmpty(moving.Name)) return false;

            CaptureIntoProfile(_shownProfile);

            string name = moving.Name;
            if (!_cfg.MoveProfileTo(name, to)) return false;

            ConfigStore.Save(_cfg);

            bool wasOpen = CmbProfile.IsDropDownOpen;
            Reload();
            if (wasOpen) CmbProfile.IsDropDownOpen = true;

            TxtHint.Text = "「" + name + "」已挪到第 " + (to + 1) + " 个，状态条那份清单也按这个顺序。";
            return true;
        }

        void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (_cfg == null) return;
            CaptureIntoProfile(_shownProfile);

            string name = _cfg.NextDefaultName();

            var np = new Profile { Name = name };
            Profile cur = _cfg.Active;
            if (cur != null && cur.Rules != null)
                foreach (Rule r in cur.Rules)
                {
                    // 池要深拷贝：直接共享同一个 List 会让两个配置互相改
                    var pool = new List<LayerRef>();
                    if (r.Layers != null)
                        foreach (LayerRef l in r.Layers)
                            if (l != null) pool.Add(new LayerRef(l.Name, l.ColorValue));

                    np.Rules.Add(new Rule { Category = r.Category, Layers = pool, Active = r.Active });
                }

            _cfg.Profiles.Add(np);
            _cfg.ActiveProfile = name;
            ConfigStore.Save(_cfg);
            Reload();

            // 新建完立刻让用户起名字，否则一堆「新配置 1/2/3」根本分不清谁是谁。
            // 用户点了取消也没关系，先留着自动名，之后点「改名」还能补。
            AskRename(name, "新配置的名称", "已新建「{0}」，填好图层后点保存。");
        }

        void BtnDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (_cfg == null || string.IsNullOrEmpty(_shownProfile)) return;
            CaptureIntoProfile(_shownProfile);

            Profile copy = _cfg.DuplicateProfile(_shownProfile);
            if (copy == null) return;

            ConfigStore.Save(_cfg);
            Reload();

            // 复制出来的名字同样要立刻定下来，不然很快就堆出一串「XX 副本 2/3」。
            AskRename(copy.Name, "副本的名称", "已复制为「{0}」，填好图层后点保存。");
        }

        void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_cfg == null || string.IsNullOrEmpty(_shownProfile)) return;
            if (_cfg.Profiles == null || _cfg.Profiles.Count <= 1) return;

            CaptureIntoProfile(_shownProfile);
            string name = _shownProfile;

            bool ok = ConfirmDialog.Ask(this, "删除配置",
                "确定删除配置「" + name + "」？\n\n这个配置里的图层映射会一起删掉，无法撤销。",
                "删除");
            if (!ok) return;

            if (!_cfg.RemoveProfile(name))
            {
                TxtHint.Text = "删除失败：至少要保留一个配置。";
                return;
            }

            ConfigStore.Save(_cfg);
            Reload();
            TxtHint.Text = "已删除「" + name + "」，当前配置：" + _cfg.ActiveProfile;
        }

        void BtnRename_Click(object sender, RoutedEventArgs e)
        {
            if (_cfg == null || string.IsNullOrEmpty(_shownProfile)) return;
            CaptureIntoProfile(_shownProfile);
            AskRename(_shownProfile, "配置名称", null);
        }

        /// <summary>
        /// 弹出改名框。doneTemplate 是成功后的提示模板（{0} 替换成新名字），
        /// 传 null 表示只提示「已改名为」。
        /// </summary>
        bool AskRename(string current, string label, string doneTemplate)
        {
            string newName;
            try
            {
                var dlg = new NameDialog(label, current, n => _cfg.ValidateName(n, current));
                try { dlg.Owner = this; } catch { }
                if (dlg.ShowDialog() != true) return false;
                newName = dlg.Value;
            }
            catch (Exception ex)
            {
                TxtHint.Text = "改名窗口打不开：" + ex.Message;
                return false;
            }

            if (string.Equals(newName, current, StringComparison.Ordinal))
            {
                if (doneTemplate != null) TxtHint.Text = string.Format(doneTemplate, newName);
                return true;
            }

            if (!_cfg.Rename(current, newName))
            {
                TxtHint.Text = _cfg.ValidateName(newName, current) ?? "改名失败。";
                return false;
            }

            ConfigStore.Save(_cfg);
            Reload();
            TxtHint.Text = doneTemplate != null
                ? string.Format(doneTemplate, newName)
                : "已改名为「" + newName + "」。";
            return true;
        }

        static Database HostDb()
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                return doc != null ? doc.Database : null;
            }
            catch { return null; }
        }
    }
}
