# 第三方管理器 Rocket 逆向记录：注入链路与鸣潮 UHD 贴图修复

**Date**: 2026-10-29
**分析对象**: `Rocket 管理器最新版`（桌面分发目录）
**目的**: 为 JASM 后续可能的「运行时注入」能力做技术存档；同时为 `docs/overlay-uhd-texture-fix-prd.md` 的 Phase 0 提供对照。
**性质**: 静态逆向 + 本机日志取证。未经实机注入验证的条目均标注「未取证」。

> ⚠️ 使用边界：本文仅记录对本机第三方程序的静态分析结果。向游戏进程注入 DLL 属于对游戏运行时的改写，受游戏服务条款与反作弊（鸣潮为 ACE）约束，存在封号风险，且需要长期跟随版本维护。采用前请单独评估。

---

## 0.1 分析对象与时效（先读这一节）

本文的所有结论绑定在**两个具体二进制**上，不是"Rocket 这个软件"的普遍结论：

| 分析对象 | 分析时的版本 | 证据 |
|---|---|---|
| `Rocket.exe` | 16,155,648 B，PE 时间戳 **2026-10-09 17:04:48 UTC**，SHA-256 `564f81b6…` | 已复制到 `D:\rea-work\bin\Rocket.exe` |
| `Lod0_texture.dll` | 236,032 B，构建串 **`2026-10-10-world-texture-streaming-v24`**，SHA-256 `db9ca9f1…` | 已复制到 `D:\rea-work\bin\Lod0_texture.dll` |
| `d3d11.dll`（主 DLL） | 3,333,632 B，SHA-256 `1b04e1bc…` | 至今未变，本文 §5 仍对外有效 |

**本机已于 2026-10-29 13:44 更新过（由 Rocket 自身更新器或手动更新触发）**，更新后的形态：

| 文件 | 更新后 | 变化 |
|---|---|---|
| `Rocket.exe` | 16,403,968 B，PE 时间戳 **2026-10-26 23:24:38 UTC**，SHA-256 `39abe77f…` | 新构建，多 248 KB |
| `Lod0_texture.dll` | **3,050,496 B**（13 倍），版本 **2.9**，SHA-256 `d04070ee…` | **内部名变为 `ResourcePackChanger.User.dll`，构建串 `ResourcePackChanger-1.6.2-client-fast-start-core-v25`**，并内嵌 Dear ImGui 1.92.8 + D3D11 后端（游戏内 HUD） |

**因此本文的效力边界**：
- **架构级结论仍然成立**（注入双通道、`Rocker_lod_texture` 闸门、`LOD0Texture_GetBridgeVersion/RequestPatch` 两个导出、`LODENC01` + DPAPI、UE 反射类/函数名单）—— v2.9 里这些字符串与导出**都还在**。
- **插件实现细节（§4 的地址、常量、行为序列）属于旧构建**，对 v2.9 需重导；特别是 v2.9 已从"LOD/mip 修复器"演化为**资源包切换器（ResourcePackChanger）+ 游戏内 ImGui HUD**，UHD 相关的实现路径很可能不同。
- v2.9 新增的可观测线索（未深入分析）：`[CORE][WARN] old Lod0_texture conflict=%d; concurrent writes disabled`（会检测旧版冲突）、`[Lod0Tex][ACTIVE] pawn=%p components=%d ...`、`##RocketStartupNotice`、`[OVERLAY]`。

> 复查方法：对同一路径跑 `Get-FileHash`，与上表比对即可判断本文是否过期。

---

## 0. 结论速览

Rocket 自己**不碰图形 API**，是三层结构：

```
Rocket.exe（注入器 + 配置 + Mod 管理）
   ├─ 注入 → Client-Win64-Shipping.exe（直接注入 / WH_CBT 钩子注入）
   │            └─ 主 DLL：backing\Config\WW\d3d11.dll（3DMigoto 分支）
   │                  └─ 自行加载插件：Lod0_texture.dll
   └─ 建命名共享内存 Rocker_lod_texture（插件总开关）
```

- **注入**：两种模式。直接注入 = `OpenProcess` + `VirtualAllocEx` + `WriteProcessMemory` + `CreateRemoteThread(LoadLibraryW)`；Hook 注入 = 全局 `WH_CBT` 钩子，DLL 需导出 `CBTProc`。
- **UHD 贴图修复**：靠 **UE 反射**在游戏进程内改状态 —— `SkinnedMeshComponent.SetForcedLOD(1)` + 属性写入 + 材质 `SetForceMipLevelsToBeResident` 租约续期 + `KuroStreamingQualityLevel` 强制 4。**不重写 Mod 文件**。
- 与 JASM 现有「修复」（重写 Mod 包 ini 的哈希/贴图引用）是**两条互补但不同的路**。

---

## 1. 分析对象与指纹

| 文件 | 大小 | SHA-256 | 说明 |
|---|---|---|---|
| `Rocket.exe` | 16,155,648 | `564f81b611bf2810940fdb0d95b5c9cd930b3a3bd3646b3925f4df78f9adeaaa` | PE32+ amd64，MSVC（MSVCP140/VCRUNTIME140），无签名、无版本资源，ImGui 自绘 UI |
| `backing\Config\WW\d3d11.dll` | 3,333,632 | — | **3DMigoto 分支**（`HackerDevice`/`HackerContext`/`ShaderRegex` 全在） |
| `backing\Config\WW\Lod0_texture.dll` | 236,032 | `db9ca9f1602efd726afbc510b61b0d25558d4be2c941b67f43a0f390267d8bea` | 贴图修复插件，导出 2 个 |
| `backing\Config\WW\d3dx.ini` | 52,583 | — | 3DMigoto 配置，`include = WWMI\WuWa-Model-Importer.ini` |
| `WinDivert.dll/.sys` | — | — | 网络过滤驱动（与本功能无关） |
| `UnRAR64.dll` | — | — | Mod 包解压 |

`Rocket.exe` 导入表要点：`CreateRemoteThread / VirtualAllocEx / WriteProcessMemory / OpenProcess / CreateProcessW / SetWindowsHookExW / CreateFileMappingW / MapViewOfFile / CryptProtectData(CRYPT32)`。

---

## 2. 注入实现

基址 `0x140000000`；下列地址为 Ghidra 自动命名函数入口（RVA）。

### 2.1 直接注入：远程 `LoadLibraryW`

**`FUN_1402a2280`（最小可用实现）**

```text
GetModuleHandleW(L"kernel32.dll")
GetProcAddress(_, "LoadLibraryW")                      // 本进程内地址
GetModuleHandleExW(0x6 /* PIN | UNCHANGED_REFCOUNT */, LoadLibraryW, &hK32)
GetModuleFileNameW(hK32, ...)                           // 取 kernel32 基址
offset = LoadLibraryW - kernel32Base                    // 关键：用相对偏移

VirtualAllocEx(pid, 0, wcslen(path)*2 + 2, MEM_COMMIT|MEM_RESERVE(0x3000), PAGE_READWRITE(4))
WriteProcessMemory(pid, buf, path, size)
CreateRemoteThread(pid, start = kernel32BaseRemote + offset, param = buf)
WaitForSingleObject(hThread, 15000)
GetExitCodeThread()                                     // 0x103(STILL_ACTIVE) 或 0 视为失败
VirtualFreeEx(pid, buf, 0, MEM_RELEASE(0x8000))
```

要点：**不直接把本机 `LoadLibraryW` 地址投到目标进程**，而是取 kernel32 的基址再用相对偏移重算 —— 规避进程间 ASLR 差异。

**`FUN_1402b04f0`（简化版，批量用）**：同样的 `VirtualAlloc + WriteProcessMemory + CreateRemoteThread`，但直接用本机 `GetProcAddress(kernel32,"LoadLibraryW")` 地址；`OpenProcess(0x1FFFFF)`；等待循环 100 ms/次，最长 15 s；退出码非 0 视为成功。

**`FUN_1402a2800`（带前后检查的封装）**

```text
GetFileAttributesW(dllPath)   必须存在
OpenProcess(0x10043A, pid)
模块已加载？(按模块名在目标进程枚举)
  ├─ 否 → FUN_1402a2280 (注入) → 轮询最多 50×100ms 等模块出现
  └─ 是 → 跳过注入
FUN_1402a2540 (授权握手，见 2.2)
```

### 2.2 原神特例：`RocketAuthorize` 握手

**`FUN_1402a2540`**：对 `ys.dll` 注入后还要过一次授权：

```text
LoadLibraryExW(ys.dll, NULL, LOAD_LIBRARY_AS_DATAFILE(1))
GetProcAddress(h, "RocketAuthorize")        // 拿到相对偏移
FreeLibrary

// 0x28 字节结构体
struct { u64 magic = 0x3154485455415359 /* "YSAUTHT1" */;
         u32 version = 1; u32 size = 0x28;
         u32 selfPid; u32 targetPid;
         u64 token; u64 check; }            // token/check 由 QPC + GetTickCount64 混合算出

VirtualAllocEx(0x28) → WriteProcessMemory → CreateRemoteThread(ysBaseRemote + offset, &arg)
WaitForSingleObject(10000)；退出码 == 1 才算成功
```

**含义**：被注入的 DLL 自带「只有我们的加载器能启用我」的握手，防止被别的注入器白嫖。若自研同类组件，这是可直接借鉴的反搭便车手段。

### 2.3 Hook 注入：全局 `WH_CBT`

**`FUN_1402a9da0` + `FUN_1402a9f60`**

```text
CreateMutexW(NULL, 0, L"Local\\3DMigotoLoader")   // 已存在(0xB7) → 「其他 Hook 加载器正在运行」
LoadLibraryExW(mainDll, NULL, 0x900)              // 先在本进程摸导出
GetProcAddress(h, "CBTProc")                      // 无 → 「主 DLL 不支持 Hook 模式（缺少 CBTProc）」
SetWindowsHookExW(WH_CBT(5), CBTProc, hInstance, 0)   // 线程 0 = 全局
```

游戏启动后第一个 CBT 事件触发时，系统把 DLL 映进游戏进程 —— 不写内存、不改线程，属于最"温和"的一条注入通道。

**前置校验 `FUN_1402ac740`**：

```text
GetPrivateProfileStringW(L"Loader", L"target", L"", buf, 0x8000, L"<config>\d3dx.ini")
_wcsicmp(d3dx.ini[Loader].target, 配置里的进程名)
不等 → 「Hook 模式的 d3dx.ini 加载目标与当前游戏不匹配，请检查 [Loader] target」
```

即：用户手改过 `d3dx.ini` 的 `[Loader] target` 会导致空注入，所以必须先对齐。

### 2.4 启动、延迟与收尾

- **`FUN_1402ae180`**：`CreateProcessW(game, ..., dwCreationFlags = 0x10 /* CREATE_NEW_CONSOLE */)`。
- **`FUN_1402aefa0`**：注入全部 DLL 后 `ResumeThread(h@+0x108)` + `CloseHandle`。
  - 旧构建里观察到 `CreateProcessW` 的 flags 为 `0x10`（CREATE_NEW_CONSOLE），"挂起再恢复"这条路当时**未取证**；
  - **新版 Rocket.exe（10-26）的字符串已证实这条语义**：`游戏主线程放行前未能确认主 DLL 加载`、`尚未放行本次游戏主线程`、`恢复游戏主线程失败`、`请先恢复旧版磁盘修改，再尝试内存方式` —— 即「挂起主线程 → 注入并核对 → 放行」，核对不通过就不放行。
- **`FUN_1402af350`**：注入前 `Sleep(注入延迟ms)` —— 对应 UI 设置「注入延迟(毫秒)」。

### 2.5 注入清单与字段

字符串证据（UTF-8/UTF-16 混合），构成传给注入器的作业描述：

```text
路径 / 游戏 / 滤镜 / 目录 / 进程名 / 参数 / 延迟 / 第三方路径 / 第三方启用 / 内置DLL / 额外DLL / 主DLL注入模式
游戏.开始注入 / 游戏.鸣潮贴图 / 游戏.鸣潮ini / 游戏.原神Hook / 游戏.绝区零Hook / 游戏.异环模块
模式 / 错误 / 句柄
```

- 主 DLL：`backing\Config\<游戏代号>\d3d11.dll`（YS 另有 `ys.dll`、ZZZ 用 `zz.dll`、NE 用 `dsound.dll` 代理）。
- 额外 DLL 之一：

```text
FUN_140303ea0(&entry + 0x2d0, L"\\backing\\Config\\WW\\Lod0_texture.dll")
FUN_140111320(&entry + 0x288, "鸣潮 Rocket 贴图修复组件")   // 显示名
```

- UI 文案侧证：`使用滤镜dll / 不使用滤镜dll`（ReShade 分支）、`SD/HD/UHD 资源档位`（选错会 UE4 崩溃）。

---

## 3. 贴图修复的总开关：`Rocker_lod_texture`

**`FUN_1402d6e30`**

```text
CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE(4), 0, 0x100, L"Rocker_lod_texture")
  GetLastError() == 0xB7 → 复用已有映射（日志「共享内存已存在，正在复用已有共享内存」）
MapViewOfFile(FILE_MAP_WRITE|.., 0, 0, 0x100)
memset(view, 0, 0x100)
wcsncpy_s(view, 0x80, pathString, ...)     // 写一段宽字符路径（前 128 wchar）
FlushViewOfFile(view, 0x100)
```

插件侧 `FUN_18000ca90` 只做存在性判断：

```c
h = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Rocker_lod_texture");
if (h) { MapViewOfFile(h, FILE_MAP_READ, ...); UnmapViewOfFile(...); CloseHandle(h); return ENABLED; }
return (GetLastError() == ERROR_ACCESS_DENIED(5));   // 拒绝访问也算启用
```

**设计价值**：这是一个「授权闸门」——插件默认全关，只有本家启动器建了同名映射才干活，比读配置文件更难伪造（映射名与句柄语义由内核维护）。

---

## 4. `Lod0_texture.dll`（插件本体）

### 4.1 导出面

| 导出 | 地址 | 行为 |
|---|---|---|
| `LOD0Texture_GetBridgeVersion` | `0x180010370` | `return 2;` |
| `LOD0Texture_RequestPatch` | `0x1800102d0` | `atomic_or(mask, 0x10 /*默认*/)`；把刷新截止时间推到 `GetTickCount()+6000`；`mask & 0x10` 时置位；`SetEvent(hEvent)` 唤醒工作线程 |

调用方是主 DLL（`d3d11.dll`，见 §5），不是 Rocket。

### 4.2 启动链

```text
DllMain(DLL_PROCESS_ATTACH)  FUN_180010380
  DisableThreadLibraryCalls
  hEvent = CreateEventW(NULL, FALSE, FALSE, NULL)
  CreateThread(FUN_18000f8d0)

worker FUN_18000f8d0
  初始化诊断日志（模块目录\Lod0_texture.diagnostic.log）
  读 Lod0_quality.ini → [QUALITY] enabled（DPAPI）
  RegisterWindowMessageW(L"Rocket.Lod0QualityTrial.v21")
  gate 检查（§3）→ 不通过则打日志退出线程
  GetModuleHandleW(L"Client-Win64-Shipping.exe")（取不到退回 GetModuleHandleW(NULL)）
  Sleep(3000)                        // 等游戏起渲染
  读 Lod0_scan_cache.ini，校验 GObjects/AppendString RVA；失效则现场扫描
  主循环：F9(0x7A) 触发强制刷新；每 tick 调 FUN_18000cd60；5s 一次 [PERF] 日志
  首轮成功后把扫描结果写回 Lod0_scan_cache.ini
```

### 4.3 加密容器 `LODENC01`（`Lod0_quality.ini` / `Lod0_scan_cache.ini` / 日志）

```
偏移 0 : "LODENC01"        (8B)
偏移 8 : u32 version       (1..3)，必须等于调用方期望值
偏移 12: u32 cipherLen
偏移 16: DPAPI blob
熵   = b"LOD1" + version(小端 u32)      // 8 字节
作用域 = 当前用户（CryptProtectData，flags=1 = CRYPTPROTECT_UI_FORBIDDEN）
```

- 读：`FUN_180001e90`；写：`FUN_180001600`（`FILE_FLAG_WRITE_THROUGH` + `.tmp.%lu.%ld` + `MoveFileEx` 原子替换）。
- **兼容明文**：不是 `LODENC01` 就按 UTF-16 BOM(`FF FE`) / UTF-8 BOM(`EF BB BF`) / 裸文本解析，解析成功后**就地重新加密回写**。
- 诊断日志是**多条记录追加**：首条带 `LODENC01` 头，后续每条为 `[u32 cipherLen][DPAPI blob]`（无 magic、version 固定 1）。

**本机解密脚本（PowerShell，同用户）**

```powershell
Add-Type -AssemblyName System.Security
function Decrypt-LodEnc([string]$path) {
  $b = [IO.File]::ReadAllBytes($path)
  $ver = [BitConverter]::ToUInt32($b, 8); $len = [BitConverter]::ToUInt32($b, 12)
  $c = New-Object byte[] $len; [Array]::Copy($b, 16, $c, 0, $len)
  $ent = [byte[]](0x4C,0x4F,0x44,0x31) + [BitConverter]::GetBytes($ver)   # "LOD1"+ver
  [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($c,$ent,'CurrentUser'))
}
```

### 4.4 UE 反射发现（不写死版本偏移）

| 步骤 | 函数 | 说明 |
|---|---|---|
| 定位 GObjects | `FUN_180003c60` | 在 `.data/.bss` 里扫 `TUObjectArray` 形状（objects/num/max/chunks） |
| 校验 | `FUN_180003700` | `AppendString` 特征串；两者都对才接受候选 |
| 类/函数发现 | `FUN_180009b20` | 从 `CoreUObject.Object` 自举 UClass，再按名字扫 |
| 名字解析 | `FUN_1800046b0` / `FUN_180004860` | FName → 字符串（含大小写折叠比较） |
| 属性偏移 | `FUN_180004cd0`（按名）/ `FUN_180004fb0`（批量） | 反射取 FProperty offset，走 `[D7OFFSET]` 档位兜底 |

发现目标（全部按名字，不按索引）：

```text
类    StreamableRenderAsset / Texture / StaticMeshComponent / SkinnedMeshComponent /
      SkeletalMeshComponent / SkeletalMaterial / MaterialInterface / MaterialInstance /
      PlayerController / KuroGAS.BaseCharacter
函数  SkinnedMeshComponent.SetForcedLOD
      MaterialInterface.SetForceMipLevelsToBeResident
      StreamableRenderAsset.GetKuroStreamingQualityLevel / SetKuroStreamingQualityLevel /
      GetKuroStreamingLevelState
属性  Pawn / AcknowledgedPawn / Mesh / AttachChildren / AttachParent / OverrideMaterials /
      KuroColorChangeMaterials / ForcedLodModel / MinLodModel / bOverrideMinLod /
      ComponentLODBias / bEnableGlobalLODBias / bForceTickThisFrame / bForceMeshObjectUpdate /
      SkeletalMesh / AnimClass / KuroAnimInstanceLod / TsBaseCharacter.Camp
```

已知硬编码档位（兜底，`USkeletalMeshComponent`）：`CN-173637289`、`CN-176585051`、`CN-Legacy-169995595`、`Global`、`-Auto`；参数 `Forced=0x790 Min=0x794 Flags=0x798 Bias=0x820 Mesh=0x5D0`。

### 4.5 目标筛选

- 主控：`PlayerController.AcknowledgedPawn` → pawn → `Mesh`（SkeletalMeshComponent）。
- 世界角色：`TsBaseCharacter.Camp` + AnimBlueprint 类名 `ABP_BaseRole_C` / `ABP_BaseRoleNPC_C` / `ABP_BaseRole_Seq_V2_C`。
- 两套预算：active（快速续租，间隔 500 ms 常量）与 npc（warmup + 上限），日志 `[WORLDROLE] tracked/retired/patchedLOD/mipRequests/budgetLeft`。

### 4.6 动作①：强制 LOD0（`FUN_180006f50` → `FUN_180006dc0`）

```c
// FUN_180006dc0 —— 通过 ProcessEvent 调 UFunction
if (!ctx->processEventFound 或 ctx->disabled) return 0;
processEvent = resolveProcessEvent(obj);                 // 先读 vtable，失败再按签名扫
if ((ufunc->FunctionFlags & 0x400) == 0) {              // 0x400 = FUNC_Native
    ctx->disabled = 1; log("UFunction is not native"); return 0;
}
params.target = n;                                       // int32
processEvent(obj, ufunc, &params);                       // 真调用
if (readInt(obj + offsetForcedLodModel) != n) log("verification failed");   // 读回校验
```

`FUN_180006f50` 的完整写入序列：

```text
SetForcedLOD(0)  → SetForcedLOD(1)          // 先复位再置 1（UE: ForcedLodModel 是 1-based，1 == LOD0）
read-back ForcedLodModel == 1                // 校验，不一致则记失败
MinLodModel       = 0
bOverrideMinLod  |= 1
ComponentLODBias  = 0
bEnableGlobalLODBias &= ~1
bForceTickThisFrame   |= 4                   // 常量取自 .data: 0x18001b15a = 0x04
bForceMeshObjectUpdate|= 0x40                //                             0x18001b15b = 0x40
// 沿 AttachChildren 链（最多 0x40 层）对同类组件重复
全部通过 → Beep(1000 Hz, 120 ms) 一次
任一步失败 → Beep(520 Hz, 90 ms) ×3
```

> 与 UI 文案对应：「嘀一声为修改成功，两声为失败，不嘀为注入失败」（实现里失败是 3 短音）。

### 4.7 动作②：材质 mip 常驻租约（`FUN_1800074e0`）

```c
processEvent(materialInterface, ufunc_SetForceMipLevelsToBeResident, &params);
// params 含一个 float 时长（秒）；active 与 npc 各自一套时长与续租间隔
// 维护一个按对象哈希的租约表：未到期跳过，到期重新调用
```

作用：防止 UE 纹理流送器把 Mod 的高清 mip 换掉 —— 这是「贴图突然变糊/变花」最直接的治点。

### 4.8 动作③：流送档位强制 = 4（`FUN_18000e680` / `FUN_18000ef60`）

```text
发现并校验三个 UFunction（FUNC_Native + 标志位）
FUN_18000ecc0(3, ...) 读当前 KuroStreamingQualityLevel
FUN_18000ecc0(5, ...) 读 GetKuroStreamingLevelState（二次验证）
if (current != 4) → FUN_18000ecc0(4, 4, ...) 写入
开强制时存档原值；关闭/还原时写回，并再次读回校验，不一致 → 记 FAIL 并排队下次重试
连续三次被 setter 拒绝 → 停止强制并排队还原（避免半强制状态）
```

**写入必须发生在游戏 UI 线程**：

```text
EnumWindows → GetClassNameW == L"UnrealWindow" && 同 PID && IsWindowVisible
  → tid = GetWindowThreadProcessId(hwnd)
SetWindowsHookExW(WH_GETMESSAGE(3), proc, hModule, tid)     // 线程级钩子
PostMessageW(hwnd, RegisterWindowMessageW("Rocket.Lod0QualityTrial.v21"), 0, 0)
```

热键：`Ctrl+Alt+F7`（0x11/0x12/0x76）切换强制/还原；`F9`（0x7A）触发强制刷新；均用 `GetAsyncKeyState` 轮询，不注册全局热键。

### 4.9 配置与日志（明文形态）

`Lod0_quality.ini`：

```ini
[Quality]
Enabled=1
```

`Lod0_scan_cache.ini`：

```ini
[Scan]
Format=1
ImageTimestamp=<游戏 exe 时间戳>
ImageSize=<游戏 exe 大小>
GObjectsRva=<u32>
ReferenceRva=<u32>
AppendStringRva=<u32>
```

按 exe 的 `ImageTimestamp/ImageSize` 失效 —— 游戏一更新自动重扫，这是它跨版本存活的核心机制。

---

## 5. 主 DLL（3DMigoto 分支）如何挂插件

`d3d11.dll` 内含 UTF-16 字符串：

```text
Rocker_lod_texture
Lod0_texture_event.dll
Lod0_texture.dll
Lod0_texture_loop.dll
Lod0_texture_xxmi.dll
LOD0Texture_GetBridgeVersion
LOD0Texture_RequestPatch
```

结论：**主 DLL 自己去找并加载插件**（四个文件名候选，对应不同触发策略），再 `GetProcAddress` 调 `LOD0Texture_RequestPatch`。Rocket 侧的报错也印证这一点：

> 「主 DLL 已通过 Hook 加载，但鸣潮贴图修复插件 Lod0_texture.dll 未加载成功」

—— 这是注入后去目标进程模块表做的**结果核对**，不是它自己注入插件。

`_event / _loop / _xxmi` 三个变体的分工**未取证**（推测分别对应事件驱动、轮询循环、走 XXMI 启动器三种场景）。

---

## 6. 本机实机日志（2026-10-12 11:21，pid 13612）

解密 `Lod0_texture.diagnostic.log`（45 条记录）后的关键行：

```text
[Lod0Tex] start build=2026-10-10-world-texture-streaming-v24 (scoped role LOD + target-role mip leases)
[QUALITY] mode=streaming-only target=4 enabled=1; Ctrl+Alt+F7 toggles force/restore; -1 means unqueried
[Lod0Tex] gate present -> enabled (active/playable-role skinned components only)
[Lod0Tex] base=0x0000000140000000 startupDelay=3000ms
[Lod0Tex][startup] cache validated GObjects=0x99A3070 reference=0x3796E5E AppendString=0x31F40A0; reflection still required
[Lod0Tex] selected profile=CN-Auto mode=reflection-pending gobjects=0x1499A3070 ...
[Lod0Tex] class discovery range=0x0-0x7B0F/7B0F found stream=1 tex=1 static=1 skin=1 skeletal=1 ... forceMips=1
[Lod0Tex][D7OFFSET] resolved Pawn=0x2E8 Ack=0x340 Mesh=0x310 Attach=0xE8/D0 SkinnedMesh=0x5D0 Forced=0x790 Min=0x794 Bias=0x820 Anim=0x900/0x908/0x910/0x9F0/0xA70
[QUALITY] API StreamableRenderAsset.GetKuroStreamingLevelState validated index=0x4F19
[QUALITY] bound window thread=10744; APIs execute on message dispatch
[Lod0Tex] resolved ProcessEvent index=0x49 address=0x1433F66B0 (signature)
[QUALITY] OBSERVE game=-1 render=-1 stream=4 state=0 enabled=1
[QUALITY] BEFORE game=-1 render=-1 stream=4 state=0 levels=-1; candidate target=4 thread=10744
[Lod0Tex][PERF] window=5000ms ticks=65 scannedSlots=2180308 mipCalls=0 workerMs=351.96 maxWorkMs=273.56
```

即：整条链路在本机跑通过；那次流送档位本来就是 4，故无写操作；5 s 窗口内 `mipCalls=0`（无目标角色在镜头内）。

---

## 7. 若要自研同样能力：工程要点与坑

### 7.1 可复用的模式

| 模式 | 价值 | 注意 |
|---|---|---|
| 远程 `LoadLibraryW` + **kernel32 相对偏移** | 规避 ASLR 差异，最稳的基础注入 | 目标 64 位、同会话；若目标以管理员运行需同级权限 |
| 全局 `WH_CBT` 钩子 | 不写内存、不改线程，最温和 | DLL 必须导出 `CBTProc`；仅在有 UI 线程消息泵的进程可靠 |
| 命名共享内存作闸门 | 插件默认全关，只有本家启动器能启用 | 需同权限级；`ERROR_ACCESS_DENIED` 也要当"启用"处理 |
| DPAPI + 自定义容器 | 配置/日志不被随意篡改或外泄 | 换机器/换用户即失效（有意为之），排障需要配套解密工具 |
| 反射发现 + 缓存 RVA（带 ImageTimestamp 失效） | 跨游戏版本存活 | 必须有"缓存失效→现场扫描→再缓存"的闭环 |
| 写后**读回校验** + 失败即停手/排队还原 | 避免半生效状态 | 比直接写偏移稳得多 |

### 7.2 必须自己解决的（Rocket 踩过的）

1. **`ProcessEvent` 定位**：先读对象 vtable，失败再按签名扫；调用前必须校验 `UFunction.FunctionFlags & FUNC_Native(0x400)`，非 native 直接调会崩。
2. **UI 线程约束**：UE 的 Setter 只能在游戏线程执行 —— 用线程级消息钩子 + 自定义消息把工作投递过去，**不要在工作线程直接写**。
3. **档位/状态回环**：任何"强制"都要先存原值、可还原、还原后再校验；连续被拒要停手。
4. **显存与性能**：mip 常驻有显存代价，必须做预算与租约过期（Rocket 用 active/npc 两套配额 + `[PERF]` 采样）。
5. **反作弊**：鸣潮为 ACE。注入类方案存在检测/封号风险，且每次游戏更新都要重新验证；这也是 Rocket 把「直接注入 / Hook 注入」做成可切换的原因之一。
6. **GPLv3**：3DMigoto 分支属 GPLv3 派生。**修改并分发** `d3d11.dll` 需要遵守 GPL；仅本地自用则不涉及分发。JASM 目前走 XXMI 官方包，不修改其二进制（见 `docs/xxmi-*`、`PackXxmiLauncher.py` 相关约定）。

### 7.3 若在 JASM 落地的分层建议

```text
Core/（纯逻辑，可测）
  ├─ 注入作业模型（路径/进程名/参数/延迟/DLL 列表/模式）
  ├─ 注入结果核对（目标进程模块表里是否出现指定模块）
  └─ 断言：不改游戏目录、不改画质设置

WinUI/Services/（进程与句柄）
  ├─ Injector（直接注入 / Hook 注入 两条实现，共用接口）
  ├─ 闸门共享内存的创建与回收（生命周期跟随游戏进程）
  └─ 插件加载结果核对 → 浮窗状态反馈
```

与现有浮窗能力的关系：浮窗已通过 Elevator 做提权送键/F10 刷新，注入器需要**同权限级**（游戏提权运行时，注入器也必须提权），这部分与现有 Elevator 链路有重叠，应复用而不是新建。

---

## 8. 未取证 / 存疑清单

| 项 | 状态 |
|---|---|
| `d3d11.dll` 调用 `LOD0Texture_RequestPatch` 的时机（Present 每帧 / 事件驱动） | 未反汇编 3.3 MB 分支 |
| `_event / _loop / _xxmi` 四个插件名候选的分工与选择条件 | 未取证 |
| `Rocker_lod_texture` 里写入的路径字符串用途 | 旧插件只读不取；v2.9 是否读取未确认 |
| 是否存在「挂起→注入→核对→放行」路径 | ✅ **新版已证实**（字符串：`尚未放行本次游戏主线程` 等，见 §2.4）；`CREATE_SUSPENDED` 的具体 flags 仍未直接观察到 |
| Rocket 对 UHD 的具体假设 | 旧构建只做到「钉 LOD/mip/流送」；**v2.9 改成资源包切换器**，实现路径见 §10 |
| 「UHD 贴图错误」的游戏侧根因 | 与本文无关，见 `docs/overlay-uhd-texture-fix-prd.md` Phase 0 |

---

## 9. 复现步骤（供后续验证）

1. 把目标二进制复制到 **ASCII 路径**下（中文路径会让 Ghidra headless 出问题）。
2. Ghidra 12.1.4 headless（需 JDK 21），自定义脚本导出：符号、字符串、按特征匹配函数的反编译。
   - 本次脚本存档：`D:\rea-work\scripts\DumpAll.java`、`SelectDump.java`、`DumpAt.java`
   - 产物：`D:\rea-work\out\Lod0_texture.dll.dump.txt`、`Rocket.exe.select.txt`、`Rocket.exe.targets.txt`
3. 16 MB 的 `Rocket.exe` 全量反编译很慢（`ApplyDataArchiveAnalyzer` 是瓶颈），建议先按字符串/导入调用点筛选函数再定点反编译。
4. DPAPI 配置与日志按 §4.3 脚本就地解密。

---

## 10. v2.9 初查：`ResourcePackChanger.User.dll`（进行中）

> 2026-10-29 更新后的新插件。以下为**当日初查**结果：结论标注了取证强度，未取证的明确标注。

### 10.1 身份

| 项 | 值 |
|---|---|
| 内部名 | `ResourcePackChanger.User.dll`（导出目录里的 DLL 名，已不是 `Lod0_texture.dll`） |
| 构建串 | `ResourcePackChanger-1.6.2-client-fast-start-core-v25` |
| 体积 | 3,050,496 B（旧版 236,032 B，**13 倍**） |
| 导出 | **未变**：`LOD0Texture_GetBridgeVersion` / `LOD0Texture_RequestPatch` → 与主 DLL 的桥接协议不变 |
| 泄露的 PDB 路径 | `D:\桌面\Lod逆向项目\鸣潮资源包更改\构建\输出\用户版\Release\ResourcePackChanger.User.pdb`（作者工程名："鸣潮资源包更改"） |

### 10.2 保留了什么（旧引擎整套还在，已取证）

- **日志词汇完全一致**：`[Lod0Tex] start build=…`、`gate present -> enabled`、`[D7OFFSET]`、`[WORLDROLE]`、`[ACTIVE]`、`[CAPTURE]`、`[PERF]`。
- **闸门不变**：`Rocker_lod_texture`（UTF-16 串仍在）。
- **加密容器不变**：`LODENC01` + DPAPI（"LOD1"+ver）。
- **UE 反射对象集不变**：`StreamableRenderAsset / Texture / SkinnedMeshComponent / SetForcedLOD / SetForceMipLevelsToBeResident / KuroStreaming…`，连 `Forced=0x790 Min=0x794 Flags=0x798 Bias=0x820 Mesh=0x5D0` 这套档位常量也一模一样。
- 新增缓存目录：`\Rocket\Cache\Wuwa\Startup`。
- 新增窗口消息名：`Rocket.ResourcePackChanger.Quality.v1`（旧版是 `Rocket.Lod0QualityTrial.v21`）。

**含义**：v2.9 不是重写，而是在原插件上叠加。§4 描述的 LOD/mip/流送三件事**大概率仍是有效主体**，只是外面套了一层"资源包切换 + HUD"。

### 10.3 新增了什么（按证据强度分级）

| 能力 | 证据 | 强度 |
|---|---|---|
| `.pak` 资源包扫描 | `FUN_180016b00` 按扩展名比较 `.pak`；`FUN_180017270` 从自身 DLL 路径向上两级再枚举 | 已反编译 |
| **文件级换包工具层** | 已反编译出六个薄封装：`Create_symlink`（`0xCEA10`，`CreateSymbolicLinkW` + `ALLOW_UNPRIVILEGED_CREATE`）、硬链接封装（`0xCF448`）、`CopyFileW` 封装（`0xCEC20`）、按句柄删除封装（`0xCEB70`，`FileDispositionInfo`）、`DeviceIoControl` 封装（`0xCFC9C`）、以及消费方 `0xCFCE0` 用 **`FSCTL_GET_REPARSE_POINT (0x900A8)` 读取现有链接指向** | 已反编译 |
| 线程级操作 | `SuspendThread` 调用点 `0x14A0`、`SetThreadContext` `0x12CD`、`VirtualProtect` | 调用点已定位，语义未拆 |
| 游戏内 HUD | 内嵌 Dear ImGui 1.92.8 + `imgui_impl_win32/dx11` + `D3DCompile`；`D3D11CreateDeviceAndSwapChain` 在导入表但无直接 `FF 15` 调用（应为 `GetProcAddress` 动态取） | 结构已确认 |
| 与旧插件共存保护 | `[CORE][WARN] old Lod0_texture conflict=%d; concurrent writes disabled` | 字符串 |
| 内嵌资源读取 | `FindResourceW/LoadResource/LockResource`，调用点 `0x1984A` | 调用点已定位 |

### 10.4 这套文件层到底是干什么的（已修正一次推论）

> ⚠️ 初查时的猜测是"它把游戏的 SD/HD/UHD 资源包换掉"。**继续取证后，这个猜测证据不足，已收回。**

**证据链**：

1. 四个封装（符号链接 / 硬链接 / 复制 / 删除含只读回退 / `FSCTL_GET_REPARSE_POINT` 查询）**没有被任何函数直接调用**——它们只出现在 **函数指针表**（`0x1801E1FD8`、`0x1801EA918/948/9D8/A80`）里，由一个抽象层间接派发。说明这是"文件操作接口"，上层策略另有一处。
2. `FUN_1800CFCE0` 是"**删文件并处理只读属性**"的健壮实现（`SetFileInformationByHandle(0x15)` → 失败回退按句柄删除 → 若是只读则清 `FILE_ATTRIBUTE_READONLY` 再删）。典型的"可逆安装/卸载"配套。
3. Rocket.exe（新旧两版都有）存在 **PakMod** 概念：`PakMod已启用`、`PakMod已禁用并压缩`、`pak_mod_list`、`尝试识别Pak切换键`，且有 UTF-16 路径模板 `Content/Paks`。
4. 旧引擎（LOD/mip/流送/反射）在 v2.9 里**一字未改地保留**。

**修正后的判断**：
- 这套文件层更像 **PakMod 加载器**——把以 `.pak` 形式分发的 Mod 通过链接/复制放进游戏的 `Content/Paks`，可启用/禁用/压缩，且用 `FSCTL_GET_REPARSE_POINT` 判断"当前是不是我们已经链接进去的"从而做到幂等与可逆。
- **不是**（至少未被证据支持为）"把游戏的 UHD 资源包整体换掉"。
- 「极致(UHD) Mod 自动兼容」「尚未执行资源包实验」这类串仍指向"按资源档位做兼容"，但**它靠的仍是 §4 的运行时引擎**，而不是换掉游戏 pak。

**仍未取证**：pak 的具体落点目录规则（`Content/Paks` 下哪个子目录、是否 `~mods`）、启停的时间点、是否改游戏原有 pak。

### 10.5 对 JASM 的含义

1. **UHD 贴图错误的最强参考仍然是 §4 的运行时引擎**（钉 LOD0 + mip 常驻 + 流送档位=4），它在 v2.9 中完整保留且未改动；文件层是另一件事（PakMod）。
2. 因此我们原来的判断——「Rocket 的 UHD 修复靠运行时，不是靠改文件」——**经受住了新版本检验**，`overlay-uhd-texture-fix-prd.md` 选文件级路线与 Rocket 走的是**不同**的路，风险自负。
3. PakMod 那套文件层（链接/复制 + 可逆卸载）对我们另有价值：如果 JASM 将来要支持以 `.pak` 形式分发的 Mod，这是可直接借鉴的实现范式（幂等 + 可逆 + 免管理员）。
4. 「不碰游戏本体」的约束**没有被新证据推翻**——除非我们主动要做 PakMod 支持。

---

## 附录：关键地址速查

| 对象 | 地址 |
|---|---|
| 直接注入（相对偏移版） | `0x1402a2280` |
| 直接注入（批量版） | `0x1402b04f0` |
| 注入封装 + 轮询等模块 | `0x1402a2800` |
| `RocketAuthorize` 握手 | `0x1402a2540` |
| Hook 加载器准备 | `0x1402a9da0` |
| `SetWindowsHookExW(WH_CBT)` | `0x1402a9f60` |
| `d3dx.ini [Loader] target` 校验 | `0x1402ac740` |
| 创建游戏进程 | `0x1402ae180` |
| 注入后再 ResumeThread | `0x1402aefa0` / `0x1402af350`（带延迟） |
| 闸门共享内存 | `0x1402d6e30` |
| 插件：DllMain | `0x180010380` |
| 插件：worker 主循环 | `0x18000f8d0` |
| 插件：gate 判定 | `0x18000ca90` |
| 插件：LODENC01 读/写 | `0x180001e90` / `0x180001600` |
| 插件：GObjects 扫描 | `0x180003c60`、`0x180003700` |
| 插件：类/函数发现 | `0x180009b20` |
| 插件：组件打补丁 | `0x180006f50` → `0x180006dc0`（ProcessEvent） |
| 插件：mip 租约 | `0x1800074e0` |
| 插件：画质发现/写入 | `0x18000e680` / `0x18000ecc0` / `0x18000ef60` |
| 插件：UI 线程投递 + 热键 | `0x18000f3d0` / `0x18000f4e0` |
