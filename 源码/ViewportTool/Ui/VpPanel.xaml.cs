using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AutoLayer.Core;
// 不能 using DatabaseServices：那个命名空间里也有一个 Visibility 枚举，
// 会和 System.Windows.Visibility 撞车，整个文件里的可见性判断全报 CS0176
using AcDb = Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace VpLayer.Ui
{
    /// <summary>
    /// 轻量面板：图框模板管理、视口状态列表、批量生成入口。
    ///
    /// 需要点选图元的操作（拾取图框、刷取状态、批量生成）一律转成 CAD 命令执行 ——
    /// 非模态 WPF 窗口里直接调 Editor.GetEntity 会失败，这是 AutoCAD 的硬限制。
    /// 命令执行期间面板整个收起来，结束后再弹回来，免得挡着点选。
    /// </summary>
    public partial class VpPanel : Window
    {
        static VpPanel _win;

        /// <summary>填充列表时置位，避免程序设 SelectedIndex 触发 SelectionChanged 又去写盘。</summary>
        bool _loading;

        /// <summary>面板弹回来时的重试计数，防止 CAD 一直报「有命令在跑」导致面板回不来。</summary>
        int _restoreTries;

        public static void Open()
        {
            if (_win == null)
            {
                _win = new VpPanel();
                _win.Closed += (s, e) => { _win = null; };
            }

            _win.Reload();

            if (_win.IsVisible)
            {
                _win.Activate();
                return;
            }

            try
            {
                var helper = new WindowInteropHelper(_win);
                helper.Owner = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle;
            }
            catch { }

            _win.Show();
            _win.Activate();
        }

        /// <summary>面板开着就按最新数据重摆一遍（设置窗口里导入视口配置后调）。</summary>
        public static void RefreshIfOpen()
        {
            try { if (_win != null) _win.Reload(); } catch { }
        }

        /// <summary>ApplyPane 里程序改 IsChecked 时挡住 Checked 事件，避免递归。</summary>
        bool _applying;

        public VpPanel()
        {
            InitializeComponent();

            // Tab 的 Checked 在 InitializeComponent 期间就会触发，那时后面的面板控件还没建好，
            // OnTabChanged → ApplyPane 会摸到 null 字段，整个构造失败 → VPT 面板打不开。
            // 初始选中放到控件全部就位后再设，并用 _applying 挡住这次事件。
            _applying = true;
            try { TabGen.IsChecked = true; }
            finally { _applying = false; }

            // 主面板走同一套深色标题栏；不调用的话深色窗口会顶着一条系统白标题栏
            SourceInitialized += delegate { AutoLayer.UI.DarkTitleBar.Apply(this); };

            // 进/出视口时自动刷新「当前视口」那一行；关窗口要退订，否则静态事件会按着死窗口不放
            Loaded += delegate
            {
                VpProfileWatcher.Changed -= OnWatcherChanged;
                VpProfileWatcher.Changed += OnWatcherChanged;
                ApplyPane();
                UpdateTagText();
            };
            Closed += delegate { VpProfileWatcher.Changed -= OnWatcherChanged; };
        }

        // ------------------------------------------------------------------ 任务 Tab

        /// <summary>两个任务页互斥；底部生成条只在生成页出现。</summary>
        void ApplyPane()
        {
            // InitializeComponent 期间 Checked 会提前触发；此时这些字段还是 null，直接放过。
            if (TabGen == null || TabTag == null || PageGen == null || PageTag == null || OpBar == null) return;

            _applying = true;
            try
            {
                if (TabGen.IsChecked != true && TabTag.IsChecked != true) TabGen.IsChecked = true;
            }
            finally { _applying = false; }

            bool gen = TabGen.IsChecked == true;
            PageGen.Visibility = gen ? Visibility.Visible : Visibility.Collapsed;
            PageTag.Visibility = gen ? Visibility.Collapsed : Visibility.Visible;
            OpBar.Visibility = gen ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (_applying) return;
            ApplyPane();
        }

        /// <summary>
        /// 标题栏那个滑杆图标：打开主设置窗口。
        /// 设一次就不动的东西（视口配置导出导入）都收到那儿去了 ——
        /// 面板只留每天要点的操作，这样 ⚙ 也不再是"第二层设置页"。
        /// </summary>
        void OnToggleSettings(object sender, RoutedEventArgs e)
        {
            try
            {
                bool imported;
                AutoLayer.UI.SettingsWindow.Edit(this, ConfigStore.Current, out imported);
            }
            catch (Exception ex)
            {
                Trace.Add("打开设置窗口失败: " + ex.GetType().Name + " " + ex.Message);
            }

            // 设置里可能导入了新的视口配置，回来必须按新数据重摆
            Reload();
        }

        // ------------------------------------------------------------------ 刷新

        sealed class VpRow
        {
            public int Index { get; set; }
            public string Name { get; set; }
            public string CountText { get; set; }

            /// <summary>绑定判定：→ 已绑 / = 同名 / ≈ 接近 / — 无匹配。</summary>
            public string BindText { get; set; }

            public Brush BindColor { get; set; }
        }

        /// <summary>「已标记视口」列表的一行。</summary>
        sealed class TagRow
        {
            public string Name { get; set; }
            public string Profile { get; set; }
            public string CurrentText { get; set; }
        }

        /// <summary>标记列表的上次内容签名，内容没变就不重建，免得定时刷新时闪。</summary>
        string _tagSig = "";

        // 语义色一律取视觉令牌（源码/UI/UiTheme.cs），不在这里自造 #RRGGBB
        static readonly Brush Green  = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Ok);
        static readonly Brush Yellow = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Warn);
        static readonly Brush Gray   = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.TextDim);
        static readonly Brush TextBrush = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Text);

        /// <summary>设置图标点亮时用（设置页开着）。</summary>
        static readonly Brush AccentLine = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.AccentLine);

        /// <summary>对齐表里方向按钮被选中时的底色（和列表选中同一个令牌）。</summary>
        static readonly Brush SelectionBg = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.SelectionBg);
        static readonly Brush Transparent = System.Windows.Media.Brushes.Transparent;

        /// <summary>
        /// 图框那一行的人话摘要：名称 + 外框 + 内框。
        /// 以前这些摊在 ⚙ 的三行里，现在压成一行放一级；放不下就省略号，
        /// 真正需要注意的（文件丢了 / 没尺寸）由 <see cref="FrameWarn"/> 单独一行说。
        /// </summary>
        /// <summary>图框那一行的摘要。设置窗口的「视口工具」页也用它，所以不是 private。</summary>
        internal static string FrameSummary(VpConfig cfg)
        {
            FrameTemplate cur = cfg == null ? null : cfg.Frame;
            if (cur == null || !cur.Valid)
                return "还没有图框　—　点「拾取图框」从图纸里选一个";

            string s = cur.DisplayName
                     + "　外框 " + Math.Round(cur.Width) + "×" + Math.Round(cur.Height);

            s += cur.HasInner
                ? "　内框 " + Math.Round(cur.DrawW) + "×" + Math.Round(cur.DrawH)
                  + (cur.InnerManual ? "（手动）" : "（自动）")
                : "　内框未识别";

            return s;
        }

        void Reload()
        {
            VpConfig cfg = VpStore.Current;

            // ---- 图框选择器 ----
            _loading = true;

            var frameItems = new List<string>();
            for (int i = 0; i < cfg.Frames.Count; i++)
            {
                FrameTemplate f = cfg.Frames[i];
                if (f == null) { frameItems.Add("（空）"); continue; }
                frameItems.Add(f.DisplayName + "　" + Math.Round(f.Width) + "×" + Math.Round(f.Height));
            }

            CmbFrames.ItemsSource = frameItems;
            CmbFrames.SelectedIndex = frameItems.Count == 0
                ? -1
                : Math.Min(Math.Max(cfg.FrameIndex, 0), frameItems.Count - 1);

            CmbMode.ItemsSource = new List<string> { "每个状态一张图纸", "一个布局，一排视口" };
            CmbMode.SelectedIndex = cfg.LayoutMode == VpConfig.ModeRow ? 1 : 0;

            // ---- 状态集下拉：各组名 + 末尾三个动作项 ----
            StateGroup activeGroup = cfg.ActiveGroup;      // 先取一次，保证当前组真的存在
            List<string> gnames = cfg.GroupNames();

            var groupItems = new List<string>();
            for (int i = 0; i < gnames.Count; i++) groupItems.Add(GroupLabel(gnames[i]));
            groupItems.Add(ActNewGroup);
            groupItems.Add(ActRenameGroup);
            groupItems.Add(ActDeleteGroup);

            CmbGroup.ItemsSource = groupItems;
            CmbGroup.SelectedItem = activeGroup.DisplayName;

            _loading = false;

            // ---- 图框详情（一级，和下拉同一屏）----
            FrameLine.Text = FrameSummary(cfg);
            RowFrameTools.IsEnabled = cfg.Frame != null && cfg.Frame.Valid;

            // ---- 状态列表 ----
            StateHint.Text = cfg.States.Count == 0
                ? "还没有状态"
                : "共 " + cfg.States.Count + " 个 · 每个一张图纸";

            var rows = new List<VpRow>();
            int unmatched = 0;

            for (int i = 0; i < cfg.States.Count; i++)
            {
                VpState st = cfg.States[i];
                ProfileMatch.Hit hit = ProfileMatch.Resolve(st.Name, st.Profile);

                string bind;
                Brush color;

                switch (hit.Kind)
                {
                    case ProfileMatch.Kind.Bound:
                        bind = "→ " + hit.Profile;
                        color = Green;
                        break;

                    case ProfileMatch.Kind.SameName:
                        bind = "= " + hit.Profile;
                        color = Green;
                        break;

                    case ProfileMatch.Kind.Fuzzy:
                        bind = "≈ " + hit.Suggest;
                        color = Yellow;
                        unmatched++;
                        break;

                    default:
                        bind = "— 无匹配";
                        color = Gray;
                        unmatched++;
                        break;
                }

                rows.Add(new VpRow
                {
                    Index = i + 1,
                    Name = st.Name,
                    BindText = bind,
                    BindColor = color,
                    CountText = "冻结 " + st.FrozenLayers.Count + " 层"
                });
            }

            if (unmatched > 0)
                StateHint.Text = "共 " + cfg.States.Count + " 个 · " + unmatched + " 个没对上配置";

            int keep = LstStates.SelectedIndex;
            LstStates.ItemsSource = rows;
            if (rows.Count > 0)
                LstStates.SelectedIndex = Math.Min(Math.Max(keep, 0), rows.Count - 1);

            // 空状态只替换列表区，标题行与行内工具仍在原位且可操作
            EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LstStates.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            TxtScale.Text = cfg.Scale > 0 ? Math.Round(1.0 / cfg.Scale).ToString("0") : "自动";

            // 图框异常只在生成页露一行。以前这些信息只写在 ⚙ 里，
            // 用户在生成页看不到「文件已不在」，会在不知情的情况下退回矩形框。
            FrameTemplate cur = cfg.Frame;

            string warn = "";
            if (cur == null)
                warn = "还没有图框　—　点「拾取图框」从图纸里选一个";
            else if (!cur.Valid)
                warn = "这个图框还没有尺寸　—　重新拾取一次，或点「重框内框」";
            else if (!string.IsNullOrEmpty(cur.LibFile) && !System.IO.File.Exists(cur.LibFile))
                warn = "⚠ 图框文件已经不在了，生成时会退回矩形框";

            FrameWarn.Text = warn;
            FrameWarn.Visibility = warn.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            // 缺前置条件时把按钮灰掉 —— 比让用户点了再看命令行报错友好
            bool hasFrame = cur != null && cur.Valid;
            BtnBatch.IsEnabled = hasFrame && cfg.States.Count > 0;
            BtnApply.IsEnabled = cfg.States.Count > 0;

            // 空列表时行内那几个按钮全是无的放矢，整行灰掉，别让人点了再弹「先选一个」
            RowTools.IsEnabled = cfg.States.Count > 0;

            FillTagProfiles();
            FillTagList();
            UpdateTagText();
        }

        /// <summary>
        /// 填「视口标记」的配置下拉 —— 点选代替命令行输序号。
        ///
        /// 尽量保留用户已经选中的那个：列表重填后先按名字找回它，
        /// 找不回就退到主插件当前配置，再退到第一个。
        /// </summary>
        void FillTagProfiles()
        {
            try
            {
                string keep = CmbTagProfile.SelectedItem as string;

                List<string> names = LayerProfile.ProfileNames();

                // 列表没变就别重建，免得每 200ms 刷新时下拉一直闪
                bool same = names.Count == CmbTagProfile.Items.Count;
                if (same)
                {
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (!string.Equals(names[i], CmbTagProfile.Items[i] as string, StringComparison.Ordinal))
                        {
                            same = false;
                            break;
                        }
                    }
                }

                if (!same)
                {
                    CmbTagProfile.Items.Clear();
                    foreach (string n in names) CmbTagProfile.Items.Add(n);
                }

                CmbTagProfile.IsEnabled = names.Count > 0;

                if (names.Count == 0) return;

                int idx = -1;

                if (!string.IsNullOrEmpty(keep))
                    idx = names.FindIndex(x => string.Equals(x, keep, StringComparison.OrdinalIgnoreCase));

                if (idx < 0)
                {
                    string cur = LayerProfile.CurrentProfile;
                    if (!string.IsNullOrEmpty(cur))
                        idx = names.FindIndex(x => string.Equals(x, cur, StringComparison.OrdinalIgnoreCase));
                }

                CmbTagProfile.SelectedIndex = idx < 0 ? 0 : idx;
            }
            catch { }
        }

        // ------------------------------------------------------------------ 视口标记

        /// <summary>
        /// 列出本图已经标记过的视口。以前这块是「看全部」按钮 + 命令行刷屏，
        /// 现在直接摆在标记页上 —— 标记任务真正需要看的就是这张表。
        /// </summary>
        void FillTagList()
        {
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                var sig = new StringBuilder();

                AcDb.ObjectId cur = AcDb.ObjectId.Null;
                Dictionary<string, string> map = null;

                if (doc != null)
                {
                    map = VpTagStore.Read(doc.Database);
                    cur = VpProfileWatcher.CurrentViewportId;

                    foreach (KeyValuePair<string, string> kv in map)
                        sig.Append(kv.Key).Append('=').Append(kv.Value).Append('|');
                }

                sig.Append('#').Append(cur.IsNull ? "" : cur.Handle.ToString());

                // 判重放在解析布局名之前：签名只由「标签映射 + 当前视口句柄」决定，
                // 布局名只影响显示文本。这个方法跟着 CVPORT 变化走，大多数拍子其实没变，
                // 早退就能省掉后面那趟布局读取 + 视口解析事务。
                string sigText = sig.ToString();
                if (sigText == _tagSig) return;
                _tagSig = sigText;

                var rows = new List<TagRow>();

                if (map != null && map.Count > 0)
                {
                    // 「句柄 → 布局名」一次事务解完。
                    // 原来每个标签要 TryGetViewport（轻）+ LayoutNameOf（两次事务）。
                    Dictionary<AcDb.ObjectId, string> layoutNames =
                        VpService.LayoutNamesByBlock(doc.Database);
                    var nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using (AcDb.Transaction tr = doc.Database.TransactionManager.StartTransaction())
                        {
                            foreach (KeyValuePair<string, string> kv in map)
                            {
                                try
                                {
                                    long v = Convert.ToInt64(kv.Key, 16);
                                    AcDb.ObjectId vpId;
                                    if (!doc.Database.TryGetObjectId(new AcDb.Handle(v), out vpId)) continue;
                                    if (vpId.IsNull || !vpId.IsValid) continue;
                                    if (!VpService.IsViewport(vpId)) continue;

                                    AcDb.DBObject o = tr.GetObject(vpId, AcDb.OpenMode.ForRead);
                                    string lay = null;
                                    if (o != null) layoutNames.TryGetValue(o.OwnerId, out lay);
                                    nameOf[kv.Key] = lay ?? "";
                                }
                                catch { }
                            }
                            tr.Commit();
                        }
                    }
                    catch { }

                    // 当前视口用句柄比：ObjectId 比较要先拿到 vpId，
                    // 而句柄在标签里本来就是键，直接比串就行
                    string curHandle = cur.IsNull ? "" : cur.Handle.ToString();

                    foreach (KeyValuePair<string, string> kv in map)
                    {
                        bool alive = nameOf.ContainsKey(kv.Key);
                        string name = alive ? nameOf[kv.Key] : null;
                        if (string.IsNullOrEmpty(name)) name = "视口 " + kv.Key;

                        rows.Add(new TagRow
                        {
                            Name = alive ? name : name + "（已不存在）",
                            Profile = kv.Value,
                            CurrentText = alive && string.Equals(curHandle, kv.Key, StringComparison.OrdinalIgnoreCase)
                                        ? "←当前" : ""
                        });
                    }
                }

                rows.Sort(delegate(TagRow a, TagRow b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });

                LstTags.ItemsSource = rows;

                bool empty = rows.Count == 0;
                LstTags.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                TagEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                TagCountHint.Text = empty ? "还没有" : "共 " + rows.Count + " 个";
            }
            catch { }
        }

        /// <summary>刷新"当前视口 / 已标记哪个配置"这一行。</summary>
        void UpdateTagText()
        {
            try
            {
                try
                {
                    BtnLayerFallback.IsChecked = VpStore.Current.LayerFallback;
                }
                catch { }

                TagText.Foreground = Gray;

                if (!LayerProfile.Available)
                {
                    TagDot.Fill = Yellow;
                    TagText.Foreground = Yellow;
                    TagText.Text = "没检测到「Y_归层工具」主插件 —— 标记能存进图纸，但进视口不会切配置";
                    return;
                }

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                int tagged = doc == null ? 0 : VpTagStore.Read(doc.Database).Count;

                if (VpProfileWatcher.CurrentViewportId.IsNull)
                {
                    TagDot.Fill = Gray;
                    TagText.Text = "不在视口里 · 本图已标记 " + tagged + " 个";
                    return;
                }

                string prof = VpProfileWatcher.CurrentTagProfile;
                if (prof == null)
                {
                    TagDot.Fill = Gray;
                    TagText.Text = "当前视口：未标记 · 本图已标记 " + tagged + " 个";
                }
                else
                {
                    TagDot.Fill = Green;
                    TagText.Text = "当前视口：已标记「" + prof + "」";
                }
            }
            catch { }
        }

        /// <summary>
        /// 标记视口：把下拉里选好的配置带给命令，命令就不再列清单让人输序号，
        /// 直接进入「点选/框选视口」这一步。选好后面板会收起来，不挡着点视口。
        /// </summary>
        void OnTag(object sender, RoutedEventArgs e)
        {
            VpCommands.PendingTagProfile = CmbTagProfile.SelectedItem as string;
            RunCommand("_.VLTAG ");
        }

        void OnUntag(object sender, RoutedEventArgs e) { RunCommand("_.VLUNTAG "); }

        void OnWatcherChanged()
        {
            // 定时器在 UI 线程上，但保险起见还是排到 Dispatcher 里更新
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    FillTagList();
                    UpdateTagText();
                }));
            }
            catch { }
        }

        int SelectedIndex()
        {
            return LstStates.SelectedIndex;
        }

        bool RequireSelection(VpConfig cfg, out int idx)
        {
            idx = SelectedIndex();
            if (idx < 0 || idx >= cfg.States.Count)
            {
                MessageBox.Show(this, "先在列表里选一个状态。", "视口图层", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ 图框管理

        void OnFrameSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            int idx = CmbFrames.SelectedIndex;
            if (idx < 0) return;

            VpConfig cfg = VpStore.Current;
            if (idx == cfg.FrameIndex) return;

            cfg.FrameIndex = idx;
            VpStore.Save(cfg);
            Reload();
        }

        void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            int idx = CmbMode.SelectedIndex;
            if (idx < 0) return;

            VpConfig cfg = VpStore.Current;
            int mode = idx == 1 ? VpConfig.ModeRow : VpConfig.ModePerSheet;
            if (mode == cfg.LayoutMode) return;

            cfg.LayoutMode = mode;
            VpStore.Save(cfg);
            Reload();
        }

        void OnReframeInner(object sender, RoutedEventArgs e) { RunCommand("_.VLINNER "); }

        // ------------------------------------------------------------ 状态集（一个公司一套）

        /// <summary>下拉末尾的三个动作项。选它们不改配置，只开对话框。</summary>
        const string ActNewGroup = "＋ 新建状态集…";
        const string ActRenameGroup = "✎ 状态集改名…";
        const string ActDeleteGroup = "✕ 删除当前状态集…";

        /// <summary>空组在界面上叫「默认」，别显示成一行空白。</summary>
        static string GroupLabel(string name)
        {
            return string.IsNullOrEmpty(name) ? "默认" : name;
        }

        void OnGroupChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            string pick = CmbGroup.SelectedItem as string;
            if (string.IsNullOrEmpty(pick)) return;

            if (pick == ActNewGroup) { NewGroup(); return; }
            if (pick == ActRenameGroup) { RenameGroup(); return; }
            if (pick == ActDeleteGroup) { DeleteGroup(); return; }

            VpConfig cfg = VpStore.Current;
            string key = pick == "默认" ? "" : pick;

            if (string.Equals(cfg.ActiveGroupName ?? "", key, StringComparison.OrdinalIgnoreCase)) return;

            cfg.ActiveGroupName = key;
            VpStore.Save(cfg);
            Reload();
        }

        /// <summary>
        /// 新建状态集。新建完直接切过去 —— 建了不切，用户还得再点一次下拉，多余。
        /// 取消对话框要把下拉弹回原值（Reload），否则它会停在动作项上。
        /// </summary>
        void NewGroup()
        {
            string name = PromptString("新建状态集（一个公司 / 项目一套）", "");
            if (name == null) { Reload(); return; }

            name = name.Trim();
            if (name.Length == 0 || name == "默认") { Reload(); return; }

            VpConfig cfg = VpStore.Current;
            if (cfg.FindGroup(name) != null)
            {
                MessageBox.Show(this, "已经有同名的状态集了。", "视口图层",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Reload();
                return;
            }

            cfg.EnsureGroup(name);
            cfg.ActiveGroupName = name;
            VpStore.Save(cfg);
            Reload();
        }

        void RenameGroup()
        {
            VpConfig cfg = VpStore.Current;
            string old = cfg.ActiveGroupName ?? "";

            string name = PromptString("状态集改名", GroupLabel(old));
            if (name == null) { Reload(); return; }

            name = name.Trim();
            if (name == "默认") name = "";
            if (name.Length == 0 && old.Length == 0) { Reload(); return; }

            if (!cfg.RenameGroup(old, name))
            {
                MessageBox.Show(this, "已经有同名的状态集了。", "视口图层",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Reload();
                return;
            }

            VpStore.Save(cfg);
            Reload();
        }

        void DeleteGroup()
        {
            VpConfig cfg = VpStore.Current;

            if (cfg.Groups.Count <= 1)
            {
                MessageBox.Show(this, "至少要留一个状态集。", "视口图层",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Reload();
                return;
            }

            string name = cfg.ActiveGroupName ?? "";
            int count = cfg.States.Count;

            var r = MessageBox.Show(this,
                "删除状态集「" + GroupLabel(name) + "」？\n里面的 " + count
                + " 个状态会一起删掉，图纸里的视口不受影响。",
                "视口图层", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) { Reload(); return; }

            cfg.RemoveGroup(name);
            VpStore.Save(cfg);
            Reload();
        }

        // ------------------------------------------------------------ 对齐配置（面板内）

        /// <summary>下拉里表示「不绑定」的那一项。</summary>
        const string NoBind = "（不绑定）";

        /// <summary>浮层当前的行。打开时按 VpStore 现场生成，保存后丢弃。</summary>
        List<AlignRow> _alignRows;

        /// <summary>选择层正在为哪一行挑配置；null 表示没打开。</summary>
        AlignRow _pickRow;

        /// <summary>
        /// 对齐表的一行。圆点、提示和两个方向按钮的可用状态都是算出来的，
        /// 改了下拉要立刻反映，所以走 INotifyPropertyChanged。
        /// </summary>
        sealed class AlignRow : System.ComponentModel.INotifyPropertyChanged
        {
            /// <summary>状态原名。保存时才真正改，中途不动它，这样「取消」就是再点一下。</summary>
            public string Name = "";

            /// <summary>
            /// 打开浮层时的那个状态对象。保存时直接改它，不再按名字回查 ——
            /// 同一批里前面的行改了名，后面的行按名字就找不着了。
            /// </summary>
            public VpState State;

            /// <summary>模糊匹配到的配置名，只用来决定圆点是黄还是灰。</summary>
            public string Suggest = "";

            public List<string> Choices;

            string _sel = NoBind;

            public string Selected
            {
                get { return _sel; }
                set
                {
                    string v = string.IsNullOrEmpty(value) ? NoBind : value;
                    if (_sel == v) return;
                    _sel = v;
                    if (_dir != 0) _dir = 0;   // 换了目标，之前选的改名方向就不作数了
                    RaiseAll();
                }
            }

            /// <summary>
            /// 名字往哪边走：
            /// 0 = 不改 / 1 = 状态名跟配置名 / 2 = 配置名跟状态名。
            /// 两个方向互斥 —— 一次保存只让一边改名，免得绕成一团。
            /// </summary>
            int _dir;

            public int Dir
            {
                get { return _dir; }
                set
                {
                    int v = value;
                    if (v == 2 && !CanRenameProfile) v = 0;
                    if (v == 1 && !CanRenameState) v = 0;
                    if (_dir == v) return;
                    _dir = v;
                    RaiseAll();
                }
            }

            public bool Bound { get { return _sel != NoBind; } }

            public Brush DotFill
            {
                get
                {
                    if (Bound) return Green;                                    // 会生效
                    return string.IsNullOrEmpty(Suggest) ? Gray : Yellow;       // 有建议没选 / 完全没对应
                }
            }

            /// <summary>没绑定或名字已经一样时没什么可改的。</summary>
            public bool CanRenameState { get { return Bound && _sel != Name; } }

            public bool CanRenameProfile { get { return Bound && _sel != Name; } }

            public Brush StateBtnBg
            {
                get { return _dir == 1 ? SelectionBg : Transparent; }
            }

            public Brush ProfileBtnBg
            {
                get { return _dir == 2 ? SelectionBg : Transparent; }
            }

            public string NameText { get { return _dir == 1 && Bound ? _sel : Name; } }

            public Brush NameBrush
            {
                get { return _dir != 0 ? AccentLine : TextBrush; }
            }

            public string RenameHint
            {
                get
                {
                    if (_dir == 1) return "（原 " + Name + "）";
                    if (_dir == 2) return "（配置改名）";
                    return "";
                }
            }

            void RaiseAll()
            {
                Raise("Selected");
                Raise("Dir");
                Raise("DotFill");
                Raise("CanRenameState");
                Raise("CanRenameProfile");
                Raise("StateBtnBg");
                Raise("ProfileBtnBg");
                Raise("NameText");
                Raise("NameBrush");
                Raise("RenameHint");
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            void Raise(string n)
            {
                System.ComponentModel.PropertyChangedEventHandler h = PropertyChanged;
                if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs(n));
            }
        }

        /// <summary>
        /// 打开对齐浮层。原来这里是 RunCommand("_.VLALIGN")，等于把用户丢回命令行
        /// 一条条敲 B / R —— 2026-11-16 用户要求改成面板内直接改。
        /// </summary>
        void OnAlign(object sender, RoutedEventArgs e)
        {
            ClosePicker();

            VpConfig cfg = VpStore.Current;
            List<string> names = LayerProfile.ProfileNames();

            var choices = new List<string>();
            choices.Add(NoBind);
            choices.AddRange(names);

            _alignRows = new List<AlignRow>();

            for (int i = 0; i < cfg.States.Count; i++)
            {
                VpState st = cfg.States[i];
                if (st == null) continue;

                ProfileMatch.Hit hit = ProfileMatch.Resolve(st.Name, st.Profile);

                var row = new AlignRow
                {
                    Name = st.Name,
                    State = st,
                    Choices = choices,
                    Suggest = hit.Kind == ProfileMatch.Kind.Fuzzy ? hit.Suggest : ""
                };

                // 已经对上的按现状预选；没对上的留「不绑定」，等用户自己挑，不替他猜
                if (hit.Kind == ProfileMatch.Kind.Bound || hit.Kind == ProfileMatch.Kind.SameName)
                    row.Selected = hit.Profile;

                _alignRows.Add(row);
            }

            LstAlign.ItemsSource = _alignRows;

            AlignHint.Text = names.Count == 0
                ? "主插件里还没有任何归层配置，先去主面板建一套，再回来对齐。"
                : _alignRows.Count == 0
                    ? "还没有视口状态。先关掉这里，在图纸里调好冻结层，再点「+ 刷取状态」。"
                    : "给每个视口状态指一套归层配置。对上之后，进这个视口才会自动切过去。";

            RefreshAlignStat();
            AlignOverlay.Visibility = Visibility.Visible;
        }

        void RefreshAlignStat()
        {
            if (AlignStat == null || _alignRows == null) return;

            int ok = 0;
            for (int i = 0; i < _alignRows.Count; i++)
                if (_alignRows[i].Bound) ok++;

            AlignStat.Text = _alignRows.Count == 0
                ? "没有视口状态"
                : ok + " / " + _alignRows.Count + " 已对上";
        }

        /// <summary>名字和配置完全一样的状态，一次全绑上（等价 VLALIGN 的第 1 步）。</summary>
        void OnAlignSameName(object sender, RoutedEventArgs e)
        {
            if (_alignRows == null) return;

            for (int i = 0; i < _alignRows.Count; i++)
            {
                AlignRow r = _alignRows[i];
                if (r.Choices != null && r.Choices.Contains(r.Name)) r.Selected = r.Name;
            }

            RefreshAlignStat();
        }

        /// <summary>
        /// 「按建议预选」：把名字接近（黄点）的那些按建议填进下拉。
        /// 只填空着的行，已经绑好的不覆盖 —— 用户手挑的优先。
        /// </summary>
        void OnAlignSuggest(object sender, RoutedEventArgs e)
        {
            if (_alignRows == null) return;

            for (int i = 0; i < _alignRows.Count; i++)
            {
                AlignRow r = _alignRows[i];
                if (r.Bound) continue;
                if (string.IsNullOrEmpty(r.Suggest)) continue;
                if (r.Choices != null && r.Choices.Contains(r.Suggest)) r.Selected = r.Suggest;
            }

            RefreshAlignStat();
        }

        /// <summary>「跟配置」：只做标记，保存时才把状态名改成所选配置名 —— 点错了再点一下就撤销。</summary>
        void OnAlignRenameState(object sender, RoutedEventArgs e)
        {
            AlignRow r = RowOf(sender);
            if (r == null || !r.CanRenameState) return;
            r.Dir = r.Dir == 1 ? 0 : 1;
        }

        /// <summary>
        /// 「跟状态」：反过来 —— 把所选归层配置改成这个状态名。
        /// 用户 2026-11-16 要求两边都能换，不是只有状态跟着配置走。
        /// </summary>
        void OnAlignRenameProfile(object sender, RoutedEventArgs e)
        {
            AlignRow r = RowOf(sender);
            if (r == null || !r.CanRenameProfile) return;
            r.Dir = r.Dir == 2 ? 0 : 2;
        }

        static AlignRow RowOf(object sender)
        {
            FrameworkElement fe = sender as FrameworkElement;
            return fe == null ? null : fe.Tag as AlignRow;
        }

        // ------------------------------------------------------------ 选择层（替代 Popup 下拉）

        /// <summary>
        /// 点「绑定到」弹选择层。位置按被点的按钮现场算，算完贴到它左下；
        /// 下方放不下就翻到上面，免得最后几行点开看不见。
        /// </summary>
        void OnOpenPicker(object sender, RoutedEventArgs e)
        {
            FrameworkElement btn = sender as FrameworkElement;
            AlignRow r = RowOf(sender);
            if (btn == null || r == null || r.Choices == null) return;

            _pickRow = r;
            LstPicker.ItemsSource = r.Choices;

            AlignPicker.Visibility = Visibility.Visible;
            AlignPicker.UpdateLayout();

            double areaW = AlignOverlay.ActualWidth;
            double areaH = AlignOverlay.ActualHeight;
            double boxW = PickerBox.ActualWidth;
            double boxH = PickerBox.ActualHeight;

            Point p = btn.TransformToAncestor(AlignOverlay)
                         .Transform(new Point(0, btn.ActualHeight));

            double x = p.X;
            if (x + boxW > areaW - 4) x = Math.Max(4, areaW - boxW - 4);

            double y = p.Y + 2;
            if (y + boxH > areaH - 4) y = Math.Max(4, p.Y - btn.ActualHeight - boxH - 2);

            PickerBox.Margin = new Thickness(x, y, 0, 0);
        }

        void OnPickerPick(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            string v = btn == null ? null : btn.Content as string;

            if (_pickRow != null && v != null) _pickRow.Selected = v;
            ClosePicker();
        }

        /// <summary>点空白处关掉。点在列表里不算 —— 否则按钮还没 Click 就被收走了。</summary>
        void OnPickerOutside(object sender, MouseButtonEventArgs e)
        {
            if (PickerBox != null && PickerBox.IsMouseOver) return;
            ClosePicker();
        }

        /// <summary>列表一滚动就关，不然选择层浮在原地、跟行对不上。</summary>
        void OnAlignScroll(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0) ClosePicker();
        }

        void ClosePicker()
        {
            _pickRow = null;
            if (AlignPicker != null) AlignPicker.Visibility = Visibility.Collapsed;
        }

        void OnAlignCancel(object sender, RoutedEventArgs e)
        {
            ClosePicker();
            _alignRows = null;
            AlignOverlay.Visibility = Visibility.Collapsed;
        }

        void OnAlignSave(object sender, RoutedEventArgs e)
        {
            if (_alignRows == null)
            {
                AlignOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            ClosePicker();
            VpConfig cfg = VpStore.Current;

            // 1) 先把「跟状态」这边要改的配置名汇总，冲突的先剔掉
            int skipped;
            List<string[]> renames = CollectProfileRenames(out skipped);

            // 2) 真改主插件的配置名（连同所有状态里的引用）
            //    返回真正改成的那些：被跳过的行不能拿新名字去绑，否则会绑到空气上
            Dictionary<string, string> done = ApplyProfileRenames(cfg, renames);

            // 3) 逐行落状态
            for (int i = 0; i < _alignRows.Count; i++)
            {
                AlignRow r = _alignRows[i];
                VpState st = r.State;
                if (st == null) continue;

                string pick = r.Bound ? r.Selected : "";

                // 「跟状态」的行：配置已经改名成状态名了，引用也跟过去。
                // 只认改名真成了的 —— 被跳过的那行保持原绑定。
                if (r.Dir == 2 && pick.Length > 0)
                {
                    string renamed;
                    if (done.TryGetValue(pick, out renamed)) pick = renamed;
                }

                if (r.Dir == 1 && pick.Length > 0)
                {
                    // 撞名就加后缀，别把别的状态顶掉（和 VLALIGN 一个规矩）
                    st.Name = cfg.Find(pick) != null ? cfg.UniqueName(pick) : pick;
                    st.Profile = pick;
                }
                else
                {
                    st.Profile = pick;
                }
            }

            VpStore.Save(cfg);

            _alignRows = null;
            AlignOverlay.Visibility = Visibility.Collapsed;
            Reload();

            if (skipped > 0)
            {
                MessageBox.Show(this,
                    "有 " + skipped + " 行「跟状态」没做成：目标名字已经被另一套归层配置占着，\n"
                    + "配置名不能重复。把那套配置先改个名，或者给状态换个名字，再来一次。",
                    "视口图层", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 汇总「跟状态」的改名请求。新名已被别的配置占用、或同一次保存里两行抢同一个新名，
        /// 都算冲突，跳过并计数 —— 宁可少改，也不能把两套配置并成一套。
        /// </summary>
        List<string[]> CollectProfileRenames(out int skipped)
        {
            var list = new List<string[]>();
            skipped = 0;

            List<string> existing = LayerProfile.ProfileNames();

            for (int i = 0; i < _alignRows.Count; i++)
            {
                AlignRow r = _alignRows[i];
                if (r.Dir != 2 || !r.Bound) continue;

                string oldName = r.Selected;
                string newName = r.Name;

                if (newName.Length == 0
                    || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                bool taken = false;

                for (int k = 0; k < existing.Count; k++)
                {
                    if (string.Equals(existing[k], newName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(existing[k], oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        taken = true;
                        break;
                    }
                }

                if (!taken)
                {
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (string.Equals(list[k][1], newName, StringComparison.OrdinalIgnoreCase))
                        {
                            taken = true;
                            break;
                        }
                    }
                }

                if (taken) { skipped++; continue; }

                list.Add(new[] { oldName, newName });
            }

            return list;
        }

        /// <summary>
        /// 改主插件里的配置名，并把这套配置在**所有状态集**里的引用一起改掉。
        /// 只改配置不改引用的话，状态里存的那个旧名字就悬空了，进视口切不过去。
        /// 本图纸的视口标签也顺手改掉；别的图纸翻不到，只能重新标记。
        ///
        /// 返回「旧名 → 新名」，只含真改成的那些。调用方拿它去同步状态绑定 ——
        /// 被跳过的行如果也按新名字绑，就绑到一个不存在的配置上了。
        /// </summary>
        Dictionary<string, string> ApplyProfileRenames(VpConfig cfg, List<string[]> renames)
        {
            var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < renames.Count; i++)
            {
                string oldName = renames[i][0];
                string newName = renames[i][1];

                if (!LayerProfile.RenameProfile(oldName, newName)) continue;

                done[oldName] = newName;

                for (int g = 0; g < cfg.Groups.Count; g++)
                {
                    StateGroup grp = cfg.Groups[g];
                    if (grp == null || grp.States == null) continue;

                    for (int s = 0; s < grp.States.Count; s++)
                    {
                        VpState st = grp.States[s];
                        if (st != null && string.Equals(st.Profile, oldName, StringComparison.OrdinalIgnoreCase))
                            st.Profile = newName;
                    }
                }

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    try { VpTagStore.RenameProfile(doc.Database, oldName, newName); }
                    catch { }
                }
            }

            return done;
        }

        /// <summary>
        /// 图层兜底开关。留在面板里而不是设置窗口 —— 它是「视口 → 配置」这条线上的行为，
        /// 跟标记是同一件事，放在标记页底部才说得通（2026-11-14 用户定）。
        /// </summary>
        void OnToggleLayerFallback(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            cfg.LayerFallback = BtnLayerFallback.IsChecked == true;
            VpStore.Save(cfg);
            UpdateTagText();
        }

        /// <summary>
        /// 改归层配置的名字。这是「两边都能改」的另一半 ——
        /// 状态名在图面标题上不好动，那就在配置这边靠过来。
        ///
        /// 会同步：主插件配置名、状态里的绑定、本图纸的视口标签。
        /// 别的图纸里的标签同步不了（插件不会去翻别人的图纸），会明确告诉用户。
        /// </summary>
        void OnRenameProfile(object sender, RoutedEventArgs e)
        {
            string old = CmbTagProfile.SelectedItem as string;

            if (string.IsNullOrEmpty(old))
            {
                MessageBox.Show(this, "先在「配置」下拉里选一个要改名的配置。", "视口图层",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = PromptString("改配置名（主插件里的归层配置）", old);
            if (name == null) return;

            name = name.Trim();
            if (name.Length == 0 || name == old) return;

            AutoLayer.Core.Config pc = AutoLayer.Core.ConfigStore.Current;
            if (pc == null) return;

            if (!pc.Rename(old, name))
            {
                MessageBox.Show(this, "改不了：名字非法，或者已经有同名配置。", "视口图层",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AutoLayer.Core.ConfigStore.Save(pc);

            // 状态里的绑定跟着改，否则它们会指向一个已经不存在的名字
            VpConfig cfg = VpStore.Current;
            int stCount = 0;

            for (int i = 0; i < cfg.States.Count; i++)
            {
                VpState st = cfg.States[i];
                if (st == null) continue;
                if (!string.Equals(st.Profile, old, StringComparison.OrdinalIgnoreCase)) continue;

                st.Profile = name;
                stCount++;
            }

            VpStore.Save(cfg);

            // 本图纸的标签也改掉
            int tagCount = 0;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null) tagCount = VpTagStore.RenameProfile(doc.Database, old, name);

            try { AutoLayer.Core.LayerEvents.Raise(); } catch { }

            Reload();

            MessageBox.Show(this,
                "已改名：「" + old + "」→「" + name + "」\n\n"
                + "状态绑定同步 " + stCount + " 个\n"
                + "本图视口标签同步 " + tagCount + " 个\n\n"
                + "⚠ 别的图纸里已经打好的标签不会跟着变，那些图纸要重新标记一次。",
                "视口图层", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OnFrameRename(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            if (cfg.Frames.Count == 0) return;

            FrameTemplate f = cfg.Frame;
            string name = PromptString("图框名称（随便起，跟块名无关）", f.DisplayName);
            if (name == null) return;

            name = name.Trim();
            if (name.Length == 0 || name == f.DisplayName) return;

            f.Label = name;
            VpStore.Save(cfg);
            Reload();
        }

        void OnFrameDelete(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            if (cfg.Frames.Count == 0) return;

            FrameTemplate f = cfg.Frame;
            var r = MessageBox.Show(this,
                "删除图框「" + f.DisplayName + "」？\n只删配置，图纸里的图框不受影响。",
                "视口图层", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            cfg.Frames.RemoveAt(cfg.FrameIndex);
            if (cfg.FrameIndex >= cfg.Frames.Count)
                cfg.FrameIndex = Math.Max(0, cfg.Frames.Count - 1);

            VpStore.Save(cfg);
            Reload();
        }

        // ------------------------------------------------------------------ 状态列表操作

        void OnMoveUp(object sender, RoutedEventArgs e) { Move(-1); }
        void OnMoveDown(object sender, RoutedEventArgs e) { Move(1); }

        void Move(int delta)
        {
            VpConfig cfg = VpStore.Current;
            int idx;
            if (!RequireSelection(cfg, out idx)) return;

            if (!cfg.Move(idx, delta)) return;

            VpStore.Save(cfg);
            Reload();
            LstStates.SelectedIndex = idx + delta;
        }

        void OnDelete(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            int idx;
            if (!RequireSelection(cfg, out idx)) return;

            VpState st = cfg.States[idx];
            var r = MessageBox.Show(this,
                "删除状态「" + st.Name + "」？\n只删配置，图纸里的视口不受影响。",
                "视口图层", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            cfg.States.RemoveAt(idx);
            VpStore.Save(cfg);
            Reload();
        }

        void OnRename(object sender, RoutedEventArgs e)
        {
            RenameSelected();
        }

        void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            RenameSelected();
        }

        void RenameSelected()
        {
            VpConfig cfg = VpStore.Current;
            int idx;
            if (!RequireSelection(cfg, out idx)) return;

            VpState st = cfg.States[idx];
            string name = PromptString("重命名状态", st.Name);
            if (name == null) return;

            name = name.Trim();
            if (name.Length == 0 || name == st.Name) return;

            if (cfg.Find(name) != null)
            {
                MessageBox.Show(this, "已经有同名状态了。", "视口图层", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            st.Name = name;
            VpStore.Save(cfg);
            Reload();
            LstStates.SelectedIndex = idx;
        }

        void OnShowLayers(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            int idx;
            if (!RequireSelection(cfg, out idx)) return;

            VpState st = cfg.States[idx];
            string body = st.FrozenLayers.Count == 0
                ? "（这个视口没有冻结任何图层）"
                : string.Join("\n", st.FrozenLayers.ToArray());

            MessageBox.Show(this, "「" + st.Name + "」冻结的图层：\n\n" + body,
                "视口图层", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ------------------------------------------------------------------ 比例

        void OnScaleChanged(object sender, RoutedEventArgs e) { ApplyScale(); }

        void OnScaleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ApplyScale();
        }

        void ApplyScale()
        {
            VpConfig cfg = VpStore.Current;
            string text = (TxtScale.Text ?? "").Trim();

            double s;
            if (!TryParseScale(text, out s))
            {
                TxtScale.Text = cfg.Scale > 0 ? Math.Round(1.0 / cfg.Scale).ToString("0") : "自动";
                return;
            }
            if (Math.Abs(s - cfg.Scale) < 1e-9) return;

            cfg.Scale = s;
            VpStore.Save(cfg);
        }

        /// <summary>面板上的比例文本 → 系数。空或「自动」= 0（由内框自动算）。</summary>
        static bool TryParseScale(string s, out double scale)
        {
            scale = 0;
            if (string.IsNullOrWhiteSpace(s)) return true;

            s = s.Trim().Replace("：", ":");
            if (s == "自动" || string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return true;

            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                double a, b;
                if (!double.TryParse(s.Substring(0, colon), out a)) return false;
                if (!double.TryParse(s.Substring(colon + 1), out b)) return false;
                if (a <= 0 || b <= 0) return false;
                scale = a / b;
                return true;
            }

            double d;
            if (!double.TryParse(s, out d) || d <= 0) return false;
            scale = d >= 1 ? 1.0 / d : d;
            return true;
        }

        // ------------------------------------------------------------------ 转成 CAD 命令

        void OnSaveFrame(object sender, RoutedEventArgs e) { RunCommand("_.VLFRAME "); }
        void OnCapture(object sender, RoutedEventArgs e) { RunCommand("_.VLCAP "); }

        /// <summary>
        /// 刷到视口：把选中项的序号塞给命令，命令直接让你点视口，不再问「刷哪个」。
        /// </summary>
        void OnApply(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            int idx;
            if (!RequireSelection(cfg, out idx)) return;

            VpCommands.PendingStateIndex = idx;
            RunCommand("_.VLAPPLY ");
        }

        /// <summary>更新：用新点选的视口覆盖选中状态的冻结层，名字和序号都不变。</summary>
        void OnUpdate(object sender, RoutedEventArgs e)
        {
            VpConfig cfg = VpStore.Current;
            int idx;
            if (!RequireSelection(cfg, out idx)) return;

            VpCommands.PendingStateIndex = idx;
            RunCommand("_.VLUPDATE ");
        }

        /// <summary>批量生成：比例用面板上填的，命令里不再问。</summary>
        void OnBatch(object sender, RoutedEventArgs e)
        {
            double s;
            if (!TryParseScale(TxtScale.Text, out s)) s = 0;

            VpCommands.PendingScale = s;
            RunCommand("_.VLBATCH ");
        }

        /// <summary>
        /// 把命令丢回 CAD 执行。执行期间把面板整个收起来，免得挡着点视口、框图元；
        /// 命令结束（成功/取消/失败都算）再弹回来 —— 这三个事件都要挂，少一个就会卡在隐藏状态。
        /// </summary>
        void RunCommand(string cmd)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            CommandEventHandler done = null;
            done = delegate(object s, CommandEventArgs e)
            {
                doc.CommandEnded -= done;
                doc.CommandCancelled -= done;
                doc.CommandFailed -= done;

                // 一定要等到 CAD 完全空闲再弹回来。
                // 用默认优先级会在命令还没收尾时就 Show()，窗口先画出半张白板，
                // 用户看到的就是一个飘在绘图区上的白框。
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Restore));
            };

            doc.CommandEnded += done;
            doc.CommandCancelled += done;
            doc.CommandFailed += done;

            try
            {
                Hide();
            }
            catch { }

            try
            {
                // 先发两个 Esc 把上一个没走完的命令取消掉。
                // 否则新命令的字符会被上一条命令的提示吃掉，命令行就会串成一团乱码。
                doc.SendStringToExecute("\x03\x03" + cmd, true, false, false);
            }
            catch
            {
                doc.CommandEnded -= done;
                doc.CommandCancelled -= done;
                doc.CommandFailed -= done;
                Restore();
            }
        }

        /// <summary>命令结束后把面板弹回来。窗口已经关掉时不要复活它。</summary>
        void Restore()
        {
            if (_win != this) return;

            // CAD 还在跑别的命令就别抢焦点，等它跑完这一轮再说（最多等 20 拍，防止等不到）
            try
            {
                string running = AcApp.GetSystemVariable("CMDNAMES") as string;
                if (!string.IsNullOrEmpty(running) && _restoreTries < 20)
                {
                    _restoreTries++;
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Restore));
                    return;
                }
            }
            catch { }

            _restoreTries = 0;

            try
            {
                if (!IsVisible)
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Show();
                }

                // 强制走一遍布局和渲染，避免弹回来时是半张没画完的白板
                InvalidateVisual();
                UpdateLayout();

                Activate();
                Reload();
            }
            catch { }
        }

        // ------------------------------------------------------------------ 输入框

        /// <summary>极简文本输入框（不引 Microsoft.VisualBasic，少一个运行时依赖）。</summary>
        static string PromptString(string title, string def)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.BgDeep),
                Foreground = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Text),
                FontFamily = AutoLayer.UI.UiTheme.Font,
                FontSize = 12,
                ShowInTaskbar = false
            };

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var box = new TextBox
            {
                Text = def ?? "",
                Background = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Sunken),
                Foreground = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Text),
                BorderBrush = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.LineControl),
                Padding = new Thickness(6, 4, 6, 4),
                CaretBrush = AutoLayer.UI.UiTheme.B(AutoLayer.UI.UiTheme.Text)
            };
            Grid.SetRow(box, 0);
            root.Children.Add(box);

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            string result = null;

            var ok = new Button { Content = "确定", Width = 76, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "取消", Width = 76, IsCancel = true };

            ok.Click += (s, e) => { result = box.Text; win.Close(); };
            cancel.Click += (s, e) => { win.Close(); };

            bar.Children.Add(ok);
            bar.Children.Add(cancel);
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            win.Content = root;
            win.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            win.ShowDialog();

            return result;
        }
    }
}
