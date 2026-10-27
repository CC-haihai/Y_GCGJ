using System;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoLayer.Core
{
    public class AutoLayerCommands
    {
        const string Tag = "[Y_归层工具]";

        // ------------------------------------------------------------------ 面板

        /// <summary>默认快捷键命令：弹出面板。UI 组件按需加载，无界面内核里不会碰 WPF。</summary>
        [CommandMethod("QH")]
        public void ShowPanel()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                if (!UiBridge.Call("Show", null))
                    ed.WriteMessage("\n" + Tag + " 找不到面板组件 AutoLayer.UI.dll（应与主 DLL 同目录）");
            }
            catch (System.Exception ex)
            {
                System.Exception inner = ex.InnerException ?? ex;
                ed.WriteMessage("\n" + Tag + " 面板无法打开：" + inner.Message);
            }
        }

        /// <summary>打开设置窗口（面板快捷键 / 圆盘触发方式）。</summary>
        [CommandMethod("ALSET")]
        public void ShowSettings()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                if (!UiBridge.Call("ShowSettings", null))
                    doc.Editor.WriteMessage("\n" + Tag + " 找不到界面组件 AutoLayer.UI.dll（应与主 DLL 同目录）");
            }
            catch (System.Exception ex)
            {
                System.Exception inner = ex.InnerException ?? ex;
                doc.Editor.WriteMessage("\n" + Tag + " 设置窗口无法打开：" + inner.Message);
            }
        }

        // ------------------------------------------------------------------ 开关

        [CommandMethod("ALON")]
        public void Enable()
        {
            SetEnabled(true);
        }

        [CommandMethod("ALOFF")]
        public void Disable()
        {
            SetEnabled(false);
        }

        [CommandMethod("ALTOGGLE")]
        public void Toggle()
        {
            Config cfg = ConfigStore.Current;
            SetEnabled(!cfg.Enabled);
        }

        static void SetEnabled(bool on)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Config cfg = ConfigStore.Current;
            cfg.Enabled = on;
            ConfigStore.Save(cfg);
            doc.Editor.WriteMessage("\n" + Tag + (on ? " 已启用" : " 已停用（不再介入任何命令）"));
            LayerEvents.Raise();
        }

        // ------------------------------------------------------------------ 状态

        [CommandMethod("ALSTATUS")]
        public void Status()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            Config cfg = ConfigStore.Current;
            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 状态：").Append(cfg.Enabled ? "已启用" : "已停用");
            sb.Append("\n  当前配置：").Append(string.IsNullOrEmpty(cfg.ActiveProfile) ? "(未选择)" : cfg.ActiveProfile);
            sb.Append("\n  配置总数：").Append(cfg.Profiles.Count);
            sb.Append("\n  命令结束还原当前图层：").Append(cfg.Settings.RestoreClayer ? "开" : "关");
            sb.Append("\n  自动新建图层：").Append(cfg.Settings.AutoCreateLayer ? "开" : "关");
            sb.Append("\n  自动解冻/打开/解锁：")
              .Append((cfg.Settings.AutoThaw ? "解冻" : "-") + "/"
                    + (cfg.Settings.AutoTurnOn ? "打开" : "-") + "/"
                    + (cfg.Settings.AutoUnlock ? "解锁" : "-"));

            Profile p = cfg.Active;
            if (p != null)
            {
                sb.Append("\n  当前配置映射：");
                foreach (string cat in Categories.All)
                {
                    var pool = p.LayersFor(cat);
                    if (pool.Count == 0) continue;

                    sb.Append("\n    ").Append(cat).Append(" → ");
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (i > 0) sb.Append("  |  ");
                        bool act = i == p.ActiveIndexOf(cat);
                        if (act) sb.Append("[").Append(pool[i].Name).Append("]");
                        else sb.Append(pool[i].Name);
                    }
                    if (pool.Count > 1) sb.Append("   (").Append(pool.Count).Append(" 个，[ ] 是当前)");
                }
            }

            string pieKey = cfg.Settings.PieButton == 1 ? "侧键后退"
                          : cfg.Settings.PieButton == 2 ? "侧键前进"
                          : "右键长按 " + cfg.Settings.PieHoldMs + "ms";
            sb.Append("\n  圆盘：").Append(cfg.Settings.PieEnabled ? "开（" + pieKey + "）" : "关");
            sb.Append("　状态条：顶部居中（固定）");

            string hk = cfg.Settings.HotkeyKey > 0
                ? Hotkey.Text(cfg.Settings.HotkeyMods, cfg.Settings.HotkeyKey)
                : "无（只用 QH 命令）";
            sb.Append("\n  面板快捷键：").Append(hk);

            string alias = cfg.Settings.CommandAlias ?? "";
            sb.Append("\n  自定义命令：")
              .Append(alias.Length == 0 ? "无（只用 QH）" : alias + "（= QH）");

            sb.Append("\n  配置文件：").Append(ConfigStore.FilePath);
            ed.WriteMessage(sb.ToString());
        }

        /// <summary>手动弹一次圆盘，方便验证样子（正常用法是右键长按）。</summary>
        [CommandMethod("ALPIE")]
        public void ShowPie()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try
            {
                bool ok = UiBridge.CallBool("ShowPieNow", null, out bool shown);
                if (!ok) doc.Editor.WriteMessage("\n" + Tag + " 圆盘没能弹出（界面组件未加载）");
                else if (!shown) doc.Editor.WriteMessage("\n" + Tag + " 圆盘没弹：当前类别配的图层不足两个");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n" + Tag + " 圆盘异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 快捷键诊断。快捷键不响应时先跑这个 —— 它会说清楚是钩子没装上、
        /// 组合键没对上、CAD 不是前台，还是有命令正在跑。
        /// </summary>
        [CommandMethod("ALKEY")]
        public void DiagnoseHotkey()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 快捷键诊断");

            string detail;
            if (!UiBridge.CallString("DescribeHotkey", null, out detail))
                sb.Append("\n  界面组件没加载，快捷键根本不会起来（ALON / QH 还能用吗？）");
            else sb.Append(detail);

            sb.Append("\n  自定义命令：").Append(CommandAliasHost.Describe());
            sb.Append("\n  说明：钩子是全局低层钩子，回调里会判断前台是不是 CAD —— 不是 CAD 就不接管。");
            doc.Editor.WriteMessage(sb.ToString());
        }

        /// <summary>
        /// 圆盘触发诊断。侧键 / 右键按了没反应时先跑这个 ——
        /// 它会说清楚钩子有没有装上、总开关和圆盘开没开、命令是不是正在跑，
        /// 以及最近几条按键消息（按一下侧键再看，就知道鼠标到底发没发 XBUTTON）。
        /// </summary>
        [CommandMethod("ALMOUSE")]
        public void DiagnoseMouse()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 圆盘触发诊断");

            string detail;
            if (!UiBridge.CallString("DescribeMouse", null, out detail))
                sb.Append("\n  界面组件没加载，钩子根本不会起来。");
            else sb.Append(detail);

            doc.Editor.WriteMessage(sb.ToString());
        }

        [CommandMethod("ALTRACE")]
        public void ShowTrace()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var sb = new StringBuilder();
            sb.Append("\n").Append(Tag).Append(" 最近记录：");
            var items = Trace.Recent();
            if (items.Count == 0) sb.Append("\n  (空)");
            for (int i = items.Count - 1; i >= 0; i--) sb.Append("\n  ").Append(items[i]);
            doc.Editor.WriteMessage(sb.ToString());
        }

        [CommandMethod("ALEDIT")]
        public void OpenConfigFolder()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try
            {
                Directory.CreateDirectory(ConfigStore.Folder);
                if (!File.Exists(ConfigStore.FilePath)) ConfigStore.Save(ConfigStore.Current);
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + ConfigStore.FilePath + "\"");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n" + Tag + " 打开失败：" + ex.Message);
            }
        }
    }
}
