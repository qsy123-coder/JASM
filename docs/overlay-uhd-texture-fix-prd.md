# Product Requirements Document: 浮窗 UHD 贴图修复

**Version**: 1.0
**Date**: 2026-10-29
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

---

## Executive Summary

鸣潮的 Mod 贴图修复目前只在 **HD 资源档位**下有效。切到 **UHD（极致资源）**后，主控角色的 Mod 会整体错乱 / 变彩虹 / 糊成一团，而浮窗里的「修复」跑完也救不回来。对已经在用浮窗切换 Mod 的玩家来说，这意味着「开了最高画质就不能用 Mod」，而这恰恰是装了 Mod、又愿意折腾画质的那批人最不能接受的组合。

本功能在浮窗「修复」页新增 **资源档位选择（SD / HD / UHD）** 与 **「UHD 贴图修复」开关**，沿用现有的 Wuwa Mod Fixer 文件级修复链路（`wuwa-mod-cli` + `config.json`，重写 Mod 包内 ini 的组件 vb0 哈希与贴图引用），把修复的适用范围从 HD 扩展到 UHD。修复范围、备份、回滚、F10 刷新全部复用现有链路，不引入新的注入组件、不改动游戏本体与画质设置。

因为「UHD 为什么失效」的根因尚未取证，本 PRD 把 **[Phase 0 取证]** 作为交付的第一段：只有当取证给出「改哪一处、从异常变正常」的最小证据后，才进入编码。若取证结论指向 LOD / 流送而不是哈希，则会触发一次方案复评（见 §风险）。

**交付范围**：老线浮窗，本地分支自用，不 push、不发版。

---

## Problem Statement

**Current Situation**
- 浮窗「修复」= 上游 `wuwa-mod-cli` 重写 Mod 包内 ini 的组件哈希与贴图引用，**整套代码没有任何资源档位概念**，只按一套哈希/引用去修。
- 玩家切到 UHD 后，同一批 Mod 出现整体贴图错乱，现有修复跑完无效，且浮窗不会提示「当前档位下这个修复不适用」。
- 参考实现（第三方管理器 Rocket）是**双管齐下**：运行时钉 LOD0 + 材质 mip 常驻 + 流送档位强制=4（旧引擎，v2.9 完整保留），**外加**一张内嵌的 `UHD-to-HD` 别名表 —— 2026-10-29 的 v2.9 在 RCDATA 里带着 **6118 行、18 个角色**的 UHD↔HD 纹理/组件哈希对照（见 `rocket-injection-and-uhd-texture-analysis.md` §10.5）。
- 这基本回答了「UHD 的错是不是哈希错」：**是**，至少作者是这么认为并且专门维护了数据。本 PRD 的 Phase 0 因此从"判断根因"改为"**验证这张表的语义、确认我们能不能自己生成**"。

**Proposed Solution**
1. **Phase 0（开发期线下取证，不进产品）**：用 3DMigoto hunting/dump 对比同一 Mod 在 HD 与 UHD 下的贴图绑定与 `TextureOverride` 命中情况，给出根因分类与最小可验证改动。
2. **Phase 1（MVP）**：浮窗修复页新增档位选择与 UHD 开关；按 Phase 0 结论，在现有 CLI 调用链上追加 UHD 处理（新增参数、或修后后处理、或两者），备份/回滚/进度/日志沿用现状。
3. 档位由用户手选并记住；上游数据缺失时给出明确可读的失败原因，而不是「修完了但还是错的」。

**Business Impact**
- 解除「UHD 与 Mod 二选一」的限制，直接扩大可用人群；
- 复用现有链路使增量成本集中在**数据**而不是**架构**；
- 把「修不好且不知道为什么」变成「明确告知缺口在哪」，减少误报与反复试错。

---

## Success Metrics

**Primary KPIs**
- **修复后首帧正确率**：抽样 20 个「角色 × Mod」组合，UHD 档位修复后贴图正常 ≥ 19（≥95%）。
- **HD 无回归**：同一批样本在 HD 档位下修复结果与当前版本逐项一致（100%）。
- **回滚完整性**：100% 被改动的 ini 有对应 `.BAK`；抽样回滚后与原文件逐字节一致。
- **单角色修复耗时**：≤ 20 个 Mod 时 ≤ 60 秒。
- **可诊断性**：任何失败都必须落到可读原因之一 —— 上游数据缺 UHD / 档位选错 / 文件被占用 / 磁盘满 / CLI 或配置版本不满足。

**Validation**
- 开发期：`JASM.Tests` 覆盖档位参数构造、UHD 分支开关、回滚与备份扫描；用真实 Mod 包做端到端手测。
- 上线后（本地自用）：用户在 UHD 档位下修一个角色 → F10 → 模型与贴图恢复，且切到其他角色仍正常。

---

## User Personas

### Primary: 高画质 Mod 玩家（本用户）
- **Role**：在游戏内用浮窗切换 Mod 的鸣潮玩家，画质开 UHD。
- **Goals**：不降画质也能用 Mod；游戏更新后一条链路修完所有角色。
- **Pain Points**：UHD 下 Mod 整体错乱；现有修复跑完无效且不说为什么；不知道要不要为此降回 HD。
- **Technical Level**：Advanced（会用浮窗、会看日志，但不写代码）。

### Secondary: 普通浮窗玩家
- **Role**：画质多为 HD，偶尔切 UHD 试试。
- **Goals**：切档位后不要莫名其妙坏掉。
- **Pain Points**：档位与修复状态互不相关，出了问题不知道是档位还是 Mod 的问题。
- **Technical Level**：Intermediate。

---

## User Stories & Acceptance Criteria

### Story 1: 选择资源档位
**As a** 高画质 Mod 玩家
**I want to** 在修复页告诉 JASM 我在用哪个资源档位
**So that** 修复按正确的一套数据执行

**Acceptance Criteria**
- [ ] 修复页出现 SD / HD / UHD 三选一，默认取上次选择；首次使用默认 HD（与当前行为一致）。
- [ ] 选择立即写入设置并持久化，重启 JASM 后仍保留。
- [ ] 切换档位不影响已安装 Mod 与已有备份。
- [ ] 档位控件旁有说明文案：选错档位可能导致修复无效（不是崩溃）。

### Story 2: 一键修 UHD 下的贴图
**As a** 高画质 Mod 玩家
**I want to** 开着 UHD 打开浮窗点修复，所有已装 Mod 角色的贴图都正常
**So that** 我不需要为了用 Mod 把画质降到 HD

**Acceptance Criteria**
- [ ] 修复页新增「UHD 贴图修复」开关，默认关闭；档位 ≠ UHD 时该开关置灰并提示「当前档位不需要」。
- [ ] 档位=UHD 且开关开启时，修复流程在现有「一键修复 + 五个开关」基础上追加 UHD 处理，进度与日志沿用现有 UI。
- [ ] 修复完成后自动发 F10 刷新；F10 失败时降级为提示手动刷新（沿用现有行为）。
- [ ] 修复后：所有已装 Mod 的角色，切到该角色贴图正常，不出现整体错乱 / 彩虹 / 糊。
- [ ] 上游数据未覆盖 UHD 时，开关可用但执行前明确提示「上游配置未包含 UHD 数据，请更新配置或等待上游」，不产生半小时假修复。

### Story 3: 失败可理解、可回滚
**As a** 普通浮窗玩家
**I want to** 修复失败时知道原因，并且能回到修之前
**So that** 我不会把 Mod 越修越坏

**Acceptance Criteria**
- [ ] 失败原因归类到：上游缺 UHD 数据 / 档位不匹配 / 文件占用 / 权限 / 磁盘 / CLI 或配置版本。
- [ ] 每个被改动的 ini 在修复前生成 `.BAK`；回滚点列表与「还原到最早」「清理」沿用现有实现。
- [ ] 回滚后文件与原文件逐字节一致。
- [ ] 修复被中断（关浮窗 / 退游戏 / 断电）后，下次进入修复页能看到未完成状态并可回滚。

### Story 4: 档位切回去不坏
**As a** 普通浮窗玩家
**I want to** 从 UHD 切回 HD 时 Mod 仍然能用
**So that** 我不需要记住「上次是按哪个档位修的」

**Acceptance Criteria**
- [ ] 从 UHD 切到 HD 后，再执行一次修复可回到 HD 正常状态；不做修复时不产生新的文件改动。
- [ ] 界面上能看出「当前修复结果是按哪个档位做的」（最近一次修复档位与时间）。

---

## Functional Requirements

### Core Features

**Feature 1: 资源档位选择**
- Description：修复页的 SD / HD / UHD 单选，持久化到现有 Overlay 设置。
- User flow：打开浮窗 → 修复页 → 选档位 → 执行修复。
- Edge cases：档位与游戏实际资源包不一致 → 修复可能无效，提示但不阻断（不崩溃、不改画质）。
- Error handling：设置写入失败 → 提示且回退到上次值。

**Feature 2: UHD 贴图修复开关**
- Description：仅在档位=UHD 时可用的增强开关；开启后把 UHD 处理并入现有修复流程。
- User flow：档位选 UHD → 打开开关 → 点开始修复 → 看进度 → 完成自动 F10。
- Edge cases：与「应用稳定纹理」「补全贴图状态」同时开启 → 保持现有互斥规则，并在日志说明生效组合。
- Error handling：上游 config/CLI 版本不含 UHD 能力 → 执行前拦截并给出可读原因 + 「更新配置」入口。

**Feature 3: UHD 数据接入（形态由 Phase 0 决定）**
- 候选 A：上游 CLI 新增 UHD 参数 → JASM 只传参。
- 候选 B：修后后处理，按映射表改写 ini 的贴图引用（JASM 内做）。
- 候选 C：A + B 组合（上游有就用，没有就本地覆盖）。
- 约束：无论哪种，**必须复用现有备份/回滚/进度/日志**，不得新增第二套修复入口。

### Out of Scope
- 不做 UE 反射注入 DLL（不做 ForcedLOD / mip 常驻 / 流送档位强制）。
- 不改游戏本体文件、不改画质与流送设置、不写 GameUserSettings。
- 不做资源档位的自动检测（手选 + 记住）。
- 不做 WinDivert / ReShade 相关能力。
- 不做 HD 档位下现有修复行为的任何改变。

---

## Technical Constraints

### Performance
- 单角色 ≤ 20 个 Mod：修复 ≤ 60 秒；浮窗进度刷新 ≥ 2Hz 不卡 UI。
- 不增加常驻后台进程/线程；修复期间不得让游戏帧率明显下降。

### Security
- 只写 Mod 目录与 JASM 自身目录，不写游戏安装目录。
- 所有文件改写前必须落 `.BAK`；日志不得包含用户完整路径之外的敏感信息。
- CLI 与配置文件下载沿用现有校验（版本门禁 + 大小上限）。

### Integration
- **wuwa-mod-cli 3.7.0 / config.json 3.7.1**：现有引擎；UHD 支持取决于上游，见风险。
- **3DMigoto / WWMI**：Phase 0 取证依赖 hunting dump 能力（已有配置）。
- **浮窗 F10 刷新链路 / Elevator**：修复完成后沿用现有刷新与前台归还路径。

### Technology Stack
- 现有 WinUI 3 + Core `WuwaModFixer` 服务；新增代码落在 `GIMI-ModManager.Core/Services/WuwaModFixer` 与浮窗 UI。
- 遵循仓库约定：每文件独立提交、新写/改动行零格式差异。

---

## MVP Scope & Phasing

### Phase 0: 取证（必须先做，不在产品内）

**目标已更新**：不再是"判断是哈希还是 LOD"，而是「**验证别名语义**」——
Rocket v2.9 已在 RCDATA 里带了一张 6118 行的 `UHD-to-HD` 对照表（18 个角色，见逆向文档 §10.5），
说明作者认定根因就是哈希错位。我们要确认的是：

1. 同一角色、同一组件、同一槽位在 **HD dump** 与 **UHD dump** 里绑定的纹理哈希，是否正好对应表里的两列；
2. 表里的 B/C/D 三列各自是什么语义（UHD 侧 / 共享 / 变体）；
3. 我们能否**自己从游戏 dump 生成同样的表**（不依赖第三方数据）。

- 素材：同一 Mod 在 HD 与 UHD 下各一次 3DMigoto hunting dump（`log.txt` + 绑定槽位）。
- 产出：`docs/uhd-texture-diagnosis.md`（结论 + 证据 + 建议修法 + 生成脚本）。
- **Gate**：若 HD/UHD 两套哈希的差异与表对不上（或根本不是哈希问题），停止编码，回到方案复评。

### Phase 1: MVP
- 档位选择（SD/HD/UHD）+ 持久化。
- UHD 贴图修复开关（仅 UHD 可用）+ 并入现有修复流程。
- 上游数据缺失时的拦截与可读提示。
- 复用备份/回滚/进度/日志/F10。

**MVP Definition**：在 UHD 档位下，对**所有已装 Mod 角色**执行一次修复后，贴图全部正常；HD 档位行为零回归。

### Phase 2: 增强
- 修复结果与档位的可视化（"上次按 UHD 修于 10-29 22:10"）。
- 上游支持后自动消解本地覆盖（减少我们的维护面）。
- 档位误判的辅助提示（读游戏设置只做「建议」不自动切换）。

### Future Considerations
- 若 Phase 0 结论是 LOD/流送且文件级不可修：单独立项评估运行时方案（注入 DLL），不在本 PRD 范围。

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|---|---|---|---|
| 上游 `config.json` / CLI 不含 UHD 数据 | High | High | 第三方已有现成对照表（逆向文档 §10.5）可验证"这条路走得通"，但**不直接搬运**（来源/授权）；过渡方案：用同一方法自建表并随 JASM 发版，同时向上游提 PR 归并 |
| UHD 根因不是哈希（是 LOD/流送） | Low-Medium | High | 已找到作者自标 `UHD-to-HD` 的 6118 行对照表，哈希路径有实物支撑；仍保留 Phase 0 的对照验证，若对不上则停止编码 |
| 别名表语义理解错误导致"修坏" | Medium | High | 先用单角色最小样本验证（1 个角色 × 1 个组件 × 1 个槽位），确认后逐角色放量；全部改动仍走 `.BAK` 回滚 |
| 上游 config 每周更新引入回归 | Medium | Medium | 版本门禁 + 配置校验 + 修复前后抽样比对；保留可回滚备份 |
| 用户选错档位 | Medium | Medium | 手选 + 记忆 + 文案说明 + 结果卡显示「按哪个档位修」 |
| 修完仍个别角色异常 | Medium | Medium | 结果摘要按角色列出未成功项，可单角色重试；保留完整日志 |
| 与现有五个开关叠加产生冲突 | Low-Medium | Medium | 沿用现有互斥规则，UHD 处理放在最外层（先基础修复再 UHD 收尾） |

---

## Dependencies & Blockers

**Dependencies**
- `wuwa-mod-cli` 3.7.0 / `config.json` 3.7.1（上游 Moonholder）—— UHD 数据的唯一来源。
- 3DMigoto hunting dump 环境（Phase 0 取证）。
- 现有浮窗修复链路（引擎、备份、回滚、F10）。

**Known Blockers**
- Phase 0 结论未出：UHD 修复的具体形态（新增参数 / 后处理 / 两者）无法定稿。
- 若上游无 UHD 计划：需要一次「要不要自维护数据」的决策（本 PRD 已按「跟上游走」定调，但需等价过渡方案）。

---

## Appendix

### Glossary
- **UHD / 极致资源**：鸣潮最高资源档位；HD / 流畅为低档位。
- **vb0 哈希**：组件顶点缓冲哈希，Mod 与游戏当前资产对齐的判据。
- **TextureOverride**：3DMigoto 中把贴图槽绑定到指定资源的规则。
- **LOD0**：最高细节层级；UHD 下更容易被流送策略影响。
- **.BAK**：上游修复工具生成的备份文件。

### References
- 现有实现：[`docs/overlay-mod-fixer-prd.md`](./overlay-mod-fixer-prd.md)
- 逆向参考：[`docs/rocket-injection-and-uhd-texture-analysis.md`](./rocket-injection-and-uhd-texture-analysis.md)（第三方管理器 Rocket 的注入链路与运行时贴图修复，仅供对照，本 PRD 不采用其运行时方案）
- 相关手册：`docs/wuwa-mod-hash-fix.md`

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
