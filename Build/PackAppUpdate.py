"""把 Release.py 打好的更新包整理成 COS 上的更新清单 `app/update.json`。

背景
----
JASM 的应用更新原先完全靠 GitHub Releases：包挂在那里，客户端顺带从 releases API 的
`tag_name` / `assets` 里读出「最新版是哪个、包在哪」。搬到国内可直连的 COS 之后，对象存储只提供
文件本体、不提供任何元数据，那份元数据就得自己带 —— 就是本脚本维护的 `app/update.json`。

清单与包放同一个桶的同一个前缀下，客户端（主程序与独立更新器）先读清单，读不到再回退 GitHub API。

清单 schema（字段名大小写不敏感，客户端按 camelCase 读）
--------------------------------------------------------
    {
      "schemaVersion": 1,
      "releases": [
        {
          "version": "2.31.0",                 # 纯数字点分，不带 v 前缀（客户端用 Version.TryParse 解析）
          "prerelease": false,                 # true 时普通用户不会被提示更新
          "publishedAt": "2026-09-30T08:00:00Z",
          "notesUrl": "https://github.com/qsy123-coder/JASM/releases/tag/v2.31.0",
          "assets": [
            { "name": "JASM_v2.31.0.7z", "kind": "folder",
              "url": "https://<桶域名>/app/JASM_v2.31.0.7z",
              "sizeBytes": 148726913, "sha256": "……" },
            { "name": "SingleFile_JASM_v2.31.0.zip", "kind": "singleFile",
              "url": "https://<桶域名>/app/SingleFile_JASM_v2.31.0.zip",
              "sizeBytes": 96384012, "sha256": "……" }
          ]
        }
      ]
    }

  * `kind` 是**显式**判别：`folder`（.7z，含独立更新器；由 `JASM - Auto Updater.exe` 下载）
    与 `singleFile`（.zip，单 exe；由主程序自己进程内下载替换）。缺字段时客户端退回按文件名前缀
    （`JASM_` / `SingleFile_JASM_`）匹配，所以文件名仍然是承重的，脚本会断言。
  * `sizeBytes` / `sha256` 给客户端的进度总量与完整性校验用；`sha256` 缺省则跳过校验。

用法
----
    # 两个包一起发（最常见）：包就是 Release.py 产在仓库根目录的那两个
    python Build/PackAppUpdate.py --version 2.31.0 \\
        --folder-package JASM_v2.31.0.7z \\
        --single-file-package SingleFile_JASM_v2.31.0.zip

    # 只补一个形态（例如单文件包由 CI 产出得晚一些），与已有清单合并
    python Build/PackAppUpdate.py --version 2.31.0 \\
        --single-file-package SingleFile_JASM_v2.31.0.zip --manifest update.json

    # 预发布版本：普通用户不会被提示，仅入档
    python Build/PackAppUpdate.py --version 2.32.0 --folder-package ... --prerelease

⚠️ **增量合并的正确姿势**：`--manifest` 指向「线上那份 update.json 的本地副本」。脚本只把本次这一个
版本并进去，并保留更早的版本记录；如果你从零开始生成一份，线上清单里的历史版本就都没了 ——
而客户端只会看到「最新版」，看起来什么都不会发生，直到有人想回退到旧版才发现记录没了。

产物
----
    Build/out/app/update.json     可直接上传 COS 的清单

⚠️ **GitHub release 上的同版本资产仍然要挂**：≤2.30.0 的老客户端、以及用户机器上那份旧的
`JASM - Auto Updater.exe`，都只会读 GitHub Releases API。COS 出问题要回滚时也是靠它们兜底。
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

# 默认与 appsettings.json 的 AppUpdate:ManifestUrl 同桶同前缀，改桶时两处一起改。
DEFAULT_BASE_URL = "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/app/"

DEFAULT_OUT = "Build/out/app"
DEFAULT_MANIFEST = "Build/out/app/update.json"

MANIFEST_NAME = "update.json"

KIND_FOLDER = "folder"
KIND_SINGLE_FILE = "singleFile"

# 文件名前缀：除了给人看，还是客户端在 kind 缺失时的回退匹配依据（GitHub 回退通道就是按前缀反推
# kind 的），所以文件名不能随便改。
FOLDER_PREFIX = "JASM_"
SINGLE_FILE_PREFIX = "SingleFile_JASM_"

# 单文件包里必须能找到的那个 exe（对应 SingleFileSelfUpdater.ExtractExe 的查找条件）。
EXE_NAME = "JASM - Just Another Skin Manager.exe"

SCHEMA_VERSION = 1

# 清单里保留多少个历史版本，默认与 ModEnv 的 KeepBackupCount 对齐。
DEFAULT_KEEP = 5

DEFAULT_RELEASES_PAGE = "https://github.com/qsy123-coder/JASM/releases"

# 版本号必须是纯数字点分（2.31 / 2.31.0 / 2.31.0.1）。带 v 前缀或后缀的写法客户端解析不出来，
# 只会被静默跳过 —— 表现为「明明发了新版，用户端却不提示」，很难回查，所以这里直接拦掉。
VERSION_PATTERN = re.compile(r"^\d+(?:\.\d+){1,3}$")


def sha256_of(path: Path) -> str:
    """返回文件 SHA256 的小写十六进制（与客户端校验用的格式一致）。"""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def version_sort_key(version: str) -> tuple[int, int, int, int]:
    """把版本号变成可比较的元组。解析不出来的排到最后（0,0,0,0），不会让整份清单排序失败。"""
    if not VERSION_PATTERN.match(version):
        return (0, 0, 0, 0)
    parts = [int(part) for part in version.split(".")]
    while len(parts) < 4:
        parts.append(0)
    return (parts[0], parts[1], parts[2], parts[3])


def assert_asset_name(path: Path, prefix: str, extension: str, version: str) -> None:
    """断言包名恰好是 `<前缀>v<版本><扩展名>`。

    这个名字是承重的：客户端在清单缺 `kind`、以及走 GitHub 回退通道时，都按前缀判断包的类型；
    独立更新器则只认 `JASM_` 开头的资产。名字对不上不会立刻报错，而是变成「用户点了更新没反应」。
    """
    expected = f"{prefix}v{version}{extension}"
    if path.name != expected:
        raise SystemExit(
            f"包名不符合约定：{path.name}，应为 {expected}。\n"
            f"  文件名前缀（{prefix}）是客户端判别包类型的依据之一（清单缺 kind 时、以及 GitHub 回退"
            f"通道上就只看前缀），请用 Release.py 产出后原样使用，不要改名。"
        )


def assert_single_file_zip(path: Path) -> None:
    """断言单文件包解得出那个 exe。

    主程序的进程内自更新最后一步就是从这个 zip 里解出 exe，包里没有它的话用户会走到最后一步才失败
    （那时已经开始退出程序了），所以在发布前先在这里拦掉。.7z 由 7-Zip 读，Python 标准库读不了，
    因此这里只校验 .zip —— 而它恰好是自更新唯一消费的那种。
    """
    if not zipfile.is_zipfile(path):
        raise SystemExit(f"{path.name} 不是可读的 zip（主程序的进程内自更新只用 BCL 解压 .zip）。")

    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()

    if not any(Path(name).name == EXE_NAME for name in names):
        raise SystemExit(
            f"{path.name} 里没有 {EXE_NAME}（条目：{', '.join(names) or '空'}）。\n"
            f"  确认这确实是 SingleFile 模式的产物（7z a ... ./output/*，其中 output/JASM/ 下有那个 exe）。"
        )


def build_asset(path: Path, kind: str, prefix: str, extension: str, version: str, base_url: str) -> dict:
    """算出清单里的一条资产记录（名字 / 类型 / 直链 / 体积 / 哈希）。"""
    if not path.is_file():
        raise SystemExit(f"包不存在: {path}")

    assert_asset_name(path, prefix, extension, version)
    if kind == KIND_SINGLE_FILE:
        assert_single_file_zip(path)

    size_bytes = path.stat().st_size
    if size_bytes <= 0:
        raise SystemExit(f"{path.name} 是空文件，不要把它发出去。")

    digest = sha256_of(path)
    print(f"  ✓ {path.name}  {size_bytes / 1024 / 1024:.2f} MB  sha256={digest[:16]}…")

    return {
        "name": path.name,
        "kind": kind,
        "url": f"{base_url}{path.name}",
        "sizeBytes": size_bytes,
        "sha256": digest,
    }


def load_manifest(path: Path) -> dict:
    """读入已有的清单用于增量合并。

    文件不存在 = 从零开始（首次发布）。文件存在但不是清单结构（例如误指到别的 json）时直接退出 ——
    宁可让维护者看一眼，也不要静默把它覆盖掉。
    """
    if not path.is_file():
        print(f"  未找到已有清单 {path}，将从零生成（首次发布时属正常）")
        return {"schemaVersion": SCHEMA_VERSION, "releases": []}

    try:
        # utf-8-sig：容忍被人用带 BOM 的编辑器改过。CDN 上的清单本身必须无 BOM（见 write_json）。
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError) as ex:
        raise SystemExit(f"已有清单 {path} 无法解析: {ex}") from ex

    if not isinstance(payload, dict) or not isinstance(payload.get("releases"), list):
        raise SystemExit(f"{path} 不是一份更新清单（缺 releases 数组），请确认 --manifest 指向的是它。")

    print(f"  读入已有清单 {path}，现有 {len(payload['releases'])} 个版本")
    return payload


def merge_release(releases: list, new_release: dict) -> str:
    """把本次的版本并进清单：同版本按 kind 覆盖资产，新版本直接加进去。

    同版本合并是为了支持「先发 folder 包、晚点补单文件包」这种分批发布：本次没提供的那些资产
    保持原样，不会被抹掉。
    """
    version = new_release["version"]
    existing = next((r for r in releases if isinstance(r, dict) and str(r.get("version", "")).strip() == version), None)

    if existing is None:
        releases.append(new_release)
        return "新增版本"

    provided_kinds = {asset["kind"] for asset in new_release["assets"]}
    kept = [asset for asset in existing.get("assets", []) if asset.get("kind") not in provided_kinds]
    existing["assets"] = kept + new_release["assets"]
    # 说明性字段（publishedAt / notesUrl / prerelease）以本次命令行为准 —— 维护者可以借此纠正上一次的笔误。
    for key, value in new_release.items():
        if key != "assets":
            existing[key] = value
    return f"合并进已有版本（保留 {len(kept)} 个本次未提供的资产）"


def write_json(path: Path, payload: dict) -> None:
    """统一按 UTF-8 无 BOM + 2 空格缩进写出（CDN 上的 JSON 清单不要带 BOM）。"""
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="生成 / 增量更新 COS 上的 JASM 更新清单 app/update.json",
    )
    parser.add_argument("--version", required=True, help="本次发布的版本号，纯数字点分（如 2.31.0）")
    parser.add_argument("--folder-package", type=Path, default=None,
                        help="folder 版包（Release.py 产出的 JASM_v<版本>.7z）")
    parser.add_argument("--single-file-package", type=Path, default=None,
                        help="单文件版包（SingleFile_JASM_v<版本>.zip，只能由 CI 产出）")
    parser.add_argument("--out", default=DEFAULT_OUT, help="产物输出目录")
    parser.add_argument("--manifest", type=Path, default=Path(DEFAULT_MANIFEST),
                        help="已有清单（线上那份的本地副本），用于增量合并")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="COS 上 app/ 目录的 URL 前缀")
    parser.add_argument("--notes-url", default=None,
                        help="本次更新的说明链接，默认指向 GitHub 上同 tag 的 release 页")
    parser.add_argument("--no-notes-url", action="store_true",
                        help="清单里不写说明链接（客户端退回仓库 releases 页）。"
                             "PowerShell 5.1 传不了空字符串参数，要清空就用这个开关")
    parser.add_argument("--published-at", default=None,
                        help="发布时间（ISO 8601，如 2026-09-30T08:00:00Z），默认取当前时间")
    parser.add_argument("--prerelease", action="store_true",
                        help="标为预发布：普通用户不会被提示更新，仅入档")
    parser.add_argument("--keep", type=int, default=DEFAULT_KEEP, help="清单里保留多少个历史版本")
    args = parser.parse_args()

    version = args.version.strip()
    if not VERSION_PATTERN.match(version):
        raise SystemExit(
            f"版本号 {args.version!r} 不是纯数字点分（如 2.31.0）。\n"
            f"  带 v 前缀或后缀的写法客户端解析不出来，只会被静默跳过（现象是「发了新版用户端不提示」）。"
        )

    if args.folder_package is None and args.single_file_package is None:
        raise SystemExit("至少要给一个包：--folder-package 或 --single-file-package。")

    base_url = args.base_url if args.base_url.endswith("/") else args.base_url + "/"

    print("PackAppUpdate.py")
    print(f"版本   : {version}{'（预发布）' if args.prerelease else ''}")
    print(f"输出到 : {args.out}")
    print(f"COS前缀: {base_url}")
    print()

    print("校验并计算资产：")
    assets: list[dict] = []
    if args.folder_package is not None:
        assets.append(build_asset(args.folder_package, KIND_FOLDER, FOLDER_PREFIX, ".7z", version, base_url))
    if args.single_file_package is not None:
        assets.append(build_asset(args.single_file_package, KIND_SINGLE_FILE, SINGLE_FILE_PREFIX, ".zip", version,
                                  base_url))
    print()

    manifest = load_manifest(args.manifest)

    published_at = args.published_at or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    # notesUrl 写成 null 而不是省略这个键：同版本合并时"省略"会保留上一次的值，写 null 才能表达
    # 「这次要清掉它」。客户端把 null / 空白都当成"没有"，退回仓库 releases 页。
    if args.no_notes_url:
        notes_url = None
    elif args.notes_url is not None:
        notes_url = args.notes_url.strip() or None
    else:
        notes_url = f"{DEFAULT_RELEASES_PAGE}/tag/v{version}"

    new_release = {
        "version": version,
        "prerelease": bool(args.prerelease),
        "publishedAt": published_at,
        "notesUrl": notes_url,
        "assets": assets,
    }

    action = merge_release(manifest["releases"], new_release)
    manifest["schemaVersion"] = SCHEMA_VERSION

    # 按版本降序排列（客户端取的是「最大非预发布版」，顺序只是给人看；但保持有序便于人工核对）。
    manifest["releases"].sort(key=lambda r: version_sort_key(str(r.get("version", "")).strip()), reverse=True)

    keep = max(1, args.keep)
    dropped = manifest["releases"][keep:]
    if dropped:
        print(f"  清单超过 {keep} 个版本，丢弃最旧的 {len(dropped)} 个："
              f"{', '.join(str(r.get('version')) for r in dropped)}")
    manifest["releases"] = manifest["releases"][:keep]

    # 顺手体检一遍留下的条目：坏数据在客户端只会被静默跳过，在这里吼一声更早发现。
    for release in manifest["releases"]:
        entry_version = str(release.get("version", "")).strip()
        if not VERSION_PATTERN.match(entry_version):
            print(f"  ! 清单里 {entry_version!r} 不是纯数字点分，客户端会跳过这条")
        for asset in release.get("assets", []):
            if not str(asset.get("url", "")).startswith(("http://", "https://")):
                print(f"  ! {entry_version} 的资产 {asset.get('name')!r} 没有可用的绝对 URL，客户端会跳过")

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = out_dir / MANIFEST_NAME
    write_json(manifest_path, manifest)

    print()
    print(f"本次 {action}：{version}")
    print(f"已生成 {manifest_path}（其中 {len(manifest['releases'])} 个版本："
          f"{', '.join(str(r.get('version')) for r in manifest['releases'])}）")
    print()
    print("下一步：")
    print(f" 1) 把 {manifest_path} 与包上传到 COS 的 app/ 前缀（COSBrowser 拖拽或控制台均可）：{base_url}")
    for asset in assets:
        print(f"      - {asset['name']}")
    print(f"    传完用浏览器打开 {base_url}{MANIFEST_NAME} 确认返回的是 JSON、而不是被 COS 包了一层的 XML。")
    print()
    print(" 2) ⚠️ GitHub release 上仍要挂同一版本的同名资产 —— ≤2.30.0 的老客户端和用户机上那份旧的")
    print("    JASM - Auto Updater.exe 只会读 GitHub Releases API；COS 清单拉不到时（欠费/权限/被刷）")
    print("    也是靠它回退。清单拉不到时把两个包重新挂回 release 即可让所有已发布客户端自动走回退。")
    release_args = " ".join(asset["name"] for asset in assets)
    print(f"      gh release upload v{version} {release_args} --repo qsy123-coder/JASM --clobber")
    print()
    print(" 3) 清单是「先下载线上那份、再本地增量合并」的用法：加下一个版本前，先把 COS 上的")
    print(f"    {MANIFEST_NAME} 存到 --manifest 指向的位置，否则本地这份会丢掉更早的版本记录。")

    return 0


if __name__ == "__main__":
    sys.exit(main())
