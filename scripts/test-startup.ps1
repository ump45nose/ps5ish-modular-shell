param([Parameter(Mandatory=$true)][string]$Video)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=Join-Path $root 'artifacts'
$framework="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf=Join-Path $framework 'WPF'
# 串行编译到既定artifacts，若发生占用仅原地重试一次。
for($attempt=0;$attempt -lt 2;$attempt++){
    $messages=@(& "$framework\csc.exe" /nologo /target:exe "/out:$out\Shell.StartupLive.exe" "/reference:$out\Shell.Contracts.dll" "/reference:$out\Shell.Core.dll" "/reference:$out\Shell.Playnite.dll" "/reference:$out\Playnite.SDK.dll" "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll "$root\tests\StartupLive.cs" 2>&1)
    $code=$LASTEXITCODE;$messages|Write-Output
    if($code -eq 0){break}
    if($attempt -eq 0 -and ($messages -join "`n") -match 'CS2012|CS0042'){continue}
    throw '启屏测试编译失败，停止构建'
}
& "$out\Shell.StartupLive.exe" $Video
if($LASTEXITCODE -ne 0){throw '启屏实文件测试失败'}
