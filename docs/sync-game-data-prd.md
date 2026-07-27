# Product Requirements Document: 云端同步游戏数据

**Version**: 1.0
**Date**: 2026-07-27
**Author**: Sarah (Product Owner)
**Quality Score**: 90/100

---

## Executive Summary

JASM 目前每次游戏版本更新新增角色时，用户必须等待 JASM 发布新版本才能获得新角色支持。本功能通过在设置页新增「云端同步游戏数据」模块，让 JASM 启动时自动从 GitHub Releases 下载最新的角色数据 ZIP 包（含 JSON 定义 + 图片资源），无需更新整个 JASM 即可获取新角色。角色概览页在同步完成后自动刷新，即时显示新角色。

目标用户是 JASM 的 Mod 玩家——他们希望游戏更新新角色后能立刻在 JASM 中管理这些角色的 Mod，而不是等几天甚至几周等 JASM 发版。

---

## Problem Statement

**当前状况**: 角色数据（`characters.json`、`npcs.json` 等）和角色图片作为 JASM 编译时资源打包在应用中。新增角色必须由开发者提交代码、构建、发版、用户手动下载更新。从游戏版本更新到 JASM 支持新角色之间存在显著延迟。

**解决方案**: 将角色数据从「编译时资源」转为「可运行时更新的外部数据」。在设置页新增游戏数据同步功能，JASM 启动时自动检查 GitHub Releases 上是否有新的数据包，有则下载 ZIP 并解压到本地 Assets 目录，覆盖旧数据并保留用户自定义角色，角色概览页自动刷新。

**预期效果**: 新角色上线后，开发者只需往 GitHub Release 上传一个数据 ZIP 包，用户下次启动 JASM 时自动获取新角色。从「等 JASM 发版」变为「启动即得」。

---

## Success Metrics

**主要 KPI:**
- **新角色可用延迟**: 从游戏版本更新到用户在 JASM 中看到新角色的时间 < 24 小时（对比目前需要等 JASM 发版，通常 1-4 周）
- **同步成功率**: 手动同步成功率 > 95%（排除网络不可用情况）
- **自动同步感知率**: 用户无需手动干预即可获得新角色的比例 > 80%

**验证方式**: 通过 JASM 日志记录同步成功/失败次数，GitHub Release 下载量统计。

---

## User Personas

### Primary: Mod 玩家

- **角色**: 使用 JASM 管理游戏 Mod 的玩家
- **目标**: 游戏更新后第一时间给新角色换 Mod
- **痛点**: 游戏已经能玩了，JASM 还没更新支持新角色，Mod 放不进去
- **技术水平**: 初级到中级，不想手动编辑 JSON 或下载文件放到指定目录

---

## User Stories & Acceptance Criteria

### Story 1: 启动时自动获取新角色

**As a** Mod 玩家
**I want to** JASM 启动时自动检查并下载新角色数据
**So that** 我不用手动操作就能看到新角色

**Acceptance Criteria:**
- [ ] 设置页有「启动时自动检查更新」开关，默认开启
- [ ] JASM 启动后，后台静默检查当前游戏的 GitHub Release 数据包
- [ ] 如果本地已是最新版本，不做任何操作，不弹窗
- [ ] 如果有新数据包，后台下载并解压，完成后角色概览页自动刷新
- [ ] 网络失败时不弹窗打扰用户，仅在设置页状态文字更新

### Story 2: 手动同步游戏数据

**As a** Mod 玩家
**I want to** 在设置页点击按钮手动检查并同步游戏数据
**So that** 我可以主动获取最新角色

**Acceptance Criteria:**
- [ ] 设置页游戏选择器下方有「游戏数据同步」区域
- [ ] 显示上次同步时间和当前数据版本
- [ ] 「检查并同步」按钮点击后显示同步进度
- [ ] 同步完成后显示成功/失败 Toast 通知
- [ ] 同步失败时 Toast 提示具体原因（网络错误 / 已是最新）

### Story 3: 同步后角色概览页即时更新

**As a** Mod 玩家
**I want to** 同步完成后角色概览页面立即出现新角色
**So that** 我不需要重启 JASM 就能管理新角色的 Mod

**Acceptance Criteria:**
- [ ] 同步完成后，角色概览页自动刷新角色列表
- [ ] 新角色卡片显示正确的头像图片、名称、元素等信息
- [ ] 新角色的 Mod 文件夹自动创建
- [ ] 用户已有的自定义角色不丢失

---

## Functional Requirements

### Core Features

**Feature 1: GitHub Release 数据包下载与解压**

- 描述: 从 `https://api.github.com/repos/Jorixon/JASM/releases` 获取最新 Release 中命名规则为 `{game}-data-*.zip` 的资产文件，下载并解压到本地 `Assets/Games/{GameName}/` 目录
- 用户流程: 自动触发或手动点击 → 检查版本 → 下载 ZIP → 解压覆盖 → 通知完成
- 边界情况:
  - ZIP 内 JSON 格式无效 → 回滚，保留旧数据，Toast 报错
  - 磁盘空间不足 → 下载前检查空间，不足时提示
  - GitHub API 限流 → 使用 ETag/If-None-Match 减少请求，被限流时静默跳过
- 错误处理: 网络超时重试 2 次（Polly），仍失败则静默跳过（自动模式）或 Toast 提示（手动模式）

**Feature 2: 版本检测与增量同步**

- 描述: 本地存储当前数据版本号（与 Release tag 对应），与 GitHub Release 最新 tag 比较，仅在有新版本时下载
- 用户流程: 比较版本号 → 相同则跳过 → 不同则下载
- 边界情况:
  - 首次使用无本地版本记录 → 认为需要同步
  - 本地版本比云端新（开发环境） → 跳过同步
- 版本号存储: 在 `Assets/Games/{GameName}/.dataversion` 文件中记录

**Feature 3: 冲突处理与自定义角色保留**

- 描述: 云端 JSON 覆盖本地对应 JSON 文件。用户通过 JASM 自定义角色管理页面创建的自定义角色存储在独立文件中，不会被覆盖
- 用户流程: 覆盖 characters.json / npcs.json 等 → 保留自定义角色配置文件 → GameService 重新加载
- 边界情况:
  - 自定义角色 Keys 与云端新角色冲突 → 云端优先，自定义角色 Keys 被覆盖可能导致 Mod 关联丢失 → 记录警告日志

**Feature 4: 设置页同步 UI**

- 描述: 在 SettingsPage.xaml 的游戏选择区域下方插入「游戏数据同步」区域
- UI 元素:
  - 上次同步时间（如 "从未同步" 则显示提示文字）
  - 当前数据版本
  - 「🔄 检查并同步游戏数据」按钮
  - 同步状态文字（✓ 已是最新 / ↓ 正在下载... / ✗ 同步失败）
  - 「JASM 启动时自动检查更新」ToggleSwitch
- 状态管理: 使用 CommunityToolkit.Mvvm `[ObservableProperty]` 驱动 UI 绑定

**Feature 5: 角色概览页自动刷新**

- 描述: 同步完成后通过消息机制（`WeakReferenceMessenger`）通知 CharactersViewModel 重新加载角色列表
- 用户流程: 同步完成 → 发送 RefreshMessage → CharactersViewModel.OnNavigatedTo 重新执行 → UI 更新

### Out of Scope
- 不支持同步时保留用户手动修改的角色显示名称（语言文件覆盖）
- 不支持选择特定角色下载（总是全量同步当前游戏数据）
- 不支持断点续传（数据量 < 10MB，无需）
- 不包含 Supabase 后端 — 使用 GitHub Releases 作为唯一数据源

---

## Technical Constraints

### 性能
- 数据包 < 10MB，下载应在 30 秒内完成（普通宽带）
- 同步过程在后台线程执行，不阻塞 UI
- ZIP 解压 < 5 秒

### 安全
- GitHub Releases 通过 HTTPS 下载，验证 SSL 证书
- ZIP 解压前检查文件路径，防止 Zip Slip 攻击（路径遍历）
- 仅允许覆盖 `Assets/Games/{GameName}/` 下的已知文件名

### 集成
- **GitHub API**: `https://api.github.com/repos/Jorixon/JASM/releases`（复用现有 `UpdateChecker` 模式）
- **IHttpClientFactory**: 注册命名的 `"GameDataSync"` HttpClient，配置 Polly 重试策略
- **GameService**: 同步完成后调用重新初始化方法加载新数据
- **CharacterViewModel**: 通过 `WeakReferenceMessenger` 接收同步完成消息

### 技术栈
- .NET 9.0, WinUI 3, WinAppSDK
- CommunityToolkit.Mvvm（消息、命令、属性）
- `IHttpClientFactory` + Polly（HTTP 请求与重试）
- `System.IO.Compression.ZipArchive`（ZIP 解压，.NET 内置）
- SharpCompress（已在依赖中，可作为备选）

---

## MVP 范围与分阶段规划

### Phase 1: MVP（首次发布必须）
- [ ] `GameDataSyncService` — GitHub Release ZIP 下载与解压核心服务
- [ ] 版本检测与 `.dataversion` 本地状态管理
- [ ] 设置页 UI 区域（上次同步时间 + 版本 + 同步按钮 + 自动检查开关）
- [ ] 启动时自动检查逻辑
- [ ] 同步完成后角色概览页自动刷新
- [ ] Toast 通知（成功/失败）
- [ ] Zip Slip 安全防护

### Phase 2: 增强（后续可选）
- [ ] 下载进度条（百分比显示）
- [ ] 多语言同步（目前仅 zh-cn）
- [ ] 同步日志详情页
- [ ] 手动回滚到上一个数据版本

### 未来考虑
- [ ] 支持增量图片下载（仅下载新增图片）
- [ ] 数据包完整性校验（SHA256 checksum）
- [ ] 内置 CDN 镜像作为 GitHub 被限流时的 fallback

---

## 风险分析

| 风险 | 概率 | 影响 | 缓解策略 |
|------|------|------|----------|
| GitHub API 限流（60次/小时未认证） | 中 | 中 | 使用 ETag 条件请求减少调用；启动检查间隔至少 2 小时 |
| ZIP 解压覆盖导致数据损坏 | 低 | 高 | 解压前备份旧数据到 `.backup/`，JSON 解析失败时自动回滚 |
| 自定义角色与云端角色 Keys 冲突 | 中 | 低 | 记录警告日志，云端优先；后续版本可增加冲突检测 UI |
| WinUI 资源重新加载性能问题 | 低 | 中 | 使用虚拟化集合，增量更新而非全量重建 UI |
| GitHub Release 数据包未及时发布 | 中 | 低 | 同步失败时保留旧数据，设置页显示「等待开发者更新」状态 |

---

## 依赖与前置条件

**依赖项:**
- **GitHub Release 工作流**: 需要 Release 脚本（`Build/Release.py`）能自动打包角色数据 ZIP 并上传为 Release Asset
- **数据包命名规范**: ZIP 文件命名格式为 `{game}-data-v{semver}.zip`，例如 `genshin-data-v2.23.0.zip`

**已知阻塞项:**
- 无。所有依赖项在 JASM 代码库内可控

---

## 附录

### 术语表
- **GameService**: `GIMI-ModManager.Core` 中的核心服务，负责加载和管理游戏角色数据
- **CharactersViewModel**: 角色概览页的 ViewModel，通过 `ICategory` 参数接收并显示角色列表
- **ModdableObject**: JASM 中可安装 Mod 的实体基类（角色、NPC、武器等）
- **数据版本**: 与 JASM Release 版本号对齐的语义版本，记录在 `.dataversion` 文件中

### 参考
- 现有 GitHub API 调用模板: `src/GIMI-ModManager.WinUI/Services/AppManagement/Updating/UpdateChecker.cs`
- 现有 HTTP 客户端注册模式: `src/GIMI-ModManager.WinUI/App.xaml.cs`
- 角色 JSON 数据模型: `src/GIMI-ModManager.Core/GamesService/JsonModels/JsonCharacter.cs`
- 角色加载初始化: `src/GIMI-ModManager.Core/GamesService/GameService.cs`

---

*本 PRD 通过交互式需求收集和质量评分创建，确保业务、功能、用户体验和技术维度全面覆盖。*
