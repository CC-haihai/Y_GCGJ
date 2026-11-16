using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using AutoLayer.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoLayer.UI.QuickSwitch
{
    /// <summary>
    /// 图层切换面板（原 CC 插件）的宿主。
    ///
    /// 命令 CC 落在 Core 里，Core 不引用 acmgd / WPF，所以经由 PanelHost 反射转进来。
    /// 选中物体 → 弹面板选图层 → 图元挪过去，顺便把「最近使用」和勾选状态记进 ini。
    /// </summary>
    public static class QuickSwitchHost
    {
        /// <summary>当前是否有面板开着（避免连按 CC 叠出好几个窗口）。</summary>
        static LayerSelectionWindow _open;

        // ── 「回到上次选中」记忆（用户 2026-11-11 拍板）──
        // 上次打开面板时带进来的那批图元。CC 再开时若手头没选中任何东西，
        // 面板给一个一键回归按钮，把这批图元重新装回对象模式 —— 这是「改选」
        // 唯一值得存在的场景；手头已有选中直接改时不需要改选。
        static ObjectId[] _lastPicked;
        static Document _lastPickedDoc;

        // GetLastValidPicked 的结果缓存。这个方法被"搜索框每敲一个字"间接触发
        // （UpdateModeBar → UpdateRecallButton → 这里），每次都要对整批图元做
        // IsValid/IsErased 查询 —— 选了几百个图元时，打字就是几百次 CAD 调用。
        // 同一批（引用没变）只算一次；RememberLastPicked 换了新数组，引用比较自动失效。
        static ObjectId[] _lastValidCache;
        static ObjectId[] _lastValidCacheFor;

        /// <summary>记住「上次选中」的那批图元（打开面板时手头有预选就记）。</summary>
        internal static void RememberLastPicked(Document doc, ObjectId[] ids)
        {
            if (doc == null || ids == null || ids.Length == 0) return;
            _lastPickedDoc = doc;
            _lastPicked = ids;
        }

        /// <summary>
        /// 取「上次选中」里还活着的图元（被删/失效的过滤掉）。
        /// 只在同一个图纸文档里有效；换图纸 / 关闭重开 / 全被删 = 返回 null。
        /// </summary>
        internal static ObjectId[] GetLastValidPicked()
        {
            try
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null || _lastPicked == null || _lastPicked.Length == 0) return null;
                if (!ReferenceEquals(doc, _lastPickedDoc)) return null;

                if (ReferenceEquals(_lastValidCacheFor, _lastPicked) && _lastValidCache != null)
                    return _lastValidCache.Length > 0 ? _lastValidCache : null;

                var valid = new List<ObjectId>();
                foreach (ObjectId id in _lastPicked)
                {
                    try
                    {
                        if (id.IsValid && !id.IsErased && !id.IsEffectivelyErased) valid.Add(id);
                    }
                    catch { }
                }

                _lastValidCacheFor = _lastPicked;
                _lastValidCache = valid.Count > 0 ? valid.ToArray() : new ObjectId[0];
                return _lastValidCache.Length > 0 ? _lastValidCache : null;
            }
            catch { return null; }
        }

        public static void Show()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                if (_open != null)
                {
                    try { _open.Activate(); } catch { }
                    return;
                }

                List<LayerInfo> layers = LayerService.GetAllLayers(db);
                // 图纸无图层：不再挡在外面，照常开窗走 §3.9 空状态（唯一出路 = 关闭）

                ObjectId[] picked = (layers.Count > 0)
                    ? GetSelectedObjects(ed)
                    : new ObjectId[0];

                // 打开时手头有选中就记住 —— 下次空手打开 CC 时给「回到上次选中」（v1.9）
                if (picked.Length > 0) RememberLastPicked(doc, picked);

                // 「最近使用」只保留当前图纸里还存在的图层
                var recent = new List<string>();
                foreach (string name in RecentLayerManager.GetRecentLayers(5))
                {
                    foreach (LayerInfo l in layers)
                    {
                        if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            recent.Add(name);
                            break;
                        }
                    }
                }

                bool setCurrent = ConfigStore.Current.Settings != null
                               && ConfigStore.Current.Settings.QuickSwitchSetCurrent;

                // 光标位置只取一次：CursorLogical 里要查 DPI，取两次没必要
                System.Drawing.Point cursor = CursorLogical();
                var win = new LayerSelectionWindow(picked, layers, recent, setCurrent)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = cursor.X,
                    Top = cursor.Y
                };

                AdjustWindowPosition(win);

                try
                {
                    new WindowInteropHelper(win).Owner = AcadApp.MainWindow.Handle;
                }
                catch { }

                _open = win;

                // 窗口显示之后在后台把「图层 → 图元」索引建好。
                // 不放在 ShowDialog 之前：那会把扫全图的时间加到"弹出"上，
                // 而用户点第一行之前有足够时间；真点得比它快也有同步兜底。
                try
                {
                    win.Loaded += delegate
                    {
                        try
                        {
                            win.Dispatcher.BeginInvoke(
                                new Action(delegate { try { PreviewHighlighter.WarmIndex(); } catch { } }),
                                System.Windows.Threading.DispatcherPriority.Background);
                        }
                        catch { }
                    };
                }
                catch { }

                try
                {
                    win.ShowDialog();
                }
                finally
                {
                    _open = null;
                }

                if (!string.IsNullOrEmpty(win.ResultMessage))
                {
                    // §3.8：成功不弹框 —— 命令行一句 + 胶囊条闪目标层名
                    ed.WriteMessage("\n[图层切换] " + win.ResultMessage);
                    try { UI.StatusHud.Flash("图层 → " + win.ConfirmedLayerName); } catch { }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[图层切换] 出错：" + ex.Message);
            }
            finally
            {
                try { PreviewHighlighter.ClearHighlight(); } catch { }
                // 预览索引跟着面板会话走：关窗即丢，下次打开按当前图纸重建
                try { PreviewHighlighter.EndSession(); } catch { }
            }
        }

        /// <summary>
        /// 有预选就用预选；没有就空手直接开面板（v1.9 改）。
        ///
        /// 旧版在命令行弹一个「选择物体」提示，面板要等用户回车/框选完才出现 ——
        /// 从主面板图标点进来时这个提示藏在命令行里看不见，用户以为面板打不开。
        /// 现在没预选就直接开面板：当前层模式 + 「回到上次选中」按钮；
        /// 要换一批物体，Esc 关面板 → 图上选好 → 再敲 CC 即可（也是原来的习惯）。
        /// </summary>
        static ObjectId[] GetSelectedObjects(Editor ed)
        {
            try
            {
                PromptSelectionResult implied = ed.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                    return implied.Value.GetObjectIds();
            }
            catch { }

            return new ObjectId[0];
        }

        // ------------------------------------------------------------------ 定位

        static System.Drawing.Point CursorLogical()
        {
            try
            {
                System.Drawing.Point p = System.Windows.Forms.Cursor.Position;
                double scale = DpiHelper.GetDpi(null).DpiScaleX;
                if (scale <= 0.1) scale = 1.0;
                return new System.Drawing.Point((int)(p.X / scale), (int)(p.Y / scale));
            }
            catch
            {
                return new System.Drawing.Point(200, 200);
            }
        }

        /// <summary>把窗口夹在当前屏幕的工作区内，别让它跑到屏幕外。</summary>
        static void AdjustWindowPosition(Window window)
        {
            try
            {
                double scale = DpiHelper.GetDpi(null).DpiScaleX;
                if (scale <= 0.1) scale = 1.0;

                var phys = new System.Drawing.Point((int)(window.Left * scale), (int)(window.Top * scale));
                Screen screen = Screen.FromPoint(phys);
                if (screen == null) return;

                System.Drawing.Rectangle wa = screen.WorkingArea;
                double waLeft = wa.Left / scale;
                double waTop = wa.Top / scale;
                double waRight = wa.Right / scale;
                double waBottom = wa.Bottom / scale;

                double w = (window.ActualWidth > 0) ? window.ActualWidth : window.Width;
                double h = (window.ActualHeight > 0) ? window.ActualHeight : window.Height;
                if (w <= 0) w = 360;
                if (h <= 0) h = 480;

                if (window.Left + w > waRight) window.Left = waRight - w - 10;
                if (window.Top + h > waBottom) window.Top = waBottom - h - 10;
                if (window.Left < waLeft) window.Left = waLeft + 10;
                if (window.Top < waTop) window.Top = waTop + 10;
            }
            catch { }
        }
    }
}
