# 从 Release.py 产出的 output/JASM 生成安装包 JASM_Setup_v<版本>.exe。
#
# 用法：
#   pwsh Build/PackInstaller.ps1                 # 版本号自动从 csproj 的 VersionPrefix 读
#   pwsh Build/PackInstaller.ps1 -Version 2.36.2
#   pwsh Build/PackInstaller.ps1 -Sign           # 打包前后调用 Build/Sign.ps1（有证书时才真正签）
#
# 前提：先跑过 `python Build/Release.py`（CI 的 folder 构建），output/JASM 必须存在且完整。
# 本机没有 Inno Setup 时脚本会给出安装命令（winget install JRSoftware.InnoSetup）。

[CmdletBinding()]
param(
    [string]$Version,
    [string]$RepoRoot,
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'

$repo = if ($RepoRoot) {
    (Resolve-Path $RepoRoot).Path
} else {
    Split-Path -Parent $PSScriptRoot
}

$iss = Join-Path $PSScriptRoot 'Installer\JASM.iss'
$payload = Join-Path $repo 'output\JASM'
$payloadExe = Join-Path $payload 'JASM - Just Another Skin Manager.exe'

if (-not (Test-Path $iss)) {
    throw "找不到 Inno 脚本: $iss"
}
if (-not (Test-Path $payloadExe)) {
    throw @"
output/JASM 不完整（缺少 $payloadExe）。
先跑 `python Build/Release.py`（CI 的 folder 构建）。本机没有 MSVC 时用
`python Build/Release.py ExcludeElevator` 只验证脚本，但那种产物不能发给用户。
"@
}

# 只接受 folder 构建：SingleFile / SelfContained 的 output/JASM 只有一个 exe 或没有更新器目录，
# 把它们塞进安装包等于把「单文件自解压」的问题原样保留下来。
$updaterDir = Join-Path $payload 'JASM - Auto Updater_New'
if (-not (Test-Path $updaterDir)) {
    throw @"
output/JASM 里没有 'JASM - Auto Updater_New'，这不是 folder 构建。
安装包必须基于 `python Build/Release.py` 的 folder 产物（含独立更新器）；SingleFile /
SelfContained 模式不生成安装包。
"@
}

if (-not $Version) {
    $csproj = Join-Path $repo 'src\GIMI-ModManager.WinUI\GIMI-ModManager.WinUI.csproj'
    $match = Select-String -Path $csproj -Pattern '<VersionPrefix>([^<]+)</VersionPrefix>'
    if (-not $match) {
        throw "无法从 $csproj 读取 <VersionPrefix>，请用 -Version 显式传入"
    }
    $Version = $match.Matches[0].Groups[1].Value.Trim()
}

if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "版本号 '$Version' 不是纯数字点分（如 2.36.2）"
}

# Inno 的 VersionInfoVersion 要求四段；三段的 2.36.2 补成 2.36.2.0。
$parts = @($Version.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$version4 = ($parts[0..3] -join '.')

function Find-Iscc {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    if ($candidates.Count -gt 0) { return @($candidates)[0] }

    $fromPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    throw @"
找不到 Inno Setup 6 的 ISCC.exe。
安装：winget install JRSoftware.InnoSetup
或从 https://jrsoftware.org/isdl.php 下载（安装时勾选 "Install Inno Setup Preprocessor"）。
"@
}

$iscc = Find-Iscc
Write-Host "版本      : $Version ($version4)"
Write-Host "ISCC      : $iscc"
Write-Host "源目录    : $payload"

if ($Sign) {
    & (Join-Path $PSScriptRoot 'Sign.ps1') -Path $payload -Recurse
    if ($LASTEXITCODE -ne 0) { throw "负载签名失败，终止打包" }
}

& $iscc "/DMyAppVersion=$Version" "/DMyAppVersion4=$version4" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC 编译失败（exit $LASTEXITCODE）"
}

$setup = Join-Path $repo "JASM_Setup_v$Version.exe"
if (-not (Test-Path $setup)) {
    throw "ISCC 成功退出但没有生成 $setup，检查 JASM.iss 的 OutputDir / OutputBaseFilename"
}

if ($Sign) {
    & (Join-Path $PSScriptRoot 'Sign.ps1') -Path $setup
    if ($LASTEXITCODE -ne 0) { throw "安装包签名失败" }
}

$hash = Get-FileHash -Algorithm SHA256 -Path $setup
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 2)
Write-Host ""
Write-Host "安装包: $setup"
Write-Host "体积  : $sizeMb MB"
Write-Host "SHA256: $($hash.Hash.ToLowerInvariant())"
Write-Host ""
Write-Host "下一步: python Build/PackAppUpdate.py --version $Version --folder-package JASM_v$Version.7z --installer-package JASM_Setup_v$Version.exe"
