using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoLayer.UI
{
    /// <summary>
    /// 「从图纸里勾选多个图层」的对话框。
    ///
    /// 为什么要有它：配置里一类可以配多个图层，但以前只能在输入框里手打
    /// 「E-04 造型线 ; E-05 辅助线」—— 图层名根本背不下来，打错一个字母就配了个不存在的层。
    /// 这里直接把当前图纸的 LayerTable 列出来勾，勾几个算几个。
    ///
    /// 为什么不用 XAML：合并版把 UI 源码编进一个 DLL，每多一个 XAML 就多一条 BAML
    /// 资源路径要跟程序集名对齐（PieWindow / HudWindow 也是纯代码，同一个原因）。
    /// </summary>
    internal sealed class LayerPicker : Window
    {
        /// <summary>一行图层：名字 + 图纸里的颜色 + 状态（正常/已冻结/不存在…）。</summary>
        internal sealed class Entry
        {
            public string Name;
            public Color Color;
            public string State;
        }

        sealed class Row
        {
            public Entry Entry;
            public Border Box;          // 整行
            public Border Tick;         // 左边那个小方块
            public TextBlock TickText;  // 方块里的 ✓
            public TextBlock Name;
            public TextBlock State;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly List<string> _order = new List<string>();                       // 进来时的顺序
        readonly HashSet<string> _sel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly StackPanel _list = new StackPanel();
        readonly TextBlock _count = new TextBlock();
        TextBox _search = new TextBox();     // BuildSearch 里重建，不能 readonly

        List<string> _result;

        LayerPicker()
        {
            Title = "选择图层";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 400;
            Height = 500;
            FontFamily = UiTheme.Font;
            FontSize = 12;
            Foreground = UiTheme.B(UiTheme.Text);
            UseLayoutRounding = true;

            Content = BuildShell();

            // Esc 取消 / Enter 确定（焦点在搜索框里也要能用）
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { _result = null; Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            };
        }

        // ---------------------------------------------------------------- 对外

        /// <summary>
        /// 弹出来让用户勾。返回 null = 取消（调用方什么都别改）；否则是勾中的图层名，
        /// 顺序 = 原来的顺序在前、新勾的按列表顺序接在后面。
        /// </summary>
        internal static List<string> Pick(Window owner, IList<Entry> all, IList<string> selected)
        {
            var w = new LayerPicker();
            w.Fill(all, selected);
            try { if (owner != null && owner.IsVisible) w.Owner = owner; } catch { }
            w.ShowDialog();
            return w._result;
        }

        void Fill(IList<Entry> all, IList<string> selected)
        {
            if (selected != null)
                foreach (string n in selected)
                    if (!string.IsNullOrEmpty(n)) _order.Add(n);

            if (selected != null)
                foreach (string n in selected)
                    if (!string.IsNullOrEmpty(n)) _sel.Add(n);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (all != null)
            {
                foreach (Entry e in all)
                {
                    if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                    if (!seen.Add(e.Name)) continue;
                    AddRow(e);
                }
            }

            // 配置里有、图纸里没有的（比如图纸没打开时配的）也要列出来，
            // 否则一保存就被悄悄删掉，用户会觉得"我自己配的怎么没了"
            foreach (string n in _order)
            {
                if (!seen.Add(n)) continue;
                AddRow(new Entry { Name = n, Color = Colors.Transparent, State = "图纸里没有" });
            }

            ApplyFilter("");
            UpdateCount();
        }

        void Accept()
        {
            var res = new List<string>();
            foreach (string n in _order)
                if (_sel.Contains(n)) res.Add(n);
            foreach (Row r in _rows)
                if (_sel.Contains(r.Entry.Name) && !Contains(res, r.Entry.Name)) res.Add(r.Entry.Name);

            _result = res;
            Close();
        }

        static bool Contains(List<string> list, string name)
        {
            foreach (string s in list)
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- 界面

        FrameworkElement BuildShell()
        {
            var root = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = UiTheme.B(UiTheme.BgDeep),
                BorderBrush = UiTheme.White(0x1F),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(BuildTitle());

            FrameworkElement search = BuildSearch();
            Grid.SetRow(search, 1);
            grid.Children.Add(search);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(12, 0, 12, 0),
                Content = _list
            };
            Grid.SetRow(scroll, 2);
            grid.Children.Add(scroll);

            FrameworkElement footer = BuildFooter();
            Grid.SetRow(footer, 3);
            grid.Children.Add(footer);

            root.Child = grid;
            return root;
        }

        FrameworkElement BuildTitle()
        {
            var grid = new Grid { Margin = new Thickness(16, 0, 10, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "选择图层",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiTheme.B(UiTheme.Text)
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var close = MakeIconButton("✕", () => { _result = null; Close(); });
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            // 拖标题栏搬窗口
            grid.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            return grid;
        }

        FrameworkElement BuildSearch()
        {
            var wrap = new Border
            {
                Margin = new Thickness(16, 0, 16, 10),
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = UiTheme.B(UiTheme.Raised),
                BorderBrush = UiTheme.White(0x1F),
                BorderThickness = new Thickness(1)
            };

            var host = new Grid { Margin = new Thickness(9, 0, 9, 0) };

            var hint = new TextBlock
            {
                Text = "搜索图层…",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiTheme.B(UiTheme.TextFaint),
                IsHitTestVisible = false
            };
            host.Children.Add(hint);

            _search = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = UiTheme.B(UiTheme.Text),
                CaretBrush = UiTheme.B(UiTheme.AccentLine),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = UiTheme.Font,
                FontSize = 12
            };
            _search.TextChanged += (s, e) =>
            {
                hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                ApplyFilter(_search.Text);
            };
            host.Children.Add(_search);

            wrap.Child = host;
            return wrap;
        }

        FrameworkElement BuildFooter()
        {
            // 一条发丝线把"列表"和"按钮"分开，不然最后一行会贴着按钮
            var wrap = new Border
            {
                BorderBrush = UiTheme.White(0x14),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };

            var grid = new Grid { Margin = new Thickness(16, 12, 16, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _count.FontSize = 11.5;
            _count.Foreground = UiTheme.B(UiTheme.TextDim);
            _count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_count, 0);
            grid.Children.Add(_count);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };

            Border cancel = MakeButton("取消", false, () => { _result = null; Close(); });
            cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(MakeButton("确定", true, Accept));

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            wrap.Child = grid;
            return wrap;
        }

        void AddRow(Entry e)
        {
            var row = new Row { Entry = e };

            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 左边的小方块：选中就填蓝 + 白勾。不用 CheckBox，是因为 WPF 默认那个
            // 浅色方框压在深底上特别扎眼，改模板又得写一大段 ControlTemplate。
            var tick = new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };
            var tickText = new TextBlock
            {
                Text = "✓",
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                Visibility = Visibility.Collapsed
            };
            tick.Child = tickText;
            line.Children.Add(tick);

            if (e.Color != Colors.Transparent)
            {
                var sw = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(e.Color),
                    BorderBrush = UiTheme.White(0x40),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                line.Children.Add(sw);
            }

            var name = new TextBlock
            {
                Text = e.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiTheme.B(UiTheme.Text)
            };
            line.Children.Add(name);

            var rowHost = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(line);

            if (!string.IsNullOrEmpty(e.State) && e.State != "正常")
            {
                var st = new TextBlock
                {
                    Text = e.State,
                    FontSize = 10.5,
                    Margin = new Thickness(8, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = UiTheme.B(e.State == "图纸里没有" ? UiTheme.Warn : UiTheme.TextFaint)
                };
                Grid.SetColumn(st, 1);
                grid.Children.Add(st);
                row.State = st;
            }

            rowHost.Child = grid;
            row.Box = rowHost;
            row.Tick = tick;
            row.TickText = tickText;
            row.Name = name;

            rowHost.MouseLeftButtonUp += (s, ev) => { ev.Handled = true; Toggle(row); };
            rowHost.MouseEnter += (s, ev) => { if (!_sel.Contains(row.Entry.Name)) rowHost.Background = UiTheme.White(0x0F); };
            rowHost.MouseLeave += (s, ev) => { if (!_sel.Contains(row.Entry.Name)) rowHost.Background = Brushes.Transparent; };

            _rows.Add(row);
            _list.Children.Add(rowHost);

            Paint(row);
        }

        void Toggle(Row row)
        {
            string n = row.Entry.Name;
            if (_sel.Contains(n)) _sel.Remove(n);
            else _sel.Add(n);
            Paint(row);
            UpdateCount();
        }

        void Paint(Row row)
        {
            bool on = _sel.Contains(row.Entry.Name);
            row.Tick.Background = on ? UiTheme.B(UiTheme.Accent) : Brushes.Transparent;
            row.Tick.BorderBrush = on ? UiTheme.White(0x66) : UiTheme.White(0x33);
            row.TickText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            row.Box.Background = on ? UiTheme.B(UiTheme.AccentSoft) : Brushes.Transparent;
            row.Name.Foreground = on ? Brushes.White : UiTheme.B(UiTheme.Text);
            if (row.State != null) row.State.Foreground = UiTheme.B(UiTheme.TextDim);
        }

        void ApplyFilter(string q)
        {
            string s = (q ?? "").Trim();
            foreach (Row r in _rows)
            {
                bool hit = s.Length == 0
                    || r.Entry.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
                r.Box.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void UpdateCount()
        {
            int n = 0;
            foreach (Row r in _rows)
                if (_sel.Contains(r.Entry.Name)) n++;
            _count.Text = n == 0 ? "还没选" : "已选 " + n + " 个";
        }

        // ---------------------------------------------------------------- 小零件

        Border MakeIconButton(string text, Action onClick)
        {
            var b = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = UiTheme.B(UiTheme.TextDim)
                }
            };
            b.MouseEnter += (s, e) => b.Background = UiTheme.White(0x14);
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            return b;
        }

        Border MakeButton(string text, bool primary, Action onClick)
        {
            var b = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(18, 0, 18, 0),
                Background = primary ? UiTheme.B(UiTheme.Accent) : Brushes.Transparent,
                BorderBrush = primary ? UiTheme.B(UiTheme.Accent) : UiTheme.White(0x33),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = primary ? Brushes.White : UiTheme.B(UiTheme.TextDim)
                }
            };
            b.MouseEnter += (s, e) =>
            {
                b.Background = primary ? UiTheme.B(UiTheme.AccentHi) : UiTheme.White(0x14);
            };
            b.MouseLeave += (s, e) =>
            {
                b.Background = primary ? UiTheme.B(UiTheme.Accent) : Brushes.Transparent;
            };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            return b;
        }
    }
}
