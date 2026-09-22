# Product Requirements Document: 鸣潮游戏内 Mod 切换浮窗

**Version**: 1.0
**Date**: 2026-09-22
**Author**: Sarah (Product Owner)
**Quality Score**: 96/100

---

## Executive Summary

JASM 目前只能在主窗口里勾选 Mod：玩家在鸣潮里想换一套皮肤，必须走「Alt+Tab 出游戏 → 在主窗口找到角色 → 勾/取消 Mod → Alt+Tab 回游戏 → 按 F10 刷新」这条六步链路。每一环都在打断游戏，实测这个循环会把一次简单的试装变成一件"懒得做"的事。

本功能在游戏画面上叠一个小浮窗，直接列出当前角色的 Mod 供勾选。浮窗用全局热键唤出，用 `WS_EX_NOACTIVATE` 保证点击它时**游戏不会失去前台焦点**（不掉全屏、不暂停），勾选写入即触发 F10 刷新，全程不需要离开游戏。

功能只服务鸣潮。这不只是范围裁剪，而是现状使然：JASM 已有的「一键配置 Mod 环境」链路本身就硬编码了鸣潮（`Services/ModEnv/ModEnvSetupFacade.cs:162`），本功能可以完全踩在既有轨道上。

需要特别指出的是：本功能**不是**"把主窗口搬进游戏"那么轻松。它的可行性取决于两个尚未验证的 Windows 行为假设（独占全屏下置顶窗口能否显示、游戏锁定鼠标时浮窗能否收到点击），因此本 PRD 把「Phase 0 原型验证」作为**独立且有明确 kill 条件的第一阶段**，在投入正式开发前先用半天把这两个假设证伪或证实。

---

## Problem Statement

### Current Situation

1. **换 Mod 必须离开游戏**。Mod 的启用状态没有任何 UI 之外的入口，只能回到 JASM 主窗口操作。
2. **刷新基本靠手动**。JASM 有提权助手 `Elevator.exe` 能替用户发 F10，但只有「应用预设」和「随机化」两条路径会调用它（`ViewModels/PresetViewModel.cs:187-190`、`Services/ModRandomizationService.cs:192-195`），且还要同时满足 `ElevatorStatus.Running` 与设置项 `ModPresetSettings.AutoSyncMods`（`Services/ModHandling/ModPresetHandlerService.cs:110-116`）——而后者**默认是 false**（`Models/Settings/ModPresetSettings.cs:9`）。**单个 Mod 的勾选不会触发任何自动刷新**，用户只能手动按 F10。
   - 注：`README.md:24,29` 声称"启用/禁用 Mod 会自动刷新"，与当前代码不符，属过期描述，应在本功能中一并订正。
3. **F10 链路对鸣潮实际是断的**。`Elevator/Program.cs:167` 把目标进程**硬编码为 `GenshinImpact`**，而 `GameKeySender` 走的是从 `d3dx.ini` 动态解析（`Services/Input/GameKeySender.cs:208`）。也就是说，即便今天就有自动刷新的入口，在鸣潮上也不会生效。
4. **切换状态本身很轻**。Mod 的"启用"就是给目录加/去 `DISABLED_` 前缀（`src/GIMI-ModManager.Core/Helpers/ModFolderHelpers.cs:5-6`）。这是本功能的有利条件——它意味着浮窗的勾选动作不需要重启游戏、不需要重载 Mod 池，任何时刻都能安全执行。

### Proposed Solution

新增一个无边框、置顶、**不夺取激活**的浮窗窗口（WinUIEx `WindowEx` 已在用，见 `GIMI-ModManager.WinUI.csproj:61`）。浮窗内是当前角色的 Mod 勾选列表 + 角色选择 + 搜索。用全局热键（JASM 目前**完全没有**全局热键实现）唤出/隐藏。勾选直接调 `CharacterModList.ToggleMod`，写回目录后触发一次 F10 刷新（修复 Elevator 的目标进程解析使其支持鸣潮），并对连续点击做防抖合并。

### Business Impact

- 把「换 Mod」从 6 步跨窗口操作压到 3 步（热键 → 点击 → 结束），且**一次 Alt+Tab 都不需要**。
- 补上 JASM 作为「Mod 管家」最后一块体验缺口：Mod 的**选择**发生在游戏里，而 Mod 的**管理**才在 JASM 里。
- 顺带修掉一个既有缺陷：F10 刷新只对原神有效的硬编码问题。

---

## Success Metrics

**Primary KPIs:**

| KPI | 目标值 | 测量方式 |
|---|---|---|
| 端到端操作动作数 | 从 6+ 步降到 ≤3 步（热键 → 点击勾选 → 完成） | 手测清单逐步计数 |
| Alt+Tab 次数 | 0（除首次启动游戏） | 手测观察 |
| 点击到游戏内生效延迟 | 中位数 < 3s（含 3DMigoto 重载） | Serilog 打点：点击时间戳 → Elevator 管道回执 |
| 浮窗与主窗口状态一致性 | 100% 无偏差（不允许出现"浮窗勾了但主窗还显示未勾"） | 对照测试：浮窗操作后截图比对主窗口 |
| 稳定性 | 连续切换 20 次无 JASM 崩溃、无游戏闪退/掉全屏 | 手测压测 |
| 刷新失败可见性 | 刷新失败（Elevator 未运行等）100% 有明确提示，不允许静默失败 | 故障注入测试：杀掉 Elevator 后点击 |

**Validation**: 发布前用扩展后的 `docs/mod-env-hand-test.md` 手测清单跑一遍；发布后观察 2 周内的崩溃日志（`UnhandledException`）与用户反馈中"浮窗看不见/点不动"的占比——这两类反馈若持续出现，说明 Phase 0 的假设在别的机器上不成立，需回到技术选型。

---

## User Personas

### Primary: 鸣潮 Mod 玩家（JASM 现有用户）
- **Role**: 用 JASM 管理鸣潮 Mod 的普通玩家（本功能的首要用户即需求提出者本人）
- **Goals**: 在游戏里快速试不同的皮肤/特效组合，不想为了换个 Mod 反复切窗口
- **Pain Points**: Alt+Tab 打断沉浸感；切回来后游戏可能掉帧或掉全屏；忘了按 F10 导致"改了没效果"；Mod 一旦装多了，回主窗口找角色本身就很费时
- **Technical Level**: 中级偏上（已能自行配置 XXMI/WWMI 环境、理解 `DISABLED_` 的作用）

### Secondary: Mod 作者 / 调试者
- **Role**: 需要反复开关单个 Mod 来定位冲突或对比效果的人
- **Goals**: 高频切换、快速看到结果
- **Pain Points**: 一次开合要跑一趟主窗口；F10 手动按容易漏
- **Technical Level**: 高级

---

## User Stories & Acceptance Criteria

### Story 1: 在游戏里唤出浮窗

**As a** 鸣潮 Mod 玩家
**I want to** 在游戏画面上用一个热键叫出 Mod 浮窗
**So that** 我不用离开游戏就能看到并操作 Mod 列表

**Acceptance Criteria:**
- [ ] 游戏在运行时，按下全局热键（默认值待定，见 Phase 0 结论；可在设置中修改）浮窗显示；再按一次隐藏
- [ ] 热键在 JASM 主窗口没有焦点时依然有效（这是全局热键，不是应用内快捷键）
- [ ] 浮窗不夺取游戏的前台焦点：唤出、点击浮窗、在浮窗内滚动，游戏均不被暂停、不掉出全屏、不最小化
- [ ] 浮窗位置可拖动，且位置在重启 JASM 后保留（复用 `Services/AppManagement/LifeCycleService.cs:272,307-308` 的 `AppWindow.Position` 持久化模式）
- [ ] 热键与游戏自身按键冲突时（例如与鸣潮的某个功能键撞车），用户能在设置里改成别的键

### Story 2: 勾选 Mod 并立即看到效果

**As a** 鸣潮 Mod 玩家
**I want to** 在浮窗里勾选/取消 Mod，并立刻在游戏里看到变化
**So that** 试装不需要额外的确认步骤

**Acceptance Criteria:**
- [ ] 浮窗列出当前角色的全部 Mod，每项显示启用状态，勾选/取消立即写回磁盘（目录 `DISABLED_` 前缀增删）
- [ ] 每次勾选操作后自动触发 F10 刷新，**不需要用户手动按 F10**
- [ ] 快速连续点击多个 Mod 时，刷新被合并（防抖窗口内只发一次 F10），不会排队重载多次
- [ ] 上一次刷新尚未完成时的点击不会导致并发刷新：刷新被串行化，且界面在刷新中显示进行中状态
- [ ] 刷新在鸣潮上真实生效（前提修复：Elevator 目标进程不再硬编码 `GenshinImpact`）
- [ ] 刷新失败时（Elevator 未运行 / 提权失败 / 找不到游戏窗口）浮窗显示明确原因与可操作的建议，**不静默失败**

### Story 3: 切换角色与搜索

**As a** Mod 数量很多的玩家
**I want to** 在浮窗里切换要看的角色，并按名字搜索
**So that** 我不用为了找一个 Mod 而滚动几百项

**Acceptance Criteria:**
- [ ] 浮窗内可切换角色；默认打开时定位到用户在 JASM 主窗口中最后浏览的角色
- [ ] 提供搜索框，输入即时过滤（复用主窗口既有的过滤语义）
- [ ] 角色切换后列表滚动位置重置到顶部，不残留上一个角色的滚动状态
- [ ] 浮窗内的缩略图懒加载，不阻塞列表渲染；加载失败时降级为占位图而非空白或异常

### Story 4: 与主窗口保持一致

**As a** 同时使用浮窗与主窗口的用户
**I want to** 在任一处改的勾选状态在另一处都正确反映
**So that** 我不会因为两边不同步而误判 Mod 的真实状态

**Acceptance Criteria:**
- [ ] 浮窗修改后，主窗口的 Mod 列表（含画廊视图与详情页 DataGrid）状态同步更新，无需手动刷新
- [ ] 主窗口修改后，若浮窗正显示同一角色，其勾选状态同样同步
- [ ] 状态一致性不依赖轮询：复用既有的 FileSystemWatcher + `SkinManagerService.RefreshModsAsync`（`:156`、`:228-236`）链路
- [ ] Mod 被外部程序（在资源管理器里手工改名/删除）改动后，浮窗能反映真实状态，不显示陈旧的启用位

### Story 5: 边界与失败场景

**As a** 用户
**I want to** 在功能不可用时得到清楚的解释而不是一片沉默
**So that** 我能自己判断该怎么处理

**Acceptance Criteria:**
- [ ] 游戏未运行时按热键：给出明确提示（而非弹出一个操作无意义的空浮窗）
- [ ] 未配置鸣潮 Mod 环境 / Mod 目录为空时：浮窗显示引导文案，指向「一键配置 Mod 环境」
- [ ] Elevator 未运行时：浮窗内可见刷新状态指示，并提供一键启动入口（注意：Elevator 需要 UAC 提权 `runas`，见 `Services/ElevatorService.cs:69-76`；若提权弹窗出现在全屏游戏之上会打断体验，需在 UI 上明确说明"建议进游戏前先启动"）
- [ ] 浮窗盖住了游戏关键 UI 时，用户可以拖动它或整体隐藏
- [ ] JASM 主窗口关闭而浮窗仍存在时，行为明确（见开放问题 Q1）

---

## Functional Requirements

### Core Features

**Feature 1: 全局热键子系统（新建）**

- Description: JASM **当前没有任何全局热键实现**——全仓库 `RegisterHotKey` / `WM_HOTKEY` / `RawInput` / `SetWindowsHookEx` 零命中，`README.md:27-33` 列出的 F10/SPACE/F5 等全部是应用内快捷键。本功能必须新建这一层。
- User flow: 用户按热键 → 系统消息到达 JASM → 切换浮窗可见性。
- Edge cases: 热键被其他程序占用时注册失败，必须能检测并提示用户换键；游戏自身也占用该键时不应影响游戏（用 `RegisterHotKey` 注册的键会被系统拦下、不再传给游戏，这是设计上必须接受的副作用——因此默认键不能选游戏常用键）。
- Error handling: 注册失败 → 设置页显式报错 + 引导换键，不静默降级为"热键无效"。

**Feature 2: 无激活浮窗窗口（新建）**

- Description: 无边框（`AppWindow` / `OverlappedPresenter` 去掉边框与标题栏）+ 置顶 + `WS_EX_NOACTIVATE` 扩展样式。仓库现有 5 处第二窗口先例（`Services/AppManagement/WindowManagerService.cs:105-128` 统一登记），但**完全没有**无边框/透明/无激活窗口的代码，全部是新引入。
- User flow: 热键唤出 → 浮窗出现在游戏画面之上 → 用户直接鼠标点击列表项。
- Edge cases:
  - `WS_EX_NOACTIVATE` 是绕过 WinUI 在裸 HWND 上用 `SetWindowLongPtr(GWL_EXSTYLE)` 设置的**非标准用法**，必须验证不与 WinUI 3 的输入/渲染管线冲突。
  - 浮窗在独占全屏下能否可见，取决于 Windows「全屏优化」（FSO）是否把独占全屏转成了无边框翻转——**这是未经验证的假设，由 Phase 0 决定**。
  - 鼠标可达性：鸣潮是锁定鼠标的 3D 游戏（`SetCapture` / 鼠标裁剪）。若游戏正持有鼠标捕获，浮窗**收不到点击**。可行的使用姿势是在游戏菜单/背包/大地图等"鼠标已解锁"的界面操作浮窗——但这一点同样必须由 Phase 0 实测确认。
- Error handling: 若样式设置失败 → 降级为普通置顶窗口，并在 UI 中告知"点击浮窗会让游戏失去焦点"。降级路径必须存在，不允许启动即崩溃。

**Feature 3: Mod 勾选与状态同步**

- Description: 直接复用 `CharacterModList.ToggleMod`（`src/GIMI-ModManager.Core/Entities/CharacterModList.cs:333`）等既有 API 做目录重命名。启用状态**不落盘**，是扫描时从目录名推导的内存镜像（`Entities/CharacterSkinEntry.cs:17`、`CharacterModList.cs:249-251`），因此浮窗必须持有与主窗口同一份 `ISkinManagerService` 实例而不是自己重扫磁盘，否则会产生两份互相打架的状态。
- User flow: 点击项 → 重命名目录 → 刷新 → 主窗口经 watcher 感知并同步。
- Edge cases:
  - 自我触发回环：重命名会触发 FileSystemWatcher 事件，主窗口代码里已有 `DisableWatcher()`（`CharacterModList.cs:398`）用于规避，浮窗路径必须同样处理。
  - Mod 数量大时列表渲染性能：缩略图必须懒加载。
  - 只读/被占用的 Mod 目录重命名失败（例如被资源管理器打开）。
- Error handling: 重命名失败 → 该项回滚到原状态 + 明确报错，不允许 UI 显示成功而磁盘未变。

**Feature 4: F10 刷新链路修复与触发**

- Description: 修复 `Elevator/Program.cs:167` 的硬编码 `GenshinImpact`，改为从 `d3dx.ini` 动态解析目标进程。已有可复用实现：`Core/Helpers/D3dxIniTargetParser`（被 `Services/Input/GameKeySender.cs:208` 使用）与 `Services/Input/WindowProcessQuery.cs`（窗口/进程查询，注释声明与提权助手共用，**当前无调用方**）。
- User flow: 勾选 → 防抖窗口结束 → 经命名管道 `MyPipess` 向 Elevator 发刷新指令 → Elevator 抢回目标窗口前台并模拟 F10。
- 为什么走 Elevator 而不是 `GameKeySender`: 鸣潮的 XXMI 配置是 `require_admin = true`（`Services/Input/GameKeySender.cs:25-28`），游戏以管理员权限运行，而 `GameKeySender` 在完整性级别低于目标时直接返回 `NeedsElevation`（`:114-121`）。那条路依赖尚不存在的 `KeyHelperHost` 提权助手（`Core/Helpers/KeyHelperProtocol.cs` 只有协议与单测，程序本体未实现）。Elevator 已经是提权进程，是现成的正确通道。
- Edge cases:
  - 防抖：连续点击合并为一次刷新，避免多次重载。
  - 串行化：刷新进行中的新请求需排队或被合并，不能并发发送。既有实现有"等待 `d3dx_user.ini` 落盘、超时 5s"的逻辑（`Services/ElevatorService.cs:149`、`:181`）可直接复用。
  - 刷新发出但游戏未响应（游戏切窗口/走神）。
- Error handling: Elevator 未运行 → 明确提示 + 提供启动入口；启动需 UAC 提权，交互中断需在 UI 文案中提前说明。

### Out of Scope

- **注入式 overlay**（随 XXMI/d3dx 注入游戏进程自绘）。若 Phase 0 证明外部置顶窗口不可行，才重新评估此项，且需重新走一轮需求。
- **预设一键切换**（`ModPresetService.ApplyPresetAsync` 已存在，但本 MVP 不做）。
- **分类批量开关**、**随机化**。
- **原神 / 崩铁 / 绝区零**：本 MVP 只服务鸣潮。F10 目标解析的改造会为多游戏留出扩展点，但不做验证。
- 点击穿透模式（`WS_EX_TRANSPARENT`）。
- Mod 的安装 / 更新 / 删除 / 改名 / 移动。
- 编辑 merged.ini 按键、换装变体切换（`d3dx_user.ini` 相关偏好）。
- 移动端 / 手柄导航。

---

## Technical Constraints

### Performance

- 热键唤出到浮窗可见 < 200ms（列表用已有内存状态，不重扫磁盘）。
- 浮窗隐藏时不得产生持续 CPU 占用（禁止轮询游戏进程来判断"游戏是否在运行"；如需要，用 `WindowProcessQuery` 的事件化方案或低频（≥5s）检查）。
- 记忆开销：WinUI 页面导航已存在内存泄漏问题（`README.md:113-117`），浮窗不得加重——**不要**在热键切换时反复创建/销毁窗口，应保持单一实例、切换可见性。
- 缩略图懒加载 + 缓存，避免一次性读入全部 Mod 图片。

### Security

- 浮窗涉及提权边界：游戏以管理员运行，JASM 非提权。所有对游戏的输入必须经已提权的 Elevator 通道，**不要**为此把 JASM 主进程整体提权（会破坏现有"是否已提权"的状态提示逻辑）。
- 全局热键是一个系统级输入拦截点，默认键必须避开鸣潮的常用按键（尤其 F 系列、字母键）。
- 新增的进程/窗口查询代码需沿用既有的只读查询方式（`WindowProcessQuery`），不引入新的高权限句柄操作。
- 日志中不得写入用户本地路径细节（沿用 `CLAUDE.md` 的既有约定）。

### Integration

- **`ISkinManagerService` / `ISkinManagerService.GetAllMods`**（`Services/SkinManagerService.cs:987`）：浮窗的数据源，必须与主窗口共用同一实例（DI 注入）。
- **`CharacterModList`**（`Core/Entities/CharacterModList.cs`）：`ToggleMod` / `IsModEnabled` / `DisableWatcher`。
- **`ModPresetSettings.AutoSyncMods`**（`Models/Settings/ModPresetSettings.cs:9`）：**本功能不复用这个门控**。浮窗的刷新是用户显式点击触发的，与"预设自动同步"是两件事；不要为了省事把浮窗挂在那个默认关闭的开关下，否则功能默认不生效。
- **`ElevatorService`**（`Services/ElevatorService.cs`）：管道名 `MyPipess`（`:16`）、`StartElevator`（`:58`）、`RefreshGenshinMods`（`:120`，方法名沿用过时语义，内部可一并重命名）、`CheckStatus`（`:292`）。
- **`Elevator.exe`**（`src/Elevator/Program.cs`）：需改造目标进程解析；`H.InputSimulator` 已是既有依赖（`Elevator.csproj:16`）。
- **`WindowManagerService`**（`Services/AppManagement/WindowManagerService.cs:105-128`）：浮窗窗口的登记与生命周期统一走这里。
- **WinUI3Localizer**：所有新增 UI 文案需补 `Strings/zh-cn/*.resw` 词条（参考 `Strings/zh-cn/Settings.resw:178-184` 的既有文案风格）。
- **设置持久化**：热键绑定、浮窗位置与尺寸需落盘（参照 `%localappdata%\JASM\ApplicationData` 既有约定）。

### Technology Stack

- .NET 9 / WinUI 3 / Windows App SDK 1.7，与现有工程一致。
- WinUIEx 2.5.1（`IsAlwaysOnTop` / `AppWindow` 已可用）。
- 新增 Win32 互操作：`RegisterHotKey` / `UnregisterHotKey`、`SetWindowLongPtr`（`WS_EX_NOACTIVATE`）。**不引入新的第三方库**（`CLAUDE.md` 明令禁止未使用的依赖）；如确有必要（例如消息循环挂接），优先看 WinUIEx 是否已提供。
- 目标平台：Windows 11（本仓库主力测试环境），Windows 10 尽力兼容。

---

## MVP Scope & Phasing

### Phase 0: 原型验证（时间盒 0.5 天，独立于正式开发）

**目的**：用实机事实回答两个决定生死的假设，避免在错误的技术路线上投入。

**原型内容**：一个无边框 + 置顶 + `WS_EX_NOACTIVATE` 的最小窗口，内含一个按钮和一个时间戳。不做 Mod 列表、不做数据绑定。

**测试矩阵**（在鸣潮实机跑）：

| 游戏显示模式 | 鼠标状态 | 浮窗可见？ | 浮窗可点击？ | 游戏掉全屏/暂停？ |
|---|---|---|---|---|
| 独占全屏 | 3D 视角（鼠标被锁） | | | |
| 独占全屏 | ESC 菜单/背包（鼠标解锁） | | | |
| 无边框窗口 | 3D 视角（鼠标被锁） | | | |
| 无边框窗口 | ESC 菜单/背包（鼠标解锁） | | | |

**Go 条件**：至少存在一行"可见 + 可点击"。最优情形是无边框模式下两行全通过。

**No-Go 时的处置**：
1. 若仅"无边框窗口"可行 → MVP 保留方案 A，但在产品内增加引导：检测到游戏处于独占全屏时提示"切到无边框窗口模式可启用浮窗"。
2. 若全部不可见（FSO 关闭且独占全屏） → 方案 A 作废，回到技术选型：转注入式（工作量高一个数量级，需重新走需求与排期）或降级取消本功能。
3. 若可见但始终不可点击（鼠标捕获无解） → 交互方式需重新设计（例如浮窗退化为纯状态显示 + 用全局热键做"上一个/下一个 Mod"切换），需重新走一轮需求确认。

**交付物**：把测试矩阵的实测结果追加到 `docs/mod-env-hand-test.md`，作为后续回归的基线。

### Phase 1: MVP（Required）

Phase 0 通过后实现：

1. **全局热键子系统**（默认键 + 设置页可改）
2. **无激活置顶浮窗窗口**（单实例、可拖动、位置记忆）
3. **当前角色 Mod 勾选列表**（含角色选择器 + 搜索 + 缩略图懒加载）
4. **勾选写回 + 防抖合并的 F10 刷新**（含 Elevator 目标进程动态化改造）
5. **状态双向同步**（主窗口 ↔ 浮窗，基于既有 watcher 链路）
6. **失败与边界提示**（游戏未运行 / Elevator 未运行 / 环境未配置 / 权限不足）
7. **本地化词条**

**MVP Definition**: 用户能在鸣潮里用热键叫出浮窗、切换角色、勾选 Mod 并在游戏内看到效果，全程不 Alt+Tab、不手动按 F10，且任何失败都有明确解释。

### Phase 2: Enhancements（Post-Launch）

- 预设一键切换（顶部一排预设按钮，复用 `ModPresetService.ApplyPresetAsync`）
- 分类批量开关（一键禁用某一类 Mod）
- 浮窗主题/透明度/尺寸个性化
- 默认热键按用户反馈调整；支持第二个热键用于"快速隐藏"
- 把 F10 目标动态化推广到其余三个游戏（彼时需验证各游戏的 `d3dx.ini` 与 `require_admin` 差异）

### Future Considerations

- 注入式 overlay（若 Phase 0 结论迫使路线切换，或用户对"每次都要先开菜单解锁鼠标"不满）
- 浮窗内直接预览 Mod 图片大图 / 描述
- 通过浮窗操作换装变体（`d3dx_user.ini` 的叠加键切换）
- 与 JASM 的 Mod 市场联动（浮窗内直接下载新 Mod）

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|---|---|---|---|
| 独占全屏下浮窗不可见（FSO 未启用） | High | High | Phase 0 实测；不可见则引导用户切无边框窗口模式；仍不行则转注入式或放弃 |
| 游戏鼠标捕获导致浮窗收不到点击 | High | High | Phase 0 实测三种鼠标状态；UX 明确"在菜单/背包界面操作"；若不可接受则重设计交互（热键驱动而非点击） |
| `WS_EX_NOACTIVATE` 与 WinUI 3 不兼容 | Medium | Medium | Phase 0 验证；必须有降级为普通置顶窗的路径（代价是游戏失焦） |
| Elevator 目标硬编码原神（**已存在缺陷**） | High（已知） | High | 复用 `D3dxIniTargetParser` 动态解析；补单测；Phase 1 必做项 |
| Elevator 未运行 → 刷新静默失败 | Medium | Medium | 浮窗内显式状态指示 + 一键启动入口；文案提示"建议进游戏前先启动"以避免 UAC 弹窗打断全屏 |
| 点击浮窗导致游戏掉全屏/最小化 | Medium | Medium | 无激活窗规避；Phase 0 记录实测结果，作为 go/no-go 依据 |
| F10 重载期间连点造成竞态/重复重载 | Medium | Low | 防抖合并 + 刷新串行化（复用 ElevatorService 的落盘等待逻辑） |
| 浮窗与主窗口状态不一致（双份状态） | Medium | Medium | 强制共用同一 `ISkinManagerService` 实例；走既有 watcher 同步链路；加对照测试 |
| WinUI 内存泄漏被浮窗放大 | Medium | Low | 单实例复用、禁止反复创建窗口；缩略图懒加载 |
| 默认热键与游戏按键冲突 | Medium | Low | 默认键避开游戏常用键；设置页可改；注册失败显式报错 |
| 浮窗遮挡游戏关键 UI | Low | Low | 可拖动 + 位置记忆 + 一键隐藏 |

---

## Dependencies & Blockers

**Dependencies:**

- `Elevator.exe` 目标进程解析改造（原神 → 从 `d3dx.ini` 动态解析）：本功能的刷新链路依赖它，属 Phase 1 必做。
- 全局热键子系统：全新，无既有实现可复用。
- 无边框 + 无激活窗口：全新，仓库无先例。
- 本地化词条：`Strings/zh-cn/*.resw`。
- Mod 缩略图读取路径：需确认 `ModSettings`（`Core/Entities/Mods/Contract/ModSettings.cs:10-29`）中自定义图片的读取方式与主窗口缩略图实现是否可直接复用。

**Known Blockers:**

- **Phase 0 结论未决**：浮窗在独占全屏下的可见性与鼠标可达性，是决定技术路线能否成立的前提。Phase 0 之前不进入正式开发。
- **鸣潮的鼠标锁定行为未实测**：直接影响可用的交互方式（点击 vs 纯热键）。

**开放问题（需在实现前确认）:**

- **Q1**：主窗口关闭时浮窗如何处理？跟随退出（简单、无歧义）还是保留（需要独立的窗口生命周期管理）？默认取"跟随退出"。
- **Q2**：热键默认值具体定哪个键？建议在 Phase 0 期间顺便确认鸣潮的按键占用情况后定稿。
- **Q3**：刷新失败时是否应退回"让用户手动按 F10"作为兜底提示？倾向于要，成本低。

---

## Appendix

### Glossary

- **Mod 启用/禁用**: 通过对 Mod 目录增删 `DISABLED_` 前缀实现，状态不落盘，由目录名推导（`Core/Helpers/ModFolderHelpers.cs:5-6`）。
- **F10 刷新**: 3DMigoto/XXMI 的重载热键，按下后重新加载 Mod 配置。JASM 通过提权的 `Elevator.exe` 模拟该按键。
- **Elevator**: JASM 的可选提权助手进程，通过命名管道 `MyPipess` 接收指令。用于发 F10 与提权复制文件。
- **XXMI / WWMI**: 鸣潮的 Mod 加载框架（Wuthering Waves Model Importer），由 JASM 的「一键配置 Mod 环境」安装。
- **FSO（全屏优化）**: Windows 10+ 将部分"独占全屏"转为无边框翻转的机制。它是否启用直接决定外部置顶窗口能否覆盖在游戏上。
- **`WS_EX_NOACTIVATE`**: 窗口扩展样式，使窗口被点击时不夺取前台焦点。
- **`NeedsElevation`**: `IGameKeySender` 的返回状态（`Services/Input/IGameKeySender.cs:7-34`），表示 JASM 完整性级别低于目标游戏，无法发送输入。

### References

- Mod 启用机制：`src/GIMI-ModManager.Core/Helpers/ModFolderHelpers.cs`、`src/GIMI-ModManager.Core/Entities/CharacterModList.cs`
- 提权刷新链路：`src/GIMI-ModManager.WinUI/Services/ElevatorService.cs`、`src/Elevator/Program.cs`
- 按键发送（未走本方案的备选通道）：`src/GIMI-ModManager.WinUI/Services/Input/GameKeySender.cs`、`IGameKeySender.cs`
- 窗口管理先例：`src/GIMI-ModManager.WinUI/Services/AppManagement/WindowManagerService.cs`
- 既有需求文档风格参照：`docs/xxmi-version-switch-prd.md`、`docs/mod-env-setup-prd.md`
- 手测清单（Phase 0 交付物追加至此）：`docs/mod-env-hand-test.md`
- 工程规范与提交约定：`CLAUDE.md`

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
