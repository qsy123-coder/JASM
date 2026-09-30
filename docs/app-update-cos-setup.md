# 用腾讯云 COS 托管 JASM 自己的更新包

JASM 的**应用更新**（「发现新版本 → 点更新 → 换掉自己」）原先完全靠 GitHub Releases：包挂在 release
上，客户端顺带从 releases API 的 `tag_name` / `assets` 里读出「最新版是哪个、包在哪」。国内用户拉
`api.github.com` 和 `github.com` 的 release 资产都很慢，所以现在把**版本清单与安装包**搬到与
「一键配置 Mod 环境」同一个 COS 桶的 `app/` 前缀下，**GitHub 只作为回退通道**。

> 桶的创建（名称 / 地域 / **公有读私有写**）与域名获取见 `docs/mod-env-cdn-setup.md` §一、§四，
> 这里不再重复。本文只讲 `app/` 前缀下有什么、怎么发一版。

---

## 一、目录布局

同一个桶（`jasm-modenv-1327973389`）、与 `modenv/` 平级的 `app/` 前缀：

```
app/
├── update.json                    ← 版本清单（客户端先读它）
├── JASM_v2.31.0.7z                ← folder 版包（外部更新器下载）
└── SingleFile_JASM_v2.31.0.zip    ← 单文件版包（主程序进程内下载）
```

对应地址：

```
https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/app/update.json
```

**一切以 `app/update.json` 为准**：客户端不再从文件名或 GitHub 的 tag 猜版本，清单是唯一事实来源。
清单读不到时（网络 / 欠费 / 权限 / 被刷新覆盖成坏 JSON）才回退到 GitHub Releases API。

## 二、清单 schema（`app/update.json`）

```json
{
  "schemaVersion": 1,
  "releases": [
    {
      "version": "2.31.0",
      "prerelease": false,
      "publishedAt": "2026-09-30T08:00:00Z",
      "notesUrl": "https://github.com/qsy123-coder/JASM/releases/tag/v2.31.0",
      "assets": [
        { "name": "JASM_v2.31.0.7z", "kind": "folder",
          "url": "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/app/JASM_v2.31.0.7z",
          "sizeBytes": 148726913, "sha256": "……" },
        { "name": "SingleFile_JASM_v2.31.0.zip", "kind": "singleFile",
          "url": "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/app/SingleFile_JASM_v2.31.0.zip",
          "sizeBytes": 96384012, "sha256": "……" }
      ]
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `version` | **纯数字点分**（`2.31.0`），**不带 `v` 前缀**。客户端用 `Version.TryParse` 解析，解析不出来的条目会被**静默跳过** |
| `prerelease` | `true` 时普通用户不会被提示更新，仅入档 |
| `publishedAt` | ISO 8601，仅展示用 |
| `notesUrl` | 「看看更新了什么」链接。缺失 / `null` 时客户端退回仓库 releases 页 |
| `assets[].kind` | **显式**判别包类型：`folder`（`.7z`，含独立更新器）/ `singleFile`（`.zip`，单 exe）。缺字段时退回按文件名前缀匹配 |
| `assets[].name` | 包名，**承重**：必须是 `JASM_v<版本>.7z` / `SingleFile_JASM_v<版本>.zip`，GitHub 回退通道就是按这个前缀反推 `kind` 的 |
| `assets[].sizeBytes` | 进度条总量与「.part 是否已达终态」的判断依据；缺省则退回 `Content-Length` |
| `assets[].sha256` | 小写十六进制，下载完严格比对；缺省则跳过校验 |

三条硬规则：

- **两个资产一起发**（`folder` + `singleFile`）。只发一个，对应形态的用户就更新不了 —— 单文件版是
  大多数用户用的那个，漏了它等于漏了大多数人。
- **`version` 与 `.csproj` 的 `<VersionPrefix>` 必须是同一个版本号**。客户端比的是
  `编译版本 < 清单版本`，两边对不上（例如清单里写了 `2.31.0` 但包编译出来是 `2.30.0`）会变成
  「用户点更新 → 更新器发现装的和下的版本一样 → 直接退出」，看起来像点了没反应。
- **同一个包换了内容，就必须同步改清单里的 `sha256` / `sizeBytes`**。文件名不变、哈希不变而内容变，
  用户端会卡在 SHA256 校验失败，且现象上像「什么都没改」。

## 三、发一版：固定四步

```bash
# 1) 打两个包（folder 版本机可跑；单文件版需 MSVC，只能由 CI 产出）
python Build/Release.py                 # → JASM_v2.31.0.7z
python Build/Release.py SingleFile      # → SingleFile_JASM_v2.31.0.zip  （本机无 MSVC 会在此步失败）

# 2) 生成 / 增量更新清单（⚠️ 加新版本前，先把线上那份 update.json 存成本地副本）
python Build/PackAppUpdate.py --version 2.31.0 \
    --folder-package JASM_v2.31.0.7z \
    --single-file-package SingleFile_JASM_v2.31.0.zip

# 3) 上传三个文件到 COS 的 app/ 前缀（COSBrowser 拖拽或控制台均可）
#    Build/out/app/update.json、JASM_v2.31.0.7z、SingleFile_JASM_v2.31.0.zip

# 4) ⚠️ GitHub release 上仍要挂同版本的同名资产（见下节）
gh release upload v2.31.0 JASM_v2.31.0.7z SingleFile_JASM_v2.31.0.zip \
    --repo qsy123-coder/JASM --clobber
```

`PackAppUpdate.py` 会校验包名（必须是 `<前缀>v<版本><扩展名>`）、断言单文件 zip 里确实有
`JASM - Just Another Skin Manager.exe`（主程序自更新最后一步要从这里解出 exe，包不对的话用户会在
**已经开始退出程序**之后才失败），并算出体积与 sha256。它的结尾会把上面第 3 步的注意事项和一条现成的
`gh release upload` 命令再打印一遍。

> ⚠️ **增量合并的正确姿势**：`--manifest` 指向「线上那份 `update.json` 的本地副本」。脚本只把本次这
> 一个版本并进去、保留更早的版本记录；如果你从零开始生成一份，线上清单里的历史版本就都没了。
> 客户端只会看「最新版」，所以不会立刻出问题 —— 直到有人想回退到旧版，才发现记录没了。
> 每次发版前先下载线上那份存到 `Build/out/app/update.json`。

清单默认保留最近 **5** 个版本（`--keep`），与 ModEnv 的 `KeepBackupCount` 对齐。

## 四、⚠️ GitHub 上的资产仍然要挂

切到 COS **不是**「GitHub 可以不管了」：

- **≤2.30.0 的老客户端**只会读 GitHub Releases API，看不到 COS 清单。
- **用户机器上那份旧的 `JASM - Auto Updater.exe`**（folder 版的更新执行体）也只会读 GitHub —— 它是随包
  分发的独立 exe，改了源码不等于用户手上那份会变，得等一次成功的更新才会被替换掉。这一条要撑过
  **一两个版本**才自然消亡。
- **COS 出事时的回退**：清单拉不到（欠费 / 权限被改 / 被刷 / 传成了坏 JSON）时，客户端会自动回退到
  GitHub API。此时只要 GitHub release 上挂着同版本的包，所有客户端就都还能正常更新 —— 这是最快的救火手段。

同版本资产一起挂上去的成本几乎为零，别省。

## 五、手测：不碰真包，走一遍假更新

不用真的发一版就能验完整链路（含进度条）。找个临时目录当假 CDN：

```bash
mkdir C:\jasm-mock-cdn-app
# 1) 放一份真 exe 进单文件 zip（这样"替换并重启"是安全的：换上的还是当前这份）
#    用当前构建产物：src\GIMI-ModManager.WinUI\bin\Debug\net9.0-windows10.0.22621.0\
#    把 "JASM - Just Another Skin Manager.exe" 打成一个 zip
# 2) 生成清单：版本号写成一个远高于当前的大版本（如 99.0.0），kind=singleFile
python Build/PackAppUpdate.py --version 99.0.0 \
    --single-file-package SingleFile_JASM_v99.0.0.zip \
    --base-url http://localhost:8899/ --out C:\jasm-mock-cdn-app

# 3) 起服务
python -m http.server 8899 --directory C:\jasm-mock-cdn-app
```

把 `appsettings.json` 的 `AppUpdate:ManifestUrl` **临时**指到 `http://localhost:8899/update.json`，
重新构建运行（⚠️ 构建前先关掉运行中的 JASM，DLL 会被锁），然后确认：

- 徽标显示 v99.0.0（说明清单被读到了）；
- 点 Update → **进度条走百分比 + MB/MB + 速度**（这是本次改动的核心验收点）；
- 到 100% → 程序退出、exe 被替换、自动重启。

日志在 exe 旁的 `logs\log.txt`（`bin\Debug\...` 下）。关键行：

```
[INF AppUpdateManifestService] App update release 99.0.0 resolved from the COS manifest
```

异常路径也值得走一遍（都不需要改代码）：

| 造法 | 期望 |
|---|---|
| 清单里 `sha256` 写错 | 明确报「更新包校验失败（SHA256 不匹配）」，不静默替换 |
| 下载中途杀掉 mock 服务 | 日志出现 `Resuming download of ... from N bytes`，续传而非从头下 |
| `ManifestUrl` 指到一个 404 | 回退 GitHub，日志有 `No app update release could be resolved. Reason: ...`，行为与改动前一致 |
| 清单版本号写成 `v99.0.0` | 该条被跳过（`PackAppUpdate.py` 会在生成时就拦掉这种写法） |

> 想看清进度条而不是一闪而过，可把 `python -m http.server` 换成 `docs/mod-env-hand-test.md:127` 的
> 节流服务器配方（每次只吐一小块、sleep 一下）。

## 六、大文件与费用

更新包是**百 MB 级**（folder 版约 150 MB、单文件版约 100 MB），比 ModEnv 那批（几十 MB）大不少：

- **默认域名直连对百 MB 的文件仍然可用**，但 `docs/mod-env-cdn-setup.md` 的「几十 MB 内够用」这句在
  这里属于边界外。先直连实测，明显慢再谈自定义 CDN（国内 CDN 需**已备案域名**）。
- **流量费是主要开销**（约 ¥0.5/GB）：一次全量更新大约 0.1 GB/人，按用户量估一下。
- 好在 `PackAppUpdate.py` 默认只保留 5 个版本，桶不会无限膨胀；不再需要的旧包可以删，但删之前想想
  「还想不想让用户从那一版更新过来」—— 删了包而清单里还留着条目，那条就是坏的（`PackAppUpdate.py`
  的体检只会提示 URL 不像 http(s)，不会替你检查对象是否真的存在）。

## 七、开关与回滚

- `appsettings.json` 的 **`AppUpdate:ManifestUrl` 留空 = 完全不走 COS**，直接走 GitHub；
  改回一行配置即可整体回滚到「纯 GitHub」行为。
- `AppUpdate:ReleasesApiUrl` 是回退通道的地址（指向 fork 仓库的 releases API）。
- `AppUpdate:MaxDownloadRetries` / `DownloadStallTimeoutSeconds` / `ProgressReportIntervalMs`
  控制重试次数、无数据超时与进度上报节流（默认 3 次 / 60 秒 / 400 毫秒）。
- **`JASM - Auto Updater.exe` 的清单地址是写死在源码里的**（`MainPageVM.ManifestUrl`）—— 它是随包分发的
  独立进程，拿不到主程序的 `appsettings.json`。改桶 / 改前缀时，这一处和 `appsettings.json`、以及
  `Build/PackAppUpdate.py` 的 `DEFAULT_BASE_URL` **三处要一起改**。

## 参考

- 桶的创建与域名：[`docs/mod-env-cdn-setup.md`](mod-env-cdn-setup.md)
- 手测配方：[`docs/mod-env-hand-test.md`](mod-env-hand-test.md)
- [COS 域名管理概述](https://cloud.tencent.com/document/product/436/18424)
