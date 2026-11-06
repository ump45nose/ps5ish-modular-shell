$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$cache = Join-Path $root 'artifacts\webview2-sdk'
$hash = '911A472128C82AC8BAA0C486C23342CC9DD6E7DC50D754E676726642CA065C60'
New-Item -ItemType Directory -Force $cache | Out-Null
# 锁定官方 SDK 包与散列，浏览器运行时使用系统已安装的 Evergreen。
$archive = Join-Path $cache 'sdk.zip'
if (!(Test-Path $archive)) { Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3650.58/microsoft.web.webview2.1.0.3650.58.nupkg' -OutFile $archive }
if ((Get-FileHash $archive).Hash -ne $hash) { throw 'WebView2 SDK 散列不匹配。' }
Expand-Archive $archive (Join-Path $cache 'package') -Force
$destination = Join-Path $root 'artifacts\plugins\Emby'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item "$cache\package\lib\net462\Microsoft.Web.WebView2.Core.dll", "$cache\package\lib\net462\Microsoft.Web.WebView2.Wpf.dll" $destination -Force
# SDK 根据当前进程位数选择 loader，同时支持 Playnite x86 和独立 x64 验证。
foreach ($architecture in @('x86','x64')) {
    New-Item -ItemType Directory -Force "$destination\runtimes\win-$architecture\native" | Out-Null
    Copy-Item "$cache\package\runtimes\win-$architecture\native\WebView2Loader.dll" "$destination\runtimes\win-$architecture\native" -Force
}
Copy-Item "$cache\package\LICENSE.txt" "$destination\WebView2-LICENSE.txt" -Force
