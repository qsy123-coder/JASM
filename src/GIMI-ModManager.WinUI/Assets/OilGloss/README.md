# KatFXMatcap（油光本体）

本目录是随 JASM 分发的油光资源，运行时由 `OilGlossService` 复制进启用油光的 Mod 根目录：

- `Matcap.ini`
- `Oil/*.dds`

**来源**：GameBanana **mods/733773**（“油腻高光特效-任意角色（＊-+）”），作者 **neerajksah**。
若原作者不希望被再分发，请移除本目录，并在
`GIMI-ModManager.WinUI.csproj` 中删除对应的 `EmbeddedResource` 条目。

## JASM 对本体做的修改（从 BodyVersion 2 起）

- 补上 `[ResourceEmpty_Normal]` 定义（原包引用了却没有定义，3DMigoto 会报
  `Unrecognised entry` 并让整段 `CommandListOilSkin` 失效）。
- **删除 `CommandListOilSkin` 里的 `ps-t0 = ResourceEmpty_Normal`**。`ps-t0` 在不同
  shader 上可能是法线/漫反射/其它语义，且 `CommandListClear` 无法恢复原始绑定，
  会造成头发、衣物整块偏色或贴图被平色覆盖。油光改为只做高光叠加。
- `BodyVersion` 记录在 `JASM_Oil/manifest.json`；旧版本体（哈希未变）在打开详情页
  或浮窗刷新时自动升级。

## 变量与命令（开发者参考）

- `namespace = KatFXMatcap`
- `$oilSkin`：0 = 关；1–6 = 不同 matcap 贴图（2 = MC6 默认）；`*` 只在 1–6 之间切换，
  不再能把油光关成 0（关油光统一交给 F7 总开关）。
- `$oilStrength`：默认 0.7，范围 0.2–2.0；`+` / `-` 调整，最低 0.2。
  JASM 的 F7 菜单「油光强度」滑块上限为 2.0。
- `CommandList\KatFXMatcap\OilSkin` / `Clear`：逐 drawcall 开始 / 结束油光

补丁由 `GIMI-ModManager.Core/Services/OilGloss/` 生成，变量放在
Mod 根目录的 `JASM_Oil.ini`（命名空间 `JasmOil<8位哈希>`），状态见
`JASM_Oil/manifest.json`。