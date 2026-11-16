// -----------------------------------------------------------------------------
//  本文件移植自 StarPie（星盘）的轮盘几何算法
//    https://github.com/Star-Pie/StarPie
//    WinPieGestures/IconHelper.cs → CreateAdvancedSectorGeometry /
//                                   CreateRoundedAnnularSectorGeometry
//
//  StarPie 采用 MIT 许可证，以下为原文照录（版权行、免责声明一个字都不能省）：
//
//    MIT License
//
//    Copyright (c) 2026 Star Pie
//
//    Permission is hereby granted, free of charge, to any person obtaining a copy
//    of this software and associated documentation files (the "Software"), to deal
//    in the Software without restriction, including without limitation the rights
//    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//    copies of the Software, and to permit persons to whom the Software is
//    furnished to do so, subject to the following conditions:
//
//    The above copyright notice and this permission notice shall be included in all
//    copies or substantial portions of the Software.
//
//    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//    SOFTWARE.
//
//  移植改动：
//    · 去掉了 HexagonHive（六边形蜂巢）分支 —— 这里用不到，少一段死代码；
//    · 角度沿用 StarPie 的约定：0° 指向正东、顺时针为正（WPF 的 y 轴朝下）。
//      本插件内部用的是"0° 指向上方"，调用处减 90° 转换，见 PieWindow.ToStarAngle。
//    · 类名改叫 SectorGeometry，放在 AutoLayer.UI 命名空间下。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Media;

namespace AutoLayer.UI
{
    /// <summary>
    /// 环形扇区几何。核心是 <see cref="CreateRoundedAnnularSectorGeometry"/>：
    /// 四个角用真正的圆角弧（半径 cornerRadius）过渡，而不是拿粗描边糊出来。
    /// </summary>
    internal static class SectorGeometry
    {
        /// <summary>
        /// 按形状生成一格。角度约定：0° 正东，顺时针为正。
        /// </summary>
        /// <param name="shape">Original（圆角扇环）/ Circle（独立圆）/ Capsule（胶囊）</param>
        /// <param name="gap">相邻两格之间的缝隙，单位像素（在扇区中线处量）</param>
        /// <param name="cornerRadius">圆角半径</param>
        public static Geometry CreateAdvancedSectorGeometry(
            double cx, double cy,
            double startAngle, double endAngle,
            double innerR, double outerR,
            string shape, double gap = 0.0, double cornerRadius = 0.0)
        {
            double midAngle = (startAngle + endAngle) / 2.0;
            double midRad = midAngle * (Math.PI / 180.0);
            double midR = (innerR + outerR) / 2.0;
            double mx = cx + Math.Cos(midRad) * midR;
            double my = cy + Math.Sin(midRad) * midR;

            double span = Math.Abs(endAngle - startAngle);
            double halfRad = span / 2.0 * (Math.PI / 180.0);
            double chord = 2.0 * midR * Math.Sin(halfRad);

            double radialRoom = Math.Max(12.0, outerR - innerR - gap);
            double tangentialRoom = Math.Max(12.0, chord - gap);

            switch (shape)
            {
                case "Circle":
                {
                    double d = Math.Max(12.0, Math.Min(radialRoom, tangentialRoom) * 0.94);
                    return new EllipseGeometry(new Point(mx, my), d / 2.0, d / 2.0);
                }
                case "RoundedCapsule":
                case "FloatingCapsules":
                case "RoundedRect":
                case "Capsule":
                {
                    double w = Math.Max(16.0, radialRoom * 0.96);
                    double h = Math.Max(16.0, Math.Min(w * 0.82, tangentialRoom * 0.88));
                    double r = cornerRadius > 0.0
                        ? Math.Min(h / 2.0, cornerRadius + 2.0)
                        : Math.Min(h / 2.0, 10.0);
                    var rect = new RectangleGeometry(
                        new Rect(mx - w / 2.0, my - h / 2.0, w, h), r, r)
                    {
                        Transform = new RotateTransform(midAngle, mx, my)
                    };
                    rect.Freeze();
                    return rect;
                }
                default:
                {
                    double a0 = startAngle;
                    double a1 = endAngle;
                    if (gap > 0.0 && midR > 0.0)
                    {
                        double gapDeg = gap / midR * (180.0 / Math.PI);
                        if (gapDeg < span * 0.6)
                        {
                            a0 += gapDeg / 2.0;
                            a1 -= gapDeg / 2.0;
                        }
                    }
                    return CreateRoundedAnnularSectorGeometry(cx, cy, a0, a1, innerR, outerR, cornerRadius);
                }
            }
        }

        /// <summary>
        /// 圆角环形扇区。四个角各用一段半径 cornerRadius 的圆弧过渡，
        /// 弧心通过 asin(c / (R - c)) 求出的角度偏移来确定 —— 这是 StarPie 的原算法。
        /// </summary>
        public static Geometry CreateRoundedAnnularSectorGeometry(
            double cx, double cy,
            double startAngle, double endAngle,
            double innerRadius, double outerRadius,
            double cornerRadius)
        {
            double span = endAngle - startAngle;
            if (span <= 0.0) return Geometry.Empty;

            double thickness = outerRadius - innerRadius;
            // 圆角不能超过半个厚度，也不能超过半个内弧弦长
            double innerChord = 2.0 * innerRadius * Math.Sin(span / 2.0 * (Math.PI / 180.0));
            double limit = Math.Max(0.0, Math.Min(thickness / 2.2, innerChord / 2.2));
            double c = Math.Max(0.0, Math.Min(cornerRadius, limit));

            if (c < 0.8)
                return CreateStandardSectorGeometry(cx, cy, startAngle, endAngle, innerRadius, outerRadius);

            try
            {
                double a0 = startAngle * (Math.PI / 180.0);
                double a1 = endAngle * (Math.PI / 180.0);

                double dOuter = Math.Asin(Math.Min(0.95, c / Math.Max(1.0, outerRadius - c)));
                double dInner = Math.Asin(Math.Min(0.95, c / Math.Max(1.0, innerRadius + c)));

                double oA0 = a0 + dOuter;     // 外弧起点
                double oA1 = a1 - dOuter;     // 外弧终点
                double iA0 = a0 + dInner;     // 内弧起点
                double iA1 = a1 - dInner;     // 内弧终点

                if (oA1 <= oA0 || iA1 <= iA0)
                    return CreateStandardSectorGeometry(cx, cy, startAngle, endAngle, innerRadius, outerRadius);

                Point p0 = Polar(cx, cy, outerRadius, oA0);
                Point p1 = Polar(cx, cy, outerRadius, oA1);
                Point p2 = Polar(cx, cy, outerRadius - c, a1);
                Point p3 = Polar(cx, cy, innerRadius + c, a1);
                Point p4 = Polar(cx, cy, innerRadius, iA1);
                Point p5 = Polar(cx, cy, innerRadius, iA0);
                Point p6 = Polar(cx, cy, innerRadius + c, a0);
                Point p7 = Polar(cx, cy, outerRadius - c, a0);

                bool large = Math.Abs(span) > 180.0;
                var corner = new Size(c, c);

                var fig = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
                fig.Segments.Add(new ArcSegment(p1, new Size(outerRadius, outerRadius), 0.0, large, SweepDirection.Clockwise, true));
                fig.Segments.Add(new ArcSegment(p2, corner, 0.0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new LineSegment(p3, true));
                fig.Segments.Add(new ArcSegment(p4, corner, 0.0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new ArcSegment(p5, new Size(innerRadius, innerRadius), 0.0, large, SweepDirection.Counterclockwise, true));
                fig.Segments.Add(new ArcSegment(p6, corner, 0.0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new LineSegment(p7, true));
                fig.Segments.Add(new ArcSegment(p0, corner, 0.0, false, SweepDirection.Clockwise, true));

                var g = new PathGeometry();
                g.Figures.Add(fig);
                return g;
            }
            catch
            {
                return CreateStandardSectorGeometry(cx, cy, startAngle, endAngle, innerRadius, outerRadius);
            }
        }

        /// <summary>没有圆角的扇环（圆角算不出来时的兜底）。</summary>
        public static Geometry CreateStandardSectorGeometry(
            double cx, double cy,
            double startAngle, double endAngle,
            double innerRadius, double outerRadius)
        {
            double a0 = startAngle * (Math.PI / 180.0);
            double a1 = endAngle * (Math.PI / 180.0);
            bool large = Math.Abs(endAngle - startAngle) > 180.0;

            Point p0 = Polar(cx, cy, outerRadius, a0);
            Point p1 = Polar(cx, cy, outerRadius, a1);
            Point p2 = Polar(cx, cy, innerRadius, a1);
            Point p3 = Polar(cx, cy, innerRadius, a0);

            var fig = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
            fig.Segments.Add(new ArcSegment(p1, new Size(outerRadius, outerRadius), 0.0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(p2, true));
            fig.Segments.Add(new ArcSegment(p3, new Size(innerRadius, innerRadius), 0.0, large, SweepDirection.Counterclockwise, true));

            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        /// <summary>StarPie 的角度是弧度，这里直接吃弧度。</summary>
        static Point Polar(double cx, double cy, double r, double rad)
        {
            return new Point(cx + Math.Cos(rad) * r, cy + Math.Sin(rad) * r);
        }
    }
}
