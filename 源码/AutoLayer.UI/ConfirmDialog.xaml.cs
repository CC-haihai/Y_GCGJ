using System;
using System.Windows;

namespace AutoLayer.UI
{
    /// <summary>删除这类不可撤销的操作，统一走这个深色确认框，不用系统的白底 MessageBox。</summary>
    public partial class ConfirmDialog : Window
    {
        ConfirmDialog(string title, string body, string okText)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Apply(this);

            if (!string.IsNullOrEmpty(title)) TxtTitle.Text = title;
            TxtBody.Text = body ?? "";
            if (!string.IsNullOrEmpty(okText)) BtnOk.Content = okText;
        }

        /// <summary>返回 true 表示用户确认。任何异常都按「取消」处理，绝不误删。</summary>
        public static bool Ask(Window owner, string title, string body, string okText)
        {
            try
            {
                var dlg = new ConfirmDialog(title, body, okText);
                try { dlg.Owner = owner; } catch { }
                return dlg.ShowDialog() == true;
            }
            catch { return false; }
        }

        void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
