# Y_归层工具 V2.1 —— 源码

AutoCAD 插件，一个 DLL 三套核心能力：

- **自动归层**：画图时按图层名前缀规则自动把图元落到对应图层（V1.0 核心能力）
- **图层切换**：`CC` 图层切换面板、鼠标划过即预亮、最近图层快捷行（V2.0 新增）
- **视口图层状态**：刷取视口的冻结层、批量生成布局视口、进视口自动切归层配置（V2.0 追加）

一个程序集、一份 `AutoLayer.ini`、两个窗口互相能跳，命令全保留。
`AutoLayer.ini` 文件名与格式和 V1.0 完全一致，旧配置直接沿用。

## 兼容性怎么做到的（改代码前先看这段）

目标：**AutoCAD 2016 及以上通吃**。

做法：用 **AutoCAD 2016 的托管程序集（20.1）+ .NET Framework 4.5** 编译。

原因：AutoCAD 的 `acdbmgd` / `acmgd` / `accoremgd` **都不是强名称程序集**
（CLR header 里没有 StrongNameSigned），CLR 只按简单名绑定、不校验版本。
所以低版本编出来的 DLL 能被高版本 CAD 直接加载。

反过来不行：用 2021 的引用编，到 2016 上会因为调用了 2016 里不存在的成员而抛
`MissingMethodException` / `TypeLoadException`。

副作用（其实是好事）：用 2016 的引用编译时，2017+ 才有的 API 会**编译期报错**，
不会埋到用户机器上才炸。改代码时如果编译报「找不到某个成员」，先想想它是不是新版本才有的。

> `PieWindow.cs` 不要用 `VisualTreeHelper.GetDpi`（.NET 4.6.2 才有），
> 需要时用 `DpiCompat.cs` 里的等价实现。

## 目录

```
源码/
  Core/                 归层引擎 + 命令（只依赖 acdbmgd / accoremgd）
    Hook.cs             入口 IExtensionApplication、命令钩子、HPLAYER 复位
    Commands.cs         全部命令：QH / CC / AL*
    Config.cs           AutoLayer.ini 读写
    Rules.cs / Layers.cs / CommandAliasHost.cs / Events.cs / UiBridge.cs
  UI/                   WPF：主面板 / 状态条 / 右键圆盘 / 设置
    PanelHost.cs        Core 反射调界面的唯一入口
    PanelWindow.*       归层配置面板（QH）
    StatusHud.cs        画图区顶部状态条
    PieWindow.cs        右键长按圆盘
    SettingsWindow.*    设置窗口：一行一个功能 + 帮助页
    DpiCompat.cs        net45 下替代 VisualTreeHelper.GetDpi
    QuickSwitch/        图层切换面板（CC）
      LayerSelectionWindow.*   面板本体
      QuickSwitchHost.cs       CC 命令的宿主逻辑
      LayerService.cs 等       图层读写 / 预亮 / 最近图层
  ViewportTool/         视口图层状态（2026-11-13 由独立测试版并入）
    VpCommands.cs       命令 + 启动钩子 VpStartup（由 AutoLayerApp 调）
    VpProfileWatcher.cs 200ms 轮询 CVPORT，进出视口切/恢复归层配置
    VpTagStore.cs       视口标签：图纸级 Xrecord（句柄 → 配置名），别人看不出
    LayerProfile.cs     访问 ConfigStore / LayerEvents 的薄封装
    VpBatch.cs          批量生成布局与视口
    VpService.cs        视口冻结层的读写
    FrameLib.cs         图框库目录扫描 + 内框「最大空白矩形」识别
    ProfileMatch.cs     状态名 ↔ 配置名解析（同名自动绑 / 模糊建议）
    VpModel.cs / VpStore.cs  状态与图框模型、ViewportState.ini 读写
    Ui/VpPanel.*        视口面板（VPT）
  Plugin/
    Y_CADToolbox.csproj 单文件工程（Core + UI + ViewportTool 一起编成 AutoLayer.dll）
refs/                   AutoCAD 2016 (20.1) 托管程序集，编译用（不随交付包分发）
```

## 编译

```bash
dotnet build 源码/Plugin/Y_CADToolbox.csproj -c Release
```

产物：`源码/bin/Y_归层工具V2.1.dll`（程序集名是 ASCII 的 `AutoLayer`，
交付文件另存一份中文名 —— WPF 的 BAML 资源路径对非 ASCII 程序集名不友好）。

> 交付前先 `rm -rf 源码/Plugin/obj` 干净重建，避免中间缓存污染。
>
> 历史说明：曾有过 `-p:TestBuild=true` 的共存测试版渠道（命令名带 T、独立 ini），
> V2.0 起已整体移除；2026-11-12 又把源码里残留的 `#if TESTBUILD` 段也删干净了，
> 现在工程里没有任何条件编译分支。

### refs 从哪来

`refs/` 里是 AutoCAD 2016 的托管程序集，来自 NuGet 官方包（Autodesk 发布）：

| 包 | 版本 | 提供 |
|---|---|---|
| `AutoCAD.NET` | 20.1.0 | AcMgd / AdWindows / AcCui |
| `AutoCAD.NET.Core` | 20.1.0 | AcCoreMgd |
| `AutoCAD.NET.Model` | 20.1.0 | AcDbMgd |

```bash
nuget install AutoCAD.NET -Version 20.1.0
```

没有 `refs/` 时工程会退回本机 CAD 安装目录（`CadInstallDir`，默认
`C:\Program Files\Autodesk\AutoCAD 2021`）——**那样编出来的只能在高版本 CAD 上用**，
只在临时验证时这么干。

## V2.0 相对 V1.0 的主要改动（改代码前值得知道）

1. 新增图层切换面板（`UI/QuickSwitch/`），`CC` 命令放在 `Core/Commands.cs`
   （Core 不引用 acmgd），经 `UiBridge` 反射调 `PanelHost.ShowQuickSwitch()`，
   和 `QH` 走同一条路。
2. 「最近使用」存在 `AutoLayer.ini`（`最近图层 = A ; B ; C`）；
   「同时设为当前图层」的勾选状态也记住（`切层同时设为当前`）。
3. `HPLAYER` 复位并进 `AutoLayerApp`，每张图纸激活/新建时都复位；
   切层走 `CLAYER` 系统变量官方通道，切层后归层不受影响。
4. 两个窗口标题栏各加一个入口按钮（QH → CC 用 `SendStringToExecute("_.CC ")`，
   因为非模态面板里直接调选择集会失败）。
5. 全部窗口统一深色皮肤与键盘流；QH / CC 命令名均可在 ALSET 里自定义。
6. `AutoLayer.ini` 文件名与格式不变，任何旧版配置直接能用。

## 设置窗口（ALSET）结构

**按功能排，不按鼠标键排**（2026-11-14 定）。用户的问题是「我想给圆盘配个键」，
主语必须是功能；以前三个鼠标键各占一页，同一个功能散在三页里，想换颗键得翻三遍。

窗口两页：`设置` / `帮助`。**设置页只有控件，说明文字全在帮助页。**

设置页只有四行 + 一条阈值（列序：功能 / 快捷键 / 命令 / 鼠标键）：

| 行 | 快捷键字段 | 命令别名字段 | 鼠标键 |
|---|---|---|---|
| 主面板 | `HotkeyMods/Key` | `CommandAlias` | `MouseRight/X1/X2` |
| 图层切换 | `CcHotkey*` | `CcCommandAlias` | 同上 |
| 视口工具 | `VpHotkey*` | `VpCommandAlias` | 同上 |
| 圆盘 | `PieHotkey*` | `PieCommandAlias` | 同上 |

四个命令别名都能改（`CommandAliasHost` 里就是四个 `Slot`，走同一条 LISP
`(defun c:XX () (command "目标命令") (princ))`）。命令名撞车 / 不合法在保存时拦下。

**两个配置文件并排在设置页底部**，名字直接分开叫：

| 标签 | 文件 | 装什么 |
|---|---|---|
| 归层配置 | `AutoLayer.ini` | 映射表、开关、快捷键、命令名、鼠标绑定 |
| 视口配置 | `ViewportState.ini` | 图框、视口状态 |

格子里只显示**文件名**（一列放不下全路径，而且名字才是要区分的），全路径挂 ToolTip。
VPT 面板原来的 ⚙ 二级页整体取消，那个滑杆图标现在就是"打开设置窗口"。
**视口配置的导出导入即时生效**，不走保存/取消 —— 它和主配置是两个文件、两个生命周期。

**图层兜底留在 VPT 面板**（「视口标记」页底部）：它是「视口 → 配置」这条线上的行为，
跟标记是同一件事，放设置里反而断了上下文（2026-11-14 用户定）。

**两套方向要翻一次**：界面是「功能 → 用哪颗键」，ini 里存的是「哪颗键 → 干什么」。
`SettingsWindow._mouse[功能] = Slot*`，落盘时由 `BindOfSlot()` 反查成
`MouseRight/X1/X2 = MouseBind.*`；读配置时 `SetSlot()` 反向填。
`ini` 键名一个都没改，老配置直接能用。

一颗鼠标键只能绑一个功能：改某行的下拉时，`Mouse_Changed` 会把原来占着这颗键的功能
自动设回「不用」，否则两个功能抢一颗键，谁先触发全看循环顺序，用户猜不到。

快捷键四组各自独立，`HotkeyHost` 按下标派发（`ActPanel/ActQuickSwitch/ActViewport/ActPie`，
**顺序必须和设置窗口的行序一致**）。保存时拦两件事：命令名不合法或重名 / 两个功能配了同一组键。

界面上的说明文字一律进帮助页，设置页不留常驻解释 —— 提示只写 ToolTip，
抓键失败的反馈直接写在那个格子里（「要带 Ctrl 或 Alt」）。

## 自测

命令注册 / BAML 资源检查（不启动 CAD，用 MetadataLoadContext 读元数据）：

```
期望：29 个命令（QH / CC / CCHELP / ALON / ALOFF / ALTOGGLE / ALSET / ALPIE /
      ALSTATUS / ALKEY / ALMOUSE / ALTRACE / ALEDIT，
      以及 VPT / VLPANEL / VLHELP / VLFRAME / VLLIB / VLINNER / VLCAP / VLUPDATE /
      VLAPPLY / VLBATCH / VLLIST / VLDEL / VLTAG / VLUNTAG / VLTAGS / VLALIGN），
      6 个 BAML（ui/panelwindow、ui/settingswindow、ui/confirmdialog、
      ui/namedialog、ui/quickswitch/layerselectionwindow、viewport/ui/vppanel），
      依赖 AcDbMgd / AcMgd / AcCoreMgd v20.1.0.0。
```

## 性能约定（2026-11-16 定版，改代码前必看）

插件的开销集中在四条热路径上，改代码时别在这几条上退回去：

| 热路径 | 触发频率 | 现在怎么做的 |
|---|---|---|
| `CommandHook.OnWillStart` | 每条 CAD 命令 | 先 `RuleResolver` 过滤，只有命中类别/覆盖才开事务 |
| `LayerSelectionWindow` 列表 | 每次打开 CC | 开虚拟化，只实例化可视行 |
| `PreviewHighlighter.HighlightLayer` | 每次换行预览 | 会话索引，O(1)；不再全图扫描 |
| `StatusHud.Tick` | 每 500ms 常驻 | 内容签名去重 + 颜色查询 2 秒缓存 |
| `VpProfileWatcher.OnTick` | 每 200ms 常驻 | 命令运行期间挂起 400ms，不碰 COM |

### 已经踩过的坑

**CC 列表不能关虚拟化。** `VirtualizingPanel.IsVirtualizing="False"` 或
`ScrollViewer.CanContentScroll="False"` 任一出现，虚拟化就失效，全部图层行
都要实例化。实测成本约 1.25 ms/行：

| 图层数 | 关虚拟化 | 开虚拟化 |
|---|---|---|
| 200 | 245 ms | 23 ms |
| 500 | 750 ms | 22 ms |
| 1000 | 1285 ms | 19 ms |

要像素级滚动又保留虚拟化，用 `VirtualizingPanel.ScrollUnit="Pixel"`（.NET 4.5 起支持）。

**虚拟化下容器是「滚到才生成」的。** `ItemContainerGenerator.ContainerFromIndex`
对未滚到的行返回 null。要拿容器必须先 `ScrollIntoView` 再 `UpdateLayout`，
顺序反了焦点会停在 ListBox 而不是行上。

**预览索引里的 List 不能直接赋给 `_currentHighlighted`。** `ClearHighlight`
会 `Clear()` 它，等于把索引清空了。必须复制一份。

**`FillTagList` 的判重要放在解析布局名之前。** 签名只由「标签映射 + 当前视口句柄」
决定，布局名只影响显示文本。顺序反了就是每次 CVPORT 变化都白跑两趟事务。

**批量读图层别在循环里开事务。** `foreach (AllNames) { TryGetLayerColor(); StateOf(); }`
是 2N+1 个事务，200 层要 400 次。用 `LayerService.Snapshot(db)` 一次读完。

**`Trace.Add` 挂在每条命令上，不要在热路径格式化时间。** 存 `DateTime` 值，
`Recent()` 时再 `ToString`。

### 验证手段

`ui-preview` 渲染 CC 面板时会打印列表虚拟化的实际取值：

```
list: IsVirtualizing=True  CanContentScroll=True  ScrollUnit=Pixel  实建容器=9/10
```

容器数明显小于总行数 = 虚拟化生效。**别用「搜 DLL 里的 `IsVirtualizing` 字符串」
判断** —— BAML 里的附加属性名不是明文存的，搜不到属正常。

## 视口图层状态（V2.0 追加，2026-11-13 并入）

三件事：

1. **刷取视口**：记下某个视口里被冻结的图层名（跨图纸按名匹配），存 `ViewportState.ini`。
   **刷取即绑定**：顺手把该视口绑到当前归层配置，进它自动切过去，不用再单独 `VLTAG` 一次。
2. **批量生成**：按图框内框铺视口，每个状态一张图纸，或一个布局横排多个。
3. **进视口自动切归层配置**：视口上有一条「标签」（句柄 → 配置名），
   进这个视口就把主插件的当前配置切过去，退出时恢复。

**标记视口**（外来图纸手动绑定）：面板「视口标记」里选好配置 → 点「标记视口」→
在图纸里**点选或框选**多个视口，一次标一批。命令行 `VLTAG` 等价（回车用当前配置，
`s` 换一个）；`VLUNTAG` 同样支持框选批量取消。

**图框：拾取为主路径**（2026-11-14 调整，给插件减负）。面板「批量生成」页第一行
下拉框右边就是「拾取图框」（`VLFRAME`），在图纸里点一个块或框选整组线，
插件把图框存进自带的库（插件目录 `FrameLib.dwg`），换任何图纸都能用。
下面一行是当前图框的尺寸摘要 + 重框内框 / 改名 / 删除 —— 这四件事原本都埋在
⚙ 二级页里，减负后它们就是这条线的全部操作，所以全部提到一级。
**面板不再提供「图框库目录 + 扫描」**；命令行 `VLLIB` 仍保留
（批量导入 dwg 目录的老路子，面板不暴露）。

- 整图只有一个块参照 → 用它的块定义（块名、缩放都记下来）
- 其它情况 → 整张图当一个图框，生成时 `Database.Insert` 把整图做成块插进来

**内框自动识别**：外框内部求「最大空白轴对齐矩形」——
把所有图形当障碍，闭合矩形只算四条边（不算实心，否则图框线自己就把内部封死了），
图签栏、盖章区、散落文字自然被绕开。识别不准点「重框内框」（`VLINNER`）手动框一次，
之后重新扫描**不会覆盖**（`FrameTemplate.InnerManual`）。

`VLLIB` 扫描合并按文件路径认人：重复扫描不会重复添加，用户改过的名字和手动内框都保留；
文件被移走也不自动删模板（只是生成时退回画矩形）。

生成时的查找顺序：当前图纸块表 → `FrameLib.dwg` → 拾取时的源图纸 → 画矩形。

**状态集（V2.1，2026-11-16 新增）**：`VpConfig.Groups` 是 `List<StateGroup>`，
一个公司 / 项目一套，组与组之间允许重名、组内仍唯一。要点：

- `VpConfig.States` **不再是全量**，而是 `ActiveGroup.States` —— 当前那一套。
  刷取 / 批量生成 / 对齐 / 图层兜底全都自动只作用于当前集，不用逐处改。
  唯一例外是序列化：`VpStore.ToText` 必须遍历 `Groups`，走 `States` 会把别的集写丢。
- 默认组的名字是**空串**，界面上显示「默认」；ini 里写成 `[状态集: 默认]`，
  读回时 `GroupKey()` 还原成空串。老配置没有 `[状态集:]` 段头，状态全落默认组，不用迁移。
- 组名解析用 `FindGroup` / `EnsureGroup` / `RenameGroup` / `RemoveGroup`；
  改名会同步 `ActiveGroupName`，删最后一个组会被拒（面板得有地方放状态）。
- 当前组落盘在 `当前状态集 = xxx`；指向不存在的组时 `FromText` 末尾退回第一个组。
- 往返测试：`../_probe/roundtrip`（只编 `VpModel.cs` + `VpStore.cs`，不引 AutoCAD，
  所以放在交付包外面）—— 覆盖同名状态分属两组、老格式、默认组混存、改名/删除、悬空当前组五种情况。
  跑法：在 `归层工具` 目录下 `dotnet run --project _probe/roundtrip/RoundTrip.csproj`。

**自动绑定（V2.1 新增）**：状态用哪套配置，走 `ProfileMatch.Resolve` 三级解析 ——

1. 状态上显式记的绑定名（刷取 / 标记时写入），且配置确实存在 → 用它
2. 状态名和某个配置名完全一样 → **自动绑**，不用再手动标一次
3. 名字接近（差括号 / 空格 / 一个字）→ 只给建议，**不自动用**
   —— 自动套一个「差不多」的配置，切错了比不切更难发现

**对齐配置**（2026-11-16 从命令行搬进面板）：状态列表标题行的「对齐配置」不再往命令行塞
`_.VLALIGN`，而是打开面板内浮层（`AlignOverlay`）：一行一个状态，右侧「绑定到」点开
同一窗口里的**选择层**（`AlignPicker`，不是 ComboBox/Popup —— 那套 Popup 在 CAD 面板里
弹不出来），右侧两个方向按钮决定名字往哪边走：

| 按钮 | 做什么 | 保存时的影响面 |
|---|---|---|
| 跟配置 | 状态名 → 所选配置名 | 只动这条状态，撞名自动加后缀 |
| 跟状态 | 所选配置名 → 状态名 | 主插件配置名 + **所有状态集**里的绑定 + **本图**标签 |

两个方向互斥，只做标记，点「保存」才真改，再点一下撤销。目标名字已被别的配置占着时
该行跳过，保存后提示跳过了几个 —— 配置名不允许重复，宁可不动也不把两套并成一套。
顶部「一键同名」把名字完全一致的一次绑上；「按建议预选」把黄点（≈ 接近）的行按建议填上。
`VLALIGN` 命令保留为命令行版：`B` 绑定某个配置 / `R` 把状态名改成配置名 / 回车跳过。
面板状态列表右侧直接显示判定：`→ 已绑`、`= 同名`、`≈ 接近`、`— 无匹配`。
配置名也能在「视口标记」页单独改（「配置改名」按钮），同步范围和「跟状态」一致。

**图层兜底（V2.1 新增，可关）**：进视口发现**没有标签**时，拿这个视口的冻结层去状态列表里
找**完全一致**的那个状态（数量相同 + 每个名字都对得上），用它的配置。
默认开，开关在「视口标记」页最下面（跟标记是同一件事，所以放同一页）。
视口配置的导出/导入在 ALSET 设置窗口底部（2026-11-14：面板的 ⚙ 二级页整体取消，
那个滑杆图标现在就是"打开设置窗口"）。

**标签存在哪**：图纸的命名对象字典里**一条 Xrecord**（键 `AUTOLAYER_VP_TAG`），
内容 = 标记串 + 句柄/配置名交替平铺。不是每视口一条，50 个视口约 1KB。
Xrecord 不出现在 LIST / 特性 / 打印里，不生成实体和图层 —— 没装插件的人看不出区别。
需要保存图纸才持久化；WBLOCK 分发会丢（丢了只是不切配置，不坏图）。

**外人怎么清掉它**（没装插件也能做）：

```lisp
(dictremove (namedobjdict) "AUTOLAYER_VP_TAG")
```

**触发方式**：200ms 轮询 `CVPORT`（`SystemVariableChanged` 对 CVPORT 不可靠，没当主路径），
值变了才开事务读标签。命令运行中跳过并把状态复位，不会漏判。
`VpStartup.Start()` 由 `AutoLayerApp.Initialize()` 调 —— 一个程序集只能有一个
`IExtensionApplication`，独立版自己的入口已删除。

**边界**：模型空间（TILEMODE=1）和图纸空间本身（CVPORT=1）没有标签；
布局主视口（Number=1）不参与。切配置**只改内存**，不写 `AutoLayer.ini`。

**体检**：`VLTAGS` 除了列标记，还会报三类「标了但不会生效」——配置名在主插件里不存在、
标记的视口是关着的、还有多少视口没打标记。
