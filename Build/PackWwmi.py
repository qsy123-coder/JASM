"""把官方 WWMI-PACKAGE 包打成 JASM 分发的 `wwmi-<版本>.zip`，并更新版本清单与可选版本目录。

背景
----
JASM 的「一键配置 Mod 环境」把 WWMI（鸣潮的 3DMigoto 游戏包）装进 MI 文件夹
（`<XXMI 根>\\WWMI`）。分发的**不是**官方包原件：官方 `WWMI-PACKAGE-vX.Y.Z.zip` 只含
`Core\\` / `d3dx.ini` / `Mods\\`（空）/ `README.md` / `ShaderFixes\\`，约 75 KB —— 里面
没有注入器 DLL，因为按官方那套分工，DLL 是 XXMI 启动器从框架包里部署进去的。

而 JASM 的 `CheckGamePackageFiles` 要求 MI 文件夹里同时有 `d3d11.dll` + `d3dx.ini` +
`Mods\\`，且每次配置收尾的 `DeployFrameworkIntoMiFolder` 本来就会把 DLL 覆盖成 JASM 管理
的那份 —— 也就是说包里那两份 DLL 只为了让「装完那一刻」的校验能过。

配方 = **官方包内容原样** + 从实机 MI 文件夹补进三份文件与一个空目录（见 EXTRA_FILES）。

为什么包里有 d3dx_user.ini
--------------------------
它是 3DMigoto 的**用户**文件（按键 / 开关覆写），不是包自己的配置 —— 包自己的配置是
`d3dx.ini`。JASM 安装游戏包时会把它列进 `preserveExistingFiles`（见
ModEnvSetupFacade.WwmiUserIniFileName），所以它**只在全新安装时**落到用户机器上，之后
切版本不会再覆盖用户的改动。换句话说它是「新装用户的初始按键表」，不是会被反复分发的配置。

⚠️ 发出去的就是打包机上那份（维护者自己的按键），新装用户拿到同一份默认值。

绝不外发
--------
  * `Mods\\` 下的任何文件 —— 那是用户的 mod（实机上通常几百个）
  * `ShaderCache\\` 下的任何文件 —— 运行时缓存（实机上几千个）

这两条在 assert_package 里逐条检查、一票否决：宁可不发包，也不能把用户的东西分发出去。

可复现
------
官方条目原样克隆元数据（时间戳 / 权限位 / 压缩方式）；补进去的三份文件时间戳统一取官方包内
`d3dx.ini` 的时间戳、权限位写常量 —— **不从实机文件读任何属性**。所以「同一份官方包 + 同一个
`--extra-dir` 内容」连跑两次，产物 sha256 完全一致（哈希要写进清单，不这样就谈不上重跑核对）。

⚠️ 但 `--extra-dir` 指向实机目录时，**实机那几份内容变了产物就会变**。脚本会把取用的三个文件
的 sha256 打印出来；改过实机目录就得重传，并同步两份清单里的哈希。

用法
----
    # 扫默认源目录，把所有版本都打一遍，并更新清单与目录
    python Build/PackWwmi.py \\
        --update-manifest Build/out/xxmi-versions/version.json \\
        --update-catalog Build/out/xxmi-versions/wwmi-versions.json

    # 只打某一版
    python Build/PackWwmi.py --official "D:/.../WWMI-PACKAGE-v1.1.0.zip"

产物
----
    <out>/wwmi-<版本>.zip       直接传 CDN 的 WWMI 游戏包
    <out>/wwmi-versions.json    可选版本目录，**增量**并入（见 update_catalog）
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

# 官方 WWMI-PACKAGE 包的默认目录（与 PackXxmiVersions / PackXxmiLauncher 的源目录是三个不同
# 目录：前者是注入器框架版本包，后者是启动器本体，这里是游戏包）。
# 用原始字符串 —— 路径里的 \W \X 会被 Python 当成非法转义并报 SyntaxWarning。
DEFAULT_SOURCE_DIR = r"D:\BaiduNetdiskDownload\MC-MOD整合包\WWMI官方包（持续更新）"

# 补进去的那几份从哪来：一台配置好 Mod 环境的机器上的 MI 文件夹。
DEFAULT_EXTRA_DIR = r"D:\XXMI\WWMI"

# 默认与 appsettings.json 的 ModEnv:ManifestUrl 同桶同前缀，改桶时三处一起改。
DEFAULT_BASE_URL = "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/modenv/"

DEFAULT_OUT = "Build/out/xxmi-versions"

SOURCE_GLOB = "WWMI-PACKAGE-v*.zip"
SOURCE_VERSION_PATTERN = re.compile(r"^WWMI-PACKAGE-v(\d+\.\d+\.\d+)\.zip$", re.IGNORECASE)

# 可选版本目录，schema 与 xxmi-versions.json / launcher-versions.json 完全一致：
# {"CatalogVersion": 1, "Versions": [{Version, DownloadUrl, Sha256, SizeBytes, ReleasedAt, Notes}]}
CATALOG_NAME = "wwmi-versions.json"

# 官方包里必有的那份配置（也进 REQUIRED_ENTRIES）。
D3DX_INI = "d3dx.ini"

MODS_DIR = "Mods"
SHADER_CACHE_DIR = "ShaderCache"

# 从实机 MI 文件夹补进包里的东西。三份文件（缺一即报错退出）+ 一个空目录。
EXTRA_FILES = ("d3d11.dll", "d3dcompiler_47.dll", "d3dx_user.ini")
EXTRA_EMPTY_DIRS = (SHADER_CACHE_DIR + "/",)

# 打进去以后必须存在的关键条目。
REQUIRED_ENTRIES = (D3DX_INI, "Core/", MODS_DIR + "/")

# 这两个目录下一个文件都不能有（用户 mod / 运行时缓存），见模块 docstring。
MUST_BE_EMPTY_DIRS = (MODS_DIR + "/", SHADER_CACHE_DIR + "/")

# 与另两个打包脚本一致：最大压缩。必须显式传给 writestr —— 传 ZipInfo 时 zipfile 不读
# ZipFile 的 compresslevel，会退回 zlib 默认级别。
COMPRESS_LEVEL = 9


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
        raise SystemExit(f"源包文件名不符合官方命名 `WWMI-PACKAGE-vX.Y.Z.zip`：{path.name}")
    return match.group(1)


def version_sort_key(version: str) -> tuple[int, int, int]:
    return tuple(int(part) for part in version.split("."))  # type: ignore[return-value]


def locate_sources(official_arg: str | None, source_dir: str) -> list[Path]:
    """返回要打包的官方包，按版本号**降序**。

    给了 `--official` 就只用它；否则扫源目录，把找到的每一版都打一遍 —— 官方包很小，
    一次把目录里的版本补齐，比事后发现漏了一版再补打省事。
    """
    if official_arg:
        path = Path(official_arg)
        if not path.is_file():
            raise SystemExit(f"源包不存在：{path}")
        return [path]

    directory = Path(source_dir)
    if not directory.is_dir():
        raise SystemExit(f"源目录不存在：{directory}（可用 --official 指定单个官方 zip）")

    candidates = [p for p in directory.glob(SOURCE_GLOB) if SOURCE_VERSION_PATTERN.match(p.name)]
    if not candidates:
        raise SystemExit(f"{directory} 下没找到 {SOURCE_GLOB}")

    return sorted(candidates, key=lambda p: version_sort_key(read_version_from_name(p)), reverse=True)


def clone_info(info: zipfile.ZipInfo, name: str) -> zipfile.ZipInfo:
    """复制源条目的元数据再改名。

    不能直接 `writestr("路径", 数据)` —— 那样 zip 里的时间戳会取**当前时间**，同一份源包
    连打两次哈希就不一样了，脚本也就谈不上可复现（而清单里写的就是这个哈希）。
    """
    clone = zipfile.ZipInfo(name, date_time=info.date_time)
    clone.compress_type = zipfile.ZIP_DEFLATED
    clone.external_attr = info.external_attr
    clone.create_system = info.create_system
    return clone


def official_stamp(source: Path) -> tuple[int, int, int, int, int, int]:
    """官方包内**最新**的条目时间戳。两个用途：

    ① 补进去那几份统一用它的时间戳 —— 保证产物可复现，且与实机文件的时间无关；
    ② 当作发布日期 —— 上游每版都会动若干文件，所以包里最新那个时间戳能反映这版何时发的
    （实测 v1.0.0 -> 2026-03-30、v1.1.0 -> 2026-10-04）。

    ⚠️ 别改用 `d3dx.ini` 的时间戳当日期：实测 1.1.0 根本没动过它，两版会算出同一个日期。
    """
    with zipfile.ZipFile(source) as origin:
        stamps = [info.date_time for info in origin.infolist()]
    if not stamps:
        raise SystemExit(f"官方包是空的：{source.name}")
    return max(stamps)


def build_package(source: Path, extra_dir: Path, out_zip: Path) -> list[tuple[str, str]]:
    """官方内容原样重打 + 补进实机那三份文件与一个空目录。返回补进去的文件的 (名字, sha256)。"""
    stamp = official_stamp(source)
    extras: list[tuple[str, str]] = []

    for name in EXTRA_FILES:
        path = extra_dir / name
        if not path.is_file():
            raise SystemExit(
                f"实机来源目录里找不到 {name}：{path}\n"
                "MI 文件夹不完整（没配好 Mod 环境？），或改用 --extra-dir 指一个装好的目录。"
            )
        extras.append((name, sha256_of(path)))

    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=COMPRESS_LEVEL) as target:
        with zipfile.ZipFile(source) as origin:
            for info in origin.infolist():
                name = info.filename.replace("\\", "/")
                if name.endswith("/"):
                    target.writestr(clone_info(info, name), b"", compresslevel=COMPRESS_LEVEL)
                else:
                    target.writestr(clone_info(info, name), origin.read(info), compresslevel=COMPRESS_LEVEL)

        # 补进去的条目元数据全部写常量，不从实机文件读 —— 那会让产物随机器而变。
        for name, _ in extras:
            entry = zipfile.ZipInfo(name, date_time=stamp)
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            entry.create_system = 3  # unix
            target.writestr(entry, (extra_dir / name).read_bytes(), compresslevel=COMPRESS_LEVEL)

        for name in EXTRA_EMPTY_DIRS:
            entry = zipfile.ZipInfo(name, date_time=stamp)
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o40755 << 16
            entry.create_system = 3  # unix
            target.writestr(entry, b"", compresslevel=COMPRESS_LEVEL)

    return extras


def assert_package(zip_path: Path, source: Path) -> None:
    """复查产物，任一条不成立就删掉产物并退出。"""
    with zipfile.ZipFile(zip_path) as archive:
        names = [name.filename.replace("\\", "/") for name in archive.infolist()]
        files = {name for name in names if not name.endswith("/")}
    with zipfile.ZipFile(source) as origin:
        source_names = {name.filename.replace("\\", "/") for name in origin.infolist()}

    problems: list[str] = []

    for entry in REQUIRED_ENTRIES:
        if entry not in names:
            problems.append(f"缺少 {entry}")

    for name in EXTRA_FILES:
        if name not in files:
            problems.append(f"缺少补进去的 {name}")

    # 一票否决：用户的 mod 与运行时缓存绝不能出现在可公开下载的包里。
    for prefix in MUST_BE_EMPTY_DIRS:
        leaked = sorted(n for n in files if n.startswith(prefix))
        if leaked:
            problems.append(f"{prefix} 下不该有文件，却有 {len(leaked)} 个（例如 {leaked[:3]}）")

    expected = source_names | set(EXTRA_FILES) | set(EXTRA_EMPTY_DIRS)
    missing = sorted(expected - set(names))
    extra = sorted(set(names) - expected)
    if missing:
        problems.append(f"比源包少了 {missing[:5]}")
    if extra:
        problems.append(f"比源包多了 {extra[:5]}")

    if problems:
        zip_path.unlink(missing_ok=True)
        detail = "\n  - ".join(problems)
        raise SystemExit(f"打包校验失败：{zip_path.name}\n  - {detail}\n已删除该包，请检查源包与来源目录。")


def update_manifest(manifest_path: Path, version: str, download_url: str, sha256: str,
                    size_bytes: int) -> None:
    """把 version.json 里 wwmi 那一条改成新包，其余条目原样保留。

    `GameVersion` / `CompatibleGameVersions` 是**兼容性**数据（JASM 据此决定要不要弹不兼容
    警告），与包字节无关，所以沿用原条目里的值，不在这里清掉。
    """
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    packages = manifest.setdefault("Packages", {})
    previous = packages.get("wwmi") or {}
    packages["wwmi"] = {
        "Version": version,
        "DownloadUrl": download_url,
        "Sha256": sha256,
        "SizeBytes": size_bytes,
        "GameVersion": previous.get("GameVersion"),
        "CompatibleGameVersions": previous.get("CompatibleGameVersions") or [],
    }
    # 与同目录其它清单一致：UTF-8 无 BOM + 2 空格缩进 + 结尾换行。
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def release_date_of(source: Path) -> str:
    """取官方包内最新条目的时间戳作为发布日期，格式 `YYYY-MM-DD`。

    用它而不是源 zip 的 mtime（重下一次 mtime 就变，不代表发布日）也不是下载时间。
    zip 的 date_time 已经是一个本地时间元组，直接拼即可。
    """
    year, month, day = official_stamp(source)[:3]
    return f"{year:04d}-{month:02d}-{day:02d}"


def update_catalog(catalog_path: Path, version: str, download_url: str, sha256: str, size_bytes: int,
                   released_at: str) -> None:
    """把这一版增量并入 wwmi-versions.json，其余条目原样保留。

    增量（而不是从零生成）是硬要求：源目录里通常只放最近几版，从零生成会把更早的记录整段抹掉
    —— 客户端只看最新版，所以不会立刻出事，直到有人想把某个坏掉的版本退回去，而那正是这份
    目录存在的理由。

    `Notes` 是维护者手写的（例如「1.0.0 与 2.x 客户端不兼容」），重跑必须保住：同名版本只覆盖
    由文件本身决定的那四项（url / 哈希 / 体积 / 发布日期）。`GameVersion` 这类手填字段同理。
    """
    if catalog_path.is_file():
        catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    else:
        catalog = {"CatalogVersion": 1, "Versions": []}

    versions = catalog.setdefault("Versions", [])
    entry = {
        "Version": version,
        "DownloadUrl": download_url,
        "Sha256": sha256,
        "SizeBytes": size_bytes,
        "ReleasedAt": released_at,
    }

    existing = next((item for item in versions if item.get("Version") == version), None)
    if existing is None:
        entry["Notes"] = ""
        versions.append(entry)
    else:
        existing.update(entry)

    versions.sort(key=lambda item: version_sort_key(item["Version"]), reverse=True)

    catalog_path.write_text(
        json.dumps(catalog, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description="把官方 WWMI-PACKAGE 包打成 JASM 分发的 WWMI 游戏包")
    parser.add_argument("--source-dir", default=DEFAULT_SOURCE_DIR,
                        help="官方 WWMI-PACKAGE zip 所在目录（默认扫该目录下所有版本）")
    parser.add_argument("--official", default=None, help="只打这一份官方 zip（覆盖 --source-dir）")
    parser.add_argument("--extra-dir", default=DEFAULT_EXTRA_DIR,
                        help="补进包里的 d3d11.dll / d3dcompiler_47.dll / d3dx_user.ini 从哪来")
    parser.add_argument("--out", default=DEFAULT_OUT, help="产物输出目录")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="CDN 上 modenv/ 目录的 URL 前缀")
    parser.add_argument("--update-manifest", default=None,
                        help="把**本次打的最高版本**写进这份 version.json 的 Packages.wwmi")
    parser.add_argument("--update-catalog", default=None,
                        help=f"把每一版增量并入这份 {CATALOG_NAME}（客户端 WWMi 版本下拉读它）")
    args = parser.parse_args()

    sources = locate_sources(args.official, args.source_dir)
    out_dir = Path(args.out)
    extra_dir = Path(args.extra_dir)
    base_url = args.base_url if args.base_url.endswith("/") else args.base_url + "/"

    print("PackWwmi.py")
    print(f"官方包 : {', '.join(p.name for p in sources)}")
    print(f"来源   : {extra_dir}")
    print(f"输出到 : {out_dir}")
    print(f"CDN前缀: {base_url}")
    print()

    if not extra_dir.is_dir():
        raise SystemExit(f"实机来源目录不存在：{extra_dir}（一台上配好 Mod 环境的机器，或 --extra-dir）")

    out_dir.mkdir(parents=True, exist_ok=True)

    packaged: list[tuple[str, Path, str, int, str]] = []
    for source in sources:
        version = read_version_from_name(source)
        zip_path = out_dir / f"wwmi-{version}.zip"
        extras = build_package(source, extra_dir, zip_path)
        assert_package(zip_path, source)

        size_bytes = zip_path.stat().st_size
        digest = sha256_of(zip_path)
        released_at = release_date_of(source)
        packaged.append((version, zip_path, digest, size_bytes, released_at))

        print(f"  ✓ {zip_path.name}  {size_bytes / 1024 / 1024:.2f} MB  sha256={digest[:16]}…  ({released_at})")
        for name, file_digest in extras:
            print(f"      补入 {name}  sha256={file_digest[:16]}…")

    if args.update_manifest:
        # 多版本扫描时以**最高版本**为清单默认：清单只能表达一个版本，而它就是「用户该拿到的那个」。
        version, _, digest, size_bytes, _ = max(packaged, key=lambda item: version_sort_key(item[0]))
        manifest_path = Path(args.update_manifest)
        if not manifest_path.is_file():
            raise SystemExit(f"要改写的 version.json 不存在：{manifest_path}")
        update_manifest(manifest_path, version, f"{base_url}wwmi-{version}.zip", digest, size_bytes)
        print(f"  ✓ 已更新 {manifest_path} 的 Packages.wwmi -> {version}")

    if args.update_catalog:
        catalog_path = Path(args.update_catalog)
        for version, zip_path, digest, size_bytes, released_at in packaged:
            update_catalog(catalog_path, version, f"{base_url}{zip_path.name}", digest, size_bytes,
                           released_at)
        print(f"  ✓ 已把 {len(packaged)} 个版本并入 {catalog_path}")

    print()
    print("下一步：上传到 CDN（与 appsettings.json 的 ModEnv:ManifestUrl 同桶同前缀），顺序敏感 ——")
    print("        先传各 wwmi-<版本>.zip，再传 version.json，最后传 wwmi-versions.json。")
    print("        目录里引用的 zip 得先在线上，否则用户选中那一刻才开始 404；")
    print("        换掉同名 zip 却不更新引用它的清单，用户会卡在 SHA256 校验失败。")
    for version, _, _, _, _ in packaged:
        print(f"    {base_url}wwmi-{version}.zip")
    return 0


if __name__ == "__main__":
    sys.exit(main())
