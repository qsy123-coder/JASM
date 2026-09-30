# Product Requirements Document: Mod 商店 (Mod Store)

**Version**: 1.0
**Date**: 2026-09-30
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

---

## Executive Summary

JASM 目前的 Mod 来源分为两条：一条是「Mod 市场」（Supabase `mods` 表 + COS 快照 + 网盘直链），另一条是用户从 GameBanana 手动拿链接、靠 `GbModPageWindow` 打开单个 mod 页。后者意味着：发现内容要离开 JASM、下载后还要手动解压、再回到 JASM 点「添加模组」选文件夹，最后才进安装向导 —— 一条链路上有 4 个需要用户做判断的手动断点。

本功能新增一个**独立的「Mod 商店」页面**：直接消费 GameBanana 公开 API，首版只服务**鸣潮（Wuthering Waves）**，用户在商店里浏览/搜索/筛选，点「一键部署」后下载完成即自动弹出安装向导，全程不需要手动选文件、不需要离开 JASM。

「独立」是本功能的核心约束：商店**不读也不写 Supabase**，与 Mod 市场的取数、缓存、UI 状态完全隔离 —— 市场那条链路的任何资产（含国内网盘分发）都不受影响，商店也不会因为 GameBanana 不稳而拖累市场。

---

## Problem Statement

**Current Situation**

1. 用户在 JASM 里想找鸣潮 mod，JASM 给不出目录，只能自己去 GameBanana 网站翻。
2. 拿到 mod 页链接后，回到 JASM 走 `GbModPageWindow`（`ModDragAndDropService.AddModFromUrlAsync`）下载 —— 这条下载路径**没有断点续传、没有校验**，进度是 200ms 轮询文件大小估算出来的。
3. 下载完还要手动解压、再去角色详情页点「添加模组」、用系统文件选择器挑文件夹、进安装向导。
4. 装过什么、有没有新版，JASM 完全不记得。

**Proposed Solution**

在左侧导航新增「Mod 商店」页，布局复用 Mod 市场（左侧分类栏 + 卡片列表 + 右侧详情抽屉），数据源换成 GameBanana `apiv11`，只显示鸣潮板块。卡片上直接给「一键部署」，下载完成后**零中间确认**弹出安装向导。已装/可更新状态由一份本地索引文件判定，不引入任何服务端。

**Business Impact**

- 把「发现 → 安装」从约 7 步（站外搜索、下载、解压、进角色页、点添加、选文件夹、安装向导）压到 2 步（点部署、在向导里确认）。
- 让 JASM 对鸣潮用户有独立于 Mod 市场的第二个内容入口，且**不产生任何托管成本**（内容与带宽都由 GameBanana 承担，这与「不弄云存储 mod」的既定方向一致）。
- 顺带修掉现有 GameBanana 下载路径的两处已知短板（无续传、无校验）。

---

## Success Metrics

**Primary KPIs**

| 指标 | 目标 | 测量方式 |
|---|---|---|
| 一键部署链路成功率 | ≥ 90%（不含用户主动取消） | 本地日志（Serilog）统计「开始下载 → 安装向导成功弹出」的比例 |
| 手动选文件次数 | 0 | 走查：从商店点部署到安装向导出现，不出现系统文件选择器 |
| 下载可续传率 | 断流后自动续传成功 ≥ 95% | 本地日志统计 `.part` 续传重试成功率 |
| 安装记录准确率 | 已装/可更新判定误报 ≤ 1% | 手测清单：装完删目录、改名、重装、换版本四种子场景 |

**Validation**

JASM **没有遥测**（不采集用户数据），以上指标以「本机日志 + 手测清单」验证，发布后靠 GitHub issue 与用户群反馈做定性回收。不引入任何上报机制 —— 这是隐私边界，不是偷懒。

---

## User Personas

### Primary: 鸣潮玩家「小白」
- **Role**: 装了 WWMI 框架、会用 JASM 但不懂 mod 目录结构
- **Goals**: 想给自己的角色换个皮肤，越少步骤越好
- **Pain Points**: 不理解为啥下载完还要自己解压、不知道文件夹该丢哪、分不清 mod 有多个文件时该下哪个
- **Technical Level**: 初级

### Secondary: 进阶用户「整合者」
- **Role**: 一次装十几个 mod，会自己挑文件变体
- **Goals**: 想快速浏览/筛选大量 mod、精确选择要哪个文件
- **Pain Points**: 需要一个能按角色筛选、能看下载量/点赞来避坑的列表
- **Technical Level**: 中级

---

## User Stories & Acceptance Criteria

### Story 1: 浏览与筛选鸣潮 mod

**As a** 鸣潮玩家
**I want to** 在 JASM 里直接浏览鸣潮的 GameBanana mod 列表，按角色筛选、按最新或热度排序
**So that** 我不用离开 JASM 去网站上翻

**Acceptance Criteria:**
- [ ] 左侧导航出现「Mod 商店」项，点击进入商店页，布局与 Mod 市场一致（左筛选栏 + 卡片列表 + 右详情）
- [ ] 列表默认按 `_sSort=default` 拉取鸣潮板块（GameBanana game id **20357**）内容，只展示 `_sModelName == "Mod"` 的记录
- [ ] 支持滚动加载更多，分页依据 `_aMetadata._bIsComplete`
- [ ] 排序可选：默认（`_sSort=default`）、最新（`_sSort=new`）、最近更新（`_sSort=updated`）
- [ ] 左侧栏分三节：`全部` + 板块根分类（`Skins` / `Other·Misc` / `UI`，带服务端给的条目数）+ 角色表（带每角色计数）
- [ ] 点根分类走**服务端**筛选（`Mod/Index` 的 `_aFilters[Generic_Category]`，见下节实测表）；点角色走搜索端点按角色名查（上游**没有**「列出板块子分类」的端点）
- [ ] 排序下拉只在「全部」视图可用（分类端点拒绝 `_sSort`、搜索端点也不吃它），其余情形置灰
- [ ] 卡片信息：预览图、标题、作者、相对时间、三项统计（浏览 / 点赞 / **评论**）、根分类、角色标签（无角色时不占位）、NSFW 角标
- [ ] 搜索走 `Util/Search/Results`，结果按 `_sModelName` 过滤掉 Concept/Poll 等非 Mod 类型
- [ ] 商店**不发出任何 Supabase 请求**（可在日志中断言）

### Story 2: 一键部署（核心）

**As a** 鸣潮玩家
**I want to** 点一下「一键部署」，下载完就自动进入安装
**So that** 我不用解压、不用自己挑文件夹

**Acceptance Criteria:**
- [ ] 卡片/详情页有「一键部署」按钮，点击后开始下载并显示进度
- [ ] 详情页列出该 mod 的**文件列表**（名字/大小/日期），用户点哪个文件就装哪个
- [ ] 下载完成后**零中间确认**直接弹出安装向导（`ModInstallerPage`），不出现系统文件选择器
- [ ] 安装向导行为与「添加模组 → 选中同一份文件夹」完全一致（复用 `ModInstallerService.StartModInstallationAsync`）
- [ ] 下载前按 `Md5Checksum` 查本地归档缓存，命中则跳过下载直接进安装向导
- [ ] 下载后校验 MD5，不匹配则报错并允许重试，不进入安装向导
- [ ] 弹窗出现时若用户正在拖动/交互其他窗口，不阻塞主窗口

### Story 3: 下载管理器

**As a** 进阶用户
**I want to** 一次点多个 mod，让它们在后台排队下载，能暂停/继续、能看进度
**So that** 我不用等一个装完再点下一个

**Acceptance Criteria:**
- [ ] 下载任务串行排队（一次一个活动任务），列表/抽屉可见队列与每个任务的进度
- [ ] 支持暂停/继续；继续时走 HTTP `Range` 从 `.part` 断点续传
- [ ] 无数据超时（stall）自动重试，重试有指数退避
- [ ] 支持取消；取消后清理 `.part` 与临时目录
- [ ] 应用重启后未完成的下载不自动恢复（明确不做断点跨进程续传），但 `.part` 文件保留

### Story 4: 已装检测与更新提示

**As a** 鸣潮玩家
**I want to** 一眼看出哪些我已经装过、哪些有新版
**So that** 我不会重复装，也不会错过更新

**Acceptance Criteria:**
- [ ] 装完写一条本地安装记录（mod id、file id、md5、版本、时间、安装位置）
- [ ] 商店卡片对已装 mod 打「已安装」角标；有新版时打「可更新」并可一键更新
- [ ] 更新判定依据：该 mod 最新 file 的 id/md5 与本地索引不一致
- [ ] 用户手动删掉 mod 目录后，「已安装」角标**不应**残留（索引 + 目录存在性双重判定）
- [ ] 判定不依赖任何服务端存储

### Story 5: 成人内容控制

**As a** 用户
**I want to** 默认不看到成人内容，需要时自己在设置里打开
**So that** 商店在公共场合/共享电脑上不会尴尬

**Acceptance Criteria:**
- [ ] 设置页新增一个开关（默认「隐藏成人内容」），存入游戏级本地设置
- [ ] 开关关闭时，列表客户端过滤掉 `_bHasContentRatings == true` 的记录
- [ ] 详情页对成人内容给出明确标识
- [ ] 关闭过滤后无需重启即刻生效

### Story 6: 跨游戏状态下仍能装到鸣潮

**As a** 用户（当前 JASM 选中的游戏不是鸣潮）
**I want to** 从商店装 mod 时它仍然装到鸣潮的目录
**So that** 我不用为了装个 mod 去切换游戏再重启

**Acceptance Criteria:**
- [ ] 商店自行定位鸣潮的 Mods 目录（读 WuWa 游戏级配置），不依赖当前选中游戏
- [ ] 若鸣潮从未配置过（无 Mods 目录记录），一键部署按钮禁用并给出明确提示，指引用户先在设置里选一次鸣潮
- [ ] 商店页面在任何选中游戏下都显示鸣潮内容（固定只服务鸣潮）

---

## Functional Requirements

### Core Features

**Feature 1: 商店页面与浏览**

- **Description**: 复用 Mod 市场布局的新页面，数据源为 GameBanana `apiv11`。
- **User flow**: 点导航「Mod 商店」→ 左侧栏建好（`全部` + 根分类 + 角色，计数后台补）→ 默认列表加载 → 滚动加载更多 → 用搜索框 / 内容筛选 / 排序筛选 → 点卡片开详情抽屉。
- **布局**（2026-09-30 与产品确认的版式）：左侧栏标题「分类」= `全部` + `Skins` / `Other·Misc` / `UI`（带条目数）+ 角色 A–Z（带条目数）；工具栏 = 搜索框 + 内容筛选下拉 + 排序下拉 + 下载管理按钮；卡片 250×340。
- **与原版式的差异**（有意为之，不是漏做）：
  - 版式上的第三个下拉「仅Mods」在商店里**没有可筛的东西** —— `Mod/Index` 与 Subfeed 的记录本来就全是 Mod，留着是个点了没反应的空控件。改成「内容筛选（隐藏 / 显示 NSFW）」，服务层 `IncludeAdultContent` 已有。
  - 卡片上多了一块**角色标签**（版式里没有）：角色是商店的核心维度，根分类只有三个，光看分类分不出角色。
- **Edge cases**:
  - GameBanana 不可达 / 返回非 JSON → 显示错误态与「重试」，**不**回退到 Mod 市场数据（数据独立的硬要求）。
  - 搜索结果里混入 Concept/Poll 等非 Mod 类型 → 按 `_sModelName` 过滤。
  - 列表返回的 `_aTags` 实测为空 → **不可**用标签做筛选维度。
  - 本地角色数据读不出来（`characters.json` 缺失）→ 侧栏退化成「只有分类」，页面照常能看内容。
  - 侧栏计数取不到 → 那一格**不显示数字**（不是显示 0）；计数是尽力而为的后台补齐，失败不阻塞页面。
- **Error handling**: 所有解析失败退化为空列表 + 可重试的错误态，不抛异常到 UI 线程。

**Feature 2: 详情与文件选择**

- **Description**: 详情走 `Mod/{id}/ProfilePage`，展示截图、作者、说明、分类、点赞/浏览/下载数、版本、更新时间；文件列表用于选择安装哪个文件。
- **User flow**: 点卡片 → 右侧抽屉展开 → 看图文说明 → 在文件列表点某个文件的「部署」。
- **Edge cases**:
  - 一个 mod 多个 file（主文件/变体/不同角色版本）→ 由用户显式选择，**不**自动挑。
  - `_bHasFiles == false` → 禁用部署按钮并说明该 mod 无可下载文件。
  - `_bIsObsolete == true` → 卡片上标注「已过时」。
- **Error handling**: 详情拉取失败时抽屉显示错误态，列表卡片仍可用。

**Feature 3: 一键部署与安装衔接**

- **Description**: 下载完成后自动打开安装向导，复用既有的 `ModInstallerService` 链路。
- **User flow**: 点部署 → 进度 → 下载完成 → 自动弹出 `ModInstallerPage` 独立窗口 → 用户在向导里确认 → 写安装记录。
- **Edge cases**:
  - 下载过程中用户关掉商店页 → 下载继续（队列在服务层，不随页面销毁）。
  - 安装向导被用户直接关闭 → 不写安装记录，包保留在归档缓存里。
  - 同名 mod 已存在 → 交给安装向导原有的重名处理逻辑，商店不额外干预。
- **Error handling**: 下载失败/校验失败 → 卡片显示失败态 + 「重试」，日志记录原因。

**Feature 4: 下载器**

- **Description**: 抽出一份公共下载件（进度 + 断点续传 + 哈希校验），**本期只给商店用**。
- **Design note**: 项目内已有两份刻意重复的实现（`AppUpdateDownloader`、`ModEnvInstallerService.DownloadWithResumeAsync`），`AppUpdateDownloader.cs` 的类注释写明「出现第三个调用方时应抽成共享 helper」—— 商店正是第三个调用方。本期抽公共件但**不迁移**那两条已实机验证的链路，降低回归风险。
- **Edge cases**: 服务器忽略 `Range` 返回 200 → 从头重下；`416` → 丢弃 `.part` 重来；服务器无 `Content-Length` → 退回按已知大小估算进度。
- **Error handling**: 活动超时（无数据）判失败并自动续传重试；重试耗尽后暴露失败原因。

**Feature 5: 本地安装索引**

- **Description**: 一份本地 JSON，记录从商店装过的 mod，用于已装角标与更新判定。
- **Edge cases**: 记录存在但目录已被删 → 判定为未安装（角标不显示）；用户手动重命名目录 → 视为已装但提示位置异常（仅日志）。
- **Error handling**: 索引文件损坏 → 视为空索引并重建，不影响商店浏览与安装。

### Out of Scope

- **启动游戏 / 游戏内刷新**：这两个功能是原神专属（`GenshinProcessManager`、`ElevatorService.RefreshGengshinMods`），鸣潮下本就没有，商店不负责补齐。
- **非鸣潮游戏**：商店固定只服务鸣潮，不做游戏切换器。
- **商店内卸载 / 回滚**：本期只做安装与更新，不做卸载。
- **评论 / 评分 / 投稿 / 登录**：GameBanana 的社交功能一律不做。
- **把另两条下载链路迁到公共件**：下期单独做。
- **网盘分发通道**：商店不做网盘链接、不做手动导入入口（那是 Mod 市场的形态）。

---

## Technical Constraints

### 已验证的 GameBanana API 事实（2026-09-30 实测）

所有结论均从本机直连 GameBanana 实测得出（**无需代理**，`apiv11` 返回 200）。这些是本次 PRD 里最承重的部分 —— 实现前**不要**凭直觉改参数名。

| 能力 | 端点 | 实测结论 |
|---|---|---|
| 列表 | `GET /apiv11/Game/20357/Subfeed?_nPage=1&_csvModelInclusions=Mod&_sSort=<v>` | 可用。`_aMetadata` 给 `_nRecordCount`、`_nPerpage`、`_bIsComplete`。**分页参数是 `_nPage`，页大小固定 15** —— `perPage` / `_nPerpage` / `_nPerPage` 全部被忽略（要 3 条照样给 15 条）。`_csvModelInclusions=Mod` 是**服务端**过滤，实测有效（6090 条提交 → 3062 个 Mod） |
| 列表总数 | `_aMetadata._nRecordCount` | ⚠️ **随排序视图变**：`default` / `new` 报 3062，`updated` 只报 1333（「最近更新」视图只覆盖有更新记录的提交）。它是「当前视图的记录数」，UI 上**不能**写成「板块共 N 个 mod」 |
| 排序 | 同上 `_sSort` | **参数名是 `_sSort`，不是 `sort`** —— `sort=new/likes/...` 全部被静默忽略（返回结果与默认完全一致）。已确认可用值：`default`（默认/热度）、`new`（实测首条为当天）、`updated`（实测首条为最近更新日）。`_sSort=newest` 返回 **400**，说明该参数被真正解析。深翻页有效（实测 `updated` 下 `_nPage=80` 仍正常返回 15 条），**不需要**做「只能看前 N 页」的限制。**点赞/下载量排序的取值需在 Phase 1 枚举确认** |
| 搜索 | `GET /apiv11/Util/Search/Results?_sSearchString=<q>&_idGameRow=20357&_nPage=<n>` | 可用。**返回混合类型提交**（实测一页 6 条里混着 Request / Question / Mod），必须逐条过滤 `_sModelName`（该端点上没有可用的服务端类型过滤参数）。实测关键词 `skin`：全类型命中 705、其中 Mod 277 —— `_nRecordCount` 是**全类型**总数，要取 Mod 数得读 `_aMetadata._aSectionMatchCounts` 里的 `Mod` 项。分页同样是 `_nPage`、页大小 15 |
| 详情 | `GET /apiv11/Mod/{id}/ProfilePage` | 可用。含 `_aCategory._sName`、`_aFiles`、`_nDownloadCount`、`_nLikeCount`、`_nViewCount`、`_sVersion`、`_sText`、`_aSubmitter`、`_aPreviewMedia`、`_bHasUpdates`、`_nUpdatesCount` |
| 文件列表 | `GET /apiv11/Mod/{id}/DownloadPage` | 项目内已有调用（`IApiGameBananaClient.GetModFilesInfoAsync`） |
| 下载 | `GET /gamebanana.com/dl/{fileId}` | 项目内已有（`DownloadModAsync`）。`ApiModFileInfo` 已带 `Md5Checksum` |

**分类筛选与计数（同日第二轮实测）** —— 侧栏「分类」那一节与卡片上的第三项统计就靠这几条：

| 能力 | 端点 | 实测结论 |
|---|---|---|
| 按分类筛 | `GET /apiv11/Mod/Index?_nPage=1&_nPerpage=30&_aFilters[Generic_Game]=20357&_aFilters[Generic_Category]=29524` | ✅ **唯一可行的服务端分类筛选**。Subfeed 上**任何**分类参数都被忽略。`_aFilters` 下只有 `Generic_Game` / `Generic_Category` / `Generic_Submitter` 三个键存在（`Generic_Name` / `Generic_Text` 等一律 400） |
| 分类端点分页 | 同上 `_nPerpage` | ✅ 这个端点**认 `_nPerpage`**（1 / 5 / 30 / 50 均可用，**100 → 400**）；`_nPage` 分页正确且不重叠。⚠️ 与 Subfeed（页大小定死 15、`perPage` 一族改不动）**行为不同**，两个端点要分别对待 |
| 分类端点排序 | 同上 `_sSort` | ❌ **任何值都 400** —— 走分类路径时没有排序可用，UI 上把排序下拉置灰（`ModStoreViewModel.CanSort`） |
| 板块 Mod 总数 | `Mod/Index?_nPage=1&_nPerpage=1&_aFilters[Generic_Game]=20357` | ✅ `_nRecordCount=3056`（整板块口径，与排序视图无关）。比 Subfeed 的总数可靠 —— 那个换个排序就从 3062 变 1333 |
| 关键词命中数 | `Search/Results` 的 `_aMetadata._aSectionMatchCounts` | 取 `Mod` 项的 `_nMatchCount`；⚠️ **命中 0 时服务端不给这一项**（不是给 0），要按「有清单但没 Mod 项 = 0」处理，否则空结果会被当成「未知」而一直显示不出「没有找到」 |
| 根分类清单 + 条目数 | `GET /apiv11/Game/20357/ProfilePage` | ✅ `_aModRootCategories[]` 带 `_idRow` + `_nItemCount`：`Skins` 29524/2815、`Other/Misc` 29493/155、`UI` 29496/84。**一次请求拿全**，不用翻列表去数 |
| 子分类（角色）清单 | 无 | ❌ **没有这个端点**：`Game/{id}/Categories` 404、`Mod/Categories?_idGameRow=…` 400、`ModCategory/Index` 忽略游戏过滤按全局分页每页 5 条。侧栏的角色表因此来自**本地游戏数据**（`GameService.GetAllModdableObjectsAsCategory<ICharacter>()`，中文 `DisplayName` 排序） |
| 评论数 | 三个列表端点的 `_nPostCount` | ✅ Subfeed / Search / Mod-Index **都给** —— 卡片上第三项统计（浏览 / 点赞 / 评论）可以稳定显示。⚠️ `_nDownloadCount` 仍然**只有详情页**有，卡片上不要放下载量 |
| 角色字段的有无 | Subfeed vs Mod-Index 的 `_aSubCategory` | ⚠️ Subfeed 记录**经常整个没有这个键**（UI 类记录就是这样），而 Mod-Index 记录同时给 `_aSubCategory` 与 `_aGame`。角色字段必须可空，卡片上不占位 |

**侧栏「分类」的设计决定**（据上表）：

- `全部` 用 `Mod/Index` 的 `_nRecordCount`（3056）；三个根分类的数字零成本（ProfilePage 一次给全）。
- **角色计数没有批量端点**，只能一个角色一个请求（每个约 35 KB）→ 并发限 2、可取消、会话内缓存；界面**先出名字**、数字后台逐个补，取不到就不显示数字（**不是**显示 0，那会读成「这个角色没有 mod」）。
- 分类数字之和（2815+155+84=3054）与「全部」（3056）**口径不同**，差两条，不要互相推。
- 分类路径一页取 30（`_nPerpage` 上限 50）；搜索端点页大小定死 15。

**列表记录（Subfeed）可用字段**（实测全量）：
`_idRow`、`_sModelName`、`_sName`、`_sProfileUrl`、`_tsDateAdded`、`_tsDateModified`、`_tsDateUpdated`、`_bHasFiles`、`_sVersion`、`_bIsObsolete`、`_aRootCategory`、`_aSubCategory`、`_aPreviewMedia`、`_aSubmitter`、`_nLikeCount`、`_nViewCount`、`_nPostCount`、`_bWasFeatured`、`_sInitialVisibility`、`_bHasContentRatings`、`_aTags`、`_bIsOwnedByAccessor`

⚠️ `_aRootCategory` **没有 `_idRow`**（实测只有 `_sName` / `_sProfileUrl` / `_sIconUrl`），分类 Id 只能从 `_sProfileUrl` 末段抠（`…/mods/cats/29496`）；筛选请落在 `_sName` 上。另外 `_aPreviewMedia` 可以**存在但没有 `_aImages` 键**（实测 Question 93923 只有 `_aMetadata`），解析时按「零张图」处理而不是判空。

**四个必须写进实现的坑**：

1. **NSFW 无法服务端过滤**：`_bShowNsfw=false` 参数实测**无效**（记录数 6090 → 6090，无变化）。列表侧唯一可靠信号是 **`_bHasContentRatings`**（布尔）。因此「默认隐藏」必须是**客户端过滤**。注意：详情页文本里出现的 "adult/NSFW content" 字样属于 `_aLicenseChecklist` 的许可条款文案，**不是** NSFW 标志，不要误用。
2. **`_aTags` 两个端点行为不同**：Subfeed 恒为空数组（抽样 15 条全空，本次抓的 3 条也全空），但 **Search 会返回真值**（实测 `jinhsi: manuka`）。所以「按标签筛选」在**浏览视图**下不可行（搜索视图下或许可以，但两个视图行为不一致的筛选维度不要做）。
   > **勘误（同日第二轮）**：这里原先写「`_aRootCategory` 的分类即角色名（实测 `Hsin`、`UI`、`Skins`）」—— **错了**。`_aRootCategory`（列表字段）只有 `Skins` / `Other-Misc` / `UI` 三个**根**分类；`Hsin` 来自**详情页**的 `_aCategory._sName`，那是**子**分类。两处字段名像，语义不同：列表记录的角色在 `_aSubCategory`，分类筛选请走 `Mod/Index` 的 `_aFilters[Generic_Category]`（见上表）。
3. **列表与详情的字段不一致**：`_nDownloadCount` 只在详情页有；`_aCategory` 只在详情页有。列表里要用 `_aRootCategory`，别指望复用同一套模型。
4. **分页与页大小都改不动**：参数是 `_nPage`（不是 `page`），页大小**固定 15**（`perPage` 一族全被忽略）。这带来两个后果：① 列表页大小不能照抄 Mod 市场的 24（那是 Supabase 侧自己定的一页 24 条）；② 一页 15 条里混着非 Mod 时，过滤完可能只剩几条 —— 「还有没有下一页」必须看服务端的 `_bIsComplete`，**不能**用「本页条目数 < 页大小」来判断。

### 现有代码落点

| 要做的事 | 复用/改动点 |
|---|---|
| 导航项 | `Views/ShellPage.xaml:61-112`（`NavigationView.MenuItems`，照 `:89-93` 的 Mod 市场模板） |
| 页面注册 | `Services/PageService.cs:39` 加一行 `Configure<...>()`（路由 key 是 ViewModel 的 FullName） |
| DI 注册 | `App.xaml.cs:340-389`（`AddTransient` VM + Page；服务按需 `AddSingleton`） |
| 资源字符串 | `Strings/zh-cn/Resources.resw`、`Strings/en-us/Resources.resw` 加 `Shell_ModStore.Content`。**注意 es-ar / ru-ru 目前连 `Shell_ModMarket` 都缺**，是否补齐需与既有缺口保持一致策略 |
| 布局复用 | `Views/ModMarketPage.xaml`：190 左侧栏 `:153-165`、卡片列表 `:237-259`（`WrapGridPanel`）、详情抽屉 `views:ModDetailPanel` `:289`；分页/滚动触底 `ModMarketPage.xaml.cs:214-224`，页大小 24（`ModMarketViewModel.cs:84`） |
| 安装衔接 | `ModInstallerService.StartModInstallationAsync`（**已是单例**，`App.xaml.cs:245`）→ `ModInstallerService.cs:62-74` 里 `new ModInstallerPage(...)` 塞进独立 `WindowEx`。**现成先例**：`ModPageVM.cs:281-287` 就是「下载完带 `ModUrl` 调这个方法」 |
| 角色/游戏数据 | `GameService.InitializeCharactersAsync` 读 `Assets/Games/WuWa/characters.json`（`GameService.cs:810-864`）。WuWa 资源齐全，无需新增 |
| 游戏级配置 | `SelectedGameService`：配置按游戏隔离在 `%LocalAppData%\JASM\ApplicationData_<game>\`（`SelectedGameService.cs:41-51`、`:96-108`） |
| 本地索引 | 沿用 `ILocalSettingsService`（`Services/LocalSettingsService.cs`，游戏级 `LocalSettings.json`，`SettingScope.Game`）。归档命名沿用 `ModArchiveRepository` 的 `<name>_!!_<modId>_!!_<fileId>_!!_<md5>` 约定 |
| 下载器蓝本 | `ModEnvInstallerService.DownloadWithResumeAsync:145` / `DownloadOnceAsync:199`（`.part` + `Range` + 活动超时 + 指数退避 + SHA256）；`AppUpdateDownloader.cs:11-23` 的注释明确要求抽公共件 |
| HTTP client | `App.xaml.cs:197-206` 已有具名/类型化 GameBanana client（自定义 UA、Polly 限流 + 重试）。商店复用该 client，**不新建** |

### Performance

- 首页加载 ≤ 2s（在正常网络下），列表走分页：浏览 / 搜索端点**页大小定死 15**（改不动），分类端点取 30（上限 50）。
- 搜索输入做去抖（≥ 300ms），避免撞击 GameBanana 限流。
- 复用现有 Polly 令牌桶限流；商店新增的请求量必须落在同一限流策略内。

### Security

- 无认证、无 API key（GameBanana 公开 API）。**不引入任何凭据**。
- 商店不写也不读 Supabase，**不新增任何服务端**。
- 安装记录只存本地（mod id / file id / md5 / 路径），不上报。
- 下载文件校验 MD5 后再交给安装向导，避免半截包进安装流程。
- 日志中不得出现用户的本地绝对路径细节（沿用项目既有约定）。

### Integration

- **GameBanana `apiv11`**：唯一数据源。注意上游已标记该 API 存在 Deprecation 迹象（现有 `HealthCheckAsync` 会检测并 `Debugger.Break()`），商店需容错到「API 挂了也不影响 App 其他部分」。
- **现有 GameBanana 客户端** `IApiGameBananaClient`：**没有搜索方法**，需扩展（列表 / 搜索两个新方法 + 列表记录的模型）。
- **Supabase**：**零接触**。

### Technology Stack

.NET 9 + WinUI 3 / CommunityToolkit.Mvvm（`[ObservableProperty]` / `[RelayCommand]`）/ `IHttpClientFactory` + Polly / Serilog。遵循 `src/.editorconfig`，只保证新写与改动的行干净。

---

## MVP Scope & Phasing

### Phase 1: MVP（本 PRD 范围）

1. 导航 + 商店页面骨架，布局复用 Mod 市场 — ✅ 已完成
2. GameBanana 客户端扩展：列表（Subfeed）、搜索（Search/Results）、分类索引（Mod/Index）、根分类（ProfilePage）、列表记录模型 — ✅ 已完成
3. 浏览能力：分页、搜索、排序（默认/最新/最近更新）、按根分类（服务端 `_aFilters[Generic_Category]`）与按角色（搜索端点）筛选、左侧栏分类与计数 — ✅ 已完成
4. 详情抽屉：截图、作者、说明、统计、文件列表
5. 公共下载件（进度 + 断点续传 + MD5 校验），**只给商店用**
6. 下载管理器：串行队列 + 暂停/继续 + 取消 + 进度
7. 一键部署：下载完成 → 零确认弹安装向导 → 写安装记录
8. 本地安装索引 + 已装角标 + 可更新提示
9. NSFW：设置页开关（默认隐藏）+ 客户端过滤 — 页面内下拉已可用（会话级，未落盘；设置页开关未做）
10. 跨游戏目录定位（读 WuWa 游戏级配置 + 未配置时的引导态）
11. **Phase 1 内需收口的验证项**：
    - ✅ 分类筛选已收口：服务端参数是 `_aFilters[Generic_Game]` + `[Generic_Category]`，走 `Mod/Index`（原先猜的 `_idCategoryRow` 是错的，会被静默忽略）
    - ⏳ 仍待枚举：`_sSort` 的点赞 / 下载量排序取值（`Mod/Index` 不接受 `_sSort`，只能落在 Subfeed 的「全部」视图上）

**MVP Definition**：用户能在 JASM 里搜到鸣潮 mod、点一下、装进游戏，且装过的东西 JASM 记得住。

### Phase 2: Enhancements（发布后）

- 把 `AppUpdateDownloader` / `ModEnvInstallerService` 迁移到公共下载件，消除三份重复
- 商店内卸载 / 回滚
- 点赞/下载量排序（若 Phase 1 未收口）
- 更细的分类树筛选（分类筛选本身已服务端化；上游没有「列出板块子分类」的端点，要做得自己维护一份 id 映射）

### Future Considerations

- 扩展到其他游戏（改游戏 id 配置即可 —— 商店的数据层按 game id 参数化，不写死 20357）
- 与「Mod 预设」联动（把商店装好的 mod 直接进预设）
- 「已安装」筛选标签页

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|---|---|---|---|
| **排序参数取值不全**：`_sSort` 的点赞/下载量排序未确认 | 中 | 中 | Phase 1 内枚举确认；退路是页内客户端排序（仅影响单页，需在 UI 上说明或改为「热门」语义） |
| **GameBanana API 非官方契约**：上游已在讨论弃用，字段可能变 | 中 | 高 | 所有解析做防御式：缺字段退化为 null/空，绝不抛异常（沿用 `AppUpdateReleaseResolver` 的既有风格）；API 挂掉只影响商店页，不拖累 App |
| **NSFW 只能客户端过滤**，且详情页没有干净的 NSFW 布尔字段 | 中 | 中 | 列表用 `_bHasContentRatings`；详情页另找可靠信号，找不到就在详情侧对成人内容做保守标注 |
| **下载器抽取引入回归** | 低 | 高 | 本期公共件**只给商店用**，不动两条已实机验证的链路；公共件以 `ModEnvInstallerService` 的成熟形状为蓝本 |
| **角色筛选靠「角色名当关键词搜」**，本地 `InternalName` 与 GameBanana 子分类名对不齐（如 `YangyangXuanling` vs `Yangyang: Xuanling`） | 中 | 中 | 分类（根分类）走服务端精确筛选不受影响；角色命中不齐时用户还能用搜索框自己搜 —— 文案上不承诺「角色全量可选」 |
| **角色计数要一个角色一个请求**（约 35 KB / 个，五十多个角色） | 高 | 低 | 并发限 2 + 可取消 + 会话内缓存 + 拿不到就不显示数字；用户先看到内容，计数只是锦上添花 |
| **跨游戏写配置的耦合**（商店读另一游戏的配置目录） | 中 | 中 | 封装成单一服务（如 `ModStoreTargetResolver`），只读 + 一次解析 + 明确失败态；未配置时禁用部署并引导 |

---

## Dependencies & Blockers

**Dependencies**
- GameBanana `apiv11` 的可用性（外部，无 SLA、无合同）
- 鸣潮在 JASM 中已作为一等游戏存在：`Assets/Games/WuWa/` 资源齐全、`game.json` 含 `GameBananaUrl: https://gamebanana.com/games/20357` 与 `ModEnv(PackageId=wwmi, SubDir=WWMI)` —— **已确认，非阻塞**
- 「添加模组 → 安装向导」链路零游戏硬编码 —— **已确认，非阻塞**

**Known Blockers**
- 无硬性阻塞。`_sSort` 的排序取值枚举是 Phase 1 内的自收口任务，不依赖外部。

**Working-tree 风险（非技术）**：本 PRD 落盘时当前分支为 `feat/update-cos-and-progress`，该分支有 8 个未提交的修改文件，且另有 agent 在别的分支上作业。本功能应在新分支上实施。

---

## Appendix

### Glossary

- **`apiv11`**: GameBanana 的公开只读 API，无需认证。
- **Subfeed**: 游戏板块的内容流端点，商店的列表数据源。
- **`_sSort`**: 排序参数。**不是 `sort`** —— 用错名字会被静默忽略（无报错、结果不变），是本次最容易踩的坑。
- **`_nPage`**: 分页参数。**不是 `page`** —— 同样会被静默忽略；页大小固定 15，`perPage` 一族都改不动。
- **`_bHasContentRatings`**: 列表记录里唯一可靠的成人内容标志。
- **`_aRootCategory`**: **列表**记录上的**根**分类对象（鸣潮只有 `Skins` / `Other-Misc` / `UI`），且**没有 `_idRow`**，id 只能从 `_sProfileUrl` 末段抠。
- **`_aSubCategory`**: **列表**记录上的**子**分类对象，鸣潮板块的子分类就是角色名（`Jinhsi` / `Qingxiao` …）。⚠️ 记录里**可以整个没有这个键**（UI 类 mod 就没有）。
- **`Mod/Index`**: `apiv11` 的列表索引端点。本次实测它是**唯一**支持服务端分类筛选（`_aFilters[Generic_Game]` + `[Generic_Category]`）且认 `_nPerpage` 的列表端点；代价是不支持 `_sSort`（400）。
- **`Generic_Category` 筛选**: 按分类 id 的服务端筛选，形如 `_aFilters[Generic_Category]=29524`。参数名写错（如 `_idCategoryRow`）不会报错，只是被静默忽略。
- **WWMI**: Wuthering Waves Model Importer，鸣潮的 mod 加载框架（`d3d11.dll`）。
- **一键部署**: 本 PRD 的核心交互 —— 下载完成即零确认弹出安装向导。

### References

- GameBanana 鸣潮板块: https://gamebanana.com/games/20357
- GameBanana WWMI 工具: https://gamebanana.com/tools/17252
- 相关代码入口：`src/GIMI-ModManager.Core/Services/GameBanana/IApiGameBananaClient.cs`、`src/GIMI-ModManager.WinUI/Services/ModHandling/ModInstallerService.cs`、`src/GIMI-ModManager.WinUI/Views/ModMarketPage.xaml`
- 项目规范：`CLAUDE.md`（提交约定、发布链路、编码规范）

### 实测证据（2026-09-30）

```
GET /apiv11/Game/20357/Subfeed?page=1&perPage=2            → 200, _nRecordCount=6090, _nPerpage=15
                                                             （要 2 条给了 15 条 → page/perPage 均被忽略）
GET /apiv11/Game/20357/Subfeed?_nPage=1&_csvModelInclusions=Mod → _nRecordCount=3062（Mod-only）
GET /apiv11/Game/20357/Subfeed?...&_sSort=new              → 首条 _tsDateAdded = 2026-09-30（今天），_nRecordCount 仍 3062
GET /apiv11/Game/20357/Subfeed?...&_sSort=updated          → 首条 _tsDateUpdated = 2026-03-07，_nRecordCount **1333**
GET /apiv11/Game/20357/Subfeed?...&_sSort=updated&_nPage=80 → 200，仍 15 条（深翻页有效）
GET /apiv11/Game/20357/Subfeed?...&_sSort=newest           → 400
GET /apiv11/Game/20357/Subfeed?...&sort=<任意值>            → 与默认完全一致（参数被忽略）
GET /apiv11/Game/20357/Subfeed?...&_bShowNsfw=false        → _nRecordCount 仍为 6090（过滤无效）
GET /apiv11/Util/Search/Results?_sSearchString=skin&_idGameRow=20357 → 200, 705 命中 / Mod 277
                                                             （一页 6 条里混着 Request / Question / Mod）
GET /apiv11/Mod/722504/ProfilePage                         → _aCategory._sName="Hsin", _sInitialVisibility="hide"
GET /apiv11/Mod/575376/ProfilePage                         → _aCategory._sName="UI",   _sInitialVisibility="show"
```

第二轮（分类筛选 / 计数）：

```
GET /apiv11/Mod/Index?_aFilters[Generic_Game]=20357                     → 200, _nRecordCount=3056（整板块）
GET /apiv11/Mod/Index?...&_nPerpage=30                                   → 200, 30 条，_nPerpage=30（**该端点认页大小**）
GET /apiv11/Mod/Index?...&_nPerpage=100                                  → 400（上限 50 有效）
GET /apiv11/Mod/Index?...&_nPage=2                                       → 200，与第 1 页不重叠
GET /apiv11/Mod/Index?...&_sSort=new                                     → 400（**该端点不支持排序**）
GET /apiv11/Mod/Index?...&_aFilters[Generic_Category]=29524              → 200, _nRecordCount=2817（Skins）
GET /apiv11/Mod/Index?...&_aFilters[Generic_Category]=46598              → 200, _nRecordCount=2（某角色子分类）
GET /apiv11/Game/20357/Subfeed?...&_aFilters[Generic_Category]=29524     → 29524 被**静默忽略**（记录与不传时一致）
GET /apiv11/Mod/Index?...&_aFilters[Generic_Name]=foo                    → 400（没有这个键）
GET /apiv11/Game/20357/ProfilePage                                       → _aModRootCategories = [Skins 29524/2815, Other/Misc 29493/155, UI 29496/84]
GET /apiv11/Game/20357/Categories                                        → 404（没有「列出板块子分类」的端点）
GET /apiv11/ModCategory/Index                                            → 忽略游戏过滤，全局分页每页 5 条
GET /apiv11/Util/Search/Results?_sSearchString=<q>&_idGameRow=20357      → _aSectionMatchCounts 里 Mod 项 = 命中数；**命中 0 时这一项不存在**
```

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
