$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$cache = Join-Path $root 'artifacts/dependencies/dreams-become-real.mp3'
$output = Join-Path $root 'artifacts/themes/audio'
# 作者公开下载及 CC BY 4.0 授权；锁定内容，避免远端替换造成不可重复构建。
New-Item -ItemType Directory -Force (Split-Path $cache), $output | Out-Null
if (-not (Test-Path -LiteralPath $cache)) {
    Invoke-WebRequest 'https://incompetech.com/music/royalty-free/mp3-royaltyfree/Dreams%20Become%20Real.mp3' -OutFile $cache
}
if ((Get-FileHash -LiteralPath $cache).Hash -ne 'A8EA5DF5F969ED8D898AF7FE07EB4712233F03E9D83F7EBCC88D1C851F3171B0') { throw '主题音乐内容校验失败。' }
Copy-Item -LiteralPath $cache -Destination (Join-Path $output 'ambient.mp3') -Force
