# 油光（KatFXMatcap）逐 Mod 可选功能 PRD

> **后续修正**：身体/衣物两组与「三行菜单」已被单一总开关取代；
> `ps-t0` 替换导致的偏色、旧补丁自动升级等修复见
> [`oil-gloss-all-mods-prd.md`](oil-gloss-all-mods-prd.md)。

**Version**: 1.0
**Date**: 2026-11-09
**Status**: 需求已确认，待实现

---

## 背景与问题

玩家想让**任意一个已安装的 Mod** 的皮肤/衣物呈现油亮、反光效果。现成方案是 `oil_sc` 里的 KatFXMatcap（customoilfx）：

- 本体是一套 3DMigoto 的 `Matcap.ini` + `Oil/*.dds`，`namespace = KatFXMatcap`；
- 用法是在 Mod 的 `[TextureOverride*]` 段里，把目标 `drawindexed` 夹在
  `run = CommandList\KatFXMatcap\OilSkin` 与 `run = CommandList\KatFXMatcap\Clear` 之间；
- 总开关是 `$oilSkin`（0 = 关），强度是 `$oilStrength`，物理键 `*` / `+` / `-`。

`oil_sc` 里的 `分体油光4+撤销油光` 是一套 Python 工具，做法正是**直接改作者的 ini**：

1. 扫描 Mod 下所有 ini，收集 `drawindexed`；
2. 按部位名 → 位置启发式，或人工填编号，分成「身体 / 衣物」两组；
3. 每组 drawcall 外面套 `if $oil_body` / `if $oil_cloth`；
4. 补 `[Constants]` 与两个 `[Key*]` 热键段；
5. 备份成 `*.ini.oil.bak`，`--undo` 还原。

问题在于：这套工具要用户自己找路径、开命令行、看表格手填编号，而且和 JASM 已有的 F7 游戏内菜单完全脱节。

JASM 已经有：

- 每个 Mod 一份游戏内菜单 `JASM_Menu.ini`（F7 打开、可点击、行数据来自 `[Key*]`）；
- 成熟的 ini 编码保留读写（`IniFileText`）；
- 「改作者 ini + 备份 + 原子写」的先例（`UhdAliasImportService`）。

所以本次把「油光」做成 **JASM 内的一键可选能力**：详情页点一下，自动复制本体、自动分组、自动打补丁，并在 F7 菜单里多出三行油光开关。

---

## 已确认的决策（本 PRD 的前提）

| 决策 | 结论 |
|---|---|
| 本体来源 | **随 JASM 内置分发**（已用解压码解出 `Matcap.ini` + `Oil/*.dds`） |
| 补丁策略 | **允许修改作者 ini**，带 `.jasm_oil.bak` 备份与一键撤销 |
| 菜单落点 | **F7 唤出的游戏内菜单**（`JASM_Menu.ini`），不是 WinUI 按键映射面板 |
| 选项粒度 | **总开关 + 身体 / 衣物两组** |
| 分组方式 | **全自动猜测**（不做人工表格） |
| 出现时机 | **详情页手动开启**，默认不影响任何现有 Mod |
| 总开关实现 | **新增独立门控** `$jasm_oil_on`，不覆盖 `$oilSkin` |
| 身体/衣物物理热键 | **不加**，只靠 F7 菜单点击 |
| 已带本体的 Mod | **识别并接管**：不重复复制本体，只补分组与菜单行 |
| 游戏范围 | **只做鸣潮 WWMI** |

---

## 实现状态（2026-11-09）

已完成：

- Core：`OilGlossScanner` / `OilGlossPatcher` / `OilGlossBodyProvisioner` / `OilGlossMenuRows`
  （`src/GIMI-ModManager.Core/Services/OilGloss/`）。
- WinUI：详情页「开启油光 / 撤销油光」入口（`ModPane.xaml` + `ModPaneVM`），
  `OilGlossService`，内嵌本体资源（`Assets/OilGloss/**` → `EmbeddedResource`）。
- 菜单生成器：`InGameMenuOptions.ExtraRows` / `ExcludedNamespaces`、
  `PickGateNamespace` 修正。
- 单测：扫描 / 补丁 / 撤销 / 编码 / 菜单注入，共 18 个新测试；全量 1406 个测试通过。

Phase 0（实机，2026-11-09）已通过：

- 跨命名空间 `if $\JasmOilXXXX\jasm_oil_on` **生效**：日志确认条件被编译执行；物理按 Insert 时变量 1↔0 切换，游戏世界里油光亮/灭肉眼可见。
- 发现并修掉本体缺陷：`CommandListOilSkin` 引用了 `ResourceEmpty_Normal` 却没有定义，3DMigoto 报
  `Unrecognised entry: ps-t0 = resourceempty_normal`，导致后续 matcap 参数设置失效、油光不显示。
  已在随包资源里补上 `[ResourceEmpty_Normal] filename = Oil/Empty_Normal.dds`。
- 油光在**游戏世界**生效；**角色图鉴 / 角色选择界面**不走 matcap 的 shader 路径，看不到油光（不影响游戏内效果）。
- 注入的 `SendInput` / `PostMessage` 键 3DMigoto 不认，实机验证必须物理按键；F10 会重载 mod ini（WWMI `reload_config`）。

菜单点击与多 ini（2026-11-09 补充验证）：

- 物理 `Alt+左键` 点击 `Phase0 Body` 行后，`d3dx_user.ini` 的
  `$\JasmOilPhase0\oil_body` 从 1 变成 0，菜单显示从 `2/2` 变成 `1/2`，世界画面里身体组油光消失。
- 把两个 `TextureOverrideComponent` 拆到第二个 ini（`JASM_OilPart2.ini`，同一 namespace），
  和主 `mod.ini` 里的组件共用 `$\JasmOilPhase0\oil_body`；一次菜单点击同时控制两份文件的网格，
  日志里两个文件的所有组件都带着同一个 `oil_body` 条件。多 ini 共享变量成立。

UI 冒烟（2026-11-09，集成分支构建）：

- 详情页「开启油光」→ 状态变为「已开启（身体 / 衣物）」；`JASM_Oil.ini`、
  `JASM_Oil/manifest.json`、`Matcap.ini`、`Oil/`、`mod.ini.jasm_oil.bak` 全部落盘；
- F7 菜单前三行是「油光总开关 / 身体油光 / 衣物油光」（注入行，中文标签）；
- 实机画面油光可见；「撤销油光」能完整还原作者 ini 并删除 JASM 产物。

自动分组修正（同日）：仅按索引数会选中 `$draw_component_*` 默认关闭、实际不绘制的网格
（例如 `_3_005` / `_3_022`），补丁打上但默认造型看不到油光。新增
`OilGlossConditionEvaluator`：读取 `[Constants]` 的 `global` 默认值，再让
`[CommandListProcessToggles]` 里的重算赋值覆盖它，自动分组优先选默认可见的 drawcall。
本机 rover Mod 因此从 `_3_005`/`_3_022` 改为 `_3_002`（身体）/ `_3_028`（衣物）。

---

## 目标 / 非目标

### 目标

- 任意鸣潮 Mod 在详情页点「开启油光」后，获得：
  - 本体资产已就位；
  - 身体 / 衣物 drawcall 已按自动分组包好；
  - F7 菜单多出「油光总开关 / 身体油光 / 衣物油光」三行，点击即生效；
  - 状态可查、可一键撤销、备份可还原。
- 补丁不改动原作者文件的**其它**内容，编码、换行符、缩进原样保留。
- 撤销后 Mod 回到开启前的字节状态（在文件未被外部修改的前提下）。

### 非目标（本版明确不做）

- 不做人工 drawcall 分组表格（全自动；精度不够时只提供「重新猜测」）。
- 不做油光强度 / 样式的菜单行（保留本体自带的 `*` / `+` / `-` 物理键）。
- 不改 WinUI 的「按键映射」面板。
- 不支持鸣潮以外的游戏（GIMI / Honkai / ZZZ 一律不显示入口）。
- 不自动应用到所有 Mod（必须逐个手动开启）。
- 不做「Mod 更新后自动重打补丁」（更新会覆盖补丁，状态会如实显示为未开启）。

---

## 用户故事

### Story 1：详情页一键开启

**作为** 鸣潮 Mod 玩家
**我想要** 在详情页点一下就给这个 Mod 加好油光
**以便** 不用开命令行、不用自己找 ini、不用手填 drawcall 编号

- [ ] 详情页出现「油光」区块，显示未开启 / 已开启 / 失败原因。
- [ ] 点击「开启油光」后，Mod 根目录出现 `Matcap.ini` 与 `Oil/`（若原本没有）。
- [ ] 自动选出身体 / 衣物两组 drawcall，并写入补丁。
- [ ] 原本已有 KatFXMatcap 本体的 Mod 不会被覆盖，只补分组。
- [ ] 不满足条件的 Mod（无 `[TextureOverride]` / 无 `drawindexed`）按钮禁用并说明原因。

### Story 2：F7 菜单控制

**作为** 玩家
**我想要** 在 F7 菜单里直接开关油光，并且身体和衣物能分开
**以便** 只想让皮肤亮或只想让衣服亮时不用记热键

- [ ] 开启油光后，F7 菜单固定出现三行：`油光总开关`、`身体油光`、`衣物油光`，值为 0/1。
- [ ] 点击 `身体油光` 只影响身体组，`衣物油光` 只影响衣物组，网格本身始终正常显示。
- [ ] 点击 `油光总开关` 关掉后，所有油光都不生效；再点开恢复原来的身体/衣物开关状态。
- [ ] 三行固定在菜单第一页最前面。
- [ ] 菜单的「恢复默认」把三行都恢复为 1。
- [ ] 本体自带的 `*` / `+` / `-` 物理键继续可用，且不会在菜单里额外多出行。

### Story 3：撤销与状态

**作为** 维护者 / 玩家
**我想要** 随时撤销，并且知道当前状态
**以便** 出问题时能干净回到原样

- [ ] 详情页「撤销油光」恢复所有被补丁的 ini。
- [ ] 撤销同时删除 JASM 补进去的本体文件（仅限 JASM 写过的、且未被修改的）。
- [ ] 撤销后重新生成菜单，三行油光消失。
- [ ] 若文件在开启后被外部改过（哈希与记录不符），拒绝直接覆盖并给出明确提示。

---

## 方案设计

### 总体流程

```
详情页「开启油光」
        │
        ▼
1. 游戏检查：GameShortName == "WuWa"
2. 扫描 Mod：有 [TextureOverride*] + drawindexed 吗？
3. 本体：已存在 KatFXMatcap → 接管；否则从内嵌资源复制
4. 自动分组：身体 = 最大块；衣物 = 排除头部后最大块
5. 写 JASM_Oil.ini（变量 + 3 个菜单行）
6. 逐个 ini 插入门控（备份 .jasm_oil.bak）
7. 重新生成 F7 菜单（若已存在）
8. 状态写入 .JASM_ModConfig.json
```

### 1. 本体资源分发

**来源**：`oil_sc/油腻高光特效-任意角色（＊-+）by neerajksah.exe`（WinRAR SFX，解压码由用户提供）。

解包后实际需要的内容：

```
Matcap.ini
Oil/MC1.dds  MC4.dds  MC5.dds  MC6.dds  Mask.dds
Oil/Oil.dds  Oil1.dds  Oil2.dds  OIl_MC.dds
Oil/Oil_Normal.dds  Empty_Normal.dds  Wet.dds
```

不需要：`preview*.jpg`、`mod主站网址.url`。

**落库位置**：`src/GIMI-ModManager.WinUI/Assets/OilGloss/`，按现有内置引擎的做法打成 `EmbeddedResource`
（参考 `Assets\WuwaModFixer\**` 与 `Assets\elevator`），保证 SingleFile 发布也拿得到。

```xml
<ItemGroup>
  <EmbeddedResource Include="Assets\OilGloss\**\*">
    <LogicalName>JASM.OilGloss.%(RecursiveDir)%(Filename)%(Extension)</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

**运行期复制**：写到 Mod 根目录（与 `mod.ini` 同级）。只写**不存在**的文件；已存在的不覆盖。
把写过的文件记录到状态里，撤销时只删「JASM 写的 + 内容未变」的那些。

### 2. 自动分组（全自动猜测）

扫描规则（移植自 `split_oil.py`，并收紧到 `[TextureOverride*]` 段）：

- 遍历 Mod 目录下 `*.ini`；
- 跳过：`JASM_Menu.ini`、`JASM_Oil.ini`、`*.bak`、`*.jasm_oil.bak`、`DISABLED_*` 路径；
- 只处理同时包含 `[TextureOverride` 与 `match_first_index` 的文件；
- 只包裹 `[TextureOverride*]` 段内的 `drawindexed` / `draw` 行（透明层的命令内绘制不包）。

分组：

1. 排除头部：条件变量 / 贴图槽 / 紧邻注释里出现 `hat / hair / face / eye / brow / head`；
2. **优先默认可见**：读取 `[Constants]` 的 `global` 默认值，再让
   `[CommandListProcessToggles]` 里的 `$draw_component_* = (...)` 重算赋值覆盖它，
   只在默认可见的 drawcall 里选；求不出可见性的按可见处理，全部被判定隐藏时退回全体；
3. **身体组** = 候选里索引数（`drawindexed` 第一个参数）最大的一块；
4. **衣物组** = 除身体外最大的一块；
5. 只有一块 drawcall 时：身体组 = 它，衣物组为空；
6. 衣物组为空时，菜单不显示「衣物油光」行。

**已知局限**（写进详情页说明）：

- 一个 drawcall 里身体和衣服合在一起时分不开，只能整块归到身体；
- 带裙摆/披风的包可能把布料判进衣物组之外；
- 自动分组不提供人工修正（本版非目标）。

### 3. 补丁写法

**共享变量文件**：Mod 根目录新增 `JASM_Oil.ini`（JASM 生成，可整体删除）。
只声明变量，不写 `[Key*]`（菜单行由生成器注入，见第 4 节）。

```ini
; ===== generated by JASM - oil gloss (do not edit) =====
namespace = JasmOil<token>

[Constants]
global persist $jasm_oil_on = 1
global persist $oil_body = 1
global persist $oil_cloth = 1

[End of file.]
```

> 为什么变量单独放一个文件：一个 Mod 可能有 `norm/mod.ini`、`ult/mod.ini` 等多个被补丁的文件，
> 如果变量写在各自文件里，菜单一行只能改到其中一个命名空间。集中到 `JasmOil<token>` 后，
> 所有被补丁的文件引用同一组变量。

**作者 ini 里的包裹**（身体组；衣物组把 `oil_body` 换成 `oil_cloth`）：

```ini
if $\JasmOil<token>\jasm_oil_on
if $\JasmOil<token>\oil_body
run = CommandList\KatFXMatcap\OilSkin
endif
endif
drawindexed = 161130, 212301, 0
if $\JasmOil<token>\jasm_oil_on
if $\JasmOil<token>\oil_body
run = CommandList\KatFXMatcap\Clear
endif
endif
```

- 3DMigoto 的 `if` 不支持 `&&`，所以总开关与分组开关用**嵌套 if**。
- `if` 只包油光的两条命令，**不包 `drawindexed`**：关掉油光时网格照常绘制。
- 命名空间按本体文件里的写法 `KatFXMatcap`（ini 段名/命名空间大小写不敏感，
  `split_oil.py` 验证过 `KatFXMatCap` 也能用）。

**幂等**：文件里已出现 `$jasm_oil_on` 或紧贴的 `OilSkin` 门控时跳过，不重复嵌套。

**写盘**：复用 `IniFileText.ReadAllText` / 原编码回写；首次补丁前复制
`<file>.jasm_oil.bak`；写临时文件后原子替换。

### 4. 接入 F7 菜单

**不往 ini 里写 `[Key*]` 段**，而是给生成器注入「额外行」：

- `InGameMenuOptions.ExtraRows`：由 `OilGlossMenuRows.Create(manifest)` 生成三行
  （总开关 / 身体 / 衣物），固定拼在作者行之前 → 菜单第一页最前面。
- 这样既避免「没有 `key =` 的 `[Key*]` 段是否被 3DMigoto 接受」这个未知风险，
  也方便直接给中文标签（`油光总开关` / `身体油光` / `衣物油光`）。

配套的两处生成器修正：

- **排除本体自带的按键行**：`Matcap.ini` 的 `[KeyOilToggle]` / `[KeyOilIncrease]` /
  `[KeyOilDecrease]` 属于 `namespace = KatFXMatcap`，由
  `InGameMenuOptions.ExcludedNamespaces` 过滤掉（物理键 `*`/`+`/`-` 仍然生效）。
- **检测命名空间来源**：原来取 `rows.FirstOrDefault(非空 Namespace).Namespace` 作为
  `$\ns\object_detected` 的来源。注入行可能排在最前，导致门控变量指向一个不存在的命名空间。
  改为 `PickGateNamespace`：优先取「该文件里真的声明了 `$object_detected`」的命名空间。

开启 / 撤销油光后：
  - 若菜单已生成 → 重新生成；
  - 若未生成 → 不强制生成（保持现有「详情页手动生成菜单」的语义）。

### 5. 状态与撤销

状态**不写进** `.JASM_ModConfig.json`，而是跟 F7 菜单一样落在 Mod 目录里：
`JASM_Oil/manifest.json`。这样状态跟着 Mod 走，导出 / 换机 / 重装也不会丢，
且不需要给 `ModSettings` 加字段（避免旧配置兼容问题）。

```json
{
  "ManifestVersion": 1,
  "GeneratorVersion": 1,
  "Namespace": "JasmOilA1B2C3D4",
  "BodyProvidedByJasm": true,
  "BodyFiles": [
    { "RelativePath": "Matcap.ini", "Sha256": "…" },
    { "RelativePath": "Oil/MC1.dds", "Sha256": "…" }
  ],
  "PatchedFiles": [
    {
      "RelativePath": "mod.ini",
      "BackupRelativePath": "mod.ini.jasm_oil.bak",
      "PatchedSha256": "…"
    }
  ],
  "HasBodyGroup": true,
  "HasClothGroup": true,
  "AppliedAtUtc": "2026-11-09T12:00:00Z"
}
```

状态由文件系统推导（`OilGlossPatcher.GetStatus`）：

| 状态 | 判据 |
|---|---|
| `NotApplied` | 没有 manifest，也没有 `JASM_Oil.ini` |
| `Applied` | manifest 存在、`JASM_Oil.ini` 存在、每个补丁文件哈希与记录一致 |
| `Broken` | manifest 在但 `JASM_Oil.ini` / 补丁文件 / 备份丢失（例如 Mod 被更新覆盖） |
| `Conflict` | 补丁文件在补丁后被外部修改（撤销会覆盖用户改动） |

撤销流程：

1. 对每个 `PatchedFiles`：
   - 当前文件 SHA256 == `PatchedSha256` → 用备份覆盖，删除备份；
   - 不相等 → 整次撤销中止，列出冲突文件，状态置 `Conflict`（不覆盖用户的改动）。
2. 删除 `JASM_Oil.ini`。
3. 删除 `BodyFiles` 中「JASM 写的 + 当前内容未变」的文件；作者原有文件不删。
4. 状态改 `NotApplied`，重新生成菜单。

### 6. UI

详情页 `ModPane.xaml` 的「游戏内菜单」按钮行下方（约 `:258-279`）新增一块：

```
油光：未开启                    [开启油光]
```

- 已开启时显示 `油光：已开启（身体 1 组 / 衣物 2 组）` + `[撤销油光]`。
- 失败时显示原因（无 drawcall / 无写权限 / 文件冲突 / 非鸣潮 Mod）。
- 非 `GameShortName == "WuWa"` 时不显示该区块。
- 操作期间禁用按钮，完成后刷新按键面板与菜单状态。

### 7. 与现有模块的关系

| 模块 | 复用 / 新建 |
|---|---|
| `IniFileText` | **复用**：编码探测与原编码回写 |
| `UhdAliasImportService` 的备份 + 原子写 | **借鉴**：不直接调用 |
| `ModIniToggleParser` | **复用**：解析作者行；本体行由生成器按命名空间过滤 |
| `InGameMenuGenerator` | **小改**：注入行置顶、`ExtraRows`、检测命名空间来源修正 |
| `InGameMenuService` | **扩展**：`ToOptions` 读取 manifest 并注入三行；开启 / 撤销后重建菜单 |
| `ModSettings` / `.JASM_ModConfig.json` | **不改**：状态落在 `JASM_Oil/manifest.json` |
| `ISkinMod` / `SkinMod` | **复用**：定位目录、读取 ini |
| 内嵌资源分发（WuwaModFixer / Elevator） | **复用模式**：`EmbeddedResource` + `LogicalName` |
| 新增 Core 服务 | **新建**：`Core/Services/OilGloss/` |

---

## 数据与接口变更

### 新增 Core 类型（`src/GIMI-ModManager.Core/Services/OilGloss/`）

- `OilGlossPatcher`：`Scan` / `Apply` / `Undo` / `GetStatus`。
- `OilGlossScanner`：收集 drawcall 与分组（纯函数，便于单测）。
- `OilGlossBodyProvisioner`：从内嵌资源复制本体、判断「已有本体」。
- `OilGlossState` / `OilGlossFileRecord`：状态模型。
- `OilGlossOptions`：注入的资源读取委托（便于测试）。

### `ModSettings` 不新增字段

状态改为 Mod 目录内的 `JASM_Oil/manifest.json`（见第 5 节），避免改
`.JASM_ModConfig.json` 的结构与旧配置兼容。

### `InGameMenuOptions` 新增字段

- `ExtraRows`：调用方注入的行（油光三行），固定置顶。
- `ExcludedNamespaces`：默认包含 `KatFXMatcap`，过滤本体自带的强度/样式按键。

### 菜单生成行为变化

- `ModIniToggleParser` 跳过 `namespace = KatFXMatcap` 的行。
- `gateNamespace` 改为取声明 `$object_detected` 的文件的命名空间。

---

## 边界与异常

| 场景 | 行为 |
|---|---|
| 非鸣潮游戏 | 详情页不显示油光区块 |
| Mod 无 `[TextureOverride]` / 无 `drawindexed` | 按钮禁用，提示「没有可包油光的网格」 |
| Mod 目录只读 / 写入失败 | `Failed`，不阻断其它功能，给出可行动原因 |
| 已有 `Matcap.ini` / 已有 `namespace = KatFXMatcap` | 不覆盖，接管现有本体 |
| 已手动用 `split_oil.py` 包过 drawcall | 检测到 `OilSkin` 命令但不含本补丁命名空间 → **拒绝**并提示「已有油光包裹，请先撤销原补丁」。本版不自动改写别人的包裹（会叠两层油光） |
| Mod 有 `norm/`、`ult/` 多套 ini | 每个文件都包；共享 `JasmOil<token>` 变量，菜单一行同时控制两套 |
| 只有一块 drawcall | 只有身体组；菜单不显示衣物行 |
| 衣物组为空 | 同上 |
| 一个 drawcall 同时含身体与衣物 | 整块归身体组（自动分组不可分时的既定行为） |
| 文件在开启后被外部修改 | 撤销时检测哈希，冲突则中止并提示 |
| Mod 更新 / 重装 | 补丁被覆盖；`.JASM_ModConfig.json` 若保留会显示已开启但实际无补丁 → 读取时校验 `JASM_Oil.ini` 与备份是否存在，缺失则降级 `NotApplied` |
| 游戏正在运行 | 只写文件，不要求关闭游戏；由 3DMigoto 重新加载后生效（F10 不重载 Mod ini，需冷启动） |
| 菜单已生成 | 开启 / 撤销后自动重建 |
| 菜单未生成 | 不自动生成，保持用户手动语义 |
| `$\JasmOil<token>\var` 跨命名空间条件不被支持（Phase 0 失败） | 回退方案：变量写进主 ini 的命名空间，仅支持单套 ini；多套 ini 场景标注不支持 |

---

## 验收标准（可逐条勾选）

### 开启

- [ ] 鸣潮 Mod 详情页出现「开启油光」；非鸣潮不出现。
- [ ] 点击后 Mod 根目录出现 `Matcap.ini`、`Oil/`、`JASM_Oil.ini`。
- [ ] 被补丁的每个 ini 都有 `.jasm_oil.bak`，且除新增门控外内容不变（编码/换行保留）。
- [ ] 身体组 = 索引数最大块；衣物组 = 排除头部后次大块；头部/头发/脸不被包。
- [ ] 文件里每个 `OilSkin` 与 `Clear` 成对出现，`if/endif` 配平。
- [ ] 重复点击「开启」不会产生嵌套重复的门控。

### 菜单

- [ ] 已生成菜单的 Mod 开启后，F7 菜单第一页最前面出现三行油光开关。
- [ ] 三行的值在 0/1 之间切换；点击后同帧或下一帧画面变化。
- [ ] 关闭身体油光后，身体网格仍正常显示，只是不亮。
- [ ] 关闭总开关后，三组油光全部失效；重新打开恢复各分组原状态。
- [ ] 本体自带的 `*` / `+` / `-` 仍可用，且菜单里没有它们对应的额外行。
- [ ] 菜单「恢复默认」把三行恢复为 1。

### 撤销

- [ ] 撤销后所有被补丁的 ini 与开启前逐字节一致（无外部修改前提下）。
- [ ] `JASM_Oil.ini` 与 JASM 写入的本体文件被删除；作者原有文件保留。
- [ ] 菜单重新生成后油光三行消失。
- [ ] 外部改过文件时撤销给出冲突提示，不覆盖用户改动。

### 回归

- [ ] 未开启油光的 Mod 行为完全不变。
- [ ] 既有 F7 菜单的生成 / 移除 / 自定义行顺序不受影响。
- [ ] WinUI 按键映射面板行为不变。
- [ ] `dotnet build` 与发布脚本通过；不新增 NuGet 依赖。

---

## 测试计划

### 单测（`JASM.Tests`）

- `OilGlossScanner`
  - 单文件单 drawcall / 多 drawcall / 多 ini；
  - 头部关键词排除；
  - 最大块 = 身体、次大块 = 衣物；
  - 只有一块时衣物为空。
- `OilGlossPatcher`
  - 门控插入位置正确、成对、缩进保留；
  - `if/endif` 配平（depth 归零、无负数）；
  - 幂等：二次 Apply 不重复；
  - 编码：UTF-8 / UTF-8 BOM / GB18030 / UTF-16 回写一致；
  - 撤销：哈希一致时还原、不一致时冲突。
- `JASM_Oil.ini` 生成：变量、三个 Key 段、命名空间、生成标记。
- `ModIniToggleParser`：排除 `KatFXMatcap` 命名空间；解析 `JASM_Oil.ini` 三行。
- `InGameMenuGenerator`：油光行置顶；`gateNamespace` 取 `$object_detected` 所在文件。

### 组件 / 集成

- 详情页开启 → 文件落盘 → 菜单重建 → 状态刷新（临时目录 + 假游戏信息）。
- 撤销 → 字节级还原 → 菜单重建。
- 已有本体的 Mod：不覆盖、只补分组。
- 已有 `split_oil.py` 补丁：不重复包裹。

### 实机（Phase 0 + 回归）

| 场景 | 验证点 |
|---|---|
| 鸣潮 + 一个普通角色 Mod | 本体加载、三行出现、点击生效 |
| `$\JasmOil<token>\var` 跨命名空间 `if` | 条件是否被 3DMigoto 正确解析 |
| 注入行（不依赖 `[Key*]`） | 菜单第一页最前面出现三行、值 0/1 |
| `norm` + `ult` 双套 ini | 一行开关是否同时控制两套 |
| 关闭身体油光 | 网格仍在、仅无油光 |
| 关闭总开关 | 全部失效 |
| 撤销后冷启动游戏 | 完全恢复原样 |

**Phase 0 结果**：

1. 跨命名空间 `if $\ns\var`：**通过**（日志 + 世界画面确认）；
2. 菜单行读写同一变量：待真机点击确认（变量机制与门控相同，无额外风险）。

补充结论：角色图鉴 / 角色选择界面不显示 matcap 油光，属于该场景的 shader 路径差异，验收以游戏世界为准。

---

## 分期

### Phase 0：实机验证（必须先做）

用一个现成鸣潮 Mod，手写 `JASM_Oil.ini` + 一个 drawcall 的门控，验证上表前三项。约 0.5 天。

### Phase 1：MVP

1. 内嵌本体资源与复制 / 接管。
2. 扫描 + 自动分组 + 补丁 + 备份 + 撤销。
3. `JASM_Oil.ini` 生成与菜单三行。
4. 详情页区块与状态。
5. 菜单生成器的置顶 / 排除 / 检测命名空间修正。
6. 单测与实机回归。

### Phase 2（后续，不在本版）

- 人工分组的 drawcall 表格。
- 强度 / 样式菜单行。
- Mod 更新后自动重打补丁。
- 非鸣潮游戏的适配验证。

---

## 风险与对策

| 风险 | 概率 | 影响 | 对策 |
|---|---|---|---|
| **本体版权 / 分发授权**：KatFXMatcap 来自 GameBanana `mods/733773`（作者 neerajksah），随 JASM 内置分发需注明来源并确认授权 | 中 | 高 | 资源目录与关于页注明作者/来源；发布前确认授权；若不允许，退回「首次使用下载」 |
| 跨命名空间 `if` 不被支持 | — | — | **已验证通过（2026-11-09）**：条件编译执行、变量切换可见 |
| 角色图鉴 / 角色选择界面不显示油光 | 高 | 低 | 已知场景差异（该场景不走 matcap shader）；验收以游戏世界为准 |
| 多个 Mod 同时启用油光时共享 `KatFXMatcap` 命名空间，出现重复 `[CommandList]` / `[Resource]` 段 | 中 | 中 | 素材与段内容完全一致，预期后者覆盖前者、行为不变；Phase 0 用两个角色同时开启验证。若冲突，改为每个 Mod 复制一份并重命名 namespace |
| 自动分组把衣物判错 | 中 | 中 | 详情页显示分组结果（各组 drawcall 数与索引范围）；提供「重新开启」重算 |
| 修改作者 ini 导致 Mod 异常 | 中 | 高 | 备份 + 哈希校验 + 一键撤销；门控只加不删 |
| 多套 ini 共享变量失败 | 低 | 中 | Phase 0 覆盖 `norm/ult`；失败时降级并提示 |
| Mod 更新覆盖补丁 | 高 | 低 | 状态读取时校验产物存在性，缺失降级为未开启 |
| 文件被游戏/杀软占用 | 低 | 低 | 捕获 IO 异常，给出可重试提示 |

---

## 开放问题

1. 本体资源是否需要同时上传到 COS 作为「更新用」？本版只做随包内置。
2. 开启油光后是否自动打开 F7 菜单（若尚未生成）？当前取「不自动生成」。
3. 三行油光的中文标签是否允许用户自定义？当前固定。

---

## 附：需求确认记录（2026-11-09）

- 本体来源：随 JASM 内置分发（解压码已提供，已核对 `Matcap.ini` / `Oil/*.dds`）。
- 补丁策略：允许改作者 ini，带备份与撤销。
- 菜单落点：F7 游戏内菜单。
- 选项粒度：总开关 + 身体 / 衣物。
- 分组：全自动。
- 时机：详情页手动开启。
- 总开关：新增 `$jasm_oil_on`。
- 物理热键：不加。
- 已有本体：识别并接管。
- 游戏：只做鸣潮 WWMI。