param(
    [Parameter(Mandatory=$true)][ValidateSet('Add','Audit','Prune','SetLock')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$UserDataRoot,
    [string]$SourcePath,
    [string]$CachePath,
    [ValidateRange(1,1048576)][int]$MaxMiB = 1024,
    [switch]$Locked
)
$ErrorActionPreference = 'Stop'
$dataRoot = [IO.Path]::GetFullPath($UserDataRoot)
$cacheRoot = Join-Path $dataRoot 'media-cache'
$manifestPath = Join-Path $cacheRoot 'index.json'
$limit = [long]$MaxMiB * 1MB

# 管理范围固定为 SDK 用户目录下的 media-cache，所有现有祖先均不得为链接。
function Assert-Plain([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "拒绝链接路径：$cursor" }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

# 清单必须为脚本生成的哈希文件名，拒绝目录、重复和越界条目。
function Read-Cache {
    if (-not (Test-Path -LiteralPath $manifestPath)) { return @() }
    Assert-Plain $manifestPath
    $value = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($value.schema -ne 1 -or $value.items -isnot [Array]) { throw '缓存清单版本错误。' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $value.items) {
        if ($entry.name -notmatch '^[A-Fa-f0-9]{64}\.(mp4|mkv|webm|mov|avi|wav|mp3|flac|ogg|m4a)$' -or -not $seen.Add($entry.name) -or $entry.locked -isnot [bool]) { throw '缓存清单条目非法。' }
        Assert-Plain (Join-Path $cacheRoot $entry.name)
    }
    return @($value.items)
}

# 清单原子更新；清理失败时保留原记录，下一次审计仍能定位遗留文件。
function Save-Cache($Entries) {
    Assert-Plain ($manifestPath+'.bak')
    $temporary = $manifestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [ordered]@{schema=1; items=@($Entries)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
        if (Test-Path -LiteralPath $manifestPath) { [IO.File]::Replace($temporary,$manifestPath,$manifestPath+'.bak') } else { [IO.File]::Move($temporary,$manifestPath) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

Assert-Plain $cacheRoot
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
# 独占租约使两个维护进程不能同时淘汰或改写清单。
$leasePath = Join-Path $cacheRoot 'maintenance.lock'
Assert-Plain $leasePath
$lease = [IO.File]::Open($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $entries = @(Read-Cache)
    # 锁定修改只能命中当前已登记缓存，不接受任意磁盘路径。
    if ($Mode -eq 'SetLock') {
        if ([string]::IsNullOrWhiteSpace($CachePath)) { throw '未选择缓存。' }
        $chosen = [IO.Path]::GetFullPath($CachePath)
        $matches = @($entries | Where-Object { (Join-Path $cacheRoot $_.name) -eq $chosen })
        if ($matches.Count -ne 1) { throw '所选路径不属于已登记缓存。' }
        $matches[0].locked = [bool]$Locked
        Save-Cache $entries
        return
    }
    $references = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $assetIndex = Join-Path $dataRoot 'assets.json'
    if (Test-Path -LiteralPath $assetIndex) {
        Assert-Plain $assetIndex
        $assets = Get-Content -LiteralPath $assetIndex -Raw | ConvertFrom-Json
        if ($assets.schema -ne 1 -or $assets.items -isnot [Array]) { throw '素材索引不可读，停止缓存清理。' }
        foreach ($item in $assets.items) { foreach ($kind in @('music','video')) { if ($item.$kind) { $null = $references.Add([IO.Path]::GetFullPath([string]$item.$kind)) } } }
    }
    $total = 0L
    foreach ($entry in $entries) {
        $path = Join-Path $cacheRoot $entry.name
        $bytes = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-Item -LiteralPath $path).Length } else { 0L }
        $total += $bytes
        if ($Mode -eq 'Audit') {
            [pscustomobject]@{path=$path; bytes=$bytes; locked=$entry.locked; referenced=$references.Contains($path); source=$entry.source; importedUtc=$entry.importedUtc; exists=(Test-Path -LiteralPath $path -PathType Leaf)}
        }
    }
    if ($Mode -eq 'Audit') { return }
    $incoming = 0L
    $destination = $null
    if ($Mode -eq 'Add') {
        if ([string]::IsNullOrWhiteSpace($SourcePath) -or $SourcePath -notmatch '^[A-Za-z]:[\\/]') { throw '必须提供本地绝对素材路径。' }
        $source = [IO.Path]::GetFullPath($SourcePath)
        Assert-Plain $source
        $file = Get-Item -LiteralPath $source
        if ($file.PSIsContainer -or $file.Extension -notmatch '^\.(mp4|mkv|webm|mov|avi|wav|mp3|flac|ogg|m4a)$') { throw '不支持的本地素材类型。' }
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $name = $hash + $file.Extension.ToLowerInvariant()
        $destination = Join-Path $cacheRoot $name
        if (@($entries | Where-Object name -eq $name).Count -gt 0 -and (Test-Path -LiteralPath $destination)) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne $hash) { throw '已有缓存内容被修改，请审计后处理。' }
            if ($Locked) { foreach ($entry in $entries) { if ($entry.name -eq $name) { $entry.locked = $true } }; Save-Cache $entries }
            Write-Output $destination
            return
        }
        if (Test-Path -LiteralPath $destination) { throw '存在未登记的同名缓存，拒绝覆盖。' }
        $incoming = $file.Length
        if ($incoming -gt $limit) { throw '单个素材超过缓存限额，未修改缓存。' }
    }
    # FIFO 淘汰仅适用于未锁定且未被当前素材索引引用的登记文件。
    $candidates = @($entries | Where-Object { -not $_.locked -and -not $references.Contains((Join-Path $cacheRoot $_.name)) } | Sort-Object importedUtc)
    $evict = @()
    foreach ($entry in $candidates) {
        if ($total + $incoming -le $limit) { break }
        $path = Join-Path $cacheRoot $entry.name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $total -= (Get-Item -LiteralPath $path).Length }
        $evict += $entry.name
    }
    if ($total + $incoming -gt $limit) { throw '锁定或正在引用的缓存占满额度；未执行淘汰。' }
    $temporary = Join-Path $cacheRoot ([Guid]::NewGuid().ToString('N')+'.tmp')
    $quarantine = @()
    $newFileMoved = $false
    try {
        # 先验证完整副本，磁盘满或源文件变化不会提前删除已有缓存。
        if ($Mode -eq 'Add') {
            Copy-Item -LiteralPath $source -Destination $temporary
            if ((Get-FileHash -LiteralPath $temporary).Hash -ne $hash -or (Get-Item -LiteralPath $temporary).Length -ne $incoming) { throw '源素材在复制时发生变化。' }
        }
        foreach ($nameToRemove in $evict) {
            $path = [IO.Path]::GetFullPath((Join-Path $cacheRoot $nameToRemove))
            if (-not $path.StartsWith($cacheRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '清理路径越界。' }
            Assert-Plain $path
            if (Test-Path -LiteralPath $path) {
                $held = $path + '.' + [Guid]::NewGuid().ToString('N') + '.pending-delete'
                [IO.File]::Move($path,$held)
                $quarantine += [pscustomobject]@{original=$path; held=$held}
            }
        }
        $entries = @($entries | Where-Object { $evict -notcontains $_.name })
        if ($Mode -eq 'Add') {
            [IO.File]::Move($temporary,$destination)
            $newFileMoved = $true
            $entries = @($entries | Where-Object name -ne $name)
            $entries += [pscustomobject]@{name=$name; source=$source; importedUtc=[DateTime]::UtcNow.ToString('o'); locked=[bool]$Locked}
        }
        Save-Cache $entries
    } catch {
        # 清单提交前保留可逆改名；正常异常时恢复旧缓存和索引。
        if ($newFileMoved) { Remove-Item -LiteralPath $destination }
        foreach ($held in $quarantine) { [IO.File]::Move($held.held,$held.original) }
        throw
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    foreach ($held in $quarantine) { Remove-Item -LiteralPath $held.held }
    if ($Mode -eq 'Add') { Write-Output $destination }
} finally { $lease.Dispose() }
