# CLAUDE.md

本文件是 **JASM (Just Another Skin Manager)** 仓库的项目上下文与工程规范，供 Claude Code 在此仓库中工作时遵循。

> 注意：本仓库**不是**「Claude Code 最佳实践参考仓库」。仓库内没有 `.claude/`、`best-practice/`、`skills/` 目录。此前模板版 CLAUDE.md 里的 Weather System、Subagent/Skill/Hooks 定义均不适用于此处，已清理。

## 项目概述

- **项目**：JASM - Just Another Skin Manager，一个 WinUI 3 桌面应用，用于管理游戏（原神等）的 Mod / 皮肤。
- **fork 关系**：本仓库是 `Jorixon/JASM` 的 fork → `qsy123-coder/JASM`，默认分支为 `master`（不存在 `main` 分支）。
- **自动更新**：检测 + 手动点更新，主 app 用 `UpdateChecker`，更新执行体是独立的 `JASM - Auto Updater.exe`。

## 技术栈

- **框架**：.NET 9 (`net9.0-windows10.0.22621.0`) + Windows App SDK 1.7 (WinUI 3)，`Nullable` / `ImplicitUsings` 均开启。
- **MVVM**：CommunityToolkit.Mvvm 8.4（`[ObservableProperty]` / `[RelayCommand]` 源生成器）。
- **UI**：CommunityToolkit.WinUI.*（Segmented / SettingsControls 等）、WinUIEx、WinUI3Localizer。
- **日志**：Serilog（File / EventLog / Debug sink）。
- **数据**：Newtonsoft.Json、Supabase / PostgREST（Mod 市场后端）。
- **其他**：FluentValidation、Polly（重试/限流）、OneOf、WindowsDisplayAPI。
- **构建**：`Build/Release.py`（发布打包）、`Build/PackGameData.py`（游戏数据打包）。

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
├── Release.py                  # 发布脚本 (ExcludeElevator / SingleFile / SelfContained)
└── PackGameData.py             # 游戏数据打包
.github/workflows/
├── dotnet-desktop.yml           # 默认打包 (ExcludeElevator) → artifact
├── dotnet-desktop-self-contained.yml  # SelfContained → artifact
├── dotnet-format.yml            # dotnet format --verify-no-changes (监听 main, 不会触发)
└── release-please.yml           # 版本管理 + 开 release PR (监听 master)
```

## 编码规范

- 遵循 `src/.editorconfig`；格式门禁为 `dotnet format --verify-no-changes`。
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
2. **Python 发布脚本**：`python Build/Release.py ExcludeElevator` —— 验证打包流程不会因转义字符、路径问题中断
3. **分支名匹配**：CI workflow 监听默认分支，确保 `.github/workflows/*.yml` 中的分支名与仓库一致（本仓库是 `master`；`dotnet-format.yml` / `release-please.yml` 若有 `main` 需改为 `master`，否则永不触发）
4. **GitHub Actions 启用**：fork 仓库默认禁用 Actions，需手动去 Actions 页开启

常见坑：
- Python 3.12+ 对 `\P` `\d` 等非法转义报 SyntaxWarning，路径用正斜杠 `/`，正则用原始字符串 `r""`
- `dotnet publish` 不加 `-o` 时输出到 TFM 子目录，与脚本期望的路径不匹配
- workflow `branches:` 过滤器不匹配会导致 push 不触发 CI
- **本地构建前先关闭运行中的 JASM 进程**，避免 exe/dll 被锁定导致编译或打包失败

## JASM 自动更新 / Release 发布链路

> 记录自动更新 + 发布踩过的坑，涉及 `UpdateChecker`、`JASM.AutoUpdater`、`release-please.yml`、`dotnet-desktop*.yml`。

### 1. 更新检测与下载源都写死 GitHub 仓库

- 主 app：`src/GIMI-ModManager.WinUI/Services/AppManagement/Updating/UpdateChecker.cs:23` 的 `ReleasesApiUrl`
- AutoUpdater：`src/JASM.AutoUpdater/MainPageVM.cs` 的 `:387`（下载 zip 的 API）、`:34` / `:171`（浏览器与回退链接）

`qsy123-coder/JASM` 是 `Jorixon/JASM` 的 fork，**4 处都要改成自己的 fork repo**，否则用户端检测不到、更新器也下不了包。

### 2. 版本号由 release-please 管理

`GIMI-ModManager.WinUI.csproj` 的 `<VersionPrefix>` 被 `<!-- x-release-please-start-version -->` / `end` 标记包住，release-please 用它自动 bump。**确保 `release-please.yml` 的 `on.push.branches` 匹配默认分支**（本项目是 `master`，不是 `main`——`main` 分支不存在，配错了 workflow 永不触发）。

### 3. release-please 需要两个权限开关

Settings → Actions → General → Workflow permissions 下要**同时**勾「Read and write permissions」和「Allow GitHub Actions to create and approve pull requests」。只勾前者会报 `release-please failed: GitHub Actions is not permitted to create or approve pull requests`——报错在「开 release PR」那步，之前的版本 bump / 分支创建其实都成功了。

### 4. 构建 workflow 只传 artifact，不挂 release

`dotnet-desktop.yml` / `dotnet-desktop-self-contained.yml` 最后都是 `actions/upload-artifact`，**不会把 zip 挂到 GitHub release**。release 的 asset 要手动挂（或自己加 `gh release upload` 步骤）。

### 5. 单文件模式没有 AutoUpdater ⇒ 无法自动更新

- `Release.py` 在 `SingleFile` / `SelfContained` 模式跳过构建 AutoUpdater，且只复制单 exe。
- 自动更新执行体是 `JASM - Auto Updater.exe`（独立更新器进程），**单文件安装完根本没有它** → 检测到新版徽标能亮，但点更新 `StartSelfUpdateProcess()` 会失败。
- **自动更新只在 folder 版（`JASM_v*.7z`，即 `Release.py ExcludeElevator` 默认模式，含更新器）成立。**

### 6. AutoUpdater 只认 `JASM_` 开头的 asset

`MainPageVM.cs:160`：`.FirstOrDefault(a => a.name?.StartsWith("JASM_") ?? false)`。`SingleFile_JASM_*.zip` / `SelfContained_JASM_*.7z` 都**不匹配**，给自动更新用的 asset 必须是 `JASM_*` 命名。

### 7. 发布两步走 & 更新机制

release-please 建 release（tag `vX.Y.Z`）→ 手动挂 `JASM_vX.Y.Z.7z` 上去 → 老用户手动下一次 folder 版 → 之后 folder 用户自动更新。机制是「提示 + 手动点」，不是静默推：UpdateChecker 每 2h 查 GitHub releases，比对 `tag_name`（去 `v`）与编译版本，只有 `CurrentVersion < latest` 才亮徽标；用户点更新才拉起 AutoUpdater 下载替换。升级必须 bump `<VersionPrefix>` **且** release tag 用同一版本，否则 `==` 不触发。

### 8. 打包映射速查

| `Release.py` 模式 | 产物命名 | 含 AutoUpdater | 自动更新 |
|---|---|---|---|
| 默认 / `ExcludeElevator` | `JASM_v*.7z` | ✅ | ✅ |
| `SingleFile` | `SingleFile_JASM_v*.zip` | ❌ | ❌ |
| `SelfContained` | `SelfContained_JASM_v*.7z` | ❌ | ❌ |

### 9. 发布时要挂载的多形态产物

release 的 asset 不会自动挂（见第 4 节），**每次手动挂载，把面向不同用户的分发形态都传上去**：

| 产物 | 用途 | 是否必须 |
|---|---|---|
| `JASM_vX.Y.Z.7z` | folder 版，含 AutoUpdater，**自动更新的唯一对象** | ✅ 必须 |
| `SingleFile_JASM_vX.Y.Z.zip` | 单 exe 便携版，无需安装/不参与自动更新，给不想自动更新的用户 | ✅ 一并上传 |
| `SelfContained_JASM_v*.7z` | 自包含版 | 按需 |

- **CI 不构建 `SingleFile`**：`dotnet-desktop.yml` 是 `ExcludeElevator`，`dotnet-desktop-self-contained.yml` 是 SelfContained，**没有 workflow 会产出 `SingleFile_*.zip`**。想要单 exe，需本地跑 `python Build/Release.py SingleFile`（会写 `GITHUB_ENV`，本地需先设临时环境变量否则脚本 `exit(1)`）。
- AutoUpdater 只认 `JASM_` 前缀（见第 6 节），单文件/自包含都不会被自动更新器匹配，二者只是「给手动下载的用户」的分发形态 —— 不影响自动更新。
- 挂载命令示例：`gh release upload vX.Y.Z SingleFile_JASM_vX.Y.Z.zip --repo qsy123-coder/JASM --clobber`。

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
dotnet format --verify-no-changes                             # 格式 / lint 门禁 (在 src/ 下)
dotnet test src/JASM.Tests/JASM.Tests.csproj                  # 测试
python Build/Release.py ExcludeElevator                       # 默认 folder 打包 (.7z, 含 AutoUpdater)
python Build/Release.py SingleFile                            # 单 exe 打包 (.zip, 无 AutoUpdater)
python Build/Release.py SelfContained ExcludeElevator         # 自包含打包 (.7z, 无 AutoUpdater)
```

## 提交前质量门禁

每次提交 / 合并到 `master` 前执行以下检查，CI 必须绿：

1. **编译**：`dotnet build` 必须成功。
2. **格式**：`dotnet format --verify-no-changes` 零差异。
3. **发布链路**：涉及发布时跑 `python Build/Release.py ExcludeElevator` 确认打包脚本不中断（`dotnet build` 通过≠发布能跑）。
4. **CI 验证**：push 后等待 GitHub Actions 通过；失败时读日志、修复、重新 push，直到绿。

**Claude 执行规范**：
- 每次 commit 前必须跑编译；有错必须先修。
- CI 失败若来自**未修改**的文件，视为既有问题，一并处理。
- push / 合并 / 发 release 前先征得用户同意；用户同意后等 CI 通过，确认成功后再说「完成」。
