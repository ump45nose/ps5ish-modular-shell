param([string]$PlaynitePath = "$env:LOCALAPPDATA\Playnite")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = [IO.Path]::GetFullPath((Join-Path $PlaynitePath 'Themes\Fullscreen\Default'))
$main = Join-Path $source 'Views\Main.xaml'
if (-not (Test-Path -LiteralPath $main)) { throw '未找到本机 Default 全屏基线。' }
[xml]$document = Get-Content -LiteralPath $main -Raw
$ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$hostNode = $document.SelectSingleNode('//*[@x:Name="PART_MainHost"]', $ns)
$viewNode = $document.SelectSingleNode('//*[@x:Name="PART_ViewHost"]', $ns)
if ($null -eq $hostNode -or $null -eq $viewNode -or $viewNode.ParentNode -ne $hostNode) { throw '基线结构变化，停止注入。' }
# 未能注入时保留原生列表，不创建第三方 CLR 类型依赖。
$fragment = $document.CreateDocumentFragment()
$fragment.InnerXml = '<ContentControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="ModularShell_Browser" HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch" Focusable="False" />'
$hostNode.InsertAfter($fragment, $viewNode) | Out-Null
$visibility = $document.CreateDocumentFragment()
$visibility.InnerXml = '<Grid.Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Style TargetType="Grid"><Setter Property="Visibility" Value="Collapsed"/><Style.Triggers><DataTrigger Binding="{Binding Content, ElementName=ModularShell_Browser}" Value="{x:Null}"><Setter Property="Visibility" Value="Visible"/></DataTrigger></Style.Triggers></Style></Grid.Style>'
$viewNode.PrependChild($visibility) | Out-Null
# WPF 属性元素不接受重复的命名空间声明；继承文档根的同一命名空间。
foreach($node in @($document.SelectNodes('//*'))){
    if($node -ne $document.DocumentElement){
        foreach($attribute in @($node.Attributes)){if($attribute.Prefix -eq 'xmlns' -or $attribute.Name -eq 'xmlns'){$node.RemoveAttributeNode($attribute) | Out-Null}}
    }
}
# 只在固定构建区生成候选包，不改当前主题和 Playnite 配置。
$out = Join-Path $root 'artifacts'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$path = Join-Path $out 'ModularShell-Fullscreen-0.8.0.pthm'
$stream = [IO.File]::Open($path, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @(Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName)) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '拒绝链接主题文件。' }
        $name = $file.FullName.Substring($source.Length + 1).Replace('\','/')
        $entry = $zip.CreateEntry($name)
        $target = $entry.Open()
        try {
            if ($name -eq 'Views/Main.xaml') { $document.Save($target) }
            elseif ($name -eq 'theme.yaml') {
                $text = "Id: ModularShell_Fullscreen_80b793c3-a46b-4702-9249-2105bc2c3029`nName: Modular Shell · PS5`nAuthor: ump45nose; Playnite baseline`nVersion: 0.8.0`nMode: Fullscreen`nThemeApiVersion: 2.0.0`n"
                $bytes = [Text.Encoding]::UTF8.GetBytes($text); $target.Write($bytes,0,$bytes.Length)
            } else { $inputFile=[IO.File]::OpenRead($file.FullName); try {$inputFile.CopyTo($target)} finally {$inputFile.Dispose()} }
        } finally { $target.Dispose() }
    }
    # PA 可直接加载独立主题覆盖文件；未安装 PA 时不引入 CLR 资源依赖。
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'themes\PlayniteAchievements\AchievementToast.xaml'), 'PlayniteAchievements/AchievementToast.xaml') | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'third-party\Playnite-MIT.txt'), 'Playnite-MIT.txt') | Out-Null
} finally { $zip.Dispose(); $stream.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($path)
try {
    $reader=[IO.StreamReader]::new($check.GetEntry('Views/Main.xaml').Open())
    try {[xml]$verified=$reader.ReadToEnd()} finally {$reader.Dispose()}
    $names=@($verified.SelectNodes('//*[@x:Name]', $ns) | ForEach-Object {$_.GetAttribute('Name','http://schemas.microsoft.com/winfx/2006/xaml')})
    if (@($names | Where-Object {$_ -eq 'ModularShell_Browser'}).Count -ne 1 -or $names -notcontains 'PART_ListGameItems') {throw '注入或原生回退槽位丢失'}
} finally {$check.Dispose()}
[ordered]@{source=$source; mainSha256=(Get-FileHash $main).Hash; package=$path; packageSha256=(Get-FileHash $path).Hash; verification='XML and fallback slots only; not activated'} | ConvertTo-Json | Set-Content (Join-Path $out 'fullscreen-theme-receipt.json') -Encoding UTF8
Write-Output "已生成未启用的全屏候选包：$path"
