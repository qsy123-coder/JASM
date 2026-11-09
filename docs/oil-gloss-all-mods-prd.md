# 油光全 Mod 兼容 PRD（消除变色与贴图错误）

**Version**: 1.0
**Date**: 2026-11-09
**Status**: 需求已确认，待实现
**前置文档**: `docs/oil-gloss-per-mod-prd.md`（Phase 0/1 实现）

---

## 背景与问题

Phase 1 已把 KatFXMatcap 油光做成详情页 / 浮窗的一键开关。实机使用后暴露两类问题：

1. **头发变色**：阿尔比盖（尤诺-阿比盖尔泳装 3.0）的头发、尤诺-C酱发型的双马尾，在开启油光后变成亮绿/异常颜色。
2. **贴图错乱 / 被覆盖**：部分 Mod 出现肌肉、衣物、眼睛等部位贴图异常，表现为整块偏色或原贴图被平色覆盖。

本机 `F:\XXMI\WWMI\d3d11_log.txt` 与上一轮会话（2026-11-09）的实测数据给出了明确线索：

- `Matcap.ini` 的 `[CommandListOilSkin]` 会把 `ps-t0` 替换成 `Oil/Empty_Normal.dds`
  （RGB 约 `129,129,0`，一张偏橄榄色的 64×64 贴图）；
- `[CommandListClear]` 只把 `ps-t124..127` 与 matcap 参数复位，**从不恢复 `ps-t0`**；
- 因此一次加油光后，`ps-t0` 会一直停留在 `Empty_Normal`，直到下一个自己绑定 `ps-t0` 的
  TextureOverride 覆盖它；在此期间绘制的网格（尤其透明层的头发、后续 drawcall）
  都会拿到错误的 `ps-t0`，直接表现就是**偏色 / 贴图被平色覆盖**。

同时，上一轮会话确认阿尔比盖的 Component1（索引数最大块，59k）实际是**头发**，
不是身体；旧版「最大块 = 身体」的自动分组把头发包进了油光。
（WWMI 组件约定：Component0–1 = 身体绒毛 / 头发，Component2 = 脸，Component3+ = 身体/衣物。）

当前分支 `fix/oil-gloss-simple-grouping` 已改成只包 Component3+，但：

- 已落盘的旧补丁（`Matcap.ini` 副本）不会被更新，旧 `ps-t0` 逻辑仍在生效；
- 没有 Component3+ 的纯发型 Mod 仍会走「包全部」回退，头发仍被包；
- 没有任何机制阻止「把头发网格当成 Component3+ 衣物」包进去。

---

## 目标 / 非目标

### 目标

- 任意已安装的鸣潮 Mod 点一次「开启油光」后：
  - 皮肤/衣物出现油光；
  - **色相不变**：只允许高光/亮度变化，不允许偏绿、偏黄、发黑或原贴图被平色覆盖；
  - 头发、脸、眼睛、发饰不变色（默认不包 Component0–2）；
  - 已有的旧油光补丁无需用户逐个撤销重开，自动升级到修复后的本体。
- 撤销后 Mod 与开启前逐字节一致（外部未修改的前提下）。
- 保持「逐 Mod 手动开启」，不做批量自动应用。

### 非目标

- 不重做自动分组 UI（详情页仍保持「一个开关 + 状态」）。
- 不做身体/衣物分组开关（当前为单一总开关，见上一版决策）。
- 不改动作者自己的 `Matcap.ini`（若 Mod 作者预装本体，JASM 只识别、不覆盖）。
- 不引入新的第三方依赖。
- 不修改游戏内存 / 不注入 DLL；仍然只改 ini 与贴图文件。

---

## 用户故事

### Story 1：旧补丁自动修复

**作为** 已经给多个 Mod 开过油光的玩家
**我想要** 更新 JASM 后不用逐个撤销重开，旧补丁自动换成修复后的本体
**以便** 头发/贴图立即恢复正常

- [ ] 打开详情页/浮窗时，检测到「JASM 写过、且未被用户改动」的旧版
      `Matcap.ini`，自动替换为新版并更新 manifest 哈希。
- [ ] 旧补丁（身体/衣物两组、包含 Component0–2）自动撤销并按新规则重打，
      菜单同步重建为「油光 + 油光强度」两行。
- [ ] 用户自己改过的本体文件不动，状态显示「本体被修改，已保留」。
- [ ] 游戏正在运行时替换文件失败不报错，下次再试。

### Story 2：任意 Mod 开启油光都不变色

**作为** 玩家
**我想要** 给任意角色 Mod 开启油光后，皮肤/衣物有油光但颜色正常
**以便** 不用逐个试错

- [ ] Component3+ 的网格被包；Component0–2（身体绒毛/头发/脸）默认不包。
- [ ] Mod 只有 Component0–2 时不再「包全部」；返回「只找到脸/头发网格，已跳过」。
- [ ] 每个 drawcall 只按自己的局部上下文判断：向前最多 20 行，遇到上一个 drawcall /
      段落头就停；`hair / head / face / eye / brow` 只影响它自己那一块。
- [ ] 同一个 Component3 段落里先画头发、后画身体时，身体网格仍然被包
      （爱弥斯-不善女仆就是这种结构，旧规则会把身体全排除）。
- [ ] 包进去的网格色相不变：油光只做高光叠加，不再替换 `ps-t0`。
- [ ] 纯发型 Mod（网格全在 Component0–2）整包跳过；若作者把头发放在 Component3+，
      油光只允许提亮，不允许偏色。

### Story 3：撤销与状态不回归

- [ ] 撤销后作者 ini 逐字节还原，`JASM_Oil.ini` / `JASM_Oil/` / 补丁本体删除。
- [ ] 旧 manifest（没有 `BodyVersion` 字段）仍可正常撤销。
- [ ] 外部修改过的补丁文件仍拒绝覆盖。

---

## 方案设计

### 1. 根因修复：不再替换 `ps-t0`（核心）

`Matcap.ini` 的 `[CommandListOilSkin]` 删除下面这行：

```ini
ps-t0 = ResourceEmpty_Normal
```

（同时保留 `[ResourceEmpty_Normal]` 定义与 `Oil/Empty_Normal.dds`，避免旧补丁/旧本体引用时缺资源。）

理由：

- matcap 的 UV 来自注入着色器里的 `wnorm`（顶点法线），不依赖 `ps-t0`；
- `ps-t0` 在不同 shader 上语义不同（法线 / 漫反射 / 其它），一刀切换成 `Empty_Normal`
  会让头发、衣物等整块偏色；
- `Clear` 无法可靠恢复原始 `ps-t0`，每次替换都会污染后续 drawcall。

**取舍**：不采用「保存/恢复 `ps-t0`」方案。`ref` 保存/恢复只能保证「不泄漏给后续 drawcall」，
但被包的那次 draw 本身仍然拿到错误贴图，变色问题只解决一半，还多一个跨命令列表的状态机。

**效果预期**：油光高光按原始法线/材质叠加，颜色保持原贴图；高光细节可能比原版更丰富
（原版靠 `Empty_Normal` 抹平法线）。

### 2. 网格选择规则（收紧，不再猜身体）

按 WWMI 官方组件约定，只包 Component3 及以后：

| 规则 | 行为 |
|---|---|
| 有 `TextureOverrideComponent3+` | 只包这些段落里的 `drawindexed` |
| 只有 Component0–2 | **不包**，返回「只找到脸/头发网格」 |
| 完全没有 ComponentN 段落 | 包所有 `[TextureOverride*]` 里的 `drawindexed`，但跳过名字/条件/注释带头发关键词的 |
| drawcall 自己的局部上下文（向前 20 行，遇到上一个 drawcall / 段落头停止）含 `hair/head/face/eye/brow` | 不包 |
| 同一个 Component 段落里既有头发又有身体 | 只排除头发那一块，身体照常包 |

删除当前「没有 Component3+ 就包全部」的回退。原因是它会命中纯发型 Mod
（C酱 11 块全是头发），而官方的身体网格本来就在 Component3+；Component0–2 的
老 Mod 极少数，且宁可不开也不要给头发上油。

关键词只做**排除**，不做身体识别；不再根据索引大小/高度/位置猜身体。

**不按文件夹名跳过**：不少整模 Mod 的名字里也带「发型」（例如
`尤诺-C酱发型 by 布鲁加尔` 可能带完整身体），仅凭名字会误伤。纯发型 Mod
若网格全在 Component0–2，本来就会被上一条规则跳过；如果作者把头发放在
Component3+，在 `ps-t0` 修复后只会得到中性高光，不再偏色。

### 3. 本体 / 补丁升级与版本化

新增 `OilGlossProtocol.BodyVersion`（本次为 4）与 `PatchVersion`（本次为 3）。
`manifest.json` 增加：

```json
"BodyVersion": 4,
"PatchVersion": 3
```

升级流程（`OilGlossBodyProvisioner.Upgrade`）：

1. 读 manifest；`BodyProvidedByJasm == true` 且 `BodyVersion < 当前`；
   **或者** `Matcap.ini` 的哈希命中 JASM 历史版本哈希表（早期 manifest 可能
   丢失所有权，但文件能证明是 JASM 写的）；
2. 对 `Matcap.ini`（以及未来其他本体文件）：
   - 当前文件 SHA256 == manifest 记录的哈希（或已知旧 JASM 哈希）→ 覆盖；
   - 不相等 → 用户/作者改过 → 保留，记录警告，不阻断；
   - 文件缺失 → 从内嵌资源补写；
3. 清单 `BodyFiles` 不完整时（早期升级只记了 `Matcap.ini`）补全记录：
   现有文件内容与内嵌资源一致才纳入，撤销时才能删干净；
4. 更新 `BodyFiles` 哈希、`BodyVersion`、`BodyProvidedByJasm`；
5. 原子写（临时文件 + `File.Move(overwrite)`），失败（游戏占用）留到下次。

补丁重打（`OilGlossService.RepatchIfOutdated`）：

1. `State == Applied` 且 `PatchVersion < 当前`；
2. 撤销旧补丁（还原作者 ini、删除旧本体）；
3. 按当前扫描规则重新应用（只包 Component3+、单一总开关）；
4. 新规则下没有可包网格（纯发型 Mod）→ 保持未开启并清理本体；
5. 菜单重建（旧菜单里的「身体油光 / 衣物油光」两行会消失）。

触发点：

- `OilGlossService.GetStatus` 之后（详情页/浮窗刷新时，浮窗构建每一行都会调用，
  等同于遍历当前角色下所有 Mod）；
- `EnableAsync` / `ApplyGroupsAsync` 之前（重新开启 / 撤销后重开也会升级）。

> 为什么自动改：这些文件是 JASM 自己复制进 Mod 的，manifest 已明确记录
> `BodyProvidedByJasm=true` 与哈希；用户没有理由手改。只改「哈希未变」的文件，
> 不会覆盖用户改动。

### 4. 多 Mod 共用 `namespace = KatFXMatcap`

实机日志确认：多个 Mod 各自带一份 `Matcap.ini` 时，第二次加载的所有段落都是
`Duplicate section found` / `Entry outside of section`，即**后来者被忽略，第一份生效**。

本版保持「每个 Mod 一份本体」不变（不做共享目录，避免新增一个用户可见的伪 Mod），
但要求：

- 升级时把**所有**已开启油光的 Mod 的 `Matcap.ini` 一起升级；
- 只要有一份旧本体，它就可能因加载顺序先被加载，所以维护流程必须遍历全部 Mod；
- 状态页/日志记录「发现 N 个旧本体，已升级 M 个」。

> 后续若要做共享本体，单独立项；本版不改目录结构，保证撤销仍能逐 Mod 还原。

### 5. 强度上限与贴图切换（F7 菜单）

- **强度上限 2.0**：`$oilStrength` 直接作为 `matcap1_opacity`，允许 >1；
  菜单滑块轨道按 `SliderMax = 2.0` 归一化，拖动/滑块头位置都不再假设 0–1。
  物理键 `+` / `-` 的循环表同步扩到 2.0，最低 0.2；`*` 只在 1–6 之间切换样式，
不再能把 `$oilSkin` 设成 0。关油光统一交给「油光」总开关。
- **油光贴图行**：新增 `[KeyOilTexture] 油光贴图` 循环行，直接写
  `$\KatFXMatcap\oilSkin`，候选值 `2,3,4,5,6,1`，显示文本
  `MC6 / MC1 / Oil1 / Oil2 / Wet / OIl_MC`。
  - 不含 0：关油光由「油光」总开关负责，避免出现第二个关闭入口。
  - 菜单生成器新增 `ValueLabels`（循环值显示文本）与 `SliderMax`（滑块上限）；
    生成器版本升到 **v35**，旧菜单启动后自动重建。
- **作者已有可点击菜单不再阻止油光菜单**：
  - 普通 Mod 仍保持原行为（避免出现两套菜单）；
  - 启用了油光的 Mod 由 `InGameMenuOptions.AllowWithExistingClickableMenu = true`
    强制生成 JASM 菜单，油光行才能进 F7；
  - 撤销油光后，如果生成器因作者菜单而跳过，删除这份由油光带出来的第二菜单，
    不留下失效的油光行。

### 6. 隐藏的关闭路径：`$oilSkin` / `$oilStrength`

这两个变量是 `global persist`，所有油光 Mod 共用。原版 KatFXMatcap 的 `*` / `-`
热键会把它们存成 0；一旦为 0，`CommandListOilSkin` 直接不生效 ——
F7 菜单里的油光行照常显示，但画面完全没有油光，是「有菜单没效果」最常见的原因。

- JASM 在**开启油光 / 打补丁 / 本次会话第一次打开油光 Mod**时检查一次：
  - `$oilSkin` 缺失或 <= 0 → 写回 `2`（MC6）；
  - `$oilStrength` 缺失或 <= 0 → 写回 JASM 保存的强度（默认 0.7）。
  - 只复位异常值，不覆盖用户选的样式/强度。
- 本体 **BodyVersion 4**：物理键不再能设 0（`*` 只切 1–6，`-` 最低 0.2）；
  关油光统一由 F7 总开关负责。
- 写入 `d3dx_user.ini` 后需要 F10 / 重启游戏才会被 3DMigoto 重新读取。

### 7. 兼容性

- 旧 manifest 没有 `BodyVersion` / `PatchVersion` → 视为 0，允许升级 / 重打。
- 旧撤消逻辑不变：`BodyFiles` 哈希与当前文件比对，未改才删。
- 用户手动安装的原始 KatFXMatcap（`BodyProvidedByJasm=false` 且哈希不在
  JASM 历史本体表里）不升级、不覆盖；详情页状态提示「使用作者自带本体，
  油光可能仍有旧版偏色，可撤销后让 JASM 接管」。
- 早期 manifest 把 JASM 本体误记为作者本体时，靠 `Matcap.ini` 的历史哈希
  识别并升级（`C2B053…` = BodyVersion 1）。
- 所有新增字符串仍为 ASCII（3DMigoto 表达式解析限制）。

---

## 数据与接口变更

| 位置 | 变更 |
|---|---|
| `OilGlossProtocol` | 新增 `BodyVersion = 4`、`PatchVersion = 3`、`MaxStrength = 2.0`、`TextureVariable` |
| `OilGlossManifest` | 新增 `int BodyVersion` / `int PatchVersion`（默认 0） |
| `OilGlossBodyProvisioner` | 新增 `Upgrade`（本体升级 / 清单补全）、`IsOutdatedKnownJasmBody`；`Ensure` 记录 `BodyProvidedByJasm` |
| `OilGlossScanner` | 删除 Component0–2 回退；新增头发/脸关键词排除 |
| `OilGlossService` | 状态刷新/开启前触发本体升级；`RepatchIfOutdated` 自动重打旧补丁 |
| `Assets/OilGloss/Matcap.ini` | 删除 `ps-t0 = ResourceEmpty_Normal` |
| `Assets/OilGloss/README.md` | 记录 JASM 对 `ps-t0` 的修改 |

---

## 边界与异常

| 场景 | 行为 |
|---|---|
| 游戏运行中占用 `Matcap.ini` | 升级失败静默重试，不阻断开关/菜单 |
| 用户改过本体 | 保留用户版本，记录警告，不覆盖 |
| Mod 只有 Component0–2 | 不包，返回原因；按钮状态显示「没有可包油光的网格」 |
| 纯发型 Mod（全在 Component0–2） | 整包跳过，返回原因 |
| 多 Mod 同时开启 | 全部升级到同一份新版本体 |
| Mod 更新覆盖补丁 | 状态 `Broken`，提示重新开启（不变） |
| 非鸣潮游戏 | 入口不显示（不变） |
| 游戏使用 DX12 | 3DMigoto 不注入；F7 菜单可能仍显示，但油光不生效。需切回 DX11 |

---

## 验收标准（可逐条勾选）

### 颜色 / 贴图

- [ ] 阿尔比盖类 Mod（头发在 Component0–2）：头发颜色不变，身体/衣物有油光。
- [ ] C酱类纯发型 Mod：网格全在 Component0–2 时不产生补丁；若在 Component3+，
      双马尾只允许提亮，不允许变绿/变黄。
- [ ] 心-点绛唇、女漂-暗黑共鸣：皮肤/衣物有油光，色相与原贴图一致。
- [ ] 贴图无平色覆盖、无条纹、无 UV 错乱；`ps-t0` 不再被写入 `Empty_Normal`。
- [ ] 开启油光后，非目标部位（脸、眼睛、发饰）颜色不变。

### 选择 / 状态

- [ ] Component3+ 全覆盖，Component0–2 全排除。
- [ ] 只有 Component0–2 的 Mod 返回「只找到脸/头发网格」，不包。
- [ ] 段落/条件/注释带 hair/head/face/eye 的网格不包，但只影响它自己那一块。
- [ ] 同一个 Component3 段落里先有 Hair Acc / Hair Ribbon、后有 Body / Top / Skirt 时，
      身体网格仍被包（爱弥斯-不善女仆）。
- [ ] 网格全在 Component0–2 的 Mod 整包跳过。
- [ ] 旧补丁自动升级；`BodyVersion` / `PatchVersion` 写入 manifest。
- [ ] 旧补丁的门控从「总开关 + 身体/衣物」变成「总开关」一层。
- [ ] 用户改过的本体保留，不被覆盖。
- [ ] 早期清单丢失所有权但 `Matcap.ini` 哈希命中旧 JASM 版本时仍能升级。

### 菜单

- [ ] 油光强度滑块上限为 2.0；拖到最右时滑块头停在轨道末端，变量写入 2.0。
- [ ] 「油光贴图」行循环 6 种贴图，显示 MC6 / MC1 / Oil1 / Oil2 / Wet / OIl_MC。
- [ ] 切换贴图不需要 F10；下一帧生效。
- [ ] 菜单生成器版本为 v35，旧菜单启动后自动重建。
- [ ] 作者自带可点击菜单的 Mod 开启油光后，仍能生成含油光行的 JASM 菜单。
- [ ] 撤销油光后，这份第二菜单被移除（不残留失效的油光行）。
- [ ] 原版 `*` / `-` 把 `$oilSkin` / `$oilStrength` 存成 0 后，重新打开 JASM
      或开启任意油光 Mod 会把异常值复位；F10 后油光恢复。
- [ ] 复位不覆盖用户已经选好的贴图样式和强度。

### 撤销 / 回归

- [ ] 撤销逐字节还原；新旧 manifest 都能撤销。
- [ ] 未开启油光的 Mod 行为不变。
- [ ] F7 菜单「油光 / 油光强度」两行行为不变；旧菜单的身体/衣物行被移除。
- [ ] `dotnet build`、全量测试、`Release.py SingleFile ExcludeElevator` 通过。

---

## 测试计划

### 单测

- `OilGlossScanner`
  - Component0–2 排除；只有 0–2 时不回退；
  - 无 ComponentN 时包全部并跳过 hair 关键词；
  - 只有 Component0–2 时返回原因（不回退包全部）；
  - 同一个 Component 段落里头发/身体混合时，只排除头发 drawcall。
- `OilGlossBodyProvisioner`
  - 旧版 JASM 本体（哈希匹配）被升级；
  - 用户改过的本体不升级；
  - 游戏占用时失败可重试。
- `OilGlossPatcher`
  - 新补丁不再引用 `Empty_Normal`；
  - 旧 manifest 撤销兼容。
- `OilGlossMenuRows` / 菜单生成器
  - 三行：油光 / 油光强度 / 油光贴图；
  - 滑块 `SliderMax = 2.0`，生成 ini 里包含 `/ 2 *` 与 `slider_v > 2`；
  - 贴图行写 `$\KatFXMatcap\oilSkin`，值 2,3,4,5,6,1，显示文本正确。

### 真实 Mod 回归（本机）

| Mod | 验证点 |
|---|---|
| `F:\XXMI\WWMI\Mods\character\rover\女漂-暗黑共鸣 by Kendj1` | 当前旧补丁升级；身体/衣物油光；无贴图错乱 |
| `F:\XXMI\WWMI\Mods\character\xinyuehu\心(含大招)-点绛唇v2.4…` | 多 ini（norm/ult）门控；颜色正常 |
| 阿比盖尔 / C酱（若用户重新安装） | 头发不变色；只有 0–2 的纯发型 Mod 跳过 |

### 实机

- 应用后 F10，观察游戏世界（角色图鉴不走 matcap，不作为验收场景）。
- 两个 Mod 同时开启，确认无重复段落导致的异常。
- 撤销后 F10，确认恢复原样。

---

## 分期

- **Phase A（本版）**：`ps-t0` 修复 + 选择规则收紧 + 本体自动升级。
- **Phase B（后续）**：共享本体目录 / 更强材质识别（如需）。

---

## 附：需求确认记录（2026-11-09）

- 现象：贴图错乱/被覆盖 + 皮肤/衣物整块变色；头发变色（参考上一轮会话截图）。
- 范围：逐 Mod 手动开启，不做批量。
- 验收口径：只允许高光/亮度变化，不允许色相改变。
- 样本：上一轮会话中的阿比盖尔、C酱发型；本机现有女漂-暗黑共鸣、心-点绛唇。
