using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VpLayer.Ui;

/// <summary>
/// VpPanel 预览桩：只让新的 VpPanel.xaml 脱离 AutoCAD 编译并离屏渲染，不参与出货。
/// 构造函数里塞一点示例数据，让预览能同时看到列表行、开关、下拉和生成条。
/// </summary>
public partial class VpPanel : Window
{
    /// <summary>预览模式：空 / 生成页空状态 / 标记页 / 标记页空 / 设置页（由命令行 vp:xxx 传入）。</summary>
    public static string Mode = "";

    public VpPanel()
    {
        InitializeComponent();

        // 与真实 code-behind 一致：初始选中在控件全部就位后再设
        TabGen.IsChecked = true;

        CmbFrames.Items.Add("A1 横式图框 · 841×594");
        CmbFrames.Items.Add("A2 横式图框 · 594×420");
        CmbFrames.Items.Add("A3 横式图框 · 420×297");
        CmbFrames.SelectedIndex = 0;

        CmbMode.Items.Add("每个状态一张图纸");
        CmbMode.Items.Add("一个布局，一排视口");
        CmbMode.SelectedIndex = 0;

        // 状态集下拉：两组 + 末尾三个动作项（和真面板一致）
        CmbGroup.Items.Add("默认");
        CmbGroup.Items.Add("演示集 1");
        CmbGroup.Items.Add("演示集 2");
        CmbGroup.Items.Add("＋ 新建状态集…");
        CmbGroup.Items.Add("✎ 状态集改名…");
        CmbGroup.Items.Add("✕ 删除当前状态集…");
        CmbGroup.SelectedIndex = 1;

        CmbTagProfile.Items.Add("建筑-平面图");
        CmbTagProfile.Items.Add("结构-平面图");
        CmbTagProfile.SelectedIndex = 0;

        TxtScale.Text = "自动";

        FrameLine.Text = "A1 横式图框　外框 841×594　内框 674×574（自动）";
        RowFrameTools.IsEnabled = true;
        StateHint.Text = "共 5 个 · 每个一张图纸";
        TagText.Text = "当前视口：已标记「建筑-平面图」";
        TagCountHint.Text = "共 3 个";
        BtnLayerFallback.IsChecked = true;


        var ok = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        var warn = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        var faint = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA0));
        ok.Freeze(); warn.Freeze(); faint.Freeze();

        TagDot.Fill = ok;

        LstStates.ItemsSource = new object[]
        {
            new { Index = 1, Name = "一层平面图", BindText = "建筑-平面图", BindColor = ok,    CountText = "冻结 12 层" },
            new { Index = 2, Name = "二层平面图", BindText = "建筑-平面图", BindColor = ok,    CountText = "冻结 8 层" },
            new { Index = 3, Name = "墙身大样",   BindText = "≈ 墙身-大样", BindColor = warn, CountText = "冻结 5 层" },
            new { Index = 4, Name = "立面图",     BindText = "— 无匹配",    BindColor = faint, CountText = "冻结 6 层" },
            new { Index = 5, Name = "节点详图",   BindText = "建筑-平面图", BindColor = ok,    CountText = "冻结 9 层" }
        };
        LstStates.SelectedIndex = 1;

        LstTags.ItemsSource = new object[]
        {
            new { Name = "一层平面图", Profile = "建筑-平面图", CurrentText = "" },
            new { Name = "二层平面图", Profile = "建筑-平面图", CurrentText = "←当前" },
            new { Name = "屋顶平面图", Profile = "结构-平面图", CurrentText = "" }
        };

        // 模拟真实面板三个互斥视图，离屏核对各页的布局与资源解析
        if (Mode == "empty")
        {
            LstStates.ItemsSource = null;
            LstStates.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            StateHint.Text = "还没有状态";
            RowTools.IsEnabled = false;
        }
        else if (Mode == "warn")
        {
            FrameWarn.Text = "⚠ 图框文件已经不在了，生成时会退回矩形框";
            FrameWarn.Visibility = Visibility.Visible;
        }
        else if (Mode == "many")
        {
            // 20 行：核对窗口封顶 740 之后列表是否滚动、底条是否还在原位
            var many = new System.Collections.Generic.List<object>();
            for (int i = 1; i <= 20; i++)
                many.Add(new { Index = i, Name = "视口状态 " + i, BindText = "建筑-平面图", BindColor = ok, CountText = "冻结 " + i + " 层" });

            LstStates.ItemsSource = many;
            LstStates.SelectedIndex = 0;
            StateHint.Text = "共 20 个 · 每个一张图纸";
        }
        else if (Mode == "tag")
        {
            TabTag.IsChecked = true;
            PageGen.Visibility = Visibility.Collapsed;
            PageTag.Visibility = Visibility.Visible;
            OpBar.Visibility = Visibility.Collapsed;
        }
        else if (Mode == "align" || Mode == "alignpick")
        {
            // 对齐浮层：绿（已绑）/ 绿+改名中 / 黄（有建议没选）/ 灰（没对应）四种态同屏
            var choices = new System.Collections.Generic.List<string>
            {
                "（不绑定）", "建筑-平面图", "结构-平面图", "墙身-大样"
            };

            LstAlign.ItemsSource = new object[]
            {
                new AlignVm("一层平面图", "建筑-平面图", "", 0, choices, ok),
                new AlignVm("二层平面图", "结构-平面图", "（原 二层平面图）", 1, choices, ok),
                new AlignVm("墙身大样",   "（不绑定）",   "", 0, choices, warn),
                new AlignVm("立面图",     "（不绑定）",   "", 0, choices, faint),
                new AlignVm("节点详图",   "墙身-大样",   "（配置改名）", 2, choices, ok)
            };

            AlignStat.Text = "3 / 5 已对上";
            AlignOverlay.Visibility = Visibility.Visible;

            // 选择层展开态：按第 1 行那个按钮的位置贴下去
            if (Mode == "alignpick")
            {
                LstPicker.ItemsSource = choices;
                AlignPicker.Visibility = Visibility.Visible;
                PickerBox.Margin = new Thickness(236, 156, 0, 0);
            }
        }
        else if (Mode == "tagempty")
        {
            TabTag.IsChecked = true;
            PageGen.Visibility = Visibility.Collapsed;
            PageTag.Visibility = Visibility.Visible;
            OpBar.Visibility = Visibility.Collapsed;

            LstTags.ItemsSource = null;
            LstTags.Visibility = Visibility.Collapsed;
            TagEmpty.Visibility = Visibility.Visible;
            TagCountHint.Text = "还没有";
            TagText.Text = "不在视口里 · 本图已标记 0 个";
            TagDot.Fill = faint;
        }
    }

    // ---- 事件桩：XAML 里挂了的方法都要有，空实现即可 ----
    private void OnTabChanged(object sender, RoutedEventArgs e) { }
    private void OnToggleSettings(object sender, RoutedEventArgs e) { }
    private void OnFrameSelected(object sender, SelectionChangedEventArgs e) { }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e) { }
    private void OnGroupChanged(object sender, SelectionChangedEventArgs e) { }
    private void OnSaveFrame(object sender, RoutedEventArgs e) { }
    private void OnReframeInner(object sender, RoutedEventArgs e) { }
    private void OnFrameRename(object sender, RoutedEventArgs e) { }
    private void OnFrameDelete(object sender, RoutedEventArgs e) { }
    private void OnCapture(object sender, RoutedEventArgs e) { }
    private void OnAlign(object sender, RoutedEventArgs e) { }
    private void OnAlignSameName(object sender, RoutedEventArgs e) { }
    private void OnAlignSuggest(object sender, RoutedEventArgs e) { }
    private void OnAlignRenameState(object sender, RoutedEventArgs e) { }
    private void OnAlignRenameProfile(object sender, RoutedEventArgs e) { }
    private void OnOpenPicker(object sender, RoutedEventArgs e) { }
    private void OnPickerPick(object sender, RoutedEventArgs e) { }
    private void OnPickerOutside(object sender, MouseButtonEventArgs e) { }
    private void OnAlignScroll(object sender, ScrollChangedEventArgs e) { }
    private void OnAlignCancel(object sender, RoutedEventArgs e) { }
    private void OnAlignSave(object sender, RoutedEventArgs e) { }
    private void OnMoveUp(object sender, RoutedEventArgs e) { }
    private void OnMoveDown(object sender, RoutedEventArgs e) { }
    private void OnRename(object sender, RoutedEventArgs e) { }
    private void OnShowLayers(object sender, RoutedEventArgs e) { }
    private void OnUpdate(object sender, RoutedEventArgs e) { }
    private void OnDelete(object sender, RoutedEventArgs e) { }
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) { }
    private void OnApply(object sender, RoutedEventArgs e) { }
    private void OnBatch(object sender, RoutedEventArgs e) { }
    private void OnScaleChanged(object sender, RoutedEventArgs e) { }
    private void OnScaleKeyDown(object sender, KeyEventArgs e) { }
    private void OnTag(object sender, RoutedEventArgs e) { }
    private void OnUntag(object sender, RoutedEventArgs e) { }
    private void OnRenameProfile(object sender, RoutedEventArgs e) { }
    private void OnToggleLayerFallback(object sender, RoutedEventArgs e) { }
    private void OnExportConfig(object sender, RoutedEventArgs e) { }
    private void OnImportConfig(object sender, RoutedEventArgs e) { }
}

/// <summary>
/// 对齐浮层的一行（预览用）。属性名必须和真实 code-behind 里的 AlignRow 一致，
/// DataTemplate 是按名字反射取值的，不是按类型。
/// </summary>
public sealed class AlignVm
{
    readonly string _name;
    readonly string _hint;
    readonly int _dir;
    readonly Brush _dot;

    public AlignVm(string name, string selected, string hint, int dir,
                   System.Collections.Generic.List<string> choices, Brush dot)
    {
        _name = name;
        _hint = hint;
        _dir = dir;
        _dot = dot;
        Choices = choices;
        Selected = selected;
    }

    public System.Collections.Generic.List<string> Choices { get; private set; }
    public string Selected { get; set; }

    public Brush DotFill { get { return _dot; } }

    static readonly Brush SelBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x44));

    public bool CanRenameState { get { return Selected != "（不绑定）" && Selected != _name; } }
    public bool CanRenameProfile { get { return CanRenameState; } }

    public Brush StateBtnBg { get { return _dir == 1 ? SelBg : Brushes.Transparent; } }
    public Brush ProfileBtnBg { get { return _dir == 2 ? SelBg : Brushes.Transparent; } }

    public string NameText { get { return _dir == 1 ? Selected : _name; } }

    public Brush NameBrush
    {
        get
        {
            return _dir != 0
                ? new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA))
                : new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        }
    }

    public string RenameHint { get { return _hint; } }
}
