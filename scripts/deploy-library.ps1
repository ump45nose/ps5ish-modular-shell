param([string]$PlayniteData="$env:APPDATA\Playnite",[switch]$InstallAchievements)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=Join-Path $root 'artifacts'
if (@(Get-Process | Where-Object {$_.ProcessName -like 'Playnite*'}).Count) {throw '请正常退出 Playnite 后部署。'}
$backup=Join-Path $PlayniteData ('ModularShell-backups\library-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$installed=New-Object System.Collections.ArrayList

# 暂存与目标严格比较相对路径和散列；完整旧目录保留，目标存在时 Directory.Move 必须失败。
function Install-Archive([string]$Package,[string]$Target,[string]$Label,[string]$Required) {
    $absolute=[IO.Path]::GetFullPath($Target)
    if(-not $absolute.StartsWith([IO.Path]::GetFullPath($PlayniteData)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '安装目标越界'}
    New-Item -ItemType Directory -Force -Path (Split-Path $absolute) | Out-Null
    $stage=Join-Path $backup ($Label+'-new')
    $old=Join-Path $backup ($Label+'-previous')
    $zip=[IO.Compression.ZipFile]::OpenRead($Package)
    try {
        foreach($entry in $zip.Entries){
            $path=[IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
            if(-not $path.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw '压缩包路径越界'}
        }
    }finally{$zip.Dispose()}
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$stage)
    if(-not(Test-Path -LiteralPath (Join-Path $stage $Required) -PathType Leaf)){throw '包缺少根清单'}
    $expected=@(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {@{relative=$_.FullName.Substring($stage.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    if(Test-Path -LiteralPath $absolute){
        if((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '拒绝链接目录'}
        [IO.Directory]::Move($absolute,$old)
    }
    try {
        [IO.Directory]::Move($stage,$absolute)
        foreach($file in $expected){if((Get-FileHash -LiteralPath (Join-Path $absolute $file.relative)).Hash -ne $file.sha256){throw '安装校验失败'}}
        if(@(Get-ChildItem -LiteralPath $absolute -Recurse -File).Count -ne $expected.Count){throw '安装文件数异常'}
    }catch{
        if(Test-Path -LiteralPath $absolute){[IO.Directory]::Move($absolute,(Join-Path $backup ($Label+'-failed')))}
        if(Test-Path -LiteralPath $old){[IO.Directory]::Move($old,$absolute)}
        throw
    }
    # 先登记交换结果，即使写回执失败也能由外层按逆序恢复。
    [void]$installed.Add(@{target=$absolute;old=$old;label=$Label})
    @{target=$absolute;packageHash=(Get-FileHash -LiteralPath $Package).Hash;files=$expected;backup=$old} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $backup ($Label+'-receipt.json')) -Encoding utf8
    Write-Output "$Label 已安装并校验 $($expected.Count) 个相对路径"
}
foreach($name in @('config.json','fullscreenConfig.json')){Copy-Item -LiteralPath (Join-Path $PlayniteData $name) -Destination (Join-Path $backup $name)}
# 保存迁移前的用户素材引用，部署不覆盖所选媒体文件。
$userSettings=Join-Path $PlayniteData 'ExtensionsData\f13fdbe6-52ba-4198-8bea-457be1a609cf'
foreach($name in @('theme-experience.json','preview.json')){if(Test-Path -LiteralPath (Join-Path $userSettings $name)){Copy-Item -LiteralPath (Join-Path $userSettings $name) -Destination (Join-Path $backup $name)}}
$shellTarget=Join-Path $PlayniteData 'Extensions\ModularShell_f13fdbe6-52ba-4198-8bea-457be1a609cf'
try {
Install-Archive (Join-Path $out 'ModularShell-0.8.0.pext') $shellTarget 'shell' 'extension.yaml'
Install-Archive (Join-Path $out 'ModularShell-Fullscreen-0.8.0.pthm') (Join-Path $PlayniteData 'Themes\Fullscreen\ModularShell') 'theme' 'theme.yaml'
if($InstallAchievements){Install-Archive (Join-Path $out 'PlayniteAchievements_3_1_3.pext') (Join-Path $PlayniteData 'Extensions\PlayniteAchievements') 'achievements' 'extension.yaml'}
# 更改全屏专用设置；桌面主题和其他应用配置保持不变。
$file=Join-Path $PlayniteData 'fullscreenConfig.json'
$config=Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
$config.Theme='ModularShell_Fullscreen_80b793c3-a46b-4702-9249-2105bc2c3029'
$config.EnableGameControllerSupport=$true
$config.InterfaceVolume=0
$config.BackgroundVolume=0
$temporary=$file+'.library.tmp'
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temporary -Encoding utf8
[IO.File]::Replace($temporary,$file,$file+'.bak')
$receipt=[ordered]@{installedAt=(Get-Date).ToString('o');target=$shellTarget;backup=$backup;packageHash=(Get-FileHash (Join-Path $out 'ModularShell-0.8.0.pext')).Hash;files=@(Get-ChildItem -LiteralPath $shellTarget -Recurse -File | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
$receipt | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'installation-receipt.json') -Encoding utf8
Write-Output "恢复备份：$backup"
} catch {
    $failure=$_
    $rollbackErrors=New-Object System.Collections.ArrayList
    # 仅移动本次记录且位于 Playnite 根内的路径，失败版本保留供排查。
    for($index=$installed.Count-1;$index -ge 0;$index--){
        $item=$installed[$index]
        try {
            $target=[IO.Path]::GetFullPath($item.target)
            if(-not $target.StartsWith([IO.Path]::GetFullPath($PlayniteData)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '回退目标越界'}
            if(Test-Path -LiteralPath $target){[IO.Directory]::Move($target,(Join-Path $backup ($item.label+'-rolled-back-new')))}
            if(Test-Path -LiteralPath $item.old){[IO.Directory]::Move($item.old,$target)}
        }catch{[void]$rollbackErrors.Add($_.Exception.Message)}
    }
    try {Copy-Item -LiteralPath (Join-Path $backup 'fullscreenConfig.json') -Destination (Join-Path $PlayniteData 'fullscreenConfig.json') -Force}
    catch{[void]$rollbackErrors.Add($_.Exception.Message)}
    if($rollbackErrors.Count){throw "部署失败：$($failure.Exception.Message)。回退未完整完成：$($rollbackErrors -join '；')。备份：$backup"}
    throw "部署失败，已恢复本次交换的目录和全屏配置：$($failure.Exception.Message)。备份：$backup"
}
