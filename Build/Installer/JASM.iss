; JASM 安装包（Inno Setup 6.3+）
;
; 为什么要安装包：单 exe 版由 singlefilehost 把 ~250MB 依赖自解压到 %TEMP%\.net\... 再加载
; 原生 DLL —— 这正是 Defender 启发式（Wacatac / Bearfoos 一类）最集中的形态，加上无签名，
; 用户侧就是「下载即被拦」。安装包把同一份 folder 产物以普通文件落盘，首次启动不再有
; 自解压动作；再配合 Authenticode 签名，才能把误报压下去。
;
; 构建前提：先跑 `python Build/Release.py`（CI 上的 folder 构建），产出 output/JASM/。
; 编译（版本号必须显式传入）：
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" ^
;       /DMyAppVersion=2.36.2 /DMyAppVersion4=2.36.2.0 Build\Installer\JASM.iss
; 或直接：pwsh Build/PackInstaller.ps1
;
; 安装位置固定为「当前用户」%LOCALAPPDATA%\Programs\JASM，不要改成 Program Files：
; 应用内更新器（JASM - Auto Updater.exe）以普通权限运行，装在 Program Files 时替换文件会失败。

#define MyAppName "JASM - Just Another Skin Manager"
#define MyAppShortName "JASM"
#define MyAppPublisher "qsy123-coder"
#define MyAppURL "https://github.com/qsy123-coder/JASM"
#define MyAppExeName "JASM - Just Another Skin Manager.exe"

#ifndef MyAppVersion
  #error 必须通过 /DMyAppVersion=x.y.z 传入版本号（PackInstaller.ps1 会自动从 csproj 读取）
#endif
#ifndef MyAppVersion4
  ; VersionInfoVersion 要求四段数字；三段的 2.36.2 补成 2.36.2.0。
  #define MyAppVersion4 MyAppVersion + ".0"
#endif

[Setup]
; AppId 一旦发布就不能改：它决定「升级同一份安装」还是「并排装第二份」。
AppId={{7C2C3B4E-9E1B-4C62-9B1A-5D7E2F3A1C44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion4}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}

; 当前用户安装（免 UAC，且更新器能直接覆盖文件）。
DefaultDirName={localappdata}\Programs\{#MyAppShortName}
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

; 产物落在仓库根目录，与 Release.py 的 JASM_v<版本>.7z 放一起。
OutputDir=..\..
OutputBaseFilename=JASM_Setup_v{#MyAppVersion}
SetupIconFile=..\..\src\GIMI-ModManager.WinUI\Assets\WindowIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 升级时由 Restart Manager 关闭正在运行的 JASM；静默更新也走这条路径。
CloseApplications=yes
RestartApplications=no

; 安装包由 CI 在 ISCC 之后用 signtool 签名。若要让卸载器也带签名，配置 SignTool 后
; 把下面两行打开（见 Build/Sign.ps1 的说明）。
; SignedUninstaller=yes
; SignTool=trustedsigning

[Languages]
; 官方安装包只附带部分语言，ChineseSimplified.isl 不在其中（issrc 仓库里有，但不会随安装器分发）。
; 因此把它 vendor 到 Build/Installer/Languages/，路径相对本脚本目录。
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 原样安装 folder 产物：主程序、JASM - Auto Updater_New\、Elevator.exe、Assets、DLL。
Source: "..\..\output\JASM\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 不加 skipifsilent：应用内更新以 /VERYSILENT 静默运行时，装完要把 JASM 自动拉起来。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppShortName}}"; Flags: nowait

[UninstallDelete]
; 这些目录是运行期由程序/更新器改名或创建的，不在安装清单里，卸载时手动清掉。
; 用户数据（%LOCALAPPDATA%\JASM：助手、UHD 运行时、日志、配置）不在安装目录，保留。
Type: filesandordirs; Name: "{app}\JASM - Auto Updater"
Type: filesandordirs; Name: "{app}\JASM - Auto Updater_New"
