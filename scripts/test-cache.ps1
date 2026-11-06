$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$testRoot = [IO.Path]::GetFullPath((Join-Path $project ('artifacts\cache-tests-' + [Guid]::NewGuid().ToString('N'))))
$data = Join-Path $testRoot 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
try {
    # 用不同二进制内容测试哈希去重与限额，无需实际音频解码。
    $first = Join-Path $testRoot 'first.wav'
    $second = Join-Path $testRoot 'second.wav'
    $bytes = New-Object byte[] 700000
    [IO.File]::WriteAllBytes($first,$bytes)
    $bytes[0] = 1
    [IO.File]::WriteAllBytes($second,$bytes)
    $one = & "$PSScriptRoot\cache.ps1" -Mode Add -UserDataRoot $data -SourcePath $first -MaxMiB 1
    $duplicate = & "$PSScriptRoot\cache.ps1" -Mode Add -UserDataRoot $data -SourcePath $first -MaxMiB 1
    if ($one -ne $duplicate -or @( & "$PSScriptRoot\cache.ps1" -Mode Audit -UserDataRoot $data).Count -ne 1) { throw '缓存去重失败。' }
    'PASS 相同内容复用缓存并记录本地来源'
    [ordered]@{schema=1; items=@(@{provider='p';sourceInstance='s';itemId='1';music=$one})} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data 'assets.json')
    $rejected = $false
    try { & "$PSScriptRoot\cache.ps1" -Mode Add -UserDataRoot $data -SourcePath $second -MaxMiB 1 } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $one)) { throw '引用中的缓存被清理。' }
    'PASS 正在引用的缓存满额时拒绝新素材'
    '{"schema":1,"items":[]}' | Set-Content (Join-Path $data 'assets.json')
    # 强制最终索引替换失败，核验已暂存的淘汰文件会恢复。
    $indexFile = Join-Path $data 'media-cache\index.json'
    $indexHash = (Get-FileHash -LiteralPath $indexFile).Hash
    $handle = [IO.File]::Open($indexFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $rejected = $false
    try { & "$PSScriptRoot\cache.ps1" -Mode Add -UserDataRoot $data -SourcePath $second -MaxMiB 1 } catch { $rejected = $true } finally { $handle.Dispose() }
    if (-not $rejected -or -not (Test-Path -LiteralPath $one) -or (Get-FileHash -LiteralPath $indexFile).Hash -ne $indexHash) { throw '提交失败没有恢复原缓存。' }
    'PASS 清单提交失败恢复淘汰文件并保留旧索引'
    $two = & "$PSScriptRoot\cache.ps1" -Mode Add -UserDataRoot $data -SourcePath $second -MaxMiB 1 -Locked
    if ((Test-Path -LiteralPath $one) -or -not (Test-Path -LiteralPath $two) -or -not (Test-Path -LiteralPath $first)) { throw '限额淘汰范围错误。' }
    'PASS 超额仅淘汰未引用缓存并保留用户源文件'
    $rejected = $false
    try { & "$PSScriptRoot\cache.ps1" -Mode Add -UserDataRoot $data -SourcePath $first -MaxMiB 1 } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $two)) { throw '锁定缓存被淘汰。' }
    'PASS 人工锁定缓存不会被限额淘汰'
    & "$PSScriptRoot\cache.ps1" -Mode SetLock -UserDataRoot $data -CachePath $two -Locked:$false
    $audit = @(& "$PSScriptRoot\cache.ps1" -Mode Audit -UserDataRoot $data)
    if ($audit[0].locked) { throw '已登记缓存解锁失败。' }
    $rejected = $false
    try { & "$PSScriptRoot\cache.ps1" -Mode SetLock -UserDataRoot $data -CachePath $first -Locked } catch { $rejected = $true }
    if (-not $rejected) { throw '缓存锁定接受了用户源路径。' }
    'PASS 可解锁登记缓存且拒绝操作用户源文件'
    '{broken' | Set-Content (Join-Path $data 'assets.json')
    $rejected = $false
    try { & "$PSScriptRoot\cache.ps1" -Mode Prune -UserDataRoot $data -MaxMiB 1 } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $two)) { throw '坏索引未阻止清理。' }
    'PASS 素材索引损坏时停止清理'
} finally {
    # 测试只删除固定构建区内自己创建的目录。
    if (-not $testRoot.StartsWith([IO.Path]::GetFullPath((Join-Path $project 'artifacts'))+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '测试清理越界。' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
