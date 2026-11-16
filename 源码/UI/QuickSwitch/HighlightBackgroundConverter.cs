using System;
using System.Globalization;
using System.Windows.Data;
using AutoLayer.UI;

namespace AutoLayer.UI.QuickSwitch;

/// <summary>
/// 图纸里正在高亮的那一行 —— 底色走 UiTheme.SelectionBg（§7.1：与"选中"同语义，
/// 不允许再出现第二个蓝底，原来的 #0078D4 实心蓝已废止）。
/// </summary>
public class HighlightBackgroundConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool flag = value is bool b && b;
		return flag ? UiTheme.B(UiTheme.SelectionBg) : System.Windows.Media.Brushes.Transparent;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
