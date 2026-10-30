# Jasm.UhdMapper

注入到《鸣潮》进程的 UHD 贴图映射 DLL（PRD: `docs/uhd-runtime-mapper-prd.md` 方案 A）。

> **当前状态：Phase 1 空壳。** 只验证注入时序与数据契约，不做任何 hook。
> 游戏启动后它会往 `%LOCALAPPDATA%\JASM\UhdRuntime\Jasm.UhdMapper.log` 追加一行：

```
[2026-10-30 12:34:56.789] injected pid=12345 abi=1 module=C:\...\Jasm.UhdMapper.dll manifest=yes (2048 bytes)
```

- `manifest=yes` 说明 JASM 在 `CreateProcessW` 之前把 `active-mod.json` 写好了（契约见下）。
- `manifest=no` 说明启动时没有选中角色 / 没有已启用的 mod，或清单写入失败。

## 构建

本机（开发机）没有 MSVC，**只能由 CI 构建**：

- workflow: `.github/workflows/uhd-mapper.yml`（`windows-latest`）
- 本地产物路径：`build/Release/Jasm.UhdMapper.dll`
- 主程序 csproj 用 `Exists` 条件把这个路径内嵌进 exe，运行时由
  `UhdMapperProvisioner` 释放到 `%LOCALAPPDATA%\JASM\UhdRuntime\Jasm.UhdMapper.dll`

手动构建（有 MSVC 的机器）：

```bat
cmake -S src/Jasm.UhdMapper -B src/Jasm.UhdMapper/build -A x64
cmake --build src/Jasm.UhdMapper/build --config Release
```

调试时也可以直接把 DLL 拷到 `%LOCALAPPDATA%\JASM\UhdRuntime\` —— 提权助手只校验
文件名 `Jasm.UhdMapper.dll` 和父目录名 `UhdRuntime`。

## 数据契约

`%LOCALAPPDATA%\JASM\UhdRuntime\active-mod.json`，由 JASM 侧
`UhdActiveModManifestBuilder` 生成、`UhdActiveModStore` 原子写入：

```json
{
  "schemaVersion": 1,
  "generatedAtUtc": "2026-10-30T12:00:00+00:00",
  "game": "WuWa",
  "role": "琳奈",
  "roleInternalName": "linnai",
  "mods": [
    {
      "name": "Linnai",
      "rootPath": "D:\\...\\Mods\\Linnai",
      "iniFiles": ["D:\\...\\Linnai\\mod.ini"],
      "textures": [
        {
          "hdHash": "1998df83",
          "resourceSection": "ResourceDiffuse",
          "ddsPath": "D:\\...\\Linnai\\Texture\\Diffuse.dds",
          "overrideSection": "TextureOverrideTexture0",
          "uhdHashes": ["dd1f9bc8"],
          "iniFile": "D:\\...\\Linnai\\mod.ini"
        }
      ]
    }
  ]
}
```

- 字段名是跨语言契约，改托管侧要同步这里与 DLL 的解析。
- `schemaVersion` 不认识就整份忽略（不要猜）。
- `ddsPath` 可能为 null（资源段没有 `filename`）；`uhdHashes` 可能为空（等运行时学习）。
- 每次注入启动前 JASM 会重写一次；游戏内切换 mod 的刷新是 Phase 3。

## 安全边界（务必保持）

- 只做渲染层贴图替换：不挂钩反作弊、不改游戏逻辑、不断网、不做 PEB 摘除。
- 注入开关默认关闭；关闭时启动命令回到 XXMI Launcher。
- 解析失败/契约版本不符时**只禁用映射并写日志**，绝不崩溃游戏。
- 只用 kernel32，静态链接 CRT，避免往被注入进程里拖额外的 DLL。
