using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoLayer.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AutoLayer.TestKit.Tests))]

namespace AutoLayer.TestKit
{
    internal sealed class CaseResult
    {
        public string Name;
        public bool Passed;
        public string Detail = "";
        public readonly List<string> Failures = new List<string>();
    }

    /// <summary>
    /// 归层引擎的无人值守测试。只引用 acdbmgd + accoremgd，可在 accoreconsole 里跑。
    /// 驱动的是真实命令（LINE / DIMLINEAR / TEXT），走的是和用户完全相同的代码路径。
    /// </summary>
    public class Tests
    {
        const string Tag = "[ALTEST]";

        const string L_LINE = "T-LINE";
        const string L_DIM = "T-DIM";
        const string L_TEXT = "T-TEXT";
        const string L_FROZEN = "T-FROZEN";
        const string L_OFF = "T-OFF";
        const string L_OVR = "T-OVR";
        const string L_LOCKED = "T-LOCK";
        const string L_MISSING = "T-NOT-EXIST";
        const string L_VIEW = "T-VIEW";

        static readonly List<CaseResult> Cases = new List<CaseResult>();
        static CaseResult _cur;

        /// <summary>
        /// 诊断用：把 ACI 1..255 的真实 RGB 打出来。
        /// 面板要显示色块，颜色必须和 CAD 里看到的一致，不能自己拍一张表。
        /// </summary>
        [CommandMethod("ALPALETTE")]
        public void DumpPalette()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var sb = new StringBuilder();
            for (short i = 1; i <= 255; i++)
            {
                try
                {
                    Color c = Color.FromColorIndex(ColorMethod.ByAci, i);
                    sb.Append("[PAL] ").Append(i).Append(" ")
                      .Append(c.Red).Append(",").Append(c.Green).Append(",").Append(c.Blue)
                      .Append("\n");
                }
                catch (System.Exception ex)
                {
                    sb.Append("[PAL] ").Append(i).Append(" ERR ").Append(ex.GetType().Name).Append("\n");
                }
            }
            ed.WriteMessage("\n" + sb.ToString());
        }

        [CommandMethod("ALTEST")]
        public void RunAll()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { Console.WriteLine(Tag + " 无活动文档，无法测试"); return; }
            Editor ed = doc.Editor;
            Database db = doc.Database;

            Cases.Clear();
            W(ed, Tag + " begin acadver=" + Ver());

            PrepareLayers(db);

            Case("01_disabled_does_not_touch", () => DisabledUntouched(ed, db));
            Case("02_line_goes_to_target", () => LineGoesToTarget(ed, db));
            Case("03_clayer_restored", () => ClayerRestored(ed, db));
            Case("04_dimension", () => DimensionGoesToTarget(ed, db));
            Case("05_text", () => TextGoesToTarget(ed, db));
            Case("06_other_commands_untouched", () => OtherCommandsUntouched(ed, db));
            Case("07_frozen_layer_autothaw", () => FrozenLayerAutoThaw(ed, db));
            Case("08_off_layer_autoon", () => OffLayerAutoOn(ed, db));
            Case("09_missing_layer_skipped", () => MissingLayerSkipped(ed, db));
            Case("10_layout_space", () => LayoutSpace(ed, db));
            Case("11_command_override", () => CommandOverride(ed, db));
            Case("12_rule_logic", RuleLogic);
            Case("13_config_roundtrip", ConfigRoundTrip);
            Case("14_profile_rename", ProfileRename);
            Case("15_locked_layer_autounlock", () => LockedLayerAutoUnlock(ed, db));
            Case("16_default_profile_zero", DefaultZeroAndMigrate);
            Case("17_layer_name_validation", () => LayerNameValidation(db));
            Case("18_new_layer_color", () => NewLayerColor(db));
            Case("19_auto_create_color_e2e", () => AutoCreateColorEndToEnd(ed, db));
            Case("20_text_parse_robust", TextParseRobust);
            Case("21_layer_pool", LayerPool);
            Case("22_hotkey_and_trigger", HotkeyAndTrigger);
            Case("23_viewport_category", () => ViewportCategory(ed, db));
            Case("24_profile_switch", ProfileSwitch);
            Case("25_profile_reorder", ProfileReorder);

            int pass = Cases.Count(c => c.Passed);
            W(ed, Tag + " total=" + Cases.Count + " passed=" + pass + " failed=" + (Cases.Count - pass));
            foreach (CaseResult c in Cases)
                W(ed, Tag + " " + (c.Passed ? "PASS" : "FAIL") + " " + c.Name
                      + (c.Detail.Length > 0 ? " :: " + c.Detail : ""));
            foreach (CaseResult c in Cases)
                foreach (string f in c.Failures) W(ed, Tag + "   - " + c.Name + " → " + f);
        }

        // ------------------------------------------------------------------ cases

        static void DisabledUntouched(Editor ed, Database db)
        {
            UseConfig(false, L_LINE);
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 0, 0), new Point3d(1000, 0, 0), "");
            Check(got == "Line@0", "期望 Line@0，实际 " + got);
            Check(Clayer(db) == "0", "当前图层不应变化，实际 " + Clayer(db));
            _cur.Detail = got;
        }

        static void LineGoesToTarget(Editor ed, Database db)
        {
            UseConfig(true, L_LINE);
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 100, 0), new Point3d(1000, 100, 0), "");
            Check(got == "Line@" + L_LINE, "期望 Line@" + L_LINE + "，实际 " + got);
            _cur.Detail = got;
        }

        static void ClayerRestored(Editor ed, Database db)
        {
            UseConfig(true, L_LINE);
            Reset(ed, db);
            Draw(ed, db, "_.LINE", new Point3d(0, 200, 0), new Point3d(1000, 200, 0), "");
            Check(Clayer(db) == "0", "命令结束后当前图层应还原为 0，实际 " + Clayer(db));
            _cur.Detail = "clayer=" + Clayer(db);
        }

        static void DimensionGoesToTarget(Editor ed, Database db)
        {
            UseConfig(true, L_LINE);
            Reset(ed, db);
            string got = Draw(ed, db, "_.DIMLINEAR",
                              new Point3d(0, 300, 0), new Point3d(1000, 300, 0), new Point3d(500, 400, 0));
            Check(got.EndsWith("@" + L_DIM), "期望 *@" + L_DIM + "，实际 " + got);
            Check(Clayer(db) == "0", "当前图层应还原，实际 " + Clayer(db));
            _cur.Detail = got;
        }

        static void TextGoesToTarget(Editor ed, Database db)
        {
            UseConfig(true, L_LINE);
            Reset(ed, db);
            string got = Draw(ed, db, "_.TEXT", new Point3d(0, 500, 0), 25.0, 0.0, "ALTEST");
            Check(got == "DBText@" + L_TEXT, "期望 DBText@" + L_TEXT + "，实际 " + got);
            _cur.Detail = got;
        }

        static void OtherCommandsUntouched(Editor ed, Database db)
        {
            UseConfig(true, L_LINE);
            Reset(ed, db);
            ed.Command("_.DIST", new Point3d(0, 0, 0), new Point3d(500, 0, 0));
            Check(Clayer(db) == "0", "非归层命令不应改动当前图层，实际 " + Clayer(db));
            _cur.Detail = "DIST 后 clayer=" + Clayer(db);
        }

        static void FrozenLayerAutoThaw(Editor ed, Database db)
        {
            UseConfig(true, L_FROZEN);
            SetLayerState(db, L_FROZEN, frozen: true, off: false);
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 600, 0), new Point3d(1000, 600, 0), "");
            Check(got == "Line@" + L_FROZEN, "冻结图层应先解冻再归层，实际 " + got);
            Check(!IsFrozen(db, L_FROZEN), "图层应已被解冻");
            Check(Clayer(db) == "0", "当前图层应还原，实际 " + Clayer(db));
            _cur.Detail = got;
        }

        static void OffLayerAutoOn(Editor ed, Database db)
        {
            UseConfig(true, L_OFF);
            SetLayerState(db, L_OFF, frozen: false, off: true);
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 700, 0), new Point3d(1000, 700, 0), "");
            Check(got == "Line@" + L_OFF, "关闭图层应先打开再归层，实际 " + got);
            Check(!IsOff(db, L_OFF), "图层应已被打开");
            _cur.Detail = got;
        }

        static void LockedLayerAutoUnlock(Editor ed, Database db)
        {
            UseConfig(true, L_LOCKED);
            SetLayerLocked(db, L_LOCKED, true);
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 900, 0), new Point3d(1000, 900, 0), "");
            Check(got == "Line@" + L_LOCKED, "锁定图层应先解锁再归层，实际 " + got);
            Check(!IsLocked(db, L_LOCKED), "图层应已被解锁");
            Check(Clayer(db) == "0", "当前图层应还原，实际 " + Clayer(db));
            _cur.Detail = got;
        }

        static void MissingLayerSkipped(Editor ed, Database db)        {
            UseConfig(true, L_MISSING, autoCreate: false);
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 800, 0), new Point3d(1000, 800, 0), "");
            Check(got == "Line@0", "图层不存在且未开自动新建时应原样放过，实际 " + got);
            Check(Clayer(db) == "0", "当前图层不应变化，实际 " + Clayer(db));
            _cur.Detail = got;
        }

        static void LayoutSpace(Editor ed, Database db)
        {
            UseConfig(true, L_LINE);
            string layout = FirstLayoutName(db);
            if (layout == null) { _cur.Detail = "无布局，跳过"; return; }

            Reset(ed, db);
            ed.Command("_.CTAB", layout);
            try
            {
                string got = Draw(ed, db, "_.LINE", new Point3d(10, 10, 0), new Point3d(110, 10, 0), "");
                Check(got == "Line@" + L_LINE, "布局空间里也应归层，实际 " + got);
                _cur.Detail = layout + " → " + got;
            }
            finally
            {
                try { ed.Command("_.CTAB", "Model"); } catch { }
            }
        }

        /// <summary>
        /// 视口（1.11 新增的第 7 类）：布局里 MVIEW 建的浮视口要落到「视口」配的图层。
        /// 只验证命令拦截这一层 —— 图元是 CAD 自己建的，插件只负责把当前图层切过去。
        /// </summary>
        static void ViewportCategory(Editor ed, Database db)
        {
            Config cfg = UseConfig(true, L_LINE);
            cfg.Active.SetLayer(Categories.Viewport, L_VIEW);
            ConfigStore.SetCurrent(cfg);

            string layout = FirstLayoutName(db);
            if (layout == null) { _cur.Detail = "无布局，跳过"; return; }

            Reset(ed, db);
            ed.Command("_.CTAB", layout);
            try
            {
                // MVIEW 建完一个视口还会继续问下一个角点，末尾那个 "" 是回车收尾。
                // 少了它命令会一直挂着，后面的用例全被它吃掉。
                string got = Draw(ed, db, "_.MVIEW",
                                  new Point3d(20, 20, 0), new Point3d(180, 140, 0), "");
                Check(got.IndexOf("Viewport@" + L_VIEW, StringComparison.Ordinal) >= 0,
                      "MVIEW 建的视口应落在「视口」层 " + L_VIEW + "，实际 " + got);
                Check(Clayer(db) == "0", "MVIEW 结束后当前图层应还原，实际 " + Clayer(db));
                _cur.Detail = layout + " → " + got;
            }
            finally
            {
                try { ed.Command("_.CTAB", "Model"); } catch { }
            }
        }

        /// <summary>
        /// 状态条上点配置名 → 弹出清单 → 选一条切过去（1.13）。这里测引擎那一半：
        /// 能不能按名字切、存不存盘、选的是当前那个 / 名字不存在时会不会乱动。
        /// 清单的弹出、点击、"只有配置名可点"的命中测试在 uiprobe 里测。
        ///
        /// （1.13 最初做的是"点一下轮换到下一个"，用户否掉了：
        ///   「不是点击轮换，而是点击展开我可以选择哪一个配置」。
        ///   所以这里测的是 SwitchProfile，没有 CycleProfile 了。）
        /// </summary>
        static void ProfileSwitch()
        {
            // ---- 1. 只有一个配置：选它就是"没变化"，不该返回 true ----
            var solo = new Config { ActiveProfile = "甲", Settings = new Settings() };
            solo.Profiles.Add(new Profile { Name = "甲" });
            ConfigStore.SetCurrent(solo);
            Check(!ConfigStore.SwitchProfile("甲"), "选的就是当前配置时应返回 false");
            Check(solo.ActiveProfile == "甲", "没变化时当前配置不应变，实际 " + solo.ActiveProfile);

            // ---- 2. 三个配置：选哪个是哪个（不是"下一个"）----
            var cfg = new Config { ActiveProfile = "甲", Settings = new Settings() };
            cfg.Profiles.Add(new Profile { Name = "甲" });
            cfg.Profiles.Add(new Profile { Name = "乙" });
            cfg.Profiles.Add(new Profile { Name = "丙" });
            ConfigStore.SetCurrent(cfg);

            Check(ConfigStore.SwitchProfile("丙"), "甲 → 丙 应切换成功");
            Check(cfg.ActiveProfile == "丙", "切完当前配置应是 丙，实际 " + cfg.ActiveProfile);
            Check(ConfigStore.Current.ActiveProfile == "丙", "切完的当前配置应落在 Current 上");

            Check(ConfigStore.SwitchProfile("甲"), "丙 → 甲 应切换成功");
            Check(cfg.ActiveProfile == "甲", "切完当前配置应是 甲，实际 " + cfg.ActiveProfile);

            Check(!ConfigStore.SwitchProfile("甲"), "再选一次 甲 应返回 false（已经是它）");

            // ---- 3. 名字不存在 / 空名字：老老实实拒绝，不能把当前配置弄没 ----
            Check(!ConfigStore.SwitchProfile("不存在的"), "配置名不存在时应返回 false");
            Check(cfg.ActiveProfile == "甲", "切失败时当前配置不应变，实际 " + cfg.ActiveProfile);
            Check(!ConfigStore.SwitchProfile(""), "空名字应返回 false");
            Check(!ConfigStore.SwitchProfile(null), "null 应返回 false");
            Check(cfg.ActiveProfile == "甲", "空名字之后当前配置仍应是 甲，实际 " + cfg.ActiveProfile);

            // ---- 4. 清单里拿到的名字：按 Profiles 顺序、不含空名字 ----
            var dirty = new Config { ActiveProfile = "甲", Settings = new Settings() };
            dirty.Profiles.Add(new Profile { Name = "甲" });
            dirty.Profiles.Add(new Profile { Name = "" });      // 手改 ini 能改出来
            dirty.Profiles.Add(null);
            dirty.Profiles.Add(new Profile { Name = "丁" });
            ConfigStore.SetCurrent(dirty);

            var names = ConfigStore.ProfileNames();
            Check(names.Count == 2, "清单里应只有 2 个名字，实际 " + names.Count);
            Check(names.Count == 2 && names[0] == "甲" && names[1] == "丁",
                  "清单顺序应与配置顺序一致，实际 " + string.Join("/", names.ToArray()));

            _cur.Detail = "switch: 甲/丙/甲 + 同名拒绝 + 空名字拒绝 + 清单过滤";
        }

        /// <summary>
        /// 配置上移 / 下移（1.13）。用户："配置的顺序好像没法自己改位置"。
        /// 顺序决定面板下拉框和状态条那份清单的排列，所以只测"挪没挪对"和边界：
        /// 到顶不能再上移、到底不能再下移、挪完还在用原来那个配置。
        /// </summary>
        static void ProfileReorder()
        {
            var cfg = new Config { ActiveProfile = "乙", Settings = new Settings() };
            cfg.Profiles.Add(new Profile { Name = "甲" });
            cfg.Profiles.Add(new Profile { Name = "乙" });
            cfg.Profiles.Add(new Profile { Name = "丙" });
            ConfigStore.SetCurrent(cfg);

            // 丙 上移 → 甲 / 丙 / 乙
            Check(cfg.MoveProfile("丙", -1), "丙 上移应成功");
            Check(Order(cfg) == "甲/丙/乙", "丙 上移后顺序应是 甲/丙/乙，实际 " + Order(cfg));
            Check(cfg.ActiveProfile == "乙", "挪顺序不该改当前配置，实际 " + cfg.ActiveProfile);

            // 甲 下移 → 丙 / 甲 / 乙
            Check(cfg.MoveProfile("甲", +1), "甲 下移应成功");
            Check(Order(cfg) == "丙/甲/乙", "甲 下移后顺序应是 丙/甲/乙，实际 " + Order(cfg));

            // 边界：第一个不能再上移，最后一个不能再下移
            Check(!cfg.MoveProfile("丙", -1), "第一个配置不能再上移");
            Check(!cfg.MoveProfile("乙", +1), "最后一个配置不能再下移");
            Check(Order(cfg) == "丙/甲/乙", "边界被拒后顺序不该变，实际 " + Order(cfg));

            // 名字不存在 / 空 / null / delta=0
            Check(!cfg.MoveProfile("不存在", -1), "名字不存在应返回 false");
            Check(!cfg.MoveProfile("", +1), "空名字应返回 false");
            Check(!cfg.MoveProfile(null, +1), "null 应返回 false");
            Check(!cfg.MoveProfile("甲", 0), "delta=0 应返回 false");
            Check(Order(cfg) == "丙/甲/乙", "无效调用之后顺序仍应是 丙/甲/乙，实际 " + Order(cfg));

            // 只有一个配置：挪不动
            var solo = new Config { ActiveProfile = "独", Settings = new Settings() };
            solo.Profiles.Add(new Profile { Name = "独" });
            Check(!solo.MoveProfile("独", -1) && !solo.MoveProfile("独", +1),
                  "只有一个配置时两个方向都不该挪动");

            // ---- 拖拽排序走的是 MoveProfileTo：拖到第几条就落到第几条 ----
            var drag = new Config { ActiveProfile = "乙", Settings = new Settings() };
            drag.Profiles.Add(new Profile { Name = "甲" });
            drag.Profiles.Add(new Profile { Name = "乙" });
            drag.Profiles.Add(new Profile { Name = "丙" });
            ConfigStore.SetCurrent(drag);

            Check(drag.MoveProfileTo("甲", 2), "甲 拖到最后应成功");
            Check(Order(drag) == "乙/丙/甲", "拖完应是 乙/丙/甲，实际 " + Order(drag));
            Check(drag.ActiveProfile == "乙", "拖顺序不该换当前配置，实际 " + drag.ActiveProfile);

            Check(!drag.MoveProfileTo("甲", 2), "原地放下应返回 false");
            Check(!drag.MoveProfileTo("甲", 9), "越界应返回 false");
            Check(!drag.MoveProfileTo("甲", -1), "负索引应返回 false");
            Check(!drag.MoveProfileTo("不存在", 0), "名字不存在应返回 false");
            Check(Order(drag) == "乙/丙/甲", "无效拖放后顺序不该变，实际 " + Order(drag));

            Check(drag.IndexOfProfile("丙") == 1, "丙 应在第 2 位，实际 " + drag.IndexOfProfile("丙"));
            Check(drag.IndexOfProfile("不存在") == -1, "找不到的名字应返回 -1");

            _cur.Detail = "reorder: 丙↑/甲↓ 边界拒绝 + 拖拽 MoveProfileTo 甲→末位 乙/丙/甲";
        }

        static string Order(Config cfg)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Profile p in cfg.Profiles)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(p == null ? "<null>" : p.Name);
            }
            return sb.ToString();
        }

        static void CommandOverride(Editor ed, Database db)
        {
            Config cfg = UseConfig(true, L_LINE);
            cfg.Active.Overrides.Add(new CommandOverride { Command = "LINE", Layer = L_OVR });
            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 900, 0), new Point3d(1000, 900, 0), "");
            Check(got == "Line@" + L_OVR, "单命令覆盖应优先于类别映射，实际 " + got);
            _cur.Detail = got;
        }

        static void RuleLogic()
        {
            var p = new Profile { Name = "logic" };
            p.SetLayer(Categories.Line, "A");
            p.SetLayer(Categories.Hatch, "H");
            p.SetLayer(Categories.Viewport, "V");

            Check(Categories.Of("LINE") == Categories.Line, "LINE 应归入线段");
            Check(Categories.Of("PLINE") == Categories.Line, "PLINE 应归入线段");
            Check(Categories.Of("ARC") == Categories.Line, "ARC 应与线段同类");
            Check(Categories.Of("CIRCLE") == Categories.Line, "CIRCLE 应与线段同类");
            // 2026-10 起「矩形」不再单独成类：施工图里矩形和线段本来就是同一类线条
            Check(Categories.Of("RECTANG") == Categories.Line, "RECTANG 应并入线段");
            Check(Categories.Of("POLYGON") == Categories.Line, "POLYGON 应并入线段");
            Check(Categories.Of("HATCH") == Categories.Hatch, "HATCH 应归入填充");
            // 1.11 新增：视口单独一类（布局里的浮视口通常要用不打印的图层）
            Check(Categories.Of("MVIEW") == Categories.Viewport, "MVIEW 应归入视口");
            Check(Categories.Of("VPORTS") == Categories.Viewport, "VPORTS 应归入视口");
            Check(Categories.Of("SOLVIEW") == Categories.Viewport, "SOLVIEW 应归入视口");
            Check(CommandHook.Normalize("-VPORTS") == "VPORTS", "-VPORTS 应归一化成 VPORTS（视口靠它命中）");
            Check(Categories.Of("POINT") == null, "「点」类别已取消，POINT 不应被拦截");
            Check(Categories.Of("COPY") == null, "COPY 不应被拦截");
            Check(Categories.Of("MOVE") == null, "MOVE 不应被拦截");
            Check(Categories.Of("OFFSET") == null, "OFFSET 不应被拦截");
            Check(Categories.All.Length == 7, "类别应为 7 个，实际 " + Categories.All.Length);

            Check(CommandHook.Normalize("-HATCH") == "HATCH", "-HATCH 应归一化为 HATCH");
            Check(CommandHook.Normalize("_.LINE") == "LINE", "_.LINE 应归一化为 LINE");
            Check(CommandHook.Normalize("'ZOOM") == "ZOOM", "'ZOOM 应归一化为 ZOOM");

            Resolution r;
            Check(RuleResolver.TryResolve(p, "LINE", out r) && r.Layer == "A", "LINE 应解析到 A");
            Check(RuleResolver.TryResolve(p, "ARC", out r) && r.Layer == "A", "ARC 应与线段同层");
            Check(RuleResolver.TryResolve(p, "HATCH", out r) && r.Layer == "H", "HATCH 应解析到 H");
            Check(RuleResolver.TryResolve(p, "RECTANG", out r) && r.Layer == "A",
                  "RECTANG 已并入线段，应解析到 A");
            Check(RuleResolver.TryResolve(p, "MVIEW", out r) && r.Layer == "V", "MVIEW 应解析到视口层 V");
            Check(!RuleResolver.TryResolve(p, "COPY", out r), "未监听的命令不应解析");

            _cur.Detail = "categories/normalize/resolve ok";
        }

        /// <summary>
        /// 多图层池 + 旧配置迁移 —— 这一版的核心新特性。
        /// 一个类别可以配多个图层，靠 Active 记住"下一条命令用哪个"，
        /// 而且必须能从纯文本里原样存回来（用户会手改 ini）。
        /// </summary>
        static void LayerPool()
        {
            // ---- 1. 文本往返：多个图层 + 记住用第几个 ----
            var cfg = new Config { ActiveProfile = "甲", Settings = new Settings() };
            var p = new Profile { Name = "甲" };

            var line = new List<string>();
            line.Add("E-04 造型线");
            line.Add("E-05 辅助线");
            line.Add("E-01 结构");
            p.SetLayers(Categories.Line, line);
            p.SetActiveIndex(Categories.Line, 1);
            p.SetLayerColor(Categories.Line, LayerColorCode.FromAci(5));
            p.SetLayer(Categories.Text, "E-10 文字");
            cfg.Profiles.Add(p);

            Check(p.LayerCount(Categories.Line) == 3, "线段池应有 3 个，实际 " + p.LayerCount(Categories.Line));
            Check(p.LayerFor(Categories.Line) == "E-05 辅助线",
                  "当前层应是第 2 个，实际 " + p.LayerFor(Categories.Line));

            string text = ConfigStore.ToText(cfg);
            // 颜色跟着各自的图层写在同一段里：名字 | 色号
            Check(text.Contains("线段 = E-04 造型线 ; E-05 辅助线 | 5 ; E-01 结构"),
                  "文本里应写出整池（颜色跟在各自图层后面），实际:\n" + text);
            Check(text.Contains("当前层.线段 = 2"), "应记住当前用第 2 个");

            Config back = ConfigStore.FromText(text);
            Profile q = back == null ? null : back.FindProfile("甲");
            Check(q != null, "往返后应能找到配置");
            if (q != null)
            {
                Check(q.LayerCount(Categories.Line) == 3,
                      "往返后池大小应不变，实际 " + q.LayerCount(Categories.Line));
                Check(q.LayerFor(Categories.Line) == "E-05 辅助线",
                      "往返后当前层应保持第 2 个，实际 " + q.LayerFor(Categories.Line));
                Check(q.ActiveIndexOf(Categories.Line) == 1, "往返后 Active 应为 1");
                Check(q.LayerColorFor(Categories.Line) == 5, "颜色应跟着当前那个图层走");
                Check(q.LayerColorFor(Categories.Text) == LayerColorCode.Unset, "没记颜色应为 Unset");
            }

            // ---- 1b. 每个图层各记各的颜色（1.6：颜色列撤了，改成 chip 上逐层改） ----
            p.SetLayerColorAt(Categories.Line, 0, LayerColorCode.FromAci(1));
            p.SetLayerColorAt(Categories.Line, 2, LayerColorCode.FromAci(3));
            Check(p.LayerColorAt(Categories.Line, 0) == 1, "第 1 个图层应是 1 号色");
            Check(p.LayerColorAt(Categories.Line, 1) == 5, "第 2 个图层应还是 5 号色（没被邻居带跑）");
            Check(p.LayerColorAt(Categories.Line, 2) == 3, "第 3 个图层应是 3 号色");
            Check(p.LayerColorAt(Categories.Line, 9) == LayerColorCode.Unset, "越界应返回 Unset");
            p.SetLayerColorAt(Categories.Line, 9, LayerColorCode.FromAci(6));   // 不该炸，也不该改到别人

            string text2 = ConfigStore.ToText(cfg);
            Check(text2.Contains("E-04 造型线 | 1 ; E-05 辅助线 | 5 ; E-01 结构 | 3"),
                  "三个图层各自的颜色应分别写出来，实际:\n" + text2);
            Config back2 = ConfigStore.FromText(text2);
            Profile q2 = back2 == null ? null : back2.FindProfile("甲");
            Check(q2 != null && q2.LayerColorAt(Categories.Line, 0) == 1
                            && q2.LayerColorAt(Categories.Line, 2) == 3,
                  "逐层颜色应能往返");

            // ---- 1c. 拖动排序：顺序变了，但"当前用的是哪一层"必须还指着同一个图层 ----
            // 面板里拖 chip 最终走的就是 SetLayerRefs 这条路（名字换位置、颜色跟着名字走）。
            // 这里要盯住的是：拖完以后 Active 得**跟着名字挪**，而不是死守着原来的下标 ——
            // 死守下标的话，用户拖一下就会发现自己用的层被悄悄换掉了。
            p.SetActiveIndex(Categories.Line, 0);          // 现在用「E-04 造型线」（第 1 个）
            var moved = new List<LayerRef>();
            moved.Add(new LayerRef("E-01 结构", LayerColorCode.FromAci(3)));
            moved.Add(new LayerRef("E-04 造型线", LayerColorCode.FromAci(1)));
            moved.Add(new LayerRef("E-05 辅助线", LayerColorCode.FromAci(5)));
            p.SetLayerRefs(Categories.Line, moved);

            Check(p.ActiveIndexOf(Categories.Line) == 1,
                  "拖到第 2 位后 Active 应跟到 1，实际 " + p.ActiveIndexOf(Categories.Line));
            Check(p.LayerFor(Categories.Line) == "E-04 造型线",
                  "拖完当前层还应是「E-04 造型线」，实际 " + p.LayerFor(Categories.Line));

            string text3 = ConfigStore.ToText(cfg);
            Check(text3.Contains("线段 = E-01 结构 | 3 ; E-04 造型线 | 1 ; E-05 辅助线 | 5"),
                  "ini 里的先后应跟着拖动改，实际:\n" + text3);
            Check(text3.Contains("当前层.线段 = 2"),
                  "当前层那行应重算成 2，实际:\n" + text3);

            // ---- 2. 越界保护 ----
            var solo = new Profile { Name = "solo" };
            var dim = new List<string>();
            dim.Add("D-1");
            dim.Add("D-2");
            solo.SetLayers(Categories.Dim, dim);
            Check(!solo.SetActiveIndex(Categories.Dim, 5), "越界下标应被拒绝");
            Check(!solo.SetActiveIndex(Categories.Dim, -1), "负下标应被拒绝");
            Check(solo.ActiveIndexOf(Categories.Dim) == 0, "拒绝后应还停在 0");

            solo.SetActiveIndex(Categories.Dim, 1);
            var shrunk = new List<string>();
            shrunk.Add("D-1");
            solo.SetLayers(Categories.Dim, shrunk);
            Check(solo.ActiveIndexOf(Categories.Dim) == 0,
                  "池缩到 1 个后下标应夹回 0，实际 " + solo.ActiveIndexOf(Categories.Dim));

            // ---- 3. 旧配置里的「矩形」要平滑并进线段 ----
            Config migrated = ConfigStore.FromText("[配置: 老配置]\n矩形 = 02-柱子\n");
            Profile m = migrated == null ? null : migrated.FindProfile("老配置");
            Check(m != null, "旧配置应能解析");
            if (m != null)
            {
                Check(m.LayerFor(Categories.Line) == "02-柱子",
                      "旧的「矩形」应并到线段，实际 " + m.LayerFor(Categories.Line));
                Check(m.LayerCount("矩形") == 0, "「矩形」这个类别本身应消失");
            }

            // 线段已经配过东西时，旧的「矩形」不能覆盖它
            Config migrated2 = ConfigStore.FromText("[配置: 甲]\n线段 = 01-墙体\n矩形 = 02-柱子\n");
            Profile m2 = migrated2 == null ? null : migrated2.FindProfile("甲");
            if (m2 != null)
            {
                Check(m2.LayerFor(Categories.Line) == "01-墙体",
                      "线段已配置时不能被旧矩形覆盖，实际 " + m2.LayerFor(Categories.Line));
                Check(m2.LayerCount(Categories.Line) == 1,
                      "线段池不应被旧矩形撑成 2 个，实际 " + m2.LayerCount(Categories.Line));
            }

            // ---- 4. 单段带颜色的写法不能被池解析弄坏 ----
            Config one = ConfigStore.FromText("[配置: 甲]\n引线 = E-14 引线 | 3\n");
            Profile m3 = one == null ? null : one.FindProfile("甲");
            if (m3 != null)
            {
                Check(m3.LayerCount(Categories.Leader) == 1, "单段应解析成 1 个图层");
                Check(m3.LayerFor(Categories.Leader) == "E-14 引线", "单段图层名应完整");
                Check(m3.LayerColorFor(Categories.Leader) == 3, "单段色号应保留");
            }

            _cur.Detail = "pool/active/migrate ok";
        }

        /// <summary>纯文本配置的往返：写出来的文本再读回来，内容必须一模一样。</summary>
        static void ConfigRoundTrip()
        {
            var cfg = new Config
            {
                Enabled = true,
                ActiveProfile = "甲",
                Settings = new Settings
                {
                    RestoreClayer = false,
                    AutoThaw = false,
                    AutoUnlock = false,
                    NewLayerColorIndex = 3
                }
            };
            var p = new Profile { Name = "甲" };
            p.SetLayer(Categories.Line, "01-墙体");
            p.SetLayer(Categories.Hatch, "05-填充");
            // 记住的颜色必须能存进文本再读回来，否则换张图纸就忘了
            p.SetLayerColor(Categories.Line, LayerColorCode.FromAci(5));
            p.SetLayerColor(Categories.Hatch, LayerColorCode.FromRgb(12, 200, 240));
            cfg.Profiles.Add(p);
            cfg.Profiles.Add(new Profile { Name = "乙" });

            string text = ConfigStore.ToText(cfg);
            Check(text.Length > 0, "写出的文本不应为空");
            Check(text.Contains("启用 = 是"), "文本里应能直接看到「启用 = 是」");
            Check(text.Contains("[配置: 甲]"), "文本里应有 [配置: 甲] 段");
            Check(text.Contains("线段 = 01-墙体 | 5"), "线段应写成「图层 | 色号」，实际文本里没有这行");
            Check(text.Contains("填充 = 05-填充 | rgb(12,200,240)"), "真彩色应写成 rgb(r,g,b)");

            Config back = ConfigStore.FromText(text);
            Check(back != null, "解析结果不应为 null");
            if (back == null) return;

            Check(back.Enabled, "启用 应保持 true");
            Check(back.ActiveProfile == "甲", "当前配置应保持，实际 " + back.ActiveProfile);
            Check(back.Settings != null && !back.Settings.RestoreClayer, "命令后还原图层 应为 false");
            Check(back.Settings != null && !back.Settings.AutoThaw, "自动解冻 应为 false");
            Check(back.Settings != null && back.Settings.NewLayerColorIndex == 3, "默认颜色应为 3");
            Check(back.Profiles != null && back.Profiles.Count == 2, "应有两个配置，实际 " + (back.Profiles == null ? 0 : back.Profiles.Count));

            Profile got = back.FindProfile("甲");
            Check(got != null, "应能找到配置「甲」");
            if (got != null)
            {
                Check(got.LayerFor(Categories.Line) == "01-墙体", "线段图层应为 01-墙体，实际 " + got.LayerFor(Categories.Line));
                Check(got.LayerFor(Categories.Hatch) == "05-填充", "填充图层应为 05-填充");
                Check(got.LayerColorFor(Categories.Line) == LayerColorCode.FromAci(5),
                    "线段记住的索引色应往返不变，实际 " + got.LayerColorFor(Categories.Line));
                Check(got.LayerColorFor(Categories.Hatch) == LayerColorCode.FromRgb(12, 200, 240),
                    "填充记住的真彩色应往返不变，实际 " + got.LayerColorFor(Categories.Hatch));
                Check(got.LayerColorFor(Categories.Text) == LayerColorCode.Unset,
                    "没配过的类别颜色应为「没记录」，实际 " + got.LayerColorFor(Categories.Text));
            }
            _cur.Detail = "text=" + text.Length + " chars";

            // 位置/玻璃的开关都取消了（1.6 收尾），这行不该再被写出来 ——
            // 留着它会把"已经删掉的设置"又写回 ini，用户改了以为有效果。
            string t2 = ConfigStore.ToText(cfg);
            Check(!t2.Contains("状态条"), "ini 里不该再有「状态条」这一行");
            Check(!t2.Contains("圆盘玻璃"), "ini 里不该再有「圆盘玻璃」这一行");
        }

        /// <summary>
        /// 手改文本不能把插件搞崩：注释、空行、没等号的废行、越界颜色、中文冒号，
        /// 全部要么正确识别，要么安静跳过。
        /// </summary>
        static void TextParseRobust()
        {
            string text = string.Join("\n", new string[]
            {
                "; 这是注释，应被忽略",
                "# 这也是注释",
                "乱七八糟没有等号的一行",
                "",
                "启用 = 是",
                "当前配置 = 甲",
                "自动解冻 = 否",
                "新建图层默认颜色 = 9",
                "",
                "[配置: 甲]",
                "线段 = 01-墙体 | 5",
                "引线 = 02-柱子",
                "文字 = ",
                "标注 = E-12 | rgb(12,200,240)",
                "填充 = 05-填充 | 999",
                "不认识的键 = 随便写",
                "",
                "[配置：乙]",
                "块 = 07-图块 | 250"
            });

            Config cfg = ConfigStore.FromText(text);
            Check(cfg != null, "解析不应返回 null");
            if (cfg == null) return;

            Check(cfg.Enabled, "启用 = 是 应识别为 true");
            Check(cfg.ActiveProfile == "甲", "当前配置应为甲，实际 " + cfg.ActiveProfile);
            Check(!cfg.Settings.AutoThaw, "自动解冻 = 否 应识别为 false");
            Check(cfg.Settings.NewLayerColorIndex == 9, "默认颜色应为 9，实际 " + cfg.Settings.NewLayerColorIndex);

            Check(cfg.Profiles.Count == 2, "应识别出两个配置（含中文冒号的段头），实际 " + cfg.Profiles.Count);

            Profile a = cfg.FindProfile("甲");
            Check(a != null, "应能找到「甲」");
            if (a != null)
            {
                Check(a.LayerFor(Categories.Line) == "01-墙体" && a.LayerColorFor(Categories.Line) == 5, "线段 图层/色号 应正确");
                Check(a.LayerFor(Categories.Leader) == "02-柱子", "引线只写图层名也应识别");
                Check(a.LayerColorFor(Categories.Leader) == LayerColorCode.Unset, "没写色号应为「没记录」");
                Check(a.LayerFor(Categories.Text) == "", "空图层名应为空（不归层）");
                Check(a.LayerColorFor(Categories.Dim) == LayerColorCode.FromRgb(12, 200, 240), "标注真彩色应正确");
                Check(a.LayerColorFor(Categories.Hatch) == LayerColorCode.Unset, "越界色号 999 应被忽略而不是崩掉");
            }

            Profile b = cfg.FindProfile("乙");
            Check(b != null, "中文冒号 [配置：乙] 也应能识别");
            if (b != null) Check(b.LayerColorFor(Categories.Block) == 250, "块 的色号应为 250");

            _cur.Detail = "comments/badline/rgb/colon ok";
        }

        /// <summary>改名必须能改，但绝不能改出两个同名配置。</summary>
        static void ProfileRename()
        {
            var cfg = new Config { ActiveProfile = "甲" };
            cfg.Profiles.Add(new Profile { Name = "甲" });
            cfg.Profiles.Add(new Profile { Name = "乙" });

            // 自己跟自己同名不算冲突
            Check(cfg.ValidateName("甲", "甲") == null, "改名成自己原名应允许");
            // 空名 / 全空格 / 超长都要挡
            Check(cfg.ValidateName("", "甲") != null, "空名应被拒绝");
            Check(cfg.ValidateName("   ", "甲") != null, "全空格应被拒绝");
            Check(cfg.ValidateName(new string('x', Config.MaxNameLength + 1), "甲") != null, "超长名应被拒绝");

            // 与别的配置重名要挡，大小写不同也算重名
            Check(cfg.ValidateName("乙", "甲") != null, "与「乙」重名应被拒绝");
            Check(cfg.ValidateName("乙 ", "甲") != null, "带空格的「乙」也算重名");

            // 非法改名不能留下任何副作用
            Check(!cfg.Rename("甲", "乙"), "重名改名应返回 false");
            Check(cfg.FindProfile("甲") != null, "改名失败后原配置应还在");
            Check(cfg.ActiveProfile == "甲", "改名失败后 ActiveProfile 不应变");

            // 正常改名：名字变了，活动配置跟着走，规则不丢
            Profile a = cfg.FindProfile("甲");
            a.SetLayer(Categories.Line, "01-墙体");
            Check(cfg.Rename("甲", "  土建  "), "合法改名应成功");
            Check(cfg.FindProfile("土建") != null, "应能找到改名后的「土建」");
            Check(cfg.FindProfile("甲") == null, "旧名字不应再存在");
            Check(cfg.ActiveProfile == "土建", "ActiveProfile 应同步为「土建」，实际 " + cfg.ActiveProfile);
            Check(cfg.FindProfile("土建").LayerFor(Categories.Line) == "01-墙体", "改名后规则不应丢");

            // 自动默认名不能撞已有名字
            string auto = cfg.NextDefaultName();
            Check(cfg.FindProfile(auto) == null, "自动默认名不应与现有配置重名，实际 " + auto);

            // 兜底：配置里被手工塞进重名/空名时，读盘规整后必须唯一
            // （这里只跑纯内存的 Normalize，绝不碰用户真实的 AutoLayer.ini）
            cfg.Profiles.Add(new Profile { Name = "土建" });
            cfg.Profiles.Add(new Profile { Name = "" });
            ConfigStore.Normalize(cfg);
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Profile p in cfg.Profiles)
                Check(names.Add(p.Name), "规整后配置名应唯一，重复：" + p.Name);

            _cur.Detail = "rename/unique/trim ok";
        }

        /// <summary>默认配置应全部归 0 层。</summary>
        static void DefaultZeroAndMigrate()
        {
            Profile d = ConfigStore.DefaultProfile();
            foreach (string cat in Categories.All)
                Check(d.LayerFor(cat) == "0", "默认配置「" + cat + "」应为 0 层，实际 " + d.LayerFor(cat));

            Config created = ConfigStore.CreateDefault();
            Profile cp = created.Active;
            Check(cp != null, "新建的默认配置应可直接取到");
            if (cp != null)
                foreach (string cat in Categories.All)
                    Check(cp.LayerFor(cat) == "0", "新建配置「" + cat + "」应为 0 层");

            _cur.Detail = "default=0";
        }

        /// <summary>图层名策略与 CAD 一致：不认识的字符直接拒绝，不擅自改名。</summary>
        static void LayerNameValidation(Database db)
        {
            Check(LayerService.IsValidName("01-墙体"), "中文图层名应合法");
            Check(LayerService.IsValidName("0"), "0 层应合法");
            Check(LayerService.IsValidName("A-WALL-DIM"), "英文带横线应合法");
            Check(LayerService.IsValidName("E-04 立面造型线"), "带空格的中文名应合法");

            Check(!LayerService.IsValidName(""), "空名应非法");
            Check(!LayerService.IsValidName("   "), "全空格应非法");
            Check(!LayerService.IsValidName("墙体/承重"), "斜杠应非法");
            Check(!LayerService.IsValidName("a<b"), "尖括号应非法");
            Check(!LayerService.IsValidName("a\"b"), "双引号应非法");
            Check(!LayerService.IsValidName("a:b"), "冒号应非法");
            Check(!LayerService.IsValidName("a*b"), "星号应非法");
            Check(!LayerService.IsValidName("a|b"), "竖线应非法");
            Check(!LayerService.IsValidName(new string('x', 256)), "超长名应非法");

            // 引擎层：非法名安全跳过，绝不能悄悄改名建出别的图层
            var res = LayerService.Ensure(db, "测试/图层", new Settings());
            Check(!res.Ok, "非法图层名应返回 Ok=false，而不是改名建层");
            Check(res.Notes.Count > 0, "应给出原因，实际 " + res.Notes.Count + " 条");
            Check(LayerService.StateOf(db, "测试-图层") == "不存在",
                "不应擅自建出「测试-图层」，实际 " + LayerService.StateOf(db, "测试-图层"));

            _cur.Detail = "invalid-name rejected";
        }

        /// <summary>
        /// 「图层不存在时颜色不对」的回归点：
        /// 配置里记住的颜色必须原样用在自动新建的图层上，没记住才退回默认 7。
        /// </summary>
        static void NewLayerColor(Database db)
        {
            // 1) 没记住颜色 → 默认 7（白/黑）
            var resDef = LayerService.Ensure(db, "T-COLOR-DEF", new Settings());
            Check(resDef.Ok, "应能自动新建 T-COLOR-DEF");
            Check(ColorIndexOf(db, "T-COLOR-DEF") == 7,
                "默认新建图层颜色应为 7，实际 " + ColorIndexOf(db, "T-COLOR-DEF"));

            // 2) 配置里记住了索引色 5 → 新图纸里建出来就是 5
            var resAci = LayerService.Ensure(db, "T-COLOR-ACI", new Settings(), LayerColorCode.FromAci(5));
            Check(resAci.Ok, "应能自动新建 T-COLOR-ACI");
            Check(ColorIndexOf(db, "T-COLOR-ACI") == 5,
                "记住索引色 5 时应为 5，实际 " + ColorIndexOf(db, "T-COLOR-ACI"));

            // 3) 记住的是真彩色 → 建出来也是真彩色，不能退化成最近的索引色
            int rgb = LayerColorCode.FromRgb(12, 200, 240);
            var resRgb = LayerService.Ensure(db, "T-COLOR-RGB", new Settings(), rgb);
            Check(resRgb.Ok, "应能自动新建 T-COLOR-RGB");
            byte r, g, b;
            Check(TryRgbOf(db, "T-COLOR-RGB", out r, out g, out b), "T-COLOR-RGB 应为真彩色");
            Check(r == 12 && g == 200 && b == 240,
                "真彩色应为 12,200,240，实际 " + r + "," + g + "," + b);

            // 4) 图层已存在时绝不改写颜色（用户自己调过的色不能被插件覆盖）
            LayerService.Ensure(db, "T-COLOR-ACI", new Settings(), LayerColorCode.FromAci(1));
            Check(ColorIndexOf(db, "T-COLOR-ACI") == 5,
                "已存在图层的颜色不应被改写，实际 " + ColorIndexOf(db, "T-COLOR-ACI"));

            // 5) 能从图纸读回颜色 —— 面板保存配置时靠这个"记住当时颜色"
            int got;
            Check(LayerService.TryGetLayerColor(db, "T-COLOR-ACI", out got) && got == 5,
                "应能读回索引色 5，实际 " + got);
            Check(LayerService.TryGetLayerColor(db, "T-COLOR-RGB", out got) && LayerColorCode.IsRgb(got),
                "应能读回真彩色，实际 " + got);
            Check(!LayerService.TryGetLayerColor(db, "T-NOT-EXIST", out got),
                "不存在的图层应返回 false（调用方据此保留旧记录）");

            // 6) 换图层名必须清掉旧颜色，否则会把上一个图层的色带到新层上
            var p = new Profile { Name = "P" };
            p.SetLayer(Categories.Line, "A");
            p.SetLayerColor(Categories.Line, LayerColorCode.FromAci(3));
            Check(p.LayerColorFor(Categories.Line) == 3, "应记住颜色 3");
            p.SetLayer(Categories.Line, "A");
            Check(p.LayerColorFor(Categories.Line) == 3, "同名重复设置不应清掉颜色");
            p.SetLayer(Categories.Line, "B");
            Check(p.LayerColorFor(Categories.Line) == LayerColorCode.Unset, "换图层名后旧颜色应作废");

            _cur.Detail = "def=7 aci=5 rgb=ok keep=5";
        }

        /// <summary>
        /// 完整链路：配置里记住了图层颜色 → 在没这个图层的新图纸里画线 →
        /// 自动建出来的图层就是这个颜色。走的是用户真正走的那条路（命令拦截），
        /// 不是直接调 LayerService。
        /// </summary>
        static void AutoCreateColorEndToEnd(Editor ed, Database db)
        {
            Config cfg = UseConfig(true, "T-AUTOCOL");
            cfg.Active.SetLayerColor(Categories.Line, LayerColorCode.FromAci(4));
            ConfigStore.SetCurrent(cfg);

            Reset(ed, db);
            string got = Draw(ed, db, "_.LINE", new Point3d(0, 900, 0), new Point3d(1000, 900, 0), "");
            Check(got == "Line@T-AUTOCOL", "应归到自动新建的 T-AUTOCOL，实际 " + got);
            Check(ColorIndexOf(db, "T-AUTOCOL") == 4,
                "自动新建图层应使用配置记住的颜色 4，实际 " + ColorIndexOf(db, "T-AUTOCOL"));

            _cur.Detail = "Line@T-AUTOCOL color=4";
        }

        // ------------------------------------------------------------------ helpers

        static Config UseConfig(bool enabled, string lineLayer, bool autoCreate = true)
        {
            var cfg = new Config
            {
                Enabled = enabled,
                ActiveProfile = "T",
                Settings = new Settings
                {
                    RestoreClayer = true,
                    AutoCreateLayer = autoCreate,
                    AutoThaw = true,
                    AutoTurnOn = true
                }
            };
            var p = new Profile { Name = "T" };
            p.SetLayer(Categories.Line, lineLayer);
            p.SetLayer(Categories.Dim, L_DIM);
            p.SetLayer(Categories.Text, L_TEXT);
            cfg.Profiles.Add(p);
            ConfigStore.SetCurrent(cfg);
            return cfg;
        }

        static void Reset(Editor ed, Database db)
        {
            SetClayer(db, "0");
            try { if (!db.TileMode) ed.Command("_.CTAB", "Model"); } catch { }
        }

        static void PrepareLayers(Database db)
        {
            EnsureLayer(db, "T-LINE", 1);
            EnsureLayer(db, "T-DIM", 2);
            EnsureLayer(db, "T-TEXT", 3);
            EnsureLayer(db, "T-FROZEN", 4);
            EnsureLayer(db, "T-OFF", 5);
            EnsureLayer(db, "T-LOCK", 7);
            EnsureLayer(db, "T-OVR", 6);
            EnsureLayer(db, "T-VIEW", 8);
        }

        static string Draw(Editor ed, Database db, string cmd, params object[] args)
        {
            HashSet<ObjectId> before = Snapshot(db);
            var all = new object[args.Length + 1];
            all[0] = cmd;
            Array.Copy(args, 0, all, 1, args.Length);
            ed.Command(all);

            List<string> fresh = NewEntities(db, before);
            if (fresh.Count == 0) return "(none)";
            return string.Join(" | ", fresh.ToArray());
        }

        static HashSet<ObjectId> Snapshot(Database db)
        {
            var set = new HashSet<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr) set.Add(id);
                tr.Commit();
            }
            return set;
        }

        static List<string> NewEntities(Database db, HashSet<ObjectId> before)
        {
            var res = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    if (before.Contains(id)) continue;
                    var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e != null) res.Add(e.GetType().Name + "@" + e.Layer);
                }
                tr.Commit();
            }
            return res;
        }

        static string Clayer(Database db)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var ltr = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
                    string n = ltr.Name;
                    tr.Commit();
                    return n;
                }
            }
            catch { return "?"; }
        }

        static void SetClayer(Database db, string name)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(name)) db.Clayer = lt[name];
                    tr.Commit();
                }
            }
            catch { }
        }

        static void SetLayerState(Database db, string name, bool frozen, bool off)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                if (frozen) ltr.IsFrozen = true;
                if (off) ltr.IsOff = true;
                tr.Commit();
            }
        }

        /// <summary>读图层颜色的 ACI 索引，读不到返回 -1。</summary>
        static short ColorIndexOf(Database db, string name)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(name)) { tr.Commit(); return -1; }
                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                    short idx = ltr.Color.ColorIndex;
                    tr.Commit();
                    return idx;
                }
            }
            catch { return -1; }
        }

        /// <summary>读图层颜色的真彩色分量；不是真彩色返回 false。</summary>
        static bool TryRgbOf(Database db, string name, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(name)) { tr.Commit(); return false; }

                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                    Color c = ltr.Color;
                    bool ok = c != null && c.ColorMethod == ColorMethod.ByColor;
                    if (ok) { r = c.Red; g = c.Green; b = c.Blue; }
                    tr.Commit();
                    return ok;
                }
            }
            catch { return false; }
        }

        static bool IsFrozen(Database db, string name)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                bool v = ltr.IsFrozen;
                tr.Commit();
                return v;
            }
        }

        static bool IsOff(Database db, string name)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                bool v = ltr.IsOff;
                tr.Commit();
                return v;
            }
        }

        static void SetLayerLocked(Database db, string name, bool locked)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                ltr.IsLocked = locked;
                tr.Commit();
            }
        }

        static bool IsLocked(Database db, string name)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                bool v = ltr.IsLocked;
                tr.Commit();
                return v;
            }
        }

        static void EnsureLayer(Database db, string name, short colorIndex)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord
                    {
                        Name = name,
                        Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
                    };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                tr.Commit();
            }
        }

        static string FirstLayoutName(Database db)
        {
            var names = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry en in dict) names.Add(en.Key);
                tr.Commit();
            }
            foreach (string n in names)
                if (!string.Equals(n, "Model", StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        static void Case(string name, Action body)
        {
            var c = new CaseResult { Name = name };
            Cases.Add(c);
            _cur = c;
            try { body(); }
            catch (System.Exception ex) { c.Failures.Add(Describe(ex)); }
            finally
            {
                c.Passed = c.Failures.Count == 0;
                _cur = null;
            }
        }

        /// <summary>
        /// 面板快捷键 + 圆盘触发方式（1.7 新增的设置窗口）。
        ///
        /// 这两项都是"存进 ini、钩子每次按键/点击都要读"的东西，
        /// 所以最要紧的是**文本往返不能丢、不能变味**：用户手改 ini 写错了不该崩，
        /// 写对了必须原样生效。
        /// </summary>
        static void HotkeyAndTrigger()
        {
            // ---- 1. 快捷键：位标志 + 虚拟键码 ↔ 文本 ----
            Check(Hotkey.Text(Hotkey.Ctrl | Hotkey.Shift, 0x51) == "Ctrl+Shift+Q",
                  "Ctrl+Shift+Q 应能拼出来，实际 " + Hotkey.Text(Hotkey.Ctrl | Hotkey.Shift, 0x51));
            Check(Hotkey.Text(0, 0) == "", "没配应返回空串");

            int m, k;
            Check(Hotkey.Parse("Ctrl+Shift+Q", out m, out k)
                  && m == (Hotkey.Ctrl | Hotkey.Shift) && k == 0x51,
                  "Ctrl+Shift+Q 应能解析回来");
            // 中文输入法下很容易打出全角加号和小写
            Check(Hotkey.Parse("ctrl＋shift＋q", out m, out k)
                  && m == (Hotkey.Ctrl | Hotkey.Shift) && k == 0x51,
                  "全角加号 + 小写也应认");
            Check(Hotkey.Parse("无", out m, out k) && k == 0,
                  "「无」应解析成没配（而不是解析失败）");
            Check(!Hotkey.Parse("Ctrl+", out m, out k), "只有修饰键应判为无效");
            Check(!Hotkey.Parse("Ctrl+胡说", out m, out k), "认不出的键名应判为无效");
            Check(Hotkey.Text(Hotkey.Alt, 0x77) == "Alt+F8", "F 键应显示成 F8，实际 " + Hotkey.Text(Hotkey.Alt, 0x77));

            // ---- 2. ini 往返 ----
            var cfg = new Config { ActiveProfile = "甲", Settings = new Settings() };
            cfg.Profiles.Add(new Profile { Name = "甲" });
            cfg.Settings.HotkeyMods = Hotkey.Alt;
            cfg.Settings.HotkeyKey = 0x77;                 // F8
            cfg.Settings.PieButton = 2;                    // 侧键前进
            cfg.Settings.PieHoldMs = 240;

            string t = ConfigStore.ToText(cfg);
            Check(t.Contains("面板快捷键 = Alt+F8"), "ini 里应写出快捷键，实际:\n" + t);
            Check(t.Contains("圆盘按键 = 侧键前进"), "ini 里应写出侧键前进，实际:\n" + t);

            Config back = ConfigStore.FromText(t);
            Check(back.Settings.HotkeyMods == Hotkey.Alt && back.Settings.HotkeyKey == 0x77,
                  "快捷键应能往返");
            Check(back.Settings.PieButton == 2, "圆盘按键应能往返，实际 " + back.Settings.PieButton);
            Check(back.Settings.PieHoldMs == 240, "长按时长应能往返，实际 " + back.Settings.PieHoldMs);

            // 下限必须跟设置窗口的滑块一致（100）。以前这里是 120，
            // 滑块拖到最左存成 100、读回来被抬到 120，数值会自己跳。
            Config low = ConfigStore.FromText("圆盘长按毫秒 = 50");
            Check(low.Settings.PieHoldMs == 100, "长按时长下限应为 100，实际 " + low.Settings.PieHoldMs);
            Config low2 = ConfigStore.FromText("圆盘长按毫秒 = 100");
            Check(low2.Settings.PieHoldMs == 100, "正好 100 不该被抬走，实际 " + low2.Settings.PieHoldMs);

            // ---- 3. 写「无」= 把快捷键关掉，只剩 QH 命令 ----
            cfg.Settings.HotkeyKey = 0;
            cfg.Settings.HotkeyMods = 0;
            string t2 = ConfigStore.ToText(cfg);
            Check(t2.Contains("面板快捷键 = 无"), "没配时应写「无」，实际:\n" + t2);
            Check(ConfigStore.FromText(t2).Settings.HotkeyKey == 0, "「无」应能往返");

            // ---- 4. Normalize 兜底：手改 ini 改出越界值别让钩子认不出 ----
            var bad = new Config { Settings = new Settings() };
            bad.Settings.PieButton = 9;
            bad.Settings.HotkeyKey = 0;
            bad.Settings.HotkeyMods = Hotkey.Ctrl;          // 有修饰键没主键 = 按不出来
            ConfigStore.Normalize(bad);
            Check(bad.Settings.PieButton == 0, "越界的圆盘按键应退回右键，实际 " + bad.Settings.PieButton);
            Check(bad.Settings.HotkeyMods == 0, "没有主键时修饰键应一并清掉，实际 " + bad.Settings.HotkeyMods);

            // ---- 5. 自定义命令名的"占用判断" ----
            // IsCommandNameInUse 返回的是命令种类枚举，NoneCmd = 还没这个名字。
            // 之前把 NoneCmd 当成"占用"，用户在设置里填任何名字都提示"已被占用"。
            Check(CommandAliasHost.InterpretStatus("NoneCmd") == null,
                  "NoneCmd 表示这个名字还不是命令，不该算占用");
            Check(CommandAliasHost.InterpretStatus(null) == null, "查不到时不该算占用");
            Check(CommandAliasHost.InterpretStatus("0") == null, "0 不该算占用");
            Check(CommandAliasHost.InterpretStatus("CoreCmd") != null, "CAD 内置命令应算占用");
            Check(CommandAliasHost.InterpretStatus("LispCmd") != null, "已有 LISP 命令应算占用");
            Check(CommandAliasHost.InterpretStatus("SetvarCmd") != null, "系统变量应算占用");

            // ---- 6. 名字校验 ----
            string why;
            Check(CommandAliasHost.IsValidName("GH", out why), "GH 应合法");
            Check(CommandAliasHost.IsValidName("al-01_x", out why), "字母数字减号下划线应合法");
            Check(!CommandAliasHost.IsValidName("G", out why), "单字母应拒绝（会被 CAD 别名盖住）");
            Check(!CommandAliasHost.IsValidName("QH", out why), "撞插件自己的命令应拒绝");
            Check(!CommandAliasHost.IsValidName("G H", out why), "带空格应拒绝");
            Check(!CommandAliasHost.IsValidName("命令", out why), "中文应拒绝");
            Check(!CommandAliasHost.IsValidName("", out why), "空应拒绝");

            // ---- 7. 清空别名 = ini 里写「无」，不留残留 ----
            cfg.Settings.CommandAlias = "";
            string t3 = ConfigStore.ToText(cfg);
            Check(t3.Contains("面板命令 = 无"), "清空别名后 ini 应写「无」，实际:\n" + t3);
            Check(ConfigStore.FromText(t3).Settings.CommandAlias == "",
                  "清空别名应能往返成空串");

            // ---- 8. 只剩三档：右键长按 / 侧键后退 / 侧键前进 ----
            // 1.10 曾经有过第 4 档「任一侧键」，1.12 删了（它跟设置窗口里「关闭」的
            // 哨兵值撞在同一个 3 上，选了反而把圆盘关掉）。老 ini 里的写法必须安全降级。
            cfg.Settings.PieButton = 3;
            string t4 = ConfigStore.ToText(cfg);
            Check(!t4.Contains("任一侧键"), "「任一侧键」不该再写进 ini，实际:\n" + t4);

            Check(ConfigStore.FromText("圆盘按键 = 侧键后退\n").Settings.PieButton == 1, "侧键后退应解析成 1");
            Check(ConfigStore.FromText("圆盘按键 = 侧键前进\n").Settings.PieButton == 2, "侧键前进应解析成 2");
            Check(ConfigStore.FromText("圆盘按键 = 任一侧键\n").Settings.PieButton == 0,
                  "老 ini 里的「任一侧键」应降级成右键长按，实际 "
                  + ConfigStore.FromText("圆盘按键 = 任一侧键\n").Settings.PieButton);
            Check(ConfigStore.FromText("圆盘按键 = 侧键\n").Settings.PieButton == 0,
                  "只写「侧键」也应降级成右键长按");

            var bad2 = new Config { Settings = new Settings() };
            bad2.Settings.PieButton = 3;      // 1.10 的「任一侧键」，现在算越界
            ConfigStore.Normalize(bad2);
            Check(bad2.Settings.PieButton == 0, "PieButton=3 应归一到右键，实际 " + bad2.Settings.PieButton);
            var bad3 = new Config { Settings = new Settings() };
            bad3.Settings.PieButton = 7;
            ConfigStore.Normalize(bad3);
            Check(bad3.Settings.PieButton == 0, "越界的圆盘按键应退回右键，实际 " + bad3.Settings.PieButton);
        }

        static void Check(bool ok, string message)
        {
            if (!ok) _cur.Failures.Add(message);
        }

        static string Describe(System.Exception ex)
        {
            var sb = new StringBuilder();
            System.Exception cur = ex;
            int depth = 0;
            while (cur != null && depth < 3)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(cur.GetType().Name).Append(": ").Append(OneLine(cur.Message));
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        static string OneLine(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        static string Ver()
        {
            try { return Convert.ToString(AcApp.GetSystemVariable("ACADVER")); }
            catch { return "?"; }
        }

        static void W(Editor ed, string msg)
        {
            try
            {
                if (ed != null) ed.WriteMessage("\n" + msg);
                else Console.WriteLine(msg);
            }
            catch { }
        }
    }
}
