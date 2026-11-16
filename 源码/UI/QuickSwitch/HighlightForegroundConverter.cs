using System;
using System.Globalization;
using System.Windows.Data;
using AutoLayer.UI;

namespace AutoLayer.UI.QuickSwitch;

/// <summary>
/// 图层名文字色：高亮行 SelectionText #FFFFFF，普通行 Text #F1F5F9。
/// 深/浅主题分支已随 ◐ 一起删除（§7.3）——全插件单一深色，不再有 IsDark 依赖。
/// </summary>
public class HighlightForegroundConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool flag = value is bool b && b;
		return UiTheme.B(flag ? UiTheme.SelectionText : UiTheme.Text);
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
