using System;
using System.Collections.Generic;
using System.IO;

namespace YGuiCeng.Setup
{
    internal sealed class ApplyResult
    {
        public bool Ok = true;
        public string Error = "";
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>
    /// 把界面上的「期望状态」落到注册表与文件。界面和 --selftest 共用这一套逻辑，
    /// 保证测到的就是用户实际跑的。
    /// </summary>
    internal static class ApplyEngine
    {
        public static ApplyResult Apply(List<CadInfo> items, bool fullUninstall, bool keepConfig)
        {
            var r = new ApplyResult();
            try
            {
                if (fullUninstall)
                {
                    foreach (var c in items)
                    {
                        if (c.Installed || RegistryService.IsInstalled(c))
                        {
                            RegistryService.Uninstall(c);
                            r.Lines.Add("已卸载  " + c.DisplayName + "（" + c.Release + "）");
                        }
                    }
                    RegistryService.RemoveUninstallEntry();
                    string note = PluginFiles.RemoveAll(keepConfig);
                    r.Lines.Add(keepConfig
                        ? "配置文件已保留 → " + SetupPaths.IniPath
                        : "配置文件已删除");
                    if (!string.IsNullOrEmpty(note)) r.Lines.Add(note);
                    return r;
                }

                bool needDll = false, anyChecked = false;
                foreach (var c in items)
                {
                    if (!c.Checked) continue;
                    anyChecked = true;
                    if (!c.Installed || !PointsToCurrentDll(c)) needDll = true;
                }

                if (needDll)
                {
                    PluginFiles.ExtractDll();
                    r.Lines.Add("插件文件 → " + SetupPaths.DllPath);
                }

                // 配置迁移不再绑在「首次释放 dll」上：用户手删过 ini、或上次只写了注册表，
                // 再点一次「应用更改」也能补上。目标已有则 MigrateIni 自己跳过，绝不覆盖。
                if (anyChecked)
                {
                    var mig = PluginFiles.MigrateIni();
                    if (mig.Done)
                    {
                        r.Lines.Add("已迁移配置 → " + mig.Target);
                        r.Lines.Add("  来自：" + mig.Source);
                        if (mig.Hint.Length > 0) r.Lines.Add("  提示：" + mig.Hint);
                    }
                    else if (mig.AlreadyThere)
                    {
                        // 常见误会：插件先跑过一次，自动生成了默认 ini，
                        // 于是旧配置「搬不过来」。要用旧配置得在设置里导入。
                        r.Lines.Add("配置已存在（未覆盖）→ " + mig.Target);
                    }
                    else if (mig.Problem.Length > 0)
                    {
                        r.Lines.Add("[!] " + mig.Problem);
                    }
                }

                int installed = 0, removed = 0, kept = 0;
                foreach (var c in items)
                {
                    if (c.NeedsFirstRun) continue; // HKCU 还没有 profile 键，写了也会被 CAD 重建覆盖

                    if (c.Checked)
                    {
                        if (c.Installed && PointsToCurrentDll(c))
                        {
                            kept++;
                            continue;
                        }
                        RegistryService.CleanLegacy(new[] { c });
                        RegistryService.Install(c, SetupPaths.DllPath);
                        r.Lines.Add("已安装  " + c.DisplayName + "（" + c.Release + "）");
                        installed++;
                    }
                    else if (c.Installed)
                    {
                        RegistryService.Uninstall(c);
                        r.Lines.Add("已卸载  " + c.DisplayName + "（" + c.Release + "）");
                        removed++;
                    }
                }

                int total = 0;
                foreach (var c in items)
                {
                    c.Installed = RegistryService.IsInstalled(c);
                    if (c.Installed) total++;
                }

                if (total > 0)
                {
                    string exe = PluginFiles.DeploySelf();
                    RegistryService.WriteUninstallEntry(exe, SetupPaths.InstallDir);
                }
                else
                {
                    RegistryService.RemoveUninstallEntry();
                }

                if (installed == 0 && removed == 0)
                    r.Lines.Add(kept > 0 ? "无需改动，已装版本均为最新" : "没有选中任何 AutoCAD 版本");

                r.Lines.Add("当前共 " + total + " 个版本启用 · 重启 AutoCAD 后生效");
                return r;
            }
            catch (Exception e)
            {
                r.Ok = false;
                r.Error = e.Message;
                return r;
            }
        }

        /// <summary>升级场景：已装但注册表仍指向旧 dll 路径时，需要重写 LOADER。</summary>
        private static bool PointsToCurrentDll(CadInfo c)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(c.AppKeyPath, false))
                {
                    if (k == null) return false;
                    string loader = Convert.ToString(k.GetValue("LOADER", ""));
                    if (!string.Equals(loader, SetupPaths.DllPath, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (!File.Exists(SetupPaths.DllPath)) return false;
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
