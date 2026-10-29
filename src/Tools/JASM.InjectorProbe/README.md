# JASM.InjectorProbe —— 注入通道可行性探针（Phase A1）

按 [`docs/injection-capability-prd.md`](../../../docs/injection-capability-prd.md) 的 Phase A1 实现：
**只回答一个问题 —— 直接注入通道在本机稳不稳。** 不注入游戏、不改任何产品代码、不进发布包。

> 本目录刻意**不加入** `GIMI-ModManager.sln`，也不被主程序引用。删掉它，产品行为完全不变（PRD 决策 1）。

---

## 怎么跑

```powershell
# 普通流程：启动靶进程 → 注入 → 核对模块
dotnet run -c Release -p:Platform=x64 --project . -- matrix --count 50

# 冻结流程：启动后冻结全部线程 → 注入 → 解冻 → 核对
dotnet run -c Release -p:Platform=x64 --project . -- suspended --count 20

# 失败矩阵自检（每项都必须被正确分类）
dotnet run -c Release -p:Platform=x64 --project . -- negative

# 往已存在的进程注入（带保护性拒绝）
dotnet run -c Release -p:Platform=x64 --project . -- once --pid 12345 [--dll 名字或路径]

# 作业模型：先看计划（不注入），再执行
dotnet run -c Release -p:Platform=x64 --project . -- plan  --process 某进程 --dll a.dll --dll b.dll
dotnet run -c Release -p:Platform=x64 --project . -- apply --pid 12345 --dll a.dll --delay 500

# 加 --verbose 看每一步的地址与错误码
```

`plan` / `apply` 是 Phase B 的作业模型（`InjectionJob` → `InjectionRunner`）：
- **纯数据 + 纯校验**：目标、DLL 列表、通道、延迟、是否冻结线程；
- **同名进程多开直接拒绝**而不是随便挑一个（挑错目标比失败更糟）；
- **第一个 DLL 失败就停**，不继续投后面的；
- `--channel hook` 会明确报「尚未实现（Phase A2）」，不会静默降级成直接注入。

默认注入目标是 **`winmm.dll`**：系统自带、无副作用，且注入前一定不在目标模块表里 ——
这样"模块出现了"才是真验证，不会被已经加载的 DLL 蒙混过去。

靶进程是自建的 `cmd.exe /c ping ...`，存活约 30 秒，用完即杀。

---

## 本机实测结果（2026-10-29，Windows 11 x64）

| 项目 | 结果 |
|---|---|
| 普通流程 | **50/50 成功（100%）**，中位 1.2 ms / P95 2.2 ms |
| 冻结流程 | **10/10 成功（100%）**，中位 1.3 ms / P95 4.7 ms |
| 失败矩阵 | 自注入 / 不存在的 PID / 不存在的 DLL / 正常注入 —— 四项分类全部正确 |

对照 PRD 的 KPI（成功率 ≥98%、零崩溃、可诊断）——**A1 通过**。

---

## 三条踩出来的结论（都写进了代码注释）

### 1. `CREATE_SUSPENDED` 的进程不能注入

`CreateProcess` 带 `CREATE_SUSPENDED` 时，目标还停在 ntdll 加载器初始化之前，
**kernel32 都还没映射**，模块表为空 → 远程 LoadLibrary 必然失败。
本机实测：`suspended` 模式最初就是这么写的，**0/10 全败**。

正确姿势是第三方管理器那种：**先让进程跑起来 → 冻结全部线程 → 注入 → 解冻**。
本探针的 `ThreadFreezer` 就是这一步（`Thread32First/Next` + `OpenThread` + `SuspendThread`）。

### 2. P/Invoke 的 `CharSet` 写错会让 API 静默返回 0

- `GetProcAddress` 只有 ANSI 版本、没有 W/A 后缀：写成 `CharSet.Unicode` 会把字符串按 UTF-16
  传进去，**返回 NULL 且 `GetLastError` 为 0**。本探针第一次跑就是这么挂的（现象：`LoadLibraryW=0x0`）。
  正解：`ExactSpelling = true, CharSet = CharSet.Ansi`。
- 反过来，`GetModuleHandleW` 必须显式 `CharSet.Unicode`，否则字符串变乱码、句柄恒为 NULL。

铁律：**函数名带 `W` → 配 `CharSet.Unicode`；带 `A` 或无后缀 → 配 `CharSet.Ansi`。**

### 3. kernel32 用相对偏移，不要赌绝对地址

同一次开机内，kernel32 在注入器与目标里是**同一个基址**（共享镜像，ASLR 按开机而非按进程），
所以直接把本进程的 `LoadLibraryW` 地址投过去也能work。但那只是当前环境的巧合。
本探针一律用：

```
远程起点 = 目标进程 kernel32 基址（模块枚举得到） + (本进程 LoadLibraryW − 本进程 kernel32 基址)
```

---

## 安全边界

- **绝不注入游戏进程**：`once --pid` 对已知游戏名（鸣潮/原神/崩铁/绝区零/终末地/异环）直接拒绝。
- 只注入**系统 DLL**；`--dll` 传裸名字时一律解析到 `system32`，避免命中同目录同名文件。
- 靶进程用完即 `TerminateProcess`，不留残留。
- 本工具只做"通道验证"，**不包含** Hook(`WH_CBT`) 通道、不含任何 UE 反射逻辑；
  后者需要导出 `CBTProc` 的原生 DLL，本机没有 MSVC，得走 CI 或另装 VS Build Tools。

---

## 文件

| 文件 | 作用 |
|---|---|
| `InjectionJob.cs` | **作业模型**（Phase B）：目标 / DLL 列表 / 通道 / 延迟 / 冻结开关 + 纯校验 |
| `InjectionRunner.cs` | **执行层**（Phase B）：解析目标 → 延迟 → 冻结 → 逐个注入 → 解冻 → 汇总 |
| `RemoteLoadLibrary.cs` | 直接注入通道（OpenProcess → VirtualAllocEx → WriteProcessMemory → CreateRemoteThread → 核对） |
| `ThreadFreezer.cs` | 冻结/解冻目标全部线程（"挂起式注入"的真实做法） |
| `ModuleProbe.cs` | 模块枚举：结果核对 + 取目标 kernel32 基址 |
| `TargetProcess.cs` | 自建靶进程（`cmd.exe`），供 matrix/suspended 使用 |
| `InjectionOutcome.cs` | 结果分类枚举 + 人话描述（PRD 的可诊断性要求） |
| `Program.cs` | 子命令与统计输出 |

## 进度

- ✅ **Phase A1**：直接注入通道验证通过（50/50、10/10）。
- ✅ **Phase B（第一段）**：作业模型 + 执行层 + dry-run（`plan`）落地；通道以 `InjectionChannelKind` 抽象。
- 🟡 **Phase A2（Hook 通道）**：C# 侧已实现（`hook` 子命令 + `CbtHookInjector` + 建窗子进程）；
  原生 DLL 源码与 CI 工作流已就绪，但**还没编出产物、没跑过端到端** —— 需要一个导出 `CBTProc` 的 `cbtprobe.dll`
  （本机无 C++ 编译器，见 [`native/README.md`](native/README.md)）。
  已验证的部分：DLL 不存在 / DLL 没有 `CBTProc` 两条失败路径分类正确；建窗子进程能正常建窗并自动退出。
- ⛔ **插件本体（UE 反射 / LOD / mip）**：不在本工具范围，见分析文档 §7。
