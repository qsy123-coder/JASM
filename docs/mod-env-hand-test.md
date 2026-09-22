# 一键配置 Mod 环境 — 本地手测指南

针对 **Phase 1**（一键配置 Mod 启动环境）的本地验收步骤。对应 `docs/mod-env-setup-prd.md` 与
`feature/one-click-mod-env-setup` 分支。

## 0. 准备 mock CDN

已生成假包 + 版本清单到 `C:\jasm-mock-cdn\`：

```
version.json         # 含 xxmi / wwmi / launcher 三个包，URL 指向 localhost:8899
xxmi-1.0.5.zip       # 真实 XXMI 注入器框架包（3 个 DLL）
wwmi-1.0.0.zip       # 真实 WWMI 游戏包（d3d11.dll + d3dx.ini + Core\ + 空 Mods\）
launcher-2.2.1.zip   # 真实 XXMI 启动器离线包（~55MB，见 mod-env-cdn-setup.md「打 launcher 包」）
```

起一个本地 HTTP 服务：

```
python -m http.server 8899 --directory C:\jasm-mock-cdn
```

然后把 `src/GIMI-ModManager.WinUI/appsettings.json` 的
`ModEnv:ManifestUrl` 临时改成 `http://localhost:8899/version.json`，重新编译运行。

> 上线前记得改回维护者真实的国内 CDN 地址（GitHub 等海外源对国内用户不可达）。

## 1. 未安装环境（向导走通）

- 首启页/设置页出现「配置 / 更新 / 修复 Mod 环境」按钮
- 点开后：预检显示 未安装 → 开始配置 → 日志显示 下载/校验/解压/复制 → 成功
- 两个路径（MI 文件夹 / Mods 文件夹）自动填好，直接 Save

## 2. 已安装环境（自动识别）

- 预置一个「已安装」目录，例如 `D:\XXMI\WWMI\`（含 `d3d11.dll` + `d3dx.ini` + `Mods\`），
  并在 `D:\XXMI\` 写好 `.modenv.json` 标记
- 首启页自动识别并填好两个路径，不再弹向导

## 3. 幂等（已最新 / 可更新 / 可修复 / 未安装）

- 再次点按钮，预检按包显示四种状态之一
- 修改 `version.json` 里的版本号 → 显示「可更新」，点击后升到新版本

## 4. 断点续传

- 下载中途断网/杀掉进程 → 重试时从 `.part` 续传（日志应显示 Resuming from X bytes）
- SHA256 不符时删除 `.part` 重新下载

## 5. 版本兼容警告

- `version.json` 中 `wwmi.GameVersion` 与实际游戏版本不一致（或不在
  `CompatibleGameVersions` 内）→ 非阻断警告，仍可继续配置

## 6. 提权写盘降级

- 目标盘需要管理员权限时自动提权复制（复用 Elevator）
- 删除应用目录下的 `Elevator\Elevator.exe` → 提示「请以管理员身份运行 JASM 后重试」，
  不静默失败、不崩溃

## 7. 游戏未检测到（手动回退）

- 卸载/移动鸣潮后打开向导 → 显示「未自动检测到鸣潮安装位置」→ 浏览选择游戏目录 → 重新预检

## 8. Phase 2：XXMI 启动器（GUI）独立运行验证

目标：确认从干净 launcher 包装出的启动器**不依赖 MSI 注册表项**也能独立跑起来。

1. 把 `D:\XXMI` 改名为 `D:\XXMI.bak`（保留现场，可回滚）
2. 上传 `launcher-2.2.1.zip` + 更新版 `version.json` 到 COS `modenv/`
3. JASM 里点「一键配置 Mod 环境」→ 预检应显示 **xxmi / launcher / wwmi 三个包** → 开始配置
4. 配置完成后双击 `D:\XXMI\Resources\Bin\XXMI Launcher.exe`
   - GUI 能打开 → 注册表不是硬依赖 ✓
   - 弹错/闪退 → 需要 JASM 补写注册表卸载项或排查其他依赖
5. GUI 里点 WWMI → 选择游戏目录（`D:\Wuthering Waves\Wuthering Waves Game`）→ 点启动 → 确认注入生效
6. 验证幂等：再次点「一键配置」→ launcher 显示「已是最新」；更新 version.json 版本号 → 显示「可更新」
7. 验证保留配置：改过 GUI 里的设置后升级 launcher 包 → `XXMI Launcher Config.json` 不被覆盖

## 9. Phase 3：启动命令自动接通（启动游戏即带 mod）

目标：确认一键配置后 JASM **自动写好**「启动游戏 / 启动 3Dmigoto」两条命令，无需手动配置命令模板。
实现：`ModEnvSetupFacade.EnsureLaunchCommandsAsync`（`feature/game-launch-integration`）。

1. 一键配置成功后打开 `%LOCALAPPDATA%\JASM\ApplicationData_WuWa[_Debug]\commands.json`，应看到：
   - `StartGameCommand`：`Command` = `D:\XXMI\Resources\Bin\XXMI Launcher.exe`，`Arguments` = `--xxmi WWMI --nogui`
   - `StartGameModelImporter`：`Command` = 同上，无 `Arguments`（打开启动器 GUI）
2. 角色页点「启动游戏」→ 游戏带 mod 启动，**不弹** launcher 窗口（`--nogui` 后台注入+直启游戏）
3. 角色页点「启动 3Dmigoto」→ 打开 XXMI Launcher GUI（管理 mod 环境）
4. 游戏退出后无残留 `XXMI Launcher` 进程（launcher `auto_close=true` 自处理）
5. 幂等：再次一键配置 → 命令不变（update-or-create 替换）
6. 边界：升级前已手动配过游戏命令（如直启 `Wuthering Waves.exe`）→ 被自动替换为 launcher 命令，日志记录
7. 边界：`ModEnv:LauncherPackageId` 不配置（纯注入器无 launcher）→ 命令不被改动

## 10. Phase 4：测试启动引导（配置成功后一键验证）

目标：配置完成后向导内出现「测试启动」按钮，点击即用已自动配好的「启动游戏」命令拉起游戏，
现场验证「启动游戏即带 mod」。实现：`ModEnvSetupViewModel.RunTestLaunchAsync`
（`feature/test-launch-guidance`）。

1. 一键配置成功 → 向导动作区出现「测试启动」按钮（`Succeeded` 才显示；配置失败/取消不出现）
2. 点击「测试启动」→ UAC 提权提示 → 游戏带 mod 启动，不弹 launcher 窗口（`--nogui`）
3. 向导日志/状态栏显示「已发起测试启动：Start … (XXMI)（后台注入 + 游戏直启）。请在游戏中确认 mod 生效。」
4. 进游戏确认 mod 生效后关闭对话框 → Settings 入口照常填路径 + 保存重启；Startup 入口照常回填 + Save
5. 失败路径：UAC 点「否」→ 日志「用户取消了 UAC 提权。」，状态栏错误，不崩溃
6. 边界：`ModEnv:LauncherPackageId` 不配置再配置 → 按钮仍显示，点击后日志「未找到「启动游戏」命令
   （可能未安装 launcher 包），无法测试启动。」
7. 回归：对话框关闭后再进设置/角色页，原有「启动游戏/启动 3Dmigoto」按钮行为不变

## 11. 回归：删除 XXMI 文件夹后首启页一键配置，Save 按钮可用

目标：验证「删除 XXMI 目录 → 首次启动（无已存路径）→ 一键配置成功回填路径」后，首启页
Save 按钮不再因 `IsValid` 未刷新而保持禁用。修复见 `PathPicker.Validate()`（无参调用改为
按当前 `Path` 校验，而非按 `pathToSett` 参数空转）。

1. 删除游戏盘根目录的 `XXMI` 文件夹（如 `D:\XXMI\`），并清空 JASM 本地设置使其回到首次启动态
2. 启动 JASM → 进入首启页 → 预检显示「未检测到已安装的 Mod 环境」，出现「配置 / 更新 / 修复 Mod 环境」按钮
3. 点击按钮 → 向导走通并成功 → 两个路径（MI 文件夹 / Mods 文件夹）自动回填，状态显示成功
4. **Save 按钮应为可用状态**，点击后进入主页面；此前此场景 Save 一直禁用（回归点）
5. 回归：设置页同样删除 XXMI 后一键配置 → `ValidFolderSettings` 通过，自动走「保存并重启」流程

## 12. Phase 4：弱网/断点续传验证

目标：下载在中途断流、卡死时**自动续传重试**，过期 `.part` 自动截断，进度节流带速度，
失败链保留已完成包。实现：`ModEnvInstallerService.DownloadWithResumeAsync` 重试循环 +
`ModEnvSetupFacade` 增量 marker（`feature/mod-env-weak-network`）。

模拟弱网用一个 Python 节流服务器（起在 mock CDN 同目录，替换 `version.json` 里的 URL）：

```python
# throttle_server.py：下载中段 sleep 造成断流/卡死
import http.server, time
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.send_header("Content-Length", str(os.path.getsize("." + self.path))); self.end_headers()
        with open("." + self.path, "rb") as f:
            for i, chunk in enumerate(iter(lambda: f.read(81920), b"")):
                self.wfile.write(chunk); self.wfile.flush()
                if i == 5: time.sleep(45)   # 第 5 块后卡 45s（> 默认 30s 超时），模拟断流
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(("127.0.0.1", 8899), H).serve_forever()
```

1. **中途断流续传**：一键配置下载中，用 Ctrl+C 杀掉 `python -m http.server`（或上面的节流服务器只断一次）
   → 向导日志出现「网络不稳定，正在重试下载（第 2/3 次）...」→ 重试成功后日志「Resuming download of
   … from X bytes」（X = 已下字节，不是 0）→ 配置最终成功
2. **卡死超时**：节流服务器让某段 >30s 无数据 → 日志「下载长时间无数据，自动续传重试」，不无限挂起
3. **过期 .part**：往 `%TEMP%\JASM\modenv\` 写一个比真实包更大的 `xxmi-1.0.5.zip.part`
   （如 `fsutil file createnew` 造大文件）→ 再配置 → 日志「Discarding stale .part …」自动截断重下，
   不再报 416 通用错误
4. **进度节流 + 速度**：观察日志「下载中 … 字节（X MB/s）」行频率 ≈ 2-3 行/秒（不再每 80KB 一行）
5. **跨包保留**：把 `version.json` 里 launcher URL 指向不存在文件 → base 安装成功后 launcher 失败
   → 检查 `D:\XXMI\.modenv.json` 已含 base 版本 → 修好 URL 重跑 → 日志 base「已是最新版本，跳过」，
   只重下 launcher
6. **回归**：正常网络一次配置成功；下载中途取消 → `.part` 保留可续；SHA 不匹配 → 删 `.part` 报错

## 13. Phase 4 弱网验证结果（2026-08-29）

全部 6 项用例通过。日志位于 `%APPDATA%` 外的应用工作目录 `logs\log.txt`
（Serilog 相对路径按启动时的工作目录解析，从仓库根目录启动时落在 `仓库根/logs/log.txt`）。

| 用例 | 结果 | 关键证据（文件日志） |
|---|---|---|
| 1. 中途断流续传 | ✅ | `failed transiently (attempt 1): The response ended prematurely... (ResponseEnded)` → `Retrying download of xxmi-1.0.5.zip, attempt 2/3` → `Resuming download of xxmi-1.0.5.zip from 409600 bytes`（=5×81920，非 0） |
| 2. 卡死超时 | ✅ | 卡 30s 后 `failed transiently (attempt 1): 下载长时间无数据，自动续传重试` → `Retrying ... attempt 2/3` → `Resuming ... from 409600 bytes` |
| 3. 过期 .part | ✅ | `Discarding stale .part for xxmi-1.0.5.zip (5242880 bytes >= 3321339 bytes)`，随后从头重下，无 416 |
| 4. 进度节流+速度 | ✅ | 下载中行 ≈2.5 行/秒，每行字节增量 ≈573440（7×81920，400ms 一报），显示 `1.2-1.4 MB/s` |
| 5. 跨包保留 | ✅ | launcher 404 失败后 `D:\XXMI\.modenv.json` 仅含 `xxmi: 1.0.5`；修 URL 重跑 xxmi 跳过、只重下 launcher+wwmi |
| 6. 回归 | ✅ | 正常网络一次成功无重试；取消后 `.part` 保留并从 13MB 处续传完成；SHA 不匹配 → `SHA256 mismatch ... expected aee41df4..., got f16446c3...` → 删 `.part` 报 `SHA256 校验失败` |

**测试中发现并已修复的一个问题**：配置完成后向导内包状态列表仍显示「未安装」。
根因：`ModEnvSetupViewModel.RunSetupAsync` 结束后未刷新预检列表。已加
`RefreshPackageStatusesAsync`，在 finally 中重跑预检重建 `Packages`（成功/取消/失败均刷新），
现在完成后显示「已是最新」。

## 14. XXMI 版本选择与回退

对应 `docs/xxmi-version-switch-prd.md`。**14.1 和 14.2 不需要 JASM 代码改动就能先跑**，
且 14.2 是本功能唯一的实质性阻塞项 —— 如果某个旧版 dll 跟当前 wwmi 游戏包搭不上，
回退装了也没用，先把结论拿到再写代码。

### 14.1 生成版本包与 catalog

从本地「XXMI更新包（持续更新）」打包（脚本用白名单，只取 4 个文件，**绝不会带出
`Security/private_key.der`**）：

```
python Build/PackXxmiVersions.py
```

预期产物落在 `Build/out/xxmi-versions/`（该目录已被 `.gitignore` 忽略）：

| 产物 | 说明 |
|---|---|
| `xxmi-0.9.2.zip` / `xxmi-1.0.5.zip` / `xxmi-1.1.6.zip` / `xxmi-1.1.7.zip` | 每版本一包，约 3.2 MB，zip 内**平铺且仅有** 3 个 dll + `Manifest.json` |
| `xxmi-versions.json` | 版本清单，含 DownloadUrl / Sha256 / SizeBytes / ReleasedAt |
| `xxmi-version-hashes.json` | 逐文件哈希表，14.2 核对「当前装的是哪个版本」用 |

> `Manifest.json` **必须**跟着 dll 一起发：XXMI 启动器显示的版本号来自它，少了它就会出现
> 「dll 已换成旧版、启动器版本号却没变」。它带的是**公开**的 `signatures`（验签数据），不是私钥。
> 脚本另外会断言包内 `Manifest.json` 的 `version` 与包版本一致、`signatures` 非空。

打包后自查（应看到 4 个包各 **4 个条目**，且不得出现 `Security/`）：

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
Get-ChildItem Build\out\xxmi-versions\*.zip | ForEach-Object {
  $z = [System.IO.Compression.ZipFile]::OpenRead($_.FullName)
  "{0}: {1} -> {2}" -f $_.Name, $z.Entries.Count, ($z.Entries.FullName -join ", ")
  $z.Dispose()
}
```

然后把这些文件和 `xxmi-versions.json` 上传到 COS `modenv/`（与
`appsettings.json` 的 `ModEnv:ManifestUrl` 同桶同前缀）。

### 14.2 ⚠️ 兼容性实测（开发前必做）

目标：回答「v0.9.2 / v1.0.5 / v1.1.6 / v1.1.7 这 4 个核心包，各自跟**当前** wwmi 游戏包能否搭配工作」。
结论直接决定 catalog 里该列哪些版本。

> ⚠️ XXMI 的框架在磁盘上有**两份**：`D:\XXMI\`（游戏实际加载的）和
> `D:\XXMI\Resources\Packages\XXMI\`（**启动器读版本号的那份**，多一个 `Manifest.json`）。
> JASM 两处都写；手工实测时也必须两处都换，只换根目录则启动器显示的版本不会变。

**判定当前装的是哪个版本**（比 `.modenv.json` 更可信 —— 它可能被手改）：

```powershell
Get-FileHash D:\XXMI\3dmloader.dll,D:\XXMI\d3d11.dll,`
  D:\XXMI\Resources\Packages\XXMI\3dmloader.dll,D:\XXMI\Resources\Packages\XXMI\d3d11.dll `
  -Algorithm SHA256 | Format-Table Path, Hash -AutoSize
```

**启动器显示的版本**（唯一权威来源）：

```powershell
(Get-Content D:\XXMI\Resources\Packages\XXMI\Manifest.json -Raw | ConvertFrom-Json).version
```

对照表（`d3dcompiler_47.dll` 四个版本**完全相同**，恒为
`86110AC1986FF0E4F35E1A797A3EDE99233C4A5BD12EDACC6DD45750E22B4171`，不具区分度）：

| 版本 | `3dmloader.dll` SHA256 | `d3d11.dll` SHA256 |
|---|---|---|
| 0.9.2 | `4CA7425C18881E9EBBCE13AE22E7A3CA3843E526B9AA901D14F97953CA87F38B` | `CCBDD982B7CA4FD324198B68B83AEE23ED8B0CF0445255AA8309D021BA8AAD29` |
| 1.0.5 | `DB62EA065744EA07E04FB60B26D53BE185026E16A66E557A0553F7AF079E2A72` | `02E5F1DCF926F6A1C00517307213265B1671797E9DE1E49CEDF731A95447A403` |
| 1.1.6 | `427F6B1082121F96AD83696AFAF40EB2F5799FC29C960BEC917D403B5EB8753A` | `7BA9CC0BFC1E26F613E7F00BA0720CFDCDDF08EBEAD4C8A29B19EB98424CEB6A` |
| 1.1.7 | `44965EE51786DB44FB4252671F356426CA7D879E6F7E8436B27D68E13237B0CF` | `6C962958DD14C79786A88D4358C8EAE24026352D31B4D564DBB2DC57E85A2FFC` |

> 参考：本机 `D:\XXMI` 根目录为 **1.0.5**，而 `Resources\Packages\XXMI\Manifest.json` 曾停在
> **1.1.7** —— 修复前 JASM 只写根目录，启动器便一直显示旧版本号。修复后两处会同版本。

逐一实测（每个版本重复一遍）：

1. 备份现场：把 `D:\XXMI\` 与 `D:\XXMI\Resources\Packages\XXMI\` 下的 4 个文件各复制到别处
2. 从 `xxmi-<版本>.zip` 解出 4 个文件，**同时覆盖上面两处**
3. 双击 `D:\XXMI\Resources\Bin\XXMI Launcher.exe` → GUI 能打开
4. 启动器界面上的版本号 == 你刚换的版本（读法见上）
5. GUI 里选 WWMI → 启动游戏 → **确认注入生效**
6. 进游戏确认 **Mod 正常加载**、无黑屏/闪退
7. 记录结果（✅/❌ + 现象），填回下表

| 版本 | 启动器可开 | 显示版本一致 | 游戏可进 | Mod 生效 | 结论 |
|---|---|---|---|---|---|
| 0.9.2 | | | ✅ | ✅ | ✅ |
| 1.0.5 | | | ✅ | ✅ | ✅ |
| 1.1.6 | | | ✅ | ✅ | ✅ |
| 1.1.7 | | | ✅ | ✅ | ✅ |

**实测结果（2026-09-14）**：四个版本轮换后进游戏，**Mod 都正常加载**，全部保留在 catalog 里。

> 本轮逐版本只确认了「进游戏 + Mod 生效」这一条 —— 那正是选版本时要回答的问题。
> 空着的两列没有逐版本单独记录：启动器能否打开与版本号显示属于 §15 的运行链路，
> 修好两处同版本后不再随版本变化。

**只有结论为 ✅ 的版本才写进 `xxmi-versions.json`**；不可用的版本直接不列，
避免用户选了之后照样用不了。测完把 4 个文件还原成本来的版本（两处都要）。

### 14.3 版本下拉与默认行为（需代码改动后测）

1. 打开「一键配置 / 修复 Mod 环境」对话框 → 出现「XXMI 版本」下拉
2. **默认选中「当前已安装的版本」**（== 下方「当前版本：vX.Y.Z」那一行的版本）；不碰下拉框直接走完流程
   → 因为选中项 == 已装版本，XXMI 包判「已是最新」而跳过，效果与改动前一致（零实质行为变更）
3. 未安装任何版本（`D:\XXMI` 为空）→ 回落到清单里的默认（最新）版本
4. 下拉项按版本号**降序**，数量与 `xxmi-versions.json` 一致
5. 对话框显示「当前版本：vX.Y.Z」，且与实际安装的 3 个 dll 哈希（见 14.2）吻合
6. 选中项 == 当前版本 → 提示「已安装该版本」，跳过 XXMI 包但仍检查 launcher / wwmi
7. 改动下拉框后再触发重跑（改安装位置 / 装完刷新）→ **保留用户的选择**，不被拉回已装版本
8. 恢复备份后 → 下拉框重新指向恢复出来的那个版本（不许还停在刚回退掉的版本上）
9. 边界：`.modenv.json` 删掉或写坏 → 显示「未检测到已安装版本」，**不报错、不崩溃**
10. 边界：已装版本不在 catalog 里（如已下线）→ 如实显示在「当前版本」行，下拉回落到默认（最新）版本，
    不隐藏已装版本、也不谎称它是最新

### 14.4 CDN 降级容错（需代码改动后测）

1. 把 `xxmi-versions.json` 从 CDN 删掉（或 `appsettings.json` 里指到 404 地址）
   → 对话框**照常打开**，下拉降级为仅「最新版」一项，不阻断原有安装流程
2. catalog 内容是坏 JSON → 同上降级，日志有 warning
3. catalog 拉取超时（用 `## 12` 的节流服务器）→ 短超时后降级，不拖慢对话框打开

### 14.5 回退与备份（需代码改动后测）

1. 当前装 v1.1.7 → 下拉选 v0.9.2 → 安装
2. 备份目录（`%LOCALAPPDATA%\JASM\ModEnvBackups\`）应出现含 `1.1.7` 与时间戳的备份档，
   内含当时那 4 个文件（含 `Manifest.json`）
3. 回退完成后 **两处**（`D:\XXMI\` 与 `Resources\Packages\XXMI\`）4 个文件都在，
   且 dll 哈希等于 14.2 表里 **0.9.2** 那一行
4. 打开 XXMI 启动器 → **界面上的版本号跟着变成 0.9.2**（本功能的核心验收点）
5. `D:\XXMI\.modenv.json` 的 `InstalledVersions["xxmi"]` 更新为 `0.9.2`
6. **`WWMI\` 子目录与用户 Mod 不受影响**（回退只动那 4 个文件）
7. `XXMI Launcher Config.json` 不被覆盖（沿用 launcher 包的 `preserveExistingFiles` 行为）
8. 备份档出现在 UI 中，点「恢复此备份」→ 两处版本变回 1.1.7
9. 磁盘空间不足 / 备份目录不可写 → **中止切换并报错**，不得在不留后路的情况下覆盖
10. 游戏或 XXMI Launcher 正在运行时切换 → 给出提示（沿用现有强杀 Launcher 进程的逻辑）
11. 取消切换 → 现场保持原样，`.part` 可续传
12. 旧布局自愈：删掉 `D:\XXMI\Manifest.json`（模拟修复前装的）→ 预检查判「需修复」，
    点「开始配置」后两处补齐，启动器版本与 JASM 记录一致
13. 旧备份档（不含 `Manifest.json`，由修复前的 JASM 生成）→ 恢复不报失败，
    日志出现「该备份不含 Manifest.json…再点一次开始配置即可对齐」的提示
14. 版本被外部改动：手工改乱 `Resources\Packages\XXMI\Manifest.json` 的 `version` →
    预检查出现「XXMI 启动器读到的是 vX，与 JASM 记录的 vY 不一致」的提示，且基础包判成**需修复**
    （这是有意的：点「开始配置」必须真的把两处统一，而不是跳过）
15. 被官方更新覆盖：点官方启动器自己的「更新」按钮（从 GitHub 拉包，国内通常需要梯子）→
    之后按上一条检查：JASM 能识破并覆盖回自己管理的版本。
    注意 JASM **不会**自动覆盖，只在用户点「开始配置」时统一两处

### 14.6 回归

1. 首启页 / 设置页两个「一键配置」入口均能看到版本下拉且行为一致
2. 原有 `## 3` 幂等四态（未安装 / 已最新 / 可更新 / 可修复）判定不变
   （注意：修复前装的用户会因为根目录缺 `Manifest.json` 判成「需修复」——这是**有意的自愈**，
   重跑一次即回到「已是最新」）
3. 原有 `## 9` 启动命令自动接通、`## 10` 测试启动引导不受影响
4. 弱网相关行为（`## 12`）不回归 —— 版本包走的仍是同一条下载链路

## 15. 启动器「更新」误报抑制（把缓存对齐到实装版本）

背景：XXMI 启动器把「磁盘上读到的版本 ≠ 它缓存的 `latest_version`」当成有更新，于是弹
「将包更新到最新版本：XXMI: 1.1.7 → 1.0.5」。它的缓存只在能连上 GitHub releases 时才刷新
（国内基本连不上，日志里是 `GitHub API速率限制超出` / `ConnectionRefusedError`），所以只要 JASM 装的
不是缓存里那个版本，它就**反复**提示这个「降级更新」。

> ⚠️ 2026-09-14 实测（本机 `D:\XXMI`）：只写 `skipped_version`（也就是启动器「跳过」按钮写的那个字段）
> **不生效** —— `latest=1.0.5 / skipped=1.0.5 / 磁盘 1.1.7` 时悬停提示照旧出现。那个字段只被更新**对话框**
> 认，管不了悬停/待更新列表。真正生效的是把缓存改写成实装版本（也就是「官方更新成功」后启动器自己留下的
> 状态：`latest == deployed == 磁盘版本`）。

JASM 在 `ModEnvSetupFacade.EnsureLauncherConfigPathsAsync` 里顺带对齐：**仅当缓存版本比 JASM 实装的更旧**
（两边都能按 `vX.Y.Z` 解析且严格更小）时，写 `latest_version` / `deployed_version` = 实装版本，
清掉 `skipped_version`（仅当它指的就是这个过期版本）与 `latest_release_notes`（那是旧版本号的更新说明）。
缓存比实装**新**时一律不动。

1. 先造现场：手工把 `D:\XXMI\XXMI Launcher Config.json` 里 `Packages.packages.XXMI` 改成
   `latest_version = "1.0.5"`、`skipped_version = "1.0.5"`、`deployed_version = "1.0.5"`，
   磁盘上两份 `Manifest.json` 是 `1.1.7` → 打开启动器，悬停左下角版本应看到「1.1.7 → 1.0.5」
2. 跑一次「一键配置 Mod 环境」→ 向导日志出现「已把 XXMI 启动器缓存的过期版本对齐到实装版本（XXMI 1.0.5 → 1.1.7）…」
3. 复查该文件：`latest_version` / `deployed_version` 都是 `1.1.7`，`skipped_version` 为空、
   `latest_release_notes` 为空；`Importers.WWMI.Importer.game_folder` / `importer_folder` 照旧被回填；
   文件**无 BOM**、缩进 4 空格
4. 重启启动器 → 悬停左下角版本**不再**出现「x → y」，也不再有待更新提示（`WWMI` / `Launcher` 不受影响）
5. 幂等：再跑一次一键配置 → 缓存已是实装版本，不再改写、不重复报日志
6. 边界：缓存版本比实装的**新**（如缓存 2.0.0、实装 1.1.7，或用户主动回退到 0.9.2）→ **一律不动**，
   启动器的更新提示保留（那是真的可更新，不替用户决定）
7. 边界：缓存 `latest_version` 为空 / 非数字（如 `latest`）→ 不改、不报错，其余回填照常
8. 边界：`skipped_version` 指的是**另一个**（更新的）版本 → 不动它，只对齐版本号字段
9. 边界：启动器正在运行 → 先结束进程再写；写入被占用则重试最多 5 次，仍失败只记 issue、不中断安装

> 已知残留：若启动器某天成功连上 GitHub 并把 `latest_version` 刷回**低于**实装版本的官方最新，
> 提示会再出现 —— 那说明我们发的包比官方 GitHub release 还新，属于选包源的问题，不是这段逻辑能兜的。

## 16. 自选 XXMI 安装位置（向导「安装位置」行）

默认行为不变：`CustomRootFolder` 为空时仍解析成「游戏所在盘符 `\XXMI`」（`ResolveDriveRootAsync`）。
按钮放在**一键配置向导**里「安装位置」那一行的右侧（首启页 / 设置页共用同一个向导，所以两个入口都能改）：
点「更改…」选目录、点「用默认」回到游戏盘符下的默认位置。选中的路径写进
`ModManagerOptions.XxmiRootFolderPath`，供之后的每次一键配置沿用 —— 否则重复跑会往默认位置再装一份。

1. 默认态：向导「安装位置」显示 `X:\XXMI`（游戏所在盘符），「用默认」按钮**不可见**（尚无自定义路径）
2. 点「更改…」选一个**非游戏所在盘**的目录（如游戏在 C:、选 `D:\XXMI-Test`）→ 该行立刻变成
   `D:\XXMI-Test`，「用默认」按钮出现；下方包状态按新位置重新预检（新目录为空 → 判「未安装」，属预期）
3. 向导里点「开始配置」→ 实际装到 `D:\XXMI-Test`（日志 / 状态指向新目录）；
   完成后首启页两个路径选择框自动填入新位置下的 `3Dmigoto` / `Mods`
4. 持久化：改完路径**直接关掉向导**（不点开始配置）→ 重启 app 后再打开向导，显示的仍是 `D:\XXMI-Test`
   （两个入口都在向导关闭后立即落盘，不依赖首启页的 Save）
5. 「用默认」→ 该行回到游戏盘符下的默认路径、按钮消失；重启后仍为默认
6. 取消选择：点「更改…」后在系统选择器里按取消 → 路径**不变**（不会因误点而移动安装目标）
7. 边界：游戏安装在 C:、XXMI 自定义到 D: → 一键配置**不再要求**先选游戏目录（有自定义路径时游戏盘符不参与定位）；
   向导里 `game_folder` 仍按游戏真实目录回填
8. 边界：自定义路径被手工清空 / 目录被删掉 → 回到默认解析（游戏盘符下），不报错
9. 边界：`XxmiRootFolderPath` 传相对路径（只可能来自手改配置文件）→ 预检直接报
   「XXMI 安装位置必须是完整路径，例如 D:\XXMI」，不写盘

## 17. 游戏内浮窗切换 Mod（Phase 0 原型实测，2026-09-22）

对应 `docs/wuwa-overlay-mod-switcher-prd.md`，原型在 `feat/wuwa-overlay-spike` 分支的
`src/OverlaySpike/`（独立工程，**故意不进 sln**）。这张表是「独立无边框置顶小窗 +
`WS_EX_NOACTIVATE`」这条技术路线的**可行性基线**：以后改动浮窗的窗口样式 / 显隐 / 热键，
回来重跑这几条。

**环境**：2560x1600 @150%、Windows 11 26200。`OverlaySpike.exe` **非提权**运行，鸣潮以管理员运行。

### 17.1 可见性与可点击性（四场景全过）

| 游戏显示模式 | 鼠标状态 | 浮窗可见 | 点击被计数 | 点完游戏掉全屏/最小化 | 点完前台焦点 |
|---|---|---|---|---|---|
| 独占全屏 | 3D 视角（鼠标被锁） | ✅ | ✅ | 否 | 仍在游戏 |
| 独占全屏 | ESC 菜单 / 背包（鼠标已解锁） | ✅ | ✅ | 否 | 仍在游戏 |
| 无边框窗口 | 3D 视角（鼠标被锁） | ✅ | ✅ | 否 | 仍在游戏 |
| 无边框窗口 | ESC 菜单 / 背包（鼠标已解锁） | ✅ | ✅ | 否 | 仍在游戏 |

> 「点击被计数」= 面板上的点击计数递增，即真实鼠标输入确实到达了窗口。
> 游戏用 `SetCapture` 锁着鼠标时同样成立 —— 这原本是最可能翻车的一条。

### 17.2 全局热键（提权游戏占着前台时）

| 场景 | 结果 |
|---|---|
| 游戏 3D 视角、占着前台 | ✅ 可唤出/隐藏 |
| 游戏菜单界面、占着前台 | ✅ 可唤出/隐藏 |
| 切回桌面（游戏不在前台） | ✅ 可唤出/隐藏 |

`Ctrl+Alt+M` 被本机其他程序占用 —— `RegisterHotKey` 的组合键是**独占**的，注册失败就是彻底没反应。
原型会自动退到候选列表里第一个可用的键（当次落在 `Ctrl+Alt+K`），面板上写明实际生效的是哪个。
⇒ **非提权进程可以用全局热键驱动浮窗覆盖在提权游戏之上**。

### 17.3 附带结论

- 亚克力（`DesktopAcrylicBackdrop`）在「无边框 + NOACTIVATE」窗口上正常渲染，内容照常刷新。
- 关掉 `WS_EX_NOACTIVATE` 后，点一下浮窗立刻夺走前台焦点（面板显示「本窗口持有」）
  ⇒ 前述「点击不夺焦点」确实由该样式引起（A/B 成立）。
- NOACTIVATE 下拖动条可正常移动窗口，居中 / 退出按钮可用。

### 17.4 实测暴露的三个坑（原型已修，正式实现必须照做）

| 坑 | 症状 | 正确做法 |
|---|---|---|
| 置顶是**假的** | `OverlappedPresenter.IsAlwaysOnTop` 与 WinUIEx `WindowEx.IsAlwaysOnTop` **都不往窗口样式里写** `WS_EX_TOPMOST`，托管属性却读回 `true`；窗口被普通最大化窗口盖住，用户看到的就是「浮窗自己消失了」 | 判断置顶**只认扩展样式**（`0x00000008`），并**每秒自愈**；注意窗口显示稳定前（约 1 秒内）连 `SetWindowPos(HWND_TOPMOST)` 都会被 WinUI 抹掉，调用返回成功而样式位不在 |
| 点按钮时点击计数不涨 | `Button` 自己处理 `PointerPressed` 并标记为已处理，XAML 上的 `PointerPressed="..."` 收不到；而计数是「鼠标输入有没有到达窗口」的唯一证据，会把结论带偏成「NOACTIVATE 挡掉了鼠标输入」 | 用 `AddHandler(..., handledEventsToo: true)` 订阅 |
| 热键被占用＝彻底没反应 | `RegisterHotKey` 失败只在面板上写一行小字，用户只会觉得功能坏了 | 候选键列表 + 界面写明实际生效的组合；正式版应让用户可改键 |

**Phase 1 的阻塞项**：`src/Elevator/Program.cs:167` 写死
`Process.GetProcessesByName("GenshinImpact")`，鸣潮下必然找不到进程 ⇒
「勾选后立刻 F10 刷新」这条链在鸣潮上走不通，必须一并改（这正是本功能的核心动作）。

