# Product Requirements Document: Mod 游戏内按键切换菜单（可点击）

**Version**: 1.1
**Date**: 2026-11-07
**Author**: Product / JASM
**Status**: Phase 0 完成，MVP 已接入（交互修订为键盘导航）

---

## 实现结果（2026-11-07 修订）

Phase 0 实机验证后，有两处与原方案不同：

1. **鼠标点击不可行**：鸣潮正常游玩时系统光标被锁定，3DMigoto 的 `cursor_x/y` 不更新（红点诊断证实）。菜单改为**键盘导航**：
   - `F8` 打开/关闭
   - `↑` / `↓` 选择行
   - `F9` 切换当前行变量
   - 仍是原生 3DMigoto 绘制，点击切换的延迟目标由键盘路径同样满足（≤1 帧）。
2. **默认呼出键改为单键 `F8`**：本机 3DMigoto 构建不认多修饰键组合（`CTRL SHIFT VK_M` 匹配不上，游戏反而响应了 Ctrl+M）。呼出键在设置页可改。

实机踩坑与根因（详见 `docs/mod-ingame-menu-debug-log.md`）：

- `vs` / `ps` / `Draw` 必须写在 `[CustomShader*]`，写进 `[CommandList]` 会导致 D3D device lost（0x887A0006）。
- CommandList 的 `if` **不支持 `&&`**；解析失败会打乱 `if/endif` 结构，导致绘制无条件执行。
- 文件必须声明 **ASCII `namespace`**；否则 3DMigoto 用 ini 路径（含中文）当变量命名空间，变量名非法。
- 删除鼠标段落后必须一并删除对 `$click_guard` 的引用，未声明变量同样会破坏 `if/endif` 结构。

已接入：

- `Services/ModHandling/InGameMenuService.cs`：生成/移除/批量补齐（同一角色只给当前生效的一件）。
- 安装钩子：`ModInstallerService` 静默安装完成后自动生成。
- 启动后台批量补齐（`ActivationService`，已有菜单跳过）。
- 设置页「游戏内 Mod 菜单」：呼出键可改（改键会重建已有菜单）+「为所有 Mod 补齐菜单」。

---

## Executive Summary

JASM 现在的「浮窗」是 WinUI 桌面窗口，管的只有 **Mod 整体启用/禁用**（改目录 `DISABLED_` 前缀 + F10 刷新）。但玩家真正高频的操作是 **Mod 内部的部件/形态切换**（发型、鞋子、裙子、丝袜、胸型……），这些切换只存在于每个 Mod 作者自己写的 `[Key*]` 绑定里：要么记键，要么切出游戏去详情页看徽章。

更麻烦的是现状参差：

- 有些 Mod **根本没有菜单**，只有 `mod.ini` 里一堆 `[KeyHair] key = Num5`；
- 有些 Mod **有菜单但点不了**（只画了个提示，必须按作者定义的键）；
- 能点的菜单（如本地参考的 Nait3D 菜单）是作者自己手写的 ini + 纹理，不可复制、不通用。

本功能让 JASM **为每个已安装 Mod 自动生成一份可点击的游戏内菜单**：直接嵌进 Mod 目录（仅新增 `JASM_Menu.ini` 与资源目录，不改作者文件），由 3DMigoto 在游戏内原生绘制与命中。点击行 → 直接改 Mod 自己的 `$变量` → **同帧/下一帧生效**，不触发 F10、不跨进程、不抢前台。

**范围**：只做鸣潮（WWMI）；只补「没有鼠标点击能力」的 Mod；菜单数据只取 Mod ini 里已有的 `[Key*]` 循环/开关类绑定。

---

## 背景与问题

### 现状事实（已核对代码与本机 Mod）

| 事实 | 位置 / 佐证 |
|---|---|
| 现有浮窗是主进程内 WinUI 3 窗口，作用是勾选 Mod（整体启停） | `src/GIMI-ModManager.WinUI/Views/Overlay/OverlayWindow.xaml`、`OverlayViewModel.cs` |
| Mod 启停 = 目录加/去 `DISABLED_` 前缀，**不是**改变体 | `Core/Entities/CharacterModList.cs:276-346` |
| 已有 ini 按键解析，但**跳过** `type` / `$swapvar`，拿不到切换语义 | `Core/Entities/Mods/Helpers/ModIniKeyBindingParser.cs:112-117` |
| 已有 `d3dx_user.ini` 变量读写 | `Core/Services/UserPreferencesService.cs:117-200` |
| 仓库**没有任何** ini 生成 / 纹理生成 / 游戏内菜单实现 | 全仓检索无命中 |
| 一个 Mod 目录内多个 ini 都会加载，且共用 `namespace` | 本机 Nait3D Mod：`mod.ini` / `MenuHsin.ini` / `CommandList.ini` 各有 `[Present]`、`[Constants]`，均为 `namespace = HsinNait3D` |
| 可点击菜单的标准做法：`[Present]` 每帧读 `cursor_x/cursor_y` + `VK_LBUTTON`，命中矩形后直接改 `$swapvar_*` | 同上 `MenuHsin.ini:169-170`（坐标）、`:493-511`（命中+切换） |

### 参考实现的机制（Nait3D 菜单，本机可复现）

```
[KeyOpenUI]  key = 9  type = cycle  $open = 0,1
[KeyMouseClick] key = VK_LBUTTON  type = hold
[Present]
    $my_mouse_x = cursor_x * res_width      ; 3DMigoto 内建归一化坐标
    $my_mouse_y = cursor_y * res_height
    run = CommandListDrawUI
[CommandListDrawToggle_Hair]
    if 鼠标落在该行矩形内 && 本帧有点击
        if $swapvar_hair < 0  $swapvar_hair = $swapvar_hair + 1
        else                  $swapvar_hair = -1
```

结论：**菜单 = ini 逻辑 + 预烘焙纹理**。3DMigoto 没有内建文字/ImGui 菜单（本机 XXMI 1.2.3 的 `d3d11.dll` 中 `Menu`/`ImGui` 字符串均为 0），所以文字必须由 JASM 在生成阶段渲染成纹理。

### 用户痛点

1. 想换个发型/鞋子，得先知道作者绑的哪个键，或者切出游戏看 JASM 详情页。
2. 「有菜单但点不了」的 Mod 只能按键，鼠标用户被劝退。
3. 没有菜单的 Mod 完全无从下手。
4. 任何走「JASM 桌面浮窗 + 提权助手送键」的方案都要抢前台、约 100–300ms，且全屏/鼠标锁定时不可靠——不是用户要的「低延时」。

---

## 目标 / 非目标

### 目标

- 每个符合条件的 Mod 在游戏内拥有一个**可点击**的切换菜单，点击即生效（≤1 帧）。
- 生成过程**不修改作者任何文件**，产物可一键移除。
- 菜单只列 Mod 自己的 `[Key*]` 绑定，不臆造名称与值。
- 同一时间屏幕上只有「当前生效那一件 Mod」的菜单。

### 非目标（本版明确不做）

- 不替代、不修改作者已有的可点击菜单。
- 不做 Mod 整体启用/禁用（那是既有 Overlay 的职责）。
- 不做桌面浮窗/送键通道（本功能完全不跨进程）。
- 不支持鸣潮以外的游戏。
- 不做长按、脚本类按键的「模拟点击」。
- 不做 Mod 作者的菜单皮肤移植（不复制第三方 ini/纹理）。
- 不做 AI 推断中文标签（只显示 ini 里能读到的名字/注释）。

---

## 用户故事与验收标准

### Story 1：安装后自动获得菜单

**作为** 鸣潮 Mod 玩家
**我想要** 装完 Mod 进游戏就能看到它的切换菜单
**以便** 不用记键、不用切出游戏

- [ ] 安装/导入完成后，若该 Mod 没有可点击菜单，自动在 Mod 目录生成 `JASM_Menu.ini` 与 `JASM_Menu/` 资源目录。
- [ ] 生成失败不阻断安装：安装标记成功，但在 Mod 详情页与通知中给出可行动的原因。
- [ ] 生成产物全部位于 Mod 目录内，不改动任何作者文件（`mod.ini` / `merged.ini` / `Script.ini` 等字节不变）。
- [ ] 卸载/删除 Mod 时产物随目录一起消失；也可在详情页单独「移除菜单」。

### Story 2：在游戏内打开并点击切换

**作为** 玩家
**我想要** 按一个组合键唤出菜单，点一行就切换
**以便** 鼠标就能换部件，且没有等待感

- [ ] 默认组合键（设置可改）在游戏内唤出/收起菜单；组合键由设置页录制，冲突时给出明确提示。
- [ ] 菜单只在对应 Mod 生效时显示（`$object_detected == 1`）。
- [ ] 点击可切换行后，画面在**同一帧或下一帧**变化；不触发 F10、不重载 Mod、不出现黑屏/闪烁。
- [ ] 点击不可切换行（长按/脚本/纯提示）无任何副作用，行显示为禁用态。
- [ ] 菜单可拖动，位置在重启游戏后保留。
- [ ] 菜单不遮挡时可按同一组合键收起。

### Story 3：只有当前生效的 Mod 有菜单

**作为** 同时装了很多 Mod 的玩家
**我想要** 菜单不叠加、不打架
**以便** 屏幕不会被几套菜单糊住

- [ ] 同一角色只允许「当前生效」的那一件 Mod 携带菜单；其余 Mod 即便被启用也不生成/不激活（以既有单选模式为准）。
- [ ] 多选模式下该角色同时启用多件时：不为任何一件生成菜单，并在 JASM 内提示「该角色有多件 Mod 同时启用，菜单已跳过」。
- [ ] 切换当前生效 Mod 后，菜单内容随之切换（重新生成/激活对应那一件）。

### Story 4：菜单内容只来自 ini

**作为** 维护者
**我想要** 菜单绝不臆造内容
**以便** 不会出现「点了没反应」或错误名称

- [ ] 行数据只来自 Mod 内 `.ini` 的 `[Key*]` 段落；标签优先取段落上方注释，其次取去掉 `Key` 前缀的段落名。
- [ ] 只把 `type = cycle`（含 0/1 二值）与 `type = toggle` 生成为可点击行；其余类型显示为禁用行。
- [ ] 每个可点击行必须有明确的变量名与值列表；缺任一条件即降级为禁用行，绝不猜测。
- [ ] `condition` 不逐条照搬（可能引用未定义变量），统一用 `$object_detected` 门控。

### Story 5：分页与溢出

- [ ] 菜单固定高度，一页最多 10 行；超过时底部显示「‹ 1/3 ›」，可点翻页。
- [ ] 翻页只改变可见行，不改变 Mod 状态。
- [ ] 行数 ≤ 10 时不显示翻页控件。

---

## 方案设计

### 总体架构

```
JASM（生成期）                         游戏内（运行期，3DMigoto）
┌─────────────────────────┐            ┌──────────────────────────────┐
│ 扫描 Mod ini            │            │ [Present]                    │
│  ├ 是否有可点击菜单？   │  新增文件  │  读 cursor_x/y + VK_LBUTTON  │
│  ├ 解析 [Key*] 变量/值  │ ─────────► │  命中行矩形 → 改 $变量       │
│  ├ 渲染标签/数值纹理    │            │  绘制面板/行/高亮            │
│  └ 写 JASM_Menu.ini     │            │  （同帧生效，无跨进程）      │
└─────────────────────────┘            └──────────────────────────────┘
```

### 为什么不用桌面浮窗 + 送键

现有 Overlay 路线要经「切前台 → 提权助手 SendInput → 游戏下一帧」：延迟 100–300ms，独占全屏/鼠标锁定时不可用，还会打断游戏。**本功能的核心诉求就是低延时，所以必须留在游戏进程内。**

### 为什么不做「一个共享菜单」

3DMigoto 的变量带 `namespace`（如 `HsinNait3D$swapvar_hair`），不同 Mod 的变量互相不可见；共享菜单无法通用地读写别人的变量。而「每个 Mod 一份菜单 + `$object_detected` 门控 + 单选模式」天然满足「只显示当前 Mod」。

### 产物布局（只新增，不改作者文件）

```
<Mod 目录>/
├── mod.ini                 ← 作者文件，字节不变
├── ...                     ← 作者文件，字节不变
├── JASM_Menu.ini           ← JASM 新增：菜单全部逻辑（namespace 与 Mod 一致）
└── JASM_Menu/              ← JASM 新增：生成资源
    ├── manifest.json       ← 版本/来源/行数据快照（便于校验与移除）
    ├── panel.dds           ← 背景板（可全局复用）
    ├── row_<n>_label.dds   ← 行标签文字
    ├── row_<n>_value_<i>.dds ← 可选值文字（"1/3"）
    └── page_prev/next.dds  ← 翻页按钮
```

**移除** = 删除 `JASM_Menu.ini` + `JASM_Menu/`。不需要备份，也不存在「改坏作者 ini」的回滚问题。

### 生成流程

1. **检测**：扫描 Mod 目录全部 `*.ini`（递归）。
   - 若同时出现 **`VK_LBUTTON`（或鼠标键）** 与 **`cursor_x`/`cursor_y`（或 `$my_mouse_x`）**，判定为「已有可点击菜单」→ 跳过。
   - 仅出现其一，或出现 `[Present]` 但无命中逻辑 → 判定为「不可点击」→ 生成。
2. **解析**：抽取 `[Key*]` 段落：
   - `key` / 修饰键；
   - `type`；
   - `$变量 = 值1,值2,...`；
   - 段落上方连续注释（作为标签候选）；
   - 所在文件的 `namespace`。
3. **筛选**：只保留 `type ∈ {cycle, toggle}` 且 `变量 + 值列表` 齐全的行作为可点击；其余保留为禁用行（可配置不显示）。
4. **渲染纹理**：GDI32 `CreateDIBSection` + `DrawTextW`（系统 Microsoft YaHei UI），输出未压缩 BGRA32 DDS（手写 128 字节头）。零新增 NuGet 依赖。
5. **写 ini**：生成带 `namespace` 的 `JASM_Menu.ini`（纯 ASCII，避免编码问题）。
6. **写 manifest**：记录 `MenuVersion`、源 ini 的哈希、行快照。

### 菜单运行期行为

- **打开键**：`[KeyJasmMenuToggle] key = <组合键> type = cycle $jasm_menu_open = 0,1`
  - 默认 `ctrl shift M`（可在 JASM 设置里改；**不得**与 JASM 的全局热键 `Ctrl+Alt+J/S` 重复，否则会被 Windows 截走）。
- **显示门控**：`[Present]` 内 `if $object_detected == 1 && $jasm_menu_open == 1` 才绘制与命中。
- **坐标**：`$x = cursor_x * res_width`、`$y = cursor_y * res_height`（3DMigoto 内建）。
- **点击**：`VK_LBUTTON` `type = hold` → 本帧按下边沿 → 命中矩形 → 生成的下一条 `if/else` 链把变量推进到下一个值（等价于作者按一次键）。
- **翻页**：底部两个命中矩形，改 `$jasm_menu_page`，并用 `if` 链决定当前页显示哪些行。
- **拖动**：面板顶部 40px 作为拖拽区，`global persist $jasm_menu_x/y` 持久化到 `d3dx_user.ini`。

### 与现有模块的关系

| 模块 | 复用 / 新建 |
|---|---|
| `ModIniKeyBindingParser` | **扩展**：新增 `type` / `$变量` / `namespace` / 注释标签的解析（现有 API 行为不变） |
| `SkinMod.GetModIniPathAsync` | 复用：定位 Mod 的 ini 根 |
| 安装链路 `ModInstallerService` / 拖拽安装 | 复用：安装完成后追加一次生成钩子 |
| `OverlayViewModel` / 主窗口详情页 | 复用：展示「菜单已生成 / 已跳过 / 失败」状态与操作入口 |
| 全局热键 `OverlayHotkeyRegistrar` | **不复用**：菜单键是游戏内 3DMigoto 键，不进 Windows 全局热键 |
| `IGameKeySender` / Elevator | **不复用**：本功能不跨进程 |
| `OverlaySettings` / 新设置 | 新增：菜单键、是否自动生成、行数上限 |
| 纹理生成 | **新建**：DDS 写入 + GDI 文字渲染 |

---

## 数据与接口变更

### `.JASM_ModConfig.json`（新增字段，向后兼容）

```json
{
  "InGameMenu": {
    "Status": "Generated",      // NotGenerated | Generated | Skipped | Failed
    "MenuVersion": 1,
    "GeneratedAt": "2026-11-07T12:00:00Z",
    "Reason": null,             // Skipped/Failed 时的原因
    "SourceIniHash": "…"        // 源 ini 内容哈希，用于失效检测
  }
}
```

### `JASM_Menu/manifest.json`（生成物自描述）

```json
{
  "menuVersion": 1,
  "generatorVersion": "1.0.0",
  "sourceNamespace": "HsinNait3D",
  "openKey": "ctrl shift M",
  "rows": [
    { "section": "[KeyHair]", "variable": "swapvar_hair", "values": ["-1", "0"],
      "type": "cycle", "label": "Hair", "clickable": true }
  ]
}
```

### 设置项（`OverlaySettings` 或新 `InGameMenuSettings`）

- `Enabled`（默认 true）
- `OpenKey`（默认 `ctrl shift M`，可录制）
- `MaxRowsPerPage`（默认 10）
- `ShowDisabledRows`（默认 true）

---

## 边界与异常

| 场景 | 行为 |
|---|---|
| Mod 无可解析的 `[Key*]` | 不生成，标记 `Skipped: 无可用按键` |
| 已有可点击菜单 | 不生成，标记 `Skipped: 已有可点击菜单` |
| 只读取到 GBK/ANSI ini | 自动编码探测（BOM → UTF-8 → GB18030 回退），标签不乱码 |
| `namespace` 不一致或缺失 | 取拥有目标 `[Key*]` 的文件命名空间；文件之间不一致则 `Failed`，不猜 |
| 变量是 `local`/非全局 | 不可点击（无法跨段写入），禁用行处理 |
| 值列表含非数字/表达式 | 不可点击，禁用行处理 |
| Mod 目录只读 | `Failed: 无写权限`，不阻断安装 |
| 生成后作者更新了 Mod | `SourceIniHash` 变化 → 标记 `Stale`，下次启动/手动重建时重生成 |
| 同角色多件启用 | 都不生成，JASM 明确提示 |
| 游戏窗口未运行 | 生成不受影响（纯文件操作） |
| 全屏独占 / 鼠标被锁定 | 菜单可见性由游戏决定；点击依赖 `cursor_x/y`，Phase 0 实机验证 |
| 点击同时被游戏收到 | 3DMigoto 无法吞输入；点击会同时触发游戏自身点击。Phase 0 验证并决定是否提示「建议在安全界面操作」 |

---

## 验收标准（可逐条勾选）

### 生成

- [ ] 安装一个无菜单 Mod 后，目录出现 `JASM_Menu.ini` + `JASM_Menu/`，作者文件哈希不变。
- [ ] 安装一个已有可点击菜单的 Mod（如 Nait3D 那件）后，**不生成**，状态为 `Skipped`。
- [ ] 「为所有 Mod 补齐菜单」可批量执行，展示总数/成功/跳过/失败，失败项可重试。
- [ ] 「移除菜单」后目录恢复为安装时状态（仅多出的文件被删除）。

### 运行

- [ ] 游戏内按组合键可打开/收起菜单，菜单只出现在对应 Mod 生效时。
- [ ] 点击可切换行后，画面在 ≤1 帧内变化；连续点击 20 次无卡顿、无重载、无黑屏。
- [ ] 点禁用行无任何效果。
- [ ] 翻页、拖动、位置持久化正常。
- [ ] 同角色多件启用时无菜单出现，JASM 有提示。

### 回归

- [ ] 既有 Overlay（WinUI 浮窗）的唤出/勾选/刷新/截图/按键面板不受影响。
- [ ] 主窗口 Mod 详情页、KeySwap 编辑、安装/删除流程不受影响。
- [ ] 不新增 NuGet 依赖；`dotnet build` 与现有发布脚本通过。

---

## 测试计划

### 单测

- `[Key*]` 解析：`type`/变量/值列表/注释标签/修饰键/namespace/多 ini/GBK 编码。
- 可点击菜单检测：仅 `VK_LBUTTON`、仅 `cursor_x`、两者都有、仅 `[Present]` 无命中。
- 生成器：给定行模型 → ini 文本快照（黄金文件）；变量推进 `if/else` 链正确（0→1→0、-1→0→-1、多值循环）。
- DDS 写入：头部字段、像素布局、尺寸。
- manifest 与源哈希失效判定。

### 组件/集成

- 安装钩子：安装完成后生成，不改变作者文件字节。
- 批量生成：并发限制、失败隔离、进度上报。
- 移除菜单：只删 JASM 产物。

### 实机（Phase 0 + 回归）

| 场景 | 窗口化 | 无边框全屏 | 独占全屏 |
|---|---|---|---|
| 菜单可见 | | | |
| 鼠标可移动（cursor_x/y 变化） | | | |
| 点击命中（同时观察游戏是否也响应点击） | | | |
| 点击到画面变化帧数 | | | |

补充：多个 Mod 同角色、Mod 无 `[Key*]`、Mod 已有菜单、组合键改键。

---

## 分期

### Phase 0：技术验证（0.5–1 天，必须先做）

用本机 Nait3D Mod 的机制，在 1 个 Mod 上手写最小 `JASM_Menu.ini`（2 行、1 个打开键、固定位置），验证：

1. 新增 ini 能否与作者 ini 共存（`namespace` 一致时变量可读写）。
2. `cursor_x/y` 在鸣潮各显示模式下是否可用。
3. 点击命中时游戏是否同时收到点击（是否会在战斗中触发攻击）。
4. 点击到画面变化是否 ≤1 帧。
5. 多个 Mod 同角色时 `$object_detected` 是否能隔离。

**No-Go 条件**：点击被游戏吞掉/坐标不可用/双击触发异常 → 回到需求，考虑「键盘导航 + 屏幕高亮」或桌面浮窗。

### Phase 1：MVP

1. 解析扩展（`type`/变量/值/namespace/编码）。
2. 可点击菜单检测与跳过。
3. ini 生成 + `if/else` 变量推进。
4. GDI→DDS 文字纹理生成（标签 + 值 + 翻页按钮）。
5. 安装钩子 + 存量批量补齐 + 移除菜单。
6. 设置：开关、打开键、每页行数。
7. 主窗口/Overlay 状态展示与失败提示。

### Phase 2：增强

- 可换皮肤/更接近截图的视觉（自绘素材，不复制第三方）。
- 鼠标滚轮滚动、键盘导航（↑↓/Enter）。
- 按 Mod 自定义行顺序与隐藏。
- 多游戏支持（GIMI 等）。

---

## 风险与对策

| 风险 | 概率 | 影响 | 对策 |
|---|---|---|---|
| 点击同时被游戏接收（战斗中误触发） | 高 | 中 | Phase 0 实测；UI 文案提示在安全界面操作；必要时提供「仅键盘导航」模式 |
| `cursor_x/y` 在鼠标锁定时不可用 | 中 | 高 | Phase 0 覆盖三种显示模式；不可用时降级为键盘导航 |
| 作者 ini 的 `namespace`/变量是 private | 中 | 中 | 只接受可跨段写入的全局变量；其余降级禁用 |
| 中文 ini 为 GBK，解析/标签乱码 | 中 | 中 | 编码探测 + 单测覆盖 |
| 菜单与作者已有菜单视觉重叠 | 中 | 中 | 检测到可点击菜单即跳过；不可点击菜单仍可能叠，提供「移除菜单」与提示 |
| 生成纹理体积过大 | 低 | 低 | 单行小图 + 面板复用；单 Mod ≤ 2MB |
| 第三方菜单素材版权 | 中 | 高 | **不复制** Nait3D 等作者的 ini/纹理；只借鉴公开的 3DMigoto 语法，素材全部自绘 |
| 3DMigoto 版本差异导致语法失效 | 中 | 中 | 以 XXMI 1.2.x（本机 1.2.3）为基线；生成器带版本校验 |
| Mod 更新覆盖生成物 | 低 | 低 | 源哈希失效检测 + 启动时重生成 |

---

## 依赖与阻塞

**依赖**

- 3DMigoto（XXMI 1.2.x）支持：`cursor_x`、`cursor_y`、`VK_LBUTTON`、`[Present]`、`if/else`、`namespace`、`global persist`。
- `ModIniKeyBindingParser` 扩展。
- 现有安装/导入链路提供生成钩子。
- GDI32 文字渲染 + DDS 写出（零新增依赖）。

**阻塞（Phase 0 结论前不进入实现）**

- 点击命中与输入穿透未知。
- 鼠标锁定时的 `cursor_x/y` 行为未知。

---

## 开放问题

1. **打开键默认值**：`ctrl shift M` 是否与用户环境冲突？设置页需支持录制与冲突提示。
2. **点击穿透**：若游戏同时响应点击，是否接受「菜单只在 ESC/背包等安全界面使用」的引导？
3. **不可点击行**：默认显示（置灰）还是隐藏？MVP 取「显示但置灰」。
4. **多 Mod 同角色**：是否强制自动切成单选，还是只提示？

---

## 附录：本机实测依据

| 证据 | 路径 |
|---|---|
| 可点击菜单参考实现 | `F:\XXMI\WWMI\Mods\character\xinyuehu\心(含大招)-夜月守望者（9）by Nait3D\Normal\MenuHsin.ini` |
| 鼠标坐标来源 | 同文件 `:169-170`（`cursor_x * res_width`） |
| 点击命中 + 变量切换 | 同文件 `:493-511` |
| 多 ini 共存 / 共用 namespace | `mod.ini`、`MenuHsin.ini`、`CommandList.ini` 均含 `namespace = HsinNait3D` 与独立 `[Present]`/`[Constants]` |
| 现有按键解析缺口 | `Core/Entities/Mods/Helpers/ModIniKeyBindingParser.cs:112-117`（跳过 `type` / `$swapvar`） |
| 现有浮窗与 Mod 启停 | `docs/wuwa-overlay-mod-switcher-prd.md`、`Core/Entities/CharacterModList.cs:276-346` |
| 3DMigoto 无内建菜单 | 本机 `.tmp-xxmi-1.2.3/extract/d3d11.dll` 中 `Menu`/`ImGui` 字符串计数均为 0 |

---

*本 PRD 依据交互式需求确认（2026-11-07）：嵌入 Mod、游戏内原生、只取 `[Key*]`、统一组合键、只补无点击能力的 Mod、只做鸣潮、固定高度分页。*
