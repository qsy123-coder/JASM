# Mod 游戏内切换菜单 — 调试日志

**日期**：2026-11-07
**状态**：Phase 0 未通过，暂停实现，等待新的验证结论
**相关 PRD**：`docs/mod-ingame-menu-prd.md`

---

## 1. 目标

给每个已安装 Mod 生成一个**游戏内可点击**的切换菜单（类截图中的 3DMigoto 菜单），点击立即生效、不触发 F10、不跨进程。

## 2. 已实现（代码在仓库里，未提交）

`src/GIMI-ModManager.Core/Entities/Mods/InGameMenu/`

| 文件 | 作用 |
|---|---|
| `ModIniToggleRow.cs` | 一行菜单的数据模型（段落/变量/值列表/namespace/标签） |
| `ModIniToggleParser.cs` | 解析 `[Key*]` 的 `type` / `$变量 = 值` / 注释标签，支持 GBK、裸按键行、跨命名空间 |
| `ClickableMenuDetector.cs` | 判定 Mod 是否已有可点击菜单（`[Present]` + 光标变量 + 鼠标键） |
| `InGameMenuIniBuilder.cs` | 生成 `JASM_Menu.ini`（F8/组合键、分页、悬停、点击循环、拖动、调试红点） |
| `IniFileText.cs` | UTF-8 / UTF-16 / GB18030 编码探测 |
| `GdiTextRasterizer.cs` | GDI 文字 → 位图（零新增依赖） |
| `MenuTextureFactory.cs` | 程序化面板/悬停/纯色块 |
| `DdsWriter.cs` | 未压缩 BGRA32 DDS 写出 |
| `InGameMenuGenerator.cs` | 生成/移除/manifest/幂等 |

测试：`src/JASM.Tests/` 下 32 个单测全部通过（解析、检测、生成、分页、DDS、if-endif 配平、引用自洽、CustomShader 段落）。

## 3. 当前磁盘产物

只生成在 **RabbitFX 前置**（常驻、1 行）这一个 Mod 里：

```
F:\XXMI\WWMI\Mods\character\others\RabbitFX反虚化+发光前置v82自定义版(内附说明）\Custom RabbitFX v8.2\
├── JASM_Menu.ini
└── JASM_Menu\   (panel/hover/title/hint/label/key/value/page/cursor + draw_2d.hlsl + manifest)
```

当前诊断版配置：
- 呼出键：**F8**
- `DebugCursor = true`（16×16 红点画在计算出的鼠标位置）
- `$jasm_*_open` / `page` 非 persist；`x/y` persist
- `[Present]` 首帧强制 `open = 0`

心月狐那件的菜单已移除。

## 4. 验证结果一览

| 项目 | 结果 | 说明 |
|---|---|---|
| ini 解析 / 生成 / 纹理 / shader 编译 | ✅ | 离线用 XXMI 自带 `d3dcompiler_47.dll` 编译 VS/PS 均通过 |
| 菜单能画出来 | ✅ | 面板、标题、标签都正常显示 |
| 按键段能注册 | ✅ | **F8 可以开关菜单** |
| 启动时不自动显示 | ❌ | 首帧归零后仍然自动弹出 |
| 鼠标悬停命中 | ❌ | 红点不跟随鼠标移动 |
| 点击切换 Mod 变量 | ❌ | 点击行后 RabbitFX 的 `$censor` 不变，效果一直是「反虚化」 |

## 5. 问题时间线

### 5.1 第一次：游戏崩溃（已解决）

- 现象：进游戏约 37 秒后崩溃。
- 证据：`Client\Saved\Crashes\UE4CC-Windows-E548DCEE443BC7587A67588EACEB16DF_0001\CrashContext.runtime-xml`
  - `CrashType: GPUCrash`
  - `Fatal error: ... D3D device being lost. (Error: 0x887A0006 - 'HUNG')`
- 根因：`vs` / `ps` / `blend` / `topology` / `Draw` 被写进了 `[CommandList]`。这些属性只属于 `[CustomShader*]`。
- 修复：绘制段改为 `[CustomShaderJasmQuad_<token>]`；新增单测 `DrawSectionIsCustomShaderNotCommandList` 钉死。
- 结果：不再崩溃，菜单能显示。

### 5.2 第二次：能显示但点不了

- 现象：菜单出现，鼠标悬停无高亮，点击无反应。
- 诊断：生成带红色光标标记的版本；用户回报「红点不跟」。
- 结论（待确认）：`cursor_x` / `cursor_y` 在鸣潮正常游玩时**不更新**（系统光标被游戏锁定/隐藏），因此 `$jasm_mx/my` 是固定值，永远落不进行命中框。
- 影响：鼠标点击这条路在当前游戏状态下可能不可用，需要改成键盘导航或确认光标只在 ESC/背包界面可用。

### 5.3 第三次：Ctrl+Shift+M 无反应

- 换成单键 `F8` 后**可以开关菜单** → 按键段注册没问题。
- 说明问题在组合键 `CTRL SHIFT VK_M` 的写法或游戏占用，不是 ini 加载。
- **注意**：这一条与 5.4 的「自动弹出」混在一起时，容易误判成按键失效。

### 5.4 第四次：菜单启动即显示（未解决）

- 现象：进游戏/F10 后菜单自己出现，不需要按键。
- 已尝试的修复（均无效）：
  1. `$open` 从 `persist` 改为普通 `global`；
  2. `[Constants]` 移到按键段之后（参考实现的顺序）；
  3. `[Present]` 首帧强制 `$open = 0`。
- 当前判断：**菜单的绘制可能没有真正被 `if $jasm_open == 1` 门控**，或 `$jasm_open` 每帧被别的东西改回 1。

### 5.5 第五次：点击无法切换 Mod

- 用户回报：RabbitFX 一直是「反虚化」，菜单切换不了。
- 与 5.2 一致：鼠标没有命中行，所以点击逻辑从未执行。
- 另外尚未单独验证：即使命中，`$\RabbitFX\censor` 的跨命名空间写入是否生效（参考实现证明语法存在，但本 Mod 尚未实测）。

### 5.6 自动化实机测试（2026-11-07 22:06–22:30）

不再依赖用户手测：用管理员权限脚本启动游戏、`SendInput` 发键、`PrintWindow` 抓游戏画面。

关键发现：

1. **GDI 截屏抓不到游戏画面**（全屏 flip-model / MPO），必须用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`。
2. **F10 不会重载 ini**：
   - 把面板宽度从 560 改成 300 → 按 F10 后画面仍是 560；
   - 冷启动后变成 300，证明文件本身会被加载，只是 F10 不重读。
   - 结论：之前所有「F10 后还是一样」的测试都无效，必须冷启动。
3. **冷启动后，菜单在标题画面就已经显示**：
   - 此时 ini 已改成 `$jasm_open = 0`（单值）、呼出键段落已改名禁用、`[Constants]` 也是 0；
   - 菜单仍然绘制 → 说明 `if $jasm_open == 1` 门控没有按预期生效（或变量被赋成 1）。
4. **红色光标标记从未出现**：`cursor_x/y` 在正常游玩时取不到有效位置，鼠标命中方案不可行。
5. 点击测试期间 `$\rabbitfx\censor` 从 1 变 0（可能是用户之前手动切换，也可能是某次点击命中）；尚未在可控条件下复现。

待完成的关键实验（进行中）：把 `[Present]` 里的门控改成恒假（`if 0` / `if $nonexistent == 1`），冷启动看菜单是否消失：
- 消失 → `if` 正常，问题在 `$jasm_open` 的值；
- 仍显示 → `if` 在 `[Present]` 中没有生效，需要换一种绘制触发方式。

### 5.7 `if` 门控失效的进一步实验（2026-11-07 22:30–22:50）

- `if 0` 包裹整段绘制 → 菜单仍显示（但 `if 0` 可能本身非法，结论不充分）。
- 变量名从 `jasm_46587dbc_*` 简化为 `jasm_*` → 仍显示。
- 呼出键加 `condition = $jasm_ready == 1`（加载时不成立）→ 仍显示。
- `[Present]` 只留 `run = CommandListJasmEntry_<token>`，把 `if` 全部移进 CommandList → 仍显示。
- 文件加 `namespace`（放在最前）→ 本次测试因加载时机作废，待重测。
- 打开 3DMigoto 日志（`log_level = info`、`unbuffered = 1`、`show_warnings = 1`）后，游戏左上角出现**红色解析警告**；用户要求静音，已把 `show_warnings` 改回 0，改用日志文件 `F:\XXMI\WWMI\d3d11_log.txt` 读取具体报错。
- 注意：XXMI Launcher 会在启动时按自己的配置重写 `d3dx.ini`，所以日志设置同时写进了 `XXMI Launcher Config.json` 的 `Importers.WWMI.Migoto`。

下一步：冷启动后读取 `d3d11_log.txt`，按解析器报错定位 `if` 失效的真实原因（未知变量 / 未配对 if / 段落覆盖）。

### 5.8 根因确认与修复（2026-11-07 22:50–23:05）

3DMigoto 日志（`F:\XXMI\WWMI\d3d11_log.txt`，需 `[Logging] log_level = info` + `unbuffered = 1`）给出三个真实原因：

1. **`&&` 不受支持**
   - 日志：`Syntax Error: $a == 1 && $b == 0` → `Variable not recognized` → `Unrecognised entry: if ...` → `Statement "endif" missing "if"`。
   - 后果：`[Present]`/CommandList 里的 `if/endif` 结构错乱，后面的绘制变成**无条件执行**。
   - 修复：生成器不再输出任何 `&&`，全部改成嵌套 `if`；单测 `NeverEmitsUnsupportedAndOperator` 钉死。
2. **缺少 ASCII namespace**
   - 没有 `namespace` 时，3DMigoto 用 **ini 路径**当变量命名空间；Mod 路径含中文 → 变量名非法。
   - 日志：`Unknown global variable: $jasm_...`、`Syntax Error in variable_assignment: $\mods\...\内附说明）\...`。
   - 修复：文件头写 `namespace = JasmMenu<token>`（纯 ASCII）。
3. **删掉鼠标段落后留下未声明变量**
   - 拖动块仍引用 `$jasm_click_guard`，但该变量只在已删除的鼠标按键段里赋值。
   - 日志：`Unrecognised entry: if $jasm_click_guard == 1` → 又一次 `endif` 失配。
   - 修复：整段删除拖动/鼠标逻辑。

修复后冷启动：**菜单不再自动出现**（门控正常）。

### 5.9 仍存在的测试限制

- **`SendInput` 的合成按键不会被 3DMigoto 的 `[Key*]` 接收**：
  - 用虚拟键码与扫描码都试过，Mod 自己的 `\` 键（物理上可用）在脚本里按了也不改变 `$censor`；
  - 但 `Space`/`Enter`/鼠标点击**能推进游戏的标题画面**，说明合成输入游戏本身是接收的；
  - 结论：3DMigoto 的按键走 RawInput 并按设备过滤，注入按键不生效。**按键路径只能物理验证**。
- **F10 不重载 mod ini**：改面板宽度验证过；必须冷启动。
- **GDI 截屏抓不到游戏**：必须 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`。
- **XXMI 配置必须无 BOM 写入**：PowerShell 5.1 的 `Set-Content -Encoding UTF8` 会加 BOM，启动器直接解析失败。

当前交互设计（键盘导航，替代鼠标）：

| 键 | 作用 |
|---|---|
| `F8` | 打开/关闭菜单 |
| `↑` / `↓` | 选择行 |
| `F9` | 切换当前行的变量（等价于作者按一次键） |

鼠标点击已移除：鸣潮正常游玩时系统光标被锁定，`cursor_x/y` 不更新（红点诊断证实），无法可靠命中。




## 6. 未解问题与假设

### A. 为什么菜单启动即显示？

- **A1**：`[Present]` 里的 `if` 没有被执行，绘制实际是无条件的。
  - 验证：临时把 `$jasm_open` 永久设为 0（删掉按键段），看菜单是否还出现。
- **A2**：`$jasm_open` 被 `[Key*]` 的 `$open = 0,1` 初始化为 1，且首帧归零被覆盖。
  - 验证：同样删掉按键段，只留 `[Constants]` 的 0。
- **A3**：多个 `[Present]`（Mod 自带 + 我们的）合并后，if/endif 归属错位。
  - 验证：把我们的 `[Present]` 改名/拆小，单独放一个最小 if 测试。

### B. 为什么点击/悬停无效？

- **B1（最可能）**：游戏锁定系统光标，`cursor_x/y` 不动 → 永远不命中。
  - 验证：在 **ESC 暂停菜单**（光标可见）里打开菜单，看红点是否跟随、行是否高亮。
- **B2**：命中框坐标/缩放的换算与实际绘制不一致。
  - 验证：红点如果跟随鼠标，但行不高亮，则对比红点与行框位置。
- **B3**：`$\RabbitFX\censor` 写入语法不对（命名空间/大小写/变量可见性）。
  - 验证：绑一个**纯键盘键**（如 F9）直接 `$\RabbitFX\censor = 0` / `= 1`，绕开鼠标看 Mod 是否变化。

## 7. 推荐下一步（按顺序，先不写新功能）

1. **确认门控**：把 `$jasm_open` 永久设 0（删掉 `[KeyJasmMenuToggle]`），F10 重载。
   - 菜单消失 → 门控正常，自动弹出是初始值/按键初始化问题。
   - 菜单仍在 → 绘制未被门控，需要改绘制入口。
2. **确认光标**：在 ESC 暂停菜单里按 F8 打开，移动鼠标看红点。
   - 跟随 → 鼠标方案可行，只需修正常规场景；
   - 不跟随 → 鸣潮全程锁光标，**鼠标点击方案作废**，改键盘导航（↑↓ + 确认）或桌面浮窗。
3. **确认变量写入**：加一个 F9 直接写 `$\RabbitFX\censor`（不经鼠标），看 Mod 是否响应。
   - 响应 → 写入语法正确，问题只在命中；
   - 不响应 → 跨命名空间写入有问题，需要改成本 Mod 的命名空间或确认变量是否 `global`。

## 8. 如何完全清理

删除以下两处即可，作者文件未被修改：

```
...\Custom RabbitFX v8.2\JASM_Menu.ini
...\Custom RabbitFX v8.2\JASM_Menu\
```

或运行生成器的 `Remove(modFolderPath)`（已实现，会校验文件头，不会误删同名文件）。

## 9. 相关证据与路径

| 内容 | 路径 |
|---|---|
| 崩溃报告 | `F:\Wuthering Waves\Wuthering Waves Game\Client\Saved\Crashes\UE4CC-Windows-E548DCEE443BC7587A67588EACEB16DF_0001\CrashContext.runtime-xml` |
| 参考可点击菜单 | `F:\XXMI\WWMI\Mods\character\xinyuehu\心(含大招)-夜月守望者（9）by Nait3D\Normal\MenuHsin.ini` |
| 参考 2D shader | 同目录 `Shader\draw_2d.hlsl` |
| 本机生成的菜单 | `...\RabbitFX反虚化+发光前置v82自定义版(内附说明）\Custom RabbitFX v8.2\JASM_Menu.ini` |
| 3DMigoto 配置 | `F:\XXMI\WWMI\d3dx.ini`（`[Logging] log_level = disabled`，`include_recursive = Mods`） |
| 持久变量 | `F:\XXMI\WWMI\d3dx_user.ini`（当前无 `jasm` 残留） |
| XXMI Ini Optimizer 缓存 | `F:\XXMI\Resources\Cache\Ini Optimizer\WWMI.json`（已包含 `JASM_Menu.ini`） |

## 10. 结论（当前）

### 已验证

- 生成 ini / 纹理 / DDS / shader（离线编译通过）正常。
- 冷启动时菜单**不会自动出现**（门控修复后）。
- 强制 `open = 1` 冷启动时，菜单/标题/行/选中高亮**正常绘制**。
- 按键段注册正常：`F8`（开关）、`UP` / `DOWN`（选择）、`F9`（切换）。
- **物理实测（2026-11-07 23:2x）：F9 可把 RabbitFX 的 `$censor` 在 0/1 之间切换**，说明「键盘切换 → 跨命名空间写变量 → Mod 生效」整条链路成立。
- `↑`/`↓`：RabbitFX 只有一行，选中行被夹在 0，无可见变化（预期，多行菜单才有意义）。
- Mod 的变量可跨命名空间读写：`$\RabbitFX\censor`。

### 无法自动验证

- **物理按键路径**：3DMigoto 的 `[Key*]` 走 RawInput 并按设备过滤，`SendInput`（虚拟键/扫描码）都收不到；游戏本身能收到合成输入（Space/Enter/点击能推进标题），但 3DMigoto 的按键钩子不认。需要用物理键盘按一次确认。

### 组合键不生效（2026-11-07 23:10）

- 实体按下 `Ctrl+Shift+M`：游戏打开了地图（Ctrl+M 是地图快捷键），但菜单没有出现。
- 说明 `key = CTRL SHIFT VK_M` 在 3DMigoto 这边没有匹配上。现有 Mod 里只见过单修饰键写法（`CTRL VK_LEFT` / `ALT VK_UP`），**多修饰键组合很可能不被这个构建支持**。
- 处理：诊断/默认呼出键改为单键 **F8**（现有 Mod 风格里 F 系列是常见且安全的单键），组合键方案待单独验证。


### 产品结论

- 鼠标点击在鸣潮正常游玩时不可用（系统光标被锁定，`cursor_x/y` 不更新，红点诊断证实）。交互改为**键盘导航**：
  - `Ctrl+Shift+M` 开关，`↑`/`↓` 选择，`F9` 切换。
- 仍为原生 3DMigoto 绘制，点击切换的延迟目标（≤1 帧）由键盘路径同样满足。
- PRD 中的「可点击」验收项需要改为「键盘可切换」；若坚持鼠标点击，只能回到桌面浮窗方案（100–300ms、抢前台），与「低延迟」目标冲突。

> **2026-11-08 更新：以上「鼠标不可用」结论已被 5.12 推翻。** 根因不是光标锁定，而是用了会冻结的
> `cursor_x/y`；改用 `cursor_screen_x/y` 后鼠标点击在正常游玩中可用。

### 当前磁盘状态

- RabbitFX 前置 Mod 内有正式版菜单（`F8`，无调试红点，启动关闭）。
- 3DMigoto 日志已恢复为 `disabled`，警告已静音。
- `d3dx_user.ini` 仅保留 Mod 自己的 `$\rabbitfx\censor`，无 JASM 残留。

### 应用层接入（2026-11-07 23:30–23:55）

已接入并编译通过：

- `Services/ModHandling/InGameMenuService.cs`：生成 / 移除 / 批量补齐。
- `ModInstallerService`：静默安装完成后自动生成。
- `ActivationService`：启动后台批量补齐。
- 设置页「游戏内 Mod 菜单」：呼出键（改键后重建已有菜单）+「为所有 Mod 补齐菜单」。
- Mod 详情页按键映射区域：生成 / 移除 / 状态。

实测（Debug 构建日志）：

```
[InGameMenu] 已生成：RabbitFX反虚化+发光前置v75（1 行）
[InGameMenu] 已生成：弗洛洛-罪恶修女v1.1.2 og 狩野樱（11 行）
[InGameMenu] 启动补齐完成：生成 2，跳过 0，失败 0
```

注意：Debug 构建的 Mod 路径是 `C:\XXMI\WWMI\Mods`（`ApplicationData_WuWa_Debug` 里的旧测试路径），
所以上述产物落在 C 盘；用户实际使用的 `F:\XXMI\WWMI\Mods` 由正式构建处理。

### 5.10 UI v2：仿《清宵·绯花令》菜单布局（2026-11-08）

需求：严格仿照用户提供的游戏内菜单截图做布局。

实现（仍全部由 3DMigoto 原生绘制）：

- 左上两行标题 + 青色下划线；
- 顶部页签（页码，当前页高亮）；
- 左侧竖栏：页码 + 关闭「×」；
- 每行：左箭头、名称、当前值、右侧按键提示、右箭头；
- 行底条深蓝渐变 + 青色描边 + 内侧暖色内线；
- 底部三行说明文字。

交互（用户确认可用）：

- `F8` 开关；
- 按住 `Alt` 后：左键点击行/页签/页码，右键拖动菜单。

数据映射：截图里的分类页签在 JASM 里没有对应数据，改为**页码页签**；
行名称/值仍只来自 Mod 的 `[Key*]` 循环，不臆造中文名。

调色修正：`RasterSurface.SetPixel` 的参数顺序是 **BGRA**，页签颜色最初写反（蓝变棕），已修正。

### 5.11 多份菜单叠字 & 逐角色门控（2026-11-08）

现象：角色选择界面里「尤诺」和「绯雪」两份菜单的标题、行叠在一起；F8 还会把两份菜单的 open 一起翻转。

根因：

- 每个 Mod 的 `[TextureOverrideComponent*]` 都会设置 **全局** `$object_detected = 1`；
- 没有 `namespace` 的 Mod（尤诺/绯雪都是）共享同一个全局变量，所以任意角色模型绘制都会点亮所有菜单；
- 多份菜单都绑 F8，按一次同时翻转所有 open。

修复：

1. **逐角色检测**：从 Mod 里「设置 `$object_detected = 1` 的那一段」提取角色专属的 `hash =`，在生成的 ini 里加一个
   ```ini
   [TextureOverrideJasmDetect_<token>]
   hash = 9707e44e
   $jasm_<token>_last_seen = time
   ```
   菜单只在 `time - last_seen < 0.5` 时显示/保持打开；切换到别的角色后自动关闭。
2. 呼出键不再用全局 `$object_detected` 做条件。
3. 只保留当前使用角色的一份 JASM 菜单（多份会叠字）；需要别的角色时再生成。

已知边界：角色选择/加载界面如果正在绘制该角色模型，菜单仍会显示（这是「当前角色」的正确行为）；同屏多角色时各自菜单会同时可见，需要避免同时保留多份菜单。

### 5.12 鼠标点击根因：`cursor_x/y` 在光标隐藏时冻结（2026-11-08）

**现象**：菜单绘制、悬停样式都正常，但按住 Alt 左键点行没有任何反应（`$\RabbitFX\censor` 不变）。

**根因**：3DMigoto 的 `cursor_x/cursor_y` 是**最近一次可见光标位置**；游戏在正常游玩时隐藏光标，
这两个变量就冻结不再更新。调试红点证据：第一次 `SetCursorPos` 后红点出现在正确位置，之后连续移动光标，
红点始终停在原处。菜单可见 ≠ 命中框里的坐标是当前鼠标位置——`in_rect` 永远算成 0，点击逻辑根本没有执行。

**修复**：命中判定改用物理屏幕坐标 `cursor_screen_x / cursor_screen_y`（每帧从 `GetCursorPos` 更新）。
真机验证：

- 红点精确跟随鼠标（三个不同位置的 bbox 与预期物理坐标逐一吻合）；
- 鼠标进入行区域时悬停高亮出现（`in_rect` 正常）；
- **物理按住 Alt + 左键点击 Censor 行：值 1 → 0**，整条链路成立。

**版本门控**：生成器升到 **v3**（`InGameMenuOptions.CurrentGeneratorVersion = 3`），manifest 记录版本。
批量补齐现在用 `InGameMenuGenerator.IsUpToDate()` 判断，v2 的旧菜单会在升级后自动重建，
不需要用户手动删菜单。

**自动化测试的 DPI 坑（避免再次误判）**：

- 本机是 2560×1600 @150%，DPI-unaware 的 PowerShell 里 `SetCursorPos` 参数会 ×1.5 映射到物理坐标；
- `PrintWindow` 抓到的是物理窗口左上角的 1707×1067 裁剪，图像坐标 = 物理坐标；
- 之前用 `(1350,590)` 点「行」，实际物理落点是 `(2025,885)`，离行中心 (933,427) 差了一千多像素——
  「点击无效」有一部分是**根本没点中**。以后自动化点击必须先换算物理坐标。

**更正**：第 7 节「鼠标方案作废、改键盘导航」的结论不再成立；键盘导航（↑/↓/F9）保留作为备用路径。

### 5.13 无 namespace 的 Mod + 逐角色门控（2026-11-08 晚）

修好鼠标后暴露出两个更底层的问题：

1. **无 namespace 的 Mod 点击不生效**
   - 3DMigoto 对没有 `namespace =` 的 ini 用**文件路径**当命名空间，而 Mod 路径通常含中文。
   - 菜单里写成 `$\Mods\character\绯雪-…\mod.ini\skirt` 会被表达式解析器判为语法错误
     （日志：`Syntax Error ... \绯雪-…\mod.ini\skirt`，整段 `if` 变成 `Unrecognised entry`）。
   - 修复：生成菜单时给每个相关 ini **补一行 ASCII namespace**（`; JASM: added namespace…` +
     `namespace = JasmXXXXXXXX`），菜单改用 `$\JasmXXXXXXXX\var`。只加一行，移除菜单时按标记删掉。
   - 命名空间种子 = `Mod 绝对路径 + ini 相对路径`，保证不同 Mod 不会撞名（不同 Mod 的 ini 相对路径常常都是 `mod.ini`）。

2. **多份角色菜单叠在一起 / F8 关不掉**
   - 补 namespace 后，每个 Mod 的 `$object_detected` 变成**按 Mod 隔离**的变量：组件绘制时置 1，
     Mod 自己的 `[Present]` 再 `post` 回 0。
   - 门控改用它：`condition` 不能加在呼出键上——3DMigoto 处理按键时 `object_detected` 已经被 post 回 0，
     会导致「F8 只能打开、关不掉」。现在呼出键无条件，改为在 `[Present]` 里：
     - 角色不在场（变量为 0）→ 强制 `open = 0`（所以 F8 翻转所有菜单后，只有当前角色那份留下）；
     - 绘制前再判 `== 1`。
   - 结果：F8 只影响当前角色的菜单；切换角色时其他菜单自动关闭，不再叠字。

生成器版本：v3（`cursor_screen_x/y`）→ v4（ASCII namespace）→ v5（object_detected 门控）→ v6（门控从按键挪到 Present 自动归零）。
旧版本菜单会在批量补齐时自动重建。

3. **F8 与 3DMigoto frame analysis 冲突（2026-11-08 晚）**
   - `d3dx.ini` 里有一行 `analyse_frame = no_modifiers VK_F8`。
   - F8 会逐帧触发 `User key activation`（日志可证），把菜单的 `open` 反复 cycle，
     表现为「F8 关不掉菜单 / 一进游戏菜单自己弹出」。
   - 默认呼出键改为 **F6**（`d3dx.ini` 未占用）；生成器 **v7** 会重建旧菜单。




---

*本日志用于在继续之前保留事实与判断，避免重复试错。*
