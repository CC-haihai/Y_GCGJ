using System;

namespace AutoLayer.Core
{
    /// <summary>
    /// 鼠标键能绑的动作。
    ///
    /// 2.1 之前只有「圆盘」一个动作，靠 <see cref="Settings.PieButton"/> 指定用哪个键弹。
    /// 现在三个键（右键长按 / 侧键后退 / 侧键前进）各自独立绑一个动作 ——
    /// 有人不想敲命令，一个鼠标键就能把图层切换、视口工具、主面板叫出来。
    ///
    /// 数值直接写进 ini（中文名字），所以 <b>这几个常量值不许改</b>：
    /// 改了等于让所有老配置的绑定错位。
    /// </summary>
    public static class MouseBind
    {
        /// <summary>不绑定：这个键在 CAD 里保持原生行为。</summary>
        public const int None = 0;

        /// <summary>圆盘（按图层类别出扇区）。</summary>
        public const int Pie = 1;

        /// <summary>图层切换面板（CC）。</summary>
        public const int QuickSwitch = 2;

        /// <summary>视口图层工具（VPT）。</summary>
        public const int Viewport = 3;

        /// <summary>主面板（QH）。</summary>
        public const int Panel = 4;

        public const int Min = None;
        public const int Max = Panel;

        public static bool Valid(int a) { return a >= Min && a <= Max; }

        /// <summary>界面/ini 里的短名。四个字以内的列头要用它。</summary>
        public static string Text(int a)
        {
            switch (a)
            {
                case Pie: return "圆盘";
                case QuickSwitch: return "切层";
                case Viewport: return "视口";
                case Panel: return "面板";
                default: return "无";
            }
        }

        /// <summary>完整说法，用在提示行和诊断输出里。</summary>
        public static string Full(int a)
        {
            switch (a)
            {
                case Pie: return "圆盘（按图层类别出扇区）";
                case QuickSwitch: return "图层切换面板（CC）";
                case Viewport: return "视口图层工具（VPT）";
                case Panel: return "主面板（QH）";
                default: return "不绑定";
            }
        }

        /// <summary>
        /// 解析 ini 里那一格的值。认中文短名/全名，也认命令名（cc / vpt / qh / pie），
        /// 用户手改时写哪个都能认出来。认不出就返回 false，调用方保留原值 ——
        /// 绝不因为一个笔误就把绑定清成「无」。
        /// </summary>
        public static bool Parse(string s, out int action)
        {
            action = None;
            string v = (s ?? "").Trim();
            if (v.Length == 0) return false;
            if (v == "-" || v == "0") return true;      // 0 就是「无」，明确写出来的

            string u = v.ToUpperInvariant();

            // 先按整词认：手写整句（「不绑定图层切换」）不该被后面的"包含"匹配抢走
            switch (u)
            {
                case "无": case "不绑": case "不绑定": case "关": case "关闭":
                case "OFF": case "NONE": case "NO":
                    return true;
                case "圆盘": case "PIE": case "ALPIE": action = Pie; return true;
                case "切层": case "图层切换": case "CC": case "QUICKSWITCH": action = QuickSwitch; return true;
                case "视口": case "视口工具": case "VPT": action = Viewport; return true;
                case "面板": case "主面板": case "QH": case "PANEL": action = Panel; return true;
            }

            // 开头就是否定词的，一律当「无」
            if (v[0] == '不' || v[0] == '无') return true;

            // 再按包含认：允许写「圆盘（右键）」「CC 图层切换」这种带注解的
            if (v.IndexOf("圆盘", StringComparison.Ordinal) >= 0) { action = Pie; return true; }
            if (v.IndexOf("图层切换", StringComparison.Ordinal) >= 0
                || v.IndexOf("切层", StringComparison.Ordinal) >= 0) { action = QuickSwitch; return true; }
            if (v.IndexOf("视口", StringComparison.Ordinal) >= 0) { action = Viewport; return true; }
            if (v.IndexOf("主面板", StringComparison.Ordinal) >= 0) { action = Panel; return true; }

            if (u.IndexOf("PIE", StringComparison.Ordinal) >= 0) { action = Pie; return true; }
            if (u.IndexOf("CC", StringComparison.Ordinal) >= 0) { action = QuickSwitch; return true; }
            if (u.IndexOf("VPT", StringComparison.Ordinal) >= 0) { action = Viewport; return true; }
            if (u.IndexOf("QH", StringComparison.Ordinal) >= 0) { action = Panel; return true; }

            return false;
        }
    }
}
