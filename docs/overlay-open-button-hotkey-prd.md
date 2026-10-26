# Product Requirements Document: 概览页打开浮窗与自定义快捷键

**Version**: 1.0
**Date**: 2026-10-26
**Author**: Sarah (Product Owner)
**Quality Score**: 92/100

---

## Executive Summary

浮窗以前只能靠全局热键唤出：热键被别的程序占用时，用户在主窗口里没有任何入口；想换键也只能改代码。本次在 JASM 角色概览页顶部加「打开浮窗」按钮（仅鸣潮显示），并在设置页「浮窗」区增加组合键录制：按下新组合即保存并立即生效，被占用时保留旧键并提示。

---

## Problem Statement

- 热键注册失败（被占/系统拒绝）时，浮窗对所有用户操作都不可达。
- 默认候选表写死，用户无法按自己的习惯修改。
- 从主窗口进入浮窗需要切回游戏按热键，路径不直观。

---

## User Stories & Acceptance Criteria

### Story 1: 概览页打开浮窗

**As a** 鸣潮玩家
**I want to** 在主窗口角色概览页直接打开浮窗
**So that** 不用切回游戏按热键

**Acceptance Criteria**
- [ ] 角色概览页顶部工具栏、Refresh Mods 旁边出现「打开浮窗」。
- [ ] 只在选中游戏为鸣潮时显示；其它游戏不显示。
- [ ] 点击后浮窗按上次位置/尺寸/角色显示，行为与热键唤出一致。
- [ ] 热键被占用时按钮仍可用。
- [ ] 浮窗尚未初始化时按钮会先初始化再显示；失败时给通知。

### Story 2: 自定义唤出快捷键

**As a** 玩家
**I want to** 把浮窗热键改成我习惯的组合
**So that** 不和其它软件/游戏冲突

**Acceptance Criteria**
- [ ] 设置页「浮窗」区显示当前实际生效的键。
- [ ] 点「录制快捷键」弹出录制对话框，按下组合键即录入；必须含至少一个 Ctrl/Alt/Shift/Win；Esc 取消。
- [ ] 新键先探测再切换：被占用时不改设置、旧键继续生效，并显示原因。
- [ ] 成功后续存到 `OverlaySettings`（修饰键、虚拟键、显示名），重启后仍生效。
- [ ] 自定义键在启动时被占用：按默认候选表兜底，并提示「自定义键被占用，当前临时使用 X」。
- [ ] 当前游戏不是鸣潮时允许保存，提示「切换到鸣潮后生效」。

### Out of Scope

- 截图热键（Ctrl+Alt+S）的自定义；
- 多套快捷键配置/导入导出；
- 鼠标侧键等非键盘组合。

---

## Functional Requirements

- 按钮：`CharactersPage.xaml` 工具栏，`CharactersViewModel.OpenOverlayCommand` → `OverlayWindowService.ShowOverlayAsync()`。
- 服务：`ShowOverlayAsync` 在 UI 线程初始化并显示；不依赖热键注册成功。
- 热键设置：`OverlaySettings.OverlayHotkeyModifiers / OverlayHotkeyVirtualKey / OverlayHotkeyKeyName`。
- 注册：启动时候选自定义键 → 默认候选；设置页改键时先 `RegisterHotKey` 探测，成功才拆旧键重建注册者，失败恢复旧键。
- 录制：设置页 ContentDialog + 只读 TextBox 捕获 KeyDown；`InputKeyboardSource` 读修饰键状态。

---

## Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| 录制时按键被输入法/系统吞掉 | Med | Med | 只读 TextBox + 修饰键状态查询；Esc 退出 |
| 改键瞬间旧键被注销 | Low | Low | 先探测可用再拆；失败恢复 |
| 自定义键被占用导致无热键 | Low | Med | 启动时默认候选兜底 + 设置页提示；概览页按钮兜底 |

---

## References

- `docs/overlay-mod-fixer-prd.md`
- `docs/overlay-ux-adjustments-prd.md`
