param([Parameter(Mandatory=$true)][string]$ExtensionsPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'artifacts'
$id = 'ModularShell_f13fdbe6-52ba-4198-8bea-457be1a609cf'
$parent = (Resolve-Path -LiteralPath $ExtensionsPath).Path
$target = [IO.Path]::GetFullPath((Join-Path $parent $id))
$receiptFile = Join-Path $out 'installation-receipt.json'
$receipt = Get-Content -LiteralPath $receiptFile -Raw | ConvertFrom-Json
if ($target -ne [IO.Path]::GetFullPath($receipt.target)) { throw '目标与已记录安装不一致。' }
if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '拒绝链接目录。' }
# 不覆盖已装入运行进程的程序集；下一次退出后仍可使用同一脚本更新。
foreach ($process in @(Get-Process Playnite* -ErrorAction SilentlyContinue)) {
    throw '请正常退出所有 Playnite 进程后更新，避免运行中的插件重新创建安装目录。'
    foreach ($module in $process.Modules) {
        if ($module.FileName.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '扩展已在运行，请在 Playnite 正常退出后更新。' }
    }
}
$currentFiles = @(Get-ChildItem -LiteralPath $target -Recurse -File)
if ($currentFiles.Count -ne $receipt.files.Count) { throw '安装内容已变化，停止更新以保护本地修改。' }
foreach ($file in $currentFiles) {
    $record = @($receipt.files | Where-Object { $_.path -eq $file.FullName })
    if ($record.Count -ne 1 -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $record[0].sha256) { throw "文件已修改：$($file.Name)" }
}
. "$PSScriptRoot\version.ps1"
$package = Join-Path $out "ModularShell-$shellVersion.pext"
$backupBase=Join-Path (Split-Path $parent) 'ModularShell-backups'
$backupRoot = [IO.Path]::GetFullPath((Join-Path $backupBase ('update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))))
$stage = Join-Path $backupRoot 'stage'
$previous = Join-Path $backupRoot 'extension'
$failed = Join-Path $backupRoot 'failed-new'
# 在任何目录移动前校验源/目标绝对路径，范围限定于明确的扩展目录与项目备份区。
if (-not $backupRoot.StartsWith([IO.Path]::GetFullPath($backupBase) + '\', [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $target -Leaf) -ne $id) { throw '更新路径校验失败。' }
New-Item -ItemType Directory -Path $backupRoot | Out-Null
Copy-Item -LiteralPath $receiptFile -Destination (Join-Path $backupRoot 'receipt.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $archive.Entries) {
        $path = [IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
        if (-not $path.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '包路径越界。' }
    }
} finally { $archive.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($package, $stage)
foreach ($name in @('Shell.Playnite.dll','Shell.Contracts.dll','Shell.Core.dll')) {
    if ((Get-FileHash (Join-Path $stage $name)).Hash -ne (Get-FileHash (Join-Path $out $name)).Hash) { throw '暂存产物校验失败。' }
}
[IO.Directory]::Move($target, $previous)
try {
    # Directory.Move 在目标被其他进程重建时明确失败，绝不把 stage 嵌套进目标。
    [IO.Directory]::Move($stage, $target)
    foreach ($required in @('extension.yaml','themes\layout.ps5ish.xaml','Shell.Playnite.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $required) -PathType Leaf)) { throw '安装根结构不完整。' }
    }
    $installed = @(Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object { $_.FullName.Substring($target.Length+1).Replace('\','/') })
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try { if (@(Compare-Object @($archive.Entries.FullName) $installed).Count) { throw '安装相对路径与包不一致。' } } finally { $archive.Dispose() }
    $next = [ordered]@{ installedAt = (Get-Date).ToString('o'); target = $target; backup = $backupRoot; packageHash = (Get-FileHash $package).Hash; files = @(Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object { [ordered]@{ path = $_.FullName; sha256 = (Get-FileHash $_.FullName).Hash } }) }
    $next | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptFile -Encoding utf8
} catch {
    # 失败产物移入同一备份区保留证据，再恢复完整旧目录和安装记录。
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $failed }
    Move-Item -LiteralPath $previous -Destination $target
    Copy-Item -LiteralPath (Join-Path $backupRoot 'receipt.json') -Destination $receiptFile -Force
    throw
}
Write-Output "已更新至 $shellVersion，旧版本与安装记录保存在 $backupRoot；未重启 Playnite。"

