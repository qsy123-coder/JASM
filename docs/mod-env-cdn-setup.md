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

建议在桶里建一个子目录 `modenv/`，放 3 类文件：

| 文件 | 内容要求 |
|---|---|
| `xxmi-<版本>.zip` | XXMI 注入器框架包。解压后至少含 1 个文件/目录即可（JASM 用它判定「基础包是否装好」） |
| `wwmi-<版本>.zip` | WWMi 鸣潮游戏包。解压后**必须**在包根目录有 `WWMI Loader.exe` 和 `Mods\` 文件夹（JASM 硬性校验这两个） |
| `version.json` | 版本清单，见下节 |

WWMi 包结构示意：

```
wwmi-1.0.0.zip
├── WWMI Loader.exe   ← 必须有
└── Mods\             ← 必须有
```

> 包根目录如果只有一个文件夹（例如解压后是 `WWMI\...`），JASM 会自动解开这一层再复制，所以
> 用官方 release 的原样 zip 通常也没问题——但务必确认解压后能看到 `WWMI Loader.exe` 和 `Mods\`。

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
  "BasePackageId": "xxmi"
}
```

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
