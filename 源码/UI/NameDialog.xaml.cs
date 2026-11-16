using System;
using System.Windows;

namespace AutoLayer.UI
{
    /// <summary>
    /// 一个最小的重命名输入框。WPF 没有 InputBox，自己写一个，
    /// 顺便把校验塞在确定按钮上 —— 名字不合法就停在框里，不关窗。
    /// </summary>
    public partial class NameDialog : Window
    {
        readonly Func<string, string> _validate;

        public NameDialog(string label, string initial, Func<string, string> validate)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Apply(this);
            _validate = validate;
            if (!string.IsNullOrEmpty(label)) TxtLabel.Text = label;
            TxtName.Text = initial ?? "";

            Loaded += (s, e) =>
            {
                TxtName.Focus();
                TxtName.SelectAll();
            };
        }

        /// <summary>用户最终输入的名字（已去首尾空格）。</summary>
        public string Value
        {
            get { return (TxtName.Text ?? "").Trim(); }
        }

        void TxtName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (TxtErr == null) return;
            TxtErr.Text = "";
            TxtErr.Visibility = Visibility.Collapsed;
        }

        void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string err = _validate == null ? null : _validate(TxtName.Text);
            if (err != null)
            {
                TxtErr.Text = err;
                TxtErr.Visibility = Visibility.Visible;
                TxtName.Focus();
                TxtName.SelectAll();
                return;
            }
            DialogResult = true;
        }
    }
}
