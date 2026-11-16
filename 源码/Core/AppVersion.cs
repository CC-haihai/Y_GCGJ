using System;

namespace AutoLayer.Core
{
    /// <summary>
    /// 版本号统一从程序集里取，不在代码里写死。
    ///
    /// 每翻一版只改 csproj 的 &lt;Version&gt; 一处，加载提示、ini 注释头、
    /// 安装器显示名全都自动跟上 —— 少一处硬编码，就少一次「改漏了」。
    /// </summary>
    public static class AppVersion
    {
        static string _text;

        /// <summary>形如「V2.1」。取不到时退回 V2.1。</summary>
        public static string Text
        {
            get
            {
                if (_text != null) return _text;

                try
                {
                    Version v = typeof(AppVersion).Assembly.GetName().Version;
                    _text = "V" + v.Major + "." + v.Minor;
                }
                catch { _text = "V2.1"; }

                return _text;
            }
        }
    }
}
