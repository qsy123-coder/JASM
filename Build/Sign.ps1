# 给发布产物做 Authenticode 签名（SHA-256 + RFC3161 时间戳）。
#
# 支持的两种证书来源，按环境变量自动选择：
#
# 1) Azure Trusted Signing（推荐，个人可申请，$9.99/月，证书短有效期、无需硬件令牌）
#    需要：
#      TRUSTED_SIGNING_DLIB      Azure.CodeSigning.Dlib.dll 的完整路径
#      TRUSTED_SIGNING_METADATA  描述账号/证书配置的 metadata.json 的完整路径
#    GitHub Actions 里更省事的是直接用 azure/trusted-signing-action（见 workflow），
#    本脚本主要给本机 / 其它 CI 使用。
#
# 2) 传统 OV / EV 证书（装在当前用户证书库，或用 CA 的云签名客户端把证书映射进库）
#    需要：
#      SIGN_CERT_SHA1            证书指纹（不带空格）
#    时间戳默认 DigiCert RFC3161；可用 SIGN_TIMESTAMP_URL 覆盖。
#
# 两者都没有时脚本只打印警告并正常退出（PR 构建 / 未配置证书的开发机不应因此失败）。
# 发布构建应该在 CI 里额外断言签名成功（signtool verify 已包含在本脚本末尾）。
#
# 用法：
#   pwsh Build/Sign.ps1 -Path output/JASM -Recurse
#   pwsh Build/Sign.ps1 -Path JASM_Setup_v2.36.2.exe
#   pwsh Build/Sign.ps1 -Path output/JASM -Recurse,JASM_Setup_v2.36.2.exe   # 多个路径用逗号

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,
    [switch]$Recurse
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $found = Get-ChildItem -Path $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw "找不到 signtool.exe（安装 Windows SDK，或把 signtool 放进 PATH）"
}

$files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
foreach ($item in $Path) {
    if (Test-Path $item -PathType Container) {
        Get-ChildItem -Path $item -Recurse:$Recurse -File |
            Where-Object { $_.Extension -in '.exe', '.dll' } |
            ForEach-Object { $files.Add($_) }
    } elseif (Test-Path $item -PathType Leaf) {
        $files.Add((Get-Item $item))
    } else {
        throw "路径不存在: $item"
    }
}

if ($files.Count -eq 0) {
    Write-Warning "没有找到可签名的 exe/dll（$($Path -join ', ')）"
    return
}

$signtool = Find-SignTool
$signArgs = $null

if ($env:TRUSTED_SIGNING_DLIB -and $env:TRUSTED_SIGNING_METADATA) {
    $signArgs = @(
        'sign', '/v', '/fd', 'SHA256',
        '/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256',
        '/dlib', $env:TRUSTED_SIGNING_DLIB,
        '/dmdf', $env:TRUSTED_SIGNING_METADATA
    )
    Write-Host "签名方式: Azure Trusted Signing"
} elseif ($env:SIGN_CERT_SHA1) {
    $timestampUrl = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    $signArgs = @(
        'sign', '/v', '/fd', 'SHA256',
        '/tr', $timestampUrl, '/td', 'SHA256',
        '/sha1', $env:SIGN_CERT_SHA1
    )
    Write-Host "签名方式: 证书指纹 $($env:SIGN_CERT_SHA1)（时间戳 $timestampUrl）"
} else {
    Write-Warning @"
未配置签名证书，跳过签名（TRUSTED_SIGNING_DLIB / SIGN_CERT_SHA1 都为空）。
未签名的安装包仍会被 SmartScreen 判为「未知发布者」，Defender 误报也不会因为换成安装包而消失。
"@
    return
}

foreach ($file in $files) {
    Write-Host "签名: $($file.FullName)"
    & $signtool @signArgs $file.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "signtool 签名失败（exit $LASTEXITCODE）: $($file.FullName)"
    }
}

foreach ($file in $files) {
    & $signtool verify /pa /q $file.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "signtool 校验失败（exit $LASTEXITCODE）: $($file.FullName)"
    }
}

Write-Host "签名完成并校验通过：$($files.Count) 个文件"
