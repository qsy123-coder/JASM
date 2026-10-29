<#
.SYNOPSIS
    解密 / 枚举 LODENC01 容器（第三方管理器 Rocket 的 Lod0 插件配置与诊断日志）。

.DESCRIPTION
    格式（见 docs/rocket-injection-and-uhd-texture-analysis.md §4.3）：
        偏移 0  : "LODENC01"   (8B)
        偏移 8  : u32 version  (1..3)
        偏移 12 : u32 cipherLen
        偏移 16 : DPAPI blob
    熵 = "LOD1" + version(小端 u32)，作用域 = CurrentUser。

    诊断日志是多条记录追加：首条带 LODENC01 头，后续每条为 [u32 cipherLen][DPAPI blob]。

    只能在**生成这些文件的同一台机器、同一 Windows 用户**下解密；换机器/换用户必然失败，
    这是 DPAPI 的设计，不是脚本问题。

.PARAMETER Path
    要解密的文件（Lod0_quality.ini / Lod0_scan_cache.ini / Lod0_texture.diagnostic.log）。

.PARAMETER OutFile
    可选，解密结果写入该文件（UTF-8 无 BOM）。不填则输出到标准输出。

.PARAMETER MaxRecords
    日志模式下最多解多少条（默认 200），防止畸形文件导致长时间循环。

.EXAMPLE
    .\DecryptLodEnc.ps1 -Path 'D:\SomeGame\backing\Config\WW\Lod0_quality.ini'

.EXAMPLE
    .\DecryptLodEnc.ps1 -Path '.\Lod0_texture.diagnostic.log' -OutFile '.\lod0_diag.txt'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$OutFile,

    [int]$MaxRecords = 200
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function Get-Entropy([uint32]$Version) {
    # "LOD1" + version(LE)
    return [byte[]](0x4C, 0x4F, 0x44, 0x31) + [BitConverter]::GetBytes($Version)
}

function Try-Unprotect([byte[]]$Cipher, [uint32]$Version) {
    try {
        return [System.Security.Cryptography.ProtectedData]::Unprotect(
            $Cipher, (Get-Entropy $Version), 'CurrentUser')
    }
    catch {
        return $null
    }
}

if (-not (Test-Path -LiteralPath $Path)) {
    throw "找不到文件：$Path"
}

$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
$sb = New-Object System.Text.StringBuilder
$pos = 0
$records = 0
$failures = 0

if ($bytes.Length -ge 16 -and [System.Text.Encoding]::ASCII.GetString($bytes, 0, 8) -eq 'LODENC01') {
    # 首条带 magic
    $ver = [BitConverter]::ToUInt32($bytes, 8)
    $len = [BitConverter]::ToUInt32($bytes, 12)
    if ($len -le 0 -or (16 + $len) -gt $bytes.Length) {
        throw "头部声明的长度 $len 超出文件范围，文件可能被截断。"
    }
    $cipher = New-Object byte[] $len
    [Array]::Copy($bytes, 16, $cipher, 0, $len)
    $plain = Try-Unprotect $cipher $ver
    if ($null -eq $plain) {
        throw "DPAPI 解密失败：请确认是在生成该文件的同一机器、同一用户下运行。"
    }
    [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($plain))
    $pos = 16 + $len
    $records++

    # 后续记录：[u32 cipherLen][DPAPI blob]
    while (($pos + 4) -lt $bytes.Length -and $records -lt $MaxRecords) {
        $len = [BitConverter]::ToUInt32($bytes, $pos)
        if ($len -le 0 -or $len -gt 0x40000 -or ($pos + 4 + $len) -gt $bytes.Length) {
            break
        }
        $cipher = New-Object byte[] $len
        [Array]::Copy($bytes, $pos + 4, $cipher, 0, $len)
        $plain = Try-Unprotect $cipher 1
        if ($null -eq $plain) {
            $failures++
            break
        }
        [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($plain))
        $pos += 4 + $len
        $records++
    }
}
else {
    Write-Warning "文件没有 LODENC01 头，可能已经是明文；直接输出原文。"
    [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($bytes))
    $records = 1
}

$text = $sb.ToString().Trim([char]0)
if ($OutFile) {
    $target = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location) $OutFile }
    [System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("解密完成：{0} 条记录，输出 {1}（{2} 字符）" -f $records, $target, $text.Length)
}
else {
    Write-Verbose ("解密完成：{0} 条记录，失败 {1}" -f $records, $failures)
    $text
}
