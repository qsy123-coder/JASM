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
