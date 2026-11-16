using System;
using System.Collections.Generic;

namespace VpLayer
{
    /// <summary>
    /// 一套「视口图层状态」。
    ///
    /// 只记「这个视口里被冻结的图层名」—— 不记颜色/线型/线宽。
    /// 理由：出图时真正要统一的是「这个视口能看到哪些层」，冻结集合通常是要隐藏的少数；
    /// 记录名字而不是 ObjectId，才能跨图纸复用（新图纸里图层 ObjectId 全不一样）。
    ///
    /// 应用时先 ThawAllLayersInViewport 再按集合冻结，结果确定，
    /// 不受目标视口原本状态影响 —— 这样「统一」才是真的统一。
    /// </summary>
    public sealed class VpState
    {
        public string Name = "";
        public List<string> FrozenLayers = new List<string>();

        /// <summary>
        /// 刷这个状态时主插件正在用的「归层配置」名。
        ///
        /// 视口状态只管"哪些层被冻结"，而归层配置管"画什么自动进哪一层" ——
        /// 出图时这两件事是绑在一起的一套东西，所以状态里带上配置名：
        /// 套用状态时顺手写进视口标签，进视口就自动切过去。
        /// 空串表示没记录（主插件没装/没开/刷取时没配置）。
        /// </summary>
        public string Profile = "";

        public VpState() { }
        public VpState(string name) { Name = name ?? ""; }

        public VpState Clone()
        {
            VpState c = new VpState(Name);
            c.FrozenLayers.AddRange(FrozenLayers);
            c.Profile = Profile;
            return c;
        }

        public bool Has(string layerName)
        {
            for (int i = 0; i < FrozenLayers.Count; i++)
                if (string.Equals(FrozenLayers[i], layerName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>命令行/面板上的一句话摘要。</summary>
        public string Summary
        {
            get { return Name + "（冻结 " + FrozenLayers.Count + " 层）"; }
        }
    }

    /// <summary>
    /// 图框模板。可以存多个，生成时用当前选中的那个。
    ///
    /// 两种来源：
    ///   1. 点选布局里的块/多段线 —— 记块名与缩放，生成时按原样插入；
    ///   2. 直接框一个矩形 —— 只记尺寸，生成时画矩形。
    /// 跨图纸时块定义可能不存在，生成阶段会降级成矩形并提示。
    ///
    /// 内外框：外框 = 图框纸边，内框 = 真正的绘图区（右边常被图签栏占掉一截）。
    /// 视口必须铺满内框，不能铺满外框 —— 否则会压到图签栏上。
    /// 内框记的是「相对外框左下角的偏移 + 尺寸」，跟块缩放无关。
    /// </summary>
    public sealed class FrameTemplate
    {
        /// <summary>用户自己起的名字（「A1横」「1#图框」）。空则自动按块名/尺寸显示。</summary>
        public string Label = "";

        public bool IsBlock;
        public string BlockName = "";

        /// <summary>
        /// 图框块所在的那张图纸（拾取时记下来）。
        /// 换一张图纸生成时块表里没有这个块，就从这张图纸把它导进来 ——
        /// 否则只能画个空矩形，用户看到的就是「图框没出现」。
        /// </summary>
        public string SourceDwg = "";

        /// <summary>
        /// 来自图框库目录的哪个 dwg。空 = 手动拾取的老图框（没有对应文件）。
        /// 扫描目录时按这个字段认人，所以重新扫描不会重复添加。
        /// </summary>
        public string LibFile = "";

        /// <summary>
        /// true = 整张 dwg 当成一个图框（生成时 db.Insert 把整图做成块）。
        /// false = 用 dwg 里那个块参照的块定义（LibFile 是它所在的文件）。
        /// </summary>
        public bool WholeFile;

        /// <summary>
        /// 内框是用户手动框过的。手动框过的，重新扫描目录时不覆盖 ——
        /// 自动识别再准，也不该把人的判断冲掉。
        /// </summary>
        public bool InnerManual;

        public double Width;
        public double Height;

        /// <summary>块参照的缩放。拾取时是什么缩放，生成时就按同样缩放插。</summary>
        public double ScaleX = 1.0;
        public double ScaleY = 1.0;

        /// <summary>内框（绘图区）相对外框左下角的偏移与尺寸。全 0 = 没识别到。</summary>
        public double InnerX;
        public double InnerY;
        public double InnerWidth;
        public double InnerHeight;

        public bool HasInner { get { return InnerWidth > 0.001 && InnerHeight > 0.001; } }
        public bool Valid { get { return Width > 0 && Height > 0; } }

        /// <summary>视口该铺满的区域。没识别到内框时按外框内缩 2，免得视口边框压在图框线上。</summary>
        public double DrawX { get { return HasInner ? InnerX : 2.0; } }
        public double DrawY { get { return HasInner ? InnerY : 2.0; } }
        public double DrawW { get { return HasInner ? InnerWidth : Math.Max(1.0, Width - 4.0); } }
        public double DrawH { get { return HasInner ? InnerHeight : Math.Max(1.0, Height - 4.0); } }

        public void ClearInner()
        {
            InnerX = 0; InnerY = 0; InnerWidth = 0; InnerHeight = 0;
        }

        public FrameTemplate Clone()
        {
            var c = new FrameTemplate();
            c.Label = Label;
            c.IsBlock = IsBlock;
            c.BlockName = BlockName;
            c.SourceDwg = SourceDwg;
            c.LibFile = LibFile;
            c.WholeFile = WholeFile;
            c.InnerManual = InnerManual;
            c.Width = Width;
            c.Height = Height;
            c.ScaleX = ScaleX;
            c.ScaleY = ScaleY;
            c.InnerX = InnerX;
            c.InnerY = InnerY;
            c.InnerWidth = InnerWidth;
            c.InnerHeight = InnerHeight;
            return c;
        }

        /// <summary>列表里显示的名字：用户命名优先，其次块名，最后按尺寸凑一个。</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Label)) return Label.Trim();
                if (IsBlock && !string.IsNullOrEmpty(BlockName)) return BlockName;
                if (Valid) return "图框 " + Math.Round(Width) + "×" + Math.Round(Height);
                return "未命名图框";
            }
        }

        public string Describe()
        {
            if (!Valid) return "未保存";

            string s = DisplayName + "　" + Math.Round(Width) + " × " + Math.Round(Height);

            s += HasInner
                ? "　内框 " + Math.Round(InnerWidth) + " × " + Math.Round(InnerHeight)
                : "　内框未识别（按外框内缩 2）";

            return s;
        }
    }

    /// <summary>
    /// 一组视口状态。一个公司 / 一个项目一套，互不干扰。
    ///
    /// 为什么要有它：不同公司常常有同名状态（都叫「一层平面图」），但冻结层完全不同。
    /// 平铺在一个列表里既会撞名（Find 按名字找，第二个就找不到了），
    /// 也会塞满（用户 2026-11-16 反馈「五六个就已经塞满了」）。
    /// 名字留空表示「默认」组 —— 老配置里那批状态全都落在这里，不用迁移。
    /// </summary>
    public sealed class StateGroup
    {
        /// <summary>状态集名字。空 = 默认组。</summary>
        public string Name = "";

        /// <summary>这一套里的状态。顺序 = 批量生成时第 N 个视口套第 N 个。</summary>
        public List<VpState> States = new List<VpState>();

        public StateGroup() { }

        public StateGroup(string name) { Name = name ?? ""; }

        /// <summary>界面上显示的名字。空组显示成「默认」，别让它变成一行空白。</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? "默认" : Name; }
        }

        /// <summary>组内按名字找。重名只会在组与组之间发生，组内仍然是唯一的。</summary>
        public int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < States.Count; i++)
                if (States[i] != null && string.Equals(States[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public VpState Find(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : States[i];
        }
    }

    /// <summary>整个视口工具的配置：多个图框模板 + 若干状态集。</summary>
    public sealed class VpConfig
    {
        /// <summary>生成方式：每个状态一张图纸。</summary>
        public const int ModePerSheet = 0;

        /// <summary>生成方式：一个布局里一排多个视口。</summary>
        public const int ModeRow = 1;

        /// <summary>一排模式下，图框之间的净间距（图纸单位 mm）。</summary>
        public const double RowGap = 50.0;

        /// <summary>图框模板列表。可以存多个，生成时用 FrameIndex 指定的那个。</summary>
        public List<FrameTemplate> Frames = new List<FrameTemplate>();

        /// <summary>当前选中的图框在 Frames 里的下标。</summary>
        public int FrameIndex;

        /// <summary>生成方式，见 ModePerSheet / ModeRow。</summary>
        public int LayoutMode;

        /// <summary>
        /// 出图比例（0.02 = 1:50）。0 = 自动：按内容与内框算出能放下的最大标准比例。
        /// </summary>
        public double Scale;

        /// <summary>
        /// 状态列表。顺序 = 批量生成时第 N 个视口套第 N 个状态。
        ///
        /// 注意：这是**当前状态集**里那一列，不是全部状态。
        /// 刷取 / 批量生成 / 对齐 / 图层兜底全都自动只作用于当前集，这正是想要的
        /// —— 切到哪家公司，就只处理哪家公司的状态。
        /// 唯一的例外是序列化：<see cref="VpStore.ToText"/> 要写全部 Groups，不能走这里。
        /// </summary>
        public List<VpState> States
        {
            get { return ActiveGroup.States; }
        }

        /// <summary>所有状态集，按 ini 里的先后顺序。</summary>
        public List<StateGroup> Groups = new List<StateGroup>();

        /// <summary>当前用哪个状态集（按名字）。空 = 默认组。</summary>
        public string ActiveGroupName = "";

        /// <summary>
        /// 当前状态集。不存在就当场建一个 —— 老配置、导入的文件、刚改过名的情况都会走到这儿。
        /// </summary>
        public StateGroup ActiveGroup
        {
            get
            {
                StateGroup g = FindGroup(ActiveGroupName);
                if (g != null) return g;

                g = new StateGroup(ActiveGroupName);
                Groups.Add(g);
                return g;
            }
        }

        public StateGroup FindGroup(string name)
        {
            string n = name ?? "";
            for (int i = 0; i < Groups.Count; i++)
            {
                StateGroup g = Groups[i];
                if (g != null && string.Equals(g.Name ?? "", n, StringComparison.OrdinalIgnoreCase))
                    return g;
            }
            return null;
        }

        /// <summary>全部状态集名，按出现顺序。默认组的名字是空串。</summary>
        public List<string> GroupNames()
        {
            var list = new List<string>();
            for (int i = 0; i < Groups.Count; i++)
            {
                StateGroup g = Groups[i];
                if (g == null) continue;
                if (!list.Contains(g.Name ?? "")) list.Add(g.Name ?? "");
            }
            return list;
        }

        /// <summary>找到或新建一个状态集。名字会被 Trim。</summary>
        public StateGroup EnsureGroup(string name)
        {
            string n = (name ?? "").Trim();
            StateGroup g = FindGroup(n);
            if (g != null) return g;

            g = new StateGroup(n);
            Groups.Add(g);
            return g;
        }

        /// <summary>
        /// 状态集改名。改完 ActiveGroupName 跟着走，不然切完名字列表就空了。
        /// 重名直接返回 false，由调用方提示。
        /// </summary>
        public bool RenameGroup(string oldName, string newName)
        {
            string o = oldName ?? "";
            string n = (newName ?? "").Trim();

            if (string.Equals(o, n, StringComparison.OrdinalIgnoreCase)) return true;

            StateGroup g = FindGroup(o);
            if (g == null) return false;
            if (FindGroup(n) != null) return false;

            g.Name = n;
            if (string.Equals(ActiveGroupName ?? "", o, StringComparison.OrdinalIgnoreCase))
                ActiveGroupName = n;

            return true;
        }

        /// <summary>
        /// 删掉一个状态集（连同里面的状态）。
        /// 最后一个不给删 —— 面板得有地方放状态；真不想要那些状态就一个个删。
        /// </summary>
        public bool RemoveGroup(string name)
        {
            if (Groups.Count <= 1) return false;

            string n = name ?? "";
            int i = -1;
            for (int k = 0; k < Groups.Count; k++)
            {
                StateGroup g = Groups[k];
                if (g != null && string.Equals(g.Name ?? "", n, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
            }
            if (i < 0) return false;

            Groups.RemoveAt(i);

            if (string.Equals(ActiveGroupName ?? "", n, StringComparison.OrdinalIgnoreCase))
                ActiveGroupName = Groups.Count > 0 ? (Groups[0].Name ?? "") : "";

            return true;
        }

        /// <summary>
        /// 图框库目录。空 = 用插件目录。
        /// 用户把图框 dwg 丢进这个目录，扫描后每个 dwg 变成一个图框模板。
        /// </summary>
        public string FrameLibDir = "";

        /// <summary>
        /// 进视口发现没有标签时，拿这个视口的冻结层去状态列表里找完全一致的状态，
        /// 自动套用那个状态的配置。默认开 —— 老视口没刷过标签也能自动切。
        /// </summary>
        public bool LayerFallback = true;

        static readonly FrameTemplate Empty = new FrameTemplate();

        /// <summary>当前图框。列表为空时返回一个只读用的空模板，不会写进配置。</summary>
        public FrameTemplate Frame
        {
            get
            {
                if (Frames.Count == 0) return Empty;
                if (FrameIndex < 0 || FrameIndex >= Frames.Count) FrameIndex = 0;
                return Frames[FrameIndex];
            }
        }

        /// <summary>当前图框；没有就新建一个并选中。写入图框属性前必须先调这个。</summary>
        public FrameTemplate CurrentOrNew()
        {
            if (Frames.Count == 0)
            {
                var f = new FrameTemplate();
                Frames.Add(f);
                FrameIndex = 0;
                return f;
            }
            if (FrameIndex < 0 || FrameIndex >= Frames.Count) FrameIndex = 0;
            return Frames[FrameIndex];
        }

        /// <summary>新增一个空图框并选中，返回它。</summary>
        public FrameTemplate AddFrame()
        {
            var f = new FrameTemplate();
            f.Label = "图框" + (Frames.Count + 1);
            Frames.Add(f);
            FrameIndex = Frames.Count - 1;
            return f;
        }

        public int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < States.Count; i++)
                if (string.Equals(States[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public VpState Find(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : States[i];
        }

        /// <summary>按图框库文件找已有的模板 —— 重新扫描时靠它认人，避免重复添加。</summary>
        public FrameTemplate FindByLibFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            for (int i = 0; i < Frames.Count; i++)
            {
                FrameTemplate f = Frames[i];
                if (f == null || string.IsNullOrEmpty(f.LibFile)) continue;
                if (string.Equals(f.LibFile, path, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        /// <summary>重名时自动加后缀：平面 → 平面2 → 平面3。</summary>
        public string UniqueName(string baseName)
        {
            string b = string.IsNullOrWhiteSpace(baseName) ? "状态" : baseName.Trim();
            if (Find(b) == null) return b;
            for (int i = 2; i < 1000; i++)
            {
                string n = b + i;
                if (Find(n) == null) return n;
            }
            return b + DateTime.Now.Ticks;
        }

        /// <summary>上移/下移一个状态。返回 true 表示确实动了。</summary>
        public bool Move(int index, int delta)
        {
            if (index < 0 || index >= States.Count) return false;
            int target = index + delta;
            if (target < 0 || target >= States.Count) return false;

            VpState t = States[index];
            States[index] = States[target];
            States[target] = t;
            return true;
        }
    }
}
