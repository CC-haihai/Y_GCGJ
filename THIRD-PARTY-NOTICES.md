# 第三方组件声明

本项目包含下列第三方代码。它们按各自的许可提供，不受本项目
[LICENSE](LICENSE)（PolyForm Noncommercial 1.0.0）约束。

---

## StarPie（星盘）

- 项目地址：<https://github.com/Star-Pie/StarPie>
- 使用许可：**MIT License**
- 使用范围：**仅一个文件** —— `源码/AutoLayer.UI/SectorGeometry.cs`
  （环状扇区几何，含四角圆角过渡。原实现在 StarPie 的
  `WinPieGestures/IconHelper.cs`，函数 `CreateAdvancedSectorGeometry` /
  `CreateRoundedAnnularSectorGeometry`）
- 移植改动：
  - 去掉了 HexagonHive（六边形蜂巢）分支；
  - 类名改为 `SectorGeometry`，移入 `AutoLayer.UI` 命名空间；
  - 角度沿用 StarPie 的约定（0° 指正东、顺时针为正），
    调用处减 90° 换算成本项目"0° 指向上方"的内部约定。

### 声明

除上述一个文件外，本项目的其余代码均为自行编写。圆盘的配色、皮肤划分、
高亮方式等视觉风格参考了 StarPie 的整体观感，但**渲染实现是独立编写的**，
未复制其源码。

本项目与 StarPie 项目及其作者无隶属关系，也未经其背书；未使用
"StarPie"、"星盘" 的名称或图标进行宣传。

### MIT 许可全文

```
MIT License

Copyright (c) 2026 Star Pie

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## AutoCAD .NET API

本项目编译时引用 Autodesk 的 `acdbmgd.dll` / `accoremgd.dll` / `acmgd.dll`，
但**不随本项目分发这些文件**。使用者需自行安装 AutoCAD 后从本机安装目录引用。

AutoCAD、ObjectARX、AutoCAD .NET API 的版权与商标归 Autodesk, Inc. 所有。
