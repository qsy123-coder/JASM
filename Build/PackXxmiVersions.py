"""把本地「XXMI vX.Y.Z更新包」打成可上传 CDN 的版本包，并生成版本清单 catalog。

背景
----
JASM 的「一键配置 Mod 环境」只认 CDN 上一个固定的 xxmi 版本。为了让用户能选择并回退
XXMI 版本，需要把每个历史版本的核心文件单独打成 `xxmi-<版本>.zip` 上传 COS，再用
`xxmi-versions.json` 把可选版本列出来。本脚本负责前半段。

打包范围（白名单，非常重要）
---------------------------
XXMI 更新包里 `Packages/XXMI/` 的三个 dll 是四个版本之间**唯一**的实质差异：

    3dmloader.dll / d3d11.dll / d3dcompiler_47.dll

其余内容一律**不打包**，尤其是：

  * `Security/private_key.der` —— XXMI 的签名**私钥**，外发即泄密
    （同样警告见 docs/mod-env-cdn-setup.md）
  * `Packages/Launcher/TMP/*.msi` —— 93MB 的旧版联网引导器，四个版本完全相同
  * `Packages/EFMI`、`Packages/WWMI` —— 只有 Manifest.json，没有实际内容

脚本用白名单而非黑名单，并在打包后重新打开 zip 断言内容与白名单**完全相等**，
确保私钥不可能因为源目录结构变化而被带出去。

用法
----
    python Build/PackXxmiVersions.py

    # 指定源目录 / 输出目录 / CDN 前缀
    python Build/PackXxmiVersions.py \\
        --source "D:/MC-MOD整合包/XXMI更新包（持续更新）" \\
        --out Build/out/xxmi-versions \\
        --base-url https://<bucket>.cos.ap-guangzhou.myqcloud.com/modenv/

产物
----
    <out>/xxmi-<版本>.zip          每个版本一个包（内含 3 个 dll，平铺在根）
    <out>/xxmi-versions.json       版本清单，可直接上传 CDN
    <out>/xxmi-version-hashes.json 逐文件哈希表，供手测核对「当前装的是哪个版本」

打包完成后还需要手工上传到 COS，命令见脚本末尾输出。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

# 默认源目录：本地「XXMI更新包（持续更新）」。
# 注意用原始字符串 —— 路径里的 \B \M \X 会被 Python 当成非法转义并报 SyntaxWarning。
DEFAULT_SOURCE = r"D:\BaiduNetdiskDownload\MC-MOD整合包\XXMI更新包（持续更新）"

# 默认与 appsettings.json 的 ModEnv:ManifestUrl 同桶同前缀，改桶时两处一起改。
DEFAULT_BASE_URL = "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/modenv/"

DEFAULT_OUT = "Build/out/xxmi-versions"

# 白名单：只有这三个文件会被打进 zip，且必须全部存在。
ALLOWED_FILES = ("3dmloader.dll", "d3d11.dll", "d3dcompiler_47.dll")

# 源包内 XXMI 核心包的相对路径与版本来源。
PACKAGE_SUBDIR = "Packages/XXMI"
MANIFEST_NAME = "Manifest.json"

# 逐文件哈希表里用来代表「这个版本发布日期」的文件。
DATE_REFERENCE_FILE = "d3d11.dll"

CATALOG_NAME = "xxmi-versions.json"
HASH_REPORT_NAME = "xxmi-version-hashes.json"

# 版本号必须形如 1.2.3，否则拒绝打包（避免把目录名里的日期误当版本号）。
VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+$")

# 目录名兜底解析：`XXMI v1.1.7更新包` -> `1.1.7`
DIRNAME_VERSION_PATTERN = re.compile(r"v(\d+\.\d+\.\d+)")


def sha256_of(path: Path) -> str:
    """返回文件 SHA256 的小写十六进制（与 version.json / JASM 校验用的格式一致）。"""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_package_version(package_dir: Path) -> tuple[str, str]:
    """从 `Packages/XXMI/Manifest.json` 读版本号。

    优先用 Manifest.json 的 version 字段（权威来源），读不到再退回解析目录名。
    返回 (版本号, 版本号来源说明)。
    """
    manifest_path = package_dir / MANIFEST_NAME
    if manifest_path.is_file():
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
            version = str(manifest.get("version", "")).strip()
            if VERSION_PATTERN.match(version):
                return version, "Manifest.json"
        except (json.JSONDecodeError, OSError) as ex:
            print(f"    ! 读取 {manifest_path} 失败: {ex}")

    match = DIRNAME_VERSION_PATTERN.search(package_dir.parent.parent.name)
    if match:
        return match.group(1), "目录名"

    raise ValueError(f"无法确定版本号（{manifest_path} 不可用，目录名也解析不出）")


def version_sort_key(version: str) -> tuple[int, int, int]:
    return tuple(int(part) for part in version.split("."))  # type: ignore[return-value]


def discover_packages(source: Path) -> list[tuple[str, Path, str]]:
    """扫描源目录，返回 [(版本号, XXMI 核心包目录, 版本号来源)]，按版本降序。"""
    if not source.is_dir():
        raise SystemExit(f"源目录不存在: {source}")

    found: list[tuple[str, Path, str]] = []
    for child in sorted(source.iterdir()):
        package_dir = child / PACKAGE_SUBDIR
        if not package_dir.is_dir():
            continue
        try:
            version, origin = read_package_version(package_dir)
        except ValueError as ex:
            print(f"  - 跳过 {child.name}: {ex}")
            continue
        found.append((version, package_dir, origin))

    if not found:
        raise SystemExit(f"在 {source} 下没找到任何 {PACKAGE_SUBDIR} 目录")

    # 同版本号去重，保留先扫到的那个。
    unique: dict[str, tuple[str, Path, str]] = {}
    for version, package_dir, origin in found:
        if version in unique:
            print(f"  ! 版本 {version} 重复，已忽略 {package_dir}")
            continue
        unique[version] = (version, package_dir, origin)

    return sorted(unique.values(), key=lambda item: version_sort_key(item[0]), reverse=True)


def assert_whitelist_only(zip_path: Path) -> None:
    """打包后复查 zip 内容 —— 白名单之外多一个文件就报错退出。

    这是防止 private_key.der 等敏感文件外发的最后一道闸门，
    所以这里刻意用「与白名单完全相等」而不是「是白名单子集」。
    """
    with zipfile.ZipFile(zip_path) as archive:
        names = set(archive.namelist())
    expected = set(ALLOWED_FILES)
    if names != expected:
        zip_path.unlink(missing_ok=True)
        raise SystemExit(
            f"打包校验失败：{zip_path.name} 内容为 {sorted(names)}，应为 {sorted(expected)}。"
            "已删除该包，请检查源目录是否被改动。"
        )


def pack_version(version: str, package_dir: Path, out_dir: Path) -> dict:
    """打一个版本的包，返回 catalog 条目 + 逐文件哈希。"""
    missing = [name for name in ALLOWED_FILES if not (package_dir / name).is_file()]
    if missing:
        raise SystemExit(f"版本 {version} 缺少文件 {missing}（目录 {package_dir}）")

    zip_path = out_dir / f"xxmi-{version}.zip"
    # 平铺写入：zip 根目录直接就是 3 个 dll，对应 JASM 装到 XXMI 根目录的结构。
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in ALLOWED_FILES:
            archive.write(package_dir / name, arcname=name)

    assert_whitelist_only(zip_path)

    file_hashes = {name: sha256_of(package_dir / name) for name in ALLOWED_FILES}
    released_at = datetime.fromtimestamp(
        (package_dir / DATE_REFERENCE_FILE).stat().st_mtime, tz=timezone.utc
    ).strftime("%Y-%m-%d")

    size_bytes = zip_path.stat().st_size
    print(
        f"  ✓ xxmi-{version}.zip  "
        f"{size_bytes / 1024 / 1024:.2f} MB  "
        f"sha256={sha256_of(zip_path)[:16]}…  ({released_at})"
    )

    return {
        "entry": {
            "Version": version,
            "DownloadUrl": "",  # 由调用方填入 base-url
            "Sha256": sha256_of(zip_path),
            "SizeBytes": size_bytes,
            "ReleasedAt": released_at,
            "Notes": "",
        },
        "file_hashes": file_hashes,
    }


def write_json(path: Path, payload: dict) -> None:
    """统一按 UTF-8 无 BOM + 2 空格缩进写出（CDN 上的 JSON 清单不要带 BOM）。"""
    path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="把本地 XXMI 更新包打成 CDN 版本包并生成版本清单",
    )
    parser.add_argument("--source", default=DEFAULT_SOURCE, help="本地「XXMI更新包（持续更新）」目录")
    parser.add_argument("--out", default=DEFAULT_OUT, help="产物输出目录")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="CDN 上 modenv/ 目录的 URL 前缀")
    args = parser.parse_args()

    source = Path(args.source)
    out_dir = Path(args.out)
    base_url = args.base_url if args.base_url.endswith("/") else args.base_url + "/"

    print("PackXxmiVersions.py")
    print(f"源目录 : {source}")
    print(f"输出到 : {out_dir}")
    print(f"CDN前缀: {base_url}")
    print()

    out_dir.mkdir(parents=True, exist_ok=True)

    packages = discover_packages(source)
    print(f"发现 {len(packages)} 个版本，开始打包：")

    catalog_entries: list[dict] = []
    hash_report: dict[str, dict] = {}
    for version, package_dir, origin in packages:
        print(f"  [{version}] 版本号来自 {origin}")
        result = pack_version(version, package_dir, out_dir)
        result["entry"]["DownloadUrl"] = f"{base_url}xxmi-{version}.zip"
        catalog_entries.append(result["entry"])
        hash_report[version] = result["file_hashes"]

    catalog_path = out_dir / CATALOG_NAME
    write_json(catalog_path, {"CatalogVersion": 1, "Versions": catalog_entries})

    hash_path = out_dir / HASH_REPORT_NAME
    write_json(hash_path, hash_report)

    print()
    print(f"已生成 {catalog_path}")
    print(f"已生成 {hash_path}（手测核对当前版本用）")
    print()
    print("下一步：把 out 目录下的 zip 和 xxmi-versions.json 上传到 CDN，然后填好 Notes 再传一次。")
    print("上传示例（腾讯云 COS，用 COSBrowser 拖拽亦可）：")
    print(f"    # 目标前缀与 appsettings.json 的 ModEnv:ManifestUrl 一致：{base_url}")
    for entry in catalog_entries:
        print(f"    - xxmi-{entry['Version']}.zip")

    return 0


if __name__ == "__main__":
    sys.exit(main())
