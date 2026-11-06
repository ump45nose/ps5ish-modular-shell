param([string]$PlaynitePath="$env:LOCALAPPDATA\Playnite",[string]$PlayniteData="$env:APPDATA\Playnite")
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=Join-Path $root 'artifacts'
# 只采集显示设备与全屏相关字段，不导出账号或整份应用配置。
$monitors=@(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorID | ForEach-Object {
    @{name=(-join ($_.UserFriendlyName | Where-Object {$_} | ForEach-Object {[char]$_}));active=$_.Active}
})
$video=@(Get-CimInstance Win32_VideoController | Select-Object Name,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
$config=Get-Content -LiteralPath (Join-Path $PlayniteData 'fullscreenConfig.json') -Raw | ConvertFrom-Json
$record=@{capturedAt=(Get-Date).ToString('o');monitors=$monitors;video=$video;theme=$config.Theme;monitor=$config.Monitor;usePrimaryDisplay=$config.UsePrimaryDisplay;controller=$config.EnableGameControllerSupport;note='读取当前输出，不证明设备支持模式或已完成电视实测。'}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'tv-configuration.json') -Encoding utf8
# 快捷方式明确工作目录，保证 Playnite 能读取相对路径控制器映射。
$executable=Join-Path $PlaynitePath 'Playnite.FullscreenApp.exe'
if(-not(Test-Path -LiteralPath $executable -PathType Leaf)){throw '全屏程序不存在'}
$shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Modular Shell 电视游戏库.lnk'
if(Test-Path -LiteralPath $shortcutPath){Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $out ('tv-shortcut-'+(Get-Date -Format yyyyMMdd-HHmmss)+'.lnk'))}
$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath=$executable
$shortcut.WorkingDirectory=$PlaynitePath
$shortcut.Description='打开 Playnite 全屏游戏库'
$shortcut.IconLocation=$executable+',0'
$shortcut.Save()
Write-Output "电视入口：$shortcutPath"
Write-Output "当前显示配置：$(Join-Path $out 'tv-configuration.json')"
