using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AutoLayer.UI;

namespace AutoLayer.UI.QuickSwitch;

/// <summary>预览用的假图层行，字段名与真实 LayerInfo 对齐。</summary>
public class PreviewRow
{
    public string Name { get; set; } = "";
    public bool IsHeader { get; set; }
    public bool IsHighlighted { get; set; }
    public string StatusText { get; set; } = "";
    public string Hex { get; set; } = "#FFFFFF";

    public SolidColorBrush ColorBrush
    {
        get
        {
            var c = (Color)ColorConverter.ConvertFromString(Hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}

public static class Sample
{
    public static List<PreviewRow> Rows = new List<PreviewRow>
    {
        new PreviewRow { Name = "A-墙", Hex = "#FF0000", StatusText = "" },
        new PreviewRow { Name = "A-门窗", Hex = "#00AAFF", StatusText = "已冻结" },
        new PreviewRow { Name = "A-柱", Hex = "#FFFF00", IsHighlighted = true },
        new PreviewRow { Name = "A-楼梯", Hex = "#00FF00", StatusText = "" },
        new PreviewRow { Name = "A-楼板", Hex = "#FF00FF", StatusText = "已锁定" },
        new PreviewRow { Name = "DIM-标注", Hex = "#FFFFFF", StatusText = "已关闭" },
        new PreviewRow { Name = "E-04 造型线", Hex = "#00FFFF", StatusText = "" },
        new PreviewRow { Name = "H-填充", Hex = "#888888", StatusText = "" },
        new PreviewRow { Name = "T-文字", Hex = "#FFAA00", StatusText = "" },
        new PreviewRow { Name = "0", Hex = "#FFFFFF", StatusText = "" },
    };
}

public class HighlightBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return flag ? UiTheme.B(UiTheme.SelectionBg) : Brushes.Transparent;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class HighlightForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return UiTheme.B(flag ? UiTheme.SelectionText : UiTheme.Text);
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
