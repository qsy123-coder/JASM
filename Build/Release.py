
import os
import shutil
import re
import struct
import sys


ELEVATOR_CSPROJ = "src/Elevator/Elevator.csproj"
ELEVATOR_OUTPUT_DIR = "src/Elevator/bin/Release/Publish"
ELEVATOR_OUTPUT_FILE = f"{ELEVATOR_OUTPUT_DIR}/Elevator.exe"

JASM_CSPROJ = "src/GIMI-ModManager.WinUI/GIMI-ModManager.WinUI.csproj"
JASM_OUTPUT = "src/GIMI-ModManager.WinUI/bin/Release/Publish/"

JASM_Updater_CSPROJ = "src/JASM.AutoUpdater/JASM.AutoUpdater.csproj"
JASM_Updater_OUTPUT = "src/JASM.AutoUpdater/bin/Release/Publish/"
JASM_Updater_FolderName = "JASM - Auto Updater_New"

RELEASE_DIR = "output"
JASM_RELEASE_DIR = "output/JASM"

WUWA_FIXER_ASSETS = [
    "src/GIMI-ModManager.WinUI/Assets/WuwaModFixer/wuwa-mod-cli.exe",
    "src/GIMI-ModManager.WinUI/Assets/WuwaModFixer/config.json",
]

SelfContained = sys.argv[1] == "SelfContained" if len(sys.argv) > 1 else False
ExcludeElevator = "ExcludeElevator" in sys.argv
SingleFile = "SingleFile" in sys.argv

def checkSuccessfulExitCode(exitCode: int) -> None:
    if exitCode != 0:
        print("Exit code: " + str(exitCode))
        exit(exitCode)

def extractVersionNumber() -> str:
    with open(JASM_CSPROJ, "r", encoding="utf-8") as jasmCSPROJ:
        for line in jasmCSPROJ:
            line = line.strip()
            if line.startswith("<VersionPrefix>"):
                return re.findall(r"\d+\.\d+\.\d+", line)

# 从 .NET 9 起 SDK 默认给 apphost / singlefilehost 打 /CETCOMPAT：PE 调试目录里多一条
# IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS，值为 1（CET_COMPAT）。未打全更新的 Win10
# （22H2 低于 1904x.5007）不支持 CET，CLR 启动时直接 fatal：
#   Fatal error. Your Windows doesn't fully support CET. Please install all available windows updates.
# 用户侧表现是「双击没反应」。工程里用 <CETCompat>false</CETCompat> 关掉；这里在打包前
# 逐个体检真正要发出去的 exe —— 本机跑的是 Win11，复现不了这个故障，只能靠标记本身把关，
# 防止换 SDK、加新 exe 工程时静默回归。
IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS = 20
CET_COMPAT = 0x1


def assertCetCompatDisabled(exePath: str) -> None:
    """产物 exe 的 PE 调试目录里不能带 CET_COMPAT 标记（见上面的说明）。"""
    with open(exePath, "rb") as exe:
        data = exe.read()

    def fail(reason: str) -> None:
        print("ERROR: " + reason + " (" + exePath + ")")
        exit(1)

    if data[:2] != b"MZ":
        fail("not a PE image")
    peOffset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[peOffset:peOffset + 4] != b"PE\0\0":
        fail("no PE signature")
    coffOffset = peOffset + 4
    numberOfSections = struct.unpack_from("<H", data, coffOffset + 2)[0]
    sizeOfOptionalHeader = struct.unpack_from("<H", data, coffOffset + 16)[0]
    optionalOffset = coffOffset + 20
    magic = struct.unpack_from("<H", data, optionalOffset)[0]
    if magic == 0x20B:      # PE32+
        dataDirectoriesOffset = optionalOffset + 112
    elif magic == 0x10B:    # PE32
        dataDirectoriesOffset = optionalOffset + 96
    else:
        fail("unknown optional header magic 0x%X" % magic)

    # 数据目录第 6 项是调试目录；目录里存的是 RVA，需要按节表换算成文件偏移。
    # 没有调试目录（RVA/大小为 0）时自然也不会有 CET 标记。
    debugDirectoryRva, debugDirectorySize = struct.unpack_from("<II", data, dataDirectoriesOffset + 6 * 8)
    if debugDirectoryRva == 0 or debugDirectorySize == 0:
        return
    sections = []
    sectionTableOffset = optionalOffset + sizeOfOptionalHeader
    for index in range(numberOfSections):
        entryOffset = sectionTableOffset + index * 40
        virtualSize, virtualAddress, rawSize, rawPointer = struct.unpack_from("<IIII", data, entryOffset + 8)
        sections.append((virtualAddress, max(virtualSize, rawSize), rawPointer))

    for virtualAddress, sectionSize, rawPointer in sections:
        if virtualAddress <= debugDirectoryRva < virtualAddress + sectionSize:
            debugDirectoryOffset = rawPointer + (debugDirectoryRva - virtualAddress)
            break
    else:
        fail("debug directory RVA 0x%X is not mapped by any section" % debugDirectoryRva)

    for index in range(debugDirectorySize // 28):
        entryOffset = debugDirectoryOffset + index * 28
        debugType = struct.unpack_from("<I", data, entryOffset + 12)[0]
        sizeOfData = struct.unpack_from("<I", data, entryOffset + 16)[0]
        pointerToRawData = struct.unpack_from("<I", data, entryOffset + 24)[0]
        if debugType == IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS and pointerToRawData and sizeOfData >= 4:
            if struct.unpack_from("<I", data, pointerToRawData)[0] & CET_COMPAT:
                fail("CET_COMPAT is set; add <CETCompat>false</CETCompat> to the project " +
                     "or unpatched Windows 10 cannot start it")

print("PostBuild.py")
print("PWD: " + os.getcwd())
print("SelfContained: " + str(SelfContained))
print("SingleFile: " + str(SingleFile))

versionNumber = extractVersionNumber()
if versionNumber is None or len(versionNumber) == 0:
    print("Failed to extract version number from " + JASM_CSPROJ)
    exit(1)
versionNumber = versionNumber[0]

# 浮窗修复引擎（CLI + config）必须嵌进主 exe。资源不入库，缺失时先取一次；
# 取不到就直接失败 —— 不能发一个「修复页签永远提示引擎不可用」的包。
missing_wuwa_fixer = [path for path in WUWA_FIXER_ASSETS
                      if not os.path.isfile(path) or os.path.getsize(path) == 0]
if missing_wuwa_fixer:
    print("Fetching Wuwa Mod Fixer assets...")
    checkSuccessfulExitCode(os.system(f'"{sys.executable}" Build/FetchWuwaModFixer.py'))
    missing_wuwa_fixer = [path for path in WUWA_FIXER_ASSETS
                          if not os.path.isfile(path) or os.path.getsize(path) == 0]
    if missing_wuwa_fixer:
        print("ERROR: missing Wuwa Mod Fixer assets: " + ", ".join(missing_wuwa_fixer))
        exit(1)
    print("Finished fetching Wuwa Mod Fixer assets")
    print()

if ExcludeElevator == False:
    print("Building Elevator...")
    elevatorPublishCommand = f'dotnet publish {ELEVATOR_CSPROJ} -o {ELEVATOR_OUTPUT_DIR} /p:PublishProfile=FolderProfile.pubxml -c Release'
    print(elevatorPublishCommand)
    checkSuccessfulExitCode(os.system(elevatorPublishCommand))
    print()
    print("Finished building Elevator")

    # 主程序把助手当内嵌资源打进 exe（见 GIMI-ModManager.WinUI.csproj 的 EmbeddedResource），
    # 所以这里没产出 = 打包出的包**天生没有助手**，而且从 exe 里也拿不回来 —— 必须当场失败，
    # 不能让它悄悄发出一个送不了按键的包。
    if not os.path.isfile(ELEVATOR_OUTPUT_FILE):
        print("ERROR: " + ELEVATOR_OUTPUT_FILE + " not found after building Elevator")
        exit(1)
else:
    print("Skipping Elevator (ExcludeElevator)：本次构建不含助手，主 exe 也不会内嵌它")
    print()

if SelfContained == False and SingleFile == False:
    print("Building JASM - Auto Updater...")
    jasmUpdaterPublishCommand = f'dotnet publish {JASM_Updater_CSPROJ} -o {JASM_Updater_OUTPUT} /p:PublishProfile=FolderProfile.pubxml -c Release'
    print(jasmUpdaterPublishCommand)
    checkSuccessfulExitCode(os.system(jasmUpdaterPublishCommand))
    assertCetCompatDisabled(os.path.join(JASM_Updater_OUTPUT, "JASM - Auto Updater.exe"))
    print()
    print("Finished building JASM - Auto Updater")
else:
    print("Skipping JASM - Auto Updater build because it is not supported for self-contained build")
    print()

print("Building JASM...")
if SingleFile:
    profile = "FolderProfileSingleFile.pubxml"
    jasmOutput = "src/GIMI-ModManager.WinUI/bin/Release/PublishSingleFile/"
    # 单文件模式必须显式指定 Platform=x64，否则 WinUI 运行时激活会失败(COMException 0x80040111)
    platformArgs = " -p:Platform=x64"
else:
    profile = "FolderProfileSelfContained.pubxml" if SelfContained else "FolderProfile.pubxml"
    jasmOutput = JASM_OUTPUT
    platformArgs = ""
jasmPublishCommand = f'dotnet publish {JASM_CSPROJ} -o {jasmOutput} /p:PublishProfile={profile} -c Release{platformArgs}'
print(jasmPublishCommand)
checkSuccessfulExitCode(os.system(jasmPublishCommand))
assertCetCompatDisabled(os.path.join(jasmOutput, "JASM - Just Another Skin Manager.exe"))
print()
print("Finished building JASM")

# 先清空上一次运行的残留再建目录。不清的话有两处会出错：
#   · 上一次 folder 运行留下的 output/JASM/Elevator.exe 会混进后面的单文件 zip（体积/内容都对不上，
#     自更新只挑 exe 所以无害，但会让人以为「单文件包里怎么有两个文件」）；
#   · 下面 JASM - Auto Updater 那步用的是 os.mkdir（不带 exist_ok），目录已存在会直接抛异常。
if os.path.isdir(RELEASE_DIR):
    print("Cleaning " + RELEASE_DIR)
    shutil.rmtree(RELEASE_DIR)

# Create release directory
os.makedirs(RELEASE_DIR, exist_ok=True)
os.makedirs(JASM_RELEASE_DIR, exist_ok=True)

if ExcludeElevator == False and SingleFile == False:
    print("Copying Elevator to JASM...")
    shutil.copy(ELEVATOR_OUTPUT_FILE, JASM_RELEASE_DIR)
    print()
    print("Finished copying Elevator to release directory")

if SingleFile:
    print("Copying JASM single exe to output...")
    shutil.copy(os.path.join(jasmOutput, "JASM - Just Another Skin Manager.exe"), JASM_RELEASE_DIR)
else:
    print("Copying JASM to output...")
    shutil.copytree(JASM_OUTPUT, JASM_RELEASE_DIR, dirs_exist_ok=True)
print()
print("Finished copying JASM to release directory")

if SelfContained == False and SingleFile == False:
    print("Copying JASM - Auto Updater to output...")
    updater_dest = os.path.join(JASM_RELEASE_DIR, JASM_Updater_FolderName)
    os.mkdir(updater_dest)
    shutil.copytree(JASM_Updater_OUTPUT, updater_dest, dirs_exist_ok=True)
    print()
    print("Finished copying JASM - Auto Updater to release directory")

print("Copying text files to RELEASE_DIR...")
shutil.copy("Build/README.txt", RELEASE_DIR)
shutil.copy("CHANGELOG.md", os.path.join(RELEASE_DIR, "CHANGELOG.txt"))

# 内置了上游 GPL-3.0 的修复引擎，发布目录里带上许可与第三方声明
for notice in ("LICENSE", "THIRD-PARTY-NOTICES"):
    if os.path.isfile(notice):
        shutil.copy(notice, RELEASE_DIR)

print("Finished copying text files to release directory")

print("Zipping release directory...")
releaseArchiveName = "JASM_v" + versionNumber + ".7z"
if SingleFile:
    # 单文件版产物改为 .zip：进程内自更新用 System.IO.Compression 解压,无需运行时 7z。
    # 与 CLAUDE.md 文档及 gh release upload 示例里的 SingleFile_JASM_vX.Y.Z.zip 命名对齐。
    releaseArchiveName = "SingleFile_JASM_v" + versionNumber + ".zip"
elif SelfContained:
    releaseArchiveName = "SelfContained_" + releaseArchiveName

# 同名旧包必须先删：7z a 是「追加/更新」而不是重建，留着旧包会把上一版的内容一起带进去
if os.path.exists(releaseArchiveName):
    print("Removing stale archive " + releaseArchiveName)
    os.remove(releaseArchiveName)

checkSuccessfulExitCode(os.system(f'7z a -mx4 {releaseArchiveName} ./{RELEASE_DIR}/*'))
print()
print("Finished zipping release directory")

env_file = os.getenv('GITHUB_ENV')
if env_file is None:
    exit(1)

with open(env_file, "a") as myfile:
    myfile.write(f"zipFile={releaseArchiveName}")

checkSuccessfulExitCode(os.system(f'7z h -scrcsha256 ./{releaseArchiveName}'))

exit(0)
