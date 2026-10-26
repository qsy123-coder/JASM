# Product Requirements Document: 浮窗 Mod 修复（Wuwa Mod Fixer 集成）

**Version**: 1.1
**Date**: 2026-10-26
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

---

## Executive Summary

鸣潮每次大版本更新都会让 WWMI 产出的第三方 Mod 失效（组件 vb0 哈希、贴图哈希、材质引用过期）。目前 JASM 只有一份手工修复手册（`docs/wuwa-mod-hash-fix.md`），要求用户开 hunting、dump、读 log、逐条改 ini，普通玩家无法独立完成。上游开源工具 **Wuwa Mod Fixer**（`Moonholder/Wuwa_Mod_Fixer`，GPL-3.0）已经把这套流程数据化、自动化，但它是独立程序：每次都要用户在文件对话框里选 Mod 目录，与 JASM 的 Mod 管理完全割裂。

本功能把 Wuwa Mod Fixer 的修复能力接入 JASM 的**游戏内浮窗**（Ctrl+Alt+J）：用户不需要选目录——浮窗知道当前角色和它的全部 Mod；也不需要退出游戏——修复完成后自动走现有的 F10 刷新链路生效。修复引擎采用上游官方 CLI（`wuwa-mod-cli`），JASM 内嵌分发、按其 NDJSON 事件流驱动进度；备份沿用上游的 `_<时间戳>.BAK` 机制，回滚同样在浮窗内完成。

**交付范围**：落在老线 `master` 的浮窗上，本地分支自用，不 push、不发版（遵循老线冻结发布的约定）。

---

## Problem Statement

**Current Situation**
- 游戏更新后 Mod 集体失效，用户要么等 Mod 作者更新，要么按手册手动修 ini——门槛极高，且需要理解 hash/vb0/贴图槽位。
- 上游 Wuwa Mod Fixer 能自动修，但每次要手选 Mod 文件夹；JASM 用户手里可能有几十个 Mod 分散在角色目录下，逐个选目录是不可接受的重复劳动。
- 修复必须回到桌面程序、切出游戏，体验断裂；修完还要自己记住按 F10 重载。

**Proposed Solution**
在浮窗中新增「修复」页签：
1. 自动以**当前角色**的全部 Mod 为范围，无需选择目录；
2. 五个修复开关与上游一致，默认关闭，主修复恒定执行；
3. 内置官方 CLI 与配置表，支持手动「更新配置」并提示新版；
4. 进度、当前 Mod、结果摘要与可展开日志都在浮窗内；
5. 修复完成后自动发 F10 刷新；失败降级为提示手动刷新；
6. 复用 `.BAK` 备份，浮窗内提供还原点列表（回滚 / 还原到最早 / 清理）。

**Business Impact**
- 把「游戏更新后修一批 Mod」从 10+ 分钟的桌面操作压缩到浮窗内 3 步；
- 强化 JASM 的差异化：不只管理 Mod，还负责 Mod 的存活周期；
- 降低用户因 Mod 失效而卸载 JASM 的概率。

---

## Success Metrics

**Primary KPIs**
- **操作步数**：打开浮窗 → 修复完成 ≤ 3 次操作（切页签、点开始、可选勾选）。
- **修复耗时**：当前角色 ≤ 20 个 Mod 时，本地修复 ≤ 60 秒（不含网络）。
- **备份完整性**：被修改的每个 ini 都必须有对应 `.BAK`；抽样回滚后与修复前**逐字节一致**。
- **可诊断性**：任何失败都必须给出可读原因（CLI 未就绪 / 未识别角色 / 文件被占用 / 磁盘满 / 配置不兼容）。

**Validation**
- 开发期：`JASM.Tests` 覆盖备份扫描/回滚/配置校验/参数构造/NDJSON 解析；用真实 Mod 包做一次端到端手测。
- 上线后（本地自用）：由用户实机验收「游戏更新后修一个角色 → F10 → 模型/贴图恢复」。

---

## User Personas

### Primary: 浮窗玩家（本用户）
- **Role**：在游戏内用浮窗切换 Mod 的鸣潮玩家。
- **Goals**：游戏更新后尽快恢复 Mod；不想退出游戏、不想记哈希。
- **Pain Points**：手选目录、手改 ini、改错导致花屏；找不到旧版备份。
- **Technical Level**：Intermediate（会用 Mod，不写 ini）。

### Secondary: 排障用户
- **Role**：遇到修复失败需要定位原因的人（可能是自己）。
- **Goals**：看到失败在哪一步、哪个 Mod、什么原因；能回滚。
- **Technical Level**：Advanced。

---

## User Stories & Acceptance Criteria

### Story 1: 一键修复当前角色

**As a** 浮窗玩家
**I want to** 打开浮窗的「修复」页，点一下就开始修当前角色的全部 Mod
**So that** 游戏更新后不用选目录、不用退出游戏

**Acceptance Criteria**
- [ ] 浮窗标题栏出现「Mod 切换 / 修复」两个页签；进入修复页时当前角色保持不变，并显示「角色名 · N 个 Mod」。
- [ ] 默认全选当前角色的全部 Mod（包括当前未启用的 Mod）。
- [ ] 点击「开始修复」后，逐个 Mod 串行调用 CLI；进度条按 Mod 数推进，显示当前处理项。
- [ ] 修复完成后展示摘要：成功 N / 跳过 M / 失败 K（失败附原因），并自动触发一次 F10 刷新。
- [ ] 当前游戏不是鸣潮、或当前角色没有任何 Mod 时，修复页显示空态与引导，按钮禁用。

### Story 2: 修复选项（五个开关）

**As a** 浮窗玩家
**I want to** 只启用我需要的修复项
**So that** 不上游文档警告的「误开」坑

**Acceptance Criteria**
- [ ] 五个开关与上游一致：补全贴图状态、应用稳定纹理、模组破洞/部位缺失修复、修复爱弥斯机兵形态的模型异常、女漂-风主形态眼部修复。
- [ ] 「补全贴图状态」与「应用稳定纹理」互斥：打开一个自动关闭并禁用另一个（与上游 UI 行为一致）。
- [ ] 「模组破洞/部位缺失修复」在开启时显示琥珀色警告；「女漂」开启后显示模式 1/2 二选一与提示。
- [ ] 所有开关默认关闭；开关状态持久化到 `OverlaySettings`，下次打开浮窗保持上次选择。
- [ ] 修复进行中开关不可修改。

### Story 3: 单个修复（取消勾选）

**As a** 浮窗玩家
**I want to** 只修其中某几个 Mod
**So that** 不必为个别 Mod 单独找目录

**Acceptance Criteria**
- [ ] 修复页可展开 Mod 勾选列表；默认全选，提供「全选 / 全不选」。
- [ ] 取消勾选后点开始，只处理勾选中的 Mod；未勾选的不产生任何文件写入。
- [ ] 全部取消勾选时按钮禁用并提示「至少选择一个 Mod」。

### Story 4: 进度与日志

**As a** 玩家
**I want to** 知道修到哪了、出了什么问题
**So that** 长任务不焦虑、失败能定位

**Acceptance Criteria**
- [ ] 进度区包含：进度条（按 Mod 数）、当前 Mod 名、成功/跳过/失败计数。
- [ ] 日志默认折叠；展开后显示最近 N 条（含 CLI 输出与 JASM 侧事件），不自动抢焦点。
- [ ] 运行中「开始修复」变为「终止」；终止后已处理的 Mod 保留其结果与备份，未处理的不计入成功。
- [ ] 同一时刻只允许一个修复任务；离开页签或关闭浮窗时任务继续并保留状态，重开后可见。

### Story 5: 备份回滚

**As a** 玩家
**I want to** 撤销修复、回到任意一次修复前的状态
**So that** 修坏了有退路

**Acceptance Criteria**
- [ ] 回滚视图按 `.BAK` 时间戳分组列出还原点（倒序），每组显示时间、文件数、影响 Mod 数、占用空间。
- [ ] 「回滚到此」把目标组及之后所有组的文件恢复到**目标组之前**的状态，并删除这些组的 `.BAK`（与上游语义一致）。
- [ ] 「还原到最早」恢复到最早还原点之前的状态；「清理全部备份」仅删除 `.BAK`，不改动 Mod 文件，执行前二次确认。
- [ ] 0 字节 `.BAK` 视为「修复新增的文件」标记，回滚时删除对应原文件（与上游一致）。
- [ ] 没有 `.BAK` 时显示空态；备份文件被外部占用/缺失时给出逐文件失败原因，不半途静默。
- [ ] 回滚完成后同样自动 F10 刷新（与修复共用链路与节流）。

### Story 6: 配置更新

**As a** 玩家
**I want to** 游戏更新后拿到最新哈希表
**So that** 新角色/新贴图也能修

**Acceptance Criteria**
- [ ] 修复页底部显示当前配置版本（如 `cfg 3.7.1`）与「更新配置」按钮。
- [ ] 进入修复页时静默检查一次（每会话一次，失败不打扰）；有新版本时按钮亮提示点。
- [ ] 点击后优先从 Gitee 原始链接拉 `config.json`，失败回退 GitHub raw；成功后原子替换并显示新版本号。
- [ ] 校验失败（JSON 非法 / 缺 `characters` / `version.min_required_version` 高于内置 CLI）时拒绝替换并提示原因。
- [ ] 离线或网络失败时保留旧配置，不影响修复功能。

### Story 7: 自动生效

**As a** 玩家
**I want to** 修完立刻在游戏里看到效果
**So that** 不用退出游戏

**Acceptance Criteria**
- [ ] 修复/回滚结束后，复用 `OverlayRefreshCoordinator` 发一次 F10（含前台交接与 2.5s 节流）。
- [ ] 发键失败（助手缺失/提权受限）时，在浮窗显示「请手动按 F10 刷新」，不阻断后续操作。
- [ ] 只在浮窗可见且光标在浮窗上时执行前台交接（沿用现有规则，避免把用户踢出游戏）。

---

## Functional Requirements

### F1. 浮窗信息架构

```
┌─ 标题栏：角色头像 · [Mod 切换 | 修复] · 尺寸 · 关闭 ─┐
│ 修复页                                               │
│  ├─ 二级切换：[修复] [回滚]                          │
│  ├─ 范围条：当前角色 · N 个 Mod · 全选/勾选列表       │
│  ├─ 开关面板 ×5（互斥/警告/模式选择）                 │
│  ├─ 主按钮：开始修复 / 终止                          │
│  ├─ 进度 + 可展开日志                                │
│  └─ 底栏：cfg 版本 · 更新配置 · 回滚入口              │
└──────────────────────────────────────────────────────┘
```

- 页签切换不改变角色上下文；「Mod 切换」页现有布局与手势保持不变。
- **尺寸**：三档调整为小 380×360、中 460×480、大 520×660 DIP；修复页首次进入自动选大档，之后可自由切档、离开保持当前档位，整页可滚动以适配小屏（详见 `overlay-ux-adjustments-prd.md`）。
- **键盘**：浮窗是 `WS_EX_NOACTIVATE` 窗口，永远没有键盘焦点，因此**不新增全局热键**，修复页为纯鼠标操作；修复页激活时现有 ↑/↓/Enter/R 导航热键全部短路，避免误改盘或误刷新。
- 修复页在非鸣潮游戏下隐藏页签。

### F2. 修复引擎（内置官方 CLI）

- 随 JASM 内嵌 `wuwa-mod-cli`（固定版本 `v3.7.0`，约 3.9 MB；上游 release asset `wuwa-mod-cli_v3.7.0.exe`），运行时释放到 `%LOCALAPPDATA%\JASM\WuwaModFixer\wuwa-mod-cli.exe`，沿用 Elevator 的「版本标记 + 原子落盘」手法。
- 每个 Mod 调用一次：
  ```
  wuwa-mod-cli.exe --path "<mod目录>" --format json --config "<config.json>"
                   [--derived-hashes | --stable-texture] [--rendering-33]
                   [--aemeath-mech] [--aero-fix 1|2]
  ```
- 解析 stdout 的 NDJSON：`start{mode,total}` / `progress{current,total,percent}` / `log{level,message}` / `finish{success,message}`；stderr 作为诊断日志收集。
- 进程创建用 `ProcessStartInfo.ArgumentList`（避免路径含空格/中文被拼接破坏），隐藏窗口，重定向 stdout/stderr，超时与取消时结束进程树。
- 判定：进程退出码 0 且收到 `finish.success=true` 才算成功；否则记为失败并附 `finish.message` 或 stderr 末段。

### F3. 配置数据

- **内置**：随包附带 `config.json`（编译自上游 `config.yml` 的当前版本，`cfg 3.7.1`），首次运行释放到 `%LOCALAPPDATA%\JASM\WuwaModFixer\config.json`，并记录来源版本。
- **更新**：Gitee `https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config.json` → GitHub raw 回退；校验通过后写临时文件并 `File.Move(overwrite)` 原子替换，保留上一份 `config.json.bak`。
- **校验**：JSON 可解析；`characters` 非空；`version.current_version` 可解析；`version.min_required_version` ≤ 内置 CLI 版本。任一项失败则拒绝替换。
- 通过 `--config` 显式传入，不使用 CLI 的 `--online`（避免每次修复触网）。
- 页面展示版本 = 本地 config 的 `version.current_version`。

### F4. 备份与回滚

- **备份由 CLI 生成**，命名规则：`<原文件名含扩展名>_<yyyy-MM-dd HH-mm-ss[.fff]>.BAK`，位于 Mod 目录内。
- JASM 只读扫描 + 执行回滚，**不自行创建备份**（保证与上游工具互通）。
- 扫描范围：当前角色全部 Mod 目录（递归）。
- 分组：按时间戳字符串分组，倒序；统计文件数、影响 Mod 数、占用（0 字节标记按目标文件大小计）。
- 回滚算法（对齐上游 `rollback.rs`）：
  1. 找到目标组及**所有更新**的组；
  2. 对每个原文件，取所有候选 `.BAK` 中**最早的一份**；
  3. 0 字节 → 删除对应原文件；否则复制回原路径（必要时建目录）；
  4. 删除参与本次回滚的全部 `.BAK`；
  5. 任一步失败即中止并列出已完成项与失败项。
- 「还原到最早」= 目标组为最早一组；「清理全部备份」只删 `.BAK`（二次确认）。

### F5. 状态与持久化

`OverlaySettings` 新增（缺字段一律回退默认，不抛异常）：
- `FixOptions`：`DerivedHashes` / `StableTexture` / `Rendering33` / `AemeathMech` / `AeroFixMode(0|1|2)`，默认全关。
- `LastFixTab`：`fix` / `rollback`。
- `LastConfigCheckUtc`：静默检查节流用。

### F6. 错误处理

| 场景 | 行为 |
|---|---|
| CLI 缺失/损坏/被杀软删除 | 自动重新释放一次；仍失败则报「修复引擎不可用」并附路径 |
| Mod 内没有 ini / 未识别角色 | 计为「跳过」，展示 CLI 的匹配结果 |
| 目录只读 / 磁盘满 / 文件被占用 | 该 Mod 计失败并给出系统错误码；继续后续 Mod |
| 配置版本不满足 CLI 最低要求 | 禁止开始修复，提示更新配置或程序 |
| 用户在修复中关闭浮窗 | 任务继续；重开浮窗可看到进度/结果 |
| 网络失败（检查/更新配置） | 静默保留旧配置；手动更新时给 toast |
| 回滚时备份文件缺失 | 中止该文件并列出，不删除其 `.BAK` |

---

## Technical Constraints

### Performance
- 单角色 20 个 Mod 的修复在 60 秒内完成（本地）。
- 备份扫描只读目录元数据；回滚文件操作不得全量读入内存。
- 进度更新合并节流（≥100 ms），避免 UI 线程抖动。

### Security
- 只允许对当前角色 Mod 根目录内的路径执行扫描/删除；拒绝符号链接跳出。
- 不把完整本地路径写进对外日志/分享内容（与现有日志规范一致）。
- 内置 CLI 固定版本 + SHA256 记录；更新配置仅接受白名单主机。

### Integration
- **浮窗**：`OverlayWindow.xaml` / `OverlayViewModel` / `OverlaySettings`。
- **刷新链路**：`OverlayRefreshCoordinator`（F10 + 前台交还 + 2.5s 节流）。
- **Mod 模型**：`characterSkinEntry.Mod.FullPath` 作为 CLI `--path`。
- **提权助手**：复用现有 Elevator 通道；无助手时降级为提示手动 F10。
- **本地化**：新增文案同时补 `zh-CN` 与 `en-us` 的 `resw`（缺 `en-us` 会静默回退中文）。
- **许可证**：JASM 为 GPL-3.0，内置 GPL-3.0 的 CLI 兼容；新增 `THIRD-PARTY-NOTICES`（上游仓库、版本、源码链接）。

### Technology Stack
- .NET 9 / WinUI 3 现有栈；不引入新依赖（JSON 用 Newtonsoft，进程用 BCL）。
- 发布链路：CLI 以 `EmbeddedResource` 内嵌（单文件版无散文件可放），folder 版可额外复制一份到 exe 同目录并被优先使用。

---

## Out of Scope

- 上游 GUI 的赞助、检查更新、语言/主题设置；
- 自动批量修复「全部角色」（Phase 2）；
- 用 JASM 自己的备份体系替代 `.BAK`；
- 把修复能力移植到新线（JASM1）——本 PRD 只覆盖老线浮窗；
- 修复非 WWMI 的 Mod（原神等）；
- 自动判定 Mod 是否失效（仍需用户发现异常后手动触发）。

---

## MVP Scope & Phasing

### Phase 1（MVP，本次交付）
- 浮窗页签 + 修复页（范围条、五开关、勾选列表、进度、折叠日志）；
- 内置 CLI + 配置 + 版本展示 + 手动更新配置 + 新版提示；
- 逐 Mod 串行修复、终止、错误归因；
- `.BAK` 还原点列表（回滚 / 还原到最早 / 清理）；
- 修复与回滚结束后自动 F10（失败降级提示）；
- 设置持久化、zh/en 文案、单元测试。

### Phase 2（后续）
- 全部角色批量修复与跨角色备份视图；
- Mod 行右键「修复此 Mod」快捷入口；
- 修复历史（时间、选项、结果）与配置 diff 预览；
- 移植到新线浮窗。

### Future
- C# 重写修复核心，摆脱外部 exe；
- 结合 dump/hunting 自动判定失效 Mod。

**MVP Definition**：玩家在游戏内打开浮窗，3 步内修好当前角色的 Mod 并立即看到效果；修坏了能在同一处回滚。

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| 上游 CLI 参数/事件格式变化 | Med | High | 固定 v3.7.0；解析器对未知事件容错；升级前对照 release notes |
| 配置表与游戏版本错配 | Med | High | 版本展示 + 更新按钮 + 最低版本校验；修复失败有归因 |
| CLI 被杀软误报/删除 | Med | Med | 固定版本 + 签名由上游 SignPath 提供；启动时校验存在性并自动重放 |
| 自动 F10 前台交接失败 | Med | Med | 复用已验证链路；失败降级为提示手动 F10 |
| 误开高风险开关（3.3 / 爱弥斯） | Med | Med | 默认关、警告文案、互斥规则；回滚入口同屏可见 |
| 回滚删除范围过大 | Low | High | 只处理当前角色目录；二次确认；先校验备份文件存在再改盘 |
| `_timestamp.BAK` 与用户文件重名 | Low | Low | 严格正则匹配；非匹配文件不动 |
| 老线不发版导致用户拿不到 | High | Med | 明确本功能为本地自用；如需分发再走新线 |

---

## Dependencies & Blockers

**Dependencies**
- 上游 `wuwa-mod-cli v3.7.0`（release asset 与 SHA256 需固化进构建脚本）。
- 上游 `config.json`（当前 `cfg 3.7.1`，Gitee/GitHub raw 可达）。
- 现有浮窗刷新链路与 Elevator 助手可用。

**Known Blockers**
- 本机无 MSVC 不影响本功能（不涉及 AOT）；但单文件包验证仍受既有打包流程约束。
- 自动 F10 的真实效果依赖实机验收（前台锁行为无法在开发机模拟）。

---

## Appendix

### A. CLI 事件流（NDJSON）

```json
{"event":"start","mode":"fix","total":3}
{"event":"progress","current":1,"total":3,"percent":33.3}
{"event":"log","level":"info","message":"Matching Objects Feixue..."}
{"event":"finish","success":true,"message":"修复完成"}
```

### B. 选项 ↔ CLI 参数

| 浮窗开关 | CLI 参数 |
|---|---|
| 补全贴图状态 | `--derived-hashes` |
| 应用稳定纹理 | `--stable-texture` |
| 模组破洞/部位缺失修复 | `--rendering-33` |
| 修复爱弥斯机兵形态 | `--aemeath-mech` |
| 女漂-风主眼部修复 | `--aero-fix 1` / `--aero-fix 2` |
| 基础一键修复 | 无（恒定执行） |

### C. 备份命名

```
原文件：mod.ini
备份：  mod.ini_2026-10-26 20-15-03.BAK
正则：  ^(.*)_(\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}(?:\.\d{3})?)\.BAK$
```

### D. 决策记录

| 决策点 | 结论 |
|---|---|
| 集成方式 | 内置上游官方 CLI，NDJSON 驱动 |
| 入口 | 浮窗顶部页签，按需重构布局与尺寸 |
| 修复范围 | 默认当前角色全部 Mod，可勾选子集 |
| 配置数据 | 内置 + 手动更新 + 新版提示 |
| 备份/回滚 | 复用 `.BAK`，浮窗内还原点列表 |
| 日志 | 进度 + 可展开日志 |
| 生效方式 | 自动 F10，失败降级提示 |
| 交付 | 本地分支自用，不 push、不发版 |

### E. References
- 上游仓库：https://github.com/Moonholder/Wuwa_Mod_Fixer （GPL-3.0）
- 上游版本：v3.7.0；配置：cfg 3.7.1
- 现有手修手册：`docs/wuwa-mod-hash-fix.md`
- 浮窗 PRD：`docs/wuwa-overlay-mod-switcher-prd.md`

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
