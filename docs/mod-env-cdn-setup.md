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
| `xxmi-<版本>.zip` | XXMI 注入器框架包，**4 个文件平铺在根**：`3dmloader.dll` / `d3d11.dll` / `d3dcompiler_47.dll` / `Manifest.json`。JASM 把这 4 个文件写入 XXMI 根目录与 `Resources\Packages\XXMI\`，并在收尾把后两个 dll 部署进 MI 文件夹（`<根>\WWMI\`，**游戏真正加载的那份**），缺任一个都判「需修复」 |
| `wwmi-<版本>.zip` | WWMi 鸣潮游戏包。解压后**必须**在包根目录有 `d3d11.dll`、`d3dx.ini` 和 `Mods\` 文件夹（JASM 校验这三个，缺一即判「需修复」）。⚠️ 包里那份 `d3d11.dll` 会被框架部署覆盖成 JASM 管理的 XXMI 版本 —— 它只是为了让校验通过，别指望它生效、也别为「对齐版本」反复重打这个包 |
| `launcher-<版本>.zip` | **可选**。XXMI 启动器（GUI）离线包，由官方 Portable 包打出，见下节「打 launcher 包」 |
| `version.json` | 版本清单，见下节 |
| `xxmi-versions.json` | **可选**。可选 XXMI 版本目录，让用户在向导里选版本 / 回退，见「生成 xxmi-versions.json」 |
| `launcher-versions.json` | **可选**。可选 **XXMI 启动器本体**版本目录，让用户选启动器版本，见「生成 launcher-versions.json」 |

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

> ⚠️ **不要直接分发官方 MSI**（`XXMI-Launcher-Installer-Online-vX.Y.Z.msi`）：它是**联网引导器**，只含
> `XXMILauncher.exe`，Resources/Themes/Locale 首次运行才从境外下载——正是国内用户翻墙问题的来源。

改用**官方 Portable 包**（`XXMI-Launcher-Portable-vX.Y.Z.zip`，与 MSI 同页发布，自带
`Locale\` / `Resources\Bin\` / `Themes\`，解压即离线可跑）打成 JASM 的 `launcher-<版本>.zip`：

```bash
# 先在一台机器上跑一次**这个版本**的启动器，让它把配置写到 XXMI 根目录（脚本要拿这份来清洗）
python Build/PackXxmiLauncher.py --update-manifest Build/out/xxmi-versions/version.json \
    --update-catalog Build/out/xxmi-versions/launcher-versions.json
```

> ⚠️ **每个版本都要用它自己跑出来的配置**（`--source` 对应哪个版本，`--config` 就用那个版本
> 生成的），不要几个版本共用一份。配置里有 `Launcher.config_version` 记着**配置 schema 的版本**
> （实测跑 2.3.8 得到的是 `"2.3.8"`），旧版启动器读到新 schema 会怎样没有实测过。让启动器自己
> 生成再交给脚本清洗，就绕开了这个问题——脚本清掉的都是随机器/随部署变的字段，不会动 schema 相关的东西。

脚本做的事：**原样**取 Portable 的内容 → 注入清洗过的 `XXMI Launcher Config.json`
（根目录 + `Backups\` 各一份）→ 打包前断言。默认从
`D:\BaiduNetdiskDownload\MC-MOD整合包\XXMI软件本体更新包（持续更新）` 取版本号最高的 Portable 包
（`--source` 可覆盖），产物落在 `Build/out/xxmi-versions/`。改桶时 `--base-url` 与
`appsettings.json` 的 `ModEnv:ManifestUrl` 一起改。

> ℹ️ 「XXMI软件本体更新包」= 启动器本体（本脚本用）；「XXMI更新包」= 注入器框架版本包
> （`PackXxmiVersions.py` 用）。两个目录别搞混。

清洗规则（`PackXxmiLauncher.sanitize_config`，动的都是随机器/随部署变的字段）：

| 字段 | 改成 | 为什么 |
|---|---|---|
| `Launcher.auto_update` | `false` | 不让启动器去 GitHub 自更新（国内连不上） |
| `Launcher.locale` / `log_level` | `"CN"` / `"INFO"` | |
| `Security.user_signature` | `""` | 机器专属签名 |
| `Importers.<ID>.Importer.importer_folder` | `"<ID>/"` | 相对路径；JASM 一键配置时替换成绝对路径 |
| `Importers.<ID>.Importer.game_folder` | `""` | JASM 一键配置时按实际游戏目录回填 |
| `Importers.<ID>.Importer.shortcut_deployed` / `launch_count` / `deployed_migoto_signatures` / `*_warned` | 复位 | 部署态，机器专属 |
| `Packages.packages.<包>.update_check_time` / `skipped_version` / `*_release_notes` | 复位 | 上次检查的残留 |
| `Packages.packages.<包>.latest_version` / `deployed_version` | **保留** | 见下方说明 |

> `latest_version` / `deployed_version` 保留的理由是「打包机上官方装的版本与 JASM 当下实装的一致，
> 启动器就不会反复提示更新」。**但这个前提并不可靠**：实测本机 2.3.8 那份配置里
> `Launcher.latest_version` 是 `2.3.9`（启动器自己查过一次上游），比包版本还新。所以真正兜底的是
> JASM 收尾时的对齐（`ModEnvSetupFacade.AlignStaleLauncherVersions`），规则是：
>
> - **启动器自身**那个包键**双向**对齐 —— 从新版退回旧版时缓存会比实装新，留着它等于让启动器永远提示
>   升级到一个（可能已经下不到的）新版；JASM 才是启动器版本的唯一来源，启动器自己的自更新在配置里
>   已被关掉（`Launcher.auto_update=false`），那条提示没有任何可行动路径。
> - 其余包（`XXMI` / `WWMI`）保持**单向**：缓存比实装新时不动，那是真的可更新。
>
> 改这条规则会改 zip 的哈希（配置变了）—— 已上线的包重打就必须重传并同步两份清单的哈希。

> `importer_folder` 保持相对路径 `WWMI/` 是有意的：JASM 一键配置会把「空或非绝对路径」替换成绝对路径
> （`importer_folder="D:/XXMI/WWMI"` 正斜杠、`game_folder="D:\Wuthering Waves\Wuthering Waves Game"`），
> 用户无需在 GUI 里手选；已经是非空绝对路径的字段则保留用户值。

⚠️ **配置文件必须无 UTF-8 BOM**：launcher 的 Python `json.loads` 遇到 BOM 会抛
`Unexpected UTF-8 BOM` → 首次启动弹「错误 加载配置失败」（有「加载默认/加载备份」按钮）。
脚本按 4 空格缩进 + CRLF + 无 BOM 写回；手工改这份配置时别用会加 BOM 的编辑器
（PowerShell `Set-Content`、某些记事本会加；VS Code 右下角选「UTF-8」而非「UTF-8 with BOM」）。

⚠️ **目录层级必须原样保留，别用会「压平」的通配拷贝**。实测踩过两个坑：

- `Locale\` 必须是 **`Locale\Strings\CN\…`** 结构（2.2.1 起迁成这个结构），不能是旧版 `Locale\CN\…`——
  否则启动器崩：`Failed to load locale: [WinError 3] '…\Locale\Strings\CN'`
- `Themes\` 必须保留 **`Themes\Default\…`** 顶层——否则崩：`FileNotFoundError: …\Themes\Default\MainWindow\LauncherFrame\background-image-xxmi.webp`

从 Portable 包原样取内容天然满足这两条，脚本也会断言这两个关键文件确实在。

**绝不外发**：`Security\`（含 XXMI 签名**私钥** `private_key.der`）、
`Resources\Packages\Launcher\TMP\`（~90MB 旧版联网引导器）、`Resources\Bin\*.log`。
官方 Portable 包本来就不含这些，脚本照样逐条断言——`private_key` 一出现就删包退出。

JASM 的 `ModEnv:LauncherPackageId` 配了 `launcher` 才会装这个包；不配就跳过（纯 JASM 注入器玩法）。

> ℹ️ Portable 包**不含** `Resources\Packages\XXMI`（注入器框架），也**不含** `Packages\Launcher\Manifest.json`
> （旧版启动器用它记自己的版本；2.3.x 改记在 `XXMI Launcher Config.json` 的 `Packages.packages.Launcher` 里）。
> 框架由 `xxmi` 基础包提供，JASM 安装顺序是「基础包 → 启动器 → 游戏包」，所以启动器装上去时它已经在位。
> **别再把框架打进启动器包** —— 那会在包里多一份版本可能过期的副本。

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
      "Version": "2.3.8",
      "DownloadUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/launcher-2.3.8.zip",
      "Sha256": "……",
      "SizeBytes": 53768403,
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

> ⚠️ **`Manifest.json` 不能省**。XXMI 的框架在磁盘上有**三处**（逐处说明见 `mod-env-hand-test.md` §14.2 的表）：
> XXMI 根目录（历史布局，实测已无人读）、`Resources\Packages\XXMI\`（启动器眼里的「已安装包」，
> **所有部署都从它派生**）、以及 **MI 文件夹 `<根>\WWMI\`（游戏真正加载的注入器；启动器 2.3.x 起还拿它
> 显示版本号）**。少了 `Manifest.json`，JASM 换完 dll 启动器上的版本号也不会变——「回退到 1.0.5 后启动器
> 还显示 1.1.7」就是这么来的。
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

### 生成 launcher-versions.json（可选：让用户选启动器版本）

与上一节同一个套路，只是对象换成**启动器本体**（`launcher` 包）。启动器与注入器框架是两套独立编号、
各自发布，所以各用一份目录，不共用文件。不传（或取不到）时启动器下拉整块不显示，行为与加这个功能
之前完全一致 —— 仍然只装 `version.json` 里那一个版本。

```bash
# 对每个要放进目录的版本各跑一次：--source 指该版本的 Portable，
# --config 必须是**该版本自己**生成的配置（见「打 launcher 包」那节的警告）
python Build/PackXxmiLauncher.py \
    --source "D:/.../XXMI-Launcher-Portable-v2.2.1.zip" \
    --config "D:/tmp/2.2.1/XXMI Launcher Config.json" \
    --out Build/out/xxmi-versions \
    --update-catalog Build/out/xxmi-versions/launcher-versions.json
```

产物 `<out>/launcher-versions.json` 与 `xxmi-versions.json` **schema 完全一致**（JASM 用同一个解析器）：

```json
{
  "CatalogVersion": 1,
  "Versions": [
    {
      "Version": "2.4.1",
      "DownloadUrl": "https://<桶域名>/modenv/launcher-2.4.1.zip",
      "Sha256": "……",
      "SizeBytes": 53948965,
      "ReleasedAt": "2026-10-03",
      "Notes": ""
    }
  ]
}
```

要点（与 `xxmi-versions.json` 相同的那几条不再重复）：

- `--update-catalog` 是**增量**合并：同名版本只覆盖 url、哈希、体积、发布日期这四项，**手写的 `Notes` 保留**。
  别每次都从零生成 —— 隔几周再打包时源目录里往往只剩最新那一版，从零生成会把旧版本记录整段抹掉，
  而「把坏掉的版本退回去」正是这份目录存在的理由。
- `ReleasedAt` 取**包内启动器 exe 的时间戳**，不是源 zip 的 mtime、也不是打包当天。
- ⚠️ **传的顺序**：先把各 `launcher-<版本>.zip` 传上去，**最后**传 `launcher-versions.json`。
  目录引用的 zip 得先在线上，否则用户选中那一刻才开始 404。
- ⚠️ **上游会删旧 release，目录里的版本要自己留档**。XXMI Launcher 的 2.3.x release 已被官方删除
  （`api.github.com/repos/SpectrumQT/XXMI-Launcher/releases/tags/v2.3.9` 直接 404，只有 tag 还在），
  而 CDN 上那份是当时打的。别指望日后还能从 GitHub 重新下到同一个版本 —— 源 zip 与 CDN 各留一份。
- 下拉的默认选中项是**磁盘上已装的那个版本**（不是最新版），所以老用户打开向导看到的仍是自己那份，
  「不动下拉框」= 不重装。想让**新装**用户默认拿到哪一版，改 `version.json` 的 `launcher` 条目。

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
  "LauncherVersionCatalogUrl": "https://jasm-modenv-125xxxxxxx.cos.ap-guangzhou.myqcloud.com/modenv/launcher-versions.json",
  "BasePackageId": "xxmi",
  "LauncherPackageId": "launcher"
}
```

`VersionCatalogUrl` **留空就关掉框架版本选择**（框架下拉 + 备份区都不显示），此时向导行为与加这个功能
之前一致；`LauncherVersionCatalogUrl` 同理，留空则**启动器版本下拉**不显示（框架那个不受影响，两个
下拉各自独立显隐）。
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
