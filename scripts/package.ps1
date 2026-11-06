$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'artifacts'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# 使用明确文件清单，禁止测试程序、SDK、上游资源或用户数据混入发布包。
$files = [ordered]@{
    'themes/audio/trophy.wav' = "$out\themes\audio\trophy.wav"
    'themes/audio/startup.wav' = "$out\themes\audio\startup.wav"
    'themes/audio/switch.wav' = "$out\themes\audio\switch.wav"
    'themes/audio/ambient.mp3' = "$out\themes\audio\ambient.mp3"
    'third-party/THEME-MUSIC.md' = "$root\third-party\THEME-MUSIC.md"
    'themes/audio/move.wav' = "$out\themes\audio\move.wav"
    'themes/audio/confirm.wav' = "$out\themes\audio\confirm.wav"
    'themes/audio/back.wav' = "$out\themes\audio\back.wav"
    'plugins/Achievements/trophies.xaml' = "$root\plugins\Achievements\trophies.xaml"
    'Shell.QuickPanel.exe' = "$out\Shell.QuickPanel.exe"
    'themes/ambience.dusk.json' = "$out\themes\ambience.dusk.json"
    'themes/ambience.ocean.json' = "$out\themes\ambience.ocean.json"
    'themes/motion.brisk.json' = "$out\themes\motion.brisk.json"
    'themes/motion.soft.json' = "$out\themes\motion.soft.json"
    'extension.yaml' = "$root\src\Playnite\extension.yaml"
    'Shell.Playnite.dll' = "$out\Shell.Playnite.dll"
    'Shell.Contracts.dll' = "$out\Shell.Contracts.dll"
    'Shell.Core.dll' = "$out\Shell.Core.dll"
    'plugins/Demo/Demo.dll' = "$out\plugins\Demo\Demo.dll"
    'plugins/Demo/plugin.json' = "$out\plugins\Demo\plugin.json"
    'plugins/LocalMusic/LocalMusic.dll' = "$out\plugins\LocalMusic\LocalMusic.dll"
    'plugins/LocalMusic/plugin.json' = "$out\plugins\LocalMusic\plugin.json"
    'plugins/LocalVideo/LocalVideo.dll' = "$out\plugins\LocalVideo\LocalVideo.dll"
    'plugins/LocalVideo/plugin.json' = "$out\plugins\LocalVideo\plugin.json"
    'plugins/LocalAssets/LocalAssets.dll' = "$out\plugins\LocalAssets\LocalAssets.dll"
    'plugins/LocalAssets/plugin.json' = "$out\plugins\LocalAssets\plugin.json"
    'plugins/Achievements/Achievements.dll' = "$out\plugins\Achievements\Achievements.dll"
    'plugins/Achievements/plugin.json' = "$out\plugins\Achievements\plugin.json"
    'plugins/Emby/Emby.dll' = "$out\plugins\Emby\Emby.dll"
    'plugins/Emby/plugin.json' = "$out\plugins\Emby\plugin.json"
    'scripts/connect-emby.ps1' = "$root\scripts\connect-emby.ps1"
    'third-party/FFmpeg-LICENSE.txt' = "$root\third-party\FFmpeg-LICENSE.txt"
    'third-party/Node-LICENSE.txt' = "$root\third-party\Node-LICENSE.txt"
    'third-party/yt-dlp-LICENSE.txt' = "$root\third-party\yt-dlp-LICENSE.txt"
    'third-party/NOTICE.md' = "$root\third-party\NOTICE.md"
    'third-party/LibVLC-LGPL-2.1.txt' = "$root\third-party\LibVLC-LGPL-2.1.txt"
    'themes/layout.ps5ish.xaml' = "$out\themes\layout.ps5ish.xaml"
    'themes/layout.cinema.xaml' = "$out\themes\layout.cinema.xaml"
    'scripts/maintenance-ui.ps1' = "$root\scripts\maintenance-ui.ps1"
    'scripts/cache.ps1' = "$root\scripts\cache.ps1"
    'scripts/assets.ps1' = "$root\scripts\assets.ps1"
    'scripts/config-snapshot.ps1' = "$root\scripts\config-snapshot.ps1"
    'themes/components.controls.xaml' = "$out\themes\components.controls.xaml"
    'themes/metrics.comfort.json' = "$out\themes\metrics.comfort.json"
    'themes/palette.midnight.json' = "$out\themes\palette.midnight.json"
    'themes/palette.ember.json' = "$out\themes\palette.ember.json"
}
# 浏览器依赖限定为两份托管程序集、两种位数加载器和许可。
foreach ($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2-LICENSE.txt','runtimes/win-x86/native/WebView2Loader.dll','runtimes/win-x64/native/WebView2Loader.dll')) {
    $files['plugins/Emby/' + $name] = Join-Path "$out\plugins\Emby" $name
}
# 仅加入锁定恢复目录中的运行时 DLL 和插件索引；不收集其余构建文件。
$runtime = Join-Path $out 'plugins\LocalVideo\runtime'
foreach ($file in @(Get-ChildItem -LiteralPath $runtime -Recurse -File | Sort-Object FullName)) {
    if ($file.Extension -ne '.dll' -and $file.Name -ne 'plugins.dat') { throw '运行时目录存在非预期文件。' }
    $files['plugins/LocalVideo/runtime/' + $file.FullName.Substring($runtime.Length + 1).Replace('\','/')] = $file.FullName
}
# 仅打包锁定清单列出的素材工具，逐项校验。
$mediaTools = Get-Content "$root\third-party\media-tools.lock.json" -Raw | ConvertFrom-Json
foreach ($tool in $mediaTools.tools) {
    $toolPath = Join-Path "$out\plugins\LocalAssets\tools" $tool.name
    if ((Get-FileHash -LiteralPath $toolPath).Hash -ne $tool.sha256) { throw "素材工具散列不匹配：$($tool.name)" }
    $files['plugins/LocalAssets/tools/' + $tool.name] = $toolPath
}
$files['plugins/LocalAssets/tools/media-tools.lock.json'] = "$root\third-party\media-tools.lock.json"
. "$PSScriptRoot\version.ps1"
$package = Join-Path $out "ModularShell-$shellVersion.pext"
$stream = [IO.File]::Open($package, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $files.Keys) {
        $entry = $zip.CreateEntry($name)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026, 9, 5, 0, 0, 0, [TimeSpan]::Zero)
        $destination = $entry.Open()
        $source = [IO.File]::OpenRead($files[$name])
        try { $source.CopyTo($destination) } finally { $source.Dispose(); $destination.Dispose() }
    }
} finally { $zip.Dispose(); $stream.Dispose() }
# 重读产物，检查文件完整性和 SDK 排除规则。
$check = [IO.Compression.ZipFile]::OpenRead($package)
try {
    if ($check.Entries.Count -ne $files.Count) { throw '安装包文件数不匹配。' }
    foreach ($name in $files.Keys) { if ($null -eq $check.GetEntry($name)) { throw "缺少文件：$name" } }
    if ($null -ne $check.GetEntry('Playnite.SDK.dll')) { throw '禁止打包宿主 SDK。' }
} finally { $check.Dispose() }
Get-FileHash -LiteralPath $package | Select-Object Hash,Path
