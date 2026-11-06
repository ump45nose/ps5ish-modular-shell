param([switch]$X86)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'artifacts'
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpf = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF"
# 独立测试解析目录，不进入安装包；与宿主一样使用同一份插件二进制。
Copy-Item "$out\plugins\Emby\Microsoft.Web.WebView2.Core.dll", "$out\plugins\Emby\Microsoft.Web.WebView2.Wpf.dll" $out -Force
Copy-Item "$out\plugins\Emby\runtimes" $out -Recurse -Force
$platform = if ($X86) { 'x86' } else { 'anycpu' }
& $compiler /nologo "/platform:$platform" /target:exe "/out:$out\Shell.WebLibrarySmoke.exe" "/reference:$out\Shell.Contracts.dll" "/reference:$out\Emby.dll" "/reference:$out\Microsoft.Web.WebView2.Core.dll" "/reference:$out\Microsoft.Web.WebView2.Wpf.dll" "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll /reference:System.Drawing.dll "$root\tests\WebLibrarySmoke.cs"
if ($LASTEXITCODE -ne 0) { throw '网页验证程序编译失败。' }
& "$out\Shell.WebLibrarySmoke.exe"
if ($LASTEXITCODE -ne 0) { throw '网页验证失败。' }
