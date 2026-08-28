# 一键配置 Mod 环境 — 本地手测指南

针对 **Phase 1**（一键配置 Mod 启动环境）的本地验收步骤。对应 `docs/mod-env-setup-prd.md` 与
`feature/one-click-mod-env-setup` 分支。

## 0. 准备 mock CDN

已生成假包 + 版本清单到 `C:\jasm-mock-cdn\`：

```
version.json         # 含 xxmi / wwmi 两个包，URL 指向 localhost:8899
xxmi-1.0.5.zip       # 真实 XXMI 注入器框架包（3 个 DLL）
wwmi-1.0.0.zip       # 真实 WWMI 游戏包（d3d11.dll + d3dx.ini + Core\ + 空 Mods\）
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
