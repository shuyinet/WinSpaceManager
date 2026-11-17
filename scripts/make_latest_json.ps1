# 生成自动更新清单 latest.json（与安装包一起上传到 R2 的 sysmgr/ 目录）
# 用法：powershell -File scripts\make_latest_json.ps1 -Version 1.0.1
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$BaseUrl = 'https://gameres.shuyinet.com/sysmgr'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installer = Join-Path $root "artifacts\SysMgr-Setup-$Version.exe"
if (-not (Test-Path $installer)) {
    throw "未找到安装包：$installer"
}

$fileName = Split-Path $installer -Leaf
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = $Version
    url     = "$BaseUrl/$fileName"
    sha256  = $hash
    notes   = "SysMgr $Version"
} | ConvertTo-Json

$out = Join-Path $root 'artifacts\latest.json'
[IO.File]::WriteAllText($out, $manifest, [Text.UTF8Encoding]::new($false))
Write-Host "已生成 $out"
Write-Host "请把 $fileName 和 latest.json 一起上传到 R2 的 sysmgr/ 目录"
