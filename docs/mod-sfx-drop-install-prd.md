# Product Requirements Document: 角色概览页拖入自解压 Mod 包自动安装

**Version**: 1.0
**Date**: 2026-09-30
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

---

## Executive Summary

国内社区分发的 Mod 大量以 **WinRAR 自解压 exe** 的形式流通（示例样本：`爱弥斯-誓约（0）by 晨星.exe`，77.00 MB）。用户拿到这种包之后，必须自己走完「双击 → 选解压目录 → 输解压密码 → 在一堆文件夹里判断这属于哪个角色 → 手动移动到 GIMI/WuWa 的 `Mods\<分类>\<角色>` 下」这一长串步骤；任何一步错（放错角色、多套一层目录、忘了去 `DISABLED_` 前缀）都会表现为「装了但游戏里没反应」。

JASM 的角色概览页**已经具备拖拽装 Mod 的完整链路**（角色卡片上有 `AllowDrop` 与现成的落盘流程），缺的只是最后一环：识别 `.exe` 这种自解压包。本功能把「拖进角色概览页」补成一条端到端通路 —— **拖入 → 自动解压 → 自动认出角色 → 现有安装向导预填 → 落地**。

关键取向：**绝不执行用户拖入的 exe**。实测确认仓库内置的 7-Zip 24.09 可以直接把这种 SFX 当压缩包读取，因此整条链路是「解析」而非「运行」，既不引入执行不可信代码的风险，也不依赖各 SFX 厂商的静默参数。

---

## Problem Statement

**Current Situation**：装一个自解压包形式的 Mod，用户要手动完成 6 步（双击 exe / 选解压目录 / 输密码 / 解压 / 判断属于哪个角色 / 移动进对应目录），且必须事先知道 GIMI/WuWa 的目录约定。JASM 现有拖拽只认 `.zip` / `.rar` / `.7z`，`.exe` 会被直接判为「没有找到有效的 Mod 文件夹或压缩包」。

**Proposed Solution**：在角色概览页拖入 `.exe` 时，把它按压缩包解析（不执行）→ 命中加密则套用用户记住的单条密码 → 解压到临时目录 → 用现有的 角色名/ModFilesName 模糊匹配 认出角色 → 打开现有的安装向导并**预填角色**，由用户确认后落地。

**Business Impact**：装包步骤从 6 步降到 2 步（拖入 + 向导里点确认）；把「必须懂目录结构」变成「不需要懂」；把「装错了没反应」变成「向导里看得见、能改」。

---

## 实测技术事实

> 本节是整个方案的地基，全部为 2026-09-30 在本机对真实样本跑出来的结果，不是推测。

| 项 | 实测结果 |
|---|---|
| 样本 | `爱弥斯-誓约（0）by 晨星.exe`，80,744,878 字节 = **77.00 MB** |
| PE 存根 | x64，7 个节，映像结束于 `0x70600`（≈448 KB） |
| 载荷 | **overlay = 80,284,590 字节 = 76.57 MB，占整份文件 99.4%**，起始 8 字节 = `52 61 72 21 1A 07 01 00` → **RAR5 签名** |
| 结论 | 这是 **WinRAR 自解压包**（PE 存根 + 追加的 RAR5 载荷） |
| 能否不执行就读 | **能**。仓库内置 `Assets/7z/7z.exe`（**7-Zip 24.09**）对 SFX exe 与切出的 `payload.rar` 都能识别为 RAR5，无需先切载荷 |
| 加密 | **已加密，且是头加密**（`-hp` 级别）：`7z l` 立刻要求密码 → 没有密码**连文件列表都读不出来** |
| 角色是否已知 | **已知**。`Assets/Games/WuWa/characters.json` 中 `InternalName` / `ModFilesName` = `Aemeath`，`Keys` 含 `爱弥斯`；中文副本同 |
| 现有拖拽 | 已接好：角色卡片 `CharactersPage.xaml:492-500` → `CharactersPage.xaml.cs:88-125`；`Drop` 分派见 `:102-125` |
| 现有空壳 | 「Drop Here to Auto Detect Mod…」大区域（`CharactersPage.xaml:408-428`）的处理器 `DragAndDropArea_OnDrop`（`CharactersPage.xaml.cs:133-136`）**只打日志** |
| 现有缺口 | `.exe` 不在任何一条识别链路里（`DragAndDropScanner.cs:69-78`、`ArchiveService.cs:66-75` 只认 `.zip/.rar/.7z`） |

---

## Success Metrics

**Primary KPIs：**

- **端到端装包成功率**：用上述真实样本（含密码）从拖入到落地 `Aemeath` 目录一次成功 = 100%。验证方式：实机冒烟，人工拖拽 + 核对目标目录内容。
- **步骤数**：6 步 → **2 步**（拖入；向导内确认）。验证方式：走查交互流程并记录。
- **可执行文件执行次数 = 0**：整条链路对 `.exe` 只做解析，不存在任何 `Process.Start(用户文件)` 路径。验证方式：代码走查 + 静态检索。

**Secondary KPIs：**

- **失败可解释率 100%**：不能安装时必定给出明确原因（加密且无密码 / 不是可识别的压缩包 / 认不出角色），不出现「拖了没反应」。
- **二次装包免输入**：记住密码后，第二个同密码的加密包不再弹密码框。

**Validation**：Phase 1 完成后实机验证；样本与密码由需求方提供（见「依赖与阻塞」）。

---

## User Personas

### Primary: 中文社区玩家「小鹿」
- **Role**：鸣潮/原神玩家，从网盘或论坛帖子里下载 Mod 包，装 Mod 只为换皮肤。
- **Goals**：把下到的包用起来，越省事越好。
- **Pain Points**：拿到的是个 `.exe`，不知道要不要双击；双击后不知道该解压到哪；解开后一堆文件夹看不出属于谁；放到 `Mods` 下游戏里还是没反应（多套了一层目录 / 忘了 `DISABLED_`）。
- **Technical Level**：Novice。

### Secondary: JASM 老用户「阿哲」
- **Role**：已经会用 JASM 管理 Mod，平时用拖拽装 zip。
- **Goals**：统一入口，不要为了一个 exe 切回资源管理器。
- **Pain Points**：JASM 拖 `.exe` 直接报「没有找到有效的 Mod 文件夹或压缩包」，只能手动解压再拖文件夹。
- **Technical Level**：Intermediate。

---

## User Stories & Acceptance Criteria

### Story 1: 拖到「自动检测」区，自动认出角色
**As a** 中文社区玩家
**I want to** 把 Mod 包拖到角色概览页的检测区
**So that** 不用自己判断它属于哪个角色

**Acceptance Criteria：**
- [ ] 拖入 `.exe` 时检测区高亮，Drop 后进入处理（不再只打日志）。
- [ ] 能识别出角色时，打开安装向导且角色已预选为该角色。
- [ ] 识别依据可解释：文件名与包内目录名都能作为线索。

### Story 2: 拖到具体角色卡片，角色被钉死
**As a** JASM 老用户
**I want to** 直接把包拖到某个角色的卡片上
**So that** 不必依赖自动识别、由我指定目标

**Acceptance Criteria：**
- [ ] 拖到角色卡片时，安装向导预选该角色，且不弹候选选择框。
- [ ] 拖入的若是裸 `.rar` / `.7z`，与 `.zip` 同等处理（走同一路径）。

### Story 3: 加密包用记住的密码自动解开
**As a** 中文社区玩家
**I want to** 密码只输一次
**So that** 后面同密码的包不再打断我

**Acceptance Criteria：**
- [ ] 检测到需要密码且本地已有密码时，先静默用它尝试，成功则不弹框。
- [ ] 本地无密码或密码错误时，弹出密码输入框；输入正确后**保存/覆盖**这条密码。
- [ ] 密码错误时给出「密码不正确」的明确提示，可重试，可取消。
- [ ] **密码绝不出现在日志、异常消息或诊断串里**。

### Story 4: 认不出角色时让我选
**As a** 中文社区玩家
**I want to** 在认不出时从候选里点一个
**So that** 不至于白拖一趟

**Acceptance Criteria：**
- [ ] 识别失败时弹出候选列表，含模糊匹配的 Top-N 角色与「其他」。
- [ ] 选择后继续进入安装向导并预选该角色。
- [ ] 取消则整个流程干净退出，不留下临时文件。

### Story 5: 不是能识别的包时，说清楚为什么
**As a** 用户
**I want to** 得到一个能看懂的原因
**So that** 我知道下一步该干什么

**Acceptance Criteria：**
- [ ] 文件不是可识别的压缩包（例如是普通安装器）时，提示「这不是 JASM 能识别的 Mod 压缩包」并给出出路（手动解压后拖文件夹）。
- [ ] 包损坏 / 解压中断时给出可读错误，不静默失败。
- [ ] 任何失败路径都不留下临时目录残留。

### Story 6: 安装前能看见、能改
**As a** 用户
**I want to** 在落地前确认装到哪、装成什么样
**So that** 不会装错地方

**Acceptance Criteria：**
- [ ] 落地复用现有安装向导，展示解压出的内容与目标角色。
- [ ] 目标角色可在向导里更改。
- [ ] 同名/已存在时的处理沿用向导既有选项（覆盖 / 并存），不新造策略。

---

## Functional Requirements

### Core Features

**F1 拖拽入口**
- Description：角色概览页两处均可作为落点 —— 具体角色卡片（角色已定）与自动检测区（角色待识别）。
- User flow：拖入 → 判定文件类型 → 分派。
- Edge cases：多文件同时拖入、拖入的是文件夹（现有链路已支持）。
- Error handling：不支持的拖入内容给出既有提示。

**F2 `.exe` 识别（只解析，不执行）**
- Description：对 `.exe` 用内置 7-Zip 探测，判定其是否为可读压缩包（WinRAR SFX / 7z SFX 等）。判据基于「7z 能否识别其中含压缩包」，而非扩展名白名单。
- User flow：`.exe` → 7z 探测 → 可读则按压缩包继续。
- Edge cases：`7z l` 成功但需要密码（→ 走 F3）；`7z l` 失败（→ Story 5 的提示）。
- Error handling：**任何情况下都不执行该 exe**。

**F3 加密检测与单条密码**
- Description：检测到需要密码时，先用本地保存的**单条全局密码**尝试；失败或无密码则弹框输入，成功后保存/覆盖。
- User flow：需要密码 → 有则静默试 → 成功继续 / 失败或没有则弹框 → 输入 → 验证 → 保存。
- Edge cases：密码为空、用户取消、密码正确但仍解压失败（包损坏）。
- Error handling：区分「密码错」与「包坏了」两种原因分别提示。
- 备注：用户已明确 —— **密码是同一个、不需要按作者分别管理**，故不做多条目密码簿，也不做任何内置密码字典或自动猜测。

**F4 解压**
- Description：解压到临时目录，复用现有解压能力（优先内置 `7z.exe`，回退 SharpCompress）。
- User flow：解压 → 自动定位包内的 Mod 根（沿用现有单层展开逻辑）→ 交给识别与向导。
- Edge cases：包内多套一层目录、包内含多个角色、包很大。
- Error handling：解压失败 / 磁盘空间不足 → 明确报错并清理临时目录。

**F5 角色识别**
- Description：优先用拖入目标（拖到卡片时角色已定）；否则用文件名与解压后的目录名/内容，走现有的 `Keys` / `ModFilesName` 模糊匹配。
- User flow：候选打分 → 最高分高于阈值则直接预选 → 否则弹候选框。
- Edge cases：新角色不在 `characters.json`（→ 候选框里以「其他」兜底）；同一包跨多角色（→ 交给向导的既有处理）。
- Error handling：无任何线索 → 候选框。

**F6 落地**
- Description：复用现有安装向导 `ModInstallerPage`，预填角色；真正写盘仍走现有 `AddMod` / `AddAndReplace` 路径。
- User flow：向导确认 → 写入 `Mods\<分类>\<角色>`。
- Edge cases：目标目录已存在同名 Mod（→ 既有选项）。
- Error handling：写盘失败（权限、被占用）→ 既有提示。

### Out of Scope
- **不执行**用户拖入的任何 `.exe`（本 PRD 的硬边界）。
- 不支持 WinRAR SFX / 裸压缩包之外的 SFX 形态（NSIS / Inno / 自制 SFX）—— 见 Phase 2。
- 不做「静默直装、不问确认」模式。
- 不做按作者维护的多条密码簿、不做密码字典或自动猜测。
- 不改动 7-Zip 或 SharpCompress 的版本与解压实现。

---

## Technical Constraints

### Performance
- 解压 77 MB 级包不应阻塞 UI 线程（沿用现有 async 解压路径）；耗时操作需给进度或忙碌指示。
- 解压目标为临时目录，流程结束后必须清理。
- 拖入到「出现向导」的目标时间：本地磁盘下 < 10 秒（不含用户输入密码的时间）。

### Security
- **可执行文件零执行**：`.exe` 只作为数据被读取；不得调用 `Process.Start` 指向用户拖入的文件。
- **密码不落日志**：密码不得出现在 Serilog 输出、异常消息、诊断串或任何错误提示中；本地明文保存（用户已确认），设置页提供查看/清除能力。
- 解压路径需防目录穿越（沿用现有解压实现的行为，不新引入风险）。

### Integration
- **7-Zip**：`src/GIMI-ModManager.WinUI/Assets/7z/7z.exe`（24.09，随包分发）—— 主解压路径，已实测可读 RAR5 SFX 与头加密包。
- **SharpCompress**：现有回退路径；对 **RAR5 头加密**的支持尚未验证，实现时需用真实样本确认，若不支持则明确为「7z 优先」而非「两者皆可」。
- **现有组件复用**：`ArchiveService.ExtractArchive`、`DragAndDropScanner.ScanAndGetContents`、`ModCrawlerService.GetMatchingModdableObjects`、`SkinManagerService.GetCharacterModFolderPath`、`ModInstallerService.StartModInstallationAsync`。

### Technology Stack
- .NET 9 / WinUI 3 / CommunityToolkit.Mvvm，遵循现有 MVVM 与 DI 约定。
- **不新增 NuGet 依赖**（明文存储方案已避开 `ProtectedData`）。
- **本地化**：新增 UI 文案必须同时补 `Strings/zh-cn/Resources.resw` 与 `Strings/en-us/Resources.resw`（沿用本次汉化立下的规矩）；XAML 加 uid 时注意「文案必须是属性、不能写成元素内联内容」这个已知坑。

---

## MVP Scope & Phasing

### Phase 1: MVP
- `.exe`（WinRAR SFX / 7z SFX 一类可被 7z 识别的）拖入角色概览页
- 顺带支持裸 `.rar` / `.7z` 与 `.zip` 同等处理
- 加密检测 + **单条**密码记忆（设置页可改可清）
- 自动识别角色（卡片落点 = 已定；检测区 = 模糊匹配 → 候选框）
- 复用安装向导预填角色
- 失败路径的明确提示 + 临时目录清理
- 中英双语词条

**MVP Definition**：用真实样本（含密码）走通「拖入 → 装进 Aemeath 目录」这一条路，且认不出/加密/损坏三类失败都能给用户一句能懂的说明。

### Phase 2: Enhancements
- 更宽的 SFX 形态（NSIS / Inno / 自制 SFX）—— 先补样本再评估
- 多条目密码簿（按作者/来源），若实际使用中出现「密码不止一个」的需求
- 认角色的更多线索（包内图片资源、`.ini` 内容特征）

### Future Considerations
- 拖入即静默直装的「快速模式」（需配套撤销）
- 从 URL/市场页直接接入同一套安装链路

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|------|------------|--------|---------------------|
| 遇到非 WinRAR 的 SFX（Inno/NSIS），7z 读不出 → 用户以为功能坏了 | Med | Med | MVP 明确只承诺可被 7z 识别的一类；失败提示直接给出「手动解压后拖文件夹」的出路 |
| SharpCompress 不支持 RAR5 头加密，导致回退路径不可用 | Med | Med | 以内置 7z（已实测）为主路径；实现阶段先用真实样本验证 SharpCompress，验证不过就明确改成 7z 独占 |
| 密码明文存储被读取 | Low | Low | 用户已确认该威胁模型可接受；密码不进日志；设置页可清除；不新增依赖 |
| 大包解压期间卡 UI 或占满临时盘 | Med | Med | 沿用 async 解压 + 进度指示 + 结束即清理；失败路径同样清理 |
| 认错角色、装错目录 | Med | Med | 向导确认步骤兜底 + 候选选择框；不采用静默直装 |
| 内置 7z.exe 被杀软拦截 | Low | High | 用仓库内置副本（随包分发，非现场下载）；如被拦截则在错误信息里点明 |
| 新角色不在 `characters.json`，识别率被低估 | Med | Low | 候选框以「其他」兜底；后续可扩 `Keys` |

---

## Dependencies & Blockers

**Dependencies：**
- 内置 `Assets/7z/7z.exe` 24.09 随包分发（现状满足）。
- 现有解压/识别/安装向导链路（现状满足，本功能以复用为主）。

**Known Blockers：**
- **端到端验收需要真实样本的解压密码**。样本 `爱弥斯-誓约（0）by 晨星.exe` 是头加密包，没有密码无法验证 Story 3 与 MVP Definition。需要在验收阶段由需求方提供该密码（或由需求方在实机上手输一次）。
- 分支：`feat/sfx-drop-install`（已从 `master` 建出）。

---

## Appendix

### Glossary
- **SFX（自解压包）**：把解压存根与压缩载荷拼成一个 `.exe` 的文件；本例是 WinRAR 存根 + RAR5 载荷。
- **头加密（`-hp`）**：RAR 的一种加密级别，连文件列表都被加密，不给密码无法列出内容。
- **overlay（附加数据）**：PE 文件最后一个节之后追加的数据区；本例的 RAR5 载荷就在这里，占文件 99.4%。
- **ModFilesName / Keys**：`characters.json` 中用于「按文件名/别名识别角色」的字段。

### References
- `src/GIMI-ModManager.WinUI/Views/CharactersPage.xaml` 及其 `.xaml.cs`（现有拖拽落点）
- `src/GIMI-ModManager.Core/Services/ArchiveService.cs`、`DragAndDropScanner.cs`（解压入口）
- `src/GIMI-ModManager.Core/Services/ModCrawlerService.cs`（角色识别）
- `src/GIMI-ModManager.WinUI/Services/ModHandling/ModInstallerService.cs`（安装向导与落地）
- `docs/mod-env-setup-prd.md`（解压能力复用的既有依据）

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
