param([switch]$PluginOnly, [string]$PlaynitePath = "$env:LOCALAPPDATA\Playnite")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'

# 串行调用编译器；编译失败立即停止，不更换构建目录。
function Invoke-Compiler([string[]]$Arguments) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $messages = @(& $compiler /nologo /utf8output @Arguments 2>&1)
        $code = $LASTEXITCODE
        $messages | ForEach-Object { Write-Output $_ }
        if ($code -eq 0) { return }
        # 输出文件被并发占用时，仅在同一目录串行重试一次。
        if ($attempt -eq 0 -and (($messages -join "`n") -match 'CS2012|CS0042')) { continue }
        throw '编译失败；若存在目录占用，已停止构建，禁止切换构建目录。'
    }
}
New-Item -ItemType Directory -Force $out, "$out\plugins\Demo", "$out\plugins\LocalMusic", "$out\plugins\LocalVideo", "$out\plugins\LocalAssets", "$out\themes" | Out-Null
New-Item -ItemType Directory -Force "$out\plugins\Achievements" | Out-Null
New-Item -ItemType Directory -Force "$out\plugins\Emby" | Out-Null
if (-not $PluginOnly) {
    & python "$root\scripts\generate-audio.py"
    if ($LASTEXITCODE -ne 0) { throw "主题音频生成失败。" }
    & "$root\scripts\restore-theme-music.ps1"
    & "$root\scripts\restore-media-tools.ps1"
    Invoke-Compiler @('/target:library', "/out:$out\Shell.Contracts.dll", "$root\src\Contracts.cs")
    Invoke-Compiler @('/target:library', "/out:$out\Shell.Core.dll", "/reference:$out\Shell.Contracts.dll", '/reference:System.Runtime.Serialization.dll', "$root\src\Core.cs", "$root\src\ModuleLoader.cs", "$root\src\MusicPreview.cs", "$root\src\MediaPreview.cs")
    $refs = @("/reference:$out\Shell.Contracts.dll", "/reference:$out\Shell.Core.dll", "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll", '/reference:System.Xaml.dll')
    Invoke-Compiler (@('/target:winexe', "/out:$out\Shell.Prototype.exe", "$root\src\Prototype.cs") + $refs)
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.Tests.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\Shell.Core.dll", "$root\tests\Tests.cs")
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.VideoTests.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\Shell.Core.dll", "$root\tests\VideoTests.cs")
    # 与真实 Playnite 相同的 32 位进程另跑解码回归，防止只验证 64 位独立程序。
    Invoke-Compiler @('/target:exe', '/platform:x86', "/out:$out\Shell.VideoTests.x86.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\Shell.Core.dll", "$root\tests\VideoTests.cs")
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.VideoSoak.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\Shell.Core.dll", "$root\tests\VideoSoak.cs")
    if (-not (Test-Path "$PlaynitePath\Playnite.SDK.dll")) { throw '未找到 Playnite SDK，请指定 -PlaynitePath。' }
    Invoke-Compiler (@('/target:library', "/out:$out\Shell.Playnite.dll", "/reference:$PlaynitePath\Playnite.SDK.dll", '/reference:System.Runtime.Serialization.dll', "/resource:$root\themes\layout.ps5ish.xaml,SafeLayout.xaml", "/resource:$root\themes\components.controls.xaml,SafeComponents.xaml", "$root\src\Playnite\PlayniteSource.cs", "$root\src\Playnite\ShellPlugin.cs", "$root\src\Playnite\QuickPanelBridge.cs", "$root\src\Playnite\ShellControl.cs", "$root\src\Playnite\MediaHub.cs", "$root\src\Playnite\GameLibrary.cs", "$root\src\Playnite\GameGridPanel.cs", "$root\src\Playnite\HomeCarouselPanel.cs", "$root\src\Playnite\Atmosphere.cs", "$root\src\Playnite\AutomaticAssets.cs", "$root\src\Playnite\ThemeComposer.cs", "$root\src\Playnite\ThemePacks.cs", "$root\src\Playnite\GameCard.cs", "$root\src\Playnite\ShellSettings.cs", "$root\src\Playnite\ExperienceSettings.cs", "$root\src\Playnite\ThemeExperience.cs", "$root\src\Playnite\Ps5Presentation.cs", "$root\src\Playnite\NotificationAudio.cs", "$root\src\Playnite\NavigationPreference.cs", "$root\src\Playnite\ControllerInput.cs") + $refs)
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.AdapterTests.exe", "/reference:$PlaynitePath\Playnite.SDK.dll", "/reference:$out\Shell.Playnite.dll", "/reference:$out\Shell.Contracts.dll", "$root\tests\AdapterTests.cs")
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.ControlSmoke.exe", "/reference:$PlaynitePath\Playnite.SDK.dll", "/reference:$out\Shell.Playnite.dll", "$root\tests\ControlSmoke.cs") + $refs)
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.WpfTests.exe", "/reference:$PlaynitePath\Playnite.SDK.dll", "/reference:$out\Shell.Playnite.dll", "$root\tests\WpfTests.cs") + $refs)
    Invoke-Compiler (@('/target:winexe', "/out:$out\Shell.QuickPanel.exe", '/reference:System.Runtime.Serialization.dll', "$root\src\QuickPanel.cs") + $refs)
    # SDK 仅在本地测试目录供测试进程解析，打包清单明确排除 SDK。
    Copy-Item "$PlaynitePath\Playnite.SDK.dll" $out -Force
    Copy-Item "$root\themes\*.xaml" "$out\themes" -Force
    New-Item -ItemType Directory -Force "$out\themes\PlayniteAchievements" | Out-Null
    Copy-Item "$root\themes\PlayniteAchievements\*.xaml" "$out\themes\PlayniteAchievements" -Force
    Copy-Item "$root\themes\*.json" "$out\themes" -Force
}
# 插件只引用共享契约，可独立构建，不重编译宿主。
Invoke-Compiler @('/target:library', "/out:$out\plugins\Demo\Demo.dll", "/reference:$out\Shell.Contracts.dll", "$root\plugins\Demo\Demo.cs")
Copy-Item "$root\plugins\Demo\plugin.json" "$out\plugins\Demo" -Force
Invoke-Compiler @('/target:library', "/out:$out\plugins\LocalMusic\LocalMusic.dll", "/reference:$out\Shell.Contracts.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll", "$root\plugins\LocalMusic\LocalMusic.cs")
Copy-Item "$root\plugins\LocalMusic\plugin.json" "$out\plugins\LocalMusic" -Force
Invoke-Compiler @('/target:library', "/out:$out\plugins\LocalVideo\LocalVideo.dll", "/reference:$out\Shell.Contracts.dll", "$root\plugins\LocalVideo\Native.cs", "$root\plugins\LocalVideo\LocalVideo.cs")
Copy-Item "$root\plugins\LocalVideo\plugin.json" "$out\plugins\LocalVideo" -Force
Invoke-Compiler @('/target:library', "/out:$out\plugins\LocalAssets\LocalAssets.dll", "/reference:$out\Shell.Contracts.dll", '/reference:System.Runtime.Serialization.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', "$root\plugins\LocalAssets\LocalAssets.cs", "$root\plugins\LocalAssets\AutomaticAssets.cs", "$root\plugins\LocalAssets\MediaFiles.cs", "$root\plugins\LocalAssets\SteamAssets.cs")
Copy-Item "$root\plugins\LocalAssets\plugin.json" "$out\plugins\LocalAssets" -Force
Invoke-Compiler @('/target:library', "/out:$out\plugins\Achievements\Achievements.dll", "/reference:$out\Shell.Contracts.dll", "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll", '/reference:System.Xaml.dll', "$root\plugins\Achievements\Achievements.cs", "$root\plugins\Achievements\TrophySummary.cs", "$root\plugins\Achievements\TrophyDetails.cs", "$root\plugins\Achievements\UnlockAudio.cs", "/reference:System.Runtime.Serialization.dll")
Copy-Item "$root\plugins\Achievements\plugin.json", "$root\plugins\Achievements\trophies.xaml" "$out\plugins\Achievements" -Force
& "$root\scripts\restore-webview.ps1"
Invoke-Compiler @('/target:library', "/out:$out\plugins\Emby\Emby.dll", "/reference:$out\plugins\Emby\Microsoft.Web.WebView2.Core.dll", "/reference:$out\plugins\Emby\Microsoft.Web.WebView2.Wpf.dll", "/reference:System.Drawing.dll", "/resource:$root\plugins\Emby\controller.js,Controller.js", "$root\plugins\Emby\WebLibrary.cs", "/reference:$out\Shell.Contracts.dll", "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll", '/reference:System.Xaml.dll', '/reference:System.Security.dll', '/reference:System.Net.Http.dll', '/reference:System.Runtime.Serialization.dll', "$root\plugins\Emby\Client.cs", "$root\plugins\Emby\Configuration.cs", "$root\plugins\Emby\Browser.cs", "$root\plugins\Emby\Playback.cs", "$root\plugins\Emby\StreamRelay.cs", "$root\plugins\Emby\PreviewAssets.cs")
Copy-Item "$root\plugins\Emby\plugin.json" "$out\plugins\Emby" -Force
if (-not $PluginOnly) {
    # 测试解析目录复制仅用于独立测试进程；发布包仍只收集插件路径。
    Copy-Item "$out\plugins\Emby\Emby.dll" "$out\Emby.dll" -Force
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.EmbyTests.exe", "/reference:$out\Emby.dll", '/reference:System.Security.dll', '/reference:System.Runtime.Serialization.dll', "$root\tests\EmbyTests.cs")
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.EmbyLive.exe", "/reference:$out\Emby.dll", "$root\tests\EmbyLive.cs") + $refs)
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.PlaybackTests.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\Emby.dll", '/reference:System.Security.dll', '/reference:System.Net.Http.dll', '/reference:System.Runtime.Serialization.dll', "$root\tests\PlaybackTests.cs")
    & "$out\Shell.PlaybackTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '正片协议回归失败。' }
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.EmbyPlaybackLive.exe", "/reference:$out\Emby.dll", '/reference:System.Net.Http.dll', "$root\tests\EmbyPlaybackLive.cs") + $refs)
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.ExperienceLive.exe", "/reference:$out\Emby.dll", '/reference:System.Net.Http.dll', "$root\tests\ExperienceLive.cs") + $refs)
    & "$out\Shell.EmbyTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Emby 离线回归失败。' }
}
if (-not $PluginOnly) {
    & "$root\scripts\restore-video.ps1"
    & "$out\Shell.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw '回归测试失败。' }
    # 测试进程退出后才能移除其装载过的 DLL，目标固定在构建区。
    $fixtures = [IO.Path]::GetFullPath((Join-Path $out 'test-plugin-fixtures'))
    if (-not $fixtures.StartsWith([IO.Path]::GetFullPath($out) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径越界。' }
    if (Test-Path -LiteralPath $fixtures) { Remove-Item -LiteralPath $fixtures -Recurse -Force }
    & "$out\Shell.AdapterTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '适配器回归失败。' }
    & "$out\Shell.ControlSmoke.exe"
    if ($LASTEXITCODE -ne 0) { throw 'WPF 控件冒烟失败。' }
    & "$out\Shell.WpfTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '主题和媒体测试失败。' }
    Copy-Item "$out\plugins\Achievements\Achievements.dll" "$out\Achievements.dll" -Force
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.TrophyViewTests.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\Achievements.dll", "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll", '/reference:System.Xaml.dll', "$root\tests\TrophyViewTests.cs")
    & "$out\Shell.TrophyViewTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '奖杯详情视口验证失败。' }
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.Ps5ExperienceTests.exe", "/reference:$PlaynitePath\Playnite.SDK.dll", "/reference:$out\Shell.Playnite.dll", '/reference:System.Runtime.Serialization.dll', "$root\tests\Ps5ExperienceTests.cs") + $refs)
    & "$root\scripts\test-video.ps1"
    & "$out\Shell.VideoTests.x86.exe"
    if ($LASTEXITCODE -ne 0) { throw '32 位宿主视频解码回归失败。' }
    Invoke-Compiler (@('/target:exe', "/out:$out\Shell.QuickPanelTests.exe", "/reference:$out\Shell.QuickPanel.exe", '/reference:System.Runtime.Serialization.dll', "$root\tests\QuickPanelTests.cs", "$root\src\Playnite\QuickPanelBridge.cs") + $refs)
    & "$out\Shell.QuickPanelTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '独立面板回归失败。' }
    Copy-Item "$out\plugins\LocalAssets\LocalAssets.dll" "$out\LocalAssets.dll" -Force
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.AutomaticAssetsTests.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\LocalAssets.dll", '/reference:System.Drawing.dll', "$root\tests\AutomaticAssetsTests.cs")
    Invoke-Compiler @('/target:exe', "/out:$out\Shell.AutomaticAssetsLive.exe", "/reference:$out\Shell.Contracts.dll", "/reference:$out\LocalAssets.dll", '/reference:System.Runtime.Serialization.dll', "$root\tests\AutomaticAssetsLive.cs")
    & "$out\Shell.AutomaticAssetsTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '自动素材质量回归失败。' }
    & "$root\scripts\test-assets.ps1"
    & "$root\scripts\test-config-snapshot.ps1"
    & "$root\scripts\test-cache.ps1"
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -File "$root\scripts\maintenance-ui.ps1" -UserDataRoot "$out\maintenance-smoke-unused" -Smoke -CapturePath "$out\maintenance-ui.png"
    if ($LASTEXITCODE -ne 0) { throw '维护界面冒烟失败。' }
    & "$root\scripts\package.ps1"
}











