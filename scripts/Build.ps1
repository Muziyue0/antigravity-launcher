param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$version = 'v1.10.0-beta.1'
$versionRoot = Join-Path $outputRoot "版本\$version"
New-Item -ItemType Directory -Path $versionRoot,(Join-Path $outputRoot '共享') -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8，未发现系统 C# 编译器。' }
& (Join-Path $PSScriptRoot 'New-Icon.ps1') -Destination (Join-Path $versionRoot 'Launcher.ico')
$common = @('/nologo','/target:winexe','/platform:x64','/codepage:65001','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',('/win32icon:'+(Join-Path $versionRoot 'Launcher.ico')))
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Where-Object {$_.Name -ne 'Entry.cs'} | ForEach-Object {$_.FullName}
& $compiler @common '/r:System.Net.Http.dll' '/r:System.Management.dll' ('/out:'+(Join-Path $versionRoot 'Antigravity启动器.exe')) @sources
if ($LASTEXITCODE -ne 0) { throw '主程序编译失败。' }
& $compiler @common ('/out:'+(Join-Path $outputRoot 'Antigravity代理.exe')) (Join-Path $projectRoot 'src\Entry.cs')
if ($LASTEXITCODE -ne 0) { throw '固定入口编译失败。' }
'{"layout":1}' | Set-Content -LiteralPath (Join-Path $versionRoot 'layout.json') -Encoding UTF8
$release = [ordered]@{Version=$version;RelativeExe="版本\$version\Antigravity启动器.exe";Sha256=(Get-FileHash -LiteralPath (Join-Path $versionRoot 'Antigravity启动器.exe') -Algorithm SHA256).Hash}
$release | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot '共享\当前版本.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $outputRoot '使用说明.md') -Force
if (Test-Path -LiteralPath (Join-Path $projectRoot 'LICENSE')) {Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $outputRoot -Force}
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $outputRoot -Force
Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Where-Object {$_.Name -ne 'SHA256SUMS.txt'} | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.FullName.Substring($outputRoot.TrimEnd('\').Length+1)
} | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding UTF8
Write-Output "构建完成：$outputRoot"
