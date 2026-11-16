using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace YGuiCeng.Setup
{
    /// <summary>
    /// AutoCAD 官方 demand-load 注册（HKCU，免管理员）：
    ///   ...\Applications\YGuiCeng_V2_0
    ///     LOADER    = dll 完整路径 (REG_SZ)
    ///     LOADCTRLS = 14           (REG_DWORD，随 CAD 启动加载)
    ///     MANAGED   = 1            (REG_DWORD，.NET 程序集)
    /// 只增删自己 AppKey 前缀的键，绝不碰别的插件注册。
    /// </summary>
    internal static class RegistryService
    {
        public static bool IsInstalled(CadInfo c)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(c.AppKeyPath, false))
                    return k != null && k.GetValue("LOADER") != null;
            }
            catch { return false; }
        }

        public static void Install(CadInfo c, string dllPath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(c.AppKeyPath))
            {
                if (k == null) throw new InvalidOperationException("无法写入注册表：" + c.AppKeyPath);
                k.SetValue("LOADER", dllPath, RegistryValueKind.String);
                k.SetValue("LOADCTRLS", 14, RegistryValueKind.DWord);
                k.SetValue("MANAGED", 1, RegistryValueKind.DWord);
            }
        }

        public static void Uninstall(CadInfo c)
        {
            try
            {
                using (var apps = Registry.CurrentUser.OpenSubKey(c.ApplicationsKey, true))
                {
                    if (apps == null) return;
                    if (Array.IndexOf(apps.GetSubKeyNames(), SetupPaths.AppKey) >= 0)
                        apps.DeleteSubKeyTree(SetupPaths.AppKey, false);
                }
            }
            catch { }
        }

        /// <summary>升级时清掉旧版本注册，防止 V1.0 / 早期 V2.0 与本版命令冲突。</summary>
        public static int CleanLegacy(IEnumerable<CadInfo> list)
        {
            int n = 0;
            foreach (var c in list)
            {
                try
                {
                    using (var apps = Registry.CurrentUser.OpenSubKey(c.ApplicationsKey, true))
                    {
                        if (apps == null) continue;
                        foreach (var name in apps.GetSubKeyNames())
                        {
                            if (!name.StartsWith("YGuiCeng_", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(name, SetupPaths.AppKey, StringComparison.OrdinalIgnoreCase)) continue;
                            apps.DeleteSubKeyTree(name, false);
                            n++;
                        }
                    }
                }
                catch { }
            }
            return n;
        }

        public static void WriteUninstallEntry(string uninstallerPath, string installDir)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SetupPaths.UninstallKey))
            {
                if (k == null) return;
                k.SetValue("DisplayName", SetupPaths.ProductName, RegistryValueKind.String);
                k.SetValue("DisplayVersion", SetupPaths.VersionText, RegistryValueKind.String);
                k.SetValue("Publisher", "Y_归层工具", RegistryValueKind.String);
                k.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                k.SetValue("DisplayIcon", uninstallerPath, RegistryValueKind.String);
                k.SetValue("UninstallString", "\"" + uninstallerPath + "\" /uninstall", RegistryValueKind.String);
                k.SetValue("ModifyPath", "\"" + uninstallerPath + "\"", RegistryValueKind.String);
                k.SetValue("NoModify", 0, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(SetupPaths.UninstallKey, false);
            }
            catch { }
        }

        public static bool IsCadRunning()
        {
            try { return Process.GetProcessesByName("acad").Length > 0; }
            catch { return false; }
        }

        public static string[] DumpAppKeys(CadInfo c)
        {
            try
            {
                using (var apps = Registry.CurrentUser.OpenSubKey(c.ApplicationsKey, false))
                    return apps == null ? new string[0] : apps.GetSubKeyNames();
            }
            catch { return new string[0]; }
        }
    }
}
