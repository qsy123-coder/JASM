# Product Requirements Document: 浮窗体验调整（拖放 / 尺寸 / 按键映射）

**Version**: 1.0
**Date**: 2026-10-26
**Author**: Sarah (Product Owner)
**Quality Score**: 92/100

---

## Executive Summary

「浮窗 Mod 修复」上线后暴露出三处手感问题：修复页不能拖入新 Mod，与玩家「修完顺手装一个」的路径冲突；尺寸档位沿用旧值，新增的 520×660 修复页反而没有对应档位；按键映射面板只要某行成为唯一勾选就自动展开，装完新 Mod 还会自己弹开，挡住列表。

本次调整把这三处对齐到实际使用习惯：修复页也能拖放并自动回到 Mod 页、三档尺寸整体上移、按键映射默认折叠且不再自动展开。改动不涉及修复引擎、备份回滚与 F10 生效链路。

---

## Problem Statement

**Current Situation**
- 修复页为防误操作禁用了拖放，但玩家更常见的动作是「修完就地装一个新 Mod」；拖到修复页毫无反应。
- 尺寸为小 320×300 / 中 380×360 / 大 460×480，而修复页实际需要 520×660；大档名不副实。
- 按键映射面板在「唯一勾选」时自动展开，安装完成重建列表后新 Mod 成为唯一勾选，面板再次弹开，遮挡列表。

**Proposed Solution**
- 修复页接受拖放，Drop 时立即切回 Mod 切换页展示安装进度；修复运行中仍拒绝拖入。
- 尺寸改为小 380×360 / 中 460×480 / 大 520×660；首次进入修复页自动选大档，之后可自由切换。
- 按键映射面板默认折叠（保留标题行），任何重建/安装都不自动展开，必须用户点击。

**Business Impact**
- 减少「拖了没反应」的困惑与一次手动切页；
- 让尺寸档位与真实内容匹配，修复页不再需要专用隐藏尺寸；
- 列表不再被自动展开的面板顶走，切换 Mod 的路径更稳定。

---

## Success Metrics

- 在修复页拖入一个 Mod：100% 能安装，且界面在 1 秒内切到 Mod 切换页。
- 修复运行中拖入：100% 被拒绝，不产生任何文件写入。
- 三档尺寸分别严格等于 380×360 / 460×480 / 520×660（DIP）。
- 安装新 Mod 后按键映射面板保持折叠：0 次自动展开。

---

## User Stories & Acceptance Criteria

### Story 1: 修复页拖放

**As a** 浮窗玩家
**I want to** 在修复页直接把 Mod 拖进来
**So that** 不用先切页再拖

**Acceptance Criteria**
- [ ] 修复页拖入时显示与 Mod 页一致的接受光标与「自动识别角色」提示。
- [ ] Drop 后立即切到「Mod 切换」页，安装按现有流程继续；进度/成功状态在 Mod 页可见。
- [ ] 外部拖放通道（关 UAC 的机器）同样生效。
- [ ] 修复运行中拖入被拒绝，且不写入任何文件。
- [ ] 修复页的滚动/高亮守卫不受影响。

### Story 2: 三档尺寸

**As a** 浮窗玩家
**I want to** 三档尺寸与内容匹配
**So that** 「大」真的能放下修复页

**Acceptance Criteria**
- [ ] 小 = 380×360、中 = 460×480、大 = 520×660（DIP），下拉文案仍为「小 / 中 / 大」。
- [ ] 首次进入修复页自动选「大」；之后可手动切档，离开修复页保持当前档位。
- [ ] 标题栏尺寸下拉在修复页可用（不再禁用）。
- [ ] 窗口小于内容时可滚动，主按钮与底栏可到达。
- [ ] 首次自动选大只发生一次（持久化标记），之后不再覆盖用户选择。

### Story 3: 按键映射默认折叠

**As a** 浮窗玩家
**I want to** 按键映射默认收起
**So that** 列表不被面板顶走

**Acceptance Criteria**
- [ ] 唯一勾选的 Mod 仍显示「按键映射」标题行与展开箭头。
- [ ] 面板每次从隐藏变为显示时都是折叠状态。
- [ ] 安装新 Mod、重建列表、切换角色都不会自动展开。
- [ ] 用户点击标题行可展开/折叠；展开状态在该行保持显示期间保留。

---

## Functional Requirements

### F1. 拖放

- 移除修复页对 `AllowDrop` / `DragOver` / `Drop` / 外部通道的页签判断。
- 增加「修复运行中」判断：`FixSession.IsRunning` 时四层全部拒绝。
- Drop 成功路径先 `ActiveTabIndex = 0`，再走现有 `DropModPackageAsync`。
- 外部通道在 `CanAcceptDropAt` 与 `HandleExternalDropAsync` 同样处理。

### F2. 尺寸

- `SizeOptions` 改为 380×360 / 460×480 / 520×660。
- `OverlaySettings` 新增 `FixSizeInitialized`；首次进入修复页且未初始化时切到大档并落盘。
- 移除修复页专用 `RepairSizeOption` 与 `CurrentSizeOption` 的页签分支。
- 尺寸下拉在修复页可用。

### F3. 按键映射

- `SetKeyPanelVisible(true)` 不再设置 `IsKeyPanelExpanded = true`，改为 `false`。
- 折叠/展开只由用户点击 `ToggleKeyPanel` 改变。
- 安装、刷新、切角色重建行对象时自然保持折叠。

### Out of Scope

- 修复引擎、备份回滚、F10 链路；
- 拖放安装的目标角色选择逻辑；
- 新增尺寸档位或让窗口可自由拉伸。

---

## Technical Constraints

- 复用现有拖放通道与 `DropModPackageAsync`，不新建安装路径。
- 尺寸变化仍需 `ReassertSize + KeepOnScreen` 收口。
- 修复运行中页签与角色仍锁定（`CanSwitchTabs/CanSwitchCharacter`），拖放拒绝与之保持一致。
- 按键面板解析逻辑不变，只改展开时机。

---

## Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| Drop 切页时安装事件仍在修复页上下文 | Low | Med | 先切页再 await 安装；安装服务本身与页面无关 |
| 小档 380 宽下修复页控件拥挤 | Med | Low | 整页 ScrollViewer；文字换行；后续可再压缩按钮 |
| 首次自动大档覆盖用户偏好 | Low | Low | 只初始化一次并持久化，之后尊重手动选择 |

---

## References

- `docs/overlay-mod-fixer-prd.md`（v1.1 尺寸条款已同步）
- `docs/wuwa-overlay-mod-switcher-prd.md`

---

*This PRD was created through interactive requirements gathering with quality scoring.*
