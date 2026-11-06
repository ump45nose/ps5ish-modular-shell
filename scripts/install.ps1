param([Parameter(Mandatory=$true)][string]$ExtensionsPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$id = 'ModularShell_f13fdbe6-52ba-4198-8bea-457be1a609cf'
$parent = (Resolve-Path -LiteralPath $ExtensionsPath).Path
$target = Join-Path $parent $id
if (Test-Path -LiteralPath $target) { throw '目标已存在，本脚本只执行首次安装，不覆盖既有扩展。' }
if ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '扩展根目录不能是链接。' }
. "$PSScriptRoot\version.ps1"
$package = Join-Path $root "artifacts\ModularShell-$shellVersion.pext"
Add-Type -AssemblyName System.IO.Compression.FileSystem
# 先校验压缩包全部入口，再写入专属目录；不触碰 Playnite 配置和其他插件。
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $prefix = [IO.Path]::GetFullPath($target) + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $archive.Entries) {
        $resolved = [IO.Path]::GetFullPath((Join-Path $target $entry.FullName))
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '安装包路径越界。' }
    }
} finally { $archive.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($package, $target)
# 验证所有 DLL 与构建产物相同，记录本次安装范围供回退核对。
foreach ($name in @('Shell.Playnite.dll','Shell.Contracts.dll','Shell.Core.dll')) {
    if ((Get-FileHash (Join-Path $target $name)).Hash -ne (Get-FileHash (Join-Path "$root\artifacts" $name)).Hash) { throw "安装后校验失败：$name" }
}
$receipt = [ordered]@{ installedAt = (Get-Date).ToString('o'); target = $target; packageHash = (Get-FileHash $package).Hash; files = @(Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object { [ordered]@{ path = $_.FullName; sha256 = (Get-FileHash $_.FullName).Hash } }) }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$root\artifacts\installation-receipt.json" -Encoding utf8
Write-Output "已安装到 $target；运行中的 Playnite 需在下次正常启动时加载。"

