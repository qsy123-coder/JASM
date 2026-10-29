# cbtprobe.dll —— Hook 通道（Phase A2）的最小原生 DLL

只是为了让 `WH_CBT` 全局钩子有一个**导出 `CBTProc`** 的载体，用来验证"Hook 通道能不能把 DLL 送进目标进程"。
不碰游戏、不做逻辑：`DllMain` 写一行日志并置一个命名事件，`CBTProc` 收到 `HCBT_CREATEWND` 时再记一次。

## 为什么本机编不出来

这台机器上 `cl.exe` / `gcc` / `clang` **一个都没有**（只有 .NET SDK 9）。
和 `Elevator` 的 AOT 是同一类限制 —— 原生产物只能交给 CI 或另装工具链。

## 三条构建路径

### 1) CI（推荐，仓库已就绪）

`.github/workflows/injector-probe-native.yml`：
- 只在 `src/Tools/JASM.InjectorProbe/native/**` 变动时触发，不参与任何发布流程；
- 产物：Actions → 该次运行的 Artifacts → **`cbtprobe-x64`**；
- 下载后把 `cbtprobe.dll` 放到任意目录即可。

> 下 CI 产物请用分片并行 + 断点续传（单连接很容易被限速到几 KB/s）。

### 2) 本机装 VS Build Tools

```powershell
# 装完后（勾选 "使用 C++ 的桌面开发"）
cmake -S src/Tools/JASM.InjectorProbe/native -B build/native -A x64
cmake --build build/native --config Release
# 产物：build/native/Release/cbtprobe.dll
```

### 3) 任意 MinGW / clang（未验证）

```powershell
clang++ --target=x86_64-w64-windows-gnu -shared -O2 -o cbtprobe.dll native/cbtprobe_dll.cpp
```

## 怎么用

```powershell
# 把 cbtprobe.dll 放进探针的输出目录（或任意路径）
dotnet run -c Release -p:Platform=x64 --project . -- hook --dll .\cbtprobe.dll --timeout 10
```

期望输出：

```
Hook 通道（WH_CBT）  DLL：…\cbtprobe.dll  超时：10s
正在安装全局钩子并拉起建窗子进程……

结果：成功
      已进入 JASM.InjectorProbe（12345）
```

同时在 `%TEMP%\jasm-cbtprobe.log` 里应该看到：

```
[2026-10-29 …] pid=12345 DLL_PROCESS_ATTACH (hook injection landed)
[2026-10-29 …] pid=12345 CBTProc called (HCBT_CREATEWND)
```

## 失败时怎么读

| 结果 | 含义 |
|---|---|
| `DLL 没有导出 CBTProc` | 传错了 DLL（比如传了系统 DLL）。用 `dumpbin /exports` 或看 CI 的 "Verify export table" 步骤 |
| `SetWindowsHookExW 失败` | 钩子装不上：权限不足，或钩子类型/参数不对 |
| `钩子已安装但目标始终未触发 CBT 事件` | 目标没建窗口（探针会自己拉起一个建窗子进程，若仍超时说明钩子没生效） |

## 安全边界

- 该 DLL 只写日志、只置事件，**没有任何修改目标内存或行为的代码**；
- `hook` 子命令只对**自己拉起的子进程**验证，不接受把游戏当目标；
- 验证完立刻 `UnhookWindowsHookEx` + `FreeLibrary`，不留常驻钩子。
