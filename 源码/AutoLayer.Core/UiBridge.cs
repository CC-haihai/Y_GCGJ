using System;
using System.IO;
using System.Reflection;

namespace AutoLayer.Core
{
    /// <summary>
    /// 通往界面组件的唯一入口。
    /// Core 刻意不引用 acmgd / WPF（引了就没法在 accoreconsole 里跑测试），
    /// 所以界面全部靠反射调 —— 合并版和分离版都不用条件编译。
    /// </summary>
    internal static class UiBridge
    {
        static Type _host;
        static bool _probed;

        /// <summary>
        /// 找 AutoLayer.UI.PanelHost：
        /// 合并版就在当前程序集里；分离版在 AutoLayer.UI.dll（与主 DLL 同目录）。
        /// </summary>
        static Type Host()
        {
            if (_probed) return _host;
            _probed = true;

            try
            {
                Assembly self = typeof(UiBridge).Assembly;
                _host = self.GetType("AutoLayer.UI.PanelHost", false);
                if (_host != null) return _host;

                string dir = Path.GetDirectoryName(self.Location);
                string uiPath = Path.Combine(dir ?? "", "AutoLayer.UI.dll");
                if (!File.Exists(uiPath)) return null;

                _host = Assembly.LoadFrom(uiPath).GetType("AutoLayer.UI.PanelHost", true);
            }
            catch
            {
                _host = null;
            }
            return _host;
        }

        /// <summary>界面组件在不在。无界面内核里恒为 false。</summary>
        public static bool Available
        {
            get { return Host() != null; }
        }

        /// <summary>
        /// 调界面里的静态方法。找不到人 / 方法不存在 / 内部抛异常，一律返回 false，
        /// 绝不让界面问题冒泡到 AutoCAD 的命令循环。
        /// </summary>
        public static bool Call(string method, object[] args)
        {
            try
            {
                Type h = Host();
                if (h == null) return false;

                MethodInfo m = h.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) return false;

                m.Invoke(null, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>带返回值的调用。</summary>
        public static bool CallBool(string method, object[] args, out bool result)
        {
            result = false;
            try
            {
                Type h = Host();
                if (h == null) return false;

                MethodInfo m = h.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) return false;

                object r = m.Invoke(null, args);
                result = r is bool && (bool)r;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>要一段文字回来的调用（诊断命令用）。</summary>
        public static bool CallString(string method, object[] args, out string result)
        {
            result = "";
            try
            {
                Type h = Host();
                if (h == null) return false;

                MethodInfo m = h.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) return false;

                object r = m.Invoke(null, args);
                result = r as string ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
