using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoLayer.UI;

namespace AutoLayer.UI.QuickSwitch;

/// <summary>
/// 预览桩：只为让 LayerSelectionWindow.xaml 能脱离 AutoCAD 编译 / 渲染。
/// 事件处理全部空实现，不参与出货。State 决定渲染哪个模式条状态。
/// </summary>
public partial class LayerSelectionWindow : Window
{
    /// <summary>object / current / warn / error / empty</summary>
    public static string State = "object";

    /// <summary>
    /// 给离屏核对用的诊断串：确认 XAML 里那三个虚拟化附加属性真的生效了。
    /// 纯读属性，不影响渲染结果。
    /// </summary>
    public string ListDiagnostics()
    {
        int containers = 0;
        for (int i = 0; i < LayerListBox.Items.Count; i++)
            if (LayerListBox.ItemContainerGenerator.ContainerFromIndex(i) != null) containers++;

        return "IsVirtualizing=" + VirtualizingPanel.GetIsVirtualizing(LayerListBox)
             + "  CanContentScroll=" + ScrollViewer.GetCanContentScroll(LayerListBox)
             + "  ScrollUnit=" + VirtualizingPanel.GetScrollUnit(LayerListBox)
             + "  实建容器=" + containers + "/" + LayerListBox.Items.Count;
    }

    public LayerSelectionWindow()
    {
        InitializeComponent();

        for (int i = 0; i < Sample.Rows.Count; i++) LayerListBox.Items.Add(Sample.Rows[i]);
        LayerListBox.SelectedIndex = 2;

        BuildChips();
        BuildRecentRow();

        switch (State)
        {
            case "current":
                // v1.9e：回归模式下模式条只剩按钮（左侧图标+文字多余，按用户拍板收掉）
                SetModeVisual("",
                    (Brush)FindResource("Yb.Sunken"), (Brush)FindResource("Yb.Line"), (Brush)FindResource("Yb.TextDim"));
                ModeIcon.Visibility = Visibility.Collapsed;
                SetCurrentCheckBox.Visibility = Visibility.Collapsed;
                BottomHint.Visibility = Visibility.Collapsed;
                BtnReselect.Visibility = Visibility.Visible;
                BtnReselect.Content = "回到上次选中（4 个）";
                BtnApply.Content = "设为当前层";
                break;
            case "plain":
                // v1.9c 保留：纯当前层 + 没有可回归的图元 → 整条隐藏 + 左下角小字提示
                ModeBar.Visibility = Visibility.Collapsed;
                SetCurrentCheckBox.Visibility = Visibility.Collapsed;
                BottomHint.Visibility = Visibility.Visible;
                BtnApply.Content = "设为当前层";
                break;
            case "warn":
                SetModeVisual("2 个已移动，1 个在锁定图层被跳过",
                    (Brush)FindResource("Yb.WarnSoft"), (Brush)FindResource("Yb.WarnLine"), (Brush)FindResource("Yb.Warn"));
                IconWarn.Visibility = Visibility.Visible;
                BtnApply.Content = "移到该层";
                break;
            case "error":
                SetModeVisual("没能移动：图层被其他用户锁定",
                    (Brush)FindResource("Yb.DangerSoft"), (Brush)FindResource("Yb.DangerLine"), (Brush)FindResource("Yb.Danger"));
                IconError.Visibility = Visibility.Visible;
                BtnApply.Content = "移到该层";
                break;
            case "empty":
                SetModeVisual("没有匹配「e07」的图层",
                    (Brush)FindResource("Yb.Sunken"), (Brush)FindResource("Yb.Line"), (Brush)FindResource("Yb.TextDim"));
                IconLayer.Visibility = Visibility.Visible;
                LayerListBox.IsEnabled = false;
                LayerListBox.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                EmptyTitle.Text = "没有匹配「e07」的图层";
                EmptyDesc.Text = "当前只显示「E」分组";
                AddEmptyButton("清除搜索");
                AddEmptyButton("显示全部分组");
                BtnApply.IsEnabled = false;
                break;
            default: // object
                SetModeVisual("将 3 个图元移到目标层",
                    (Brush)FindResource("Yb.AccentSoft"), (Brush)FindResource("Yb.AccentSoftBorder"), (Brush)FindResource("Yb.AccentInk"));
                IconArrow.Visibility = Visibility.Visible;
                BtnApply.Content = "移到该层";
                break;
        }
    }

    private void SetModeVisual(string text, Brush bg, Brush border, Brush fg)
    {
        ModeText.Text = text;
        ModeBar.Background = bg;
        ModeBar.BorderBrush = border;
        ModeText.Foreground = fg;
    }

    private void AddEmptyButton(string label)
    {
        EmptyButtons.Children.Add(new Button
        {
            Content = label,
            Style = (Style)FindResource("SecondaryBtn"),
            Margin = new Thickness(4, 0, 4, 0),
            MinWidth = 32 + label.Length * 13
        });
    }

    private void BuildChips()
    {
        var sel = (Brush)FindResource("Yb.ChipSelBg");
        var selFg = (Brush)FindResource("Yb.ChipSelFg");
        var selBd = (Brush)FindResource("Yb.ChipSelBorder");
        var bg = (Brush)FindResource("Yb.ChipBg");
        var fg = (Brush)FindResource("Yb.ChipFg");
        (string text, int count, bool selected)[] chips =
        {
            ("全部", 12, true), ("A", 6, false), ("E", 2, false), ("其他", 4, false)
        };
        foreach (var (text, count, selected) in chips)
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new System.Windows.Documents.Run(text) { FontSize = 11.5 });
            tb.Inlines.Add(new System.Windows.Documents.Run(" " + count) { FontSize = 8.5, Foreground = (Brush)FindResource("Yb.TextFaint") });
            CategoryPanel.Children.Add(new Button
            {
                Content = tb,
                Style = (Style)FindResource("CategoryButtonStyle"),
                Tag = text,
                Background = selected ? sel : bg,
                Foreground = selected ? selFg : fg,
                BorderBrush = selected ? selBd : Brushes.Transparent,
            });
        }
    }

    private void BuildRecentRow()
    {
        // v1.9g：回到横排（与主工程一致），按宽度自适应显示几个
        RecentRow.Visibility = Visibility.Visible;
        string[] names = { "E-04 造型线", "A-柱", "DIM-标注" };
        string[] hexes = { "#00FFFF", "#FFFF00", "#FFFFFF" };
        for (int i = 0; i < names.Length; i++)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexes[i])),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = names[i], FontSize = 11.5, MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
            var chip = new System.Windows.Controls.Button
            {
                Content = content,
                Style = (Style)FindResource("RecentChipStyle"),
                Tag = names[i]
            };
            RecentPanel.Children.Add(chip);
            _recentButtons.Add(chip);
        }
    }

    // ── 空事件桩（XAML 引用的全部处理器） ──
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { }
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e) { }
    private void Window_Loaded(object sender, RoutedEventArgs e) { }
    private void Window_Deactivated(object sender, EventArgs e) { }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { }
    private void SearchBox_KeyDown(object sender, KeyEventArgs e) { }
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) { }
    private void SearchBox_LostFocus(object sender, RoutedEventArgs e) { }
    private void ClearSearchBtn_Click(object sender, RoutedEventArgs e) { }
    private void LayerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void LayerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) { }
    private void LayerListBox_PreviewKeyDown(object sender, KeyEventArgs e) { }
    private void CloseBtn_Click(object sender, RoutedEventArgs e) { }
    private void OpenAutoLayer_Click(object sender, RoutedEventArgs e) { }
    private void CategoryBar_SizeChanged(object sender, SizeChangedEventArgs e) { }
    private void RecentPanel_SizeChanged(object sender, SizeChangedEventArgs e) { }

    // ── 与主工程一致的自适应折叠：放得下几个显示几个，放不下的隐藏 ──
    private readonly List<System.Windows.Controls.Button> _recentButtons = new List<System.Windows.Controls.Button>();

    private void RecentPanel_LayoutUpdated(object sender, EventArgs e)
    {
        try
        {
            // 与主工程相同：可用宽度 = 行宽 - 「最近」标签（含右边距），
            // 不取 RecentPanel.ActualWidth（横向 StackPanel 会被排成自然宽）
            double avail = RecentRow.ActualWidth
                - RecentLabel.ActualWidth
                - RecentLabel.Margin.Left - RecentLabel.Margin.Right;
            if (avail <= 0) return;
            double x = 0.0;
            foreach (System.Windows.Controls.Button b in _recentButtons)
            {
                if (b.ActualWidth <= 0) return;
                bool fits = x + b.ActualWidth + 4 <= avail + 0.5;
                b.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
                if (fits) x += b.ActualWidth + 4;
            }
        }
        catch { }
    }
    private void RecentPanel_PreviewKeyDown(object sender, KeyEventArgs e) { }
    private void ApplyBtn_Click(object sender, RoutedEventArgs e) { }
    private void ApplyBtn_MouseEnter(object sender, MouseEventArgs e) { }
    private void ApplyBtn_MouseLeave(object sender, MouseEventArgs e) { }
    private void CancelBtn_Click(object sender, RoutedEventArgs e) { }
    private void Reselect_Click(object sender, RoutedEventArgs e) { }
}
