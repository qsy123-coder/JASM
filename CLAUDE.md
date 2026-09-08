# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This is a best practices repository for Claude Code configuration, demonstrating patterns for skills, subagents, hooks, and commands. It serves as a reference implementation rather than an application codebase.

## Key Components

### Weather System (Example Workflow)
A demonstration of two distinct skill patterns via the **Command → Agent → Skill** architecture:
- `/weather-orchestrator` command (`.claude/commands/weather-orchestrator.md`): Entry point — asks user for C/F, invokes agent, then invokes SVG skill
- `weather-agent` agent (`.claude/agents/weather-agent.md`): Fetches temperature using its preloaded `weather-fetcher` skill (agent skill pattern)
- `weather-fetcher` skill (`.claude/skills/weather-fetcher/SKILL.md`): Preloaded into agent — instructions for fetching temperature from Open-Meteo
- `weather-svg-creator` skill (`.claude/skills/weather-svg-creator/SKILL.md`): Skill — creates SVG weather card, writes `orchestration-workflow/weather.svg` and `orchestration-workflow/output.md`

Two skill patterns: agent skills (preloaded via `skills:` field) vs skills (invoked via `Skill` tool). See `orchestration-workflow/orchestration-workflow.md` for the complete flow diagram.

### Skill Definition Structure
Skills in `.claude/skills/<name>/SKILL.md` use YAML frontmatter:
- `name`: Display name and `/slash-command` (defaults to directory name)
- `description`: When to invoke (recommended for auto-discovery)
- `argument-hint`: Autocomplete hint (e.g., `[issue-number]`)
- `disable-model-invocation`: Set `true` to prevent automatic invocation
- `user-invocable`: Set `false` to hide from `/` menu (background knowledge only)
- `allowed-tools`: Tools allowed without permission prompts when skill is active
- `model`: Model to use when skill is active
- `context`: Set to `fork` to run in isolated subagent context
- `agent`: Subagent type for `context: fork` (default: `general-purpose`)
- `hooks`: Lifecycle hooks scoped to this skill

### Presentation System
See `.claude/rules/presentation.md` — presentation work is delegated per-presentation to `presentation-vibe-coding` (for `presentation/vibe-coding-to-agentic-engineering/`) or `presentation-claude-gemini` (for `presentation/2026-04-25-gdg-kolachi-cli-claude-code-gemini/`).

### Hooks System
Cross-platform sound notification system in `.claude/hooks/`:
- `scripts/hooks.py`: Main handler for Claude Code hook events
- `config/hooks-config.json`: Shared team configuration
- `config/hooks-config.local.json`: Personal overrides (git-ignored)
- `sounds/`: Audio files organized by hook event (generated via ElevenLabs TTS)

Hook events configured in `.claude/settings.json`: PreToolUse, PostToolUse, UserPromptSubmit, Notification, Stop, SubagentStart, SubagentStop, PreCompact, SessionStart, SessionEnd, Setup, PermissionRequest, TeammateIdle, TaskCompleted, ConfigChange.

Special handling: git commits trigger `pretooluse-git-committing` sound.

## Critical Patterns

### Subagent Orchestration
Subagents **cannot** invoke other subagents via bash commands. Use the Agent tool (renamed from Task in v2.1.63; `Task(...)` still works as an alias):
```
Agent(subagent_type="agent-name", description="...", prompt="...", model="haiku")
```

Be explicit about tool usage in subagent definitions. Avoid vague terms like "launch" that could be misinterpreted as bash commands.

### Subagent Definition Structure
Subagents in `.claude/agents/*.md` use YAML frontmatter:
- `name`: Subagent identifier
- `description`: When to invoke (use "PROACTIVELY" for auto-invocation)
- `tools`: Comma-separated allowlist of tools (inherits all if omitted). Supports `Agent(agent_type)` syntax
- `disallowedTools`: Tools to deny, removed from inherited or specified list
- `model`: Model alias: `haiku`, `sonnet`, `opus`, or `inherit` (default: `inherit`)
- `permissionMode`: Permission mode (e.g., `"acceptEdits"`, `"plan"`, `"bypassPermissions"`)
- `maxTurns`: Maximum agentic turns before the subagent stops
- `skills`: List of skill names to preload into agent context
- `mcpServers`: MCP servers for this subagent (server names or inline configs)
- `hooks`: Lifecycle hooks scoped to this subagent (all hook events are supported; `PreToolUse`, `PostToolUse`, and `Stop` are the most common)
- `memory`: Persistent memory scope — `user`, `project`, or `local` (see `reports/claude-agent-memory.md`)
- `background`: Set to `true` to always run as a background task
- `effort`: Effort level override: `low`, `medium`, `high`, `max` (default: inherits from session)
- `isolation`: Set to `"worktree"` to run in a temporary git worktree
- `color`: CLI output color for visual distinction

### Configuration Hierarchy
1. **Managed** (`managed-settings.json` / MDM plist / Registry): Organization-enforced, cannot be overridden
2. Command line arguments: Single-session overrides
3. `.claude/settings.local.json`: Personal project settings (git-ignored)
4. `.claude/settings.json`: Team-shared settings
5. `~/.claude/settings.json`: Global personal defaults
6. `hooks-config.local.json` overrides `hooks-config.json`

### Disable Hooks
Set `"disableAllHooks": true` in `.claude/settings.local.json`, or disable individual hooks in `hooks-config.json`.

## Answering Best Practice Questions

When the user asks a Claude Code best practice question, **always search this repo first** (`best-practice/`, `reports/`, `tips/`, `implementation/`, and `README.md`) before relying on training knowledge or external sources. This repo is the authoritative source — only fall back to external docs or web search if the answer is not found here.

## Workflow Best Practices

From experience with this repository:

- Keep CLAUDE.md under 200 lines per file for reliable adherence
- `.claude/rules/*.md` with `paths:` YAML frontmatter are lazy-loaded only when Claude touches matching files; without frontmatter they load into every session like CLAUDE.md
- Use commands for workflows instead of standalone agents
- Create feature-specific subagents with skills (progressive disclosure) rather than general-purpose agents
- Perform manual `/compact` at ~50% context usage
- Start with plan mode for complex tasks
- Use human-gated task list workflow for multi-step tasks
- Break subtasks small enough to complete in under 50% context

### Debugging Tips

- Use `/doctor` for diagnostics
- Run long-running terminal commands as background tasks for better log visibility
- Use browser automation MCPs (Claude in Chrome, Playwright, Chrome DevTools) for Claude to inspect console logs
- Provide screenshots when reporting visual issues

## Git Commit Rules

When committing changes, **create separate commits per file**. Do NOT bundle multiple file changes into a single commit. Each file gets its own commit with a descriptive message specific to that file's changes.

For example, if `README.md`, `best-practice/claude-subagents.md`, and a skill file all changed:
- Commit 1: `git add README.md` → commit with README-specific message
- Commit 2: `git add best-practice/claude-subagents.md` → commit with subagents-doc-specific message
- Commit 3: `git add .claude/skills/weather-fetcher/SKILL.md` → commit with skill-specific message

This makes the git history cleaner and easier to review, revert, or cherry-pick individual changes.

## JASM Build & Release Verification

Before pushing code, always verify the full CI pipeline locally — `dotnet build` alone is NOT enough:

1. **C# 编译**: `dotnet build src/GIMI-ModManager.WinUI/GIMI-ModManager.WinUI.csproj`
2. **Python 发布脚本**: `python Build/Release.py ExcludeElevator` — 验证打包流程不会因转义字符、路径问题中断
3. **分支名匹配**: CI workflow 监听 `master`（不是 `main`），确保 `.github/workflows/*.yml` 中的分支名与仓库一致
4. **GitHub Actions 启用**: fork 仓库默认禁用 Actions，需手动去 Actions 页开启

常见坑:
- Python 3.12+ 对 `\P` `\d` 等非法转义报 SyntaxWarning，路径用正斜杠 `/`，正则用原始字符串 `r""`
- `dotnet publish` 不加 `-o` 时输出到 TFM 子目录，与脚本期望的路径不匹配
- workflow `branches:` 过滤器不匹配会导致 push 不触发 CI

## JASM 自动更新 / Release 发布链路

> 本节记录 Mod 市场改造 + 自动更新踩过的坑，涉及 `UpdateChecker`、`JASM.AutoUpdater`、`release-please.yml`、`dotnet-desktop*.yml`。

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
