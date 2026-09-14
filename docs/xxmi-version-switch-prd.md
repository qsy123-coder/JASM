# Product Requirements Document: XXMI 版本选择安装与回退

**Version**: 1.0
**Date**: 2026-09-14
**Author**: Sarah (Product Owner)
**Quality Score**: 94/100

---

## Executive Summary

JASM 已有一套自研的「一键配置 Mod 环境」链路（`Services/ModEnv/`），它同时承担了 XXMI 注入器框架、XXMI Launcher 和游戏 Mod 包的安装与更新。但这套链路**只能装"最新版"**：远程清单 `version.json` 的 `Packages["xxmi"]` 只描述单一版本，UI 上没有任何选择版本的入口。

问题在于 XXMI 的版本升级并不总是安全的。新版注入器可能改变接口，导致用户已有的 Mod 池集体失效，甚至游戏直接崩溃或黑屏。此时用户唯一的自救手段是手工去 XXMI 官方渠道找旧版安装器重装 —— 对国内用户来说这条路还要翻墙，实际上等于无解。

本功能在现有配置对话框内增加一个「XXMI 版本」下拉框，让用户自主选择安装哪个历史版本，并在切换前自动备份当前版本的核心文件，从而可以随时切回。默认仍选中最新版，普通用户的使用路径与今天完全一致；只有主动选旧版才走回退流程。

---

## Problem Statement

**Current Situation**

1. **无版本选择能力**：`ModEnvManifestService` 拉取的 `version.json` 中 `Packages["xxmi"]` 只有一个 `Version` 字段（`Models/ModEnvSetup/ModEnvManifest.cs:17`），结构上就无法表达"历史版本"。
2. **无降级入口**：`ModEnvSetupFacade.EvaluateAction`（`Services/ModEnv/ModEnvSetupFacade.cs:755-767`）用**字符串不相等**判定 `UpdateAvailable`，没有新老版本大小比较。这意味着清单里指向旧版本也会被当成"有更新"装上 —— 但这是隐式行为，UI 完全不暴露，用户无从操作。
3. **操作不可逆**：`ModEnvInstallerService.CopyToTargetAsync`（`:342-357`）落到目标时 `overwrite: true`，直接原地覆盖，没有旧文件备份、没有快照、没有 `.old` 目录。`docs/mod-env-setup-prd.md:154` 还明确写着"任一步失败不自动回滚（断点续传模式）"。
4. **用户自救成本极高**：XXMI 官方安装器是**联网引导器**，首次运行要从境外下载资源（这正是 `docs/mod-env-cdn-setup.md:56-58` 记录的国内用户困境）；且用户难以判断该退到哪个版本。

**Proposed Solution**

在 `ModEnvSetupDialog` 内新增「XXMI 版本」下拉，数据来自新的 CDN 清单 `xxmi-versions.json`。用户选定版本后，安装流程复用现有下载/校验/解压/落盘链路（仅替换 XXMI 基础包的来源）。切换前把当前已装的 3 个核心 dll 备份到 JASM 数据目录，UI 显示当前版本，用户可随时切回。

**Business Impact**

- 把"升级后 Mod 坏了"从**不可自救的死局**变成**对话框里点两下就能恢复**。
- 降低维护者被追问的负担：用户不必再来问"我该怎么办"。
- 提升 JASM 作为「Mod 环境管家」的定位完整性 —— 既有安装、更新、修复，现在补上回退。

---

## Success Metrics

**Primary KPIs:**

| KPI | 目标值 | 测量方式 |
|---|---|---|
| 回退后 Mod 恢复可用 | 回退到目标版本后，用户原本失效的 Mod 恢复加载、游戏能正常进入 | 用户反馈 + 手测清单（`docs/mod-env-hand-test.md` 扩展版本切换用例） |
| "升级后 Mod 坏了"求助量下降 | 该主题的 GitHub issue / 群内求助显著减少 | 人工统计 issue 标题与群聊关键词 |
| 回退操作成功率 | 下载 → SHA256 校验 → 解压 → 落盘全链路无报错 | JASM 已有 Serilog 日志；统计失败告警 |

**Validation**: 功能发布后观察一个版本周期（约 2 周）的用户反馈与日志；用 `docs/mod-env-hand-test.md` 的版本切换用例做发布前手测。

---

## User Personas

### Primary: 鸣潮/原神 Mod 用户（受影响者）
- **Role**: 使用 JASM 管理 Mod 的普通玩家
- **Goals**: 让游戏带着 Mod 正常跑起来，不被工具链的版本问题挡住
- **Pain Points**: 某次点了"更新"之后游戏黑屏 / Mod 报错 / 进不去；不知道是新版 XXMI 的锅；知道了也不知道怎么退回去
- **Technical Level**: 中等 —— 会跟着教程装 Mod，但不会自己去找旧版安装器、也不想去翻墙

### Secondary: JASM 维护者（本仓库作者）
- **Role**: 打包版本包、维护 CDN 清单、接收用户反馈
- **Goals**: 用一个自己可控的渠道给用户发放可用版本；在某个新版出问题时能快速引导用户回退
- **Pain Points**: 目前只能靠"重新打包 + 让用户重下整个环境"来救场；无法针对性地只换掉出问题的那一层
- **Technical Level**: 高级

---

## User Stories & Acceptance Criteria

### Story 1: 选择版本安装

**As a** 第一次配置环境的用户
**I want to** 在配置 Mod 环境时选择要安装的 XXMI 版本
**So that** 我可以直接装一个已知可用的版本，而不是被动接受最新版

**Acceptance Criteria:**
- [ ] `ModEnvSetupDialog` 中出现「XXMI 版本」下拉框，默认选中**最新版**
- [ ] 下拉项来自 CDN 的 `xxmi-versions.json`，按版本号**降序**排列，每项显示版本号
- [ ] 不碰下拉框直接走完流程时，安装结果与改动前**完全一致**（零行为变更）
- [ ] 选定非最新版后，下载的是该版本对应的 zip（而非 `version.json` 里的 xxmi 版本）
- [ ] CDN 清单拉取失败时，下拉**降级为仅「最新版」一项**并继续可用，不阻断原有安装流程

### Story 2: 查看当前已装版本

**As a** 已经装好环境的用户
**I want to** 看到我当前装的是哪个 XXMI 版本
**So that** 我能判断自己需不需要回退

**Acceptance Criteria:**
- [ ] 对话框打开时，从 `<XXMI 根目录>\.modenv.json` 读出 `InstalledVersions["xxmi"]` 并显示为「当前版本：vX.Y.Z」
- [ ] 当前版本与下拉选中项一致时，明确提示「已安装该版本，无需重复安装」
- [ ] `.modenv.json` 缺失或损坏时不报错，显示「未检测到已安装版本」
- [ ] 若已装版本**不在** catalog 列表中（例如已下线），仍如实显示，不隐藏

### Story 3: 回退到历史版本

**As a** 升级后 Mod 失效的用户
**I want to** 把 XXMI 切回之前能用的版本
**So that** 我的 Mod 能重新生效、游戏能正常进

**Acceptance Criteria:**
- [ ] 切换前把当前 `<XXMI 根目录>` 下的 `d3d11.dll`、`3dmloader.dll`、`d3dcompiler_47.dll` 备份到 JASM 数据目录
- [ ] 备份失败时**中止切换**并明确报错，不得在不留后路的情况下覆盖
- [ ] 回退只替换上述 3 个核心 dll；`Launcher`、`WWMI\` 子目录与用户 Mod **保持不动**（见 Out of Scope）
- [ ] 回退完成后 `.modenv.json` 的 `InstalledVersions["xxmi"]` 更新为目标版本
- [ ] 回退后 root 下的 3 个 dll 存在且版本正确（复用现有 `BaseFilesOk` 判定）
- [ ] 游戏或 XXMI Launcher 正在运行时给出提示（复用现有强杀 Launcher 进程的逻辑）
- [ ] 全程进度与失败原因在对话框内可见，支持取消

### Story 4: 从备份切回

**As a** 刚回退完却发现新版其实更好的用户
**I want to** 切回我之前的版本
**So that** 我不用再下载一遍、也不用担心回不去

**Acceptance Criteria:**
- [ ] 备份档在 JASM 数据目录下有明确位置与命名（含版本号与时间戳）
- [ ] 备份档出现在版本下拉/管理 UI 中，可一键恢复
- [ ] 备份数量有上限（保留最近 N 份），超出时按时间淘汰最旧的
- [ ] 磁盘空间不足时给出可操作的提示

---

## Functional Requirements

### Core Features

**Feature 1: 版本清单（catalog）拉取与解析**

- Description: 新增 CDN 文件 `xxmi-versions.json`，与现有 `version.json` **并存、职责分离**：catalog 只列可选历史版本，`version.json` 继续负责"最新版"与 launcher/wwmi。
- 建议结构（字段命名对齐现有 `ModEnvPackage`）：
  ```json
  {
    "CatalogVersion": 1,
    "Versions": [
      {
        "Version": "1.1.7",
        "DownloadUrl": "https://<bucket>/modenv/xxmi-1.1.7.zip",
        "Sha256": "<小写十六进制>",
        "SizeBytes": 3200000,
        "ReleasedAt": "2026-09-14",
        "Notes": "可选，一句话说明"
      }
    ]
  }
  ```
- User flow: 对话框打开 → 并发拉 catalog 与 `version.json` → 合并出下拉项
- Edge cases: catalog 404 / 超时 / JSON 格式错误 → 降级为仅「最新版」；同一版本号重复 → 去重取第一条
- Error handling: 全部非致命，仅记 warning 并在 UI 上静默降级，不阻断原有安装流程

**Feature 2: 版本下拉与选择**

- Description: `ModEnvSetupDialog` 增加下拉，默认选中最新版。
- User flow: 用户展开下拉 → 选版本 → 点安装 → 走现有 `SetupAsync`
- Edge cases: 选中项 == 当前已装版本 → 提示并跳过 XXMI 包，仅执行后续 launcher/wwmi 检查
- Error handling: 选中版本在 catalog 中失效（切换瞬间被下线）→ 回落最新版并提示

**Feature 3: 切换前备份**

- Description: 覆盖 root 下 3 个 dll 前，先复制到 JASM 数据目录。
- 备份位置建议：`%LOCALAPPDATA%\JASM\ModEnvBackups\xxmi-<version>-<yyyyMMddHHmmss>\`
- User flow: 检测到目标版本 ≠ 当前版本 → 备份 → 下载 → 校验 → 解压 → 覆盖 → 更新 marker
- Edge cases: 备份目录不可写 / 磁盘满 → 中止并报错；当前版本未知（无 marker 但文件存在）→ 以 `unknown` 为版本号仍备份原文件
- Error handling: 备份失败一律中止本次切换，保留现场

**Feature 4: 从备份恢复**

- Description: 列出备份档并允许一键恢复其中的 3 个 dll。
- Edge cases: 备份档文件缺失/损坏 → 标注为不可用；目标目录被占用 → 走现有 `ElevatorService` 提权拷贝
- Error handling: 恢复同样先备份当前状态（避免"恢复"本身把用户卡死）

### Out of Scope（本期不做）

- **不管理 EFMI / WWMI / Launcher 的版本**：四个本地版本包里，EFMI/WWMI 只有签名清单没有实际内容，Launcher 的 msi 四版完全相同 —— 版本差异**只存在于 XXMI 核心 3 个 dll**。本期严格只切换这 3 个文件。
- 不做版本自动降级 / 智能推荐（如"检测到崩溃自动回退"）
- 不做切换后自动试启动验证
- 不做多版本并行安装（同一时刻只有一份生效）
- 不做任意本地 zip 导入
- 不处理`ModEnvSetupRequest.CustomRootFolder`（该字段目前 UI 未暴露）

---

## Technical Constraints

### Performance
- catalog 拉取单独设短超时（建议 ≤10s），失败立即降级，不拖慢对话框打开
- 版本包体积约 3MB/个（3 个 dll），远小于现有 wwmi/launcher 包，下载体验无压力
- 复用现有断点续传 + SHA256 校验 + 停滞超时（`DownloadStallTimeoutSeconds` 默认 30s）

### Security
- **⚠️ 硬性要求：绝不把 `XXMI v*更新包/Security/private_key.der` 打包进任何 `xxmi-*.zip`。** 这是 XXMI 的签名私钥，`docs/mod-env-cdn-setup.md:66` 已有同样警告。打包脚本必须显式白名单只取 `Packages/XXMI/` 下的 3 个 dll。
- `Sha256` 必须小写十六进制，复用现有严格比对逻辑
- 备份目录位于用户 `%LOCALAPPDATA%`，不含敏感信息

### Integration
- **复用现有链路**，不新建并行安装路径：`ModEnvSetupFacade` / `ModEnvInstallerService` / `ArchiveService` / `ElevatorService`
- mark 文件继续用 `<root>\.modenv.json`（`ModEnvInstallerService.MarkerFileName:32`），`InstalledVersions["xxmi"]` 语义不变
- 新增配置项放 `appsettings.json` 的 `ModEnv` 段（现有段见 `appsettings.json:9-16`）

### Technology Stack
- .NET 9 + WinUI 3 / Windows App SDK 1.7，MVVM 走 CommunityToolkit 源生成器
- 日志走 Serilog；新增字段用 `[ObservableProperty]`，命令用 `[RelayCommand]`

### ⚠️ 已知实现坑（必须在开发前处理）

1. **清单缓存**：`ModEnvManifestService` 的 manifest **在 App 生命周期内只缓存一次**（`ModEnvManifestService.cs:25` 的 `_cached`），且 `ClearCache()`（`:69`）**当前无任何调用方**。新增 catalog 后若沿用同样模式，用户"点更新 → 换版本 → 再打开对话框"会拿到陈旧数据。必须显式定义缓存失效策略（建议：catalog 沿用短 TTL 或每次打开对话框强制刷新）。
2. **两个来源的版本号可能不一致**：catalog 的"最新版"与 `version.json` 的 `Packages["xxmi"].Version` 是两份数据。需明确以谁为准（建议：下拉默认项以 `version.json` 为准以保持行为一致，并在 catalog 不包含该版本时把它**合成**为下拉首项）。
3. **`EvaluateAction` 的字符串比较语义**：`:764-766` 是 `!=` 即"可更新"。引入版本选择后，这里要能表达"用户主动选择到旧版本"与"检测到新版本"两种情况，否则 UI 文案会把回退误报成"更新"。

---

## MVP Scope & Phasing

### Phase 1: MVP（首发必须）
1. CDN `xxmi-versions.json` catalog 拉取 + 降级容错
2. `ModEnvSetupDialog` 内「XXMI 版本」下拉，默认最新版
3. 显示当前已装版本（读 `.modenv.json`）
4. 选定版本安装（只替换 3 个 dll）
5. 切换前自动备份 + 备份档可用于切回（Story 3/4）

**MVP Definition**: 用户能选一个历史版本装上，出问题能切回来。这条闭环打通即可交付。

### Phase 2: 增强（后续）
- 版本说明文案 / 「推荐版本」标记（catalog 里加 `Recommended: true`）
- 备份档管理 UI（列表、删除、占用空间显示）
- 不兼容版本的非阻断警告（复用现有 `IsCompatible`，`ModEnvSetupFacade.cs:769-776`）

### Future Considerations
- 切换后自动试启动验证 + 失败自动回滚
- 接入 XXMI 官方 release 自动同步 catalog
- Launcher / 游戏包也纳入版本管理

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|---|---|---|---|
| 打包时误把 `Security/private_key.der` 发出去 | 中 | **高**（泄密） | 打包脚本用白名单只取 3 个 dll；发布前 `unzip -l` 抽查包内容；写入 `docs/mod-env-cdn-setup.md` |
| 旧版 XXMI dll 与当前 wwmi 游戏包不兼容 | 中 | 高（回退后仍不可用，功能失去意义） | 发布前**逐一实测** 4 个版本 × 当前 wwmi 包的组合；手测清单补上 4 个组合用例；catalog 里用 `Notes` 标注可用性 |
| 清单缓存导致换了版本不生效 | 高 | 中 | 见「已知实现坑」第 1 条，MVP 必须显式处理 |
| catalog 与 version.json 版本号不一致 | 中 | 中 | 见「已知实现坑」第 2 条；建议加一个启动期一致性校验日志 |
| 备份占磁盘空间 | 低 | 低 | 保留最近 N 份 + 时间淘汰；恢复后提示可清理 |
| 「回退」被误报成「更新」，用户看不懂 | 中 | 中 | 见「已知实现坑」第 3 条；UI 文案区分"安装 vX"与"更新到 vX" |

---

## Dependencies & Blockers

**Dependencies:**

| 依赖项 | 说明 | Owner |
|---|---|---|
| `xxmi-<ver>.zip` × 4 | 从本地 4 个版本包的 `Packages/XXMI/` 各打一个 zip 上传腾讯云 COS `modenv/`。**内容是 3 个 dll，不含 Manifest.json，不含 Security/** | 维护者 |
| `xxmi-versions.json` | 新建 catalog，写入 4 个版本的 Version / DownloadUrl / Sha256 / SizeBytes | 维护者 |
| COS 桶 `jasm-modenv-1327973389` | 已存在（见 `appsettings.json:10`），需确认可上传 | 维护者 |
| SHA256 生成 | PowerShell `Get-FileHash`，见 `docs/mod-env-cdn-setup.md:127-132` | 维护者 |

**本地版本包映射（打包依据）**

| 版本包目录 | 包内 `Packages/XXMI/Manifest.json` 的 version | 需打包的 3 个 dll |
|---|---|---|
| `XXMI v0.9.2更新包` | 0.9.2 | `3dmloader.dll` / `d3d11.dll` / `d3dcompiler_47.dll` |
| `XXMI v1.0.5更新包` | 1.0.5 | 同上 |
| `XXMI v1.1.6更新包` | 1.1.6 | 同上 |
| `XXMI v1.1.7更新包` | 1.1.7 | 同上 |

> 四包的 `Packages/EFMI`、`Packages/WWMI` 只有 `Manifest.json`（无实际内容）；`Packages/Launcher/TMP/XXMI-Launcher-Installer-Online-v2.2.0.msi` 四包**完全相同**（93MB）。这些都不进 catalog。

**Known Blockers:**
- 无阻塞性技术债；4 个版本的兼容性实测是唯一的实质性前置工作。

---

## Appendix

### Glossary
- **XXMI**: Xxmi Mod Injector，跨游戏 Mod 注入器框架。本功能管理的对象
- **XXMI 核心 3 个 dll**: `d3d11.dll`（D3D11 钩子）、`3dmloader.dll`（加载器）、`d3dcompiler_47.dll`。**这是四个版本包之间唯一的实质差异**
- **catalog**: 本 PRD 引入的新 CDN 清单 `xxmi-versions.json`，列可选历史版本
- **WWMi**: 鸣潮（Wuthering Waves）Mod 包，装在 `<XXMI 根目录>\WWMI`

### References
- 现有实现 PRD：`docs/mod-env-setup-prd.md`
- CDN 搭建与打包规范：`docs/mod-env-cdn-setup.md`
- 手测清单：`docs/mod-env-hand-test.md`
- 版本包来源：`D:\BaiduNetdiskDownload\MC-MOD整合包\XXMI更新包（持续更新）\`

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
