using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AutoLayer.UI;

/// <summary>主面板预览桩：只让 PanelWindow.xaml 能脱离 AutoCAD 编译渲染，不参与出货。</summary>
public partial class PanelWindow : Window
{
    public PanelWindow()
    {
        InitializeComponent();

        // 通用演示数据（不涉及任何公司/项目/客户信息），只用于离屏出图
        CmbProfile.Items.Add("通用演示配置");
        CmbProfile.SelectedIndex = 0;
        TglEnabled.IsChecked = true;

        // 与真实面板同一套 RowBorder 样式，构造几行通用类别
        Style rowStyle = (Style)FindResource("RowBorder");
        string[][] demo = {
            new[]{"墙体",   "A-墙"},
            new[]{"门窗",   "A-门窗"},
            new[]{"柱",     "A-柱"},
            new[]{"楼梯",   "A-楼梯"},
            new[]{"标注",   "DIM-标注"},
            new[]{"填充",   "H-填充"},
        };
        foreach (var r in demo)
        {
            var border = new Border { Style = rowStyle };
            var grid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = r[0], FontSize = 12,
                Foreground = (Brush)FindResource("Yb.Text"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0); grid.Children.Add(label);

            var box = new ComboBox { IsEditable = true, Height = 26, Text = r[1], FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(box, 1); grid.Children.Add(box);

            border.Child = grid;
            RowsPanel.Children.Add(border);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e) { }
    private void BtnSettings_Click(object sender, RoutedEventArgs e) { }
    private void BtnQuickSwitch_Click(object sender, RoutedEventArgs e) { }
    private void TglEnabled_Changed(object sender, RoutedEventArgs e) { }
    private void CmbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void BtnNew_Click(object sender, RoutedEventArgs e) { }
    private void BtnRename_Click(object sender, RoutedEventArgs e) { }
    private void BtnDuplicate_Click(object sender, RoutedEventArgs e) { }
    private void BtnDelete_Click(object sender, RoutedEventArgs e) { }
    private void BtnSave_Click(object sender, RoutedEventArgs e) { }
    // 主面板后来加了「视口」入口，预览桩补上，否则 XAML 事件挂接编不过
    private void BtnViewport_Click(object sender, RoutedEventArgs e) { }
}
