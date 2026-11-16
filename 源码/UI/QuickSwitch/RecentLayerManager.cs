using System;
using System.Collections.Generic;
using System.Linq;
using AutoLayer.Core;

namespace AutoLayer.UI.QuickSwitch
{
    /// <summary>
    /// 「最近使用」的图层。
    ///
    /// 原 CC 只记在内存里，关掉 CAD 就没了；合并版直接落进 AutoLayer.ini
    /// （<c>最近图层 = A ; B ; C</c>），重开 CAD 打开面板还是那几条。
    /// 图层被删掉了不用清理 —— 面板取列表时会按当前图纸的图层表过滤。
    /// </summary>
    public static class RecentLayerManager
    {
        const int MaxRecentCount = 12;

        static List<string> Store
        {
            get
            {
                Settings st = ConfigStore.Current.Settings;
                if (st == null) return new List<string>();
                if (st.RecentLayers == null) st.RecentLayers = new List<string>();
                return st.RecentLayers;
            }
        }

        public static void RecordUsage(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return;

            try
            {
                List<string> list = Store;
                // 大小写不敏感去重：AutoCAD 图层名本身不区分大小写
                list.RemoveAll(n => string.Equals(n, layerName, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, layerName);
                while (list.Count > MaxRecentCount) list.RemoveAt(list.Count - 1);

                ConfigStore.Save(ConfigStore.Current);
            }
            catch { }
        }

        public static List<string> GetRecentLayers(int maxCount = 8)
        {
            try
            {
                return Store.Take(Math.Min(maxCount, MaxRecentCount)).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
