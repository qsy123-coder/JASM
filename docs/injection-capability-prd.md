# Product Requirements Document: 运行时注入能力（Rocket 同级）

**Version**: 1.0
**Date**: 2026-10-29
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

> 技术依据：[`docs/rocket-injection-and-uhd-texture-analysis.md`](./rocket-injection-and-uhd-texture-analysis.md)

---

## Executive Summary

JASM 现在能改的是**文件**（Mod 包 ini、配置）和**输入**（Elevator 送键、F10 刷新），改不了**游戏进程内的运行时状态**。因此像「主控角色永远用 LOD0」「材质 mip 常驻」「流送档位强制」这类效果，目前只能靠第三方管理器实现——它们靠的是一个注入器把 DLL 塞进 `Client-Win64-Shipping.exe`，再在进程内做 UE 反射调用。

本 PRD 定义 JASM 自研这条通道的**第一段**：一个可靠的注入器（两种注入通道 + 结果核对 + 生命周期管理），**不含**插件本体的 UE 反射逻辑。这样做的理由是分层：注入器是通用能力、可复用、可单测；插件是每游戏一套、紧跟版本、风险最高的一块，应该单独评估（自研 / 外采 / 先不做）。

**已确认的 3 条约束**（2026-10-29 由项目所有者确认）：
1. 仅**本地自用**，不随发布版分发（与老线冻结发布的约定一致）；
2. 第一版**不自研** UE 反射插件，只做「把别家已验证的插件加载进来」的能力；
3. 接受 ACE 反作弊的检测风险，且只在自己的测试机上验证。

这三条直接决定了本功能的形态：**不进入发布包、不做签名与更新链路、不做 GPL 合规包装、不接遥测**。它是一条开发机上的个人能力，不是产品特性。

---

## Problem Statement

**Current Situation**
- 浮窗能力止步于进程外：能切 Mod（改文件 + F10），但无法影响游戏运行时行为。
- 现有 UHD 贴图修复走文件级路线（见 `overlay-uhd-texture-fix-prd.md`），若 Phase 0 结论指向 LOD/流送，文件级根本够不到——这类需求只有注入能解。
- 没有注入能力的后果：每次遇到「只有运行时才能修」的问题，只能建议用户降画质或换第三方管理器。

**Proposed Solution**
1. 一个注入器服务：给定「进程名 / DLL 路径 / 模式 / 延迟」，把 DLL 投进目标进程，并**核对结果**（目标进程模块表里是否出现该模块）。
2. 两条通道：`WH_CBT` 钩子注入（优先，温和）与远程 `LoadLibraryW`（兜底）；失败自动回退。
3. 与现有 Elevator 共用权限链路：游戏以管理员运行时，注入器也必须同权限级。
4. 生命周期跟随游戏进程：闸门共享内存、钩子、句柄在游戏退出时全部回收。

**Business Impact**
- 打开"运行时类 Mod 能力"这扇门，且把风险最高的一块（插件）隔离在边界之外；
- 复用现有权限链路，增量成本集中在注入器本身；
- 为 Phase 0 若判定"文件级不可修"提供明确的后备路径。

---

## Success Metrics

**Primary KPIs**
- **注入成功率**：目标进程已就绪时 ≥ 98%（连续 50 次自动化测试）。
- **稳定性**：注入导致的目标进程崩溃 = 0（50 次）。
- **可诊断性**：任何失败落到枚举原因之一（无权限 / 进程不存在 / DLL 缺失或依赖不全 / 缺 CBTProc / 钩子安装失败 / 超时 / 模块未出现）。
- **时效**：从触发到"模块已在目标进程内" ≤ 10 秒（含可配置延迟）。
- **零残留**：游戏退出后，命名映射、钩子、句柄全部释放（可用 Process Explorer / handle 工具核对）。

**Validation**
- 自动化：对自建的同名测试进程（非游戏）跑 50 次注入/回退/清理矩阵。
- 手工：在真实游戏进程上验证一次（需用户配合，且仅在测试账号上）。

---

## User Personas

### Primary: 本用户（开发者 + 玩家）
- **Role**：JASM 作者，同时是重度 Mod 用户。
- **Goals**：把"只有第三方管理器能做"的运行时效果收编进 JASM；需要可调试、可回滚、可关掉。
- **Pain Points**：第三方管理器黑盒、不可控；出问题只能靠猜。
- **Technical Level**：Advanced。

---

## User Stories & Acceptance Criteria

### Story 1: 一键注入
**As a** 本用户
**I want to** 在浮窗/设置里点一下，把指定 DLL 注入当前游戏进程
**So that** 不需要手动开第三方管理器

**Acceptance Criteria**
- [ ] 提供"注入 / 卸载"两个动作，状态在 UI 上可见（未注入 / 注入中 / 已注入 / 失败原因）。
- [ ] 默认走 Hook 通道；目标 DLL 缺 `CBTProc` 或钩子安装失败时，自动回退直接注入，并在日志说明回退原因。
- [ ] 注入前校验 DLL 存在性与其依赖可加载（先用 `LOAD_LIBRARY_AS_DATAFILE` 试加载）。
- [ ] 注入后核对：目标进程模块表中出现该模块才算成功。

### Story 2: 延迟与竞态
**As a** 本用户
**I want to** 配置"等游戏窗口出现后再注入"
**So that** 不会在游戏还没初始化时把 DLL 塞进去

**Acceptance Criteria**
- [ ] 支持两种触发：进程出现即注入 / 游戏窗口出现后注入。
- [ ] 失败重试有上限，超时给出可读原因，不无限重试。

### Story 3: 干净退出
**As a** 本用户
**I want to** 关掉游戏后不留下任何痕迹
**So that** 下次启动是干净状态

**Acceptance Criteria**
- [ ] 游戏进程退出后：关闭命名共享内存、卸载钩子、关闭句柄（日志记录每条清理动作的结果）。
- [ ] JASM 异常退出后重启，能识别并清理上次残留（映射存在但目标进程不在 → 清理）。

### Story 4: 失败可诊断
**As a** 本用户
**I want** 每次失败都有明确原因
**So that** 我能判断是权限、路径还是反作弊

**Acceptance Criteria**
- [ ] 错误分类枚举化，UI 显示人话，日志带原始 Win32 错误码。
- [ ] "权限不足"场景给出明确引导（以管理员重启 / 走 Elevator）。

---

## Functional Requirements

### Core Features

**Feature 1: 注入作业模型**
- Description：一份纯数据结构描述"投什么、投到哪、怎么投"（进程名、DLL 列表、模式、延迟、是否建闸门映射）。
- Edge cases：同一进程多个实例 → 以 PID 精确锁定，不做"按名字找第一个"。
- Error handling：字段缺失/非法 → 构造期拒绝，不进入注入流程。

**Feature 2: 双通道注入**
- Hook 通道：`CreateMutexW(L"Local\\<加载器名>")` 防重入 → `LoadLibraryExW(AS_DATAFILE)` 摸 `CBTProc` → `SetWindowsHookExW(WH_CBT, ...)` → 等目标模块出现。
- 直接通道：远程 `LoadLibraryW`，用 **kernel32 相对偏移**计算目标地址；等待线程退出码。
- 回退策略：Hook 失败 → 直接通道；两者都失败 → 报错并要求用户处理（不静默）。

**Feature 3: 闸门与生命周期**
- Description：按需创建命名共享内存作为"插件总开关"，生命周期绑定游戏进程。
- Constraints：创建者退出即释放；重复创建要容忍 `ERROR_ALREADY_EXISTS`。

**Feature 4: 结果核对**
- Description：注入后枚举目标进程模块，确认模块名出现；可选核对导出是否可解析（通过本地同版本 DLL 的导出表推断）。

### Out of Scope
- **不自研 UE 反射插件**（ForcedLOD / mip 常驻 / 流送档位）——单独立项。
- 不做反作弊对抗、不做检测规避、不做隐藏（不注入到受保护进程以外的场景）。
- 不修改游戏目录内任何文件。
- 不做遥测/上报。
- 不接入发布链路（`Release.py` / CI / 自动更新 / 代码签名），见「决策记录」约束 1。

---

## Technical Constraints

### Performance
- 注入本身 < 2 秒（不含可配置延迟）；不得在游戏进程内留下轮询线程。
- 注入器自身常驻内存开销可忽略（仅在动作期间工作）。

### Security
- 与游戏同权限级；提权路径复用 `Elevator`，不新建提权组件。
- 不落盘明文凭据；日志不记录用户完整路径以外的敏感信息。
- DLL 来源必须可校验（哈希白名单），拒绝任意路径注入。

### Integration
- **Elevator**：权限对齐与前台归还可以复用现有协议。
- **浮窗/设置页**：状态与开关入口。
- **Wuwa Mod Fixer 链路**：彼此独立，注入失败不影响文件级修复。

### Technology Stack
- 注入器逻辑放 `GIMI-ModManager.Core`（可单测的作业模型 + 结果分类）；
- 进程/句柄操作放 `GIMI-ModManager.WinUI/Services`；
- 需要 P/Invoke 的新 API 必须同步补 `NativeMethods.txt`（参见仓库既有约定）。

### 合规
- 若注入的是 3DMigoto 派生 DLL 且**对外分发**，需遵守 GPLv3；仅本地自用不涉及分发。
- 向游戏进程注入属于对运行时的改写，受游戏服务条款与 ACE 约束。

---

## MVP Scope & Phasing

### Phase A: 可行性 spike（1–2 天）

> 本机实测：**没有 MSVC / `cl.exe`**，只有 .NET SDK 9.0.316。这与 Elevator 的 AOT 限制同源，所以 Phase A 拆成两步，先做**不需要编译原生 DLL** 的那一半。

**A1：验证注入通道与结果核对（纯 C#，无需新编译器）** —— ✅ **2026-10-29 已完成**

> 实现：`src/Tools/JASM.InjectorProbe/`（未入 sln、不被主程序引用）
> 实测：普通流程 **50/50 成功**（中位 1.2 ms / P95 2.2 ms）；冻结流程 **10/10 成功**；失败矩阵四项分类全对。
> 附带证伪一条常见做法：`CREATE_SUSPENDED` 的进程**不能**注入（加载器未初始化、kernel32 不可用，实测 0/10），
> 正确姿势是「先运行 → 冻结全部线程 → 注入 → 解冻」。

- 注入目标先用系统 DLL（如 `winmm.dll`）替代自研 DLL —— 通道行为与目标 DLL 无关。
- 用自建的**同名测试进程**（`cmd.exe` 改名）做靶子，不动真实游戏。
- 验证：`OpenProcess` 权限矩阵、`VirtualAllocEx/WriteProcessMemory/CreateRemoteThread` 序列、退出码判定、模块枚举核对、超时与清理。

**A2：验证 Hook 通道（需要导出 `CBTProc` 的原生 DLL）**
- 需要一个真正的原生 DLL。本机编不了 → 二选一：装 VS Build Tools，或走 CI 产出（与 `Elevator` 相同的路径）。
- 只有在 A1 全绿之后才做 A2；A2 未完成不影响把"直接注入"作为第一版通道。

**Gate**：直接注入通道在本机测试进程上稳定可用（≥ 50 次），即可进入 Phase B；Hook 通道可作为 Phase B 的增量。

### Phase B: 注入器产品化
- 作业模型 + 双通道 + 回退 + 结果核对 + 生命周期 + 错误分类 + 日志。
- 对自建同名测试进程做 50 次矩阵测试。

### Phase C: 插件接入（另行评估）
- 决定自研 / 外采 / 不做 UE 反射插件。
- 若外采，明确授权、GPL、更新与卸载路径。

**MVP Definition**：能把一个指定 DLL 稳定注进目标进程，并在游戏退出后零残留；不包含任何游戏内效果。

### Future Considerations
- UE 反射插件（LOD/mip/流送）。
- 多游戏适配（每个游戏一套 DLL 与触发策略）。

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| ACE 反作弊检测导致封号 | Medium | High | 只在自用测试账号验证；不随发布版分发；UI 明示风险并默认关闭 |
| 注入导致游戏崩溃 | Medium | High | 严格的结果核对 + 回退策略；崩溃时自动停止后续注入并记录 |
| 游戏版本更新导致钩子/偏移失效 | High | Medium | 第一版不做偏移；插件侧的版本自愈放到 Phase C |
| GPLv3 传染（分发改过的 3DMigoto） | Low | High（若分发） | 决策 1 已排除分发；万一要发版，先做合规评估，且只分发未修改的第三方二进制 |
| 与现有 Elevator/浮窗链路冲突（权限、前台） | Medium | Medium | 复用同一提权路径，先做权限矩阵测试 |
| 用户误开导致事故 | Medium | Medium | 默认关闭 + 二次确认 + 显眼的风险提示 |

---

## Dependencies & Blockers

**Dependencies**
- 可导出的 `CBTProc` 入口 DLL（自研最小 DLL 或第三方提供）。
- `Elevator` 提权链路（同权限级注入）。
- 目标进程可枚举模块的 API（`K32EnumProcessModules` 等）。

**Known Blockers**
- Phase A（A1 直接注入通道）结论未出：若不通过，本 PRD 的 MVP 前提失效，需回到方案层重评估。
- A2 Hook 通道依赖原生 DLL 与编译器：本机无 MSVC，需装 VS Build Tools 或走 CI 产出后才能验证。

---

## 决策记录

| # | 决策（2026-10-29 确认） | 对交付的影响 |
|---|---|---|
| 1 | 仅本地自用，不随发布版分发 | **不接入** `Release.py` / CI / 自动更新 / `UpdateChecker`；不需要代码签名与 GPL 合规包装；入口默认不暴露给普通用户。若日后要分发，需重新立项补齐签名、更新、GPL、风险提示与开关策略 |
| 2 | 第一版不自研 UE 反射插件 | Phase C 只做「加载并核对第三方插件」的接口；插件来源、授权与更新由使用者自行负责；本 PRD 不承诺任何游戏内视觉效果的验收 |
| 3 | 接受 ACE 风险，只在测试账号验证 | 所有实机验证限定在本机测试账号；UI 必须显示风险提示且默认关闭；不提供任何检测规避手段 |

**约束 1 的延伸要求**：实现上应保证「删掉这段代码，产品行为完全不变」——新增服务不得被发布路径反向依赖，避免将来误打包。

---

## Appendix

### Glossary
- **WH_CBT**：`SetWindowsHookEx` 的钩子类型 5，通过线程创建事件把 DLL 带入目标进程。
- **闸门共享内存**：命名映射，插件以"存在性"判断是否启用。
- **结果核对**：注入后枚举目标进程模块，确认目标 DLL 真的进去了。

### References
- [`docs/rocket-injection-and-uhd-texture-analysis.md`](./rocket-injection-and-uhd-texture-analysis.md) —— 逆向依据（含可直接借鉴的实现细节与坑）
- [`docs/overlay-uhd-texture-fix-prd.md`](./overlay-uhd-texture-fix-prd.md) —— 文件级修复路线（本能力的对照面）
- `src/Tools/rea/` —— 分析脚本（Ghidra + LODENC01 解密工具）

---

*v1.0：3 项约束已于 2026-10-29 确认并写入「决策记录」，质量分由 82 升至 91。下一道门是 Phase A 的可行性 spike。*
