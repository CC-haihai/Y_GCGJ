using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoLayer.UI
{
    /// <summary>
    /// SettingsWindow 的预览桩：只提供 XAML 里挂的事件处理器签名，
    /// 不带任何业务逻辑 —— 排版核对用（真实 code-behind 在主工程里）。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>预览哪一页：null/空 = 设置页，"help" = 帮助页。</summary>
        public static string Page;

        static readonly string[] SlotNames = { "不用", "右键长按", "侧键后退", "侧键前进" };

        public SettingsWindow()
        {
            InitializeComponent();

            // 真窗口在 Load() 里加下拉项（不能写 XAML 的 SelectedIndex），桩这里照做
            AddItems(CmbMouse0); AddItems(CmbMouse1); AddItems(CmbMouse2); AddItems(CmbMouse3);

            // 摆一套看起来像真配置的值，四种鼠标键状态都能看到
            TxtKey0.Text = "Ctrl+Shift+Q";
            TxtKey1.Text = "无";
            TxtKey1.Foreground = System.Windows.Media.Brushes.Gray;
            TxtKey2.Text = "无";
            TxtKey2.Foreground = System.Windows.Media.Brushes.Gray;
            TxtKey3.Text = "无";
            TxtKey3.Foreground = System.Windows.Media.Brushes.Gray;

            CmbMouse0.SelectedIndex = 0;   // 主面板 · 不用
            CmbMouse1.SelectedIndex = 0;   // 图层切换 · 不用
            CmbMouse2.SelectedIndex = 2;   // 视口工具 · 侧键后退
            CmbMouse3.SelectedIndex = 1;   // 圆盘 · 右键长按

            // 出厂状态：四个命令别名全留空，显示占位符（= 默认命令名）
            TxtCcAlias.Text = "";
            PhAlias.Visibility = Visibility.Visible;
            PhCcAlias.Visibility = Visibility.Visible;
            PhVpAlias.Visibility = Visibility.Visible;
            PhPieAlias.Visibility = Visibility.Visible;

            SldHold.Value = 180;
            TxtHold.Text = "180 ms";

            // 真窗口只显示文件名，全路径挂 ToolTip
            TxtCfgPath.Text = "AutoLayer.ini";
            TxtCfgPath.ToolTip = @"C:\Users\Administrator\AppData\Local\YGuiCeng\AutoLayer.ini";
            TxtVpPath.Text = "ViewportState.ini";
            TxtVpPath.ToolTip = @"C:\Users\Administrator\AppData\Local\YGuiCeng\ViewportState.ini";

            // 页签默认落在设置页（真窗口在 Load() 里做，不能写 XAML 的 IsChecked）
            PgSetup.IsChecked = true;
            if (Page == "help") PgHelp.IsChecked = true;
        }

        static void AddItems(ComboBox c)
        {
            for (int i = 0; i < SlotNames.Length; i++) c.Items.Add(SlotNames[i]);
        }

        void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (PaneSetup == null) return;
            bool help = PgHelp.IsChecked == true;
            PaneSetup.Visibility = help ? Visibility.Collapsed : Visibility.Visible;
            PaneHelp.Visibility = help ? Visibility.Visible : Visibility.Collapsed;
        }

        void KeyBox_Click(object sender, MouseButtonEventArgs e) { }
        void Mouse_Changed(object sender, SelectionChangedEventArgs e) { }
        void Alias_TextChanged(object sender, TextChangedEventArgs e) { }
        void Hold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { }
        void BtnVpExport_Click(object sender, RoutedEventArgs e) { }
        void BtnVpImport_Click(object sender, RoutedEventArgs e) { }
        void Window_PreviewKeyDown(object sender, KeyEventArgs e) { }
        void BtnSave_Click(object sender, RoutedEventArgs e) { }
        void BtnExportConfig_Click(object sender, RoutedEventArgs e) { }
        void BtnImportConfig_Click(object sender, RoutedEventArgs e) { }
    }
}
