using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AutoLayer.Core;

namespace AutoLayer.UI
{
    /// <summary>
    /// 设置窗口：面板快捷键 + 圆盘触发方式。
    ///
    /// 这里只管"收集用户的选择"，真正写回配置在 <see cref="Edit"/> 里一次做完 ——
    /// 中途点取消就一个字节都不动，跟面板里那些 chip 的行为保持一致。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>
        /// 「关闭」这一档没有对应的 PieButton 值（关掉时 ini 里是「圆盘 = 否」），用这个临时标记。
        ///
        /// ⚠ 不能跟 PieButton 的合法值 0/1/2 撞车。1.11 这里原本是 3，而当时还有一个
        /// 「任一侧键」= 3，结果选它会走进"关闭"分支（圆盘被关掉、侧键永远不弹），
        /// 圆盘本来是关的又会勾着「任一侧键」。1.12 干脆把「任一侧键」删了，
        /// 哨兵保留 4，以后再加档位也别回头用 3。
        /// </summary>
        const int TriggerOff = 4;

        int _mods;
        int _key;
        bool _capturing;

        SettingsWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Apply(this);

            // 不管怎么关（取消 / Esc / 标题栏 X），都要把钩子放回去，
            // 否则快捷键从此再也不响应，而且完全没有提示。
            Closing += (s, e) => { _capturing = false; HotkeyHost.Suspended = false; };

            Load();
        }

        /// <summary>打开设置。返回 true 表示用户点了保存（配置已在里面改好）。</summary>
        public static bool Edit(Window owner, Config cfg)
        {
            if (cfg == null) return false;

            SettingsWindow dlg;
            try
            {
                dlg = new SettingsWindow();
                try { dlg.Owner = owner; } catch { }
                dlg.LoadFrom(cfg.Settings);
                if (dlg.ShowDialog() != true) return false;
            }
            catch { return false; }

            Settings st = cfg.Settings ?? (cfg.Settings = new Settings());
            st.HotkeyMods = dlg._mods;
            st.HotkeyKey = dlg._key;
            st.PieHoldMs = (int)Math.Round(dlg.SldHold.Value);

            string alias = (dlg.TxtAlias.Text ?? "").Trim();
            st.CommandAlias = alias.Length == 0 ? "" : alias.ToUpperInvariant();

            int trig = dlg.Trigger();
            if (trig == TriggerOff)
            {
                st.PieEnabled = false;
            }
            else
            {
                st.PieEnabled = true;
                st.PieButton = trig;
            }

            // 钩子里缓存的是"当前配置"，改完必须让它重新读一遍
            try { HotkeyHost.Refresh(); } catch { }
            return true;
        }

        // ---------------------------------------------------------------- 装载

        /// <summary>构造函数里先摆一套默认值，<see cref="LoadFrom"/> 再按配置覆盖。</summary>
        void Load()
        {
            SldHold.Value = 180;
            RdoRight.IsChecked = true;
            RenderKey();
            RenderHold();
        }

        void LoadFrom(Settings st)
        {
            if (st != null)
            {
                _mods = st.HotkeyMods;
                _key = st.HotkeyKey;

                SldHold.Value = st.PieHoldMs < 100 ? 100 : (st.PieHoldMs > 1000 ? 1000 : st.PieHoldMs);

                int trig = st.PieEnabled ? st.PieButton : TriggerOff;
                if (trig == 1) RdoX1.IsChecked = true;
                else if (trig == 2) RdoX2.IsChecked = true;
                else if (trig == TriggerOff) RdoOff.IsChecked = true;
                // 0 = 右键长按；老配置里可能还留着 3（已删掉的「任一侧键」），
                // 这里一并落到「右键长按」—— 不认识的档位退回最保守的那个。
                else RdoRight.IsChecked = true;

                TxtAlias.Text = st.CommandAlias ?? "";
            }

            RenderKey();
            RenderHold();
            RenderTriggerHint();
            RenderAliasHint();
        }

        int Trigger()
        {
            if (RdoX1.IsChecked == true) return 1;
            if (RdoX2.IsChecked == true) return 2;
            if (RdoOff.IsChecked == true) return TriggerOff;
            return 0;
        }

        // ---------------------------------------------------------------- 显示

        void RenderKey()
        {
            if (_capturing) return;

            TxtKey.Text = _key > 0 ? Hotkey.Text(_mods, _key) : "无";
            TxtKey.Foreground = _key > 0
                ? UiTheme.B(UiTheme.Text)
                : UiTheme.B(UiTheme.TextFaint);
            KeyBox.BorderBrush = UiTheme.White(0x33);
        }

        void RenderHold()
        {
            TxtHold.Text = (int)Math.Round(SldHold.Value) + " ms";
        }

        void RenderTriggerHint()
        {
            int t = Trigger();
            if (t == TriggerOff)
                TxtPieHint.Text = "圆盘已关闭。需要时还能用 ALPIE 命令手动弹一次。";
            else if (t == 0)
                TxtPieHint.Text = "按住右键不动到这个时间才弹；短按照旧是 CAD 自己的右键菜单。";
            else
                TxtPieHint.Text = "侧键按下就弹、松手确认，不用等长按。没有侧键的鼠标用「右键长按」。";
        }

        /// <summary>CAD 命令那一格的说明文字：合法 / 不合法 / 被占用，三种说法。</summary>
        void RenderAliasHint()
        {
            string a = (TxtAlias.Text ?? "").Trim();

            if (a.Length == 0)
            {
                TxtAliasHint.Text = "自定义命令名，敲它等于敲 QH（比如填 GH）。留空 = 只用 QH。";
                TxtAliasHint.Foreground = UiTheme.B(UiTheme.TextFaint);
                return;
            }

            string why;
            if (!CommandAliasHost.IsValidName(a, out why))
            {
                TxtAliasHint.Text = "「" + a + "」不能用：" + why;
                TxtAliasHint.Foreground = UiTheme.B(UiTheme.Warn);
                return;
            }

            // 已经被占用的名字（CAD 自带命令或别的插件的）会被抢走，先提醒一句
            string used = CommandAliasHost.NameInUse(a.ToUpperInvariant());
            if (!string.IsNullOrEmpty(used) &&
                !string.Equals(a, CommandAliasHost.Defined, StringComparison.OrdinalIgnoreCase))
            {
                TxtAliasHint.Text = "⚠ 这个名字已经被占用（" + used + "），敲它可能进不了面板，换个更稳妥。";
                TxtAliasHint.Foreground = UiTheme.B(UiTheme.Warn);
                return;
            }

            TxtAliasHint.Text = "敲 " + a.ToUpperInvariant() + " 就等于敲 QH。点保存后立刻生效。";
            TxtAliasHint.Foreground = UiTheme.B(UiTheme.TextFaint);
        }

        // ---------------------------------------------------------------- 抓键

        void KeyBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            BeginCapture();
        }

        void BeginCapture()
        {
            _capturing = true;
            // 钩子正在跑的时候会先把这一下吞掉，输入框里就什么也录不到 —— 必须先挂起
            HotkeyHost.Suspended = true;

            TxtKey.Text = "按下组合键…";
            TxtKey.Foreground = UiTheme.B(UiTheme.AccentLine);
            KeyBox.BorderBrush = UiTheme.B(UiTheme.AccentLine);
            KeyBox.Focus();
        }

        void EndCapture()
        {
            _capturing = false;
            HotkeyHost.Suspended = false;
            RenderKey();
            TxtKeyHint.Text = "点上面的框，然后按下想用的组合键。必须带 Ctrl / Alt 之一（只有 Shift 的组合会跟打字抢键）。";
            TxtKeyHint.Foreground = UiTheme.B(UiTheme.TextFaint);
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
                _mods = 0;
                _key = 0;
                EndCapture();
                return;
            }

            int mods = 0;
            ModifierKeys m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Control) != 0) mods |= Hotkey.Ctrl;
            if ((m & ModifierKeys.Alt) != 0) mods |= Hotkey.Alt;
            if ((m & ModifierKeys.Shift) != 0) mods |= Hotkey.Shift;

            // 不带 Ctrl / Alt 的组合会跟正常打字抢键，直接拒绝并说明原因
            if ((mods & (Hotkey.Ctrl | Hotkey.Alt)) == 0)
            {
                TxtKeyHint.Text = "「" + Hotkey.KeyName(KeyInterop.VirtualKeyFromKey(k))
                    + "」单独用会跟打字抢键，请加上 Ctrl 或 Alt 再按一次。";
                TxtKeyHint.Foreground = UiTheme.B(UiTheme.Warn);
                return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk <= 0) return;

            _mods = mods;
            _key = vk;
            EndCapture();
        }

        void BtnClearKey_Click(object sender, RoutedEventArgs e)
        {
            _capturing = false;
            HotkeyHost.Suspended = false;
            _mods = 0;
            _key = 0;
            RenderKey();

            // 配了自定义命令的话，清掉快捷键不等于没别的路 —— 文案要说准
            string alias = (TxtAlias.Text ?? "").Trim().ToUpperInvariant();
            TxtKeyHint.Text = alias.Length > 0
                ? "快捷键已清空，面板用 QH 或 " + alias + " 打开。"
                : "快捷键已清空，面板只能用 QH 命令打开。";
            TxtKeyHint.Foreground = UiTheme.B(UiTheme.TextFaint);
        }

        /// <summary>
        /// 清空自定义命令名。保存时会把 ini 里那行写成「无」，
        /// 并撤掉已经定义出去的 LISP 命令（`(setq c:名字 nil)`），不留残留。
        /// </summary>
        void BtnClearAlias_Click(object sender, RoutedEventArgs e)
        {
            TxtAlias.Text = "";
            RenderAliasHint();
            TxtAlias.Focus();
        }

        // ---------------------------------------------------------------- 其它

        void SldHold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtHold == null) return;      // 模板还没建完时会被调一次
            RenderHold();
        }

        /// <summary>触发方式换了，下面那句说明要跟着换 —— 否则用户看到的是上一个选项的解释。</summary>
        void Trig_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtPieHint == null) return;   // 初始化时会被调好几次
            RenderTriggerHint();
        }

        void TxtAlias_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtAliasHint == null) return;      // 模板还没建完
            RenderAliasHint();
        }

        void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 保存前把"还在抓键"的状态收尾，否则钩子会一直处于挂起状态
            if (_capturing) EndCapture();

            // CAD 命令名不合法就停在窗口里让用户改，别存下去一个敲不出来的名字
            string alias = (TxtAlias.Text ?? "").Trim();
            if (alias.Length > 0)
            {
                string why;
                if (!CommandAliasHost.IsValidName(alias, out why))
                {
                    TxtAliasHint.Text = "「" + alias + "」不能用：" + why;
                    TxtAliasHint.Foreground = UiTheme.B(UiTheme.Warn);
                    TxtAlias.Focus();
                    TxtAlias.SelectAll();
                    return;
                }
            }

            DialogResult = true;
        }
    }
}
