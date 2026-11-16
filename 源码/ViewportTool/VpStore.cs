using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VpLayer
{
    /// <summary>
    /// 配置文件读写。格式和原插件一样是「记事本能看懂、能手改」的纯文本。
    ///
    /// 存放位置：
    ///   优先 DLL 同目录（用户打开插件文件夹就能看见），目录不可写才退到
    ///   %AppData%\VpLayerTool。判断可写的办法是写一个探针文件 ——
    ///   直接调 Directory 权限判断在部分环境上不准。
    /// </summary>
    public static class VpStore
    {
        public const string FileName = "ViewportState.ini";

        static readonly object Gate = new object();
        static VpConfig _current;
        static string _path;

        public static string PluginFolder
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(typeof(VpStore).Assembly.Location);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                    // 标准 Autoloader 结构是 xxx.bundle\Contents\Windows\，往上找到 .bundle 那一层
                    DirectoryInfo d = new DirectoryInfo(dir);
                    for (int i = 0; i < 3 && d != null; i++)
                    {
                        if (d.Name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                            return d.FullName;
                        d = d.Parent;
                    }
                    return dir;
                }
                catch { }
                return null;
            }
        }

        public static string FallbackFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VpLayerTool");
            }
        }

        public static string FilePath
        {
            get
            {
                if (_path != null) return _path;

                string dir = PluginFolder;
                if (dir != null && CanWrite(dir))
                {
                    _path = Path.Combine(dir, FileName);
                    return _path;
                }

                _path = Path.Combine(FallbackFolder, FileName);
                return _path;
            }
        }

        public static string Folder { get { return Path.GetDirectoryName(FilePath); } }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".vplayer_write_test");
                using (FileStream fs = File.Create(probe)) { fs.WriteByte(0); }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static VpConfig Current
        {
            get
            {
                lock (Gate)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        public static void Invalidate()
        {
            lock (Gate) { _current = null; }
        }

        public static VpConfig Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    VpConfig fresh = new VpConfig();
                    Save(fresh);
                    return fresh;
                }

                VpConfig cfg = FromText(File.ReadAllText(path, Encoding.UTF8));
                return cfg ?? new VpConfig();
            }
            catch
            {
                try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
                return new VpConfig();
            }
        }

        /// <summary>写盘。返回 false 表示没写成功，调用方必须提示 —— 静默失败会让用户以为存上了。</summary>
        public static bool Save(VpConfig cfg)
        {
            if (cfg == null) return false;
            lock (Gate)
            {
                try
                {
                    string path = FilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, ToText(cfg), new UTF8Encoding(true));

                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);

                    _current = cfg;
                    return true;
                }
                catch
                {
                    try { if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); } catch { }
                    return false;
                }
            }
        }

        // ------------------------------------------------------------------ 导出 / 导入

        /// <summary>
        /// 导出：把当前视口配置原样复制一份到用户选的位置。
        /// 备份、换电脑、发给同事都是它 —— 和主面板设置窗口里的「导出配置」是同一套做法。
        /// 当前文件还不存在（从未保存过）就先落一份盘再复制。
        /// </summary>
        public static bool ExportTo(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path)) { error = "没有选保存位置"; return false; }
                if (!File.Exists(FilePath)) Save(Current);
                File.Copy(FilePath, path, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 导入：用别的机器 / 备份 / 同事发来的 ini 整体替换当前视口配置
        /// （图框、状态、当前选择、兜底开关一起换）。
        ///
        /// 校验不过、或写盘失败时当前配置一个字节都不动（Save 是原子替换）。
        /// 成功时旧配置先备份成「ViewportState.ini.导入前备份」，想反悔改名回来就行。
        ///
        /// 为什么要额外校验：<see cref="FromText"/> 对随便一段文本也能解析出一份"默认配置"
        /// （认不出的行全跳过），所以必须要求文件里真的有本插件的段落或键 ——
        /// 拿一个不相干的 ini 来导，会在这里被拒，而不是把用户配置悄悄换成空表。
        /// </summary>
        public static bool ImportFrom(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                { error = "找不到选的文件"; return false; }

                if (new FileInfo(path).Length > 1024 * 1024)
                { error = "文件大得不像配置文件（超过 1MB）"; return false; }

                string text = File.ReadAllText(path, Encoding.UTF8);
                if (!LooksLikeVpConfig(text))
                { error = "这不是有效的 Y_视口图层工具 配置文件"; return false; }

                VpConfig cfg = FromText(text);
                if (cfg == null) { error = "配置文件解析失败"; return false; }

                // 先备份再动手；备份失败就别替换 —— 宁可不导入，也不能让用户丢配置
                if (File.Exists(FilePath))
                    File.Copy(FilePath, FilePath + ".导入前备份", true);

                if (!Save(cfg))
                { error = "写配置文件失败（文件被占用？），当前配置未改动"; return false; }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static bool LooksLikeVpConfig(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("[图框:", StringComparison.Ordinal) >= 0
                || text.IndexOf("[状态:", StringComparison.Ordinal) >= 0
                || text.IndexOf("[状态集:", StringComparison.Ordinal) >= 0
                || text.IndexOf("当前图框 =", StringComparison.Ordinal) >= 0
                || text.IndexOf("生成方式 =", StringComparison.Ordinal) >= 0;
        }

        // ------------------------------------------------------------------ 纯文本

        public static string ToText(VpConfig cfg)
        {
            if (cfg == null) return "";

            var sb = new StringBuilder();
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; Y_视口图层工具 V2.1 · 配置文件");
            sb.AppendLine("; 可以直接用记事本改，改完重新执行命令即生效");
            sb.AppendLine(";");
            sb.AppendLine("; 图框：可以存多个，[图框: 名字] 段就是一张图框模板");
            sb.AppendLine(";       名字是插件里的显示名，随便起，跟块名无关");
            sb.AppendLine("; 状态：从调好的视口刷取，按下面的先后顺序逐张图纸套用");
            sb.AppendLine(";   [状态: 名字] 段里的「冻结」写图层名，用分号隔开");
            sb.AppendLine("; 状态集：一个公司 / 项目一套，[状态集: 名字] 段里的状态只在这一套里");
            sb.AppendLine(";   切「状态集」下拉就换一整套；不同公司同名状态互不干扰");
            sb.AppendLine(";   当前状态集 = 上次用的那一套（「默认」= 老配置里那批）");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();

            sb.AppendLine("当前图框 = " + (cfg.FrameIndex + 1));
            sb.AppendLine("生成方式 = " + (cfg.LayoutMode == VpConfig.ModeRow ? "一排" : "单张"));
            sb.AppendLine("出图比例 = " + (cfg.Scale > 0 ? Num(cfg.Scale) : "自动"));
            sb.AppendLine("图框库目录 = " + (cfg.FrameLibDir ?? ""));
            sb.AppendLine("图层兜底 = " + YesNo(cfg.LayerFallback));
            sb.AppendLine("当前状态集 = " + GroupLabel(cfg.ActiveGroupName));

            if (cfg.Frames != null)
            {
                for (int i = 0; i < cfg.Frames.Count; i++)
                {
                    FrameTemplate f = cfg.Frames[i];
                    if (f == null) continue;

                    sb.AppendLine();
                    sb.AppendLine("[图框: " + CleanLabel(f.DisplayName) + "]");
                    sb.AppendLine("是块 = " + YesNo(f.IsBlock));
                    sb.AppendLine("块名 = " + (f.BlockName ?? ""));
                    sb.AppendLine("源图纸 = " + (f.SourceDwg ?? ""));
                    sb.AppendLine("图框库文件 = " + (f.LibFile ?? ""));
                    sb.AppendLine("整图 = " + YesNo(f.WholeFile));
                    sb.AppendLine("内框手动 = " + YesNo(f.InnerManual));
                    sb.AppendLine("宽 = " + Num(f.Width));
                    sb.AppendLine("高 = " + Num(f.Height));
                    sb.AppendLine("缩放X = " + Num(f.ScaleX));
                    sb.AppendLine("缩放Y = " + Num(f.ScaleY));
                    sb.AppendLine("内框左 = " + Num(f.InnerX));
                    sb.AppendLine("内框下 = " + Num(f.InnerY));
                    sb.AppendLine("内框宽 = " + Num(f.InnerWidth));
                    sb.AppendLine("内框高 = " + Num(f.InnerHeight));
                }
            }

            // 状态按状态集分段写。这里必须遍历 Groups —— cfg.States 只是「当前那一集」，
            // 用它写盘会把别的公司那几套悄悄丢掉。
            if (cfg.Groups != null)
            {
                foreach (StateGroup g in cfg.Groups)
                {
                    if (g == null) continue;

                    sb.AppendLine();
                    sb.AppendLine("[状态集: " + CleanLabel(GroupLabel(g.Name)) + "]");

                    if (g.States == null) continue;
                    foreach (VpState st in g.States)
                    {
                        if (st == null) continue;
                        sb.AppendLine();
                        sb.AppendLine("[状态: " + (st.Name ?? "") + "]");
                        sb.AppendLine("冻结 = " + string.Join(" ; ", (st.FrozenLayers ?? new List<string>()).ToArray()));
                        sb.AppendLine("归层配置 = " + (st.Profile ?? ""));
                    }
                }
            }

            return sb.ToString();
        }

        public static VpConfig FromText(string text)
        {
            var cfg = new VpConfig();
            if (string.IsNullOrEmpty(text)) return cfg;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            VpState curState = null;
            FrameTemplate curFrame = null;
            StateGroup curGroup = null;

            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;

                // ---------------- 段头 ----------------
                if (line[0] == '[')
                {
                    int close = line.LastIndexOf(']');
                    string inner = close > 0 ? line.Substring(1, close - 1).Trim() : line.Substring(1).Trim();

                    int colon = inner.IndexOf(':');
                    if (colon < 0) colon = inner.IndexOf('：');

                    string kind = colon >= 0 ? inner.Substring(0, colon).Trim() : "";
                    string name = colon >= 0 ? inner.Substring(colon + 1).Trim() : inner;

                    if (kind == "图框")
                    {
                        var f = new FrameTemplate();
                        f.Label = name;
                        cfg.Frames.Add(f);
                        curFrame = f;
                        curState = null;
                        curGroup = null;
                    }
                    else if (kind == "状态集")
                    {
                        curGroup = GroupOf(cfg, GroupKey(name));
                        curState = null;
                        curFrame = null;
                    }
                    else
                    {
                        // 老格式没有 [状态集:] 段头，那些状态全落进默认组，不用迁移
                        if (curGroup == null) curGroup = GroupOf(cfg, "");

                        var s = new VpState(name.Length == 0 ? "未命名" : name);
                        curGroup.States.Add(s);
                        curState = s;
                        curFrame = null;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                // ---------------- 状态段 ----------------
                if (curState != null)
                {
                    if (key == "冻结")
                    {
                        curState.FrozenLayers.Clear();
                        foreach (string part in val.Split(';'))
                        {
                            string n = part.Trim();
                            if (n.Length > 0 && !curState.Has(n)) curState.FrozenLayers.Add(n);
                        }
                    }
                    else if (key == "归层配置")
                    {
                        curState.Profile = val;
                    }
                    continue;
                }

                // ---------------- 全局键（即使在图框段里也要认，旧格式把「出图比例」写在图框键后面）----------------
                if (key == "图框库目录")
                {
                    cfg.FrameLibDir = val;
                    continue;
                }

                if (key == "图层兜底")
                {
                    // 老配置没有这个键 → 保持默认（开）。
                    // 只有明确写了「关/否/off」才算关，写别的一律当开。
                    bool off = val.IndexOf("关", StringComparison.Ordinal) >= 0
                            || val.IndexOf("否", StringComparison.Ordinal) >= 0
                            || val.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0
                            || val == "0";
                    cfg.LayerFallback = !off;
                    continue;
                }

                if (key == "当前状态集")
                {
                    cfg.ActiveGroupName = GroupKey(val);
                    continue;
                }

                if (key == "当前图框" || key == "出图比例" || key == "生成方式")
                {
                    if (key == "当前图框")
                    {
                        int n = (int)ParseNum(val);
                        cfg.FrameIndex = n > 0 ? n - 1 : 0;
                    }
                    else if (key == "生成方式")
                    {
                        cfg.LayoutMode = val.IndexOf("一排", StringComparison.Ordinal) >= 0
                            ? VpConfig.ModeRow
                            : VpConfig.ModePerSheet;
                    }
                    else
                    {
                        if (val.IndexOf("自动", StringComparison.Ordinal) >= 0) cfg.Scale = 0;
                        else
                        {
                            double s = ParseNum(val);
                            if (s > 0) cfg.Scale = s;
                        }
                    }
                    continue;
                }

                // ---------------- 旧格式（无段头的单图框）----------------
                if (curFrame == null && IsFrameKey(key))
                {
                    var legacy = new FrameTemplate();
                    cfg.Frames.Add(legacy);
                    curFrame = legacy;
                }

                // ---------------- 图框段 ----------------
                if (curFrame != null)
                {
                    ApplyFrameKey(curFrame, key, val);
                    continue;
                }
            }

            if (cfg.FrameIndex < 0 || cfg.FrameIndex >= cfg.Frames.Count) cfg.FrameIndex = 0;

            // 当前状态集指向的组不存在（手改 ini / 只删了段头）→ 退回第一个组
            if (cfg.FindGroup(cfg.ActiveGroupName) == null)
                cfg.ActiveGroupName = cfg.Groups.Count > 0 ? (cfg.Groups[0].Name ?? "") : "";

            return cfg;
        }

        /// <summary>找不到就新建一个状态集。解析时反复用到。</summary>
        static StateGroup GroupOf(VpConfig cfg, string name)
        {
            StateGroup g = cfg.FindGroup(name);
            if (g != null) return g;

            g = new StateGroup(name);
            cfg.Groups.Add(g);
            return g;
        }

        /// <summary>写进 ini 的组名。空组写成「默认」，读的时候再换回空串。</summary>
        static string GroupLabel(string name)
        {
            return string.IsNullOrEmpty(name) ? "默认" : name;
        }

        /// <summary>ini 里的组名 → 内存里的组名。「默认」还原成空串。</summary>
        static string GroupKey(string label)
        {
            string s = (label ?? "").Trim();
            return s == "默认" ? "" : s;
        }

        static bool IsFrameKey(string key)
        {
            switch (key)
            {
                case "是块":
                case "块名":
                case "宽":
                case "高":
                case "缩放X":
                case "缩放Y":
                case "内框左":
                case "内框下":
                case "内框宽":
                case "内框高":
                case "图框名称":
                case "图框是块":
                case "图框块名":
                case "图框宽":
                case "图框高":
                case "图框缩放X":
                case "图框缩放Y":
                    return true;
            }
            return false;
        }

        static void ApplyFrameKey(FrameTemplate f, string key, string val)
        {
            switch (key)
            {
                case "图框名称":
                    if (!string.IsNullOrWhiteSpace(val)) f.Label = val;
                    break;

                case "是块":
                case "图框是块":
                    f.IsBlock = ParseBool(val);
                    break;

                case "块名":
                case "图框块名":
                    f.BlockName = val;
                    break;

                case "源图纸":
                case "图框源图纸":
                    f.SourceDwg = val;
                    break;

                case "图框库文件":
                    f.LibFile = val;
                    break;

                case "整图":
                    f.WholeFile = ParseBool(val);
                    break;

                case "内框手动":
                    f.InnerManual = ParseBool(val);
                    break;

                case "宽":
                case "图框宽":
                    f.Width = ParseNum(val);
                    break;

                case "高":
                case "图框高":
                    f.Height = ParseNum(val);
                    break;

                case "缩放X":
                case "图框缩放X":
                    {
                        double v = ParseNum(val);
                        f.ScaleX = v > 0 ? v : 1.0;
                        break;
                    }

                case "缩放Y":
                case "图框缩放Y":
                    {
                        double v = ParseNum(val);
                        f.ScaleY = v > 0 ? v : 1.0;
                        break;
                    }

                case "内框左": f.InnerX = ParseNum(val); break;
                case "内框下": f.InnerY = ParseNum(val); break;
                case "内框宽": f.InnerWidth = ParseNum(val); break;
                case "内框高": f.InnerHeight = ParseNum(val); break;
            }
        }

        /// <summary>段头里的名字不能带方括号和换行，否则下次读会断在中间。</summary>
        static string CleanLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "图框";

            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '[' || c == ']' || c == '\r' || c == '\n' || c == '=') continue;
                sb.Append(c);
            }

            string r = sb.ToString().Trim();
            return r.Length == 0 ? "图框" : r;
        }

        static string YesNo(bool b) { return b ? "是" : "否"; }

        static bool ParseBool(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            return s == "是" || s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);
        }

        static double ParseNum(string s)
        {
            double v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return v;
            return 0;
        }

        static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
