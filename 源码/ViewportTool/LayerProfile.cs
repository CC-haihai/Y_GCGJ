using System;
using System.Collections.Generic;
using AutoLayer.Core;

namespace VpLayer
{
    /// <summary>
    /// 视口工具访问「归层配置」的入口。
    ///
    /// 合并进主插件之后，这里就是一层薄封装，直接调 ConfigStore / LayerEvents ——
    /// 之前独立版用的反射桥（AutoLayerBridge）已经删掉，不再有类型名字符串。
    ///
    /// 所有回调都 try/catch：视口切换是后台定时器触发的，
    /// 这里一旦抛异常会打断轮询，后面就再也不切了。
    /// </summary>
    public static class LayerProfile
    {
        /// <summary>合并后恒为 true；保留这个属性是为了让调用点不用改写法。</summary>
        public static bool Available { get { return true; } }

        public static string UnavailableReason { get { return ""; } }

        /// <summary>主插件当前用的配置名；取不到返回空串。</summary>
        public static string CurrentProfile
        {
            get
            {
                try
                {
                    Config cfg = ConfigStore.Current;
                    return cfg == null ? "" : (cfg.ActiveProfile ?? "");
                }
                catch { return ""; }
            }
        }

        /// <summary>主插件总开关（Config.Enabled）。关着的时候切配置不会生效。</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    Config cfg = ConfigStore.Current;
                    return cfg != null && cfg.Enabled;
                }
                catch { return false; }
            }
        }

        /// <summary>主插件里所有配置名，按配置文件的顺序。</summary>
        public static List<string> ProfileNames()
        {
            var list = new List<string>();
            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null || cfg.Profiles == null) return list;

                foreach (Profile p in cfg.Profiles)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    if (!list.Contains(p.Name)) list.Add(p.Name);
                }
            }
            catch { }

            return list;
        }

        public static bool HasProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            List<string> names = ProfileNames();
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// 切主插件的当前配置。
        /// 只改内存、不落盘 —— 进个视口就把配置文件里的默认配置改掉，不是好事。
        /// </summary>
        public static bool SetProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null) return false;

                string cur = cfg.ActiveProfile ?? "";
                if (string.Equals(cur, name, StringComparison.OrdinalIgnoreCase)) return true;

                cfg.ActiveProfile = name;
                LayerEvents.Raise();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把一套归层配置改名并落盘。名字合法性和重名检查都交给 Config.Rename。
        /// 返回 false 表示没改成（找不到、名字非法、或已经被别的配置占着）。
        ///
        /// 注意：调用方还得把 VpState.Profile 和图纸里的视口标签一起改掉，
        /// 否则它们存的那个旧名字就悬空了，进视口切不过去。
        /// </summary>
        public static bool RenameProfile(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;

            try
            {
                Config cfg = ConfigStore.Current;
                if (cfg == null) return false;
                if (!cfg.Rename(oldName, newName)) return false;

                ConfigStore.Save(cfg);
                LayerEvents.Raise();
                return true;
            }
            catch { return false; }
        }
    }
}
