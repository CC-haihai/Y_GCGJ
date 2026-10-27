using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AutoLayer.Core.AutoLayerApp))]

namespace AutoLayer.Core
{
    /// <summary>一次"切层"的记录，用于命令结束后精确还原。</summary>
    internal sealed class SwitchEntry
    {
        public string Command;
        public ObjectId PrevClayer;
        public ObjectId SetLayer;
    }

    /// <summary>
    /// 最近发生的归层动作。1.11 起面板底部那条「最近一次」状态条撤了，
    /// 现在只有 ALTRACE 命令读它 —— 排障时想知道"刚才那条线为什么去了那层"就靠这个。
    /// </summary>
    public static class Trace
    {
        const int Cap = 40;
        static readonly List<string> _items = new List<string>();

        public static void Add(string text)
        {
            lock (_items)
            {
                _items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
                if (_items.Count > Cap) _items.RemoveAt(0);
            }
        }

        public static List<string> Recent()
        {
            lock (_items) { return new List<string>(_items); }
        }

        /// <summary>
        /// 清空记录。面板撤掉状态条以后暂时没有调用点（设置窗口原来会清一次，
        /// 就是为了不留下"快捷键已改、底下还挂着旧记录"的假象），
        /// 留着是给以后需要"清一次重新看"的地方用。
        /// </summary>
        public static void Clear()
        {
            lock (_items) { _items.Clear(); }
        }
    }

    public sealed class AutoLayerApp : IExtensionApplication
    {
        public void Initialize()
        {
            CleanupOldFiles();
            CommandHook.Initialize();
            TryStartUi();

            // 自定义命令名要往命令行发 LISP，必须等 CAD 起完、并且有活动图纸，
            // 所以推迟到第一次 Idle。之后每切一张图纸再对一次（换图纸不会丢定义，
            // 但万一第一次没发出去，这是补上的机会）。
            try { AcApp.Idle += OnFirstIdle; } catch { }
        }

        static void OnFirstIdle(object sender, EventArgs e)
        {
            try { AcApp.Idle -= OnFirstIdle; } catch { }

            try { CommandAliasHost.Sync(); } catch { }
            try
            {
                AcApp.DocumentManager.DocumentActivated += (s, a) =>
                {
                    try { CommandAliasHost.Sync(); } catch { }
                };
            }
            catch { }
        }

        public void Terminate()
        {
            try { CommandAliasHost.Clear(); } catch { }
            try { UiBridge.Call("Shutdown", null); } catch { }
            CommandHook.Shutdown();
        }

        /// <summary>
        /// 状态条和右键圆盘要在加载时就起来，不能等用户敲 QH。
        /// 但 accoreconsole（无界面内核）里绝不能碰 WPF —— 那里连 acmgd 都没加载。
        /// </summary>
        static void TryStartUi()
        {
            try
            {
                if (!GuiLoaded()) return;
                UiBridge.Call("Startup", null);
            }
            catch { }
        }

        /// <summary>完整 AutoCAD 一定已经加载了 acmgd；内核版没有。用这个判断有没有界面。</summary>
        static bool GuiLoaded()
        {
            try
            {
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a == null) continue;
                    if (string.Equals(a.GetName().Name, "acmgd", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 升级时若 CAD 正开着，旧 DLL 只能改名成 .dll.old（删不掉）。
        /// 下次启动时它们已经不再被占用，顺手清掉，免得安装目录越来越乱。
        /// </summary>
        static void CleanupOldFiles()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(typeof(AutoLayerApp).Assembly.Location);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;

                foreach (string f in System.IO.Directory.GetFiles(dir, "*.dll.old"))
                {
                    try { System.IO.File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 核心：监听命令前置事件，把当前图层切到目标图层；命令结束后还原。
    /// 所有回调都整段 try/catch —— 异常一旦逃逸到 AutoCAD 的命令循环会导致崩溃。
    /// </summary>
    public static class CommandHook
    {
        static readonly Dictionary<Document, Stack<SwitchEntry>> Stacks =
            new Dictionary<Document, Stack<SwitchEntry>>();

        /// <summary>
        /// 每张图纸「最近一次用到哪个类别」。圆盘要按这个类别取图层池 ——
        /// 你刚画完线，长按右键出来的就该是线段那一池。
        /// </summary>
        static readonly Dictionary<Document, string> LastCategory =
            new Dictionary<Document, string>();

        static readonly HashSet<string> Notified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            AcApp.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;

            foreach (Document d in AcApp.DocumentManager) Attach(d);
            Trace.Add("AutoLayer 已加载（" + (ConfigStore.Current.Enabled ? "已启用" : "未启用") + "）");
        }

        public static void Shutdown()
        {
            AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
            foreach (Document d in AcApp.DocumentManager) Detach(d);
            _initialized = false;
        }

        static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            try { Attach(e.Document); } catch { }
        }

        static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try { Attach(e.Document); } catch { }
        }

        static void Attach(Document doc)
        {
            if (doc == null) return;
            doc.CommandWillStart -= OnWillStart;
            doc.CommandWillStart += OnWillStart;
            doc.CommandEnded -= OnCommandFinished;
            doc.CommandEnded += OnCommandFinished;
            doc.CommandCancelled -= OnCommandFinished;
            doc.CommandCancelled += OnCommandFinished;
            doc.CommandFailed -= OnCommandFinished;
            doc.CommandFailed += OnCommandFinished;
        }

        static void Detach(Document doc)
        {
            if (doc == null) return;
            try
            {
                doc.CommandWillStart -= OnWillStart;
                doc.CommandEnded -= OnCommandFinished;
                doc.CommandCancelled -= OnCommandFinished;
                doc.CommandFailed -= OnCommandFinished;
            }
            catch { }
        }

        /// <summary>把 "_."、"-"、透明命令前缀都剥掉，统一成英文大写命令名。</summary>
        public static string Normalize(string globalName)
        {
            if (string.IsNullOrEmpty(globalName)) return "";
            string s = globalName.Trim();
            while (s.Length > 0)
            {
                char c = s[0];
                if (c == '\'' || c == '_' || c == '.' || c == '-') { s = s.Substring(1); continue; }
                break;
            }
            return s.ToUpperInvariant();
        }

        // ------------------------------------------------------------------ 前置

        static void OnWillStart(object sender, CommandEventArgs e)
        {
            try
            {
                Document doc = sender as Document;
                if (doc == null) return;

                Config cfg = ConfigStore.Current;
                if (cfg == null || !cfg.Enabled) return;

                Profile profile = cfg.Active;
                if (profile == null) return;

                string command = Normalize(e.GlobalCommandName);
                if (command.Length == 0) return;

                Resolution r;
                if (!RuleResolver.TryResolve(profile, command, out r)) return;

                LayerService.EnsureResult ensured = LayerService.Ensure(doc.Database, r.Layer, cfg.Settings, r.ColorValue);
                if (!ensured.Ok)
                {
                    string why = ensured.Notes.Count > 0
                        ? string.Join("；", ensured.Notes.ToArray())
                        : "目标图层「" + r.Layer + "」不存在且未开启自动新建，本条不归层";
                    NoteOnce(doc, "skip:" + r.Layer, "[Y_归层工具] " + why);
                    return;
                }

                foreach (string note in ensured.Notes)
                    NoteOnce(doc, note, "[Y_归层工具] " + note);

                using (DocumentLock lk = doc.LockDocument())
                {
                    Database db = doc.Database;
                    ObjectId prev = db.Clayer;
                    if (prev == ensured.Id)
                    {
                        // 已经是目标图层，不需要切，也不需要还原
                        Trace.Add(command + " → " + r.Layer + "（原本就在）");
                        return;
                    }

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        db.Clayer = ensured.Id;
                        tr.Commit();
                    }

                    Stack<SwitchEntry> stack;
                    if (!Stacks.TryGetValue(doc, out stack))
                    {
                        stack = new Stack<SwitchEntry>();
                        Stacks[doc] = stack;
                    }
                    stack.Push(new SwitchEntry { Command = command, PrevClayer = prev, SetLayer = ensured.Id });
                }

                Trace.Add(command + " → " + r.Layer + "（" + r.Category + "）");

                if (!string.IsNullOrEmpty(r.Category))
                {
                    LastCategory[doc] = r.Category;
                    LayerEvents.Raise();
                }
            }
            catch (System.Exception ex)
            {
                Trace.Add("拦截失败 " + ex.GetType().Name);
            }
        }

        // ------------------------------------------------------------------ 对外

        /// <summary>这张图纸最近一次归层用的是哪个类别；还没画过返回空串。</summary>
        public static string LastCategoryOf(Document doc)
        {
            if (doc == null) return "";
            string c;
            return LastCategory.TryGetValue(doc, out c) ? c : "";
        }

        /// <summary>
        /// 切到某个类别池里的某个图层 —— 只改配置，不动当前图层。
        /// 这样"画到一半换层"不会把手上这条命令撕成两层，下一条命令才生效。
        /// 返回 true 表示确实切了（配置变了并已落盘）。
        /// </summary>
        public static bool SwitchToLayer(Document doc, string category, string layerName)
        {
            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null) return false;
                Profile p = cfg.Active;
                if (p == null || string.IsNullOrEmpty(category)) return false;

                int idx = -1;
                var list = p.LayersFor(category);
                for (int i = 0; i < list.Count; i++)
                    if (string.Equals(list[i].Name, layerName, StringComparison.Ordinal)) { idx = i; break; }

                if (idx < 0) return false;
                if (p.ActiveIndexOf(category) == idx)
                {
                    if (doc != null) LastCategory[doc] = category;
                    return false;
                }

                p.SetActiveIndex(category, idx);
                ConfigStore.Save(cfg);
                if (doc != null) LastCategory[doc] = category;

                Trace.Add("切换 " + category + " → " + layerName + "（下一条命令生效）");
                LayerEvents.Raise();
                return true;
            }
            catch { return false; }
        }

        /// <summary>把某个类别池里的第 index 个设为当前（圆盘/面板用）。</summary>
        public static bool SwitchToIndex(Document doc, string category, int index)
        {
            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null) return false;
                Profile p = cfg.Active;
                if (p == null) return false;

                var list = p.LayersFor(category);
                if (index < 0 || index >= list.Count) return false;
                return SwitchToLayer(doc, category, list[index].Name);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ 后置

        static void OnCommandFinished(object sender, CommandEventArgs e)
        {
            try
            {
                Document doc = sender as Document;
                if (doc == null) return;

                Stack<SwitchEntry> stack;
                if (!Stacks.TryGetValue(doc, out stack) || stack.Count == 0) return;

                string command = Normalize(e.GlobalCommandName);
                SwitchEntry top = stack.Peek();
                if (!string.Equals(top.Command, command, StringComparison.OrdinalIgnoreCase)) return;

                stack.Pop();

                Config cfg = ConfigStore.Current;
                if (cfg == null || cfg.Settings == null || !cfg.Settings.RestoreClayer) return;

                using (DocumentLock lk = doc.LockDocument())
                {
                    Database db = doc.Database;
                    // 只有"当前图层仍是我们设的那个"才还原：
                    // 如果用户命令中途自己改了图层，尊重用户的选择。
                    if (db.Clayer == top.SetLayer && top.PrevClayer != ObjectId.Null)
                    {
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            db.Clayer = top.PrevClayer;
                            tr.Commit();
                        }
                    }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ 提示

        static void NoteOnce(Document doc, string key, string message)
        {
            try
            {
                lock (Notified)
                {
                    string full = (doc.Name ?? "") + "|" + key;
                    if (Notified.Contains(full)) return;
                    Notified.Add(full);
                }
                doc.Editor.WriteMessage("\n" + message);
            }
            catch { }
        }
    }
}
