using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace YGuiCeng.Setup
{
    internal sealed class CadInfo
    {
        public string Release;       // R23.1
        public string Profile;       // ACAD-3001:804
        public string Year = "";     // 2020（从 UPIRELEASE 读，不能靠 R 版本硬推）
        public string Location = ""; // AcadLocation
        public bool Installed;
        public bool NeedsFirstRun;   // 只在 HKLM 找到，HKCU 还没有 profile 键
        public bool Checked;         // 界面上的期望状态

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Year) ? "AutoCAD " + Release : "AutoCAD " + Year; }
        }

        public string SubPath
        {
            get { return Release + @"\" + Profile; }
        }

        public string FullKey
        {
            get { return SetupPaths.CadRoot + @"\" + SubPath; }
        }

        public string ApplicationsKey
        {
            get { return FullKey + @"\Applications"; }
        }

        public string AppKeyPath
        {
            get { return ApplicationsKey + @"\" + SetupPaths.AppKey; }
        }
    }

    /// <summary>
    /// 运行时动态识别本机装了哪些 AutoCAD，不硬编码任何版本或路径。
    ///
    /// 关键事实（真机实测）：HKCU 下每个 CAD 只有一个带语言后缀的 profile 键，
    /// HKLM 下同一版本才有成对的通用键 + profile 键。所以以 HKCU 为准天然不重复，
    /// HKLM 仅用于补 UPIRELEASE（年份）和 AcadLocation（安装路径）。
    /// </summary>
    internal static class CadDetector
    {
        public static List<CadInfo> Detect(out string warning)
        {
            warning = "";
            var result = new List<CadInfo>();
            var meta = ReadHklmMetadata();

            RegistryKey hkcu = null;
            try
            {
                hkcu = Registry.CurrentUser.OpenSubKey(SetupPaths.CadRoot, false);
            }
            catch (Exception e) { warning = "读注册表失败：" + e.Message; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hkcu != null)
            {
                using (hkcu)
                {
                    foreach (var r in SafeNames(hkcu))
                    {
                        if (!IsRelease(r)) continue;
                        using (var rk = hkcu.OpenSubKey(r, false))
                        {
                            if (rk == null) continue;
                            // 同一 R 下若同时存在通用键与 profile 键，只取带语言后缀的那个
                            var names = new List<string>(SafeNames(rk));
                            names.Sort(CompareProfile);
                            foreach (var a in names)
                            {
                                if (!IsAcad(a)) continue;
                                string group = r + @"\" + BaseName(a);
                                if (!seen.Add(group)) continue;
                                result.Add(Build(r, a, meta));
                            }
                        }
                    }
                }
            }

            // 装了 CAD 但从未启动过：HKCU 还没有 profile 键，从 HKLM 补显示出来
            foreach (var kv in meta)
            {
                int bar = kv.Key.IndexOf('\\');
                if (bar <= 0) continue;
                string r = kv.Key.Substring(0, bar);
                string a = kv.Key.Substring(bar + 1);
                string group = r + @"\" + BaseName(a);
                if (seen.Add(group))
                {
                    var c = Build(r, a, meta);
                    c.NeedsFirstRun = true;
                    result.Add(c);
                }
            }

            result.Sort(delegate(CadInfo x, CadInfo y)
            {
                int n = string.Compare(x.Release, y.Release, StringComparison.OrdinalIgnoreCase);
                return n != 0 ? n : string.Compare(x.Profile, y.Profile, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var c in result)
            {
                c.Installed = RegistryService.IsInstalled(c);
                c.Checked = c.Installed;
            }
            return result;
        }

        private static CadInfo Build(string release, string profile, Dictionary<string, string[]> meta)
        {
            var c = new CadInfo { Release = release, Profile = profile };
            string[] m;
            if (meta.TryGetValue(release + @"\" + profile, out m))
            {
                c.Year = m[0] ?? "";
                c.Location = m[1] ?? "";
            }
            else if (meta.TryGetValue(release + @"\" + BaseName(profile), out m))
            {
                c.Year = m[0] ?? "";
                c.Location = m[1] ?? "";
            }
            return c;
        }

        private static Dictionary<string, string[]> ReadHklmMetadata()
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = root.OpenSubKey(SetupPaths.LmCadRoot, false))
                    {
                        if (k == null) continue;
                        foreach (var r in SafeNames(k))
                        {
                            if (!IsRelease(r)) continue;
                            using (var rk = k.OpenSubKey(r, false))
                            {
                                if (rk == null) continue;
                                foreach (var a in SafeNames(rk))
                                {
                                    // 只取带语言后缀的键：年份 / 安装路径都在它上面
                                    if (!IsAcad(a) || a.IndexOf(':') < 0) continue;
                                    using (var ak = rk.OpenSubKey(a, false))
                                    {
                                        if (ak == null) continue;
                                        string year = Convert.ToString(ak.GetValue("UPIRELEASE", ""));
                                        string loc = Convert.ToString(ak.GetValue("AcadLocation", ""));
                                        map[r + @"\" + a] = new[] { year, loc };
                                    }
                                }
                            }
                        }
                    }
                    if (map.Count > 0) break;
                }
                catch { }
            }
            return map;
        }

        private static IEnumerable<string> SafeNames(RegistryKey k)
        {
            try { return k.GetSubKeyNames(); }
            catch { return new string[0]; }
        }

        private static bool IsRelease(string s)
        {
            return s.Length > 1 && s[0] == 'R' && char.IsDigit(s[1]);
        }

        private static bool IsAcad(string s)
        {
            return s.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase);
        }

        private static string BaseName(string a)
        {
            int i = a.IndexOf(':');
            return i < 0 ? a : a.Substring(0, i);
        }

        /// <summary>带 :804 之类的 profile 键排在通用键前面，配合 seen 去重。</summary>
        private static int CompareProfile(string x, string y)
        {
            bool px = x.IndexOf(':') >= 0, py = y.IndexOf(':') >= 0;
            if (px != py) return px ? -1 : 1;
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}
