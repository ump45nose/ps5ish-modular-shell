param([string]$Catalog)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=Join-Path $root 'artifacts'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf=Join-Path (Split-Path $compiler) 'WPF'
$refs=@('/reference:System.Xaml.dll','/reference:System.Runtime.Serialization.dll',"/reference:$out\Shell.Contracts.dll","/reference:$out\Shell.Core.dll","/reference:$out\Shell.Playnite.dll","/reference:$out\Playnite.SDK.dll","/reference:$out\Achievements.dll","/reference:$wpf\PresentationFramework.dll","/reference:$wpf\PresentationCore.dll","/reference:$wpf\WindowsBase.dll")
# 固定构建目录串行编译，输出占用仅重试一次。
foreach($name in @('TrophyViewTests','Ps5ExperienceTests')){
    for($attempt=0;$attempt -lt 2;$attempt++){
        $messages=@(& $compiler /nologo "/out:$out\Shell.$name.exe" "$root\tests\$name.cs" @refs 2>&1)
        $messages | Write-Output
        if($LASTEXITCODE -eq 0){break}
        if($attempt -eq 0 -and ($messages -join "`n") -match 'CS2012|CS0042'){continue}
        throw '专项编译失败；停止，不更换构建目录。'
    }
}
& "$out\Shell.TrophyViewTests.exe"
if($LASTEXITCODE -ne 0){throw '奖杯布局专项失败'}
& "$out\Shell.Ps5ExperienceTests.exe" $Catalog
if($LASTEXITCODE -ne 0){throw 'PS5 页面专项失败'}
