# 第三方组件声明

本项目包含下列第三方代码与二进制。它们按各自的许可提供，不受本项目
[LICENSE](LICENSE)（PolyForm Noncommercial 1.0.0）约束。

---

## StarPie（星盘）

- 项目地址：<https://github.com/Star-Pie/StarPie>
- 使用许可：**MIT License**
- 使用范围：**仅一个文件** —— `源码/UI/SectorGeometry.cs`
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

## AutoCAD .NET API（`refs/`）

`refs/` 目录下是 AutoCAD 2016 (20.1) 的托管程序集，来自 Autodesk 官方发布的
NuGet 包（`AutoCAD.NET` / `AutoCAD.NET.Core` / `AutoCAD.NET.Model`，版本 20.1.0），
**仅用于编译引用**，让编出来的 DLL 能兼容 AutoCAD 2016 及以上。

| 文件 | 来源包 |
|---|---|
| `AcDbMgd.dll` / `acdbmgdbrep.dll` / `AcDx.dll` / `AcMr.dll` | `AutoCAD.NET.Model` |
| `AcCoreMgd.dll` | `AutoCAD.NET.Core` |
| `AcMgd.dll` / `AdWindows.dll` / `AcCui.dll` / `AcWindows.dll` / `AcTcMgd.dll` | `AutoCAD.NET` |

这些文件的版权归 **Autodesk, Inc.** 所有，随本仓库提供**仅为方便复现编译环境**，
不构成对这些文件的任何授权、再分发许可或所有权转移。若不接受此安排，请删除
`refs/` 目录，改用本机 AutoCAD 安装目录下的同名文件，或自行从 NuGet 获取。

运行时不需要 `refs/` —— 发布出去的 `Y_归层工具V2.1.dll` 不包含也不携带这些程序集，
它们在 AutoCAD 进程内由 CAD 自身提供。

AutoCAD、ObjectARX、AutoCAD .NET API 的版权与商标归 Autodesk, Inc. 所有。
本项目与 Autodesk, Inc. 无隶属关系，未经其背书。
