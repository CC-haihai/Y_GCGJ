using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AutoLayer.Core;
using VpLayer;

namespace AutoLayer.UI
{
    /// <summary>
    /// 设置窗口：一行一个功能（主面板 / 图层切换 / 视口工具 / 圆盘），
    /// 每行给这个功能配「快捷键 + 鼠标键 + 命令名」，再加一页「帮助」把用法讲完。
    ///
    /// 为什么是"按功能排"而不是"按鼠标键排"：用户心里的问题是
    /// 「我想给圆盘配个键」，不是「我想给侧键配个功能」——
    /// 主语必须是功能。以前按三个鼠标键分三页，同一个功能散在三页里，
    /// 想给圆盘换个键得先把三页都翻一遍（2026-11-14 用户指出）。
    ///
    /// 所有说明文字都搬进了「帮助」页：设置页只留控件，不再在每格下面挂一句解释。
    ///
    /// 这里只管"收集用户的选择"，真正写回配置在 <see cref="Edit"/> 里一次做完 ——
    /// 中途点取消就一个字节都不动，跟面板里那些 chip 的行为保持一致。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        // 四个功能，索引顺序必须和 HotkeyHost.Act* 一致（那边按下标派发动作）
        const int ActPanel = HotkeyHost.ActPanel;
        const int ActQuickSwitch = HotkeyHost.ActQuickSwitch;
        const int ActViewport = HotkeyHost.ActViewport;
        const int ActPie = HotkeyHost.ActPie;
        const int ActCount = 4;

        /// <summary>功能 → 它在鼠标绑定里的动作编号（MouseBind）。下标 = 功能。</summary>
        static readonly int[] ActBind =
        {
            MouseBind.Panel,        // 主面板
            MouseBind.QuickSwitch,  // 图层切换
            MouseBind.Viewport,     // 视口工具
            MouseBind.Pie           // 圆盘
        };

        // 鼠标键下拉的档位：0 不用 / 1 右键长按 / 2 侧键后退 / 3 侧键前进
        const int SlotNone = 0;
        const int SlotRight = 1;
        const int SlotX1 = 2;
        const int SlotX2 = 3;

        static readonly string[] SlotNames = { "不用", "右键长按", "侧键后退", "侧键前进" };

        readonly int[] _mods = new int[ActCount];
        readonly int[] _key = new int[ActCount];

        /// <summary>功能 → 用哪颗鼠标键（Slot*）。反过来的映射由 <see cref="BindOfSlot"/> 算。</summary>
        readonly int[] _mouse = new int[ActCount];

        bool _capturing;
        int _captureAct = -1;

        /// <summary>程序在批量摆下拉框，别把这次 SelectionChanged 当成用户操作。</summary>
        bool _syncing;

        /// <summary>
        /// 本次会话里点过「导入配置」且导入成功。
        /// 导入是即时落盘的独立操作（跟本窗口的"保存/取消"分开算），
        /// Edit 的调用方看到它就必须扔掉手里的旧 Config 对象、整个按新配置重摆 ——
        /// 否则面板关窗时会拿旧对象把导入结果覆盖回去。
        /// </summary>
        bool _imported;

        SettingsWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Apply(this);

            // 不管怎么关（取消 / Esc / 标题栏 X），都要把钩子放回去，
            // 否则快捷键从此再也不响应，而且完全没有提示。
            Closing += (s, e) => { _capturing = false; _captureAct = -1; HotkeyHost.Suspended = false; };

            Load();
        }

        /// <summary>
        /// 打开设置窗口。返回 true 表示用户点了保存（配置已改好）。
        /// <paramref name="imported"/> = 期间导入过新配置（已落盘生效）：
        /// 此时返回值只表示"窗口里后续的微调存没存"，导入本身一定已经生效。
        /// </summary>
        public static bool Edit(Window owner, Config cfg, out bool imported)
        {
            imported = false;
            if (cfg == null) return false;

            SettingsWindow dlg;
            try
            {
                dlg = new SettingsWindow();
                try { dlg.Owner = owner; } catch { }
                dlg.LoadFrom(cfg.Settings);
                bool? dr = dlg.ShowDialog();

                if (dlg._imported)
                {
                    imported = true;

                    // 导入后窗口字段已按新配置重摆；用户又调了设置并点保存的，
                    // 把这些微调写进【新】配置再存一次。点取消 = 只保留导入结果。
                    if (dr == true)
                    {
                        Config live = ConfigStore.Current;
                        if (live != null)
                        {
                            ApplyTo(live, dlg);
                            ConfigStore.Save(live);
                        }
                    }
                    return dr == true;
                }

                if (dr != true) return false;
            }
            catch { return false; }

            ApplyTo(cfg, dlg);

            // 钩子里缓存的是"当前配置"，改完必须让它重新读一遍
            try { HotkeyHost.Refresh(); } catch { }
            return true;
        }

        /// <summary>把窗口里的选择写进配置对象（不落盘 —— 落盘时机由调用方决定）。</summary>
        static void ApplyTo(Config cfg, SettingsWindow dlg)
        {
            Settings st = cfg.Settings ?? (cfg.Settings = new Settings());

            // 四组快捷键
            st.HotkeyMods = dlg._mods[ActPanel];
            st.HotkeyKey = dlg._key[ActPanel];
            st.CcHotkeyMods = dlg._mods[ActQuickSwitch];
            st.CcHotkeyKey = dlg._key[ActQuickSwitch];
            st.VpHotkeyMods = dlg._mods[ActViewport];
            st.VpHotkeyKey = dlg._key[ActViewport];
            st.PieHotkeyMods = dlg._mods[ActPie];
            st.PieHotkeyKey = dlg._key[ActPie];

            // 只有右键有长按时长。侧键在 CAD 里没有原生用途，按下即触发才是它该有的手感
            // （用户 2026-11-14 明确：侧键不用长按，只有右键）。右键最低 100ms ——
            // 再短会跟"短按出右键菜单"打架。
            st.PieHoldMs = Clamp(dlg.SldHold.Value, 100, 800);

            string alias = (dlg.TxtAlias.Text ?? "").Trim();
            st.CommandAlias = alias.Length == 0 ? "" : alias.ToUpperInvariant();

            string ccAlias = (dlg.TxtCcAlias.Text ?? "").Trim();
            st.CcCommandAlias = ccAlias.Length == 0 ? "" : ccAlias.ToUpperInvariant();

            string vpAlias = (dlg.TxtVpAlias.Text ?? "").Trim();
            st.VpCommandAlias = vpAlias.Length == 0 ? "" : vpAlias.ToUpperInvariant();

            string pieAlias = (dlg.TxtPieAlias.Text ?? "").Trim();
            st.PieCommandAlias = pieAlias.Length == 0 ? "" : pieAlias.ToUpperInvariant();

            // 界面是"功能 → 键"，ini 里存的是"键 → 动作"，这里翻一次
            st.MouseRight = dlg.BindOfSlot(SlotRight);
            st.MouseX1 = dlg.BindOfSlot(SlotX1);
            st.MouseX2 = dlg.BindOfSlot(SlotX2);

            // 老字段跟着写一遍：降级回旧版插件时，「圆盘 = 否」仍然是准的
            st.PieEnabled = st.MouseRight == MouseBind.Pie
                         || st.MouseX1 == MouseBind.Pie
                         || st.MouseX2 == MouseBind.Pie;
            st.PieButton = st.MouseX1 == MouseBind.Pie ? 1
                         : st.MouseX2 == MouseBind.Pie ? 2 : 0;
        }

        /// <summary>某颗鼠标键上绑的是哪个功能。没功能用这颗键就是「无」。</summary>
        int BindOfSlot(int slot)
        {
            for (int i = 0; i < ActCount; i++)
                if (_mouse[i] == slot) return ActBind[i];
            return MouseBind.None;
        }

        static int Clamp(double v, int min, int max)
        {
            int n = (int)Math.Round(v);
            return n < min ? min : (n > max ? max : n);
        }

        // ---------------------------------------------------------------- 控件取用

        TextBlock TxtKeyOf(int i)
        {
            switch (i)
            {
                case ActPanel: return TxtKey0;
                case ActQuickSwitch: return TxtKey1;
                case ActViewport: return TxtKey2;
                default: return TxtKey3;
            }
        }

        Border KeyBoxOf(int i)
        {
            switch (i)
            {
                case ActPanel: return KeyBox0;
                case ActQuickSwitch: return KeyBox1;
                case ActViewport: return KeyBox2;
                default: return KeyBox3;
            }
        }

        ComboBox ComboOf(int i)
        {
            switch (i)
            {
                case ActPanel: return CmbMouse0;
                case ActQuickSwitch: return CmbMouse1;
                case ActViewport: return CmbMouse2;
                default: return CmbMouse3;
            }
        }

        // ---------------------------------------------------------------- 装载

        /// <summary>构造函数里先摆一套默认值，<see cref="LoadFrom"/> 再按配置覆盖。</summary>
        void Load()
        {
            // 下拉项在这里加，不写 XAML：写 XAML 的 SelectedIndex 会在
            // InitializeComponent() 期间触发 SelectionChanged，那时别的字段还是 null。
            for (int i = 0; i < ActCount; i++)
            {
                ComboBox c = ComboOf(i);
                if (c == null) continue;
                for (int s = 0; s < SlotNames.Length; s++) c.Items.Add(SlotNames[s]);
                c.SelectedIndex = SlotNone;
            }

            SldHold.Value = 180;

            // 默认：主面板 Ctrl+Shift+Q，圆盘挂右键长按，其余留空
            _mods[ActPanel] = Hotkey.Ctrl | Hotkey.Shift;
            _key[ActPanel] = 0x51;
            _mouse[ActPie] = SlotRight;

            // 页签初始值同样不能写 XAML 的 IsChecked="True"
            PgSetup.IsChecked = true;

            RenderAll();
        }

        void LoadFrom(Settings st)
        {
            if (st != null)
            {
                for (int i = 0; i < ActCount; i++) { _mods[i] = 0; _key[i] = 0; _mouse[i] = 0; }

                _mods[ActPanel] = st.HotkeyMods;
                _key[ActPanel] = st.HotkeyKey;

                _mods[ActQuickSwitch] = st.CcHotkeyMods;
                _key[ActQuickSwitch] = st.CcHotkeyKey;

                _mods[ActViewport] = st.VpHotkeyMods;
                _key[ActViewport] = st.VpHotkeyKey;

                _mods[ActPie] = st.PieHotkeyMods;
                _key[ActPie] = st.PieHotkeyKey;

                // "键 → 动作"翻回"功能 → 键"
                SetSlot(st.MouseRight, SlotRight);
                SetSlot(st.MouseX1, SlotX1);
                SetSlot(st.MouseX2, SlotX2);

                SldHold.Value = Clamp(st.PieHoldMs <= 0 ? 180 : st.PieHoldMs, 100, 800);

                TxtAlias.Text = st.CommandAlias ?? "";
                TxtCcAlias.Text = st.CcCommandAlias ?? "";
                TxtVpAlias.Text = st.VpCommandAlias ?? "";
                TxtPieAlias.Text = st.PieCommandAlias ?? "";
            }

            RenderAll();
        }

        /// <summary>这颗键上绑的是 bind 动作 → 把那个功能的下拉设成 slot。</summary>
        void SetSlot(int bind, int slot)
        {
            if (bind == MouseBind.None || !MouseBind.Valid(bind)) return;
            for (int i = 0; i < ActCount; i++)
                if (ActBind[i] == bind) { _mouse[i] = slot; return; }
        }

        // ---------------------------------------------------------------- 显示

        void RenderAll()
        {
            RenderKeys();
            RenderMouse();
            RenderHold();
            RenderAliases();
            RenderCfgPath();
        }

        void RenderKeys()
        {
            for (int i = 0; i < ActCount; i++)
            {
                TextBlock t = TxtKeyOf(i);
                if (t == null) continue;

                // 正在录这一格：保留"按下组合键…"，别被刷回旧值
                if (_capturing && _captureAct == i) continue;

                t.Text = _key[i] > 0 ? Hotkey.Text(_mods[i], _key[i]) : "无";
                t.Foreground = _key[i] > 0 ? UiTheme.B(UiTheme.Text) : UiTheme.B(UiTheme.TextFaint);

                Border b = KeyBoxOf(i);
                if (b != null) b.BorderBrush = UiTheme.White(0x33);
            }
        }

        void RenderMouse()
        {
            _syncing = true;
            for (int i = 0; i < ActCount; i++)
            {
                ComboBox c = ComboOf(i);
                if (c != null) c.SelectedIndex = _mouse[i];
            }
            _syncing = false;
        }

        void RenderHold()
        {
            if (TxtHold == null) return;
            TxtHold.Text = Clamp(SldHold.Value, 100, 800) + " ms";
        }

        /// <summary>命令名留空时，格子里显示灰色的内置命令当占位。</summary>
        void RenderAliases()
        {
            Ph(PhAlias, TxtAlias);
            Ph(PhCcAlias, TxtCcAlias);
            Ph(PhVpAlias, TxtVpAlias);
            Ph(PhPieAlias, TxtPieAlias);
        }

        static void Ph(TextBlock ph, TextBox box)
        {
            if (ph == null || box == null) return;
            ph.Visibility = (box.Text ?? "").Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RenderCfgPath()
        {
            ShowCfgPath(TxtCfgPath, SafePath(() => ConfigStore.FilePath));
            ShowCfgPath(TxtVpPath, SafePath(() => VpStore.FilePath));
        }

        static string SafePath(Func<string> get)
        {
            try { return get() ?? ""; } catch { return ""; }
        }

        /// <summary>格子里只放文件名 —— 一列放不下全路径，而且"哪个文件"才是要区分的。</summary>
        static void ShowCfgPath(TextBlock t, string full)
        {
            if (t == null) return;
            t.Text = string.IsNullOrEmpty(full) ? "—" : System.IO.Path.GetFileName(full);
            t.ToolTip = full;
        }

        /// <summary>翻页：设置 / 帮助。</summary>
        void ShowPage(bool help)
        {
            if (PaneSetup == null) return;
            PaneSetup.Visibility = help ? Visibility.Collapsed : Visibility.Visible;
            PaneHelp.Visibility = help ? Visibility.Visible : Visibility.Collapsed;
        }

        void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (PaneSetup == null) return;    // 初始化期间会被调，此时控件还没建
            ShowPage(PgHelp.IsChecked == true);
        }

        // ---------------------------------------------------------------- 鼠标绑定

        /// <summary>
        /// 换了某一行用的鼠标键。一颗键只能绑一个功能 ——
        /// 用户把视口工具指到"侧键后退"时，原来占着这颗键的功能自动让位（变「不用」）。
        /// 不然两个功能抢同一颗键，谁先触发完全看代码里的循环顺序，用户根本猜不到。
        /// </summary>
        void Mouse_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || CmbMouse0 == null) return;

            ComboBox c = sender as ComboBox;
            if (c == null) return;

            int i;
            if (!int.TryParse(c.Tag as string, out i) || i < 0 || i >= ActCount) return;

            int slot = c.SelectedIndex < 0 ? SlotNone : c.SelectedIndex;
            _mouse[i] = slot;
            if (slot == SlotNone) return;

            _syncing = true;
            for (int j = 0; j < ActCount; j++)
            {
                if (j == i || _mouse[j] != slot) continue;
                _mouse[j] = SlotNone;
                ComboBox other = ComboOf(j);
                if (other != null) other.SelectedIndex = SlotNone;
            }
            _syncing = false;
        }

        // ---------------------------------------------------------------- 抓键

        void KeyBox_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            int i;
            if (!int.TryParse((sender as FrameworkElement)?.Tag as string, out i)) return;
            BeginCapture(i);
        }

        void BeginCapture(int act)
        {
            _capturing = true;
            _captureAct = act;

            // 钩子正在跑的时候会先把这一下吞掉，输入框里就什么也录不到 —— 必须先挂起
            HotkeyHost.Suspended = true;

            TextBlock t = TxtKeyOf(act);
            if (t != null)
            {
                t.Text = "按下组合键…";
                t.Foreground = UiTheme.B(UiTheme.AccentLine);
            }

            Border b = KeyBoxOf(act);
            if (b != null)
            {
                b.BorderBrush = UiTheme.B(UiTheme.AccentLine);
                b.Focus();
            }
        }

        void EndCapture()
        {
            _capturing = false;
            _captureAct = -1;
            HotkeyHost.Suspended = false;
            RenderKeys();
        }

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturing) return;

            // 抓键期间把这一下全吃掉，别让 Esc 顺手把窗口也关了
            e.Handled = true;

            Key k = e.Key == Key.System ? e.SystemKey : e.Key;

            // 只按了修饰键：继续等主键，不结束
            if (k == Key.LeftCtrl || k == Key.RightCtrl ||
                k == Key.LeftAlt || k == Key.RightAlt ||
                k == Key.LeftShift || k == Key.RightShift ||
                k == Key.LWin || k == Key.RWin ||
                k == Key.System || k == Key.None) return;

            if (k == Key.Escape) { EndCapture(); return; }

            // 退格 / Delete = 清成「无」
            if (k == Key.Back || k == Key.Delete)
            {
                _mods[_captureAct] = 0;
                _key[_captureAct] = 0;
                EndCapture();
                return;
            }

            int mods = 0;
            ModifierKeys m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Control) != 0) mods |= Hotkey.Ctrl;
            if ((m & ModifierKeys.Alt) != 0) mods |= Hotkey.Alt;
            if ((m & ModifierKeys.Shift) != 0) mods |= Hotkey.Shift;

            // 不带 Ctrl / Alt 的组合会跟正常打字抢键，直接拒绝。停在录制状态让用户重按 ——
            // 提示就写在格子里，不再往窗口里加一行常驻说明。
            if ((mods & (Hotkey.Ctrl | Hotkey.Alt)) == 0)
            {
                TextBlock t = TxtKeyOf(_captureAct);
                if (t != null)
                {
                    t.Text = "要带 Ctrl 或 Alt";
                    t.Foreground = UiTheme.B(UiTheme.Warn);
                }
                return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk <= 0) return;

            _mods[_captureAct] = mods;
            _key[_captureAct] = vk;
            EndCapture();
        }

        // ---------------------------------------------------------------- 命令名

        void Alias_TextChanged(object sender, TextChangedEventArgs e)
        {
            RenderAliases();
        }

        // ---------------------------------------------------------------- 配置导出 / 导入

        /// <summary>
        /// 导出：另存一份 ini。默认名带日期（Y归层配置_2026-11-11.ini），
        /// 用户改了也无妨 —— 导入端认内容不认文件名。
        /// </summary>
        void BtnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出配置",
                    Filter = "Y_归层工具配置 (*.ini)|*.ini",
                    FileName = "Y归层配置_" + DateTime.Now.ToString("yyyy-MM-dd") + ".ini"
                };
                if (dlg.ShowDialog(this) != true) return;

                string err;
                if (ConfigStore.ExportTo(dlg.FileName, out err))
                {
                    TxtIoHint.Text = "已导出到 " + dlg.FileName;
                    TxtIoHint.Foreground = UiTheme.B(UiTheme.Ok);
                }
                else
                {
                    TxtIoHint.Text = "导出失败：" + err;
                    TxtIoHint.Foreground = UiTheme.B(UiTheme.Warn);
                }
            }
            catch (System.Exception ex)
            {
                TxtIoHint.Text = "导出失败：" + ex.Message;
                TxtIoHint.Foreground = UiTheme.B(UiTheme.Warn);
            }
            TxtIoHint.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 导入：整体替换当前配置。确认弹窗说清三件事 ——
        /// 是"整体替换"（不是合并）、未保存的改动会丢、导入前有自动备份。
        /// 导入成功当场落盘并全链路生效（快捷键 / 自定义命令 / 状态条 / 已打开的面板）。
        /// </summary>
        void BtnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            TxtIoHint.Visibility = Visibility.Visible;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入配置",
                    Filter = "Y_归层工具配置 (*.ini)|*.ini|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) != true) return;

                if (MessageBox.Show(this,
                        "导入会用选中的文件整体替换当前全部配置（映射表、开关、快捷键），\n" +
                        "不是合并 —— 面板里还没保存的改动会一起被替换掉。\n" +
                        "导入前会自动备份当前配置，确定继续？",
                        "导入配置",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;

                string err;
                if (!ConfigStore.ImportFrom(dlg.FileName, out err))
                {
                    TxtIoHint.Text = "导入失败：" + err + "。当前配置没有改动。";
                    TxtIoHint.Foreground = UiTheme.B(UiTheme.Warn);
                    return;
                }

                _imported = true;

                // 窗口字段全部按新配置重摆 —— 否则用户随手点「保存」会把旧值写回去
                LoadFrom(ConfigStore.Current.Settings);

                // 导入完立即生效的几条链路
                try { HotkeyHost.Refresh(); } catch { }
                try { CommandAliasHost.Sync(); } catch { }
                try { StatusHud.Flash("配置已导入"); } catch { }
                try { PanelHost.NotifyConfigImported(); } catch { }

                TxtIoHint.Text = "已导入并立即生效（导入前的配置备份在 AutoLayer.ini.导入前备份）。";
                TxtIoHint.Foreground = UiTheme.B(UiTheme.Ok);
            }
            catch (System.Exception ex)
            {
                TxtIoHint.Text = "导入失败：" + ex.Message + "。当前配置没有改动。";
                TxtIoHint.Foreground = UiTheme.B(UiTheme.Warn);
            }
        }

        // ---------------------------------------------------------------- 视口工具页

        void BtnVpExport_Click(object sender, RoutedEventArgs e)
        {
            TxtVpIoHint.Visibility = Visibility.Visible;
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出视口配置",
                    Filter = "Y_视口图层工具配置 (*.ini)|*.ini",
                    FileName = "视口配置_" + DateTime.Now.ToString("yyyy-MM-dd") + ".ini"
                };
                if (dlg.ShowDialog(this) != true) return;

                string err;
                if (VpStore.ExportTo(dlg.FileName, out err))
                {
                    TxtVpIoHint.Text = "已导出到 " + dlg.FileName;
                    TxtVpIoHint.Foreground = UiTheme.B(UiTheme.Ok);
                }
                else
                {
                    TxtVpIoHint.Text = "导出失败：" + err;
                    TxtVpIoHint.Foreground = UiTheme.B(UiTheme.Warn);
                }
            }
            catch (System.Exception ex)
            {
                TxtVpIoHint.Text = "导出失败：" + ex.Message;
                TxtVpIoHint.Foreground = UiTheme.B(UiTheme.Warn);
            }
        }

        void BtnVpImport_Click(object sender, RoutedEventArgs e)
        {
            TxtVpIoHint.Visibility = Visibility.Visible;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入视口配置",
                    Filter = "Y_视口图层工具配置 (*.ini)|*.ini|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) != true) return;

                if (MessageBox.Show(this,
                        "导入会用选中的文件整体替换视口配置（图框、状态、兜底开关），\n" +
                        "不是合并。导入前会自动备份当前视口配置，确定继续？",
                        "导入视口配置",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;

                string err;
                if (!VpStore.ImportFrom(dlg.FileName, out err))
                {
                    TxtVpIoHint.Text = "导入失败：" + err + "。当前视口配置没有改动。";
                    TxtVpIoHint.Foreground = UiTheme.B(UiTheme.Warn);
                    return;
                }

                // 缓存里还是旧的，必须作废重读；已经打开的视口面板也要按新配置重摆
                VpStore.Invalidate();
                try { VpLayer.Ui.VpPanel.RefreshIfOpen(); } catch { }

                TxtVpIoHint.Text = "已导入并立即生效（导入前的视口配置备份在 ViewportState.ini.导入前备份）。";
                TxtVpIoHint.Foreground = UiTheme.B(UiTheme.Ok);
            }
            catch (System.Exception ex)
            {
                TxtVpIoHint.Text = "导入失败：" + ex.Message + "。当前视口配置没有改动。";
                TxtVpIoHint.Foreground = UiTheme.B(UiTheme.Warn);
            }
        }

        // ---------------------------------------------------------------- 保存

        void Hold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtHold == null) return;     // 模板还没建完时会被调一次
            RenderHold();
        }

        void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 保存前把"还在抓键"的状态收尾，否则钩子会一直处于挂起状态
            if (_capturing) EndCapture();

            // 命令名不合法 / 重名就停在窗口里让用户改，别存下去一个敲不出来的名字
            string[] names =
            {
                (TxtAlias.Text ?? "").Trim(),
                (TxtCcAlias.Text ?? "").Trim(),
                (TxtVpAlias.Text ?? "").Trim(),
                (TxtPieAlias.Text ?? "").Trim()
            };
            TextBox[] boxes = { TxtAlias, TxtCcAlias, TxtVpAlias, TxtPieAlias };

            for (int i = 0; i < ActCount; i++)
            {
                if (names[i].Length == 0) continue;

                string why;
                if (!CommandAliasHost.IsValidName(names[i], out why))
                {
                    MessageBox.Show(this, "「" + names[i] + "」不能用：" + why,
                                    HotkeyHost.ActionName(i) + "命令",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    boxes[i].Focus();
                    boxes[i].SelectAll();
                    return;
                }

                for (int j = 0; j < i; j++)
                {
                    if (names[j].Length == 0) continue;
                    if (!string.Equals(names[j], names[i], StringComparison.OrdinalIgnoreCase)) continue;

                    MessageBox.Show(this,
                        "「" + names[i] + "」同时填给了「" + HotkeyHost.ActionName(j) + "」和「"
                        + HotkeyHost.ActionName(i) + "」，敲了不知道进哪个，换一个。",
                        "命令重名", MessageBoxButton.OK, MessageBoxImage.Warning);
                    boxes[i].Focus();
                    boxes[i].SelectAll();
                    return;
                }
            }

            // 两个功能用同一组快捷键 = 一个永远轮不到，必须拦
            for (int i = 0; i < ActCount; i++)
            {
                if (_key[i] <= 0) continue;
                for (int j = i + 1; j < ActCount; j++)
                {
                    if (_key[j] != _key[i] || _mods[j] != _mods[i]) continue;

                    MessageBox.Show(this,
                        "「" + HotkeyHost.ActionName(i) + "」和「" + HotkeyHost.ActionName(j)
                        + "」都配成了 " + Hotkey.Text(_mods[i], _key[i]) + "，一个键只能触发一个功能，改一个。",
                        "快捷键重复", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            DialogResult = true;
        }
    }
}
