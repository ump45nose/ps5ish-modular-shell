$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$testRoot=Join-Path $root ('artifacts\deploy-test-'+[guid]::NewGuid().ToString('N'))
$PlayniteData=Join-Path $testRoot 'Playnite'
$out=Join-Path $testRoot 'packages'
$backup=Join-Path $PlayniteData 'backup'
New-Item -ItemType Directory -Path $out,$backup | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
# 从生产脚本取得函数及最外层事务，测试不复制实现，也不绕过真实部署进程检查。
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'deploy-library.ps1'),[ref]$null,[ref]$null)
$function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Install-Archive'},$true)
. ([scriptblock]::Create($function.Extent.Text))
$transaction=@($ast.EndBlock.Statements | Where-Object {$_ -is [System.Management.Automation.Language.TryStatementAst]})[0]
$shellTarget=Join-Path $PlayniteData 'Extensions\ModularShell_f13fdbe6-52ba-4198-8bea-457be1a609cf'
$themeTarget=Join-Path $PlayniteData 'Themes\Fullscreen\ModularShell'
foreach($target in @($shellTarget,$themeTarget)){New-Item -ItemType Directory -Path $target | Out-Null;Set-Content -LiteralPath (Join-Path $target 'old.txt') -Value 'previous-version'}
$original='{"Theme":"original","EnableGameControllerSupport":false,"InterfaceVolume":0.5,"BackgroundVolume":0.5}'
Set-Content -LiteralPath (Join-Path $PlayniteData 'fullscreenConfig.json') -Value $original -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PlayniteData 'fullscreenConfig.json') -Destination $backup
$before=(Get-FileHash -LiteralPath (Join-Path $PlayniteData 'fullscreenConfig.json')).Hash
foreach($item in @(@{name='ModularShell-0.8.0.pext';manifest='extension.yaml'},@{name='ModularShell-Fullscreen-0.8.0.pthm';manifest='theme.yaml'})){
    $stage=Join-Path $testRoot $item.manifest
    New-Item -ItemType Directory -Path $stage | Out-Null
    Set-Content -LiteralPath (Join-Path $stage $item.manifest) -Value 'Id: test'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage,(Join-Path $out $item.name))
}
$installed=New-Object System.Collections.ArrayList
$InstallAchievements=$true
$failed=$false
# 故意缺失第三个包，前两个已交换目录必须被恢复，失败产物仍保留用于审计。
try {. ([scriptblock]::Create($transaction.Extent.Text))}catch{$failed=$true}
if(-not $failed -or $installed.Count -ne 2){throw '未命中预期的第三包失败'}
foreach($target in @($shellTarget,$themeTarget)){
    if((Get-Content -LiteralPath (Join-Path $target 'old.txt') -Raw).Trim() -ne 'previous-version'){throw '旧版本未恢复'}
    if(@(Get-ChildItem -LiteralPath $target -Recurse -File).Count -ne 1){throw '目标残留新包文件'}
}
if((Get-FileHash -LiteralPath (Join-Path $PlayniteData 'fullscreenConfig.json')).Hash -ne $before){throw '配置字节变化'}
Write-Output 'PASS 第三包失败逆序恢复 Shell、主题和原配置字节'
Write-Output "故障注入证据：$testRoot"
