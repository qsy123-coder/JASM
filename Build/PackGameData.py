"""
JASM 游戏数据打包脚本
用法: python Build/PackGameData.py <游戏名> <版本号>

示例: python Build/PackGameData.py WuWa v2.23.0
输出: output/WuWa-data-v2.23.0.zip
"""
import sys
import shutil
from pathlib import Path

GAME = sys.argv[1] if len(sys.argv) > 1 else "WuWa"
VERSION = sys.argv[2] if len(sys.argv) > 2 else "v0.0.0"

ASSETS_DIR = Path("src/GIMI-ModManager.WinUI/Assets/Games") / GAME
OUTPUT_DIR = Path("output")
OUTPUT_DIR.mkdir(exist_ok=True)

ZIP_NAME = f"{GAME}-data-{VERSION}"
ZIP_PATH = OUTPUT_DIR / f"{ZIP_NAME}.zip"
TEMP_DIR = OUTPUT_DIR / ZIP_NAME

# 清理旧文件
if TEMP_DIR.exists():
    shutil.rmtree(TEMP_DIR)
if ZIP_PATH.exists():
    ZIP_PATH.unlink()

# 复制数据到临时目录（排除 .backup 目录）
# 注意: game.json 是应用的静态配置（游戏名/图标/模型导入器/ModEnv 元数据），由 exe 自带，
# 一旦打进数据包，游戏数据同步会用旧 zip 把它覆盖成旧版，导致「一键配置 Mod 环境」按钮消失。
# `.dataversion` 由同步服务自身写入，也不应打包进去。
print(f"打包 {GAME} 数据...")
shutil.copytree(ASSETS_DIR, TEMP_DIR, ignore=shutil.ignore_patterns(".backup*", "*.backup*", "game.json", ".dataversion"))

# 打包为 ZIP
shutil.make_archive(str(ZIP_PATH.with_suffix("")), "zip", TEMP_DIR)

# 清理临时目录
shutil.rmtree(TEMP_DIR)

size_mb = ZIP_PATH.stat().st_size / 1024 / 1024
print(f"✅ 完成: {ZIP_PATH} ({size_mb:.1f} MB)")
print(f"")
print(f"下一步:")
print(f"  1. 打开 https://github.com/Jorixon/JASM/releases")
print(f"  2. 创建新 Release 或编辑已有的 Draft Release")
print(f"  3. 把 {ZIP_NAME}.zip 拖入 Attach binaries 区域")
print(f"  4. 发布 Release")
