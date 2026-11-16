using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YGuiCeng.Setup
{
    internal sealed class MainForm : Form
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 0x2;
        private const int TITLE_H = 30;
        private const int CARD_ROW = 34, CARD_H = 30;
        private const int LIST_TOP = TITLE_H + 30;
        private const int VISIBLE_ROWS = 6;   // 列表最多露 6 行，再多滚动，窗口不会被撑大

        private readonly List<CadInfo> _items;
        private readonly List<CadCard> _cards = new List<CadCard>();
        private readonly string _warning;
        private Panel _list;
        private TextBox _status;
        private FlatButton _apply, _uninstall;
        private bool _hoverClose, _hoverMin;
        private bool _busy;

        /// <summary>--demo 多版本布局预览：只读，任何按钮都不会改注册表/文件。</summary>
        public bool DemoMode;

        public MainForm(List<CadInfo> items, string warning)
        {
            _items = items;
            _warning = warning ?? "";
            Build();
        }

        private void Build()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.DoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.WindowBg;
            ForeColor = Theme.Text;
            Font = Theme.Font(9f);           // 仅作 DPI 缩放基准，所有可见控件显式给字号
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = true;
            MinimizeBox = true;
            MaximizeBox = false;
            Text = SetupPaths.ProductName + " 安装管理";

            int visible = Math.Min(Math.Max(_items.Count, 1), VISIBLE_ROWS);
            int listH = visible * CARD_ROW + 4;
            int w = 480;
            int fy = LIST_TOP + listH + 8;
            int h = fy + 28 + 20 + 50 + 10;
            ClientSize = new Size(w, h);
            MinimumSize = Size;

            AddLabel("勾选要启用的版本", 12, TITLE_H + 6, 250, 18, Theme.Sm, false, Theme.TextMuted);
            if (_items.Count > VISIBLE_ROWS)
                AddLabel("共 " + _items.Count + " 个 · 可滚动", w - 12 - 130, TITLE_H + 6, 130, 18,
                         Theme.Xs, false, Theme.TextFaint, ContentAlignment.MiddleRight);

            _list = new Panel
            {
                Location = new Point(12, LIST_TOP),
                Size = new Size(w - 24, listH),
                BackColor = Theme.WindowBg,
                AutoScroll = true
            };
            _list.HorizontalScroll.Enabled = false;
            _list.HorizontalScroll.Visible = false;
            Controls.Add(_list);

            int scrollW = _items.Count > VISIBLE_ROWS ? 18 : 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var card = new CadCard(_items[i])
                {
                    Location = new Point(2, 2 + i * CARD_ROW),
                    Size = new Size(_list.Width - 4 - scrollW, CARD_H)
                };
                _cards.Add(card);
                _list.Controls.Add(card);
            }

            if (_items.Count == 0)
            {
                AddLabel("没有检测到 AutoCAD（需先启动过一次 CAD）",
                         14, LIST_TOP + 8, w - 28, 22, Theme.Sm, false, Theme.Warn);
            }

            int lx = 12;
            lx = AddLink("全选", lx, fy + 3, delegate { SetAll(true); });
            lx = AddLink("全不选", lx, fy + 3, delegate { SetAll(false); });
            AddLink("安装目录", lx, fy + 3, delegate { OpenInstallDir(); });

            _uninstall = new FlatButton("卸载全部", ButtonKind.Danger)
            {
                Location = new Point(w - 12 - 100 - 6 - 88, fy),
                Size = new Size(88, 28)
            };
            _uninstall.Click += delegate { DoFullUninstall(); };
            Controls.Add(_uninstall);

            _apply = new FlatButton("应用更改", ButtonKind.Primary)
            {
                Location = new Point(w - 12 - 100, fy),
                Size = new Size(100, 28)
            };
            _apply.Click += delegate { DoApply(false); };
            Controls.Add(_apply);

            string warn = RegistryService.IsCadRunning()
                ? "[!] AutoCAD 正在运行，建议先关闭"
                : (string.IsNullOrEmpty(_warning) ? "" : "[!] " + _warning);
            AddLabel(string.IsNullOrEmpty(warn) ? "重启 AutoCAD 后生效" : warn,
                     12, fy + 32, w - 24, 16, Theme.Sm, false,
                     string.IsNullOrEmpty(warn) ? Theme.TextFaint : Theme.Warn);

            _status = new TextBox
            {
                Location = new Point(12, fy + 56),
                Size = new Size(w - 24, 42),
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.CardBg,
                ForeColor = Theme.TextMuted,
                Font = Theme.Font(Theme.Sm),
                Text = StatusHint,
                TabStop = false
            };
            Controls.Add(_status);
        }

        private void AddLabel(string text, int x, int y, int w, int h, float size, bool bold, Color color,
                              ContentAlignment align = ContentAlignment.MiddleLeft)
        {
            Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Color.Transparent,
                ForeColor = color,
                Font = Theme.Font(size, bold ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = align,
                AutoEllipsis = true
            });
        }

        private int AddLink(string text, int x, int y, EventHandler handler)
        {
            var l = new LinkText(text) { Location = new Point(x, y) };
            l.Click += handler;
            Controls.Add(l);
            return x + l.Width + 16;
        }

        private static string StatusHint
        {
            get { return "操作结果会显示在这里"; }
        }

        /// <summary>空状态不显示滚动条，结果多时才挂上，避免右侧常驻一条灰条。</summary>
        private void SetStatus(string text, Color color, int lineCount)
        {
            _status.ScrollBars = lineCount > 2 ? ScrollBars.Vertical : ScrollBars.None;
            _status.ForeColor = color;
            _status.Text = text;
        }

        private void SetAll(bool value)
        {
            foreach (var c in _items) if (!c.NeedsFirstRun) c.Checked = value;
            foreach (var c in _cards) c.Invalidate();
            _status.ScrollBars = ScrollBars.None;
            _status.ForeColor = Theme.TextMuted;
            _status.Text = StatusHint;
        }

        private void OpenInstallDir()
        {
            try
            {
                if (!Directory.Exists(SetupPaths.InstallDir))
                {
                    ShowInfo("还没有安装目录",
                        "先点一次「应用更改」，插件文件和配置会放在：\n" + SetupPaths.InstallDir);
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + SetupPaths.InstallDir + "\"");
            }
            catch (Exception e)
            {
                ShowInfo("打不开安装目录", e.Message + "\n\n" + SetupPaths.InstallDir);
            }
        }

        private void DoApply(bool fullUninstall, bool keepConfig = true)
        {
            if (_busy) return;
            if (DemoMode)
            {
                ShowInfo("布局预览", "这是多版本界面预览，不会改动注册表或文件。");
                return;
            }
            if (!fullUninstall && RegistryService.IsCadRunning())
            {
                var dlg = new ConfirmForm("AutoCAD 正在运行",
                    "CAD 开着时安装，插件文件可能被占用，加载项也可能在 CAD 退出时被覆盖。\n" +
                    "建议先关闭所有 AutoCAD 窗口。",
                    "仍然继续", null, "取消", false);
                if (dlg.ShowDialog(this) != 1) return;
            }

            _busy = true;
            _apply.Enabled = false;
            _uninstall.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                var r = ApplyEngine.Apply(_items, fullUninstall, keepConfig);
                SetStatus(r.Ok
                    ? string.Join(Environment.NewLine, r.Lines.ToArray())
                    : "失败：" + r.Error + Environment.NewLine + "原配置未改动",
                    r.Ok ? Theme.OkText : Theme.Danger,
                    r.Ok ? r.Lines.Count : 2);
                foreach (var c in _cards) c.Invalidate();
                if (fullUninstall && r.Ok)
                {
                    ShowInfo("卸载完成", "已从所有 AutoCAD 版本卸载。\n\n" +
                        (keepConfig ? "配置文件已保留：" + SetupPaths.IniPath : "配置文件已一并删除。"));
                    Close();
                }
            }
            finally
            {
                _busy = false;
                _apply.Enabled = true;
                _uninstall.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        private void DoFullUninstall()
        {
            var dlg = new ConfirmForm("卸载 " + SetupPaths.ProductName,
                "将从所有 AutoCAD 版本卸载并删除插件文件。\n" +
                "保留配置可留待以后重装使用。",
                "保留配置并卸载", "连配置一起删", "取消", true);
            int ans = dlg.ShowDialog(this);
            if (ans == 0 || ans < 0) return;
            DoApply(true, ans == 1);
        }

        private void ShowInfo(string title, string body)
        {
            using (var d = new ConfirmForm(title, body, "好", null, null, false))
                d.ShowDialog(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(Theme.WindowBg);
            using (var b = new SolidBrush(Theme.Divider))
                g.FillRectangle(b, 0, TITLE_H - 1, Width, 1);
            using (var p = Theme.RoundRect(new Rectangle(12, 8, 14, 14), 3))
            using (var b = new SolidBrush(Theme.Accent))
                g.FillPath(b, p);
            var titleFont = Theme.Font(Theme.Sm, FontStyle.Bold);
            TextRenderer.DrawText(g, SetupPaths.ProductName, titleFont, new Point(32, 6), Theme.Text);
            int titleW = TextRenderer.MeasureText(SetupPaths.ProductName, titleFont).Width;
            TextRenderer.DrawText(g, "安装管理", Theme.Font(Theme.Sm),
                new Point(32 + titleW + 8, 6), Theme.TextFaint);

            TextRenderer.DrawText(g, "—", Theme.Font(Theme.Sm), new Point(Width - 76, 5),
                _hoverMin ? Theme.Text : Theme.TextFaint);
            TextRenderer.DrawText(g, "×", Theme.Font(Theme.Md), new Point(Width - 40, 3),
                _hoverClose ? Theme.Danger : Theme.TextFaint);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.Y < TITLE_H)
            {
                if (e.X >= Width - 52) { Close(); return; }
                if (e.X >= Width - 92) { WindowState = FormWindowState.Minimized; return; }
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool c = e.Y < TITLE_H && e.X >= Width - 52;
            bool m = e.Y < TITLE_H && e.X >= Width - 92 && e.X < Width - 52;
            if (c != _hoverClose || m != _hoverMin)
            {
                _hoverClose = c; _hoverMin = m; Invalidate(new Rectangle(0, 0, Width, TITLE_H));
            }
        }
    }

    /// <summary>单个 CAD 版本卡片：只留版本名、R 版本、状态徽标 —— 注册表标识和安装路径是内部信息，不显示。</summary>
    internal sealed class CadCard : Control
    {
        private readonly CadInfo _info;
        private bool _hover;

        public CadCard(CadInfo info)
        {
            _info = info;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.DoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.WindowBg;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        private bool Active { get { return _info.Checked && !_info.NeedsFirstRun; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg = _hover ? Theme.CardBgHover : (Active ? Theme.CardBg : Theme.CardBgOff);
            using (var path = Theme.RoundRect(rect, 6))
            using (var fill = new SolidBrush(bg))
            using (var pen = new Pen(Active ? Theme.Accent : Theme.CardBorderOff))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (Active)
            {
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, 1, 6, 3, Height - 12);
            }

            var box = new Rectangle(10, 8, 12, 12);
            using (var bp = Theme.RoundRect(box, 3))
            {
                if (Active)
                {
                    using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, bp);
                    TextRenderer.DrawText(g, "✓", Theme.Font(Theme.Xs, FontStyle.Bold),
                        new Point(box.X + 1, box.Y - 1), Theme.AccentText);
                }
                else
                {
                    using (var p = new Pen(Theme.TextFaint)) g.DrawPath(p, bp);
                }
            }

            int tx = 30;
            Color title = Active ? Theme.Text : Theme.TextDim;
            var nameFont = Theme.Font(Theme.Sm, FontStyle.Bold);
            TextRenderer.DrawText(g, _info.DisplayName, nameFont, new Point(tx, 5), title);
            int nameW = TextRenderer.MeasureText(_info.DisplayName, nameFont).Width;
            TextRenderer.DrawText(g, _info.Release, Theme.Font(Theme.Xs),
                new Point(tx + nameW + 8, 8), Theme.TextFaint);

            string badge = _info.NeedsFirstRun ? "需先启动一次 CAD" : (_info.Installed ? "已安装" : "未安装");
            Color bbg = _info.NeedsFirstRun ? Theme.WarnSoft : (_info.Installed ? Theme.OkBg : Theme.OffBg);
            Color bfg = _info.NeedsFirstRun ? Theme.Warn : (_info.Installed ? Theme.OkText : Theme.TextDim);
            var bfont = Theme.Font(Theme.Xs);
            int bw = TextRenderer.MeasureText(badge, bfont).Width + 12;
            var br = new Rectangle(Width - bw - 10, 6, bw, 16);
            using (var p = Theme.RoundRect(br, 3))
            using (var b = new SolidBrush(bbg)) g.FillPath(b, p);
            TextRenderer.DrawText(g, badge, bfont, new Point(br.X + 6, br.Y + 1), bfg);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_info.NeedsFirstRun) return;
            _info.Checked = !_info.Checked;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    internal enum ButtonKind { Primary, Secondary, Danger }

    internal sealed class FlatButton : Control
    {
        private readonly ButtonKind _kind;
        private bool _hover, _down;
        private readonly string _caption;

        public FlatButton(string text, ButtonKind kind)
        {
            _kind = kind;
            _caption = text;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.DoubleBuffer, true);
            BackColor = Theme.WindowBg;
            Cursor = Cursors.Hand;
            Enabled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, fore, border;
            bool drawBorder = false;

            if (!Enabled)
            {
                fill = Theme.CardBgOff; fore = Theme.TextDisabled; border = Theme.CardBorderOff;
            }
            else if (_kind == ButtonKind.Primary)
            {
                fill = _down ? Theme.AccentLo : (_hover ? Theme.AccentHi : Theme.Accent);
                fore = Theme.AccentText; border = Theme.AccentHi;
            }
            else if (_kind == ButtonKind.Danger)
            {
                bool solid = _down || _hover;
                fill = solid ? Theme.DangerSolid : Theme.DangerSoft;
                fore = solid ? Color.White : Theme.Danger;
                border = Theme.Danger;
                drawBorder = !solid;
            }
            else
            {
                fill = _hover ? Theme.CardBgHover : Theme.CardBg;
                fore = Theme.Text; border = Theme.CardBorder;
                drawBorder = true;
            }

            using (var p = Theme.RoundRect(r, 5))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, p);
                if (drawBorder) using (var pen = new Pen(border)) { g.DrawPath(pen, p); }
            }
            TextRenderer.DrawText(g, _caption, Theme.Font(Theme.Sm, _kind == ButtonKind.Primary ? FontStyle.Bold : FontStyle.Regular),
                new Rectangle(0, 0, Width, Height), fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    }

    internal sealed class LinkText : Control
    {
        private bool _hover;
        private readonly string _caption;

        public LinkText(string text)
        {
            _caption = text;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.DoubleBuffer, true);
            BackColor = Theme.WindowBg;
            Cursor = Cursors.Hand;
            Size = new Size(TextRenderer.MeasureText(text, Theme.Font(Theme.Sm)).Width + 6, 20);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color c = _hover ? Theme.AccentHi : Theme.Link;
            TextRenderer.DrawText(e.Graphics, _caption, Theme.Font(Theme.Sm), new Point(2, 1), c,
                TextFormatFlags.NoPrefix);
            if (_hover)
                using (var p = new Pen(c))
                    e.Graphics.DrawLine(p, 2, Height - 3, Width - 4, Height - 3);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    /// <summary>
    /// 深色自绘确认框，替代系统 MessageBox —— 系统弹窗是浅色的，
    /// 弹出来会跟插件/安装器整套深色皮肤明显打架。
    /// ShowDialog 返回：1 = 主按钮，2 = 次按钮，0 = 取消/关闭。
    /// </summary>
    internal sealed class ConfirmForm : Form
    {
        private int _result;
        private readonly bool _danger;

        public ConfirmForm(string title, string body, string primary, string secondary, string cancel, bool danger)
        {
            _danger = danger;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.DoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.WindowBg;
            ForeColor = Theme.Text;
            Font = Theme.Font(Theme.Sm);
            AutoScaleMode = AutoScaleMode.None;
            Text = title;

            int lines = 1;
            foreach (char c in body ?? "") if (c == '\n') lines++;
            int bodyH = lines * 18 + 8;
            int w = 400, h = 20 + 22 + 8 + bodyH + 16 + 28 + 14;
            ClientSize = new Size(w, h);
            MinimumSize = Size;

            Controls.Add(new Label
            {
                Text = title,
                Location = new Point(32, 18),
                Size = new Size(w - 52, 24),
                BackColor = Color.Transparent,
                ForeColor = Theme.Text,
                Font = Theme.Font(Theme.Md, FontStyle.Bold)
            });
            Controls.Add(new Label
            {
                Text = body,
                Location = new Point(20, 50),
                Size = new Size(w - 40, bodyH),
                BackColor = Color.Transparent,
                ForeColor = Theme.TextDim,
                Font = Theme.Font(Theme.Sm)
            });

            int by = h - 28 - 14;
            int bx = w - 20;
            if (cancel != null) bx = AddButton(cancel, ButtonKind.Secondary, 0, bx, by);
            if (secondary != null) bx = AddButton(secondary, ButtonKind.Danger, 2, bx, by);
            if (primary != null) bx = AddButton(primary, ButtonKind.Primary, 1, bx, by);
        }

        private int AddButton(string text, ButtonKind kind, int result, int right, int top)
        {
            int bw = Math.Max(88, TextRenderer.MeasureText(text, Theme.Font(Theme.Sm)).Width + 26);
            var b = new FlatButton(text, kind)
            {
                Location = new Point(right - bw, top),
                Size = new Size(bw, 28)
            };
            b.Click += delegate { _result = result; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(b);
            return right - bw - 8;
        }

        /// <summary>无 owner（控制面板卸载入口）时用这个读结果。</summary>
        public int Choice { get { return _result; } }

        /// <summary>用返回值而不是 DialogResult，避免三按钮语义被 WinForms 的枚举吃掉。</summary>
        public new int ShowDialog(IWin32Window owner)
        {
            base.ShowDialog(owner);
            return _result;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(Theme.WindowBg);
            using (var b = new SolidBrush(Theme.Divider))
            {
                g.FillRectangle(b, 0, 0, Width, 1);
                g.FillRectangle(b, 0, Height - 28 - 28, Width, 1);
            }
            using (var p = Theme.RoundRect(new Rectangle(20, 21, 4, 16), 2))
            using (var b = new SolidBrush(_danger ? Theme.Danger : Theme.Accent))
                g.FillPath(b, p);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < 34)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
            }
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    }
}
