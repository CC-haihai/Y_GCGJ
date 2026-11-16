using System;
using System.IO;

namespace YGuiCeng.Setup
{
    /// <summary>
    /// 所有外部路径集中在这里，测试时可整体切到隔离 hive，绝不碰真实 CAD 注册。
    /// </summary>
    internal static class SetupPaths
    {
        /// <summary>
        /// 注册表里的应用键。**故意不带版本号** ——
        /// 升级时装到同一份键、同一份文件，机器上永远只有一套，
        /// 不会堆出 YGuiCeng_V2_0 / YGuiCeng_V2_1 两份互相抢命令。
        /// </summary>
        public const string AppKey = "YGuiCeng";

        /// <summary>安装后的 DLL 文件名。同样不带版本号：升级就是替换这个文件。</summary>
        public const string DllName = "Y_归层工具.dll";

        public const string IniName = "AutoLayer.ini";

        /// <summary>显示名，如「Y_归层工具 V2.1」。版本从嵌入的 DLL 里读，安装器不用跟着改。</summary>
        public static string ProductName { get { return "Y_归层工具 V" + ShortVersion; } }

        /// <summary>四段版本号，写进卸载项的 DisplayVersion。</summary>
        public static string VersionText { get { return EmbeddedVersion; } }

        /// <summary>两段版本号（2.1），界面显示用。</summary>
        public static string ShortVersion
        {
            get
            {
                string v = EmbeddedVersion;
                string[] p = v.Split('.');
                return p.Length >= 2 ? p[0] + "." + p[1] : v;
            }
        }

        static string _version;

        /// <summary>
        /// 嵌入的插件 DLL 的文件版本。读不到就退回兜底值 ——
        /// 安装器不能因为读版本失败就整个打不开。
        /// </summary>
        static string EmbeddedVersion
        {
            get
            {
                if (_version != null) return _version;
                _version = ReadEmbeddedVersion();
                if (string.IsNullOrEmpty(_version)) _version = "2.1.0";
                return _version;
            }
        }

        static string ReadEmbeddedVersion()
        {
            string tmp = null;
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("Plugin.dll"))
                {
                    if (s == null) return null;

                    tmp = Path.Combine(Path.GetTempPath(),
                        "ygc_ver_" + Guid.NewGuid().ToString("N") + ".dll");

                    using (var f = File.Create(tmp)) s.CopyTo(f);

                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(tmp);
                    return string.IsNullOrEmpty(vi.FileVersion) ? vi.ProductVersion : vi.FileVersion;
                }
            }
            catch { return null; }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static string CadRoot = @"Software\Autodesk\AutoCAD";
        public static string LmCadRoot = @"SOFTWARE\Autodesk\AutoCAD";
        public static string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        public static string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YGuiCeng");

        /// <summary>
        /// V1.0 配置所在目录。单独拎出来是为了 --selftest 能重定向，
        /// 测试时绝不读、更不碰用户真实的旧配置。
        /// </summary>
        public static string LegacyIniDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoLayer");

        public static string DllPath
        {
            get { return Path.Combine(InstallDir, DllName); }
        }

        public static string IniPath
        {
            get { return Path.Combine(InstallDir, IniName); }
        }

        public static string UninstallKey
        {
            get { return UninstallRoot + @"\" + AppKey; }
        }

        /// <summary>自检模式：CAD 注册、卸载项、文件目录全部重定向到隔离区。</summary>
        public static void UseTestHive()
        {
            CadRoot = @"Software\YGCExeTest\AutoCAD";
            UninstallRoot = @"Software\YGCExeTest\Uninstall";
            InstallDir = Path.Combine(Path.GetTempPath(), "YGCExeTestFiles");
            LegacyIniDir = Path.Combine(Path.GetTempPath(), "YGCExeTestLegacy");
        }
    }
}
