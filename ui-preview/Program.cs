using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiPreview;

internal static class Program
{
    /// <summary>
    /// 用法：UiPreview.exe &lt;输出.png&gt; [宽] [高] [cc|panel]
    /// 不弹窗口：把窗口内容摘下来放进一个带底色的宿主 Border，离屏 Measure/Arrange 后出图。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "cc-panel.png");
        double w = args.Length > 1 ? double.Parse(args[1]) : 360;
        double h = args.Length > 2 ? double.Parse(args[2]) : 480;
        string which = args.Length > 3 ? args[3] : "cc";
        int colon = which.IndexOf(':');
        if (colon >= 0)
        {
            string mode = which.Substring(colon + 1);
            if (which.StartsWith("vp:", StringComparison.Ordinal))
                VpLayer.Ui.VpPanel.Mode = mode;   // empty / tag / settings
            else if (which.StartsWith("settings:", StringComparison.Ordinal))
                AutoLayer.UI.SettingsWindow.Page = mode;   // 空 / x1 / x2
            else
                AutoLayer.UI.QuickSwitch.LayerSelectionWindow.State = mode;
            which = which.Substring(0, colon);
        }

        Window win;
        if (which == "panel")
            win = new AutoLayer.UI.PanelWindow();
        else if (which == "settings")
            win = new AutoLayer.UI.SettingsWindow();
        else if (which == "vp")
            win = new VpLayer.Ui.VpPanel();
        else
            win = new AutoLayer.UI.QuickSwitch.LayerSelectionWindow();

        var content = (FrameworkElement)win.Content;
        win.Content = null;

        var host = new Border
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x19)),
            Child = content,
        };
        TextElement.SetFontFamily(host, win.FontFamily);
        TextElement.SetFontSize(host, win.FontSize);
        TextElement.SetForeground(host, win.Foreground);

        // 隐式样式（ListBoxItem 等）在运行期按可视树向上找资源，
        // 内容已从窗口摘下来，必须把窗口资源整体搬进宿主，否则会退回系统默认样式
        foreach (System.Collections.DictionaryEntry e in win.Resources)
            host.Resources[e.Key] = e.Value;

        // h = 0 表示「按内容自适应」，模拟窗口的 SizeToContent="Height"
        if (h <= 0)
        {
            host.Height = double.NaN;
            host.Measure(new Size(w, double.PositiveInfinity));
            h = Math.Min(Math.Ceiling(host.DesiredSize.Height), 740);
            host.Height = h;
        }

        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        // 第 5 个参数：超采样倍率（默认 1）。做视频特写要 2 倍，否则放大后字发虚。
        double scale = args.Length > 4 ? double.Parse(args[4]) : 1.0;
        var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(host);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        Console.WriteLine("saved: " + outPath);

        // CC 面板顺带把列表虚拟化的实际取值打出来：改 XAML 后能一眼确认属性生效，
        // 不用靠翻源码判断（BAML 里的附加属性名不是明文，搜 DLL 搜不到）。
        if (win is AutoLayer.UI.QuickSwitch.LayerSelectionWindow cc)
            Console.WriteLine("list: " + cc.ListDiagnostics());
    }
}
