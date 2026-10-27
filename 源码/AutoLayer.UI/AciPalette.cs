using System;
using System.Windows.Media;

namespace AutoLayer.UI
{
    /// <summary>
    /// AutoCAD 索引色（ACI 1..255）到 RGB 的对照表。
    ///
    /// 为什么要自己算：CAD 的 .NET API 对 ByAci 颜色**不提供 RGB**
    /// （实测 <c>Color.FromColorIndex(ColorMethod.ByAci, i).Red/Green/Blue</c> 一律返回 0），
    /// 而面板要画色块就必须有 RGB。
    ///
    /// 调色板结构（标准 ACI）：
    /// <list type="bullet">
    /// <item>1-9：红 黄 绿 青 蓝 洋红 白 深灰 浅灰</item>
    /// <item>10-249：24 个色相（每 15°）× 10 档明度。每档成对出现，
    ///   偶数位全饱和、奇数位半饱和，明度依次 255 / 204 / 153 / 127 / 76</item>
    /// <item>250-255：灰阶 51 → 255</item>
    /// </list>
    /// 已用 10/11/20/21/30/40/90/210 等已知值校验过（截断取整，不是四舍五入）。
    /// </summary>
    static class AciPalette
    {
        static readonly Color[] Table = Build();

        /// <summary>取索引色对应的屏幕颜色。越界按 7（白/黑）处理。</summary>
        public static Color Get(short index)
        {
            if (index < 0 || index > 255) return Table[7];
            return Table[index];
        }

        static Color[] Build()
        {
            var t = new Color[256];

            // 0 = ByBlock，图层上不会出现，给个黑色占位
            t[0] = Color.FromRgb(0x00, 0x00, 0x00);

            t[1] = Color.FromRgb(0xFF, 0x00, 0x00);
            t[2] = Color.FromRgb(0xFF, 0xFF, 0x00);
            t[3] = Color.FromRgb(0x00, 0xFF, 0x00);
            t[4] = Color.FromRgb(0x00, 0xFF, 0xFF);
            t[5] = Color.FromRgb(0x00, 0x00, 0xFF);
            t[6] = Color.FromRgb(0xFF, 0x00, 0xFF);
            // 7 在深色背景上是白、浅色背景上是黑。面板是深色主题，所以取白。
            t[7] = Color.FromRgb(0xFF, 0xFF, 0xFF);
            t[8] = Color.FromRgb(0x80, 0x80, 0x80);
            t[9] = Color.FromRgb(0xC0, 0xC0, 0xC0);

            // 明度档：255 / 204 / 153 / 127 / 76。
            // **这里故意不写成 int[] levels = {...}**：本机 .NET Framework 4.8 上，
            // 4 个元素以上的数值型数组字面量（编译器走 RuntimeHelpers.InitializeArray 的
            // RVA 数据块）会让**前两个元素读成垃圾值** —— 同一份代码在 net8 上完全正常，
            // 是这台机器的 CLR 问题。踩过一次，别再用那个写法。
            for (int i = 10; i <= 249; i++)
            {
                int hue = (i - 10) / 10;          // 0..23 → 0°..345°
                int shade = (i - 10) % 10;        // 0..9

                int v;
                switch (shade / 2)
                {
                    case 0: v = 255; break;
                    case 1: v = 204; break;
                    case 2: v = 153; break;
                    case 3: v = 127; break;
                    default: v = 76; break;
                }

                double s = (shade % 2 == 0) ? 1.0 : 0.5;
                t[i] = HsvToColor(hue * 15.0, s, v / 255.0);
            }

            // 250-255：51, 92, 133, 173, 214, 255
            for (int k = 0; k < 6; k++)
            {
                int g = 51 + (int)Math.Round(k * 40.8);
                if (g > 255) g = 255;
                t[250 + k] = Color.FromRgb((byte)g, (byte)g, (byte)g);
            }

            return t;
        }

        static Color HsvToColor(double h, double s, double v)
        {
            double c = v * s;
            double hp = h / 60.0;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double m = v - c;

            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }

            // 截断而不是四舍五入：标准表里 255×0.5 记的是 127，不是 128
            return Color.FromRgb(
                (byte)Math.Min(255, (int)((r + m) * 255)),
                (byte)Math.Min(255, (int)((g + m) * 255)),
                (byte)Math.Min(255, (int)((b + m) * 255)));
        }
    }
}
