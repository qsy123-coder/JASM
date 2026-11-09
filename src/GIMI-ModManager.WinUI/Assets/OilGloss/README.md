# KatFXMatcap（油光本体）

本目录是随 JASM 分发的油光资源，运行时由 `OilGlossService` 复制进启用油光的 Mod 根目录：

- `Matcap.ini`
- `Oil/*.dds`

**来源**：GameBanana **mods/733773**（“油腻高光特效-任意角色（＊-+）”），作者 **neerajksah**。
JASM 只做原样分发，不修改素材。若原作者不希望被再分发，请移除本目录，并在
`GIMI-ModManager.WinUI.csproj` 中删除对应的 `EmbeddedResource` 条目。

## 变量与命令（开发者参考）

- `namespace = KatFXMatcap`
- `$oilSkin`：0 = 关，2 = 默认；`*` 循环
- `$oilStrength`：默认 0.7；`+` / `-` 调整
- `CommandList\KatFXMatcap\OilSkin` / `Clear`：逐 drawcall 开始 / 结束油光

补丁由 `GIMI-ModManager.Core/Services/OilGloss/` 生成，变量放在
Mod 根目录的 `JASM_Oil.ini`（命名空间 `JasmOil<8位哈希>`），状态见
`JASM_Oil/manifest.json`。