param([string]$Root)
$ErrorActionPreference='Stop'
if(-not $Root){$Root=Split-Path -Parent $PSScriptRoot}
$Root=[IO.Path]::GetFullPath($Root)
$prefix=$Root.TrimEnd('\')+'\';$issues=New-Object Collections.Generic.List[string]
$files=Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Where-Object {
    $relative=$_.FullName.Substring($prefix.Length)
    $relative -notmatch '^(\.git|dist|artifacts|bin|obj)[\\/]'
}
foreach($file in $files){
    $relative=$file.FullName.Substring($prefix.Length)
    if($file.Name -match '^(settings|state|official-manifest|migration-plan)\.json$|^(config|clash-verge)\.ya?ml$' -or $file.Extension -match '^\.(exe|dll|pdb|zip|ico|log|bak|tmp)$'){$issues.Add("禁止发布的本地文件：$relative");continue}
    $content=[IO.File]::ReadAllText($file.FullName)
    if($content -match '(?i)[A-Z]:\\Users\\(?!Public(?:\\|$)|Default(?:\\|$))[^\\\r\n"'']+\\'){$issues.Add("疑似个人用户路径：$relative")}
    if($content -match '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AIza[A-Za-z0-9_-]{30,}'){$issues.Add("疑似密钥：$relative")}
    if($content -match '(?i)["''](?:access_token|refresh_token|password|api_key)["'']\s*[:=]\s*["''][A-Za-z0-9_-]{20,}'){$issues.Add("疑似静态凭据：$relative")}
}
if($issues.Count){$issues | ForEach-Object {Write-Output $_};throw "公开内容审查失败：$($issues.Count) 项。"}
Write-Output "公开文件审查通过：$($files.Count) 个文件；规则检查不能替代人工审查。"
