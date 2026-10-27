using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoLayer.Core
{
    /// <summary>
    /// 图层健康检查。关键实测结论：目标图层被冻结时，直接设置 Database.Clayer
    /// 会被 AutoCAD **静默忽略**（不抛异常），图元会落回原图层。
    /// 所以必须先解冻、再切层。
    /// </summary>
    public static class LayerService
    {
        /// <summary>AutoCAD 图层名不允许的字符。写进去会抛异常，必须在面板阶段就挡住。</summary>
        const string BadChars = "<>/\\\":;?*|,=`";

        /// <summary>
        /// 图层名是否合法。和 CAD 的策略保持一致：不合法就拒绝，绝不擅自改名。
        /// 不合法时 <c>LayerTable.Add</c> 会抛异常，异常被上层 catch 吞掉后
        /// 表现就是「画了线但没归层」，所以必须在进 CAD 之前就挡住。
        /// </summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.Trim();
            if (n.Length > 255) return false;
            foreach (char c in n)
                if (BadChars.IndexOf(c) >= 0) return false;
            return true;
        }

        public sealed class EnsureResult
        {
            public ObjectId Id = ObjectId.Null;
            public bool Ok;
            public readonly List<string> Notes = new List<string>();
        }

        public static EnsureResult Ensure(Database db, string layerName, Settings settings)
        {
            return Ensure(db, layerName, settings, LayerColorCode.Unset);
        }

        /// <summary>
        /// 保证图层可用。图层不存在时按 <paramref name="colorValue"/> 建；
        /// 没记住颜色（0）才退回 <see cref="Settings.NewLayerColorIndex"/>。
        /// </summary>
        public static EnsureResult Ensure(Database db, string layerName, Settings settings, int colorValue)
        {
            var res = new EnsureResult();
            if (db == null || string.IsNullOrWhiteSpace(layerName)) return res;

            string name = (layerName ?? "").Trim();
            if (name.Length == 0) return res;

            // 和 CAD 保持一致：不认识的字符直接拒绝，不擅自改名
            if (!IsValidName(name))
            {
                res.Notes.Add("图层名「" + name + "」含 CAD 不允许的字符，已跳过归层");
                return res;
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId id;

                if (!lt.Has(name))
                {
                    if (settings == null || !settings.AutoCreateLayer) return res;
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord
                    {
                        Name = name,
                        Color = BuildColor(colorValue, settings)
                    };
                    id = lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    res.Notes.Add("新建图层 " + name + "（" + DescribeColor(colorValue, settings) + "）");
                }
                else
                {
                    id = lt[name];
                }

                var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                bool thaw = rec.IsFrozen && (settings == null || settings.AutoThaw);
                bool on = rec.IsOff && (settings == null || settings.AutoTurnOn);
                bool unlock = rec.IsLocked && (settings == null || settings.AutoUnlock);

                if (thaw || on || unlock)
                {
                    rec.UpgradeOpen();
                    if (thaw) { rec.IsFrozen = false; res.Notes.Add("已解冻 " + name); }
                    if (on) { rec.IsOff = false; res.Notes.Add("已打开 " + name); }
                    if (unlock) { rec.IsLocked = false; res.Notes.Add("已解锁 " + name); }
                }

                tr.Commit();
                res.Id = id;
                res.Ok = true;
            }
            return res;
        }

        static Color BuildColor(int colorValue, Settings settings)
        {
            if (LayerColorCode.IsRgb(colorValue))
                return Color.FromRgb(LayerColorCode.R(colorValue),
                                     LayerColorCode.G(colorValue),
                                     LayerColorCode.B(colorValue));

            if (LayerColorCode.IsAci(colorValue))
                return Color.FromColorIndex(ColorMethod.ByAci, (short)colorValue);

            short fallback = (settings == null || settings.NewLayerColorIndex <= 0)
                ? (short)7 : settings.NewLayerColorIndex;
            return Color.FromColorIndex(ColorMethod.ByAci, fallback);
        }

        /// <summary>给命令行/面板看的一句话说明，方便确认"到底按什么色建的"。</summary>
        public static string DescribeColor(int colorValue, Settings settings)
        {
            if (LayerColorCode.IsRgb(colorValue))
                return "真彩色 " + LayerColorCode.R(colorValue) + ","
                     + LayerColorCode.G(colorValue) + "," + LayerColorCode.B(colorValue);
            if (LayerColorCode.IsAci(colorValue))
                return "索引色 " + colorValue;
            short fallback = (settings == null || settings.NewLayerColorIndex <= 0)
                ? (short)7 : settings.NewLayerColorIndex;
            return "默认索引色 " + fallback;
        }

        /// <summary>
        /// 读图纸里某个图层当前的颜色。图层不存在返回 false ——
        /// 调用方要据此保留配置里已经记住的旧颜色，不能拿"读不到"去覆盖它。
        /// </summary>
        public static bool TryGetLayerColor(Database db, string layerName, out int colorValue)
        {
            colorValue = LayerColorCode.Unset;
            if (db == null || string.IsNullOrWhiteSpace(layerName)) return false;

            string name = layerName.Trim();
            if (!IsValidName(name)) return false;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(name)) { tr.Commit(); return false; }

                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                    colorValue = ReadColor(ltr);
                    tr.Commit();
                    return colorValue != LayerColorCode.Unset;
                }
            }
            catch { return false; }
        }

        static int ReadColor(LayerTableRecord ltr)
        {
            Color c = ltr == null ? null : ltr.Color;
            if (c == null) return LayerColorCode.Unset;

            // 图层颜色只可能是 ACI 或真彩色；ByLayer/ByBlock 在图层上不成立，
            // 真出现了就按它的索引色兜底。
            if (c.ColorMethod == ColorMethod.ByColor)
                return LayerColorCode.FromRgb(c.Red, c.Green, c.Blue);

            return LayerColorCode.FromAci(c.ColorIndex);
        }

        public static string NameOf(Database db, ObjectId id)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    string n = ltr.Name;
                    tr.Commit();
                    return n;
                }
            }
            catch { return ""; }
        }

        public static List<string> AllNames(Database db)
        {
            var names = new List<string>();
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in lt)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        names.Add(ltr.Name);
                    }
                    tr.Commit();
                }
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>图层状态摘要，供面板显示"冻结/关闭/缺失"。</summary>
        public static string StateOf(Database db, string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return "未设置";
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(layerName.Trim())) { tr.Commit(); return "不存在"; }
                    var ltr = (LayerTableRecord)tr.GetObject(lt[layerName.Trim()], OpenMode.ForRead);
                    string s = "正常";
                    if (ltr.IsFrozen) s = "已冻结";
                    else if (ltr.IsOff) s = "已关闭";
                    else if (ltr.IsLocked) s = "已锁定";
                    tr.Commit();
                    return s;
                }
            }
            catch { return "?"; }
        }
    }
}
