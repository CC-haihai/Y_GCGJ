using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace YGuiCeng.Setup
{
    /// <summary>
    /// 单文件 exe 的落盘部分：释放内嵌 dll、持久化安装器自身（供控制面板卸载/重跑）、
    /// 迁移 V1.0 配置、完全卸载。
    /// </summary>
    internal static class PluginFiles
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string replacement, uint flags);

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        public static string SelfPath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        public static string PersistentExePath
        {
            get { return Path.Combine(SetupPaths.InstallDir, Path.GetFileName(SelfPath)); }
        }

        public static void EnsureDir()
        {
            if (!Directory.Exists(SetupPaths.InstallDir))
                Directory.CreateDirectory(SetupPaths.InstallDir);
        }

        /// <summary>把内嵌的插件 dll 写出来。CAD 正加载它时会失败，交给上层提示关闭 CAD。</summary>
        public static void ExtractDll()
        {
            EnsureDir();
            RemoveOldVersionedDlls();
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Plugin.dll"))
            {
                if (s == null) throw new InvalidOperationException("安装包内缺少插件文件，可能已损坏。");
                var buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                try
                {
                    File.WriteAllBytes(SetupPaths.DllPath, buf);
                }
                catch (IOException)
                {
                    throw new InvalidOperationException(
                        "插件文件被占用。请先关闭所有 AutoCAD 窗口，再点「应用更改」。");
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        "插件文件被占用或没有写入权限。请先关闭 AutoCAD，再点「应用更改」。");
                }
            }
        }

        /// <summary>
        /// 删掉老版本留下的「带版本号」的 DLL。
        ///
        /// 2.0 时代安装名是 Y_归层工具V2.0.dll，现在固定成 Y_归层工具.dll ——
        /// 不删的话旧文件会一直躺在安装目录里，既占地方，又容易被人误 NETLOAD 到旧版。
        /// 删不掉（CAD 正占用）就算了，下次装的时候再清。
        /// </summary>
        static void RemoveOldVersionedDlls()
        {
            try
            {
                foreach (var f in Directory.GetFiles(SetupPaths.InstallDir, "Y_归层工具V*.dll"))
                {
                    if (string.Equals(Path.GetFileName(f), SetupPaths.DllName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 把安装器自己复制一份到固定目录：用户装完删掉下载的 exe 后，
        /// 控制面板卸载入口和「重跑改勾选」依然可用。
        /// </summary>
        public static string DeploySelf()
        {
            EnsureDir();
            string src = SelfPath, dst = PersistentExePath;
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return dst;
            try { File.Copy(src, dst, true); }
            catch { return src; }
            return dst;
        }

        /// <summary>
        /// 迁移结果。状态栏据此说清「搬没搬、从哪来、到哪去」，
        /// 不再出现只报一句「已迁移」、用户却找不到文件的情况。
        /// </summary>
        internal sealed class MigrateInfo
        {
            public bool Done;           // 本次真的写入了目标
            public bool AlreadyThere;   // 目标本来就有，未覆盖
            public string Source = "";
            public string Target = "";
            public string Problem = ""; // 找到了旧文件但没法用时的原因
            public string Hint = "";    // 搬完了但可能「看起来没生效」的解释
        }

        /// <summary>
        /// 迁移 V1.0 的 AutoLayer.ini。
        ///
        /// 三处与旧实现不同：
        ///   1. 复制后校验文件确实落盘且非空，才允许界面报「已迁移」；
        ///   2. 旧文件可能是 ANSI/GBK（记事本另存过），统一转成 UTF-8 BOM 再写，
        ///      否则 V2.0 按 UTF-8 读出来全是乱码，看起来就像「配置没迁过来」；
        ///   3. 内容必须真有 [配置: ...] 段，随便一个同名 ini 不会顶掉配置。
        /// 目标已存在时一律不覆盖。
        /// </summary>
        public static MigrateInfo MigrateIni()
        {
            var info = new MigrateInfo { Target = SetupPaths.IniPath };

            if (File.Exists(SetupPaths.IniPath))
            {
                info.AlreadyThere = true;
                return info;
            }

            string[] candidates =
            {
                // 有人会把旧 ini 跟插件/安装器放一起
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SetupPaths.IniName),
                // V1.0 的默认落点
                Path.Combine(SetupPaths.LegacyIniDir, SetupPaths.IniName),
                // 个别机器上旧版退到了 LocalAppData
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "AutoLayer", SetupPaths.IniName),
            };

            foreach (var src in candidates)
            {
                try
                {
                    if (!File.Exists(src)) continue;
                    if (string.Equals(src, SetupPaths.IniPath, StringComparison.OrdinalIgnoreCase)) continue;

                    byte[] raw = File.ReadAllBytes(src);
                    if (raw.Length < 20) { info.Problem = "旧文件太小，未迁移（" + src + "）"; continue; }

                    string text = DecodeIni(raw);
                    if (!LooksLikeConfig(text))
                    {
                        info.Problem = "旧文件不是归层工具配置，未迁移（" + src + "）";
                        continue;
                    }

                    EnsureDir();
                    File.WriteAllText(SetupPaths.IniPath, text, new UTF8Encoding(true));

                    if (!File.Exists(SetupPaths.IniPath) ||
                        new FileInfo(SetupPaths.IniPath).Length == 0)
                    {
                        info.Problem = "写入失败，未迁移（" + SetupPaths.IniPath + "）";
                        continue;
                    }

                    info.Done = true;
                    info.Source = src;
                    info.Problem = "";
                    // 旧配置总开关若为「否」，插件装上也不会介入画图 ——
                    // 用户最容易把这种情况当成「配置没迁过来」，这里直接说破。
                    if (!EnabledIn(text))
                        info.Hint = "旧配置的总开关是「否」，插件默认不介入画图；需要时在面板里打开";
                    return info;
                }
                catch (Exception e)
                {
                    info.Problem = "读取旧配置失败：" + e.Message;
                }
            }
            return info;
        }

        /// <summary>UTF-8（含 BOM）优先；解码不了按 GBK 处理，最后退回系统默认编码。</summary>
        private static string DecodeIni(byte[] raw)
        {
            int start = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
            try
            {
                return new UTF8Encoding(false, true).GetString(raw, start, raw.Length - start);
            }
            catch (DecoderFallbackException)
            {
                try { return Encoding.GetEncoding(936).GetString(raw, start, raw.Length - start); }
                catch { return Encoding.Default.GetString(raw, start, raw.Length - start); }
            }
        }

        /// <summary>旧 ini 里「启用」是不是「是」。没写这一行按旧版默认（关）算。</summary>
        private static bool EnabledIn(string text)
        {
            foreach (var raw in (text ?? "").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("启用", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string v = line.Substring(eq + 1).Trim();
                return v == "是" || v == "true" || v == "1" || v == "yes" || v == "开";
            }
            return false;
        }

        /// <summary>必须真的是归层工具的 ini，避免同名文件把配置「迁」成空表。</summary>
        private static bool LooksLikeConfig(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf("[配置", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("[AutoLayer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return text.IndexOf("启用", StringComparison.Ordinal) >= 0 &&
                   text.IndexOf("当前配置", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 完全卸载：删 dll、可选删配置、删自身副本。
        /// 自身 exe 正在运行时无法直接删，用 MoveFileEx 标记重启后删除（无新进程、无黑窗）。
        /// </summary>
        public static string RemoveAll(bool keepConfig)
        {
            var notes = "";
            try
            {
                if (File.Exists(SetupPaths.DllPath)) File.Delete(SetupPaths.DllPath);
            }
            catch { notes += "插件 dll 被占用（AutoCAD 可能还开着），请关闭后手动删除。"; }

            if (!keepConfig)
            {
                try { if (File.Exists(SetupPaths.IniPath)) File.Delete(SetupPaths.IniPath); }
                catch { }
            }

            string self = SelfPath;
            bool isPersistent = string.Equals(self, PersistentExePath, StringComparison.OrdinalIgnoreCase);
            if (isPersistent)
            {
                try { File.Delete(self); }
                catch
                {
                    try { MoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
                    catch { }
                    notes += " 安装器程序将在下次重启后自动删除。";
                }
            }
            else
            {
                try { if (File.Exists(PersistentExePath)) File.Delete(PersistentExePath); }
                catch { }
            }

            try
            {
                if (Directory.Exists(SetupPaths.InstallDir) &&
                    Directory.GetFileSystemEntries(SetupPaths.InstallDir).Length == 0)
                    Directory.Delete(SetupPaths.InstallDir);
            }
            catch { }

            return notes.Trim();
        }
    }
}
