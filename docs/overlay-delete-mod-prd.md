# Product Requirements Document: 浮窗删除 Mod

**Version**: 1.0
**Date**: 2026-10-26
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

---

## Executive Summary

浮窗目前只能切换 Mod，想删掉一件失效或不再想要的 Mod 必须回主窗口。本次在 Mod 列表每行右侧加一个垃圾桶图标，点击后在**浮窗内部**弹确认卡，确认后把整个 Mod 文件夹移至 Windows 回收站，并从列表移除；启用中的 Mod 删除后自动发 F10 让游戏恢复原版模型。

浮窗是 `WS_EX_NOACTIVATE` 窗口，主窗口的 `ContentDialog` 会激活主窗口并把游戏踢出前台，所以确认提示必须留在浮窗内。

---

## Problem Statement

**Current Situation**
- 浮窗只能开关 Mod，删除要回主窗口，游戏内操作中断。
- 主窗口删除对话框会改变前台归属，不适合浮窗场景。

**Proposed Solution**
- 每行右侧常驻垃圾桶图标。
- 浮窗内覆盖式确认卡：Mod 名、回收站说明、取消/移至回收站。
- 复用 `DeleteModBySkinEntryId(id, moveToRecycleBin: true)`；删除后刷新列表，启用中的 Mod 自动 F10。

---

## User Stories & Acceptance Criteria

### Story 1: 行内删除入口

**As a** 浮窗玩家
**I want to** 在 Mod 行上直接删除
**So that** 不用切回主窗口

**Acceptance Criteria**
- [ ] 每行右侧有垃圾桶图标，悬停提示「删除 Mod（移至回收站）」。
- [ ] 点击图标不切换勾选状态、不触发按键面板。
- [ ] 修复任务运行中点击：不弹卡，显示「修复进行中，暂不能删除 Mod」。

### Story 2: 确认卡

**As a** 浮窗玩家
**I want to** 删除前看到明确提示
**So that** 不会误删

**Acceptance Criteria**
- [ ] 点击后浮窗内出现半透明遮罩 + 卡片，显示 Mod 名与「整个 Mod 文件夹会移至 Windows 回收站，可从回收站恢复」。
- [ ] 卡片覆盖整个浮窗，背后控件不可点；全局导航热键同时失效。
- [ ] 「取消」关闭卡片且不动盘；「移至回收站」执行删除，执行中按钮禁用并显示「正在删除…」。
- [ ] 删除确认卡打开时，拖放被拒绝。

### Story 3: 删除结果

**As a** 浮窗玩家
**I want to** 删除后列表与游戏状态都跟上
**So that** 不会看到幽灵 Mod

**Acceptance Criteria**
- [ ] 成功后列表/角色下拉刷新，出现绿色「已删除「名称」（已移至回收站）」。
- [ ] 被删的是启用中的 Mod 时自动发 F10；禁用中的不刷新游戏。
- [ ] 失败时显示「删除失败：原因」，并重新对一次真实列表。
- [ ] 删除角色最后一个 Mod 时，该角色从浮窗角色下拉中消失。
- [ ] 预设里的条目**不处理**（本次范围外）。

---

## Functional Requirements

- 入口：`OverlayWindow.xaml` 行模板第 7 列垃圾桶按钮，`Tag` 传行 VM，代码后置调用 `RequestDeleteMod`。
- 确认：`OverlayViewModel.Delete.cs` 的 `RequestDeleteMod / CancelDeleteCommand / ConfirmDeleteCommand`。
- 删除：`Task.Run(() => modList.DeleteModBySkinEntryId(id, moveToRecycleBin: true))`。
- 刷新：`RefreshCharacters()` + `RefreshFixTargets()`；启用中的 Mod 再 `RefreshCoordinator.RequestRefreshAsync()`。
- 并发：`FixSession.IsRunning` 时拒绝；删除卡打开时拒绝拖放与导航热键。

### Out of Scope

- 多选批量删除；
- 从预设移除条目；
- 永久删除选项；
- 删除 Mod 的关联设置（配置在 Mod 文件夹内，随文件夹一起走）。

---

## Technical Constraints

- 不使用 ContentDialog / 新窗口，确认卡必须是浮窗内 Grid。
- 删除在后台线程执行；可观察属性与 F10 回 UI 线程。
- 删除卡是最后声明的子元素，覆盖 7 行，背景吃掉指针。

---

## Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| 小档 380 宽下 7 列拥挤 | Med | Low | 垃圾桶 24px、无边框；后续可缩图标 |
| 回收站删除慢/被占用 | Low | Med | 按钮显示正在删除，失败给原因并刷新 |
| 删除后预设残留 | High | Low | 已知范围外；后续单独清理 |

---

## References

- `docs/overlay-mod-fixer-prd.md`
- `docs/overlay-ux-adjustments-prd.md`
