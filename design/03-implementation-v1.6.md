# Y_图层工具箱 · v1.6/v1.7 落地说明（2026-11-11）

按 `01-visual-spec.md v1.5` + `02-ux-spec.md v2.4` 出的第一版**代码落地**。
原则：**只动视觉与死代码，不动任何行为**——命令、配置、交互逻辑与 V1.0 完全一致。

---

## 1. 本版做了什么

| 文件 | 变更 |
|---|---|
| `UI/UiTheme.cs` | 补齐 §2 全部 40 个 token（LineControl/LineHover/SelectionBg/SelectionMark/AccentInk/WarnSoft/WarnLine/Danger 三档/Hover/HoverRow/Pressed/FocusRing/Track/SwitchTrack…）；`TextFaint` #6E7683 → **#8A93A0**（AA 达标）；新增 §3.1 六档字号常量、§4 尺寸阶（行高 28/26、标题栏 48/32、圆角 4/6/8/15、内容边距 18） |
| `UI/QuickSwitch/LayerSelectionWindow.xaml` | **整窗重写**（§7.4 骨架）：紧凑标题栏 32（标题 13.5 + 打开主面板 26×26 Path「窗口+右箭头」+ 12 净空 + ✕ 32×32 Path）；搜索框 30/r6/Raised/LineControl/内边距 9 + 内嵌清除 ✕ 24；分类 chip 22/r4（选中 AccentSoft+AccentLine 描边+AccentInk 字，不再实心蓝）；列表底 Sunken + 上下 1px Line；行高 28、色块 12×12 r2 带描边、名称 12、状态列 10.5 定宽 72 右对齐、行间 0 改 1px 分隔线；**选中三重编码**（左 2px SelectionMark + SelectionBg 底 + 名称 SelectionText）；关闭按钮 hover 中性（**#C42B1C 红底已删**，§7.2）；底部复选方块 14×14 r4；窗口尺寸限定 320–480 × 320–720 |
| `UI/QuickSwitch/LayerSelectionWindow.xaml.cs` | 删除主题死代码：`ApplyTheme` / `SetBrush` / `_isDark` / `IsDarkTheme` / `ThemeToggle_Click`（§7.3 清单，◐ 按钮随 XAML 一起没了）；chip 取色键改 Yb.*；WindowChrome CaptionHeight 40 → 32 |
| `UI/QuickSwitch/LayerInfo.cs` | 🔒❄️● 字形图标 → **文字状态列**「已关闭 / 已冻结 / 已锁定」（红线 6：禁止字形图标；§5.5：状态列文案定宽不换行） |
| `UI/QuickSwitch/HighlightBackgroundConverter.cs` | #0078D4 → SelectionBg（与"选中"同语义，消灭第二个蓝底） |
| `UI/QuickSwitch/HighlightForegroundConverter.cs` | 删 IsDark 分支，固定 SelectionText / Text |
| `UI/PieWindow.cs` | 删除 `PieSkin.Light` 皮肤分支与枚举值（§7.3 清单最后一项死代码） |
| `UI/PanelWindow.xaml`、`SettingsWindow.xaml`、`ConfirmDialog.xaml`、`NameDialog.xaml` | 裸色值全部换成窗口内 `Yb.*` 画刷（红线 1）；字号收敛 11→11.5、13→13.5；圆角 3→6；控件静止边界统一 LineControl、悬停边界 LineHover（结构线保持 Line/LineStrong，"有框的是控件，有线的是结构"）；开关圆钮 #E8ECF2 → SwitchKnob |
| `UI/PanelWindow.xaml.cs`、`UI/LayerPicker.cs` | C# 侧字号散值收敛（9/9.5→8.5、11→11.5） |

## 2. 验证

- `dotnet build -c Release`：**0 警告 0 错误**（正式版 + `-p:TestBuild=true` 测试版）。
- DLL 自检：13 个命令注册、5 个 BAML 资源、无 #C42B1C / ThemeToggle 痕迹。
- **离屏渲染**（`tools/ui-preview/`，真实 XAML + 桩代码，不启动 CAD）：CC 面板 360×480 与 2× 截图、主面板 470×640 截图，见 `design/.shots/v16-*.png`。标题栏 / 搜索框 / chips / 选中行三重编码 / 状态列 / 复选框全部按规格呈现。

## 3. 交付物（merged/dist，全部更新）

- `Y_图层工具箱V1.0.dll`（210944 字节）
- `Y_图层工具箱V1.0.zip`（DLL + 使用说明）
- `Y_图层工具箱_测试版.dll` / `.zip`（命令加 T 后缀、AutoLayer.test.ini，可与旧版共存实测）
- `Y_图层工具箱V1.0_源码.zip`（44 文件）
- `使用说明.txt` 末尾追加「界面说明（2026-11 统一版）」

## 4. 已知偏差（对 01-visual-spec 的登记）

| # | 偏差 | 理由 |
|---|---|---|
| 1 | **列表内「最近使用 / 全部图层」分隔标签行保留**（规范红线 15 要求整行删除，改由最近快捷行承担） | 快捷行/模式条属**结构变更**（新增行为），本轮"只动视觉"不擅自做；删掉标签行又没有快捷行，最近使用就没了入口。等结构项拍板后一起切 |
| 2 | 行分隔线末行仍画 | 需要知道"是否末行"，ListBox 无直接绑定；影响仅一条线 |
| 3 | 图层色块三件套做了两件（内衬+1px 描边），未做 EmptyArtFill 独立内衬层 | 色块本身 12×12，三层嵌套收益极低 |
| 4 | chip 计数角标未做 8.5 TextFaint 分离样式 | tooltip 已有「N 个图层」；chip 内计数跟随文字色 |
| 5 | 滚动条保留系统默认（§6.13 宿主崩溃约束，规范允许退回） | 稳定性优先，规范明文许可 |
| 6 | 焦点环（FocusRing 2DIP）未逐控件实现 | 默认键盘虚线框保留；完整焦点环涉及全部控件模板，随结构项一起做 |

## 5. 未落地项（需要拍板再动，全是结构/行为）

> **v1.7 更新（2026-11-11）：用户拍板"按最后的 UI 文档实现"，以下 6 项已全部落地，见第 7 节。本节保留作历史记录。**

1. **任务模式条**（§6.15 四态 + 「改选 ▸」）—— 需要新增"改选"流程与结果回写。
2. **最近快捷行**（§6.16，≤5 chip）与**列表内分组行删除** —— 与偏差 1 绑定。
3. **底部「取消 + 主按钮」**（现行为是"点选/回车即生效、关窗即应用"，规范 V6 也认可即时流；改双按钮 = 改操作习惯）。
4. **空状态线稿**四类（交互 §3.9）。
5. **应用失败/部分成功保持面板打开 + 模式条告警**（验收 13）。
6. chip 组 `+N` 折叠（§6.8 换行纪律）与 Tab 停靠点收敛。

## 6. 复现验证

```
# 编译
cd merged/src/Plugin
dotnet build Y_CADToolbox.csproj -c Release                       # 正式版
dotnet build Y_CADToolbox.csproj -c Release -p:TestBuild=true ^
  -p:OutputPath=..\bin_test\ -p:BaseIntermediateOutputPath=..\obj_test\   # 测试版

# 离屏渲染（不启动 CAD）
cd tools/ui-preview && dotnet build -c Release
bin\Release\net8.0-windows\UiPreview.exe out.png 360 480 cc       # 图层切换面板
bin\Release\net8.0-windows\UiPreview.exe out.png 470 640 panel    # 主面板
```

---

## 7. v1.7：结构项全量落地（2026-11-11 第 2 批 · 用户拍板"按最后的 UI 文档实现"）

对 `02-ux-spec.md §3.2–§3.10` 的六项结构变更逐条实现。**命令 / 配置 / 快捷键不变；改的只是 CC 面板内部结构与反馈方式。**

| 规格条目 | 实现 |
|---|---|
| §3.3 底部「取消 + 主按钮」 | 底部 44：左勾选框（仅对象模式）+ 右 [取消 32] + [主按钮 32]；主按钮文案随模式变化（移到该层/设为当前层），**悬停补目标层名**；无选中行置灰 + 模式条给「先选一个目标图层」 |
| §3.4 模式条 + 改选 | 模式条 26 四态（对象 AccentSoft+箭头 / 当前层 Sunken+图层 / 警告 WarnSoft+三角 / 错误 DangerSoft+叉，底+描边+图标+文字色四条同时区分）；「改选 ▸」文字按钮（hover 只换字色、点击区满条高、Alt+R、进 Tab 序列、读屏名念数量）；改选 = 关窗 → 宿主 `PromptReselect` → 重选完再弹回，Esc 放弃零改动 |
| §3.4 图元失效 | 应用时逐图元校验：全部失效 → 模式条「图元已失效，将只切换当前图层」+ 允许改选 + 勾选框隐藏，降级只切当前层 |
| §3.5 打字即重置 chip | 搜索框一有输入，chip 视觉回「全部」 |
| §3.5.3 chip +N 折叠 | chip 区**永不滚动**；布局后按 ActualWidth 计算最多 2 行，放不下的折叠进末尾 `+N`（字母序末位优先，`其他` 优先被折）；`+N` 点开纵向清单浮层（项高 28、系统滚动条、Esc/点外关闭、焦点回 +N）；被折叠组被选中时 +N 承载选中态 + tooltip 报当前筛选 |
| §3.5/§6.16 最近快捷行 | 列表上方独立行 26（前缀「最近」10.5 + ≤5 chip：色块+名称 11.5 TextDim、MaxWidth 120、常态底 Sunken）；出现条件 = 搜索空 ∧ chip=全部 ∧ ≥2 项，否则整行收起；**单击只看（设当前项+预览）、双击才做**；1 个 Tab 停靠点、←→ roving、Enter 应用 |
| 红线 15 分组标题行 | 列表内「最近使用/全部图层」标题行整行删除，列表只有数据行 |
| §3.6 键盘流 | 任意位置打字即搜索（PreviewTextInput 重定向）；搜索框 Enter 应用当前项→第一可见行、空列表无操作；列表 Backspace 回搜索框删一字；Esc 有词先清词再关窗；Home/End/PageUp/PageDown（±10）；↑↓ 到顶/底停住不回绕；Ctrl+F 全选搜索词；Alt+W 切勾选；Alt+R 改选；chip 组/快捷行各 1 个 Tab 停靠点（TabNavigation=Once） |
| §3.8 失败保持打开 | `MoveObjectsToLayerEx` 逐图元开写：全失败 → 模式条错误态「没能移动：原因」；部分成功 → 警告态「N 个已移动，M 个被跳过」；都**不关窗**；换选中行后告警自动让位 |
| §3.8 目标层关闭/冻结 | 删掉应用中途的 MessageBox：已关闭 → 自动打开 + 模式条内联提示 + 成功消息带「已自动打开图层」；已冻结 → 提示「设为当前层后无法绘制」不阻止 |
| §3.9 空状态四类 | 内联替换列表区（搜索框/chip/模式条/快捷行/底部全留原位）：无匹配（全部/指定分组两种文案与按钮组）、前缀组为空、图纸无图层（宿主不再拦截，开窗走空状态+关闭按钮落焦点）；文案含当前过滤条件、出路按钮全为 32 高次按钮、无主按钮 |
| 成功反馈 | 命令行 `[图层切换] N 个图元 → 层名（可用 U 撤销）` + `StatusHud.Flash` 胶囊条闪层名；部分成功不关窗不写命令行 |

**同批修正**：色块三件套补齐 EmptyArtFill 内衬（v1.6 偏差 3 关闭）；删除多列布局死代码（ArrangeColumnMajor/ItemWidthEstimate，红线"多列布局禁止"）。

**仍登记的偏差**：滚动条维持系统默认（§6.13 宿主崩溃约束）；焦点环未逐控件实现（沿用默认键盘虚线框）；chip 计数角标未做 8.5 分离字号（已做 8.5 淡色计数，与偏差 4 的原表述合并）。

**验证**：正式 + 测试版 0 警告 0 错误；13 命令、无旧主题痕迹、新结构符号在 DLL 内可见；`tools/ui-preview` 五态离屏渲染（object/current/warn/error/empty）截图 `design/.shots/v17-cc-*.png`，模式条四态色、快捷行、底部按钮组、空状态三行结构全部按规格呈现。

---

## 8. v1.9：「回到上次选中」+ 主面板图标直开面板（2026-11-11 第 4 批 · 用户报障）

两个用户报障一起修，互相关联：

| 项 | 实现 |
|---|---|
| 报障 1：改选的价值 | 用户拍板原话：「如果上次选中然后修改了，但是现在没选中，可以让他回归上次选中的物体进行修改图层，这种才值得改选；选中物体直接改的时候就没必要有改选」。实现：`QuickSwitchHost` 静态记忆 `_lastPicked`（打开面板时手头有预选就记录，按 Document 绑定），窗口空手打开（当前层模式）或图元失效降级时，模式条右侧出现 **「回到上次选中（N 个）」**（Alt+R）：把那批图元重新装回对象模式，与构造初始态对齐（勾选框恢复、主按钮变「移到该层」）。已删/失效图元由 `GetLastValidPicked` 过滤（`IsValid && !IsErased && !IsEffectivelyErased`）；全没了按钮自动消失；换图纸/关重开不跨文档。**对象模式（手头已有选中）不再显示改选**；原「关窗→CAD 重选→再弹」流程与 `PromptReselect`、`ReselectRequested` 整体删除 |
| 报障 2：主面板图层图标弹不出 CC | 根因：`GetSelectedObjects` 无预选时在命令行弹 `GetSelection` 交互提示，面板要等用户回车/框选完才出现——从主面板图标点进来时提示藏在命令行里，用户以为面板打不开。修复：删掉交互提示，无预选**直接开面板**（当前层模式 + 回归按钮）。换一批物体的方式回归 CAD 原生习惯：Esc 关面板 → 图上选好 → 再敲 CC |

**行为变化点**（相对 v1.7）：CC 无预选不再有命令行选择提示；对象模式无改选按钮；新增回归上次选中。§3.4 的「改选 ▸ 重选流程」按用户 2026-11-11 拍板作废，替换为「回归上次选中」。

**验证**：正式 + 测试版 0 警告 0 错误；DLL 自检（UTF-16 探测）确认「回到上次选中 / GetLastValidPicked」已编入、旧提示文案已清除；预览工程编译通过（回归按钮在桩内因无 CAD 文档自动隐藏，布局槽位沿用 v1.7 已验证的模式条右侧位置）。

---

## 9. v1.9b：回归按钮图上高亮 + 模式条叠字修复（2026-11-11 第 5 批 · 用户报障）

| 项 | 根因与实现 |
|---|---|
| 点「回到上次选中」应让物体预览出来 | `Reselect_Click` 装回图元后调 `PreviewHighlighter.RestoreSelection`（grips + 亮显）；后续行预览临时替换、关窗时恢复本集（既有机制） |
| 模式条文字与按钮叠字 | 根因：文字在横向 `StackPanel` 里（子项无限宽），`TextTrimming` 永不生效，文字伸到右对齐按钮底下。修复：模式条改双列 Grid（`*` + `Auto`），文字列受约束后省略号正常触发；按钮模板写死的「改选」TextBlock 改为 `ContentPresenter`（文案由代码按状态写入） |
| 常驻「· 先选一个目标图层」后缀 | 按用户拍板删除（挤宽度且与回归按钮叠字）；无选中行只保留主按钮置灰 |

**验证**：两版 0 警 0 错；最坏情况（360 宽 + 完整模式文字 + 回归按钮同屏）离屏渲染 `.shots/v19-cc-current.png`——文字省略号截断、按钮完整、无叠字。

---

## 10. v1.9c：纯当前层模式撤掉模式条（2026-11-11 第 6 批 · 用户拍板）

用户原话：「常驻的仅切换当前图层这个好像也没用吧，或者放到左下角作为提示」。两者都做了：

| 项 | 实现 |
|---|---|
| 模式条按需出现 | 纯当前层模式（无警告无错误）`ModeBar.Visibility = Collapsed`（行高 Auto，列表随之上移一档）；对象模式 / 图元失效 / 警告 / 错误反馈仍占条 |
| 左下角提示 | 底部 44 左侧新增 `BottomHint`（10.5 TextFaint「仅切换当前图层」），仅「当前层模式 + 有图层 + 无回归按钮」时显示——回归按钮的文案已自说明，两者同显会在窄窗口挤到「取消」 |
| 「回到上次选中」挪位 | 从模式条右侧移到底部左下角（当前层模式的主出入口，位次更合理）；图上高亮预览行为不变 |

**验证**：两版 0 警 0 错；离屏渲染 current（无模式条、列表变高、左下回归按钮宽裕）/ object（模式条 + 勾选框正常）/ warn 三态，`.shots/v19c-cc-*.png`。

---

## 11. v1.9d：回归预览双通道亮显 + 按钮回到模式条（2026-11-11 第 7 批 · 用户报障）

| 项 | 根因与实现 |
|---|---|
| 点「回到上次选中」图上没有预览 | 根因：`SetImpliedSelection` 设置的选中/grips 要 CAD 编辑器拿回焦点才刷新，而 CC 面板是模态窗口（ShowDialog）一直压着焦点，图上什么都不画。修复：`PreviewHighlighter` 新增「直接亮显」通道 —— `Entity.Highlight()` 在图元图形上直接画高亮，不依赖焦点；`Reselect_Click` 改调 `RestoreSelectionWithHighlight`（implied selection + 直接亮显双保险）。列表行预览仍只用 implied selection（层上图元可能成千上万，逐个画太重，且那条路正常） |
| 亮显生命周期 | `_directHighlighted` 单独记账：`HighlightLayer`（行预览）/ `ClearHighlight` / `RestoreSelection`（关窗还原）都会先撤直接亮显再走原逻辑；宿主 finally 的 `ClearHighlight` 兜底。2016 API 没有 `Highlight(bool,bool)` 重载，用无参 `Highlight()`/`Unhighlight()` |
| 「回到上次选中」挪回模式条 | v1.9c 挪到左下角，用户实测不顺手，回到模式条右侧原位（v1.9b 位）。模式条 Grid 加第三列（Auto），按钮 VerticalAlignment=Stretch 占满条高 26。纯当前层模式默认仍整条隐藏（v1.9c 拍板保留），**有回归按钮时占条**；左下角 `BottomHint` 仅在无回归按钮时显示（有按钮时模式条左侧文字就是模式说明，同显重复） |

**验证**：正式 + 测试版 0 警告 0 错误；DLL 自检（UTF-8 元数据探测）确认 `RestoreSelectionWithHighlight / HighlightObjects / ClearDirectHighlight` 已编入两版；离屏渲染 `.shots/v19d-cc-current.png`（360 宽：模式条文字省略号 + 回归按钮完整无叠字）、`v19d-cc-narrow.png`（320 宽同验）、`v19d-cc-plain.png`（无回归时极简布局 + 左下角小字）。

---

## 12. 测试版渠道取消（2026-11-11 第 8 批 · 用户拍板）

用户原话：「不要测试版了，我现在分不清哪个是我要的那个版本了」。此后**只出一份正式版**：

- csproj 删除 `TestBuild` PropertyGroup（`-p:TestBuild=true` 不再产生任何东西；源码里 `#if TESTBUILD` 段成为永不再触发的死代码）
- dist 删除 `Y_图层工具箱_测试版.dll / .zip`；使用说明删除「九、测试版」一节
- 清理 `src/bin_test`、`src/obj_test`、`bin/AutoLayerTest.*` 等全部测试版产物
- ⚠ 历史教训：plain Release 与 `-p:TestBuild=true` 共用 `obj\Release`，交替构建会互相污染中间产物（DLL 尺寸漂移 1KB）。现测试版分支已删，此问题不会再出现；今后改完统一 `rm -rf obj` 后干净重建
- 交付物只剩三件：`Y_图层工具箱V1.0.dll`（唯一要加载的）、`Y_图层工具箱V1.0.zip`、`Y_图层工具箱V1.0_源码.zip`

---

## 13. v1.9e：回归预览四件套 + 最近行折叠（2026-11-11 第 9 批 · 用户报障 ×3）

| 项 | 根因与实现 |
|---|---|
| 点「回到上次选中」还是没有预览 | v1.9d 的 `Entity.Highlight()` 只是把图元标成待重绘，真正绘制通常要等编辑器空闲刷一帧 —— 模态面板开着时这一帧不来。v1.9e 四件套（全在 `LockDocument` 内）：implied selection → 逐个 `Highlight()`（顺路取 `GeometricExtents` 并集）→ **不完全在视野时自动平移视图**（`WorldToPlane` 变换到 DCS 判断可见性、保持宽高比框进来 30% 边距，全可见则不动视图）→ **`Editor.Regen()` 强制立刻刷帧**。异常写 `Core.Trace`（ALTRACE 可查「回归亮显 N/M（异常：…）」），不再黑盒。API 坑：2016 无 `PlaneWorldToNormal`（用 `WorldToPlane`）、无 `Highlight(bool,bool)`（用无参版） |
| 模式条「仅切换当前图层（未选中…）」文字多余 | 用户拍板收掉：回归模式下 `ModeIcon`/`ModeText` 都隐藏，条里只留「回到上次选中（N 个）▸」。对象模式 / 警告 / 错误的文字保留（那些是有用信息） |
| 「最近」chip 画出面板右缘 | 根因：chip 区在横向 StackPanel 里没有宽度约束（不裁剪，子项直接溢出绘制）。修复：RecentRow 改双列 Grid（Auto「最近」标签 + `*` chip 区），`SizeChanged` 触发折叠 —— 放不下的 chip（首个顶边之后的全部）Collapsed，末尾挂「+N」（RecentChipStyle）；点开 Popup 列表，单击只看 / 双击或回车应用（与 chip 行为一致），Esc 关闭；窗口拉宽自动放回。`BuildRecentRow` 里 `Children.Clear()` 后必须置空 `_recentPlusN` 字段（否则 Ensure 不会再加回来） |

**验证**：0 警 0 错（干净重建）；离屏渲染 `.shots/v19e-cc-current.png`（模式条只剩按钮）；最近行折叠逻辑复用 §3.5.3 既有模式，实机数据验证。三包两 DLL 已重打（DLL 237568）。

## §14 v1.9f：「最近」改竖向排列（第 10 批 · 用户拍板）

**背景**：v1.9e 的横向折叠实测仍溢出（用户截图 +N 未出现、chip 直接画出右缘），用户拍板「不如竖向排列」。横向 chip 方案整体退役。

**改动**（`LayerSelectionWindow.xaml` / `.xaml.cs`）：
- RecentRow 从「双列 Grid + 横向 chip + SizeChanged 折叠 + Popup 浮层」整体改为竖向 StackPanel：「最近」小标题（10.5 / TextDim）+ 每层一行全宽 chip（高 22 / Margin 底 2 / 内容左对齐）。
- chip 内容改 DockPanel（swatch Dock.Left + TextBlock 填充）——横向 StackPanel 给子项无限宽、TextTrimming 永不生效（v1.9b 模式条同坑，第二次踩）。
- `RecentChipStyle`：Margin 改 `0,0,0,2`，加 `Height=22`、`HorizontalContentAlignment=Stretch`。**连带修**：`CategoryButtonStyle` 模板里 ContentPresenter 写死 `HorizontalAlignment="Center"`，改 `TemplateBinding HorizontalContentAlignment`（Button 默认 Center，分类 chip 等不受影响，最近 chip 的 Stretch 生效）——3 处同款模板全量替换。
- 删除整套折叠机制：`_recentPlusN` / `_recentPopup` 字段、`RecentPanel_SizeChanged` / `ScheduleRecentFold` / `RecomputeRecentFold` / `EnsureRecentPlusN` / `RecentPlusN_Click` / `RecentFoldList_*` / `RecentFoldApply`；`UpdateRecentRowVisibility` 收敛为纯可见性判断。
- 键盘导航 ←→ 改 ↑↓（`RecentPanel_PreviewKeyDown`）；chip 行为不变（单击=列表选中看预览，双击=应用，Enter=应用）。
- 行 3 是 Auto 高度：竖排占多少，行 4（`*`）的列表就让多少，无溢出可能。

**验证**：两工程 0 警 0 错（干净重建，DLL 235008）；离屏渲染 `.shots/v19f-cc-current.png`（360 宽，竖排左对齐、列表自适应压缩）、`v19f-cc-narrow.png`（320 宽）、`v19f-cc-object.png`（对象模式，分类 chip 仍居中不受模板改动影响）。DLL 自检：`RecomputeRecentFold` 已不在、`RestoreSelectionWithHighlight` 保留、字面量 UTF-16 探测「回到上次选中」在 /「更多最近图层」已删。三包重打，使用说明附9。

## §15 v1.9g：「最近」回横排 + 真正修好宽度自适应（第 11 批 · 用户拍板）

**背景**：v1.9f 竖排用户觉得不好看，要求回横排并按面板宽度决定显示几个、放不下的隐藏（最多 3-5）。

**根因终于找到**（v1.9e/v1.9g 两轮实测失败的原因）：诊断输出显示 `RecentPanel.ActualWidth = chip 自然总宽（321）`—— 横向 StackPanel 在 `*` 列里 Arrange 时被排成**自然宽**而非列宽，折叠判定永远"全放得下"。外层 Border / 内层 Grid 的 ActualWidth 始终正确（284），只有 StackPanel 不可靠。

**修复**：
- 可用宽度改取 `RecentRow.ActualWidth - RecentLabel.ActualWidth - 标签边距`（行宽确定可靠）；
- 触发改挂 `RecentPanel.LayoutUpdated`（Arrange 后必触发、宽度必然就绪）+ SizeChanged 兜底；宽度未量出（ActualWidth=0）时不判定、等下一轮；
- `ClipToBounds` 上移到 RecentRow Border + panel 双保险，折叠没跑也只在行内裁剪。

**过程坑**：预览桩加折叠逻辑后两次"渲染验证无变化"，实际是桩缺 `using System.Collections.Generic` **编译失败跑旧 exe** —— 教训：WPF 预览工程每次改完必须 `grep -c "error CS"` 确认 0 再渲染；桩里必须实现与主工程相同的折叠逻辑（空桩根本测不到折叠）。

**验证**（长图层名 320 / 400 宽 + 短名 360 宽，桩内实现同款折叠）：320 长名只显 2 个完整 chip、第 3 个完全隐藏；400 长/360 短全显；均无截断溢出。键盘 ←→ 过滤隐藏 chip。主工程干净重建 0 警 0 错（DLL 236032）。

**交付**：三包重打，使用说明附10。

## §16 V2.0 升版更名（第 12 批 · 用户拍板）

**更名**：Y_图层工具箱 V1.0 → **Y_归层工具 V2.0**（升版，作为主产品名）。

**改动清单**：
- csproj：AssemblyTitle/Product → `Y_归层工具`；Version 2.0.0 / FileVersion 2.0.0.0；DeliveryName → `Y_归层工具V2.0.dll`。程序集名 `AutoLayer` 不变（ASCII 名，BAML 路径约束）。
- UI：主面板窗口 Title + 头部「Y_归层工具」+ 版本徽标「V2.0」；状态条 StatusHud 标题；CCHELP/命令行消息 Tag `[Y_归层工具]`；圆盘输出前缀；加载横幅「Y_归层工具 V2.0 已加载」。
- Config.cs ini 头注释「Y_归层工具 V2.0 · 配置文件」。**AutoLayer.ini 文件名不变**，旧配置无缝延续。
- README.md 重写（升版沿革、编译、refs 来源、TESTBUILD 移除说明）。
- dist/使用说明.txt 完全重写：安装三法（AppLoader.xml / NETLOAD / APPLOAD 启动组）、从三种旧版升级（含 Y_图层工具箱V1.0）、命令表、配置项、排障、卸载、V2.0 改进一览（收编原附1~10）。
- 清除全部旧名产物：dist 旧 DLL/zip、src/bin 旧交付、merged/test 残留 DLL；全工作区 find 已无「图层工具箱」命名文件。

**验证**：干净重建 0 警 0 错；DLL 自检 UTF-8+UTF-16 双通道：无「图层工具箱」、有「V2.0」、AssemblyVersion 2.0.0.0；预览渲染主面板确认「Y_归层工具 V2.0」（注意：改 XAML 后必须重编 ui-preview 再渲染，否则又是旧 exe）。

**交付**：merged/dist/{Y_归层工具V2.0.dll, Y_归层工具V2.0.zip, Y_归层工具V2.0_源码.zip, 使用说明.txt}。
