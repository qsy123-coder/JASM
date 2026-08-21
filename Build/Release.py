
import os
import shutil
import re
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

print("PostBuild.py")
print("PWD: " + os.getcwd())
print("SelfContained: " + str(SelfContained))
print("SingleFile: " + str(SingleFile))

versionNumber = extractVersionNumber()
if versionNumber is None or len(versionNumber) == 0:
    print("Failed to extract version number from " + JASM_CSPROJ)
    exit(1)
versionNumber = versionNumber[0]

if ExcludeElevator == False and SingleFile == False:
    print("Building Elevator...")
    elevatorPublishCommand = f'dotnet publish {ELEVATOR_CSPROJ} -o {ELEVATOR_OUTPUT_DIR} /p:PublishProfile=FolderProfile.pubxml -c Release'
    print(elevatorPublishCommand)
    checkSuccessfulExitCode(os.system(elevatorPublishCommand))
    print()
    print("Finished building Elevator")
else:
    print("Skipping Elevator")
    print()

if SelfContained == False and SingleFile == False:
    print("Building JASM - Auto Updater...")
    jasmUpdaterPublishCommand = f'dotnet publish {JASM_Updater_CSPROJ} -o {JASM_Updater_OUTPUT} /p:PublishProfile=FolderProfile.pubxml -c Release'
    print(jasmUpdaterPublishCommand)
    checkSuccessfulExitCode(os.system(jasmUpdaterPublishCommand))
    print()
    print("Finished building JASM - Auto Updater")
else:
    print("Skipping JASM - Auto Updater build because it is not supported for self-contained build")
    print()

print("Building JASM...")
if SingleFile:
    profile = "FolderProfileSingleFile.pubxml"
    jasmOutput = "src/GIMI-ModManager.WinUI/bin/Release/PublishSingleFile/"
else:
    profile = "FolderProfileSelfContained.pubxml" if SelfContained else "FolderProfile.pubxml"
    jasmOutput = JASM_OUTPUT
jasmPublishCommand = f'dotnet publish {JASM_CSPROJ} -o {jasmOutput} /p:PublishProfile={profile} -c Release'
print(jasmPublishCommand)
checkSuccessfulExitCode(os.system(jasmPublishCommand))
print()
print("Finished building JASM")

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

print("Finished copying text files to release directory")

print("Zipping release directory...")
releaseArchiveName = "JASM_v" + versionNumber + ".7z"
if SingleFile:
    releaseArchiveName = "SingleFile_" + releaseArchiveName
elif SelfContained:
    releaseArchiveName = "SelfContained_" + releaseArchiveName

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
