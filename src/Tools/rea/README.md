# tools/rea —— 逆向辅助脚本

第三方管理器 Rocket 的静态分析留下的可复用工具。分析结论见
[`docs/rocket-injection-and-uhd-texture-analysis.md`](../../../docs/rocket-injection-and-uhd-texture-analysis.md)。

这些脚本**不属于产品构建**，不参与 CI，也不会被打进发布包。它们只是把一次性调查变成可重跑的流程。

---

## Ghidra 脚本（`ghidra/`）

需要 Ghidra 12.1+（本次用 12.1.4）与 JDK 21。用法：

```powershell
$env:GHIDRA_INSTALL_DIR = 'D:\Ghidra\ghidra_12.1.4_PUBLIC'
& "$env:GHIDRA_INSTALL_DIR\support\analyzeHeadless.bat" `
    D:\rea-work\proj MyProj `
    -import D:\rea-work\bin\target.exe `
    -scriptPath src\Tools\rea\ghidra `
    -postScript DumpAll.java D:\rea-work\out `
    -deleteProject
```

| 脚本 | 参数 | 用途 |
|---|---|---|
| `DumpAll.java` | `[输出目录]` | 全量导出：内存块、符号、字符串、**每个函数**的反编译。小目标（< 1 MB）适用；16 MB 级会非常慢 |
| `SelectDump.java` | `[输出目录]` | 只反编译「引用了关注字符串」或「调用了关注 API」的函数 + 它们的直接调用者。大目标优先用它，关注清单在文件顶部 `NEEDLES` / `IMPORTS` 里改 |
| `DumpAt.java` | `[输出目录] [地址...]` | 定点反编译：给一批地址，导出包含它的函数 + 调用者。先用字节级扫调用点定位地址，再用它拿代码 |

### 实战顺序（大目标）

1. **先别全量反编译。** 16 MB 的 MSVC 程序里 `ApplyDataArchiveAnalyzer` 会成为瓶颈，跑十几分钟还在打转；`-analysisTimeoutPerFile` 可兜底，但副作用是函数可能没被建出来。
2. 用 `SelectDump` 拿"带特征字符串的那批函数"，通常几下就能摸清架构。
3. 需要精确定位时，用 PE 节表 + IAT 做调用点扫描（`FF 15 <rel32>` → 目标 RVA），把命中的 RVA 喂给 `DumpAt`。
   - 注意：用 PowerShell 做 rel32 运算时把中间值转 `[int64]`，否则超过 `0x7FFFFFFF` 的 RVA 会抛 `UInt32` 转换异常。
4. 目标二进制先复制到**纯 ASCII 路径**再分析，中文路径会让 headless 出问题。

---

## `DecryptLodEnc.ps1`

解密 Rocket 的 `LODENC01` 容器：`Lod0_quality.ini`、`Lod0_scan_cache.ini`、`Lod0_texture.diagnostic.log`。

```powershell
.\src\Tools\rea\DecryptLodEnc.ps1 -Path 'D:\...\Config\WW\Lod0_quality.ini'
.\src\Tools\rea\DecryptLodEnc.ps1 -Path 'D:\...\Lod0_texture.diagnostic.log' -OutFile .\lod0_diag.txt
```

- DPAPI **用户作用域**：只能在生成文件的同一台机器、同一 Windows 用户下解密。换机器/换用户必然失败，这是设计而非 bug。
- 诊断日志是多条记录追加（首条有 magic，后续是 `[u32 len][blob]`），脚本会自动逐条解。
- 幂等：只读源文件，不修改任何东西。

---

## 边界

- 仅用于对**本机第三方程序**的静态分析与排障。
- 相关能力（注入、运行时改写）受游戏服务条款与反作弊约束，采用前单独评估；见分析文档 §7.2。
