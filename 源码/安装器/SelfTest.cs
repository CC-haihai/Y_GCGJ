using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace YGuiCeng.Setup
{
    /// <summary>
    /// 四场景回归，全部跑在隔离注册表树 + 临时目录里，不碰真实 CAD 注册。
    /// 调用方式：Y_归层工具V2.1_安装器.exe --selftest
    /// 结果写到 exe 同目录 selftest-result.txt，最后一行 RESULT: PASS / FAIL。
    /// </summary>
    internal static class SelfTest
    {
        private static readonly List<string> Log = new List<string>();
        private static int _fail;

        public static void Run()
        {
            SetupPaths.UseTestHive();
            Reset();

            var items = new List<CadInfo>
            {
                new CadInfo { Release = "R23.1", Profile = "ACAD-3001:804", Year = "2020", Location = @"C:\CAD\2020" },
                new CadInfo { Release = "R24.0", Profile = "ACAD-4101:804", Year = "2021", Location = @"C:\CAD\2021" },
                new CadInfo { Release = "R26.0", Profile = "ACAD-A101:804", Year = "2027", Location = @"D:\CAD\2027" },
            };

            // ---- 场景 1：选择性安装，只勾 1 和 3 ----
            items[0].Checked = true; items[1].Checked = false; items[2].Checked = true;
            var r1 = ApplyEngine.Apply(items, false, true);
            Check("场景1 执行成功", r1.Ok, r1.Error);
            Check("场景1 第1个已注册", RegistryService.IsInstalled(items[0]));
            Check("场景1 第2个未动", !RegistryService.IsInstalled(items[1]));
            Check("场景1 第3个已注册（十六进制编号）", RegistryService.IsInstalled(items[2]));
            Check("场景1 LOADER 路径", Read(items[0], "LOADER") == SetupPaths.DllPath);
            Check("场景1 LOADCTRLS=14", Convert.ToInt32(Read(items[0], "LOADCTRLS")) == 14);
            Check("场景1 MANAGED=1", Convert.ToInt32(Read(items[0], "MANAGED")) == 1);
            Check("场景1 dll 已释放", File.Exists(SetupPaths.DllPath));
            Check("场景1 卸载项已写", UninstallExists());

            // ---- 场景 2：全装 + 重复应用幂等 ----
            foreach (var c in items) c.Checked = true;
            var r2 = ApplyEngine.Apply(items, false, true);
            Check("场景2 执行成功", r2.Ok, r2.Error);
            Check("场景2 三个全注册", RegistryService.IsInstalled(items[0]) &&
                                     RegistryService.IsInstalled(items[1]) &&
                                     RegistryService.IsInstalled(items[2]));
            var r2b = ApplyEngine.Apply(items, false, true);
            Check("场景2 重复应用幂等", r2b.Ok && r2b.Lines.Exists(x => x.Contains("无需改动")), r2b.Error);

            // ---- 场景 3：部分卸载，只取消第 1 个，文件应保留 ----
            items[0].Checked = false;
            var r3 = ApplyEngine.Apply(items, false, true);
            Check("场景3 执行成功", r3.Ok, r3.Error);
            Check("场景3 只卸第1个", !RegistryService.IsInstalled(items[0]));
            Check("场景3 第2/3个保留", RegistryService.IsInstalled(items[1]) &&
                                      RegistryService.IsInstalled(items[2]));
            Check("场景3 插件文件保留", File.Exists(SetupPaths.DllPath));
            Check("场景3 卸载项仍在", UninstallExists());

            // ---- 场景 4：完全卸载，连配置一起删 ----
            File.WriteAllText(SetupPaths.IniPath, "[AutoLayer]\ntest=1\n");
            foreach (var c in items) c.Checked = false;
            var r4 = ApplyEngine.Apply(items, true, false);
            Check("场景4 执行成功", r4.Ok, r4.Error);
            Check("场景4 三个全清", !RegistryService.IsInstalled(items[0]) &&
                                   !RegistryService.IsInstalled(items[1]) &&
                                   !RegistryService.IsInstalled(items[2]));
            Check("场景4 卸载项已删", !UninstallExists());
            Check("场景4 dll 已删", !File.Exists(SetupPaths.DllPath));
            Check("场景4 ini 已删", !File.Exists(SetupPaths.IniPath));

            // ---- 场景 5：配置迁移（全部走隔离目录，不碰用户真实的 V1.0 ini）----
            string legacyDir = SetupPaths.LegacyIniDir;
            Directory.CreateDirectory(legacyDir);
            string legacy = Path.Combine(legacyDir, SetupPaths.IniName);

            File.WriteAllText(legacy, "启用 = 是\n当前配置 = 默认配置\n[配置: 我的配置]\n线段 = E-04 造型线\n",
                              new UTF8Encoding(true));
            var m1 = PluginFiles.MigrateIni();
            Check("场景5 UTF-8 旧配置已迁移", m1.Done, m1.Problem);
            Check("场景5 目标文件确实落盘", File.Exists(SetupPaths.IniPath));
            Check("场景5 目标含配置段", File.Exists(SetupPaths.IniPath) &&
                   File.ReadAllText(SetupPaths.IniPath, Encoding.UTF8).Contains("[配置: 我的配置]"));
            Check("场景5 总开关为「是」不误报", m1.Hint.Length == 0, m1.Hint);

            var m2 = PluginFiles.MigrateIni();
            Check("场景5 已有配置不覆盖", m2.AlreadyThere && !m2.Done);

            File.Delete(SetupPaths.IniPath);
            File.WriteAllBytes(legacy, Encoding.GetEncoding(936).GetBytes(
                "启用 = 是\n当前配置 = 默认配置\n[配置: 中文配置]\n线段 = 轴线\n"));
            var m3 = PluginFiles.MigrateIni();
            Check("场景5 GBK 旧配置转 UTF-8", m3.Done &&
                  File.ReadAllText(SetupPaths.IniPath, Encoding.UTF8).Contains("中文配置"), m3.Problem);

            File.Delete(SetupPaths.IniPath);
            File.WriteAllText(legacy, "启用 = 否\n当前配置 = 默认配置\n[配置: 旧配置]\n线段 = 轴线\n",
                              new UTF8Encoding(true));
            var m5 = PluginFiles.MigrateIni();
            Check("场景5 总开关为「否」给出提示", m5.Done && m5.Hint.Contains("总开关"), m5.Hint);

            File.Delete(SetupPaths.IniPath);
            File.WriteAllText(legacy, "this is not a config file at all", Encoding.UTF8);
            var m4 = PluginFiles.MigrateIni();
            Check("场景5 无效文件不迁移", !m4.Done && !File.Exists(SetupPaths.IniPath));

            try { Directory.Delete(legacyDir, true); } catch { }

            Reset();
            Log.Add("RESULT: " + (_fail == 0 ? "PASS" : "FAIL (" + _fail + ")"));
            try
            {
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-result.txt"),
                    string.Join(Environment.NewLine, Log.ToArray()), Encoding.UTF8);
            }
            catch { }
        }

        private static void Check(string name, bool ok, string extra = "")
        {
            if (!ok) _fail++;
            Log.Add((ok ? "PASS  " : "FAIL  ") + name + (ok || string.IsNullOrEmpty(extra) ? "" : "  -> " + extra));
        }

        private static string Read(CadInfo c, string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(c.AppKeyPath, false))
                    return k == null ? null : Convert.ToString(k.GetValue(value, null));
            }
            catch { return null; }
        }

        private static bool UninstallExists()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SetupPaths.UninstallKey, false))
                    return k != null;
            }
            catch { return false; }
        }

        private static void Reset()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\YGCExeTest", false); }
            catch { }
            try
            {
                if (Directory.Exists(SetupPaths.InstallDir))
                    Directory.Delete(SetupPaths.InstallDir, true);
            }
            catch { }
        }
    }
}
