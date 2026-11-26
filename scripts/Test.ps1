param([string]$BuildDirectory,[switch]$Integration,[string]$CorePath)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(-not $BuildDirectory){$BuildDirectory=Join-Path $projectRoot 'dist'}
$version='v1.10.0-beta.1';$exe=Join-Path $BuildDirectory "版本\$version\Antigravity启动器.exe"
if(-not(Test-Path -LiteralPath $exe)){throw '请先运行 scripts\Build.ps1。'}
$oldData=$env:AGY_LAUNCHER_DATA
$testData=Join-Path ([IO.Path]::GetTempPath()) ('agy-launcher-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testData '用户数据') -Force | Out-Null
$env:AGY_LAUNCHER_DATA=$testData
try {
    # Synthetic loopback-only fixture; no real subscription, account, CLI, or model call.
    $configPath=Join-Path $testData 'fixture.yaml'
    @'
proxies:
  - name: test-one
    type: http
    server: 127.0.0.1
    port: 9
  - name: test-two
    type: http
    server: 127.0.0.1
    port: 9
proxy-groups: []
'@ | Set-Content -LiteralPath $configPath -Encoding UTF8
    [ordered]@{AppPath=$exe;CorePath=$CorePath;ClashConfigPath=$configPath;Model='';CreateDesktopShortcut=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testData '用户数据\settings.json') -Encoding UTF8
    $flags=@('--self-test','--batch-test','--parallel-test','--wait-policy-test','--runtime-test','--community-test','--layout-test','--tray-test','--recovery-test')
    if($Integration){if(-not(Test-Path -LiteralPath $CorePath)){throw '集成测试需要 -CorePath 指向可信 Mihomo EXE。'};$flags+=@('--isolation-test','--runtime-integration-test')}
    foreach($flag in $flags){
        $p=Start-Process -FilePath $exe -ArgumentList $flag -WindowStyle Hidden -PassThru
        if(-not $p.WaitForExit(25000)){throw "测试超时：$flag；仅使用本轮创建的测试进程，未操作真实客户端。"}
        if($p.ExitCode -ne 0){throw "测试失败：$flag；报告在 $testData"}
        Write-Output "PASS $flag"
    }
    $reportRoot=Join-Path (Join-Path $projectRoot 'artifacts\tests') (Split-Path -Leaf $testData);New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $testData "reports\1.10.0-beta.1\*-test.json") -Destination $reportRoot -Force
    $count=0;Get-ChildItem -LiteralPath $reportRoot -Filter '*-test.json' | ForEach-Object {$object=Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json;foreach($property in $object.PSObject.Properties){if($property.Value -ne $true){throw "失败断言：$($_.Name)/$($property.Name)"};$count++}}
    Write-Output "通过 $count 项检查；未调用模型，未修改系统代理或真实客户端。"
} finally {$env:AGY_LAUNCHER_DATA=$oldData}
