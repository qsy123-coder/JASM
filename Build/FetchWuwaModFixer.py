"""下载并校验浮窗修复引擎（wuwa-mod-cli + config.json）。

浮窗的「修复」页签内置上游 Wuwa_Mod_Fixer 的官方 CLI。CLI 与配置体积不大但不适合入库：
  - CLI 是 3.9 MB 的预编译 exe，版本固定、由本脚本按 SHA256 校验；
  - config.json 是上游每周都可能更新的数据，入库就会变成陈年快照。

脚本幂等：文件已存在且校验通过就直接跳过。CI / Release.py 在 publish 前调用一次即可。
代理走标准环境变量（HTTPS_PROXY），不额外读系统配置。
"""

import hashlib
import json
import os
import sys
import urllib.request
from pathlib import Path

# GitHub Actions 的 Windows 控制台是 GBK/cmd 代码页，直接 print 中文会抛
# UnicodeEncodeError（charmap），整个 Release.py 会因此中断。把标准输出钉成 UTF-8。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

CLI_VERSION = "3.7.0"
CLI_SHA256 = "737c71507a8bc43a0794cace0aba07bccee6967de95cfb29291c9700cc85a370"
CLI_URL = (
    "https://github.com/Moonholder/Wuwa_Mod_Fixer/releases/download/"
    f"v{CLI_VERSION}/wuwa-mod-cli_v{CLI_VERSION}.exe"
)

EXPECTED_CONFIG_VERSION = "3.7.1"
MIN_REQUIRED_CONFIG_VERSION = "3.7.0"
CONFIG_URLS = [
    "https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config.json",
    "https://raw.githubusercontent.com/Moonholder/Wuwa_Mod_Fixer/main/config.json",
]

REPO_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = REPO_ROOT / "src" / "GIMI-ModManager.WinUI" / "Assets" / "WuwaModFixer"
CLI_PATH = OUTPUT_DIR / "wuwa-mod-cli.exe"
CONFIG_PATH = OUTPUT_DIR / "config.json"

MAX_CLI_BYTES = 20 * 1024 * 1024
MAX_CONFIG_BYTES = 2 * 1024 * 1024
TIMEOUT_SECONDS = 180


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url: str, max_bytes: int) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "JASM-WuwaModFixer-Fetcher"})
    with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:
        data = response.read(max_bytes + 1)

    if len(data) > max_bytes:
        raise RuntimeError(f"下载内容超过上限 {max_bytes} 字节：{url}")

    return data


def atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".part")
    temp.write_bytes(data)
    os.replace(temp, path)


def fetch_cli() -> None:
    if CLI_PATH.is_file() and sha256_of(CLI_PATH) == CLI_SHA256:
        print(f"[WuwaModFixer] CLI 已就绪：{CLI_PATH}")
        return

    print(f"[WuwaModFixer] 下载 CLI v{CLI_VERSION}…")
    data = download(CLI_URL, MAX_CLI_BYTES)
    actual = hashlib.sha256(data).hexdigest()

    if actual != CLI_SHA256:
        raise RuntimeError(
            "CLI SHA256 不匹配：\n"
            f"  期望 {CLI_SHA256}\n  实际 {actual}\n"
            "上游可能重新上传了同名资产；确认无误后更新脚本里的固定值。"
        )

    atomic_write(CLI_PATH, data)
    print(f"[WuwaModFixer] CLI 已写入：{CLI_PATH}")


def validate_config(text: str) -> dict:
    try:
        config = json.loads(text)
    except json.JSONDecodeError as error:
        raise RuntimeError(f"config.json 不是合法 JSON：{error}") from error

    characters = config.get("characters")
    if not isinstance(characters, (dict, list)) or len(characters) == 0:
        raise RuntimeError("config.json 缺少 characters 角色表")

    version = config.get("version")
    if not isinstance(version, dict):
        raise RuntimeError("config.json 缺少 version 信息")

    current = str(version.get("current_version", "")).strip()
    minimum = str(version.get("min_required_version", "")).strip()
    if not current or not minimum:
        raise RuntimeError("config.json 的 current_version / min_required_version 为空")

    if current != EXPECTED_CONFIG_VERSION:
        print(
            f"[WuwaModFixer] 提示：上游配置版本变为 {current}（脚本期望 {EXPECTED_CONFIG_VERSION}），"
            "请复核后再更新 EXPECTED_CONFIG_VERSION"
        )

    if compare_versions(minimum, CLI_VERSION) > 0:
        raise RuntimeError(
            f"config.json 要求程序 {minimum}，高于内置 CLI v{CLI_VERSION}；请同步升级 CLI"
        )

    return config


def parse_version(text: str) -> tuple[int, int, int, int]:
    value = text.strip().lstrip("vV")
    parts = value.split(".")
    numbers = [0, 0, 0, 0]
    for index, part in enumerate(parts[:4]):
        digits = ""
        for char in part:
            if char.isdigit():
                digits += char
            else:
                break
        numbers[index] = int(digits) if digits else 0
    return tuple(numbers)


def compare_versions(left: str, right: str) -> int:
    left_version = parse_version(left)
    right_version = parse_version(right)
    return (left_version > right_version) - (left_version < right_version)


def fetch_config() -> None:
    if CONFIG_PATH.is_file():
        try:
            existing = validate_config(CONFIG_PATH.read_text(encoding="utf-8"))
            if str(existing["version"]["current_version"]).strip() == EXPECTED_CONFIG_VERSION:
                print(f"[WuwaModFixer] 配置已就绪：{CONFIG_PATH}")
                return
        except (RuntimeError, OSError):
            print("[WuwaModFixer] 本地配置无效，重新下载")

    errors = []
    for url in CONFIG_URLS:
        try:
            print(f"[WuwaModFixer] 下载配置：{url}")
            text = download(url, MAX_CONFIG_BYTES).decode("utf-8")
            validate_config(text)
            atomic_write(CONFIG_PATH, text.encode("utf-8"))
            print(f"[WuwaModFixer] 配置已写入：{CONFIG_PATH}")
            return
        except Exception as error:  # noqa: BLE001 - 需要继续尝试下一个源
            errors.append(f"{url}: {error}")

    raise RuntimeError("所有配置源都失败：\n" + "\n".join(errors))


def main() -> int:
    print(f"[WuwaModFixer] 输出目录：{OUTPUT_DIR}")
    fetch_cli()
    fetch_config()

    print(f"[WuwaModFixer] CLI SHA256：{sha256_of(CLI_PATH)}")
    print(f"[WuwaModFixer] 配置 SHA256：{sha256_of(CONFIG_PATH)}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001 - 脚本边界统一打印
        print(f"[WuwaModFixer] 失败：{error}", file=sys.stderr)
        sys.exit(1)
