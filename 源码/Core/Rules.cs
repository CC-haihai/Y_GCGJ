using System;
using System.Collections.Generic;

namespace AutoLayer.Core
{
    /// <summary>
    /// 类别 → 命令 的静态映射。用户只需要配「类别 → 图层」，不用记命令名。
    /// </summary>
    public static class Categories
    {
        public const string Line = "线段";
        public const string Text = "文字";
        public const string Dim = "标注";
        public const string Hatch = "填充";
        public const string Leader = "引线";
        public const string Block = "块";

        /// <summary>
        /// 视口：布局（图纸空间）里 MVIEW / VPORTS 建出来的浮视口。
        ///
        /// 为什么单列一类而不是并进「线段」：视口框和墙线虽然都叫"框"，
        /// 但视口框十有八九**不该打印**，实际做法是单独给它一个不打印的图层。
        /// 并进线段的话，一旦线段那池切到了打印层，视口框就跟着被打出来了。
        /// </summary>
        public const string Viewport = "视口";

        /// <summary>面板里按这个顺序展示。视口放最后 —— 它是布局里的事，模型空间用不上。</summary>
        public static readonly string[] All =
        {
            Line, Text, Dim, Hatch, Leader, Block, Viewport
        };

        static readonly Dictionary<string, string> CommandToCategory =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static Categories()
        {
            // 直线、圆、圆弧、样条、矩形、多边形全部归「线段」。
            // 施工图里它们几乎总是同一图层，单独分「矩形」那一类没人用得上。
            Add(Line, "LINE", "PLINE", "3DPOLY", "XLINE", "RAY", "MLINE", "SKETCH",
                      "CIRCLE", "ARC", "ELLIPSE", "SPLINE", "DONUT", "HELIX", "REVCLOUD",
                      "RECTANG", "POLYGON");
            Add(Text, "TEXT", "DTEXT", "MTEXT", "ARCTEXT");
            Add(Dim, "DIM", "DIMLINEAR", "DIMALIGNED", "DIMANGULAR", "DIMDIAMETER", "DIMRADIUS",
                     "DIMORDINATE", "DIMBASELINE", "DIMCONTINUE", "DIMJOGGED", "DIMARC", "QDIM", "TOLERANCE");
            Add(Hatch, "HATCH", "BHATCH", "GRADIENT", "SOLID");
            Add(Leader, "LEADER", "QLEADER", "MLEADER", "MULTILEADER");
            Add(Block, "INSERT", "CLASSICINSERT");
            // 视口：MVIEW 建单个浮视口，VPORTS 在布局里建多个、在模型空间是"视口配置"
            // （那条切了层也会还原，没有副作用）。SOLVIEW 是老的实体/视图命令，一起收。
            // 注意 Normalize 会把 "-VPORTS" 剥成 "VPORTS"，所以不用另列一遍。
            Add(Viewport, "MVIEW", "VPORTS", "SOLVIEW");
        }

        static void Add(string category, params string[] commands)
        {
            foreach (string c in commands) CommandToCategory[c] = category;
        }

        public static string Of(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string cat;
            return CommandToCategory.TryGetValue(command, out cat) ? cat : null;
        }

        public static bool IsWatched(string command)
        {
            return Of(command) != null;
        }
    }

    public sealed class Resolution
    {
        public string Layer = "";
        public string Category = "";
        public string Source = "";   // "override" | "category"
        /// <summary>配置里记住的颜色，用于该图层不存在时的自动新建。0 = 没记录。</summary>
        public int ColorValue = LayerColorCode.Unset;
    }

    /// <summary>
    /// 解析顺序：单命令覆盖 &gt; 类别映射 &gt; 不命中（原样放过）。
    /// 不命中一律返回 false，调用方直接退出，绝不"猜"一个图层。
    /// </summary>
    public static class RuleResolver
    {
        public static bool TryResolve(Profile profile, string command, out Resolution result)
        {
            result = null;
            if (profile == null || string.IsNullOrEmpty(command)) return false;

            if (profile.Overrides != null)
            {
                foreach (CommandOverride o in profile.Overrides)
                {
                    if (o == null || string.IsNullOrEmpty(o.Command)) continue;
                    if (!string.Equals(o.Command, command, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(o.Layer)) return false;
                    result = new Resolution
                    {
                        Layer = o.Layer.Trim(),
                        Category = Categories.Of(command) ?? "",
                        Source = "override",
                        ColorValue = LayerColorCode.Unset   // 单命令覆盖不记颜色，走全局默认色
                    };
                    return true;
                }
            }

            string category = Categories.Of(command);
            if (category == null) return false;

            string layer = profile.LayerFor(category);
            if (string.IsNullOrEmpty(layer) || layer.Trim().Length == 0) return false;

            result = new Resolution
            {
                Layer = layer.Trim(),
                Category = category,
                Source = "category",
                ColorValue = profile.LayerColorFor(category)
            };
            return true;
        }
    }
}
