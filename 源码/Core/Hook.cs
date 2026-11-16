using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

        /// <summary>
        /// 一条记录：时刻 + 原文。时间戳在这里**不格式化** ——
        /// Add 挂在每条 CAD 命令上，原来每命令都要 DateTime.Now.ToString("HH:mm:ss")
        /// 再拼一次字符串（两次分配），而 ALTRACE 可能一整天都不敲一次。
        /// 存值类型、读的时候才拼，热路径上只剩一次入队。
        /// </summary>
        struct Item
        {
            public DateTime At;
            public string Text;
        }

        static readonly List<Item> _items = new List<Item>();

        public static void Add(string text)
        {
            if (text == null) return;
            lock (_items)
            {
                _items.Add(new Item { At = DateTime.Now, Text = text });
                if (_items.Count > Cap) _items.RemoveAt(0);
            }
        }

        public static List<string> Recent()
        {
            lock (_items)
            {
                var lines = new List<string>(_items.Count);
                for (int i = 0; i < _items.Count; i++)
                {
                    Item it = _items[i];
                    lines.Add(it.At.ToString("HH:mm:ss") + "  " + it.Text);
                }
                return lines;
            }
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

            // 视口图层工具：功能区选项卡 + 「进视口自动切归层配置」的轮询。
            // 只在有界面时启 —— 内核版（accoreconsole）没有布局视口可切，开了纯属浪费。
            try { if (GuiLoaded()) VpLayer.VpStartup.Start(); } catch { }

            // 填充层复位（原 CC 插件的功能）：HPLAYER 被旧图设成固定层时，
            // 新建的填充会跑到那一层去，不回到当前层。
            ForceHplayerToCurrent();

            // 自定义命令名要往命令行发 LISP，必须等 CAD 起完、并且有活动图纸，
            // 所以推迟到第一次 Idle。之后每切一张图纸再对一次（换图纸不会丢定义，
            // 但万一第一次没发出去，这是补上的机会）。
            try { AcApp.Idle += OnFirstIdle; } catch { }
        }

        /// <summary>把 HPLAYER 复位为 "."（当前图层），让新建填充落在当前层。</summary>
        static void ForceHplayerToCurrent()
        {
            try
            {
                if (AcApp.DocumentManager.MdiActiveDocument == null) return;
                AcApp.SetSystemVariable("HPLAYER", ".");
            }
            catch { }
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
                    ForceHplayerToCurrent();
                };
                AcApp.DocumentManager.DocumentCreated += (s, a) =>
                {
                    ForceHplayerToCurrent();
                };
            }
            catch { }

            PrintBanner();
        }

        /// <summary>加载完成提示。只在有界面时打一次 —— 内核版（accoreconsole）刷这些没意义。</summary>
        static void PrintBanner()
        {
            try
            {
                if (!GuiLoaded()) return;
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                string c = AutoLayerCommands.Cmd;
                string who = "Y_归层工具 " + AppVersion.Text + "  已加载（自动归层 + 图层切换 + 视口）";

                doc.Editor.WriteMessage(
                    "\n============================================" +
                    "\n  " + who +
                    "\n--------------------------------------------" +
                    "\n  QH" + c + "       归层设置面板    ALON" + c + "/ALOFF" + c + "  启用/停用" +
                    "\n  CC" + c + "       图层切换面板    鼠标划过图层即预亮" +
                    "\n  ALSET" + c + "    设置窗口        ALPIE" + c + "      圆盘" +
                    "\n  ALSTATUS" + c + " 状态 | ALTRACE" + c + " 记录 | ALEDIT" + c + " 配置文件" +
                    "\n  CCHELP" + c + "   图层切换说明" +
                    "\n--------------------------------------------" +
                    "\n  VPT" + c + "      视口图层工具    VLTAG" + c + "   视口标记" +
                    "\n============================================");
            }
            catch { }
        }

        public void Terminate()
        {
            try { CommandAliasHost.Clear(); } catch { }
            try { VpLayer.VpStartup.Stop(); } catch { }
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
        /// <summary>
        /// 每张图纸的切层还原栈。
        ///
        /// **必须用 ConditionalWeakTable 而不是 Dictionary&lt;Document, …&gt;**：
        /// Document 是 CAD 托管的对象，图纸关掉后这个 key 不该再被插件按住 ——
        /// 用普通字典会一直攒着已关闭图纸的引用（每关一张图漏一份栈，长班次下来很可观）。
        /// CWT 的 key 是弱引用，图纸没了条目自己就没了，不需要额外的清理时机。
        /// 顺带解决线程安全：CAD 的回调可能来自不同上下文，CWT 内部自带同步。
        /// </summary>
        static readonly ConditionalWeakTable<Document, Stack<SwitchEntry>> Stacks =
            new ConditionalWeakTable<Document, Stack<SwitchEntry>>();

        /// <summary>建栈的工厂：缓存成静态字段，避免每次 GetValue 都现造一个委托。</summary>
        static readonly ConditionalWeakTable<Document, Stack<SwitchEntry>>.CreateValueCallback NewStack =
            delegate { return new Stack<SwitchEntry>(); };

        /// <summary>
        /// 每张图纸「最近一次用到哪个类别」。圆盘要按这个类别取图层池 ——
        /// 你刚画完线，长按右键出来的就该是线段那一池。
        /// 同上，弱引用，跟着图纸生命周期走。
        /// </summary>
        static readonly ConditionalWeakTable<Document, CategoryBox> LastCategory =
            new ConditionalWeakTable<Document, CategoryBox>();

        /// <summary>CWT 的 value 必须是个引用类型，用它把 string 装进去。</summary>
        sealed class CategoryBox { public string Value = ""; }

        static readonly ConditionalWeakTable<Document, CategoryBox>.CreateValueCallback NewCategoryBox =
            delegate { return new CategoryBox(); };

        /// <summary>
        /// 「这条提示已经说过了」的去重集合。键是「图纸名|提示键」。
        /// 图纸关了名字不会自己消失，所以要封顶 —— 超过上限整批清空即可，
        /// 最坏结果只是某条提示再说一遍，不会无限涨。
        /// </summary>
        static readonly HashSet<string> Notified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int NotifiedCap = 512;

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
            foreach (Document d in AcApp.DocumentManager)
            {
                Detach(d);
                // 还原栈留着会让"重新 NETLOAD 之后第一命令莫名还原图层"，
                // 卸载时把还开着的图纸的栈一并清空
                try
                {
                    Stack<SwitchEntry> s;
                    if (d != null && Stacks.TryGetValue(d, out s)) s.Clear();
                }
                catch { }
            }
            lock (Notified) { Notified.Clear(); }
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

            // 不用 Substring 循环：每条命令前后各调一次，每剥一个字符就多一次分配。
            // 顺序仍是「先 Trim 再剥前缀」（原来是 Trim + 逐个 Substring，等价）。
            string s = globalName.Trim();
            int start = 0;
            while (start < s.Length)
            {
                char c = s[start];
                if (c == '\'' || c == '_' || c == '.' || c == '-') { start++; continue; }
                break;
            }
            if (start >= s.Length) return "";

            return (start == 0 ? s : s.Substring(start)).ToUpperInvariant();
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

                    bool switched = false;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        db.Clayer = ensured.Id;

                        // 回读校验：Clayer 赋值会被 CAD 静默吞掉（目标层冻结等）；
                        // 也存在编辑器侧缓存没跟上的情况（CC 面板模态对话框里直写过
                        // db.Clayer 的图纸）。被忽略就走 CLAYER 系统变量官方通道再试一次。
                        if (db.Clayer != ensured.Id)
                        {
                            try { AcApp.SetSystemVariable("CLAYER", r.Layer); }
                            catch { }
                        }
                        switched = db.Clayer == ensured.Id;
                        tr.Commit();
                    }

                    if (!switched)
                    {
                        // 没切过去绝不入还原栈（否则命令结束会"还原"一个从没设上去的层）。
                        // 原因进 Trace：ALTRACE 一查就知道这条为什么没归层。
                        Trace.Add(command + " → " + r.Layer + "（切层被 CAD 忽略，本条不归层）");
                        return;
                    }

                    // GetValue 是原子的（TryGetValue + Add 两步会有并发窗口）
                    Stack<SwitchEntry> stack = Stacks.GetValue(doc, NewStack);
                    stack.Push(new SwitchEntry { Command = command, PrevClayer = prev, SetLayer = ensured.Id });
                }

                Trace.Add(command + " → " + r.Layer + "（" + r.Category + "）");

                if (!string.IsNullOrEmpty(r.Category))
                {
                    LastCategory.GetValue(doc, NewCategoryBox).Value = r.Category;
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
            CategoryBox box;
            return LastCategory.TryGetValue(doc, out box) ? (box.Value ?? "") : "";
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
                    if (doc != null) LastCategory.GetValue(doc, NewCategoryBox).Value = category;
                    return false;
                }

                p.SetActiveIndex(category, idx);
                ConfigStore.Save(cfg);
                if (doc != null) LastCategory.GetValue(doc, NewCategoryBox).Value = category;

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
                            // 还原也走一遍系统变量通道，理由同 OnWillStart：
                            // 只写 db.Clayer 可能让编辑器缓存停在旧值上
                            if (db.Clayer == top.PrevClayer)
                            {
                                try { AcApp.SetSystemVariable("CLAYER", LayerService.NameOf(db, top.PrevClayer)); }
                                catch { }
                            }
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
                    // 封顶：图纸关掉后键不会自己消失，攒满就整批丢掉重来。
                    // 最坏后果只是某条提示再说一遍，比无限增长好。
                    if (Notified.Count >= NotifiedCap) Notified.Clear();
                    Notified.Add(full);
                }
                doc.Editor.WriteMessage("\n" + message);
            }
            catch { }
        }
    }
}
