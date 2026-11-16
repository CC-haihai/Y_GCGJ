using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoLayer.Core
{
    /// <summary>
    /// 自定义 CAD 命令名：让用户在设置里随便填一个名字（比如 GH），敲它等于敲 QH。
    /// 四个入口各有一个槽位 —— 主面板（QH）/ 图层切换（CC）/ 视口工具（VPT）/ 圆盘（ALPIE）。
    ///
    /// **为什么只能这么绕**：AutoCAD 的命令名是 [CommandMethod] 在编译期写死的，
    /// 运行期没有任何托管 API 能注册新命令（把 acmgd / accoremgd / acdbmgd 全翻了一遍，
    /// 只有 Utils.IsCommandNameInUse 这种查询方法，没有注册方法）。
    ///
    /// 但 AutoLISP 可以在运行期定义命令：往命令行发一句
    /// <code>(defun c:GH () (command "QH") (princ))</code>
    /// AutoCAD 就认这个 GH 了。这是唯一能做到"名字由用户定"的路子。
    ///
    /// 注意几件事：
    /// <list type="bullet">
    /// <item>定义是**全局**的，不跟着图纸走，所以只在启动 / 改名时发一次。</item>
    /// <item>发送要等 CAD 起完并且有活动图纸，否则 SendStringToExecute 没地方去。</item>
    /// <item>改名时先把旧的 <c>(setq c:旧名 nil)</c> 撤掉，不然会越积越多。</item>
    /// <item>LISP 命令会被 acad.pgp 里的同名别名盖住 —— 所以名字别用 L / C / M 这类。</item>
    /// <item>四个槽位的名字不能互相撞，也不能撞插件自己的命令。</item>
    /// </list>
    /// </summary>
    public static class CommandAliasHost
    {
        /// <summary>槽位：主面板（目标命令 QH）。</summary>
        public const string SlotPanel = "panel";
        /// <summary>槽位：图层切换面板（目标命令 CC）。</summary>
        public const string SlotQuickSwitch = "cc";
        /// <summary>槽位：视口工具（目标命令 VPT）。</summary>
        public const string SlotViewport = "vp";
        /// <summary>槽位：圆盘（目标命令 ALPIE）。</summary>
        public const string SlotPie = "pie";

        class Slot
        {
            public string Key;                   // 槽位名（诊断 / 查找用）
            public string Target;                // 真正要敲的命令（QH / CC / VPT / ALPIE）
            public string Title;                 // 给提示用的中文名
            public Func<Settings, string> Read;  // 从配置里取这个名字
            public string Defined = "";          // 已经定义出去的名字。空串 = 当前没有定义
        }

        static readonly Slot[] Slots = new[]
        {
            new Slot { Key = SlotPanel, Target = "QH" + AutoLayerCommands.Cmd, Title = "主面板",
                       Read = s => s.CommandAlias },
            new Slot { Key = SlotQuickSwitch, Target = "CC" + AutoLayerCommands.Cmd, Title = "图层切换",
                       Read = s => s.CcCommandAlias },
            // VPT 的命令名就是 VPT（VpCommands 里写死的）
            new Slot { Key = SlotViewport, Target = "VPT", Title = "视口工具",
                       Read = s => s.VpCommandAlias },
            new Slot { Key = SlotPie, Target = "ALPIE" + AutoLayerCommands.Cmd, Title = "圆盘",
                       Read = s => s.PieCommandAlias },
        };

        static Slot Of(string key)
        {
            foreach (Slot s in Slots)
                if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>上一次发出去的结果，给 ALKEY 诊断用。</summary>
        public static string LastNote = "";

        /// <summary>某个槽位当前实际生效的名字（没定义成就是空串）。</summary>
        public static string DefinedOf(string key)
        {
            Slot s = Of(key);
            return s == null ? "" : s.Defined;
        }

        static string WantedOf(Slot s)
        {
            Config cfg = ConfigStore.Current;
            if (cfg == null || cfg.Settings == null) return "";
            string v = s.Read == null ? null : s.Read(cfg.Settings);
            return (v ?? "").Trim().ToUpperInvariant();
        }

        /// <summary>这个名字能不能当命令名。返回 false 时 why 里是给用户看的原因。</summary>
        public static bool IsValidName(string name, out string why)
        {
            why = null;
            string s = (name ?? "").Trim();
            if (s.Length == 0) { why = "不能为空"; return false; }
            if (s.Length < 2) { why = "至少两个字 —— 单字母基本都被 CAD 自己的别名占了"; return false; }
            if (s.Length > 20) { why = "最多 20 个字"; return false; }

            foreach (char c in s)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                       || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok)
                {
                    why = "只能用字母、数字、减号、下划线（CAD 命令名不许有空格和中文）";
                    return false;
                }
            }

            // 和插件自己的命令撞名会让 LISP 定义覆盖掉真命令，直接拒
            foreach (string own in OwnCommands())
            {
                if (string.Equals(s, own, StringComparison.OrdinalIgnoreCase))
                {
                    why = "「" + own + "」是插件自带的命令，换一个";
                    return false;
                }
            }
            return true;
        }

        /// <summary>本插件注册过的全部命令名。撞名检查和诊断共用。</summary>
        static string[] OwnCommands()
        {
            string c = AutoLayerCommands.Cmd;
            return new[]
            {
                "QH" + c, "CC" + c, "CCHELP" + c, "ALSET" + c,
                "ALON" + c, "ALOFF" + c, "ALTOGGLE" + c, "ALSTATUS" + c,
                "ALPIE" + c, "ALTRACE" + c, "ALEDIT" + c, "ALKEY" + c, "ALMOUSE" + c,

                // 视口工具那批原样就是命令名
                "VPT", "VLPANEL", "VLHELP", "VLFRAME", "VLLIB", "VLINNER",
                "VLCAP", "VLUPDATE", "VLAPPLY", "VLBATCH", "VLLIST", "VLDEL",
                "VLTAG", "VLUNTAG", "VLTAGS", "VLALIGN"
            };
        }

        /// <summary>
        /// 这个名字是不是已经被 CAD 或别的插件占了。没占返回 null。
        /// </summary>
        public static string NameInUse(string name)
        {
            // 没有活动图纸就别去问。
            // 底层是原生函数 acedIsCommandNameInUse，编辑器还没初始化时它直接读野指针，
            // 抛的是 AccessViolationException —— 进程级致命错误，普通的 catch 根本拦不住，
            // 表现就是整个 CAD 被带走。宁可"查不到当没占用"，也绝不冒这个险。
            try { if (AcApp.DocumentManager.MdiActiveDocument == null) return null; }
            catch { return null; }

            string raw = QueryRaw(name);
            return InterpretStatus(raw);
        }

        /// <summary>
        /// 把 IsCommandNameInUse 的返回值翻成"占用说明"；没占用返回 null。**纯函数，可以单测。**
        ///
        /// 这个方法返回的是命令**种类**（枚举），不是 bool：
        /// <c>NoneCmd</c> = 这个名字根本不是命令（= 没被占用），
        /// 其余 <c>CoreCmd / ARXCmd / LispCmd / ActionMacroCmd / SetvarCmd</c> 才是真的占了。
        /// 一开始把 NoneCmd 当成"占用"，结果用户填什么名字都提示"已被占用"。
        /// </summary>
        public static string InterpretStatus(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (string.Equals(raw, "NoneCmd", StringComparison.OrdinalIgnoreCase)) return null;
            if (raw == "0") return null;
            if (string.Equals(raw, "False", StringComparison.OrdinalIgnoreCase)) return null;

            return KindText(raw);
        }

        /// <summary>把枚举名翻成人话，提示里直接给用户看。</summary>
        static string KindText(string s)
        {
            if (string.Equals(s, "CoreCmd", StringComparison.OrdinalIgnoreCase)) return "CAD 内置命令";
            if (string.Equals(s, "ARXCmd", StringComparison.OrdinalIgnoreCase)) return "ARX 命令";
            if (string.Equals(s, "LispCmd", StringComparison.OrdinalIgnoreCase)) return "已有的 LISP 命令";
            if (string.Equals(s, "ActionMacroCmd", StringComparison.OrdinalIgnoreCase)) return "动作宏";
            if (string.Equals(s, "SetvarCmd", StringComparison.OrdinalIgnoreCase)) return "CAD 系统变量";
            return s;
        }

        /// <summary>
        /// 真去问 AutoCAD。反射调原生函数，所以必须能扛住 AccessViolationException ——
        /// 靠 HandleProcessCorruptedStateExceptions 把它变回普通异常再 catch。
        /// </summary>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        static string QueryRaw(string name)
        {
            try
            {
                Type t = Type.GetType("Autodesk.AutoCAD.Internal.Utils, acmgd", false);
                if (t == null) return null;

                MethodInfo m = t.GetMethod("IsCommandNameInUse", new[] { typeof(string) });
                if (m == null) return null;

                object r = m.Invoke(null, new object[] { name });
                return r == null ? null : r.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// 让实际定义和配置保持一致。配置没变就什么都不做 —— 这个方法会被
        /// 启动、切图纸、保存设置反复调用，必须便宜。两个槽位各自独立比对。
        /// </summary>
        public static void Sync()
        {
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;      // 还没开图纸，等下一次（切图纸时会再调）

                foreach (Slot s in Slots) SyncSlot(doc, s);
            }
            catch (Exception ex)
            {
                LastNote = "定义失败：" + ex.GetType().Name + " " + ex.Message;
            }
        }

        static void SyncSlot(Document doc, Slot s)
        {
            string want = WantedOf(s);
            if (string.Equals(want, s.Defined, StringComparison.Ordinal)) return;

            // 先把旧的撤掉：LISP 里 (setq c:GH nil) 就是取消定义
            if (s.Defined.Length > 0)
            {
                Send(doc, "(setq c:" + s.Defined + " nil)");
                s.Defined = "";
            }

            if (want.Length == 0)
            {
                LastNote = "没有自定义" + s.Title + "命令";
                return;
            }

            string why;
            if (!IsValidName(want, out why))
            {
                LastNote = s.Title + "名字不合法：" + why;
                return;
            }

            // 两个槽位互相撞名：后定义的会盖掉先定义的，必须拦下
            foreach (Slot other in Slots)
            {
                if (other == s) continue;
                if (string.Equals(want, WantedOf(other), StringComparison.Ordinal))
                {
                    LastNote = s.Title + "名字「" + want + "」和" + other.Title + "命令同名，未定义";
                    return;
                }
            }

            Send(doc, "(defun c:" + want + " () (command \"" + s.Target + "\") (princ))");
            s.Defined = want;
            LastNote = "已定义 " + want + "（" + s.Title + "）";
        }

        /// <summary>卸载插件时把定义撤掉，免得留下一个敲了就报"未知命令"的空壳。</summary>
        public static void Clear()
        {
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                foreach (Slot s in Slots)
                {
                    if (s.Defined.Length == 0) continue;
                    if (doc != null) Send(doc, "(setq c:" + s.Defined + " nil)");
                    s.Defined = "";
                }
            }
            catch { }
        }

        static void Send(Document doc, string lisp)
        {
            // 四个参数：不激活文档、不处理非活动文档、**不把 LISP 回显到命令行**
            // —— 回显的话用户会莫名其妙看到一串括号。
            doc.SendStringToExecute(lisp + " ", false, false, false);
        }

        /// <summary>给 ALKEY 用的诊断串。</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();

            foreach (Slot s in Slots)
            {
                string want = WantedOf(s);
                sb.Append(s.Title).Append("命令：")
                  .Append(string.IsNullOrEmpty(want) ? "未配置" : want)
                  .Append("（").Append(s.Defined.Length > 0 ? "已定义 " + s.Defined : "未定义").Append("）");
                if (s != Slots[Slots.Length - 1]) sb.Append("　");
            }
            if (!string.IsNullOrEmpty(LastNote)) sb.Append("　").Append(LastNote);
            return sb.ToString();
        }
    }
}
