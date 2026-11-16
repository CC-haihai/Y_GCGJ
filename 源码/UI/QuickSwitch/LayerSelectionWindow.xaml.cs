using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using AutoLayer.Core;

namespace AutoLayer.UI.QuickSwitch;

public partial class LayerSelectionWindow : Window
{
	// ───────────────────────── 常量 ─────────────────────────

	private const string CAT_ALL = "__all__";
	private const string CAT_OTHER = "__other__";

	/// <summary>模式条四态（§6.15）：底 + 描边 + 图标 + 文字色四条同时区分。</summary>
	private enum ModeVisual
	{
		Object,     // AccentSoft + 箭头：「将 N 个图元移到目标层」
		Current,    // Sunken + 图层：「仅切换当前图层」
		Warn,       // WarnSoft + 三角：部分成功 / 层已关闭 / 冻结 / 图元失效
		Error       // DangerSoft + 叉：应用失败
	}

	// ───────────────────────── 数据 ─────────────────────────

	private ObjectId[] _selectedObjectIds;
	private readonly List<LayerInfo> _allLayers;
	private readonly List<string> _initialRecentLayerNames;
	private ObservableCollection<LayerInfo> _displayedLayers = new ObservableCollection<LayerInfo>();

	private string _confirmedLayerName;
	private bool _isApplying;
	private LayerInfo _pendingPreviewLayer;
	private List<LayerCategory> _categories = new List<LayerCategory>();
	private string _selectedCategoryName = CAT_ALL;

	/// <summary>正在整体替换列表内容：这期间 SelectionChanged 是中间态，不要当成用户操作。</summary>
	private bool _updatingList;

	/// <summary>已识别的分类前缀（大写）。「其他」判定靠它，别再每层遍历一遍分类列表。</summary>
	private HashSet<string> _knownPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>前缀 → 该前缀下第一个图层名。只给 tooltip 用，建一次就够。</summary>
	private Dictionary<string, string> _firstLayerOfPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private static double _savedWidth = 0.0;
	private static double _savedHeight = 0.0;
	private bool _isInitializing = true;

	// ── 新增结构状态（02-ux-spec §3.2–§3.10，已拍板） ──
	private bool _objectMode;                   // 打开时有预选 →「移到该层」；「回到上次选中」也会置真
	private bool _objectsInvalidated;           // 图元全部失效 → 降级为只切当前层
	private string _resultMessage;              // 成功后给命令行的一句（宿主拼 [图层切换] 前缀）

	// chip 折叠（§3.5.3：最多 2 行，超出末尾 +N，chip 区永不滚动）
	private readonly List<Button> _chipButtons = new List<Button>();
	private Button _plusNButton;
	private Popup _foldPopup;
	private int _chipFocusIndex = -1;

	// 最近快捷行（§6.16：1 个 Tab 停靠点，←→ 移动，Enter 应用；v1.9g 横排按宽度自适应）
	private readonly List<Button> _recentButtons = new List<Button>();
	private int _recentFocusIndex = -1;

	// ───────────────────────── 对宿主暴露 ─────────────────────────

	public string ConfirmedLayerName => _confirmedLayerName;
	public bool SetAsCurrent => (SetCurrentCheckBox?.IsChecked ?? false);
	/// <summary>应用成功后写命令行的一句话（含「可用 U 撤销」等口径，§3.8）。</summary>
	public string ResultMessage => _resultMessage;

	// ───────────────────────── 构造 ─────────────────────────

	public LayerSelectionWindow(ObjectId[] selectedObjectIds, List<LayerInfo> allLayers, List<string> recentLayerNames, bool setCurrent = false)
	{
		InitializeComponent();
		var chrome = new System.Windows.Shell.WindowChrome
		{
			GlassFrameThickness = new Thickness(0),
			CornerRadius = new CornerRadius(0),
			CaptionHeight = 32,
			ResizeBorderThickness = new Thickness(4)
		};
		System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

		// 全局异常兜底：WPF 布局/渲染管线异常不能冒泡到 AutoCAD 宿主
		Dispatcher.UnhandledException += (s, e) =>
		{
			try { AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nQLAYER 未处理异常(已拦截): " + e.Exception.Message); } catch { }
			e.Handled = true;
		};

		_selectedObjectIds = selectedObjectIds ?? new ObjectId[0];
		_allLayers = allLayers ?? new List<LayerInfo>();
		_initialRecentLayerNames = recentLayerNames ?? new List<string>();
		_objectMode = _selectedObjectIds.Length > 0;

		// 上次勾选状态从 ini 带进来
		try { if (setCurrent) SetCurrentCheckBox.IsChecked = true; } catch { }
		SetCurrentCheckBox.Visibility = _objectMode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		BtnApply.Content = _objectMode ? "移到该层" : "设为当前层";

		if (_savedWidth > 0.0 && _savedHeight > 0.0)
		{
			base.Width = _savedWidth;
			base.Height = _savedHeight;
		}

		_categories = LayerCategoryService.DetectCategories(_allLayers.Select(l => l.Name));
		BuildCategoryIndex();
		BuildCategoryButtons();
		BuildRecentRow();
		ApplySearchFilter();
		LayerListBox.ItemsSource = _displayedLayers;
		SearchBox.Text = "";
		UpdatePlaceholder();
		UpdateModeBar();
		UpdateApplyButton();

		base.SizeChanged += delegate
		{
			if (!_isInitializing)
			{
				_savedWidth = base.Width;
				_savedHeight = base.Height;
			}
		};
		base.Loaded += delegate
		{
			_isInitializing = false;
			// 图纸无图层：焦点落在空状态的「关闭」上（§3.9）；否则自动聚焦搜索
			if (_allLayers.Count == 0)
			{
				var btn = FindEmptyStateButton("关闭");
				if (btn != null) { btn.Focus(); Keyboard.Focus(btn); }
			}
			else
			{
				SearchBox.Focus();
				Keyboard.Focus(SearchBox);
			}
			ScheduleChipFold();
		};

		// chip 组键盘流（§3.6：组只占 1 个 Tab 停靠点）
		CategoryPanel.PreviewKeyDown += CategoryPanel_PreviewKeyDown;
	}

	// ───────────────────────── chip 词条（§3.5.1 前缀算法） ─────────────────────────

	/// <summary>
	/// 预建两张表：已知前缀集合 + 前缀→首个图层名。
	///
	/// 原来这两件事都是在使用点上现算的：判定「其他」要把分类列表遍历一遍，
	/// tooltip 要对每个分类把全部图层扫一遍（分类数 × 图层数）。
	/// 建一次之后，判定是一次哈希查、tooltip 是一次字典取。
	/// </summary>
	private void BuildCategoryIndex()
	{
		_knownPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		_firstLayerOfPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (LayerCategory c in _categories)
		{
			if (c == null || c.IsOther) continue;
			_knownPrefixes.Add(c.Name);
		}

		foreach (LayerInfo l in _allLayers)
		{
			if (l == null) continue;
			string p = l.Prefix ?? "";
			if (p.Length == 0 || _firstLayerOfPrefix.ContainsKey(p)) continue;
			_firstLayerOfPrefix[p] = l.Name;
		}
	}

	private void BuildCategoryButtons()
	{
		CategoryPanel.Children.Clear();
		_chipButtons.Clear();
		_chipFocusIndex = -1;

		if (_allLayers.Count == 0)
		{
			// 唯一不显示 chip 行的情况：图纸无图层（§3.5.3）
			CategoryBar.Visibility = System.Windows.Visibility.Collapsed;
			return;
		}
		CategoryBar.Visibility = System.Windows.Visibility.Visible;

		_chipButtons.Add(MakeChip(CAT_ALL, "全部", _allLayers.Count, null));
		foreach (LayerCategory category in _categories)
		{
			if (category.IsOther && category.Count <= 0) continue;
			string tip = BuildCategoryTooltip(category);
			_chipButtons.Add(MakeChip(category.Name, category.IsOther ? "其他" : category.DisplayName, category.Count, tip));
		}
		UpdateCategorySelection(_selectedCategoryName);
	}

	/// <summary>词条 = 前缀原文（大写）11.5 + 8.5 淡色计数；tooltip = 「N 个图层」（超 8 字退化时补来源说明，§3.5.2）。</summary>
	private Button MakeChip(string tag, string text, int count, string tooltip)
	{
		var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
		tb.Inlines.Add(new System.Windows.Documents.Run(text) { FontSize = 11.5 });
		tb.Inlines.Add(new System.Windows.Documents.Run(" " + count) { FontSize = 8.5, Foreground = FindResource("Yb.TextFaint") as Brush });
		Button button = new Button
		{
			Content = tb,
			Style = FindResource("CategoryButtonStyle") as Style,
			Tag = tag,
			ToolTip = tooltip ?? (count + " 个图层"),
			Focusable = false
		};
		button.Click += CategoryButton_Click;
		CategoryPanel.Children.Add(button);
		return button;
	}

	/// <summary>超 8 字符退化组（如 ELEVATION → ELE）必须在 tooltip 说清来源，不得静默（§3.5.2）。</summary>
	private string BuildCategoryTooltip(LayerCategory category)
	{
		string raw;
		if (!_firstLayerOfPrefix.TryGetValue(category.Name ?? "", out raw)) return category.Count + " 个图层";
		if (raw == null) return category.Count + " 个图层";
		string firstSegment = raw.Split('-', '_', '.')[0].Trim();
		if (firstSegment.Length > 8)
			return $"{category.Count} 个图层 · 前缀「{firstSegment}」超过 8 字，取前 3 字";
		return category.Count + " 个图层";
	}

	private void CategoryButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string tag })
		{
			_selectedCategoryName = tag;
			UpdateCategorySelection(tag);
			ApplySearchFilter();
			// 换 chip 后焦点留在 chip 组（回车即过滤结果第一行可达 ↓）
		}
	}

	private void UpdateCategorySelection(string selectedName)
	{
		foreach (object child in CategoryPanel.Children)
		{
			if (child is Button { Tag: string tag } button && button != _plusNButton)
			{
				ApplyChipVisual(button, tag == selectedName);
			}
		}
		// +N 折叠态：被折叠的组被选中时由 +N 承载选中态（§3.5.3）
		if (_plusNButton != null)
		{
			bool foldedSelected = _foldedTags != null && _foldedTags.Contains(selectedName);
			ApplyChipVisual(_plusNButton, foldedSelected);
			_plusNButton.ToolTip = foldedSelected
				? $"当前筛选：{DisplayNameOfCategory(selectedName)}（另有 {_foldedTags.Count - 1} 个已折叠分类）"
				: null;
		}
	}

	private void ApplyChipVisual(Button button, bool selected)
	{
		if (selected)
		{
			button.Background = FindResource("Yb.ChipSelBg") as Brush;
			button.Foreground = FindResource("Yb.ChipSelFg") as Brush;
			button.BorderBrush = FindResource("Yb.ChipSelBorder") as Brush;
		}
		else
		{
			button.Background = FindResource("Yb.ChipBg") as Brush;
			button.Foreground = FindResource("Yb.ChipFg") as Brush;
			button.BorderBrush = Brushes.Transparent;
		}
	}

	private string DisplayNameOfCategory(string tag)
	{
		if (tag == CAT_ALL) return "全部";
		if (tag == CAT_OTHER) return "其他";
		return _categories.FirstOrDefault(c => c.Name == tag)?.DisplayName ?? tag;
	}

	// ───────────────────────── chip 折叠：最多 2 行 + 末尾 +N（§3.5.3） ─────────────────────────

	private List<string> _foldedTags;

	private void CategoryBar_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		ScheduleChipFold();
	}

	private void ScheduleChipFold()
	{
		Dispatcher.BeginInvoke((Action)RecomputeChipFold, DispatcherPriority.Background);
	}

	/// <summary>
	/// chip 区永不滚动：放得下 2 行的照常铺开，第 2 行末尾放 +N（折叠从字母序末位开始）。
	/// 在布局完成后按 ActualWidth 计算，窗口宽度变化时重算。
	/// </summary>
	private void RecomputeChipFold()
	{
		try
		{
			double panelWidth = CategoryPanel.ActualWidth;
			if (panelWidth <= 0 || CategoryBar.Visibility != System.Windows.Visibility.Visible) return;

			double x = 0.0;
			int row = 1;
			List<string> folded = new List<string>();
			bool overflow = false;

			foreach (object child in CategoryPanel.Children)
			{
				if (child is not Button b || b == _plusNButton) continue;
				double w = b.ActualWidth + 4; // Margin 右 4
				if (x + w > panelWidth + 0.5)
				{
					row++;
					x = 0;
					if (row > 2) { overflow = true; break; }
				}
				x += w;
			}

			if (!overflow)
			{
				_foldedTags = null;
				foreach (object child in CategoryPanel.Children)
					if (child is Button b && b != _plusNButton) b.Visibility = System.Windows.Visibility.Visible;
				if (_plusNButton != null) { _plusNButton.Visibility = System.Windows.Visibility.Collapsed; }
				return;
			}

			// 重新走一遍，把放不下的折进 +N（从字母序末位开始，其他/后位优先被折）
			x = 0.0; row = 1; folded.Clear();
			double plusWidth = 46.0; // "+N" 估宽（含 margin）
			foreach (object child in CategoryPanel.Children)
			{
				if (child is not Button b || b == _plusNButton) continue;
				double w = b.ActualWidth + 4;
				double reserve = (row == 2) ? plusWidth : 0.0; // 第 2 行要给 +N 留位
				if (x + w + reserve > panelWidth + 0.5)
				{
					row++;
					x = 0;
					reserve = (row == 2) ? plusWidth : 0.0;
					if (row > 2) { folded.Add((string)b.Tag); b.Visibility = System.Windows.Visibility.Collapsed; continue; }
				}
				if (row > 2) { folded.Add((string)b.Tag); b.Visibility = System.Windows.Visibility.Collapsed; continue; }
				x += w;
				b.Visibility = System.Windows.Visibility.Visible;
			}

			if (folded.Count > 0)
			{
				EnsurePlusNButton();
				_plusNButton.Content = "+" + folded.Count;
				_plusNButton.Visibility = System.Windows.Visibility.Visible;
				_foldedTags = folded;
				UpdateCategorySelection(_selectedCategoryName);
			}
		}
		catch { }
	}

	private void EnsurePlusNButton()
	{
		if (_plusNButton != null) return;
		_plusNButton = new Button
		{
			Content = "+N",
			Style = FindResource("CategoryButtonStyle") as Style,
			Tag = "__fold__",
			Focusable = false,
			ToolTip = "已折叠的分类"
		};
		_plusNButton.Click += PlusN_Click;
		CategoryPanel.Children.Add(_plusNButton);
	}

	private void PlusN_Click(object sender, RoutedEventArgs e)
	{
		if (_foldedTags == null || _foldedTags.Count == 0) return;
		try
		{
			if (_foldPopup != null) { _foldPopup.IsOpen = false; return; }

			var list = new ListBox
			{
				Background = FindResource("Yb.Raised") as Brush,
				Foreground = FindResource("Yb.Text") as Brush,
				BorderBrush = FindResource("Yb.LineStrong") as Brush,
				BorderThickness = new Thickness(1),
				FontSize = 11.5,
				MinWidth = 140,
				MaxHeight = 28 * 6 // 项高 28，系统滚动条（不自定义模板，§6.13）
			};
			list.SelectionChanged += FoldList_SelectionChanged;
			list.PreviewKeyDown += FoldList_PreviewKeyDown;
			foreach (string tag in _foldedTags)
			{
				var cat = _categories.FirstOrDefault(c => c.Name == tag);
				list.Items.Add(new FoldItem { Tag = tag, Display = DisplayNameOfCategory(tag) + "  " + (cat?.Count ?? 0) });
			}

			_foldPopup = new Popup
			{
				Child = list,
				PlacementTarget = _plusNButton,
				Placement = PlacementMode.Bottom,
				StaysOpen = false,
				AllowsTransparency = true
			};
			_foldPopup.Closed += (s, _) => { _foldPopup = null; try { _plusNButton.Focus(); } catch { } };
			_foldPopup.IsOpen = true;
			list.Focus();
		}
		catch { }
	}

	private sealed class FoldItem
	{
		public string Tag;
		public string Display;
		public override string ToString() => Display;
	}

	private void FoldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (sender is ListBox list && list.SelectedItem is FoldItem item && _foldPopup != null)
		{
			_selectedCategoryName = item.Tag;
			if (_foldPopup != null) _foldPopup.IsOpen = false;
			UpdateCategorySelection(item.Tag);
			ApplySearchFilter();
		}
	}

	private void FoldList_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			if (_foldPopup != null) _foldPopup.IsOpen = false;
			e.Handled = true;
		}
	}

	// ───────────────────────── 最近快捷行（§6.16） ─────────────────────────

	/// <summary>只收当前图纸里还存在的最近层（宿主已过滤，这里再防御一次）。</summary>
	private List<LayerInfo> RecentLayers()
	{
		List<LayerInfo> result = new List<LayerInfo>();
		foreach (string name in _initialRecentLayerNames)
		{
			LayerInfo info = _allLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
			if (info != null) result.Add(info);
			if (result.Count >= 5) break; // ≤5，第 6 个起不再出现
		}
		return result;
	}

	private void BuildRecentRow()
	{
		RecentPanel.Children.Clear();
		_recentButtons.Clear();
		_recentFocusIndex = -1;
		foreach (LayerInfo layer in RecentLayers())
		{
			var content = new StackPanel { Orientation = Orientation.Horizontal };
			var swatch = new Border
			{
				Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
				Background = layer.ColorBrush,
				BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
				BorderThickness = new Thickness(1),
				VerticalAlignment = VerticalAlignment.Center
			};
			content.Children.Add(swatch);
			// MaxWidth 显式约束 → 竖排那轮的"横向 StackPanel 无限宽"坑在这里不成立（截断发生在 120 内）
			content.Children.Add(new TextBlock
			{
				Text = layer.Name,
				FontSize = 11.5,
				MaxWidth = 120,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(6, 0, 0, 0)
			});
			Button chip = new Button
			{
				Content = content,
				Style = FindResource("RecentChipStyle") as Style,
				Tag = layer.Name,
				Focusable = false,
				ToolTip = layer.Name + "（单击预览，双击应用）"
			};
			chip.Click += RecentChip_Click;
			chip.MouseDoubleClick += RecentChip_DoubleClick;
			RecentPanel.Children.Add(chip);
			_recentButtons.Add(chip);
		}
		ScheduleRecentFold(); // 布局完成后按可用宽度决定显示几个
	}

	/// <summary>出现条件：搜索为空 且 chip = 全部 且 最近 ≥2 项（§3.5）；不满足整行收起，不留空白高度。</summary>
	private void UpdateRecentRowVisibility()
	{
		bool show = _allLayers.Count > 0
			&& string.IsNullOrEmpty(SearchBox.Text)
			&& _selectedCategoryName == CAT_ALL
			&& _recentButtons.Count >= 2;
		RecentRow.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		if (show) ScheduleRecentFold();
	}

	// ── 最近 chip 自适应（v1.9g）：面板放得下几个显示几个，放不下的隐藏（无 +N）──

	private void RecentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		ScheduleRecentFold();
	}

	// 布局一更新就重算（v1.9e 实测失败根因：BeginInvoke 调度的时机 chip 宽度还没量出来，
	// 判成"全放得下"→ 溢出）。LayoutUpdated 在 Arrange 之后触发，宽度必然就绪；
	// 计算本身极轻（≤5 个按钮的加法），频繁触发无压力
	private void RecentPanel_LayoutUpdated(object sender, EventArgs e)
	{
		RecomputeRecentFold();
	}

	private void ScheduleRecentFold()
	{
		Dispatcher.BeginInvoke((Action)RecomputeRecentFold, DispatcherPriority.Loaded);
		Dispatcher.BeginInvoke((Action)RecomputeRecentFold, DispatcherPriority.Render);
	}

	private void RecomputeRecentFold()
	{
		try
		{
			if (RecentRow.Visibility != System.Windows.Visibility.Visible) return;
			// 可用宽度 = 行宽 - 「最近」标签（含右边距）。不取 RecentPanel.ActualWidth：
			// 实测横向 StackPanel 在 * 列里会被排成自然宽（v1.9e 实机溢出的根因），
			// 而外层 Border 的 ActualWidth 始终等于内容区实际宽度，确定可靠。
			double avail = RecentRow.ActualWidth
				- RecentLabel.ActualWidth
				- RecentLabel.Margin.Left - RecentLabel.Margin.Right;
			if (avail <= 0) return;

			double x = 0.0;
			foreach (Button b in _recentButtons)
			{
				if (b.ActualWidth <= 0) return; // 还有 chip 没量出来，等下一次 LayoutUpdated
				bool fits = x + b.ActualWidth + 4 <= avail + 0.5; // Margin 右 4
				b.Visibility = fits ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
				if (fits) x += b.ActualWidth + 4;
			}
		}
		catch { }
	}

	/// <summary>单击只"看"：把该层设为列表当前项 + 图纸预览；双击才"做"（§3.7）。</summary>
	private void RecentChip_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string name })
		{
			LayerInfo match = _displayedLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
			if (match != null) LayerListBox.SelectedItem = match; // 触发 SelectionChanged → 预览
		}
	}

	private void RecentChip_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (sender is Button { Tag: string name })
		{
			LayerInfo match = _displayedLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
			if (match != null) { LayerListBox.SelectedItem = match; ApplyCurrent(); }
		}
	}

	private void RecentPanel_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		// 只在可见 chip 间移动（v1.9g：宽度不够被隐藏的不参与 ←→，与分类 chip 同规则）
		List<Button> visible = _recentButtons
			.Where(b => b.Visibility == System.Windows.Visibility.Visible).ToList();
		if (visible.Count == 0) return;
		if (_recentFocusIndex >= visible.Count) _recentFocusIndex = -1;
		if (e.Key == Key.Left || e.Key == Key.Right) // v1.9g 回到横排：←→ 移动
		{
			int delta = (e.Key == Key.Left) ? -1 : 1;
			MoveRovingFocus(visible, ref _recentFocusIndex, delta);
			e.Handled = true;
		}
		else if (e.Key == Key.Return || e.Key == Key.Space)
		{
			// Enter 语义 = 应用当前项（快捷行 chip 直接应用该层）
			if (_recentFocusIndex >= 0 && _recentFocusIndex < visible.Count)
			{
				string name = visible[_recentFocusIndex].Tag as string;
				LayerInfo match = _displayedLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
				if (match != null) { LayerListBox.SelectedItem = match; ApplyCurrent(); }
			}
			e.Handled = true;
		}
	}

	// ───────────────────────── 筛选（§3.5：搜索 ∧ chip，打字即重置 chip） ─────────────────────────

	private LayerCategory SelectedCategory()
	{
		for (int i = 0; i < _categories.Count; i++)
			if (string.Equals(_categories[i].Name, _selectedCategoryName, StringComparison.Ordinal))
				return _categories[i];
		return null;
	}

	private bool MatchesCurrentCategory(LayerInfo layer)
	{
		if (_selectedCategoryName == CAT_ALL) return true;

		LayerCategory selected = SelectedCategory();
		if (selected == null) return true;

		// 前缀在建 LayerInfo 时就算好了（LayerService.GetAllLayers 里），直接取。
		// 原来是每个图层现算一次，搜索框每敲一个字就是几百次字符串切分。
		string prefix = layer.Prefix ?? LayerCategoryService.ExtractPrefix(layer.Name);

		if (selected.IsOther)
		{
			// 「其他」= 不属于任何已识别前缀的那批。已知前缀是 HashSet，一次查表；
			// 原来每层都要把分类列表整个遍历一遍。
			return !_knownPrefixes.Contains(prefix);
		}
		return string.Equals(prefix, selected.Name, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>列表里只有数据行：无分组标题、无"最近"重复项（红线 15）。</summary>
	private void ApplySearchFilter()
	{
		string value = SearchBox.Text?.Trim() ?? "";
		bool noKeyword = value.Length == 0;

		// 先在普通 List 里筛完，最后整体替换 —— 不要边筛边往 ObservableCollection 里塞，
		// 那样每加一条就发一次 CollectionChanged，几百层时打字明显掉帧。
		var next = new List<LayerInfo>(_allLayers.Count);
		for (int i = 0; i < _allLayers.Count; i++)
		{
			LayerInfo layer = _allLayers[i];
			if (layer == null) continue;
			if (!MatchesCurrentCategory(layer)) continue;
			if (!noKeyword && layer.Name.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) continue;
			next.Add(layer);
		}

		SetDisplayed(next);

		UpdateRecentRowVisibility();
		UpdateEmptyState();
		UpdateApplyButton();
		UpdateModeBar();
	}

	/// <summary>
	/// 整体换掉列表内容。内容和现在一模一样时直接返回（打字到某个字符时很常见）。
	///
	/// 换的时候走「新建集合 → 一次性赋给 ItemsSource」，只发一次 Reset 通知；
	/// Clear + 逐条 Add 会发 N 次。选中项按对象引用还原，_updatingList 期间
	/// SelectionChanged 是中间态，不当作"用户换了目标层"。
	/// </summary>
	private void SetDisplayed(List<LayerInfo> next)
	{
		if (SameAsDisplayed(next)) return;

		LayerInfo keep = LayerListBox.SelectedItem as LayerInfo;
		var fresh = new ObservableCollection<LayerInfo>(next);

		_updatingList = true;
		try
		{
			LayerListBox.ItemsSource = fresh;
			_displayedLayers = fresh;

			if (keep != null)
			{
				int idx = next.IndexOf(keep);
				if (idx >= 0) LayerListBox.SelectedItem = keep;
			}
		}
		finally { _updatingList = false; }
	}

	private bool SameAsDisplayed(List<LayerInfo> next)
	{
		if (next.Count != _displayedLayers.Count) return false;
		for (int i = 0; i < next.Count; i++)
			if (!ReferenceEquals(next[i], _displayedLayers[i])) return false;
		return true;
	}

	// ───────────────────────── 空状态（§3.9 四类，内联，绝不弹窗） ─────────────────────────

	private void UpdateEmptyState()
	{
		string keyword = SearchBox.Text?.Trim() ?? "";
		bool chipAll = _selectedCategoryName == CAT_ALL;

		if (_allLayers.Count == 0)
		{
			ShowEmptyState("当前图纸没有图层，无法切换", null, EmptyAction.Close);
		}
		else if (_displayedLayers.Count == 0 && keyword.Length > 0 && chipAll)
		{
			ShowEmptyState($"没有匹配「{keyword}」的图层", "改个关键词，或检查拼写", EmptyAction.ClearSearch);
		}
		else if (_displayedLayers.Count == 0 && keyword.Length > 0 && !chipAll)
		{
			ShowEmptyState($"没有匹配「{keyword}」的图层", $"当前只显示「{DisplayNameOfCategory(_selectedCategoryName)}」分组",
				EmptyAction.ClearSearch, EmptyAction.ShowAllGroups);
		}
		else if (_displayedLayers.Count == 0 && keyword.Length == 0 && !chipAll)
		{
			ShowEmptyState($"「{DisplayNameOfCategory(_selectedCategoryName)}」分组下没有图层", "这个前缀的图层可能都在别的分组里", EmptyAction.ShowAll);
		}
		else
		{
			EmptyState.Visibility = System.Windows.Visibility.Collapsed;
			LayerListBox.IsEnabled = true;
		}
	}

	private enum EmptyAction { ClearSearch, ShowAllGroups, ShowAll, Close }

	/// <summary>三行结构：标题 / 说明 / 出路按钮行；按钮一律 32 高次按钮（空状态内禁止主按钮）。</summary>
	private void ShowEmptyState(string title, string desc, params EmptyAction[] actions)
	{
		EmptyState.Visibility = System.Windows.Visibility.Visible;
		EmptyTitle.Text = title;
		EmptyDesc.Text = desc ?? "";
		EmptyDesc.Visibility = string.IsNullOrEmpty(desc) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
		EmptyButtons.Children.Clear();

		bool hasLayers = _allLayers.Count > 0;
		LayerListBox.IsEnabled = hasLayers;

		foreach (EmptyAction action in actions)
		{
			string label = action switch
			{
				EmptyAction.ClearSearch => "清除搜索",
				EmptyAction.ShowAllGroups => "显示全部分组",
				EmptyAction.ShowAll => "显示全部",
				_ => "关闭"
			};
			Button button = new Button
			{
				Content = label,
				Style = FindResource("SecondaryBtn") as Style,
				Tag = action,
				Margin = new Thickness(4, 0, 4, 0),
				MinWidth = 32 + label.Length * 13
			};
			button.Click += EmptyButton_Click;
			EmptyButtons.Children.Add(button);
		}
	}

	private Button FindEmptyStateButton(string label)
	{
		return EmptyButtons.Children.OfType<Button>().FirstOrDefault(b => b.Content as string == label);
	}

	private void EmptyButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: EmptyAction action }) return;
		switch (action)
		{
			case EmptyAction.ClearSearch:
				// 回搜索框并全选残留文字（接着打即覆盖）
				SearchBox.Text = "";
				SearchBox.Focus();
				Keyboard.Focus(SearchBox);
				SearchBox.SelectAll();
				break;
			case EmptyAction.ShowAllGroups:
				// 回搜索框但保留关键词
				SetCategory(CAT_ALL, keepFocusInSearch: true);
				break;
			case EmptyAction.ShowAll:
				// 回列表首行；若无行则回搜索框
				SetCategory(CAT_ALL, keepFocusInSearch: false);
				if (_displayedLayers.Count > 0)
				{
					LayerListBox.SelectedIndex = 0;
					// 虚拟化下先滚到首行并跑一次布局，容器才存在
					LayerListBox.ScrollIntoView(_displayedLayers[0]);
					LayerListBox.UpdateLayout();
					LayerListBox.Focus();
					(LayerListBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
				}
				else
				{
					SearchBox.Focus(); Keyboard.Focus(SearchBox);
				}
				break;
			case EmptyAction.Close:
				CancelAndClose();
				break;
		}
	}

	private void SetCategory(string tag, bool keepFocusInSearch)
	{
		_selectedCategoryName = tag;
		UpdateCategorySelection(tag);
		ApplySearchFilter();
		if (keepFocusInSearch)
		{
			SearchBox.Focus();
			Keyboard.Focus(SearchBox);
			SearchBox.SelectAll();
		}
	}

	// ───────────────────────── 模式条（§3.4 / §6.15） ─────────────────────────

	/// <summary>模式条唯一权威指示：几个图元、去哪个层、要不要顺带改当前层（§3.4）。</summary>
	private void UpdateModeBar()
	{
		// 应用失败/部分成功的告警优先显示（保持面板打开时的上下文）
		string warnText = null, errorText = null;
		if (_lastApplyOutcome == ApplyOutcome.Error) errorText = _lastApplyMessage;
		else if (_lastApplyOutcome == ApplyOutcome.Partial) warnText = _lastApplyMessage;
		// 目标层状态内联提示（替代 MessageBox，§3.8 / B3）
		else if (CurrentLayer() is LayerInfo cur)
		{
			if (cur.IsOff) warnText = "该图层当前已关闭，应用时会自动打开";
			else if (cur.IsFrozen) warnText = "该图层已冻结，设为当前层后无法绘制";
		}

		string text;
		ModeVisual visual;
		if (errorText != null) { text = errorText; visual = ModeVisual.Error; }
		else if (warnText != null) { text = warnText; visual = ModeVisual.Warn; }
		else if (_objectMode && !_objectsInvalidated)
		{
			text = $"将 {_selectedObjectIds.Length} 个图元移到目标层";
			visual = ModeVisual.Object;
		}
		else if (_objectsInvalidated)
		{
			text = "图元已失效，将只切换当前图层";
			visual = ModeVisual.Warn;
		}
		else
		{
			text = "仅切换当前图层（未选中任何图元）";
			visual = ModeVisual.Current;
		}

		// 无选中行只把主按钮置灰（v1.9：常驻「· 先选一个目标图层」后缀按用户拍板删除——
		// 挤占模式条宽度还和回归按钮叠字，置灰的主按钮已经足够说明问题）
		ModeText.Text = text;
		ApplyModeVisual(visual);

		// 「回到上次选中 ▸」只在手头没有可用图元时出现（v1.9 拍板）：
		// · 当前层模式（空手打开）→ 有上次的图元就给回归入口；
		// · 对象模式手头已有选中直接改 → 不需要（用户原话：这种情况没必要有改选）；
		// · 图元失效降级 → 也给（上次的图元若还活着）。
		bool recallShown = UpdateRecallButton();

		// v1.9c（用户拍板）：纯当前层模式（无告警无错误）整条隐藏 ——
		// 「仅切换当前图层」常驻占一整行不值，提示挪到底部左下角小字。
		// v1.9d（用户拍板）：回归按钮回到模式条右侧原位（左下角不顺手），
		// 所以有回归按钮时模式条跟着占条，按钮所在的槽位就是它原来待的地方。
		// v1.9e（用户拍板）：回归模式下左侧的「仅切换当前图层（未选中…）」文字多余
		// —— 按钮文案已自说明，图标和文字都收掉，条里只留按钮。
		bool plainCurrent = !_objectMode && errorText == null && warnText == null;
		if (plainCurrent && recallShown)
		{
			ModeIcon.Visibility = System.Windows.Visibility.Collapsed;
			ModeText.Text = "";
		}
		ModeBar.Visibility = (!plainCurrent || recallShown)
			? System.Windows.Visibility.Visible
			: System.Windows.Visibility.Collapsed;
	}

	/// <summary>
	/// 「回到上次选中」按钮（v1.9d 回到模式条右侧原位）：当前层模式/图元失效时出现；
	/// 宿主负责过滤已删/失效图元。返回按钮是否可见（模式条据此决定要不要占条）。
	/// </summary>
	private bool UpdateRecallButton()
	{
		ObjectId[] last = QuickSwitchHost.GetLastValidPicked();
		bool show = !_isApplying
			&& last != null
			&& (!_objectMode || _objectsInvalidated);
		BtnReselect.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		if (show)
		{
			string screenName = $"回到上次选中（{last.Length} 个）";
			System.Windows.Automation.AutomationProperties.SetName(BtnReselect, screenName);
			BtnReselect.ToolTip = "把这批图元重新装回本次操作（Alt+R）";
			BtnReselect.Content = screenName;
		}
		// 左下角提示只在「当前层模式 + 有图层 + 没有回归按钮」时显示：
		// 有回归按钮时模式条已占条，左侧文字就是模式说明，同显会重复
		BottomHint.Visibility = (!_objectMode && _allLayers.Count > 0 && !show)
			? System.Windows.Visibility.Visible
			: System.Windows.Visibility.Collapsed;
		return show;
	}

	private void ApplyModeVisual(ModeVisual visual)
	{
		IconArrow.Visibility = visual == ModeVisual.Object ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		IconLayer.Visibility = visual == ModeVisual.Current ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		IconWarn.Visibility = visual == ModeVisual.Warn ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		IconError.Visibility = visual == ModeVisual.Error ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

		switch (visual)
		{
			case ModeVisual.Object:
				ModeBar.Background = FindResource("Yb.AccentSoft") as Brush;
				ModeBar.BorderBrush = FindResource("Yb.AccentSoftBorder") as Brush;
				ModeText.Foreground = FindResource("Yb.AccentInk") as Brush;
				break;
			case ModeVisual.Current:
				ModeBar.Background = FindResource("Yb.Sunken") as Brush;
				ModeBar.BorderBrush = FindResource("Yb.Line") as Brush;
				ModeText.Foreground = FindResource("Yb.TextDim") as Brush;
				break;
			case ModeVisual.Warn:
				ModeBar.Background = FindResource("Yb.WarnSoft") as Brush;
				ModeBar.BorderBrush = FindResource("Yb.WarnLine") as Brush;
				ModeText.Foreground = FindResource("Yb.Warn") as Brush;
				break;
			case ModeVisual.Error:
				ModeBar.Background = FindResource("Yb.DangerSoft") as Brush;
				ModeBar.BorderBrush = FindResource("Yb.DangerLine") as Brush;
				ModeText.Foreground = FindResource("Yb.Danger") as Brush;
				break;
		}
	}

	private enum ApplyOutcome { None, Partial, Error }
	private ApplyOutcome _lastApplyOutcome = ApplyOutcome.None;
	private string _lastApplyMessage;

	private void SetApplyFeedback(ApplyOutcome outcome, string message)
	{
		_lastApplyOutcome = outcome;
		_lastApplyMessage = message;
		UpdateModeBar();
	}

	// ───────────────────────── 搜索框 ─────────────────────────

	private void UpdatePlaceholder()
	{
		SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
	}

	private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => UpdatePlaceholder();
	private void SearchBox_LostFocus(object sender, RoutedEventArgs e) => UpdatePlaceholder();

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		// 打字即重置 chip（§3.5 关键规则 1）：避免"chip 还筛着 E、搜索又叠一层"的假空态
		if (!string.IsNullOrEmpty(SearchBox.Text) && _selectedCategoryName != CAT_ALL)
		{
			_selectedCategoryName = CAT_ALL;
			UpdateCategorySelection(CAT_ALL);
		}
		ApplySearchFilter();
		UpdateClearButtonVisibility();
		UpdatePlaceholder();
	}

	private void ClearSearchBtn_Click(object sender, RoutedEventArgs e) => ClearSearch(keepFilter: true);

	private void UpdateClearButtonVisibility()
	{
		ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
	}

	private void SearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			// 应用"当前项"；无当前项则应用第一可见行；列表为空时无操作（不静默关窗）
			ApplyCurrent();
			e.Handled = true;
		}
		else if (e.Key == Key.Down)
		{
			SelectFirstVisibleInList();
			e.Handled = true;
		}
		else if (e.Key == Key.Up)
		{
			SelectLastVisibleInList();
			e.Handled = true;
		}
	}

	private void ClearSearch(bool keepFilter)
	{
		SearchBox.Text = "";
		SearchBox.Focus();
		Keyboard.Focus(SearchBox);
		if (!keepFilter) ApplySearchFilter();
	}

	// ───────────────────────── 列表 ─────────────────────────

	private LayerInfo CurrentLayer() => LayerListBox.SelectedItem as LayerInfo;

	private LayerInfo FirstVisibleLayer() => _displayedLayers.FirstOrDefault();

	private void LayerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// 整体换列表时的中间态（选中项先变 null 再还原），不是用户换了目标层。
		// ApplySearchFilter 结尾会自己刷按钮和模式条，这里直接放行即可。
		if (_updatingList) return;

		if (!_isApplying && LayerListBox.SelectedItem is LayerInfo layerInfo)
		{
			TriggerPreview(layerInfo);
		}
		// 换了目标层 = 上下文变了，上一次的"部分成功/失败"告警不再压住模式文案
		if (_lastApplyOutcome != ApplyOutcome.None) _lastApplyOutcome = ApplyOutcome.None;
		UpdateApplyButton();
		UpdateModeBar();
	}

	private void LayerListBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			case Key.Return:
				if (CurrentLayer() is LayerInfo layerInfo) ApplyCurrent();
				e.Handled = true;
				break;
			case Key.Back:
				// 焦点回搜索框并删除最后一个字符（"打错一个字母"的最短修正路径）
				SearchBox.Focus();
				Keyboard.Focus(SearchBox);
				if (SearchBox.Text.Length > 0)
				{
					SearchBox.Text = SearchBox.Text.Substring(0, SearchBox.Text.Length - 1);
					SearchBox.CaretIndex = SearchBox.Text.Length;
				}
				e.Handled = true;
				break;
			case Key.Home:
				if (_displayedLayers.Count > 0) LayerListBox.SelectedIndex = 0;
				e.Handled = true;
				break;
			case Key.End:
				if (_displayedLayers.Count > 0) LayerListBox.SelectedIndex = _displayedLayers.Count - 1;
				e.Handled = true;
				break;
			case Key.Prior: // PageUp
				LayerListBox.SelectedIndex = Math.Max(0, LayerListBox.SelectedIndex - 10);
				e.Handled = true;
				break;
			case Key.Next: // PageDown
				LayerListBox.SelectedIndex = Math.Min(_displayedLayers.Count - 1, LayerListBox.SelectedIndex + 10);
				e.Handled = true;
				break;
			case Key.Up:
				MoveListSelection(-1);
				e.Handled = true;
				break;
			case Key.Down:
				MoveListSelection(1);
				e.Handled = true;
				break;
		}
	}

	/// <summary>↑↓ 移动当前项，到顶/底停住、不回绕（§3.6）。</summary>
	private void MoveListSelection(int delta)
	{
		if (_displayedLayers.Count == 0) return;
		int index = LayerListBox.SelectedIndex;
		int next = (index < 0)
			? (delta > 0 ? 0 : _displayedLayers.Count - 1)
			: Math.Min(Math.Max(index + delta, 0), _displayedLayers.Count - 1);
		if (next == index) return;
		LayerListBox.SelectedIndex = next;
		LayerListBox.ScrollIntoView(_displayedLayers[next]);
	}

	private void LayerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (CurrentLayer() != null) ApplyCurrent();
	}

	private void SelectFirstVisibleInList()
	{
		if (_displayedLayers.Count == 0) return;
		LayerListBox.SelectedIndex = 0;
		// 虚拟化开启后容器是「滚到才生成」的，必须先滚过去、跑一次布局，再取容器。
		// 顺序反了的话 ContainerFromIndex 拿到 null，焦点就停在 ListBox 上而不是行上。
		LayerListBox.ScrollIntoView(_displayedLayers[0]);
		LayerListBox.UpdateLayout();
		LayerListBox.Focus();
		(LayerListBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
	}

	private void SelectLastVisibleInList()
	{
		if (_displayedLayers.Count == 0) return;
		int last = _displayedLayers.Count - 1;
		LayerListBox.SelectedIndex = last;
		LayerListBox.ScrollIntoView(_displayedLayers[last]);
		LayerListBox.UpdateLayout();
		LayerListBox.Focus();
		(LayerListBox.ItemContainerGenerator.ContainerFromIndex(last) as ListBoxItem)?.Focus();
	}

	// ───────────────────────── 预览 ─────────────────────────

	private void TriggerPreview(LayerInfo layer)
	{
		if (_isApplying || layer == null || _pendingPreviewLayer == layer) return;
		_pendingPreviewLayer = layer;
		Dispatcher.BeginInvoke((Action)delegate
		{
			if (_isApplying || _pendingPreviewLayer != layer) return;
			try { PreviewHighlighter.HighlightLayer(layer.Name); }
			catch { }
		}, DispatcherPriority.Background);
	}

	// ───────────────────────── 应用（§3.3 / §3.4 / §3.8） ─────────────────────────

	private void UpdateApplyButton()
	{
		bool hasSelection = CurrentLayer() != null;
		BtnApply.IsEnabled = hasSelection && _allLayers.Count > 0;
		UpdateApplyButtonText(hovered: false);
	}

	private void UpdateApplyButtonText(bool hovered)
	{
		if (_isApplying) return;
		LayerInfo layer = CurrentLayer();
		bool objectTask = _objectMode && !_objectsInvalidated;
		if (layer != null && hovered)
		{
			// 悬停补目标层名：按下之前就知道"去哪个层"（§3.4 结果预览）
			BtnApply.Content = objectTask ? $"移到 {layer.Name}" : $"设为当前层：{layer.Name}";
		}
		else
		{
			BtnApply.Content = objectTask ? "移到该层" : "设为当前层";
		}
	}

	private void ApplyBtn_Click(object sender, RoutedEventArgs e) => ApplyCurrent();
	private void ApplyBtn_MouseEnter(object sender, MouseEventArgs e) => UpdateApplyButtonText(hovered: true);
	private void ApplyBtn_MouseLeave(object sender, MouseEventArgs e) => UpdateApplyButtonText(hovered: false);

	/// <summary>唯一的应用入口：主按钮 / Enter / 双击 / 快捷行 Enter 全走这里（"当前项"是唯一概念，§3.6）。</summary>
	private void ApplyCurrent()
	{
		if (_isApplying) return;
		LayerInfo layer = CurrentLayer() ?? FirstVisibleLayer();
		if (layer == null || _allLayers.Count == 0) return;
		if (LayerListBox.SelectedItem != layer) LayerListBox.SelectedItem = layer; // 无当前项时取第一可见行
		_isApplying = true;
		try
		{
			bool objectTask = _objectMode && !_objectsInvalidated;
			bool wasOff = false;
			try
			{
				wasOff = LayerService.IsLayerOff(layer.Name);
				if (wasOff) LayerService.TurnOnLayer(layer.Name); // 自动打开，不再 MessageBox 打断（P1-3）
			}
			catch { }

			bool switchedToCurrentOnly = false;
			if (objectTask)
			{
				LayerService.MoveResult result = LayerService.MoveObjectsToLayerEx(_selectedObjectIds, layer.Name);

				// 图元已经换层了，预览索引里的归属全部过期 —— 下一次预览必须重建
				if (result.Moved > 0) PreviewHighlighter.InvalidateIndex();

				if (result.Moved == 0 && result.Skipped > 0)
				{
					// 全部失败：面板保持打开，模式条错误态写明原因（§3.8）
					SetApplyFeedback(ApplyOutcome.Error, "没能移动：" + (result.FirstError ?? "目标图层被锁定"));
					_isApplying = false;
					return;
				}
				if (result.Erased == _selectedObjectIds.Length)
				{
					// 图元已失效：降级为只切当前层（§3.4 第三行）
					_objectsInvalidated = true;
					switchedToCurrentOnly = true;
					SetCurrentCheckBox.Visibility = System.Windows.Visibility.Collapsed;
				}
				else if (result.Skipped > 0 || result.Erased > 0)
				{
					// 部分成功：保持面板打开，保留上下文让用户处理剩下的（§3.8）
					string skippedDetail = result.Erased > 0 && result.Skipped == 0
						? $"{result.Erased} 个图元已删除"
						: $"{result.Skipped + result.Erased} 个被跳过";
					SetApplyFeedback(ApplyOutcome.Partial, $"{result.Moved} 个已移动，{skippedDetail}");
					RecentLayerManager.RecordUsage(layer.Name);
					_isApplying = false;
					UpdateApplyButton();
					return;
				}
				else
				{
					_resultMessage = $"{result.Moved} 个图元 → {layer.Name}（可用 U 撤销）";
				}
			}
			if (!objectTask || switchedToCurrentOnly)
			{
				_resultMessage = $"已设为当前图层：{layer.Name}";
			}

			if (wasOff) _resultMessage += "，已自动打开图层";

			bool setAsCurrent = (!_objectMode || _objectsInvalidated || SetAsCurrent);
			if (setAsCurrent) LayerService.SetCurrentLayer(layer.Name);
			RecentLayerManager.RecordUsage(layer.Name);
			_confirmedLayerName = layer.Name;
			CloseWindow(); // 成功才关窗
		}
		catch (Exception ex)
		{
			SetApplyFeedback(ApplyOutcome.Error, "没能移动：" + ex.Message);
			try { AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nQLAYER 错误: " + ex.Message); } catch { }
			_isApplying = false;
		}
	}

	// ───────────────────────── 取消 / 改选 / 关窗 ─────────────────────────

	private void CancelAndClose()
	{
		if (!_isApplying)
		{
			_isApplying = true;
			_confirmedLayerName = null;
			CloseWindow();
		}
	}

	private void CloseBtn_Click(object sender, RoutedEventArgs e) => CancelAndClose();
	private void CancelBtn_Click(object sender, RoutedEventArgs e) => CancelAndClose();

	/// <summary>
	/// 「回到上次选中 ▸」：把上次带进面板的那批图元重新装回本次操作（v1.9）。
	/// 这是「改选」唯一保留的形态 —— 空手打开 CC 时一键回归上次改过图层的那批物体；
	/// 手头已有选中直接改时不出现。已删/失效的图元由宿主过滤，全没了按钮自动消失。
	/// </summary>
	private void Reselect_Click(object sender, RoutedEventArgs e)
	{
		if (_isApplying) return;
		ObjectId[] last = QuickSwitchHost.GetLastValidPicked();
		if (last == null || last.Length == 0)
		{
			UpdateModeBar();
			return;
		}

		_selectedObjectIds = last;
		_objectMode = true;
		_objectsInvalidated = false;
		_lastApplyOutcome = ApplyOutcome.None;
		_lastApplyMessage = null;

		// 与构造函数对象模式的初始状态对齐
		SetCurrentCheckBox.Visibility = System.Windows.Visibility.Visible;
		BtnApply.Content = "移到该层";

		// 把这批图元在图上亮出来：implied selection（grips，面板关掉后 CAD 拿回焦点就出）
		// + Entity.Highlight 直接亮显（面板开着、焦点被模态窗压住时也立刻看得见，v1.9d）。
		// 之后点行做图层预览会临时替换高亮，关窗时 RestoreSelection 会还原 recalled 集
		try { PreviewHighlighter.RestoreSelectionWithHighlight(_selectedObjectIds); } catch { }

		UpdateModeBar();
		UpdateApplyButton();
		LayerListBox.Focus();
	}

	private void CloseWindow()
	{
		try { PreviewHighlighter.RestoreSelection(_selectedObjectIds); }
		catch { }
		PersistPreference();
		Close();
	}

	/// <summary>上一次真正写进 ini 的勾选值；null = 还没写过。</summary>
	private bool? _persistedSetCurrent;

	/// <summary>把「同时设为当前图层」的勾选状态记回 AutoLayer.ini。</summary>
	private void PersistPreference()
	{
		try
		{
			Settings st = ConfigStore.Current.Settings;
			if (st == null) return;

			bool want = SetAsCurrent;
			// 关窗会走两条路（CloseWindow 里一次、OnClosed 里再兜一次），
			// 值没变就别重复落盘 —— Save 是整文件原子写，写两次纯属浪费。
			if (_persistedSetCurrent == want && st.QuickSwitchSetCurrent == want) return;

			st.QuickSwitchSetCurrent = want;
			ConfigStore.Save(ConfigStore.Current);
			_persistedSetCurrent = want;
		}
		catch { }
	}

	// ───────────────────────── 全局键盘流（§3.6） ─────────────────────────

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
		var modifiers = Keyboard.Modifiers;

		// Ctrl+F：焦点回搜索框并全选
		if (key == Key.F && modifiers.HasFlag(ModifierKeys.Control))
		{
			SearchBox.Focus(); Keyboard.Focus(SearchBox); SearchBox.SelectAll();
			e.Handled = true;
			return;
		}
		// Alt+W：切换「同时设为当前图层」（仅对象模式）
		if (key == Key.W && modifiers.HasFlag(ModifierKeys.Alt))
		{
			if (SetCurrentCheckBox.Visibility == System.Windows.Visibility.Visible)
			{
				SetCurrentCheckBox.IsChecked = !(SetCurrentCheckBox.IsChecked == true);
			}
			e.Handled = true;
			return;
		}
		// Alt+R：「改选 ▸」（仅对象模式 / 图元失效态）
		if (key == Key.R && modifiers.HasFlag(ModifierKeys.Alt))
		{
			if (BtnReselect.Visibility == System.Windows.Visibility.Visible) Reselect_Click(this, new RoutedEventArgs());
			e.Handled = true;
			return;
		}
		// Esc：折叠浮层优先；有搜索词先清词，第二次才关窗（§3.6）
		if (key == Key.Escape)
		{
			if (_foldPopup != null && _foldPopup.IsOpen)
			{
				_foldPopup.IsOpen = false;
				e.Handled = true;
				return;
			}
			if (!string.IsNullOrEmpty(SearchBox.Text))
			{
				SearchBox.Text = "";
				e.Handled = true;
				return;
			}
			CancelAndClose();
			e.Handled = true;
			return;
		}
	}

	/// <summary>任意位置直接输入 = 焦点跳搜索框并把字符打进去（"输入即搜索"，§3.6 硬约束）。</summary>
	private void Window_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Text)) return;
		if (e.OriginalSource is TextBox sourceTextBox && ReferenceEquals(sourceTextBox, SearchBox)) return;
		if (e.OriginalSource is TextBox) return; // 其他输入框（本窗没有，防御）
		char c = e.Text[0];
		if (char.IsControl(c)) return;
		// Space 在按钮上 = 激活意图（chip 组 Enter/Space），不该跑进搜索词
		if (c == ' ' && Keyboard.FocusedElement is Button) return;
		try
		{
			SearchBox.Focus();
			Keyboard.Focus(SearchBox);
			SearchBox.AppendText(e.Text);
			SearchBox.CaretIndex = SearchBox.Text.Length;
			e.Handled = true;
		}
		catch { }
	}

	// ───────────────────────── chip 组键盘（roving focus，1 个 Tab 停靠点） ─────────────────────────

	private void CategoryPanel_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		// 当前可见的 chip（折叠后隐藏的不参与 ←→，但折叠项仍可从 +N 浮层进入）
		List<Button> visible = CategoryPanel.Children.OfType<Button>()
			.Where(b => b.Visibility == System.Windows.Visibility.Visible).ToList();
		if (visible.Count == 0) return;
		if (_chipFocusIndex >= visible.Count) _chipFocusIndex = -1;
		if (e.Key == Key.Left || e.Key == Key.Right)
		{
			int delta = (e.Key == Key.Left) ? -1 : 1;
			MoveRovingFocus(visible, ref _chipFocusIndex, delta);
			e.Handled = true;
		}
		else if (e.Key == Key.Return || e.Key == Key.Space)
		{
			if (_chipFocusIndex >= 0 && _chipFocusIndex < visible.Count)
			{
				visible[_chipFocusIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent, visible[_chipFocusIndex]));
			}
			e.Handled = true;
		}
	}

	/// <summary>组内 roving focus：任何时刻只有一个成员 Focusable，←→ 换人，换行不增加停靠点。</summary>
	private static void MoveRovingFocus(List<Button> buttons, ref int index, int delta)
	{
		if (buttons.Count == 0) return;
		index = (index < 0) ? 0 : Math.Min(Math.Max(index + delta, 0), buttons.Count - 1);
		for (int i = 0; i < buttons.Count; i++) buttons[i].Focusable = (i == index);
		buttons[index].Focus();
	}

	// ───────────────────────── 其他 ─────────────────────────

	protected override void OnClosed(EventArgs e)
	{
		try { PreviewHighlighter.RestoreSelection(_selectedObjectIds); }
		catch { }
		PersistPreference();
		base.OnClosed(e);
	}

	/// <summary>标题栏的齿轮位：跳到归层主面板（V1.0 保留功能，只换了图标）。</summary>
	private void OpenAutoLayer_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			_isApplying = true;
			PersistPreference();
			try { PreviewHighlighter.RestoreSelection(_selectedObjectIds); } catch { }
			Close();
		}
		catch { }

		try { AutoLayer.UI.PanelHost.Show(); } catch { }
	}
}
