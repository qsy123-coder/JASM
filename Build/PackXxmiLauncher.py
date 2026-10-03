"""把官方 XXMI Launcher Portable 包打成 JASM 分发的 `launcher-<版本>.zip`，并更新版本清单。

背景
----
JASM 的「一键配置 Mod 环境」会往 XXMI 根目录装一份**离线**的 XXMI 启动器（GUI）。

分发的不是官方 MSI —— 那是**联网引导器**，只含 `XXMILauncher.exe`，Resources / Themes / Locale
首次运行才从境外下载，正是国内用户翻墙问题的来源。分发的也不是本机 `D:\\XXMI` 的手工拷贝，
而是官方 **Portable** zip：它自带 `Locale\\` / `Resources\\Bin\\` / `Themes\\`，解压即离线可跑。

旧做法是从一台已装好的 `D:\\XXMI` 里按清单手工挑文件（见 docs/mod-env-cdn-setup.md），
容易漏文件、也容易把 XXMI 的签名**私钥** `Security\\private_key.der` 一起带出去。
本脚本换成从官方 Portable 包**原样取内容**，只额外做两件事：注入一份干净配置、打包前做安全断言。

干净配置从哪来
--------------
不手搓 —— 配置有几十个字段、且随启动器版本变。做法是拿**启动器自己生成的那份**：
在一台机器上跑一次新版本 Portable，它会在 XXMI 根目录写出 `XXMI Launcher Config.json`，
用 `--config` 指给本脚本，脚本按下面这张表清洗（改的都是「随机器/随部署变」的字段）：

    Launcher.auto_update                        -> false   不让它去 GitHub 自更新（国内连不上）
    Launcher.log_level                          -> "INFO"
    Launcher.locale                             -> "CN"
    Security.user_signature                     -> ""      机器专属签名
    Importers.<ID>.Importer.importer_folder     -> "<ID>/" 相对路径，JASM 一键配置时替换成绝对路径
    Importers.<ID>.Importer.game_folder         -> ""      JASM 一键配置时按实际游戏目录回填
    Importers.<ID>.Importer.shortcut_deployed   -> false
    Importers.<ID>.Importer.launch_count        -> 0
    Importers.<ID>.Importer.deployed_migoto_signatures -> {}  部署态，机器专属
    Importers.<ID>.Importer.*_warned            -> false
    Packages.packages.<包>.update_check_time    -> 0
    Packages.packages.<包>.skipped_version      -> ""
    Packages.packages.<包>.latest_release_notes / deployed_release_notes -> ""
    Packages.packages.<包>.latest_version / deployed_version -> **保留**

最后一行是有意的：保留的正是打包这台机器上官方装的版本，与 JASM 当下实装的一致，
启动器就不会反复提示「更新」。日后 JASM 实装的版本变了，`ModEnvSetupFacade.AlignStaleLauncherVersions`
会把**更旧**的缓存对齐过来（缓存比实装新时不动，那是真的可更新）。

安全断言（打不出坏包）
--------------------
打包后重新打开 zip 复查，任一条不成立就删掉产物并退出：

  * 必须含 `Resources/Bin/XXMI Launcher.exe`、`Locale/locale_index.toml`、
    `Themes/Default/MainWindow/LauncherFrame/background-image-xxmi.webp`
    （后两个是实测踩过的坑：Locale 少了 Strings 层、Themes 被压平都会让启动器起不来）
  * 必须恰好等于**源包内容 + 注入的两份配置** —— 多一个少一个都说明重打过程出了问题
  * 不得含 `Security/`（含签名私钥）、`Resources/Packages/Launcher/TMP/`（90MB 旧版引导器）
  * 不得含 `Resources/Bin/` 下的 `*.log`

> 注：Locale 文案的修订号**不跟启动器版本走**（2.3.8 的包里最高只到 `CN_2.3.1.toml`），
> 所以别拿 `CN_<启动器版本>.toml` 当「拿对包了」的判据 —— 脚本只做「比启动器版本更新就告警」。

可复现
------
打包是确定性的：条目元数据（时间戳 / 权限位）全部复制自源包，注入的两份配置取启动器 exe 的
时间戳。同一份源包 + 同一份配置连跑两次，产物 sha256 完全一致 —— version.json 里写的就是它，
所以「重跑一遍核对」不会把线上哈希搞乱（反例：`zipfile.writestr("路径", 数据)` 会写入**当前时间**）。

用法
----
    python Build/PackXxmiLauncher.py

    # 指定源包 / 配置 / 输出目录，并顺手把 version.json 的 launcher 条目改掉
    python Build/PackXxmiLauncher.py \\
        --source "D:/.../XXMI-Launcher-Portable-v2.3.8.zip" \\
        --config "D:/XXMI/XXMI Launcher Config.json" \\
        --out Build/out/xxmi-versions \\
        --update-manifest Build/out/xxmi-versions/version.json

产物
----
    <out>/launcher-<版本>.zip    直接传 CDN 的启动器离线包
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

# 官方 Portable 包的默认目录（`XXMI软件本体更新包（持续更新）`，与 PackXxmiVersions.py 的
# `XXMI更新包（持续更新）`是两个不同目录：前者是启动器本体，后者是注入器框架版本包）。
# 用原始字符串 —— 路径里的 \B \M \X 会被 Python 当成非法转义并报 SyntaxWarning。
DEFAULT_SOURCE_DIR = r"D:\BaiduNetdiskDownload\MC-MOD整合包\XXMI软件本体更新包（持续更新）"

# 启动器自己生成的配置：跑一次 Portable 后落在 XXMI 根目录。
DEFAULT_CONFIG = r"D:\XXMI\XXMI Launcher Config.json"

# 默认与 appsettings.json 的 ModEnv:ManifestUrl 同桶同前缀，改桶时两处一起改。
DEFAULT_BASE_URL = "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/modenv/"

DEFAULT_OUT = "Build/out/xxmi-versions"

SOURCE_GLOB = "XXMI-Launcher-Portable-v*.zip"
SOURCE_VERSION_PATTERN = re.compile(r"^XXMI-Launcher-Portable-v(\d+\.\d+\.\d+)\.zip$", re.IGNORECASE)

# Locale 文案文件的修订号（与启动器版本无关，见 assert_package 里的说明）。
LOCALE_FILE_PATTERN = re.compile(r"^Locale/Strings/CN/CN_(\d+\.\d+\.\d+)\.toml$")

CONFIG_NAME = "XXMI Launcher Config.json"
BACKUP_DIR = "Backups"
LAUNCHER_EXE = "Resources/Bin/XXMI Launcher.exe"

# 与 PackXxmiVersions.py 一致：最大压缩。注意必须显式传给 writestr —— 传 ZipInfo 时
# zipfile 不读 ZipFile 的 compresslevel，会退回 zlib 默认级别（包会大出约 0.2MB）。
COMPRESS_LEVEL = 9

# 打进去以后必须存在的关键文件。后两个是实测踩过的坑（见 mod-env-cdn-setup.md）：
# Locale 少了 `Strings` 那层、Themes 被压平，都会让启动器起不来。
REQUIRED_ENTRIES = (
    LAUNCHER_EXE,
    "Locale/locale_index.toml",
    "Themes/Default/MainWindow/LauncherFrame/background-image-xxmi.webp",
)

# 绝不能出现在可公开下载的包里。
FORBIDDEN_PREFIXES = (
    "Security/",                      # 含 XXMI 签名私钥 private_key.der
    "Resources/Packages/Launcher/TMP/",  # 90MB 的旧版联网引导器
)

# 文件名里出现这些片段同样一票否决（防前缀写法变了之后漏网）。
FORBIDDEN_SUBSTRINGS = ("private_key",)

# 配置里每个包的版本字段：保留 latest/deployed（见模块 docstring），只清易变字段。
PACKAGE_VOLATILE_FIELDS = {
    "update_check_time": 0,
    "skipped_version": "",
    "latest_release_notes": "",
    "deployed_release_notes": "",
}


def sha256_of(path: Path) -> str:
    """返回文件 SHA256 的小写十六进制（与 version.json / JASM 校验用的格式一致）。"""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_version_from_name(path: Path) -> str:
    match = SOURCE_VERSION_PATTERN.match(path.name)
    if not match:
        raise SystemExit(f"源包文件名不符合官方命名 `XXMI-Launcher-Portable-vX.Y.Z.zip`：{path.name}")
    return match.group(1)


def version_sort_key(version: str) -> tuple[int, int, int]:
    return tuple(int(part) for part in version.split("."))  # type: ignore[return-value]


def locale_version_of(entry_name: str) -> str:
    match = LOCALE_FILE_PATTERN.match(entry_name)
    if not match:
        raise ValueError(f"不是 Locale 文案文件：{entry_name}")
    return match.group(1)


def locale_sort_key(entry_name: str) -> tuple[int, int, int]:
    return version_sort_key(locale_version_of(entry_name))


def locate_source(source_arg: str | None) -> Path:
    """`--source` 给了就用它，否则在默认目录里挑版本号最高的 Portable 包。"""
    if source_arg:
        path = Path(source_arg)
        if not path.is_file():
            raise SystemExit(f"源包不存在：{path}")
        return path

    source_dir = Path(DEFAULT_SOURCE_DIR)
    if not source_dir.is_dir():
        raise SystemExit(f"源目录不存在：{source_dir}（可用 --source 指定 Portable zip）")

    candidates = [p for p in source_dir.glob(SOURCE_GLOB) if SOURCE_VERSION_PATTERN.match(p.name)]
    if not candidates:
        raise SystemExit(f"{source_dir} 下没找到 {SOURCE_GLOB}")

    def sort_key(path: Path) -> tuple[int, int, int]:
        return version_sort_key(read_version_from_name(path))

    return max(candidates, key=sort_key)


def sanitize_config(text: str) -> dict:
    """把启动器生成的配置清洗成可随包分发的干净默认（清洗规则见模块 docstring）。"""
    config = json.loads(text.lstrip("﻿"))

    launcher = config.setdefault("Launcher", {})
    launcher["auto_update"] = False
    launcher["log_level"] = "INFO"
    launcher["locale"] = "CN"

    security = config.get("Security")
    if isinstance(security, dict):
        security["user_signature"] = ""

    importers = config.get("Importers")
    if isinstance(importers, dict):
        for importer_id, wrapper in importers.items():
            importer = wrapper.get("Importer") if isinstance(wrapper, dict) else None
            if not isinstance(importer, dict):
                continue
            # 相对路径（`WWMI/`）是有意的：JASM 一键配置时会把「空或非绝对路径」替换成绝对路径。
            importer["importer_folder"] = f"{importer_id}/"
            importer["game_folder"] = ""
            importer["shortcut_deployed"] = False
            importer["launch_count"] = 0
            importer["deployed_migoto_signatures"] = {}
            for key in list(importer):
                if key.endswith("_warned"):
                    importer[key] = False

    packages = config.get("Packages", {}).get("packages") if isinstance(config.get("Packages"), dict) else None
    if isinstance(packages, dict):
        for entry in packages.values():
            if isinstance(entry, dict):
                entry.update(PACKAGE_VOLATILE_FIELDS)

    return config


def dumps_config(config: dict) -> bytes:
    """按启动器自己的写法序列化：4 空格缩进、CRLF、**无 BOM**。

    无 BOM 是硬要求 —— 启动器的 Python `json.loads` 遇到 BOM 会抛 `Unexpected UTF-8 BOM`，
    首次启动弹「加载配置失败」。
    """
    text = json.dumps(config, indent=4, ensure_ascii=False)
    return text.replace("\n", "\r\n").encode("utf-8")


def clone_info(info: zipfile.ZipInfo, name: str) -> zipfile.ZipInfo:
    """复制源条目的元数据再改名。

    不能直接 `writestr("路径", 数据)` —— 那样 zip 里的时间戳会取**当前时间**，同一份源包
    连打两次哈希就不一样了，脚本也就谈不上可复现（而 version.json 里写的就是这个哈希）。
    """
    clone = zipfile.ZipInfo(name, date_time=info.date_time)
    clone.compress_type = zipfile.ZIP_DEFLATED
    clone.external_attr = info.external_attr
    clone.create_system = info.create_system
    return clone


def config_entry_stamp(source: Path) -> tuple[int, int, int, int, int, int]:
    """注入的配置用源包里启动器 exe 的时间戳，保证打包结果稳定。"""
    with zipfile.ZipFile(source) as origin:
        for info in origin.infolist():
            if info.filename.replace("\\", "/") == LAUNCHER_EXE:
                return info.date_time
    return (1980, 1, 1, 0, 0, 0)


def build_package(source: Path, config_bytes: bytes, out_zip: Path) -> None:
    """把 Portable 包原样重打成新 zip，并注入干净配置（根目录一份 + Backups 一份）。"""
    stamp = config_entry_stamp(source)

    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=COMPRESS_LEVEL) as target:
        with zipfile.ZipFile(source) as origin:
            for info in origin.infolist():
                name = info.filename.replace("\\", "/")
                # 源包若自带配置就不重复注入（官方 Portable 没有，但别依赖这一点）。
                if name in (CONFIG_NAME, f"{BACKUP_DIR}/{CONFIG_NAME}"):
                    continue
                if name.endswith("/"):
                    target.writestr(clone_info(info, name), b"", compresslevel=COMPRESS_LEVEL)
                else:
                    target.writestr(clone_info(info, name), origin.read(info), compresslevel=COMPRESS_LEVEL)

        for name in (CONFIG_NAME, f"{BACKUP_DIR}/{CONFIG_NAME}"):
            entry = zipfile.ZipInfo(name, date_time=stamp)
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            entry.create_system = 3  # unix
            target.writestr(entry, config_bytes, compresslevel=COMPRESS_LEVEL)


def assert_package(zip_path: Path, source: Path, version: str) -> None:
    """复查产物，任一条不成立就删掉产物并退出。

    「拿错包」只能查到这一步：版本号取自官方文件名，本脚本不解析 PE 版本资源。所以额外拿**源包**
    逐条目比对 —— 产物必须恰好等于「源包内容 + 注入的两份配置」，多一个少一个都算坏包。
    """
    with zipfile.ZipFile(zip_path) as archive:
        names = [name.filename.replace("\\", "/") for name in archive.infolist()]
        files = {name for name in names if not name.endswith("/")}
        config_text = archive.read(CONFIG_NAME).decode("utf-8-sig")
    with zipfile.ZipFile(source) as origin:
        source_names = {name.filename.replace("\\", "/") for name in origin.infolist()}

    problems: list[str] = []
    warnings: list[str] = []

    for entry in REQUIRED_ENTRIES:
        if entry not in files:
            problems.append(f"缺少关键文件 {entry}")

    # Locale 的版本号是**文案修订号**，不跟启动器版本走（2.3.8 里最高只到 CN_2.3.1.toml），
    # 所以这里只断言「没被压平」，不拿它跟启动器版本对齐。
    locale_files = [n for n in files if LOCALE_FILE_PATTERN.match(n)]
    if not locale_files:
        problems.append("Locale/Strings/CN/ 下没有任何 CN_x.y.z.toml（Locale 层级被压平了？）")
    else:
        newest = max(locale_files, key=locale_sort_key)
        if version_sort_key(locale_version_of(newest)) > version_sort_key(version):
            warnings.append(f"文案修订 {locale_version_of(newest)} 比启动器版本 {version} 还新，确认没拿错包")

    expected = source_names | {CONFIG_NAME, f"{BACKUP_DIR}/{CONFIG_NAME}"}
    missing = sorted(expected - set(names))
    extra = sorted(set(names) - expected)
    if missing:
        problems.append(f"比源包少了 {missing[:5]}")
    if extra:
        problems.append(f"比源包多了 {extra[:5]}")

    for name in names:
        if name.startswith(FORBIDDEN_PREFIXES):
            problems.append(f"含禁止条目 {name}")
        if any(fragment in name for fragment in FORBIDDEN_SUBSTRINGS):
            problems.append(f"含敏感片段 {name}")
        if name.startswith("Resources/Bin/") and name.lower().endswith(".log"):
            problems.append(f"含运行日志 {name}")

    config = json.loads(config_text)
    if config.get("Launcher", {}).get("auto_update") is not False:
        problems.append("配置里 Launcher.auto_update 不是 false")
    if config.get("Security", {}).get("user_signature"):
        problems.append("配置里 Security.user_signature 没清空（机器专属）")
    for importer_id, wrapper in (config.get("Importers") or {}).items():
        importer = wrapper.get("Importer") or {}
        if importer.get("game_folder"):
            problems.append(f"配置里 Importers.{importer_id}.Importer.game_folder 不是空")
        if importer.get("importer_folder") != f"{importer_id}/":
            problems.append(f"配置里 Importers.{importer_id}.Importer.importer_folder 不是 {importer_id}/")

    for warning in warnings:
        print(f"  ! {warning}")

    if problems:
        zip_path.unlink(missing_ok=True)
        detail = "\n  - ".join(problems)
        raise SystemExit(f"打包校验失败：{zip_path.name}\n  - {detail}\n已删除该包，请检查源包。")


def update_manifest(manifest_path: Path, version: str, download_url: str, sha256: str, size_bytes: int) -> None:
    """把 version.json 里 launcher 那一条改成新包，其余条目原样保留。"""
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    packages = manifest.setdefault("Packages", {})
    packages["launcher"] = {
        "Version": version,
        "DownloadUrl": download_url,
        "Sha256": sha256,
        "SizeBytes": size_bytes,
        "GameVersion": None,
        "CompatibleGameVersions": [],
    }
    # 与同目录其它清单一致：UTF-8 无 BOM + 2 空格缩进 + 结尾换行。
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description="把官方 XXMI Launcher Portable 包打成 JASM 分发的启动器离线包")
    parser.add_argument("--source", default=None,
                        help=f"官方 Portable zip（默认取 {DEFAULT_SOURCE_DIR} 下版本号最高的那个）")
    parser.add_argument("--config", default=DEFAULT_CONFIG, help="启动器生成的 XXMI Launcher Config.json")
    parser.add_argument("--out", default=DEFAULT_OUT, help="产物输出目录")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="CDN 上 modenv/ 目录的 URL 前缀")
    parser.add_argument("--update-manifest", default=None, help="顺手改写这份 version.json 的 launcher 条目")
    args = parser.parse_args()

    source = locate_source(args.source)
    version = read_version_from_name(source)
    out_dir = Path(args.out)
    base_url = args.base_url if args.base_url.endswith("/") else args.base_url + "/"
    config_path = Path(args.config)

    print("PackXxmiLauncher.py")
    print(f"源包   : {source}")
    print(f"配置   : {config_path}")
    print(f"输出到 : {out_dir}")
    print(f"CDN前缀: {base_url}")
    print()

    if not config_path.is_file():
        raise SystemExit(
            f"找不到启动器配置：{config_path}\n"
            "先在 XXMI 根目录跑一次新版本启动器让它生成配置，再用 --config 指过来。"
        )

    config_bytes = dumps_config(sanitize_config(config_path.read_text(encoding="utf-8-sig")))

    out_dir.mkdir(parents=True, exist_ok=True)
    zip_path = out_dir / f"launcher-{version}.zip"
    build_package(source, config_bytes, zip_path)
    assert_package(zip_path, source, version)

    size_bytes = zip_path.stat().st_size
    digest = sha256_of(zip_path)
    download_url = f"{base_url}{zip_path.name}"

    with zipfile.ZipFile(zip_path) as archive:
        entry_count = len(archive.infolist())

    print(f"  ✓ {zip_path.name}  {size_bytes / 1024 / 1024:.2f} MB  "
          f"{entry_count} 个条目  sha256={digest[:16]}…")

    if args.update_manifest:
        manifest_path = Path(args.update_manifest)
        if not manifest_path.is_file():
            raise SystemExit(f"要改写的 version.json 不存在：{manifest_path}")
        update_manifest(manifest_path, version, download_url, digest, size_bytes)
        print(f"  ✓ 已更新 {manifest_path} 的 Packages.launcher -> {version}")

    print()
    print("下一步：把该 zip 上传到 CDN（与 appsettings.json 的 ModEnv:ManifestUrl 同桶同前缀），")
    print(f"        再把改好的 version.json 一并传上去。别只传 zip 不传清单 —— 用户端会卡在 SHA256 校验失败。")
    print(f"    {download_url}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
