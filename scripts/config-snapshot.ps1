param(
    [Parameter(Mandatory=$true)][ValidateSet('Backup','Restore')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$UserDataRoot,
    [Parameter(Mandatory=$true)][string]$SnapshotPath,
    [switch]$RequirePlayniteStopped
)
$ErrorActionPreference = 'Stop'
$dataRoot = [IO.Path]::GetFullPath($UserDataRoot)
$snapshot = [IO.Path]::GetFullPath($SnapshotPath)
$names = @('preview.json','theme.json','navigation.json','assets.json')

# 检查每一级现有目录，防止配置或快照借助目录链接写入其他位置。
function Assert-PlainPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "拒绝链接路径：$cursor" }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

# 快照保留原始字节和未知字段；仅接受当前支持的 JSON 版本。
function Assert-Config([string]$Path) {
    Assert-PlainPath $Path
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($null -eq $value -or $value.schema -ne 1) { throw "配置版本不受支持：$Path" }
}

# 所有输入先校验，再创建不可覆盖的快照目录及完整文件哈希清单。
function Save-Snapshot([string]$Destination, [bool]$Validate = $true) {
    Assert-PlainPath $Destination
    if (Test-Path -LiteralPath $Destination) { throw '快照目录已存在，拒绝覆盖。' }
    foreach ($name in $names) {
        $source = Join-Path $dataRoot $name
        if (Test-Path -LiteralPath $source) { Assert-PlainPath $source; if ($Validate) { Assert-Config $source } }
    }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    $entries = @()
    foreach ($name in $names) {
        $source = Join-Path $dataRoot $name
        if (Test-Path -LiteralPath $source) {
            $copy = Join-Path $Destination $name
            Copy-Item -LiteralPath $source -Destination $copy
            if ($Validate) { Assert-Config $copy }
            $entries += [ordered]@{name=$name; sha256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash}
        }
    }
    [ordered]@{schema=1; createdUtc=[DateTime]::UtcNow.ToString('o'); files=@($entries)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'snapshot.json') -Encoding UTF8
}

Assert-PlainPath $dataRoot
Assert-PlainPath $snapshot
if ($snapshot -eq $dataRoot -or $dataRoot.StartsWith($snapshot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '快照不能等于或包含用户配置目录。' }
if ($Mode -eq 'Backup') { Save-Snapshot $snapshot; Write-Output "配置快照已保存：$snapshot"; return }

# 恢复只处理白名单文件；全量验证后才允许触碰当前配置。
if ($RequirePlayniteStopped -and (Get-Process Playnite* -ErrorAction SilentlyContinue)) { throw 'Playnite 仍在运行，未恢复配置。' }
$manifestPath = Join-Path $snapshot 'snapshot.json'
Assert-PlainPath $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest -or $manifest.schema -ne 1 -or $manifest.files -isnot [Array]) { throw '快照清单不兼容。' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ($names -cnotcontains $entry.name -or -not $seen.Add($entry.name)) { throw '快照包含未知或重复文件。' }
    $source = Join-Path $snapshot $entry.name
    Assert-Config $source
    if ($entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "快照哈希不匹配：$($entry.name)" }
}
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$recovery = Join-Path $dataRoot ('config-snapshots\before-restore-' + [Guid]::NewGuid().ToString('N'))
Save-Snapshot $recovery $false
$applied = @()
try {
    foreach ($entry in $manifest.files) {
        if ($RequirePlayniteStopped -and (Get-Process Playnite* -ErrorAction SilentlyContinue)) { throw '恢复期间检测到 Playnite 启动，撤销本次恢复。' }
        $target = Join-Path $dataRoot $entry.name
        $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Copy-Item -LiteralPath (Join-Path $snapshot $entry.name) -Destination $temporary
            # 再验暂存文件，避免源快照在初次校验后发生变化。
            if ((Get-FileHash -LiteralPath $temporary).Hash -ne $entry.sha256) { throw '暂存配置校验失败。' }
            Assert-PlainPath $target
            if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary,$target,$temporary+'.previous') } else { [IO.File]::Move($temporary,$target) }
            $applied += $entry.name
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
            if (Test-Path -LiteralPath ($temporary+'.previous')) { Remove-Item -LiteralPath ($temporary+'.previous') }
        }
    }
} catch {
    $restoreFailure = $_
    $rollbackFailures = @()
    # 逆序撤销；单项回滚失败仍继续处理其余项，避免留下本可恢复的配置。
    for ($index = $applied.Count - 1; $index -ge 0; $index--) {
        $name = $applied[$index]
        $old = Join-Path $recovery $name
        $target = Join-Path $dataRoot $name
        $rollbackTemporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Assert-PlainPath $target
            if (Test-Path -LiteralPath $old) {
                # 从保留副本暂存并原子提交，回滚也不直接截断当前文件。
                Assert-PlainPath $old
                Copy-Item -LiteralPath $old -Destination $rollbackTemporary
                if ((Get-FileHash -LiteralPath $rollbackTemporary).Hash -ne (Get-FileHash -LiteralPath $old).Hash) { throw '回滚副本校验失败。' }
                if (Test-Path -LiteralPath $target) { [IO.File]::Replace($rollbackTemporary,$target,$rollbackTemporary+'.previous') }
                else { [IO.File]::Move($rollbackTemporary,$target) }
            } else { Remove-Item -LiteralPath $target }
        } catch { $rollbackFailures += ($name + '：' + $_.Exception.Message) }
        finally {
            # 清理异常同样收集，不应阻止后续文件回滚。
            try {
                if (Test-Path -LiteralPath $rollbackTemporary) { Remove-Item -LiteralPath $rollbackTemporary }
                if (Test-Path -LiteralPath ($rollbackTemporary+'.previous')) { Remove-Item -LiteralPath ($rollbackTemporary+'.previous') }
            }
            catch { $rollbackFailures += ($name + ' 临时文件：' + $_.Exception.Message) }
        }
    }
    if ($rollbackFailures.Count) { throw "恢复失败：$($restoreFailure.Exception.Message)；部分回滚失败：$($rollbackFailures -join '；')；恢复前快照：$recovery" }
    throw "恢复失败，已撤销本次配置修改：$($restoreFailure.Exception.Message)；恢复前快照：$recovery"
}
Write-Output "配置已恢复；恢复前快照：$recovery"
