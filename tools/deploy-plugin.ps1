<#
.SYNOPSIS
    把 Release 构建产物部署到本机 ClassIsland 插件目录（开发联调用）。

.DESCRIPTION
    宿主进程加载插件 DLL 后该文件会被锁定，无法原地覆盖，所以脚本会先等 ClassIsland 退出
    （默认最多等 30 分钟）再替换；部署完打印校验用的 SHA256。

.EXAMPLE
    pwsh -File tools/deploy-plugin.ps1
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Target,
    [string]$ProcessName = 'ClassIsland.Desktop',
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repo 'src\CyberTechRep.Plugin\bin\Release\net8.0' }
if (-not $Target) { $Target = Join-Path $repo 'ClassIsland_app_windows_x64_full_folder\data\Plugins\classisland.classing' }

$sourceDll = Join-Path $Source 'CyberTechRep.Plugin.dll'
if (-not (Test-Path $sourceDll)) { throw "未找到构建产物：$sourceDll（先执行 dotnet build -c Release）" }
if (-not (Test-Path $Target)) { throw "未找到插件目录：$Target" }

Write-Host "等待 $ProcessName 退出（最多 $TimeoutSeconds 秒）……"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw "等待 $ProcessName 退出超时（$TimeoutSeconds 秒），未部署。" }
    Start-Sleep -Seconds 2
}

# 进程退出后稍等一下再替换，降低「用户立刻重启、文件刚好又被锁」的竞态
Start-Sleep -Seconds 3

# 排除宿主已提供的程序集（与 CyberTechRep.Plugin.csproj 的打包剥离逻辑保持一致）
$robocopyArgs = @($Source, $Target, '/E', '/XF', 'Microsoft.Extensions.*.dll', 'System.IO.Pipelines.dll',
    '/NFL', '/NDL', '/NJH', '/NJS', '/R:30', '/W:2')
& robocopy @robocopyArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败，退出码 $LASTEXITCODE" }

$newHash = (Get-FileHash $sourceDll -Algorithm SHA256).Hash
$deployedHash = (Get-FileHash (Join-Path $Target 'CyberTechRep.Plugin.dll') -Algorithm SHA256).Hash
if ($newHash -ne $deployedHash) { throw '部署校验失败：目标 DLL 与构建产物不一致。' }

Write-Host "已部署到 $Target"
Write-Host "CyberTechRep.Plugin.dll SHA256 = $deployedHash"
