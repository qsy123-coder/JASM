# CLAUDE.md

本文件是 **JASM (Just Another Skin Manager)** 仓库的项目上下文与工程规范，供 Claude Code 在此仓库中工作时遵循。

> 注意：本仓库**不是**「Claude Code 最佳实践参考仓库」。仓库内没有 `.claude/`、`best-practice/`、`skills/` 目录。此前模板版 CLAUDE.md 里的 Weather System、Subagent/Skill/Hooks 定义均不适用于此处，已清理。

## 项目概述

- **项目**：JASM - Just Another Skin Manager，一个 WinUI 3 桌面应用，用于管理游戏（原神等）的 Mod / 皮肤。
- **fork 关系**：本仓库是 `Jorixon/JASM` 的 fork → `qsy123-coder/JASM`，默认分支为 `master`（不存在 `main` 分支）。
- **自动更新**：检测 + 手动点更新，主 app 用 `UpdateChecker`，更新执行体是独立的 `JASM - Auto Updater.exe`。版本清单与安装包托管在腾讯云 COS 的 `app/` 前缀（见「JASM 自动更新 / Release 发布链路」），GitHub Releases 是回退通道。

## 技术栈

- **框架**：.NET 9 (`net9.0-windows10.0.22621.0`) + Windows App SDK 1.7 (WinUI 3)，`Nullable` / `ImplicitUsings` 均开启。
- **MVVM**：CommunityToolkit.Mvvm 8.4（`[ObservableProperty]` / `[RelayCommand]` 源生成器）。
- **UI**：CommunityToolkit.WinUI.*（Segmented / SettingsControls 等）、WinUIEx、WinUI3Localizer。
- **日志**：Serilog（File / EventLog / Debug sink）。
- **数据**：Newtonsoft.Json、Supabase / PostgREST（Mod 市场后端）。
- **其他**：FluentValidation、Polly（重试/限流）、OneOf、WindowsDisplayAPI。
- **构建**：`Build/Release.py`（发布打包）、`Build/PackGameData.py`（游戏数据打包）、`Build/PackAppUpdate.py`（应用更新清单）。

## 目录结构

```
src/
├── GIMI-ModManager.sln
├── GIMI-ModManager.WinUI/      # 主应用 (WinUI 3) —— 真正的 JASM
├── GIMI-ModManager.Core/       # 核心库
├── JASM.AutoUpdater/           # 独立自动更新进程 (JASM - Auto Updater.exe)
├── CommunityToolkitWrapper/    # CommunityToolkit 封装
├── Elevator/                   # UAC 提权助手 (Elevator.exe)
├── JASM.Tests/                 # xUnit 测试
├── Tools/                      # 基准 / 工具
├── GenshinGenerator/           # 资产生成器
└── UpdateGenshinAssets/        # 资产生成更新
Build/
├── Release.py                  # 发布打包 (无参 folder / SingleFile / SelfContained；ExcludeElevator 是逃生口)
├── PackGameData.py             # 游戏数据打包
└── PackAppUpdate.py            # 应用更新清单 (app/update.json，上传 COS)
.github/workflows/
├── dotnet-desktop.yml                # folder 版 (无参，含 Elevator 助手与 AutoUpdater) → artifact
├── dotnet-desktop-single-file.yml    # SingleFile → artifact（单 exe 包只能由 CI 产出，本机无 MSVC）
├── dotnet-desktop-self-contained.yml # SelfContained → artifact
├── dotnet-format.yml                 # dotnet format --verify-no-changes (监听 main —— 该分支不存在，永不触发)
└── release-please.yml                # 版本管理 + 开 release PR (监听 master)
```

## 编码规范

- 遵循 `src/.editorconfig`。**注意 `dotnet format --verify-no-changes` 实际上不是门禁**：`dotnet-format.yml` 监听的是不存在的 `main` 分支（永不触发），且 `master` 上该命令本来就有存量违规。只保证自己新写 / 改动的行干净，别去动别人留下的差异。
- ViewModel 用 CommunityToolkit 源生成器（`[ObservableProperty]` / `[RelayCommand]`），避免手写 INotifyPropertyChanged。
- 依赖注入通过 `Microsoft.Extensions.Hosting`；日志统一走 Serilog。
- 边界输入（用户/远端 API）尽量校验；错误信息勿暴露敏感数据（如 token / 本地路径细节）。

## Git 提交规范

1. **每个文件单独一个 commit**（重要，是本仓库的硬性约定）。**不要**把多个文件的改动 bundle 进一个 commit —— 每个文件一条独立的、描述该文件改动的 message，便于 review / revert / cherry-pick。
   - 例如：`README.md`、`src/.../Foo.cs`、`Build/Release.py` 都改了 → 拆成 3 个 commit。
2. 提交前缀遵循 Conventional Commits：`feat:` `fix:` `refactor:` `chore:` `docs:` `ci:` `perf:` 等。
3. 涉及远程操作（`push` / 合并 PR / 发 release / 改动 release 资产）前，先与用户确认。

## JASM 构建与发布验证

push 前必须验证完整 CI 链路 —— `dotnet build` 单跑不算完成：

1. **C# 编译**：`dotnet build src/GIMI-ModManager.WinUI/GIMI-ModManager.WinUI.csproj`
2. **Python 发布脚本**：`python Build/Release.py` —— 验证打包流程不会因转义字符、路径问题中断。**本机（开发机）跑不到底**：助手是 AOT 发布、需要 MSVC，脚本会在构建助手那步失败（这是预期）。本机只验脚本其余部分时用 `python Build/Release.py ExcludeElevator` 或 `python Build/Release.py SingleFile ExcludeElevator`（逃生口 = 跳过助手，CI 上不用）
3. **分支名匹配**：CI workflow 监听默认分支，确保 `.github/workflows/*.yml` 中的分支名与仓库一致（本仓库是 `master`；`dotnet-format.yml` / `release-please.yml` 若有 `main` 需改为 `master`，否则永不触发）
4. **GitHub Actions 启用**：fork 仓库默认禁用 Actions，需手动去 Actions 页开启

常见坑：
- Python 3.12+ 对 `\P` `\d` 等非法转义报 SyntaxWarning，路径用正斜杠 `/`，正则用原始字符串 `r""`
- `dotnet publish` 不加 `-o` 时输出到 TFM 子目录，与脚本期望的路径不匹配
- workflow `branches:` 过滤器不匹配会导致 push 不触发 CI
- **本地构建前先关闭运行中的 JASM 进程**，避免 exe/dll 被锁定导致编译或打包失败
- **本机没装 7-Zip**：脚本最后的 `7z a` / `7z h` 会失败（`'7z' is not recognized`），本地验证只能走到 `output/` 那一步 + 检查目录内容。压缩这一步交给 CI
- 本地验证 `SingleFile` 时，`dotnet publish` 必须带 `-p:Platform=x64`（脚本已带），漏了会在运行时炸 WinUI 激活

## JASM 自动更新 / Release 发布链路

> 记录自动更新 + 发布踩过的坑，涉及 `UpdateChecker`、`AppUpdateReleaseResolver`（COS 清单 + GitHub 回退的唯一决策点）、`JASM.AutoUpdater`、`PackAppUpdate.py`、`release-please.yml`、`dotnet-desktop*.yml`。

### 1. 更新源：COS 清单优先，GitHub 回退

「最新版是哪个、包在哪」由腾讯云 COS 上的 `app/update.json` 说了算，GitHub Releases API 只在清单拉不到时兜底。三处落点（**改桶 / 改前缀时三处一起改**）：

- 主 app：`src/GIMI-ModManager.WinUI/appsettings.json` 的 `AppUpdate:ManifestUrl` / `AppUpdate:ReleasesApiUrl`
- AutoUpdater：`src/JASM.AutoUpdater/MainPageVM.cs` 的 `ManifestUrl` / `ReleasesApiUrl` 常量 —— 它是随包分发的独立进程，**拿不到主程序的 appsettings，只能写死**
- 打包脚本：`Build/PackAppUpdate.py` 的 `DEFAULT_BASE_URL`

**决策逻辑只有一份**：`src/GIMI-ModManager.Core/Helpers/AppUpdateReleaseResolver.cs`（COS 优先、GitHub 回退、失败原因合并进诊断串），主程序与 AutoUpdater 都走它 —— AutoUpdater 是把这三个 Core 文件**源链接**进自己的工程（`Elevator.csproj` 同款手法，见其 csproj 注释），不是加 ProjectReference（那会把 Supabase 等整条依赖链拖进这个独立 exe）。共用是硬要求：两边各自判断「最新版是哪个」就会出现「徽标说 2.31.0、更新器却下 2.30.0」这种不报错的偏差。

`qsy123-coder/JASM` 是 `Jorixon/JASM` 的 fork，仓库标识写错则回退通道失效、且老客户端也收不到更新。

#### 清单 schema（`app/update.json`）

```json
{
  "schemaVersion": 1,
  "releases": [
    { "version": "2.31.0", "prerelease": false, "publishedAt": "…", "notesUrl": "…",
      "assets": [
        { "name": "JASM_v2.31.0.7z", "kind": "folder", "url": "https://…/app/JASM_v2.31.0.7z",
          "sizeBytes": 148726913, "sha256": "…" },
        { "name": "SingleFile_JASM_v2.31.0.zip", "kind": "singleFile", "url": "https://…",
          "sizeBytes": 96384012, "sha256": "…" }
      ] }
  ]
}
```

- `asset.kind` 是**显式**判别：`folder`（`.7z`，走外部更新器）/ `singleFile`（`.zip`，主程序进程内自更新）。缺字段时才退回按文件名前缀（`JASM_` / `SingleFile_JASM_`）匹配 —— 所以**文件名仍然承重**，GitHub 回退通道就是靠前缀反推 `kind` 的。
- `sizeBytes` / `sha256` 给进度总量与完整性校验用，缺省则退回 `Content-Length` / 跳过校验（**缺字段绝不能让客户端崩**）。
- `version` **必须是纯数字点分、不带 `v` 前缀**（客户端用 `Version.TryParse`）。写错只会被静默跳过，现象是「发了新版用户端不提示」—— `PackAppUpdate.py` 在生成时就拦这种写法。
- 解析器**绝不抛异常**：畸形 JSON / 缺字段一律退化成 `null`，由调用方回退或报「找不到更新」。

详细运维说明（发版四步、手测配方、大文件与费用）见 `docs/app-update-cos-setup.md`。

### 2. 版本号由 release-please 管理

`GIMI-ModManager.WinUI.csproj` 的 `<VersionPrefix>` 被 `<!-- x-release-please-start-version -->` / `end` 标记包住，release-please 用它自动 bump。**确保 `release-please.yml` 的 `on.push.branches` 匹配默认分支**（本项目是 `master`，不是 `main`——`main` 分支不存在，配错了 workflow 永不触发）。

### 3. release-please 需要两个权限开关

Settings → Actions → General → Workflow permissions 下要**同时**勾「Read and write permissions」和「Allow GitHub Actions to create and approve pull requests」。只勾前者会报 `release-please failed: GitHub Actions is not permitted to create or approve pull requests`——报错在「开 release PR」那步，之前的版本 bump / 分支创建其实都成功了。

### 4. 构建 workflow 只传 artifact，不挂 release

`dotnet-desktop.yml` / `dotnet-desktop-single-file.yml` / `dotnet-desktop-self-contained.yml` 最后都是 `actions/upload-artifact`，**不会把 zip 挂到 GitHub release，也不会传到 COS**。两处都要手动：从 CI artifact 取包 → 上传 COS `app/` 前缀 → `gh release upload` 挂 GitHub（或自己给 workflow 加这两步）。

### 5. 两条更新通道：folder 版走外部更新器，单文件版走进程内自更新

- `Release.py` 在 `SingleFile` / `SelfContained` 模式跳过构建 AutoUpdater，单文件模式也只复制单 exe。
- 自动更新执行体是 `JASM - Auto Updater.exe`（独立更新器进程），**单文件安装里没有它** → `AutoUpdaterService.AutoUpdaterExists` 为 false，`SettingsViewModel.UpdateJasm` 转走 `SingleFileSelfUpdater`：下载 `SingleFile_JASM_*.zip` → 用 BCL 解出新的 exe → 临时 PowerShell 脚本等本进程退出后覆盖并重启。**单文件版因此也能自更新**，只是换了条通道（选用 .zip 而非 .7z 正是为了不依赖运行时 7z.exe）。
- 两条通道的触发机制一致：「提示 + 手动点」，不是静默推。
- **进度条只加在单文件版那条**（`SettingsViewModel` → `SingleFileSelfUpdater` → `AppUpdateDownloader`）：`IProgress<AppUpdateDownloadProgress>` 从下载器一路报到设置页的 `ProgressBar`。folder 版是另一个进程（`JASM - Auto Updater.exe` 自带的窗口），**UI 未改**，只是同样改读 COS 清单。
- `AppUpdateDownloader` 与 `ModEnvInstallerService` 的下载逻辑是**刻意重复**的两份（断点续传 / 活动超时 / SHA256 那套形状照抄）：ModEnv 那条已经实机验证过，不为一个进度条去动它。日后出现第三处调用者时再抽公共件（类注释里写了这事）。

### 6. 两条通道各取各的 asset（`kind` 优先，文件名前缀兜底）

`AppUpdateManifestParser.FindAsset(release, kind, namePrefix)`：先按 `kind` 找，找不到再按文件名前缀找。

- 外部更新器取 `kind = folder` / 前缀 `JASM_` → 只有 `JASM_vX.Y.Z.7z` 命中。`SingleFile_JASM_*.zip` / `SelfContained_JASM_*.7z` 都**不匹配**（既不 `kind: folder`、也不以 `JASM_` 开头）。
- 主程序取 `kind = singleFile` / 前缀 `SingleFile_JASM_` → 只有单文件包命中。

GitHub 回退通道上没有 `kind` 字段（API 只给文件名），由 `AppUpdateReleaseResolver.InferKindFromName` 按名字反推。所以**文件名仍然是承重的**，改名前先想清楚它同时是两条通道的判别依据。

### 7. 发布三步走 & 更新机制

1. `release-please` 建 release（tag `vX.Y.Z`）→
2. `Release.py` 打出两个包（folder 本机可打；`SingleFile` 需 MSVC，只能由 CI 产出，见第 9 节）→
3. `PackAppUpdate.py` 生成 / 增量更新 `app/update.json` → 连同两个包一起上传 COS 的 `app/` 前缀 →
   **同时**在 GitHub release 上挂同版本的同名资产（第 9 节，回退通道 + 老客户端要它）。

完整步骤与手测配方见 `docs/app-update-cos-setup.md`。

机制是「提示 + 手动点」，不是静默推：UpdateChecker 每 2h 问一次「最新版是哪个」（COS 清单优先，拉不到回退 GitHub releases），只有 `CurrentVersion < latest` 才亮徽标；用户点更新才拉起更新动作，folder 版走 AutoUpdater、单文件版走进程内自更新（第 5 节）。

⚠️ 升级必须 bump `<VersionPrefix>` **且**清单里的 `version` 用同一版本号：客户端比的是「编译版本 < 清单版本」，两边一致（例如清单写 2.31.0 而包编译的是 2.30.0）会变成「用户点更新 → 更新器发现装的和下的版本一样 → 直接退出」，看起来像点了没反应。

⚠️ **`PackAppUpdate.py` 是增量合并的**：加新版本前先把线上那份 `update.json` 存成本地副本作为 `--manifest`，否则从零生成会丢掉更早的版本记录（客户端只看最新版，所以不会立刻出事，直到有人想回退旧版）。

### 8. 打包映射速查

| `Release.py` 模式 | 产物命名 | 含 Elevator 助手 | 含 AutoUpdater | 自动更新 |
|---|---|---|---|---|
| 无参（folder 版，CI 用这个） | `JASM_v*.7z` | ✅ | ✅ | ✅ 外部更新器 |
| `SingleFile` | `SingleFile_JASM_v*.zip` | ✅（内嵌，运行时释放） | ❌ | ✅ 进程内自更新（见第 5 节） |
| `SelfContained` | `SelfContained_JASM_v*.7z` | ✅（内嵌，运行时释放） | ❌ | ❌ |
| 任意模式 + `ExcludeElevator` | 同上 | ❌ | 视模式而定 | 同上 |

- 助手是**内嵌在主 exe 里**的（csproj 的 `EmbeddedResource`，见第 10 节），不是靠散文件分发 —— 单 exe 版磁盘上只有一个文件，散文件无处可放。folder 版额外把 `Elevator.exe` 复制到 exe 同目录，那份会被优先使用（不重复释放）。
- `ExcludeElevator` 现在是**本机逃生口**（本机无 MSVC，编不出 AOT 助手）：它会把助手一并跳过，产出的包送不了按键。CI 上**不要**带它。

### 9. 发布时要挂载的多形态产物

每次发布要把面向不同用户的分发形态都传上去 —— **同一批包要传两个地方**：

| 产物 | 用途 | 传 COS `app/` | 挂 GitHub release |
|---|---|---|---|
| `app/update.json` | 版本清单，**客户端现在只认它** | ✅ 必须 | ❌ |
| `JASM_vX.Y.Z.7z` | folder 版，含 AutoUpdater，**外部更新器通道的唯一对象** | ✅ 必须 | ✅ 必须 |
| `SingleFile_JASM_vX.Y.Z.zip` | 单 exe 便携版（**大多数用户用的就是它**），走进程内自更新 | ✅ 必须 | ✅ 必须 |
| `SelfContained_JASM_v*.7z` | 自包含版 | 按需 | 按需 |

**为什么 GitHub 也要挂**：release 的 asset 本来就不会自动挂（见第 4 节），而且现在还多了两个理由 —— ① ≤2.30.0 的老客户端与用户机上那份**旧的** `JASM - Auto Updater.exe` 只会读 GitHub API；② COS 出事（欠费 / 权限配错 / 被刷 / 传成坏 JSON）时，客户端会自动回退 GitHub，那是**不用发新版就能救回全部已发布客户端**的唯一手段。

- **`SingleFile_*.zip` 只能由 CI 产出**：`dotnet-desktop-single-file.yml` 跑 `Release.py SingleFile`，那条路会先构建 AOT 助手（本机无 MSVC 编不出来）。本地要打单 exe 只能退化成 `python Build/Release.py SingleFile ExcludeElevator`（产物无助手）。
- 单文件/自包含都不会被**外部**更新器匹配（第 6 节）——单文件版靠的是另一条通道，不是「不参与自动更新」。
- 挂载命令示例：`gh release upload vX.Y.Z JASM_vX.Y.Z.7z SingleFile_JASM_vX.Y.Z.zip --repo qsy123-coder/JASM --clobber`。
- COS 上传用 [COSBrowser](https://cosbrowser.cloud.tencent.com) 拖拽或控制台（本机无 `coscli`）；传完**用浏览器打开 `…/app/update.json` 确认返回 JSON**，而不是被 COS 包了一层的 XML。

### 10. 提权助手 Elevator.exe 内嵌进主 exe

按键发送在「游戏以管理员身份运行」时唯一可行的路径是提权进程代发（UIPI 会静默丢弃「中 → 高」的 `SendInput`）。而单 exe 版磁盘上只有一个文件，助手没法作为散文件躺在旁边 —— 所以它被内嵌进主程序集，运行时释放到 `%LOCALAPPDATA%\JASM\Elevator.exe`。

- 内嵌：`GIMI-ModManager.WinUI.csproj` 的 `EmbeddedResource` + 显式 `LogicalName=JASM.Elevator.exe`（必须与 `ElevatorProvisioning.EmbeddedResourceName` 一致）。带 `Exists()` 条件：日常 `dotnet build` 没有助手产物，不能因此失败。
- 释放：`ElevatorProvisioner`（读内嵌资源 → 版本门控 → 临时文件 + `File.Move(overwrite)` 原子落盘）。标记文件 `Elevator.version` 存的是**主程序**版本，因此自更新换掉主 exe 后标记失配、下次启动自动重写助手 —— 助手版本永远跟着主程序走。
- 选路：`ElevatorProvisioning.Select` 按 FileVersion 取高者，**不能**简单的「同目录优先」——单 exe 用户的目录里可能残留旧 folder 安装留下的 `1.0.0.0` 助手（用户实机上就有），那样会盖掉能用的新助手。同目录那份只要 `SupportsKeySend` 就**一行都不写盘**（folder 版用户不该平白多出一个文件）。
- 版本标记只增不改：`ElevatorRefreshProtocol.MinimumFileVersionForTargetedRefresh = 2.0.0.0`、`ElevatorKeySendProtocol.MinimumFileVersionForKeySend = 3.0.0.0`、`ElevatorForegroundHandbackProtocol.MinimumFileVersionForForegroundHandback = 4.0.0.0`，对应 `Elevator.csproj` 的 `<FileVersion>`。
- **命令 `4` = 归还前台**（浮窗勾选刷新的收尾）：送键前必须先把游戏切到前台（F10 得打到前台窗口上），而游戏是提权运行时，送完键**浮窗自己再也拿不回前台** —— 前台锁只认「最近收到输入的那个进程」，那一刻的所有者是刚注入按键的助手；本进程既抢不回前台，注入解锁那一手又会被 UIPI 静默丢掉（实测）。所以只能请刚注入过的助手 `SetForegroundWindow`。载荷 `4` + 一句柄（与命令 `3` 同格式），回 `OK` / `FAIL:<reason>`；只在「光标压在浮窗上」时才发（键盘导航时用户人在游戏里，抢回来等于把他踢出游戏）。助手版本低于 `4.0.0.0` 时退回旧行为（前台留在游戏上）+ 一条 Warning。
- **助手是 AOT 发布，需要 MSVC**：只有 CI 的 `windows-latest` 编得动，本机 `dotnet publish ... /p:PublishProfile=FolderProfile.pubxml` 会报 `Platform linker not found`。因此**单 exe 包只能由 CI 产出**（第 9 节）。

## Claude 工作要求

1. **先理解再行动**：任何修改前，先梳理受影响模块，避免被局部问题误导。
2. **逐步推进**：一次只做一个功能 / 重构，不大范围改动。
3. **输出格式**：先给**变更计划**（影响文件清单）→ 再做**具体改动** → 最后给出**自检清单**（编译/格式/是否影响发布）。
4. **永远不要**：
   - 随意删除已有代码
   - 引入未在项目中使用的库 / 依赖
   - 忽略现有架构与命名约定
   - 生成不带注释的复杂逻辑
5. 复杂任务先用计划模式，长会话在 ~50% 上下文时手动 `/compact`。

## MCP 服务

- **Context7**：遇到第三方库（Windows App SDK / WinUI 3 / CommunityToolkit / Supabase 等）的 API 用法、配置、示例时，自动查实时文档，避免过时 API。

## 常用命令

```bash
dotnet build src/GIMI-ModManager.sln                          # 整个解决方案编译
dotnet build src/GIMI-ModManager.WinUI/GIMI-ModManager.WinUI.csproj  # 主应用编译
dotnet format --verify-no-changes                             # 格式 / lint (在 src/ 下；注意不是真门禁，见编码规范)
dotnet test src/JASM.Tests/JASM.Tests.csproj                  # 测试
python Build/Release.py                                       # folder 打包 (.7z, 含助手 + AutoUpdater) —— CI 用这条
python Build/Release.py SingleFile                            # 单 exe 打包 (.zip, 含内嵌助手) —— 本机会失败(需 MSVC)
python Build/Release.py SelfContained                         # 自包含打包 (.7z, 含内嵌助手)
# 本机逃生口（跳过 AOT 助手，产物送不了按键，只用于验证脚本其余部分）：
python Build/Release.py SingleFile ExcludeElevator

python Build/PackAppUpdate.py --version 2.31.0 \
    --folder-package JASM_v2.31.0.7z --single-file-package SingleFile_JASM_v2.31.0.zip
                                                              # 生成 app/update.json（上传 COS，见 docs/app-update-cos-setup.md）
```

## 提交前质量门禁

每次提交 / 合并到 `master` 前执行以下检查，CI 必须绿：

1. **编译**：`dotnet build` 必须成功。
2. **格式**：`dotnet format --verify-no-changes` —— 只在 `src/` 下跑，且承认它当前对 `master` 是红的（存量违规 + workflow 监听不存在的分支）。标准是**自己新写 / 改动的行零差异**。
3. **发布链路**：涉及发布时跑一次 `Release.py` 确认打包脚本不中断（`dotnet build` 通过≠发布能跑）。本机无 MSVC、无 7z，所以本机能验的只有「走到 `output/` 那一步 + 目录内容正确」，用 `python Build/Release.py SingleFile ExcludeElevator`；完整的 AOT + 压缩交给 CI。
4. **CI 验证**：push 后等待 GitHub Actions 通过；失败时读日志、修复、重新 push，直到绿。

**Claude 执行规范**：
- 每次 commit 前必须跑编译；有错必须先修。
- CI 失败若来自**未修改**的文件，视为既有问题，一并处理。
- push / 合并 / 发 release 前先征得用户同意；用户同意后等 CI 通过，确认成功后再说「完成」。
