using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace YGuiCeng.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                string a = args.Length > 0 ? args[0].ToLowerInvariant() : "";

                if (a == "--selftest")
                {
                    SelfTest.Run();
                    return;
                }
                if (a == "--probe")
                {
                    RunProbe();
                    return;
                }
                if (a.StartsWith("--demo="))
                {
                    RunDemo(a.Substring(7));
                    return;
                }
                if (a == "/uninstall" || a == "--uninstall")
                {
                    RunUninstall();
                    return;
                }

                bool created;
                using (var mutex = new Mutex(true, @"Local\YGuiCeng_Setup_V2_0", out created))
                {
                    if (!created)
                    {
                        using (var d = new ConfirmForm(SetupPaths.ProductName,
                            "安装器已经在运行了，请切到那个窗口操作。", "好", null, null, false))
                            d.ShowDialog();
                        return;
                    }
                    string warning;
                    var items = CadDetector.Detect(out warning);
                    Application.Run(new MainForm(items, warning));
                    GC.KeepAlive(mutex);
                }
            }
            catch (Exception e)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "安装器错误.txt"),
                        e.ToString(), Encoding.UTF8);
                }
                catch { }
                MessageBox.Show("安装器遇到问题：\n\n" + e.Message, SetupPaths.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 多版本布局预览：--demo=12 造 12 个假版本，只跑界面，不碰注册表和文件。
        /// 用来确认版本多时列表滚动、卡片不被滚动条压住。
        /// </summary>
        private static void RunDemo(string number)
        {
            int n;
            if (!int.TryParse(number, out n) || n < 1 || n > 40) n = 12;
            var demo = new List<CadInfo>();
            for (int i = 0; i < n; i++)
            {
                demo.Add(new CadInfo
                {
                    Release = "R" + (20 + i) + ".0",
                    Profile = "ACAD-" + (3001 + i) + ":804",
                    Year = (2018 + i).ToString(),
                    Location = @"C:\CAD\" + (2018 + i),
                    Installed = i % 3 == 0,
                    Checked = i % 3 == 0
                });
            }
            Application.Run(new MainForm(demo, "") { DemoMode = true });
        }

        /// <summary>控制面板 / 应用和功能 调用的卸载入口。</summary>
        private static void RunUninstall()
        {
            string warning;
            var items = CadDetector.Detect(out warning);
            int ans;
            using (var d = new ConfirmForm("卸载 " + SetupPaths.ProductName,
                "确定要从所有 AutoCAD 版本卸载吗？\n\n" +
                "保留配置 = 以后重装继续用；连配置一起删 = 完全清除。",
                "保留配置并卸载", "连配置一起删", "取消", true))
            {
                d.ShowDialog();
                ans = d.Choice;
            }
            if (ans == 0 || ans < 0) return;

            var r = ApplyEngine.Apply(items, true, ans == 1);
            using (var d = new ConfirmForm(r.Ok ? "卸载完成" : "卸载失败",
                r.Ok ? "已从所有 AutoCAD 版本卸载。" : r.Error,
                "好", null, null, !r.Ok))
                d.ShowDialog();
        }

        /// <summary>无界面诊断：列出识别到的 CAD 和内嵌资源，写到 exe 同目录。</summary>
        private static void RunProbe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("time : " + DateTime.Now.ToString("s"));
            sb.AppendLine("clr  : " + Environment.Version);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var n in asm.GetManifestResourceNames())
            {
                using (var s = asm.GetManifestResourceStream(n))
                    sb.AppendLine("res  : " + n + " bytes=" + (s == null ? -1 : s.Length));
            }
            string warning;
            var items = CadDetector.Detect(out warning);
            sb.AppendLine("warn : " + warning);
            sb.AppendLine("count: " + items.Count);
            foreach (var c in items)
                sb.AppendLine(string.Format("  {0,-8} {1,-18} year={2,-6} inst={3,-5} first={4,-5} {5}",
                    c.Release, c.Profile, c.Year, c.Installed, c.NeedsFirstRun, c.Location));
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-result.txt"),
                              sb.ToString(), Encoding.UTF8);
        }
    }
}
