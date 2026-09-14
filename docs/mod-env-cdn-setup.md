# 用腾讯云 COS 托管 Mod 环境安装包

JASM 的一键配置 Mod 环境功能需要把 **XXMI 基础包**、**WWMi 游戏包**和远程 **version.json 版本清单**
放到一个国内可直连、无需梯子的地址。最简单的方式是**腾讯云 COS 对象存储 + 默认域名直连**
（`*.cos.*.myqcloud.com` 是腾讯云平台域名，**免备案**，国内全地区可直连）。

> 注意：COS 的「默认 CDN 加速域名」（`*.file.myqcloud.com`）自 **2022-05-09** 起新存储桶不再支持开启，
> 所以别走那条路。默认域名直连对小体积包（几十 MB 内）完全够用；包大了、下载量大了再考虑加 CDN（见文末）。

---

## 一、创建存储桶（5 分钟）

1. 打开腾讯云控制台 → [对象存储 COS](https://console.cloud.tencent.com/cos)（需先开通 COS 服务）。
2. 点「创建存储桶」：
   - **名称**：全局唯一，例如 `jasm-modenv`（创建后会自动带 APPID 后缀，如 `jasm-modenv-125xxxxxxx`）。
   - **所属地域**：选离你目标用户近的，国内默认选 **广州 / 上海 / 北京** 之一即可。
   - **访问权限**：选 **公有读私有写**（关键！这样下载不需要签名，上传需要你的密钥）。
   - 其余默认，点「创建」。

## 二、上传文件

可以用控制台网页直接拖拽，或用腾讯云官方图形工具 [COSBrowser](https://cosbrowser.cloud.tencent.com)（上传大文件更稳）。

建议在桶里建一个子目录 `modenv/`，放 4 类文件：

| 文件 | 内容要求 |
|---|---|
| `xxmi-<版本>.zip` | XXMI 注入器框架包，**4 个文件平铺在根**：`3dmloader.dll` / `d3d11.dll` / `d3dcompiler_47.dll` / `Manifest.json`。JASM 会把这 4 个文件同时写入 XXMI 根目录和 `Resources\Packages\XXMI\`（后者是启动器读版本号的地方），缺任一个都判「需修复」 |
| `wwmi-<版本>.zip` | WWMi 鸣潮游戏包。解压后**必须**在包根目录有 `d3d11.dll`、`d3dx.ini` 和 `Mods\` 文件夹（JASM 校验这三个，缺一即判「需修复」） |
| `launcher-<版本>.zip` | **可选**。XXMI 启动器（GUI）包，见下节「打 launcher 包」 |
| `version.json` | 版本清单，见下节 |
| `xxmi-versions.json` | **可选**。可选 XXMI 版本目录，让用户在向导里选版本 / 回退，见「生成 xxmi-versions.json」 |

WWMi 包结构示意（干净基础包）：

```
wwmi-1.0.0.zip
├── d3d11.dll          ← 必须有（注入器）
├── d3dcompiler_47.dll
├── d3dx.ini           ← 必须有（配置）
├── d3dx_user.ini
├── README.md
├── Core\              ← 3DMigoto 核心
└── Mods\              ← 必须有（空目录即可，JASM 的 mods 放这里）
```

> 包根目录如果只有一个文件夹（例如解压后是 `WWMI\...`），JASM 会自动解开这一层再复制，所以
> 用官方 release 的原样 zip 通常也没问题——但务必确认解压后能看到 `d3d11.dll`、`d3dx.ini` 和 `Mods\`。

> ⚠️ **打包 wwmi 时务必用「干净基础包」**（注入器 + 配置 + Core + 空 `Mods\`），
> **不要**拿你在用的实机 `WWMI\` 文件夹直接打——里面通常装着几 GB 的个人 mods 和着色器缓存，
> 既不该分发给别的用户，也会让包体积爆炸（几 GB）。`ShaderCache\` / `ShaderFixes\` 是可选项，运行时自动重建。

### 打 launcher 包

> ⚠️ **不要直接分发 `XXMI-Launcher-Installer-Online-v2.2.1.msi`**：它是**联网引导器**，只含
> `XXMILauncher.exe`，Resources/Themes/Locale 首次运行才从境外下载——正是国内用户翻墙问题的来源。
> 必须从一台**已安装好的 `D:\XXMI`** 打包完整离线包。

XXMI Launcher 是个 PyInstaller 打包的 Python/tkinter 应用，`XXMI Launcher.exe`（60MB）+ `Bin\` 运行时
（~50MB）不能裁剪。但可以排除大量垃圾，最终 zip 约 **55MB**：

| 打进去（必须） | 排除（垃圾/危险） |
|---|---|
| `Resources\Bin\`（除日志） | `Resources\Packages\Launcher\TMP\`（旧版安装器，~90MB 垃圾） |
| `Resources\Packages\XXMI\` + `Launcher\Manifest.json` | `Resources\Security\`（⚠️ 含 XXMI 签名**私钥** `private_key.der`，绝不能外发） |
| `Themes\`、`Locale\`、`Backups\` | `Resources\Bin\ReShade.log*`（轮转日志） |
| 一份**干净默认** `XXMI Launcher Config.json` | 实机 `WWMI\`（JASM 的 wwmi 包已装）、根目录 3 个 DLL（JASM 的 xxmi 包已装）、`.modenv.json`、日志、`.lnk`、`Config.json` 里的机器专属字段 |

> ⚠️ **目录结构必须保持 `Resources\Bin` 用的就是原始多级结构，别用会"压平"的通配拷贝**。实测踩过两个坑：
> - `Locale\` 必须是 **`Locale\Strings\CN\…`** 结构（启动器 2.2.1 迁移后的结构），不能是旧版 `Locale\CN\…`——
>   否则启动器崩：`Failed to load locale: [WinError 3] '…\Locale\Strings\CN'`
> - `Themes\` 必须保留 **`Themes\Default\…`** 顶层——否则崩：`FileNotFoundError: …\Themes\Default\MainWindow\LauncherFrame\background-image-xxmi.webp`
>
> 打包用 `robocopy "…\Themes" <stage>\Themes /E`（不要 `Copy-Item …\Themes\*`），打包后抽查这两个关键文件是否存在。

干净 Config.json 要点（JASM 更新时会保留用户已编辑的该文件）：
- `Launcher.auto_update=false`（避免启动器去 GitHub 自更新）
- `Launcher.locale="CN"`、`log_level="INFO"`
- `Importers.WWMI.Importer.importer_folder="WWMI/"`（相对路径，指向 JASM 装的 wwmi 包）、`game_folder=""`、`shortcut_deployed=false`
  - JASM 一键配置时会**自动填入**这两项为绝对路径（`importer_folder="D:/XXMI/WWMI"` 正斜杠、`game_folder="D:\Wuthering Waves\Wuthering Waves Game"`），
    用户无需在 GUI 里手选；仅当字段已是非空绝对路径时保留用户值（`importer_folder` 为相对路径如 `WWMI/` 也会被替换成绝对路径）
- ⚠️ **配置文件必须无 UTF-8 BOM**：launcher 的 Python `json.loads` 遇到 BOM 会抛
  `Unexpected UTF-8 BOM` → 首次启动弹「错误 加载配置失败」（有「加载默认/加载备份」按钮）。
  JASM 一键配置时会**强制以无 BOM 写回**，但打包这份干净 config 时别用默认带 BOM 的编辑器保存
  （PowerShell `Set-Content`、某些记事本会加 BOM；用 VS Code 右下角选「UTF-8」而非「UTF-8 with BOM」）
- 删除 `Security.user_signature`（机器专属）

JASM 的 `ModEnv:LauncherPackageId` 配了 `launcher` 才会装这个包；不配就跳过（纯 JASM 注入器玩法）。

## 三、生成 version.json

在桶的 `modenv/` 下放一个 `version.json`，内容模板：

```json
{
  "ManifestVersion": 1,
  "Packages": {
    "xxmi": {
      "Version": "1.0.0",
      "DownloadUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/xxmi-1.0.0.zip",
      "Sha256": "……小写十六进制，见下",
      "SizeBytes": 1048576,
      "GameVersion": null,
      "CompatibleGameVersions": []
    },
    "wwmi": {
      "Version": "1.0.0",
      "DownloadUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/wwmi-1.0.0.zip",
      "Sha256": "……",
      "SizeBytes": 52428800,
      "GameVersion": "2.4.0",
      "CompatibleGameVersions": ["2.4.0", "2.5.0"]
    },
    "launcher": {
      "Version": "2.2.1",
      "DownloadUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/launcher-2.2.1.zip",
      "Sha256": "……",
      "SizeBytes": 57286625,
      "GameVersion": null,
      "CompatibleGameVersions": []
    }
  }
}
```

生成 `Sha256` 和 `SizeBytes`（PowerShell，在 zip 所在目录执行）：

```powershell
Get-FileHash ".\wwmi-1.0.0.zip" -Algorithm SHA256 | Select-Object -ExpandProperty Hash
(Get-Item ".\wwmi-1.0.0.zip").Length
```

要点：
- `Packages` 字典的 key 必须叫 **`xxmi`** 和 **`wwmi`**（分别对应 `appsettings` 的 `BasePackageId`
  和 WuWa 的 `game.json` 里 `ModEnv.PackageId`）。
- `Sha256` 必须是**小写**十六进制，JASM 下载完会做严格比对，不对会删掉重下。
- `GameVersion`/`CompatibleGameVersions`：填当前鸣潮客户端版本号（如 `2.4.0`）。JASM 检测到游戏版本
  与它不一致时会弹**非阻断警告**（不会中断安装）。
- 每次出新包：上传新 zip → 更新 `version.json` 里的版本号/url/sha256/size → 用户端再次点按钮即显示「可更新」。

### 生成 xxmi-versions.json（可选：让用户选版本 / 回退）

`version.json` 每个包只能描述**一个**版本，所以它表达不了「让用户装回旧版」。要开版本选择 / 回退，
额外传一份 `xxmi-versions.json`，向导里就会出现 XXMI 版本下拉框 + 备份恢复区；不传（或传了取不到）
则这两块都不显示，行为与加这个功能之前**完全一致**。

**用仓库里的脚本生成，别手搓**——脚本按白名单打 zip、算 sha256、按包内 `d3d11.dll` 的修改时间推算
发布日期，并带一道安全闸：

```bash
python Build/PackXxmiVersions.py --base-url https://<你的桶域名>/modenv/
```

默认从 `D:\BaiduNetdiskDownload\MC-MOD整合包\XXMI更新包（持续更新）` 读源包（可用 `--source` 覆盖），
产物落在 `Build/out/xxmi-versions/`：

| 文件 | 说明 |
|---|---|
| `xxmi-<版本>.zip` | 扁平包，**只含** `3dmloader.dll` / `d3d11.dll` / `d3dcompiler_47.dll` / `Manifest.json` |
| `xxmi-versions.json` | 版本目录，直接传 CDN |
| `xxmi-version-hashes.json` | 各版本逐文件 sha256，手测对照用 |

> ⚠️ **安全闸**：脚本对每个 zip 断言「文件名集合 == 白名单这四个文件」，不等就删掉 zip 并退出。
> 目的是防止把 `XXMI 更新包` 目录里的 `Security/private_key.der`（XXMI 的签名**私钥**，与「打 launcher 包」
> 那节同源）打进可公开下载的包里。**不要**为了少一个版本而放宽这个断言。
> （`Manifest.json` 是唯一被放进来的白名单外延伸文件：它带 `signatures` 是**公开**的验签数据，不是私钥。）

> ⚠️ **`Manifest.json` 不能省**。XXMI 的框架在磁盘上有**两份**：XXMI 根目录（游戏实际加载的）和
> `Resources\Packages\XXMI\`（**启动器显示版本号的那份**）。少了 `Manifest.json`，JASM 换完 dll
> 启动器上的版本号也不会变——「回退到 1.0.5 后启动器还显示 1.1.7」就是这么来的。
> 脚本另外会断言包内 `Manifest.json` 的 `version` 与包版本一致、`signatures` 非空，不一致直接退出。

> ⚠️ **同一个 `xxmi-<版本>.zip` 换了内容，就必须同步改 `version.json` 里 xxmi 的 `Sha256`/`SizeBytes`**。
> `version.json`（默认装哪个版本）和 `xxmi-versions.json`（可以选哪些版本）是两份独立清单，各带包哈希。
> 只重传 zip 不改 `version.json`，用户端会在下载完成后卡在 SHA256 校验失败——而且文件名没变，
> 现象上看起来像「什么都没改」。稳妥做法：传新 zip 和改好的 `version.json` 挨着做。

传上去的 `xxmi-versions.json` 形如（脚本已生成，必要时给版本填 `Notes`）：

```json
{
  "CatalogVersion": 1,
  "Versions": [
    {
      "Version": "1.1.7",
      "DownloadUrl": "https://<桶域名>/modenv/xxmi-1.1.7.zip",
      "Sha256": "7f9518eb……",
      "SizeBytes": 3411769,
      "ReleasedAt": "2026-09-13",
      "Notes": ""
    }
  ]
}
```

要点：
- `Version` 用**数字点分**（如 `1.1.7`）。JASM 按段做数值比较而非字符串比较——字符串比较会把 `1.1.7`
  排到 `0.9.2` 前面，于是「回退」会被显示成「更新」。
- `Notes` 显示在向导的版本下拉框下方，用来写「这个版本已知有什么坑」，用户**选版本时就能看到**。
- 顺序不用你排，JASM 按版本号从新到旧自己排；重复 / 缺字段的条目会被自动丢弃。
- 下拉框的**默认选中项永远是 `version.json` 里那个版本**，所以「不动下拉框」= 加此功能之前的行为。
  想让用户默认装哪个版本，改 `version.json` 即可。
- **同一个版本在 `version.json` 和 `xxmi-versions.json` 里的 `Sha256`/`SizeBytes` 必须指向同一个对象。**
  不动下拉框时 JASM 用 `version.json` 那份，选中该版本时用 catalog 那份——两处哈希不一致，
  用户就会遇到「同一个版本，有时能装有时校验失败」。
- ⚠️ **上线前先跑 `docs/mod-env-hand-test.md` §14.2 的兼容性矩阵**（每个版本 × 当前 wwmi 包），
  **只把实测可用的版本放进目录**——目录里放了坏版本，用户回退过去照样是坏的。
- 不必把全部历史版本都放上去，近期几个够用：每多一个版本，就多一份要验证、要托管的资产。

## 四、拿到访问地址，填进 JASM

1. 控制台 → 存储桶 → **域名管理** → 「默认域名」一栏，形如
   `jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com`。
2. 你的清单地址就是：
   ```
   https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/version.json
   ```
3. 先用浏览器打开这个地址，确认能返回 JSON（右键查看源码确认没被 COS 包一层 XML）。
4. 把 `src/GIMI-ModManager.WinUI/appsettings.json` 的 `ModEnv:ManifestUrl` 从占位地址改成它：

```json
"ModEnv": {
  "ManifestUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/version.json",
  "VersionCatalogUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/xxmi-versions.json",
  "BasePackageId": "xxmi"
}
```

`VersionCatalogUrl` **留空就关掉版本选择**（下拉框与备份区都不显示），此时向导行为与加这个功能之前一致。
备份默认保留最近 5 份（`ModEnv:KeepBackupCount`），存放在
`%LOCALAPPDATA%\JASM\ModEnvBackups\xxmi-<版本>-<时间戳>\`，只是基础包那几个 DLL（几 MB），随时可删。

5. 重新编译运行，按 `docs/mod-env-hand-test.md` 手测一遍。

## 五、费用与可选升级

- **费用**：COS 存储费很低（约 ¥0.1/GB/月）；主要开销是**公网下行流量费**（约 ¥0.5/GB）。
  每个用户首装大概下载 XXMI+WWMi 总共几十 MB～几百 MB，先按量估算，量大了在控制台看账单。
- **防盗链**（可选）：量大之后可在存储桶设置「防盗链（Referer 白名单）」，避免别人把包外链走流量。
- **加 CDN**（可选，需域名备案）：如果包体积大、下载量大，可绑定**自定义 CDN 加速域名**提速并做流量包。
  注意国内 CDN 需要**已备案的域名**。没有域名/备案就先别做，默认域名直连已经能满足「国内无需梯子」。

## 参考

- [COS 域名管理概述](https://cloud.tencent.com/document/product/436/18424)
- [COSBrowser 下载](https://cosbrowser.cloud.tencent.com)
- [自定义域名备案要求说明](https://cloud.tencent.com/document/product/436/56559)
