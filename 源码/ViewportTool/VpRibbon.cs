using System;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace VpLayer
{
    /// <summary>
    /// 功能区选项卡「视口」。有 Ribbon 才建，经典工作空间（没有 Ribbon）静默跳过。
    ///
    /// 这样用户连命令都不用记：点选项卡里的按钮就打开面板。
    /// 面板里的所有操作仍然是转成命令执行 —— 非模态窗口里点选图元会失败，
    /// 这是 AutoCAD 的硬限制，绕不过去。
    /// </summary>
    public static class VpRibbon
    {
        const string TabId = "VPT_RIBBON_TAB";

        /// <summary>建选项卡。返回 true 表示已就绪（新建成功或之前就建过）。</summary>
        public static bool TryCreate()
        {
            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon == null) return false;   // 功能区还没起来 / 经典界面

                foreach (RibbonTab t in ribbon.Tabs)
                    if (string.Equals(t.Id, TabId, StringComparison.Ordinal)) return true;

                var tab = new RibbonTab { Title = "视口", Id = TabId };

                var source = new RibbonPanelSource { Title = "视口图层" };
                var panel = new RibbonPanel { Source = source };

                source.Items.Add(MakeButton("视口图层工具", "VPT ", true));
                source.Items.Add(MakeButton("刷取视口", "VLCAP ", false));
                source.Items.Add(MakeButton("批量生成", "VLBATCH ", false));
                source.Items.Add(MakeButton("标记视口", "VLTAG ", false));

                tab.Panels.Add(panel);
                ribbon.Tabs.Add(tab);
                return true;
            }
            catch
            {
                // 不同版本的 Ribbon 细节有差异，失败就当没有功能区，不影响命令使用
                return true;
            }
        }

        static RibbonButton MakeButton(string text, string command, bool large)
        {
            var btn = new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = false,
                Size = large ? RibbonItemSize.Large : RibbonItemSize.Standard,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandHandler = new RelayCommand(),
                CommandParameter = command
            };
            return btn;
        }

        /// <summary>Ribbon 按钮的点击处理：把命令字符串发回 CAD 执行。</summary>
        sealed class RelayCommand : ICommand
        {
            // Ribbon 不会用到这个事件，用空实现避免"事件从未使用"的编译警告
            public event EventHandler CanExecuteChanged { add { } remove { } }

            public bool CanExecute(object parameter) { return true; }

            public void Execute(object parameter)
            {
                string cmd = parameter as string;
                if (string.IsNullOrEmpty(cmd)) return;

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                // 前面两个 Esc：把上一个没走完的命令取消掉，免得新命令的字符被它的提示吃掉
                try { doc.SendStringToExecute("\x03\x03" + cmd, true, false, false); }
                catch { }
            }
        }
    }
}
