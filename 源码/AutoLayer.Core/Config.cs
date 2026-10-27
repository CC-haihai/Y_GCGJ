using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AutoLayer.Core
{
    /// <summary>
    /// 图层颜色的紧凑存法：一个 int 同时表达「没记录 / ACI 索引 / TrueColor」。
    /// <list type="bullet">
    /// <item>0 = 没记录（新建图层时退回全局默认色）</item>
    /// <item>1..255 = ACI 索引</item>
    /// <item>0x1000000 起 = TrueColor，低 24 位是 RRGGBB</item>
    /// </list>
    /// 用 0 当哨兵，是因为反序列化时字段初始化器不保证执行，
    /// 缺字段的旧 JSON 会自然落成 0，正好等于"没记录"，不会凭空造出黑层。
    /// </summary>
    public static class LayerColorCode
    {
        public const int Unset = 0;
        const int RgbFlag = 0x1000000;

        public static int FromAci(short index)
        {
            return (index >= 1 && index <= 255) ? index : Unset;
        }

        public static int FromRgb(byte r, byte g, byte b)
        {
            return RgbFlag | (r << 16) | (g << 8) | b;
        }

        public static bool IsAci(int v) { return v >= 1 && v <= 255; }
        public static bool IsRgb(int v) { return v >= RgbFlag; }

        public static byte R(int v) { return (byte)((v >> 16) & 0xFF); }
        public static byte G(int v) { return (byte)((v >> 8) & 0xFF); }
        public static byte B(int v) { return (byte)(v & 0xFF); }
    }

    /// <summary>
    /// 池子里的一个图层。颜色只在"这个图层不存在、需要自动新建"时才用得上。
    /// </summary>
    /// <summary>
    /// 快捷键的存法：一个 int 存修饰键（位标志），一个 int 存虚拟键码。
    ///
    /// 为什么不用字符串当唯一来源：键盘钩子每按一个键都要比对，
    /// 字符串得先解析一遍；而且中文输入法下"+"这种字符容易被写成全角。
    /// 字符串只出现在 ini 和界面上，解析/格式化都走这里，两边永远一致。
    /// </summary>
    public static class Hotkey
    {
        public const int Alt = 1;
        public const int Ctrl = 2;
        public const int Shift = 4;
        public const int Win = 8;

        /// <summary>什么都没配（= 快捷键关掉）。</summary>
        public const int None = 0;

        /// <summary>格式化成「Ctrl+Shift+Q」。没配返回空串。</summary>
        public static string Text(int mods, int key)
        {
            if (key <= 0) return "";
            var parts = new List<string>();
            if ((mods & Ctrl) != 0) parts.Add("Ctrl");
            if ((mods & Alt) != 0) parts.Add("Alt");
            if ((mods & Shift) != 0) parts.Add("Shift");
            if ((mods & Win) != 0) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join("+", parts.ToArray());
        }

        /// <summary>
        /// 从 ini 里那行读回来。认不出就返回 false，调用方保留原值 ——
        /// 手改出笔误时应该是"快捷键没生效"，而不是"整个配置炸掉"。
        /// </summary>
        public static bool Parse(string s, out int mods, out int key)
        {
            mods = None;
            key = 0;

            string t = (s ?? "").Trim();
            if (t.Length == 0) return false;
            if (t == "无" || t == "-" || string.Equals(t, "none", StringComparison.OrdinalIgnoreCase))
                return true;      // 认得出是"没配"

            // 全角加号也认，中文输入法下很容易打成 ＋
            t = t.Replace('＋', '+').Replace(" ", "");

            string[] seg = t.Split('+');
            for (int i = 0; i < seg.Length; i++)
            {
                string p = seg[i].Trim();
                if (p.Length == 0) continue;

                if (Same(p, "ctrl") || Same(p, "control")) { mods |= Ctrl; continue; }
                if (Same(p, "alt")) { mods |= Alt; continue; }
                if (Same(p, "shift")) { mods |= Shift; continue; }
                if (Same(p, "win") || Same(p, "cmd")) { mods |= Win; continue; }

                int vk = KeyFromName(p);
                if (vk <= 0) return false;     // 有一段认不出来，整条作废
                key = vk;
            }

            // 只有修饰键没有主键 = 没配完，别当成有效快捷键
            return key > 0;
        }

        static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>虚拟键码 → 显示名。只覆盖会有人用的那一批，认不出的写 VKxx。</summary>
        public static string KeyName(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();          // A~Z
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();          // 0~9
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);              // F1~F24
            if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);          // 小键盘 0~9

            switch (vk)
            {
                case 0x20: return "Space";
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0xC0: return "`";
                case 0xBD: return "-";
                case 0xBB: return "=";
                case 0xDB: return "[";
                case 0xDD: return "]";
                case 0xDC: return "\\";
                case 0xBA: return ";";
                case 0xDE: return "'";
                case 0xBC: return ",";
                case 0xBE: return ".";
                case 0xBF: return "/";
            }
            return "VK" + vk;
        }

        static int KeyFromName(string name)
        {
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z') return c;
                if (c >= '0' && c <= '9') return c;
            }

            if ((name[0] == 'F' || name[0] == 'f') && name.Length <= 3)
            {
                int n;
                if (int.TryParse(name.Substring(1), out n) && n >= 1 && n <= 24) return 0x6F + n;
            }

            switch (name.ToUpperInvariant())
            {
                case "SPACE": return 0x20;
                case "BACKSPACE": return 0x08;
                case "TAB": return 0x09;
                case "ENTER": return 0x0D;
                case "ESC": return 0x1B;
                case "INSERT": return 0x2D;
                case "DELETE": return 0x2E;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "`": return 0xC0;
                case "-": return 0xBD;
                case "=": return 0xBB;
                case "[": return 0xDB;
                case "]": return 0xDD;
                case "\\": return 0xDC;
                case ";": return 0xBA;
                case "'": return 0xDE;
                case ",": return 0xBC;
                case ".": return 0xBE;
                case "/": return 0xBF;
            }
            return 0;
        }
    }

    [DataContract]
    public sealed class LayerRef
    {
        [DataMember(Name = "name", Order = 0)] public string Name = "";

        /// <summary>
        /// 配这个图层时从图纸里记下来的颜色（见 <see cref="LayerColorCode"/>）。
        /// 只在这个图层**不存在**、需要自动新建时才用得上 ——
        /// 这样在新图纸里也能建出和当初配置时一模一样的颜色。
        /// </summary>
        [DataMember(Name = "color", Order = 1, EmitDefaultValue = false)] public int ColorValue = LayerColorCode.Unset;

        public LayerRef() { }
        public LayerRef(string name) { Name = name ?? ""; }
        public LayerRef(string name, int colorValue) { Name = name ?? ""; ColorValue = colorValue; }
    }

    /// <summary>
    /// 一个类别 → 一组图层（池）。
    /// 平时只用 <see cref="Active"/> 指的那一个；池里有多个时，圆盘/面板可以切。
    /// </summary>
    [DataContract]
    public sealed class Rule
    {
        [DataMember(Name = "category", Order = 0)] public string Category = "";

        [DataMember(Name = "layers", Order = 1)] public List<LayerRef> Layers = new List<LayerRef>();

        /// <summary>池里当前用第几个。切了就记住，下次还用它（粘滞）。</summary>
        [DataMember(Name = "active", Order = 2)] public int Active = 0;

        /// <summary>当前用的图层名。池空或越界时返回空串。</summary>
        public string Layer
        {
            get
            {
                if (Layers == null || Layers.Count == 0) return "";
                int i = ClampActive();
                return Layers[i].Name ?? "";
            }
        }

        public int ClampActive()
        {
            if (Layers == null || Layers.Count == 0) return 0;
            if (Active < 0) return 0;
            if (Active >= Layers.Count) return Layers.Count - 1;
            return Active;
        }

        /// <summary>去掉空名字的层，并让 Active 仍然指向同一个层。</summary>
        public void Tidy()
        {
            if (Layers == null) { Layers = new List<LayerRef>(); Active = 0; return; }

            string keep = Layer;
            var clean = new List<LayerRef>();
            foreach (LayerRef l in Layers)
            {
                if (l == null) continue;
                string n = (l.Name ?? "").Trim();
                if (n.Length == 0) continue;
                clean.Add(new LayerRef(n, l.ColorValue));
            }
            Layers = clean;

            int idx = 0;
            for (int i = 0; i < clean.Count; i++)
                if (string.Equals(clean[i].Name, keep, StringComparison.Ordinal)) { idx = i; break; }
            Active = idx;
        }
    }


    [DataContract]
    public sealed class CommandOverride
    {
        [DataMember(Name = "command", Order = 0)] public string Command = "";
        [DataMember(Name = "layer", Order = 1)] public string Layer = "";
    }

    [DataContract]
    public sealed class Profile
    {
        [DataMember(Name = "name", Order = 0)] public string Name = "";
        [DataMember(Name = "rules", Order = 1)] public List<Rule> Rules = new List<Rule>();
        [DataMember(Name = "overrides", Order = 2)] public List<CommandOverride> Overrides = new List<CommandOverride>();

        public Rule FindRule(string category)
        {
            if (Rules == null) return null;
            foreach (Rule r in Rules)
                if (r != null && string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
                    return r;
            return null;
        }

        Rule EnsureRule(string category)
        {
            if (Rules == null) Rules = new List<Rule>();
            Rule r = FindRule(category);
            if (r == null)
            {
                r = new Rule { Category = category, Layers = new List<LayerRef>() };
                Rules.Add(r);
            }
            if (r.Layers == null) r.Layers = new List<LayerRef>();
            return r;
        }

        /// <summary>某个类别配的所有图层（池）。永远不返回 null。</summary>
        public List<LayerRef> LayersFor(string category)
        {
            Rule r = FindRule(category);
            if (r == null || r.Layers == null) return new List<LayerRef>();
            return r.Layers;
        }

        /// <summary>某个类别配了几个图层。0 表示这一类不归层。</summary>
        public int LayerCount(string category)
        {
            return LayersFor(category).Count;
        }

        /// <summary>当前用第几个（从 0 开始）。</summary>
        public int ActiveIndexOf(string category)
        {
            Rule r = FindRule(category);
            return r == null ? 0 : r.ClampActive();
        }

        /// <summary>切到池里第几个。越界或没配这一类时返回 false。</summary>
        public bool SetActiveIndex(string category, int index)
        {
            Rule r = FindRule(category);
            if (r == null || r.Layers == null || r.Layers.Count == 0) return false;
            if (index < 0 || index >= r.Layers.Count) return false;
            r.Active = index;
            return true;
        }

        /// <summary>整个替换成一组图层。</summary>
        public void SetLayers(string category, IEnumerable<string> names)
        {
            Rule r = EnsureRule(category);
            string keep = r.Layer;
            var list = new List<LayerRef>();
            if (names != null)
                foreach (string n in names)
                {
                    string t = (n ?? "").Trim();
                    if (t.Length == 0) continue;
                    list.Add(new LayerRef(t));
                }
            r.Layers = list;

            int idx = 0;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Name, keep, StringComparison.Ordinal)) { idx = i; break; }
            r.Active = idx;
        }

        /// <summary>整个替换成一组图层（带颜色）。</summary>
        public void SetLayerRefs(string category, List<LayerRef> list)
        {
            Rule r = EnsureRule(category);
            string keep = r.Layer;
            r.Layers = list ?? new List<LayerRef>();

            int idx = 0;
            for (int i = 0; i < r.Layers.Count; i++)
                if (string.Equals(r.Layers[i].Name, keep, StringComparison.Ordinal)) { idx = i; break; }
            r.Active = idx;
        }

        /// <summary>当前用的图层名（池里 Active 指的那个）。</summary>
        public string LayerFor(string category)
        {
            Rule r = FindRule(category);
            return r == null ? "" : r.Layer;
        }

        /// <summary>
        /// 单层快捷设置：把池换成只含这一个图层。
        /// 名字没变时保留原来记住的颜色（避免"点一下就把颜色弄丢"）。
        /// </summary>
        public void SetLayer(string category, string layer)
        {
            string n = (layer ?? "").Trim();
            Rule r = FindRule(category);

            // 名字没变：保留原来记住的颜色，什么都不做
            if (r != null && r.Layers != null && r.Layers.Count == 1 &&
                string.Equals(r.Layers[0].Name, n, StringComparison.Ordinal))
            {
                r.Active = 0;
                return;
            }

            Rule e = EnsureRule(category);
            e.Layers = new List<LayerRef> { new LayerRef(n) };
            e.Active = 0;
        }

        /// <summary>记住当前这个图层的颜色（配配置时从图纸里读出来的）。</summary>
        public void SetLayerColor(string category, int colorValue)
        {
            Rule r = FindRule(category);
            if (r == null || r.Layers == null || r.Layers.Count == 0) return;
            r.Layers[r.ClampActive()].ColorValue = colorValue;
        }

        /// <summary>取当前图层记住的颜色；没记录返回 <see cref="LayerColorCode.Unset"/>。</summary>
        public int LayerColorFor(string category)
        {
            Rule r = FindRule(category);
            if (r == null || r.Layers == null || r.Layers.Count == 0) return LayerColorCode.Unset;
            return r.Layers[r.ClampActive()].ColorValue;
        }

        /// <summary>
        /// 池里第 <paramref name="index"/> 个图层记住的颜色。
        /// 1.6 收尾把颜色从"类别一格"改成"跟着图层走"（面板上每个 chip 一个色块），
        /// 所以按序号取色这条得有 —— 按名字取会被重名/改名坑到。
        /// </summary>
        public int LayerColorAt(string category, int index)
        {
            Rule r = FindRule(category);
            if (r == null || r.Layers == null || index < 0 || index >= r.Layers.Count)
                return LayerColorCode.Unset;
            return r.Layers[index].ColorValue;
        }

        /// <summary>改池里第 <paramref name="index"/> 个图层的颜色。</summary>
        public void SetLayerColorAt(string category, int index, int colorValue)
        {
            Rule r = FindRule(category);
            if (r == null || r.Layers == null || index < 0 || index >= r.Layers.Count) return;
            r.Layers[index].ColorValue = colorValue;
        }

        public void RemoveRule(string category)
        {
            if (Rules == null) return;
            Rules.RemoveAll(r => r != null &&
                string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase));
        }
    }

    [DataContract]
    public sealed class Settings
    {
        /// <summary>命令结束后把当前图层还原成原来的，避免"被悄悄改掉"。</summary>
        [DataMember(Name = "restoreClayer", Order = 0)] public bool RestoreClayer = true;
        /// <summary>目标图层不存在时按配置自动新建。</summary>
        [DataMember(Name = "autoCreateLayer", Order = 1)] public bool AutoCreateLayer = true;
        /// <summary>目标图层被冻结时自动解冻（实测：不解冻则切层会被 CAD 静默忽略）。</summary>
        [DataMember(Name = "autoThaw", Order = 2)] public bool AutoThaw = true;
        /// <summary>目标图层被关闭时自动打开。</summary>
        [DataMember(Name = "autoTurnOn", Order = 3)] public bool AutoTurnOn = true;
        /// <summary>自动新建图层时使用的颜色索引。</summary>
        [DataMember(Name = "newLayerColorIndex", Order = 4)] public short NewLayerColorIndex = 7;
        /// <summary>目标图层被锁定时自动解锁（锁定层能切过去，但后续编辑会被挡住）。</summary>
        [DataMember(Name = "autoUnlock", Order = 5)] public bool AutoUnlock = true;

        /// <summary>右键长按弹圆盘。</summary>
        [DataMember(Name = "pieEnabled", Order = 7)] public bool PieEnabled = true;

        /// <summary>
        /// 右键按住多久才出圆盘。
        /// 1.6 收尾：250 用户还嫌慢，压到 **180**。再短就开始跟"顺手右键"打架了。
        /// </summary>
        [DataMember(Name = "pieHoldMs", Order = 8)] public int PieHoldMs = 180;

        /// <summary>
        /// 用什么键弹圆盘：
        /// <list type="bullet">
        /// <item>0 = 右键长按（默认）</item>
        /// <item>1 = 鼠标侧键「后退」（X1），按下即弹，松手确认</item>
        /// <item>2 = 鼠标侧键「前进」（X2），同上</item>
        /// </list>
        /// 为什么没有中键：CAD 的中键按下就开始平移，圆盘弹出来的同时图纸会跟着跑，
        /// 而吞掉中键抬起又会让平移卡住收不了尾。侧键和右键都不跟 CAD 抢东西。
        /// 彻底关掉用 <see cref="PieEnabled"/> = false。
        ///
        /// 1.10 曾经有过第 4 档「任一侧键」（= 3），1.12 删掉了：它跟设置窗口里
        /// 「关闭」的哨兵值撞在同一个 3 上，选了反而把圆盘关掉。老 ini 里写着
        /// 任一侧键 / 3 的，读的时候会退回 0（右键长按）。
        /// </summary>
        [DataMember(Name = "pieButton", Order = 9)] public int PieButton = 0;

        /// <summary>
        /// 弹面板的快捷键（修饰键，见 <see cref="Core.Hotkey"/>）。0 表示没配。
        /// 键盘钩子每次按键都要读它，所以别在钩子里现解析字符串。
        /// </summary>
        [DataMember(Name = "hotkeyMods", Order = 10)] public int HotkeyMods = Hotkey.Ctrl | Hotkey.Shift;

        /// <summary>弹面板的快捷键（虚拟键码）。0 = 不启用快捷键，只能用 QH 命令。</summary>
        [DataMember(Name = "hotkeyKey", Order = 11)] public int HotkeyKey = 0x51;   // Q

        /// <summary>
        /// 自定义的 CAD 命令名（留空 = 不用）。
        ///
        /// AutoCAD 的 [CommandMethod] 是编译期写死的，运行期没法注册新命令名 ——
        /// 所以这条走 LISP：启动时发一句
        /// <c>(defun c:GH () (command "QH") (princ))</c>，
        /// 用户在命令行敲 GH 就等于敲 QH。见 <see cref="CommandAliasHost"/>。
        /// </summary>
        [DataMember(Name = "commandAlias", Order = 12)] public string CommandAlias = "";
    }

    [DataContract]
    public sealed class Config
    {
        [DataMember(Name = "version", Order = 0)] public int Version = 1;
        /// <summary>总开关。默认关闭：装上不启用就完全不介入画图。</summary>
        [DataMember(Name = "enabled", Order = 1)] public bool Enabled = false;
        [DataMember(Name = "activeProfile", Order = 2)] public string ActiveProfile = "";
        [DataMember(Name = "settings", Order = 3)] public Settings Settings = new Settings();
        [DataMember(Name = "profiles", Order = 4)] public List<Profile> Profiles = new List<Profile>();

        public Profile FindProfile(string name)
        {
            if (Profiles == null || string.IsNullOrEmpty(name)) return null;
            foreach (Profile p in Profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        public Profile Active
        {
            get { return FindProfile(ActiveProfile); }
        }

        /// <summary>配置名最长多少字。太长会把下拉框撑爆，也会让提示行挤成一团。</summary>
        public const int MaxNameLength = 40;

        /// <summary>
        /// 校验一个配置名能不能用。返回 null 表示可用，否则返回给用户看的原因。
        /// selfName 是"正在改名的那个配置"，比较重名时要跳过它自己。
        /// </summary>
        public string ValidateName(string name, string selfName)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return "名称不能为空";
            if (n.Length > MaxNameLength) return "名称最多 " + MaxNameLength + " 个字";

            if (Profiles != null)
            {
                foreach (Profile p in Profiles)
                {
                    if (p == null) continue;
                    if (selfName != null && string.Equals(p.Name, selfName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return "已有同名配置「" + p.Name + "」，换个名字";
                }
            }
            return null;
        }

        /// <summary>改名。名字非法或重名时返回 false，且不做任何改动。</summary>
        public bool Rename(string oldName, string newName)
        {
            Profile p = FindProfile(oldName);
            if (p == null) return false;
            if (ValidateName(newName, oldName) != null) return false;

            string n = newName.Trim();
            p.Name = n;
            if (string.Equals(ActiveProfile, oldName, StringComparison.OrdinalIgnoreCase))
                ActiveProfile = n;
            return true;
        }

        /// <summary>取一个没被占用的默认名，比如「新配置 3」。</summary>
        public string NextDefaultName()
        {
            int i = 1;
            string name;
            do { name = "新配置 " + i++; } while (FindProfile(name) != null);
            return name;
        }

        /// <summary>按基础名生成一个不重名的名字，比如「住宅标准 副本 2」。</summary>
        public string UniqueName(string baseName)
        {
            string b = (baseName ?? "").Trim();
            if (b.Length == 0) b = "新配置";
            string name = b;
            int i = 2;
            while (FindProfile(name) != null) name = b + " " + i++;
            return name;
        }

        /// <summary>复制一个配置（含图层规则与单命令覆盖），并设为当前配置。</summary>
        public Profile DuplicateProfile(string sourceName)
        {
            Profile src = FindProfile(sourceName);
            if (src == null) return null;

            var copy = new Profile { Name = UniqueName(src.Name + " 副本") };
            if (src.Rules != null)
                foreach (Rule r in src.Rules)
                {
                    if (r == null) continue;
                    var nr = new Rule { Category = r.Category, Active = r.Active };
                    if (r.Layers != null)
                        foreach (LayerRef l in r.Layers)
                            if (l != null) nr.Layers.Add(new LayerRef(l.Name, l.ColorValue));
                    copy.Rules.Add(nr);
                }
            if (src.Overrides != null)
                foreach (CommandOverride o in src.Overrides)
                    copy.Overrides.Add(new CommandOverride { Command = o.Command, Layer = o.Layer });

            Profiles.Add(copy);
            ActiveProfile = copy.Name;
            return copy;
        }

        /// <summary>
        /// 删除配置。至少保留一个，所以只剩一个时返回 false。
        /// 删掉的正好是当前配置时，自动切到相邻的那个。
        /// </summary>
        /// <summary>
        /// 把某个配置挪到指定位置（拖拽排序用：拖到第几条就落到第几条）。
        ///
        /// 只改**顺序**，不动 ActiveProfile —— 挪完还在用的还是同一个配置，
        /// 变的是它出现在面板下拉框和状态条清单里的位置。
        /// 返回 false = 没挪（名字不存在、位置越界、本来就在那儿）。
        /// </summary>
        public bool MoveProfileTo(string name, int index)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (Profiles == null || Profiles.Count < 2) return false;
            if (index < 0 || index >= Profiles.Count) return false;

            int i = IndexOfProfile(name);
            if (i < 0 || i == index) return false;

            Profile moving = Profiles[i];
            Profiles.RemoveAt(i);
            Profiles.Insert(index, moving);
            return true;
        }

        /// <summary>
        /// 把某个配置在列表里往上 / 往下挪一格（delta = -1 上移、+1 下移）。
        /// 返回 false = 没挪（到顶/到底、名字不存在、只有一个配置）。
        /// </summary>
        public bool MoveProfile(string name, int delta)
        {
            if (string.IsNullOrEmpty(name) || delta == 0) return false;
            if (Profiles == null || Profiles.Count < 2) return false;

            int i = IndexOfProfile(name);
            if (i < 0) return false;

            int j = i + delta;
            if (j < 0 || j >= Profiles.Count) return false;
            return MoveProfileTo(name, j);
        }

        /// <summary>配置在列表里的位置；找不到返回 -1。</summary>
        public int IndexOfProfile(string name)
        {
            if (string.IsNullOrEmpty(name) || Profiles == null) return -1;
            for (int k = 0; k < Profiles.Count; k++)
            {
                Profile p = Profiles[k];
                if (p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return k;
            }
            return -1;
        }

        public bool RemoveProfile(string name)
        {
            Profile p = FindProfile(name);
            if (p == null) return false;
            if (Profiles.Count <= 1) return false;

            int idx = Profiles.IndexOf(p);
            Profiles.RemoveAt(idx);

            if (string.Equals(ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
            {
                int next = idx >= Profiles.Count ? Profiles.Count - 1 : idx;
                ActiveProfile = Profiles[next].Name;
            }
            return true;
        }
    }

    /// <summary>配置文件读写。任何异常都退回安全默认值（关闭状态），绝不让坏配置影响画图。</summary>
    public static class ConfigStore
    {
        static Config _current;
        static readonly object Gate = new object();

        /// <summary>配置文件名。纯文本，可以直接用记事本打开改。</summary>
        public const string FileName = "AutoLayer.ini";

        static string _path;

        /// <summary>
        /// 配置存放目录。标准 Autoloader 结构是 <c>xxx.bundle\Contents\Windows\</c>，
        /// 这里往上找到 <c>.bundle</c> 那一层 —— 用户打开插件文件夹就能看见配置文件，
        /// 不用往里翻三层。不是标准结构时就放在 DLL 旁边。
        /// </summary>
        public static string PluginFolder
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(typeof(ConfigStore).Assembly.Location);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

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

        /// <summary>插件目录写不进去时（例如装在 Program Files）退到这里。</summary>
        public static string FallbackFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AutoLayer");
            }
        }

        /// <summary>配置文件完整路径：优先插件同目录，目录不可写才退到 AppData。</summary>
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

        /// <summary>配置文件所在目录。</summary>
        public static string Folder
        {
            get { return Path.GetDirectoryName(FilePath); }
        }

        /// <summary>试写一个探针文件，判断目录是否可写（插件装在 Program Files 时不可写）。</summary>
        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".autolayer_write_test");
                using (FileStream fs = File.Create(probe)) { fs.WriteByte(0); }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static Config Current
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

        /// <summary>只替换内存中的当前配置，不落盘。供测试与"临时试用"使用。</summary>
        public static void SetCurrent(Config cfg)
        {
            lock (Gate) { _current = cfg; }
        }

        public static Config Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    // 首次运行：先把默认配置写出来，用户才找得到这个文件
                    Config fresh = CreateDefault();
                    Save(fresh);
                    return fresh;
                }

                Config cfg = FromText(File.ReadAllText(path, Encoding.UTF8));
                if (cfg == null) return CreateDefault();
                Normalize(cfg);
                return cfg;
            }
            catch
            {
                try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
                return CreateDefault();
            }
        }

        /// <summary>
        /// 写盘。返回 false 表示没写成功（磁盘满、无权限、文件被占用）。
        /// 调用方必须检查 —— 静默失败会让用户以为保存了。
        /// </summary>
        public static bool Save(Config cfg)
        {
            if (cfg == null) return false;
            Normalize(cfg);
            lock (Gate)
            {
                try
                {
                    string path = FilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    string tmp = path + ".tmp";
                    // UTF-8 带 BOM：记事本（包括老版本）能正确认出中文
                    File.WriteAllText(tmp, ToText(cfg), new UTF8Encoding(true));

                    // 原子替换：中途失败也不会把原配置弄丢
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

        // ------------------------------------------------------------------ 切配置

        /// <summary>
        /// 切到指定配置（状态条上点配置名，在弹出的清单里选一条就是调它）。
        ///
        /// 返回 true = 真换了并已存盘。
        /// 名字不存在、或本来就是这个配置 → 返回 false，不写盘（避免无意义的保存）。
        ///
        /// 1.13 一开始做的是"点一下轮换到下一个"（`CycleProfile`），
        /// 用户当场否掉了：「不是点击轮换，而是点击展开我可以选择哪一个配置」——
        /// 配置一多，轮换要点好几次才知道自己转到哪了，直接选才是对的。
        /// </summary>
        public static bool SwitchProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            Config cfg = Current;
            if (cfg == null || cfg.Profiles == null) return false;

            Profile p = cfg.FindProfile(name);
            if (p == null || string.IsNullOrEmpty(p.Name)) return false;
            if (string.Equals(p.Name, cfg.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return false;

            cfg.ActiveProfile = p.Name;
            Save(cfg);
            LayerEvents.Raise();
            return true;
        }

        /// <summary>
        /// 当前所有配置的名字（给状态条那份清单用）。列表里不会有空名字。
        /// </summary>
        public static List<string> ProfileNames()
        {
            var names = new List<string>();
            Config cfg = Current;
            if (cfg == null || cfg.Profiles == null) return names;
            foreach (Profile p in cfg.Profiles)
                if (p != null && !string.IsNullOrEmpty(p.Name)) names.Add(p.Name);
            return names;
        }

        // ------------------------------------------------------------------ 纯文本格式

        /// <summary>
        /// 把配置写成纯文本。格式故意做得能用记事本直接看懂、改得动：
        /// 顶层是一行行「设置 = 值」，每个配置是一个 [配置: 名字] 段，
        /// 段里按类别写「图层名 | 颜色号」，颜色号省略就表示"没记录"。
        /// </summary>
        public static string ToText(Config cfg)
        {
            if (cfg == null) return "";

            var sb = new StringBuilder();
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; Y_归层工具 · 配置文件");
            sb.AppendLine("; 直接用记事本改就行，改完重启 AutoCAD 生效");
            sb.AppendLine(";");
            sb.AppendLine("; 顶层设置（是 / 否）：");
            sb.AppendLine(";   启用              总开关");
            sb.AppendLine(";   当前配置          用哪一套");
            sb.AppendLine(";   命令后还原图层    画完把当前图层还原");
            sb.AppendLine(";   自动新建图层      目标图层不存在时自动建");
            sb.AppendLine(";   自动解冻 / 自动打开图层 / 自动解锁");
            sb.AppendLine(";   新建图层默认颜色  1~255，没记录颜色的图层用它");
            sb.AppendLine(";   圆盘              是 / 否（长按出圆盘）");
            sb.AppendLine(";   圆盘按键          右键 / 侧键后退 / 侧键前进（用哪个键弹）");
            sb.AppendLine(";   圆盘长按毫秒      100~1000，默认 180");
            sb.AppendLine(";   面板快捷键        例如 Ctrl+Shift+Q；写「无」= 只留 QH 命令");
            sb.AppendLine(";   面板命令          自定义一个 CAD 命令名，敲它等于敲 QH；写「无」= 不用");
            sb.AppendLine(";");
            sb.AppendLine("; 每段 [配置: 名字] 下面按类别写：  图层名 | 颜色号");
            sb.AppendLine(";   颜色号可省略（只写图层名）");
            sb.AppendLine(";   图层名留空 = 这一类不归层");
            sb.AppendLine(";   真彩色写成 rgb(12,200,240)");
            sb.AppendLine(";");
            sb.AppendLine("; 一类要配多个图层，用分号隔开，先写的排前面：");
            sb.AppendLine(";   线段 = E-04 造型线 | 3 ; E-05 辅助线 ; E-01 结构");
            sb.AppendLine("; 配了多个时，下面会多一行记「上次用的是第几个」（从 1 开始）：");
            sb.AppendLine(";   当前层.线段 = 2");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();

            sb.AppendLine("启用 = " + YesNo(cfg.Enabled));
            sb.AppendLine("当前配置 = " + (cfg.ActiveProfile ?? ""));

            Settings st = cfg.Settings ?? new Settings();
            sb.AppendLine("命令后还原图层 = " + YesNo(st.RestoreClayer));
            sb.AppendLine("自动新建图层 = " + YesNo(st.AutoCreateLayer));
            sb.AppendLine("自动解冻 = " + YesNo(st.AutoThaw));
            sb.AppendLine("自动打开图层 = " + YesNo(st.AutoTurnOn));
            sb.AppendLine("自动解锁 = " + YesNo(st.AutoUnlock));
            sb.AppendLine("新建图层默认颜色 = " + st.NewLayerColorIndex);
            sb.AppendLine("圆盘 = " + YesNo(st.PieEnabled));
            sb.AppendLine("圆盘按键 = " + (st.PieButton == 1 ? "侧键后退"
                : st.PieButton == 2 ? "侧键前进" : "右键"));
            sb.AppendLine("圆盘长按毫秒 = " + st.PieHoldMs);
            sb.AppendLine("面板快捷键 = " + (st.HotkeyKey > 0 ? Hotkey.Text(st.HotkeyMods, st.HotkeyKey) : "无"));
            sb.AppendLine("面板命令 = " + (string.IsNullOrEmpty(st.CommandAlias) ? "无" : st.CommandAlias));

            if (cfg.Profiles != null)
            {
                foreach (Profile p in cfg.Profiles)
                {
                    if (p == null) continue;
                    sb.AppendLine();
                    sb.AppendLine("[配置: " + (p.Name ?? "") + "]");
                    foreach (string cat in Categories.All)
                    {
                        sb.AppendLine(cat + " = " + RuleText(p, cat));
                        int n = p.LayerCount(cat);
                        if (n > 1 && p.ActiveIndexOf(cat) > 0)
                            sb.AppendLine("当前层." + cat + " = " + (p.ActiveIndexOf(cat) + 1));
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>池里多个图层用「 ; 」隔开，每个后面可以跟「 | 颜色号」。</summary>
        static string RuleText(Profile p, string cat)
        {
            List<LayerRef> list = p.LayersFor(cat);
            if (list.Count == 0) return "";

            var parts = new List<string>();
            foreach (LayerRef l in list)
            {
                string name = l == null ? "" : (l.Name ?? "");
                string color = ColorText(l == null ? LayerColorCode.Unset : l.ColorValue);
                parts.Add(color.Length == 0 ? name : name + " | " + color);
            }
            return string.Join(" ; ", parts.ToArray());
        }

        static string ColorText(int colorValue)
        {
            if (LayerColorCode.IsRgb(colorValue))
                return "rgb(" + LayerColorCode.R(colorValue) + ","
                     + LayerColorCode.G(colorValue) + ","
                     + LayerColorCode.B(colorValue) + ")";
            if (LayerColorCode.IsAci(colorValue)) return colorValue.ToString();
            return "";
        }

        static string YesNo(bool b) { return b ? "是" : "否"; }

        /// <summary>
        /// 解析纯文本配置。看不懂的行一律跳过，绝不抛异常 ——
        /// 用户手改出笔误时应该"少几条规则"，而不是让整个插件罢工。
        /// </summary>
        public static Config FromText(string text)
        {
            var cfg = new Config { Settings = new Settings() };
            if (string.IsNullOrEmpty(text)) return cfg;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Profile cur = null;

            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;   // 注释

                if (line[0] == '[')
                {
                    int close = line.LastIndexOf(']');
                    string inner = close > 0 ? line.Substring(1, close - 1).Trim() : line.Substring(1).Trim();

                    int colon = inner.IndexOf(':');
                    if (colon < 0) colon = inner.IndexOf('：');
                    string name = colon >= 0 ? inner.Substring(colon + 1).Trim() : inner;
                    if (name.Length == 0) name = "未命名";

                    cur = new Profile { Name = name };
                    cfg.Profiles.Add(cur);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;   // 没有等号，认不出来，跳过

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (cur == null) ApplySetting(cfg, key, val);
                else ApplyRule(cur, key, val);
            }

            Normalize(cfg);
            return cfg;
        }

        static void ApplySetting(Config cfg, string key, string val)
        {
            Settings st = cfg.Settings;
            switch (key)
            {
                case "启用": cfg.Enabled = ParseBool(val, cfg.Enabled); break;
                case "当前配置": cfg.ActiveProfile = val; break;
                case "命令后还原图层": st.RestoreClayer = ParseBool(val, st.RestoreClayer); break;
                case "自动新建图层": st.AutoCreateLayer = ParseBool(val, st.AutoCreateLayer); break;
                case "自动解冻": st.AutoThaw = ParseBool(val, st.AutoThaw); break;
                case "自动打开图层": st.AutoTurnOn = ParseBool(val, st.AutoTurnOn); break;
                case "自动解锁": st.AutoUnlock = ParseBool(val, st.AutoUnlock); break;
                case "圆盘": st.PieEnabled = ParseBool(val, st.PieEnabled); break;
                case "圆盘按键":
                    {
                        string v = (val ?? "").Trim();
                        if (v.Length > 0)
                        {
                            if (v.IndexOf("后退", StringComparison.Ordinal) >= 0 || v.IndexOf("X1", StringComparison.OrdinalIgnoreCase) >= 0)
                                st.PieButton = 1;
                            else if (v.IndexOf("前进", StringComparison.Ordinal) >= 0 || v.IndexOf("X2", StringComparison.OrdinalIgnoreCase) >= 0)
                                st.PieButton = 2;
                            // 老 ini 里可能写着「任一侧键」/「任意侧键」/「侧键」（1.10 有过的第 4 档，
                            // 1.12 删了）。这里不给它找替身，直接落回右键长按 ——
                            // 悄悄改成某一个侧键，用户按了会发现"我没配这个键怎么也能弹"。
                            else
                                st.PieButton = 0;      // 右键 / 任一侧键 / 其它看不懂的，都按右键
                        }
                        break;
                    }
                case "面板快捷键":
                    {
                        int hkMods, hkKey;
                        // 解析失败（手改笔误）就保留原来的，别把快捷键清成"无"
                        if (Hotkey.Parse(val, out hkMods, out hkKey))
                        {
                            st.HotkeyMods = hkMods;
                            st.HotkeyKey = hkKey;
                        }
                        break;
                    }
                case "面板命令":
                    {
                        string v = (val ?? "").Trim();
                        string why;
                        if (v.Length == 0 || v == "无" || v == "-") st.CommandAlias = "";
                        else if (CommandAliasHost.IsValidName(v, out why)) st.CommandAlias = v.ToUpperInvariant();
                        // 名字不合法就留着原来的，别把用户配好的命令清掉
                        break;
                    }
                case "圆盘长按毫秒":
                    {
                        int ms;
                        // 下限跟设置窗口的滑块一致（100）。以前这里是 120，
                        // 滑块拖到最左边存成 100，下次读回来又被抬到 120 —— 数值会自己跳。
                        if (int.TryParse(val, out ms))
                            st.PieHoldMs = ms < 100 ? 100 : (ms > 1000 ? 1000 : ms);
                        break;
                    }
                case "新建图层默认颜色":
                    {
                        short idx;
                        if (short.TryParse(val, out idx) && idx >= 1 && idx <= 255)
                            st.NewLayerColorIndex = idx;
                        break;
                    }
            }
        }

        /// <summary>
        /// 解析一行「类别 = ...」。支持池：
        /// <c>E-04 造型线 | 3 ; E-05 辅助线</c>（分号隔开，每段可带 | 颜色号）。
        /// </summary>
        static void ApplyRule(Profile p, string cat, string val)
        {
            // 「当前层.线段 = 2」记的是池里用第几个（1 开始）
            const string ActivePrefix = "当前层.";
            if (cat.StartsWith(ActivePrefix, StringComparison.Ordinal))
            {
                string c = cat.Substring(ActivePrefix.Length).Trim();
                int n;
                if (int.TryParse(val, out n) && n >= 1) p.SetActiveIndex(c, n - 1);
                return;
            }

            var list = new List<LayerRef>();
            var colors = new List<int>();

            string[] chunks = (val ?? "").Split(';');
            foreach (string chunk in chunks)
            {
                string seg = chunk.Trim();
                if (seg.Length == 0) continue;

                string name = seg;
                int colorValue = LayerColorCode.Unset;

                int bar = seg.IndexOf('|');
                if (bar >= 0)
                {
                    name = seg.Substring(0, bar).Trim();
                    colorValue = ParseColor(seg.Substring(bar + 1).Trim());
                }
                if (name.Length == 0) continue;

                list.Add(new LayerRef(name, colorValue));
            }

            if (list.Count == 0)
            {
                p.SetLayers(cat, null);
                return;
            }
            p.SetLayerRefs(cat, list);
        }

        static int ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return LayerColorCode.Unset;
            string t = s.Trim();

            if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                int l = t.IndexOf('(');
                int r = t.LastIndexOf(')');
                if (l > 0 && r > l)
                {
                    string[] parts = t.Substring(l + 1, r - l - 1).Split(',');
                    if (parts.Length == 3)
                    {
                        byte rr, gg, bb;
                        if (byte.TryParse(parts[0].Trim(), out rr) &&
                            byte.TryParse(parts[1].Trim(), out gg) &&
                            byte.TryParse(parts[2].Trim(), out bb))
                            return LayerColorCode.FromRgb(rr, gg, bb);
                    }
                }
                return LayerColorCode.Unset;
            }

            short idx;
            if (short.TryParse(t, out idx)) return LayerColorCode.FromAci(idx);
            return LayerColorCode.Unset;
        }

        static bool ParseBool(string s, bool fallback)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t == "是" || t == "true" || t == "1" || t == "yes" || t == "开") return true;
            if (t == "否" || t == "false" || t == "0" || t == "no" || t == "关") return false;
            return fallback;
        }

        /// <summary>规整配置：补全空字段、去重配置名、修正活动配置。纯内存操作，不落盘。</summary>
        public static void Normalize(Config cfg)
        {
            if (cfg.Settings == null) cfg.Settings = new Settings();
            if (cfg.Profiles == null) cfg.Profiles = new List<Profile>();

            // 去重：手改 JSON 或者旧版本留下的重名/空名会让下拉框出现两个一样的项，
            // 用户根本分不清在改哪一套。这里统一改成唯一名字。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Profile p in cfg.Profiles)
            {
                if (p.Rules == null) p.Rules = new List<Rule>();
                if (p.Overrides == null) p.Overrides = new List<CommandOverride>();

                MigrateRules(p);

                string baseName = string.IsNullOrEmpty(p.Name) ? "未命名" : p.Name.Trim();
                string name = baseName;
                int suffix = 2;
                while (!seen.Add(name))
                {
                    name = baseName + " (" + suffix++ + ")";
                }
                p.Name = name;
            }

            // 长按的默认值搬过两次：350（1.5 及以前）→ 250（1.6）→ 180（1.6 收尾）。
            // 只搬**默认值** —— 自己改成过 400 / 500 的原样留着，那是真在调手感。
            if (cfg.Settings.PieHoldMs == 350 || cfg.Settings.PieHoldMs == 250)
                cfg.Settings.PieHoldMs = 180;

            // 圆盘按键只有「右键 / 侧键后退 / 侧键前进」三种，其它值（手改错了）
            // 一律退回右键 —— 退回默认比"钩子认不出键、圆盘再也不弹"好查。
            // 只有 0/1/2 三档（右键长按 / 侧键后退 / 侧键前进）。3 是 1.10 那个被删掉的
            // 「任一侧键」，老 ini 或手改出来的越界值一律退回右键长按。
            if (cfg.Settings.PieButton < 0 || cfg.Settings.PieButton > 2) cfg.Settings.PieButton = 0;

            // 快捷键：主键没了就等于没配，修饰键一并清掉，
            // 否则界面会显示「Ctrl+Shift」这种按不出来的组合。
            if (cfg.Settings.HotkeyKey <= 0 || cfg.Settings.HotkeyKey > 0xFF)
            {
                cfg.Settings.HotkeyKey = 0;
                cfg.Settings.HotkeyMods = 0;
            }
            else cfg.Settings.HotkeyMods &= (Hotkey.Ctrl | Hotkey.Alt | Hotkey.Shift | Hotkey.Win);

            // 自定义命令名：手改 ini 写坏了就退回「不用」，别让启动时发一句语法错的 LISP
            string why;
            string alias = (cfg.Settings.CommandAlias ?? "").Trim();
            if (alias.Length == 0) cfg.Settings.CommandAlias = "";
            else if (CommandAliasHost.IsValidName(alias, out why)) cfg.Settings.CommandAlias = alias.ToUpperInvariant();
            else cfg.Settings.CommandAlias = "";

            if (cfg.Profiles.Count == 0) cfg.Profiles.Add(DefaultProfile());
            if (cfg.FindProfile(cfg.ActiveProfile) == null)
                cfg.ActiveProfile = cfg.Profiles[0].Name;
        }

        /// <summary>
        /// 旧版本「矩形」是单独一类，现在并进「线段」了。
        /// 老配置读进来要搬过去，否则用户会发现矩形忽然不归层了。
        /// </summary>
        static void MigrateRules(Profile p)
        {
            Rule rect = null;
            foreach (Rule r in p.Rules)
                if (r != null && string.Equals(r.Category, "矩形", StringComparison.OrdinalIgnoreCase))
                    rect = r;

            if (rect != null)
            {
                string moved = rect.Layer;
                bool lineEmpty = p.LayersFor(Categories.Line).Count == 0 ||
                                 p.LayerFor(Categories.Line).Trim().Length == 0;

                if (lineEmpty && !string.IsNullOrEmpty(moved) && moved.Trim().Length > 0)
                {
                    var list = new List<LayerRef>();
                    foreach (LayerRef l in rect.Layers) if (l != null) list.Add(l);
                    p.SetLayerRefs(Categories.Line, list);
                    p.SetActiveIndex(Categories.Line, rect.ClampActive());
                }
                p.RemoveRule("矩形");
            }

            // 规整每一类：去空层、把 Active 拉回合法范围
            foreach (Rule r in p.Rules) if (r != null) r.Tidy();

            // 已经不存在的类别（老版本遗留）直接丢掉 —— 它们在界面上也看不到
            var known = new HashSet<string>(Categories.All, StringComparer.OrdinalIgnoreCase);
            p.Rules.RemoveAll(r => r == null || !known.Contains(r.Category ?? ""));
        }

        public static Config CreateDefault()
        {
            var cfg = new Config
            {
                Version = 1,
                Enabled = false,
                ActiveProfile = "默认配置",
                Settings = new Settings()
            };
            cfg.Profiles.Add(DefaultProfile());
            return cfg;
        }

        /// <summary>
        /// 默认配置：所有类别都归 0 层。
        /// 用「0」而不是留空，是因为 0 层每张图都有，绝不会凭空造出用户没见过的层名；
        /// 同时它给出一个明确的起点——想分开哪类就改哪行，没改的继续落在 0 层。
        /// </summary>
        public static Profile DefaultProfile()
        {
            var p = new Profile { Name = "默认配置" };
            foreach (string cat in Categories.All) p.SetLayer(cat, "0");
            return p;
        }
    }
}
