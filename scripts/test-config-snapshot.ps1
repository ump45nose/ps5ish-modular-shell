$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$testRoot = [IO.Path]::GetFullPath((Join-Path $project ('artifacts\config-tests-' + [Guid]::NewGuid().ToString('N'))))
$data = Join-Path $testRoot 'data'
$snapshot = Join-Path $testRoot 'saved'
New-Item -ItemType Directory -Path $data -Force | Out-Null
try {
    # 字节级回滚必须保留未知字段，且不触碰用户媒体或未在快照中的配置。
    '{"schema":1,"palette":"ember","custom":"保留"}' | Set-Content (Join-Path $data 'theme.json') -Encoding UTF8
    'user media' | Set-Content (Join-Path $data 'user.wav')
    $original = (Get-FileHash (Join-Path $data 'theme.json')).Hash
    & "$PSScriptRoot\config-snapshot.ps1" -Mode Backup -UserDataRoot $data -SnapshotPath $snapshot
    '{"schema":1,"palette":"midnight"}' | Set-Content (Join-Path $data 'theme.json')
    '{"schema":1,"music":false}' | Set-Content (Join-Path $data 'preview.json')
    & "$PSScriptRoot\config-snapshot.ps1" -Mode Restore -UserDataRoot $data -SnapshotPath $snapshot
    if ((Get-FileHash (Join-Path $data 'theme.json')).Hash -ne $original -or -not (Test-Path (Join-Path $data 'user.wav')) -or -not (Test-Path (Join-Path $data 'preview.json'))) { throw '恢复范围或原始字节不正确。' }
    'PASS 配置快照恢复保留原始字节及其他文件'
    $recovery = @(Get-ChildItem (Join-Path $data 'config-snapshots') -Directory)
    if ($recovery.Count -ne 1 -or (Get-Content (Join-Path $recovery[0].FullName 'theme.json') -Raw | ConvertFrom-Json).palette -ne 'midnight') { throw '恢复前备份缺失。' }
    'PASS 恢复操作保留当前配置快照'
    # 哈希污染必须在任何写入之前失败。
    '{"schema":1,"palette":"midnight"}' | Set-Content (Join-Path $snapshot 'theme.json')
    $rejected = $false
    try { & "$PSScriptRoot\config-snapshot.ps1" -Mode Restore -UserDataRoot $data -SnapshotPath $snapshot } catch { $rejected = $true }
    if (-not $rejected -or (Get-FileHash (Join-Path $data 'theme.json')).Hash -ne $original) { throw '污染快照改变当前配置。' }
    'PASS 哈希污染快照被拒绝且配置未变化'
    $manifest = Get-Content (Join-Path $snapshot 'snapshot.json') -Raw | ConvertFrom-Json
    $manifest.files[0].name = '..\user.wav'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $snapshot 'snapshot.json')
    $rejected = $false
    try { & "$PSScriptRoot\config-snapshot.ps1" -Mode Restore -UserDataRoot $data -SnapshotPath $snapshot } catch { $rejected = $true }
    if (-not $rejected) { throw '越界清单未被拒绝。' }
    'PASS 非白名单快照路径被拒绝'

    # 参数指定故障类型；每个案例使用独立数据，验证拒绝发生于写入之前。
    function Test-RejectedSnapshot([string]$Fault) {
        $caseRoot = Join-Path $testRoot $Fault
        $caseData = Join-Path $caseRoot 'data'
        $caseSnapshot = Join-Path $caseRoot 'saved'
        New-Item -ItemType Directory -Path $caseData -Force | Out-Null
        $config = Join-Path $caseData 'theme.json'
        '{"schema":1,"custom":"原始配置"}' | Set-Content -LiteralPath $config -Encoding UTF8
        & "$PSScriptRoot\config-snapshot.ps1" -Mode Backup -UserDataRoot $caseData -SnapshotPath $caseSnapshot | Out-Null
        $manifestFile = Join-Path $caseSnapshot 'snapshot.json'
        $caseManifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
        switch ($Fault) {
            'future-manifest' { $caseManifest.schema = 2 }
            'duplicate-entry' { $caseManifest.files = @($caseManifest.files[0],$caseManifest.files[0]) }
            'missing-file' { Remove-Item -LiteralPath (Join-Path $caseSnapshot 'theme.json') }
            'future-config' {
                '{"schema":2}' | Set-Content -LiteralPath (Join-Path $caseSnapshot 'theme.json')
                $caseManifest.files[0].sha256 = (Get-FileHash -LiteralPath (Join-Path $caseSnapshot 'theme.json')).Hash
            }
            'invalid-json' {
                '{broken' | Set-Content -LiteralPath (Join-Path $caseSnapshot 'theme.json')
                $caseManifest.files[0].sha256 = (Get-FileHash -LiteralPath (Join-Path $caseSnapshot 'theme.json')).Hash
            }
        }
        $caseManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestFile -Encoding UTF8
        $before = (Get-FileHash -LiteralPath $config).Hash
        $failed = $false
        try { & "$PSScriptRoot\config-snapshot.ps1" -Mode Restore -UserDataRoot $caseData -SnapshotPath $caseSnapshot | Out-Null } catch { $failed = $true }
        if (-not $failed -or (Get-FileHash -LiteralPath $config).Hash -ne $before -or (Test-Path -LiteralPath (Join-Path $caseData 'config-snapshots'))) { throw "故障未在写入前拒绝：$Fault" }
        "PASS 配置恢复故障拒绝 $Fault"
    }
    foreach ($fault in @('future-manifest','duplicate-entry','missing-file','future-config','invalid-json')) { Test-RejectedSnapshot $fault }

    # 参数决定首项原先是否存在；真实文件共享锁使第二项替换失败，而备份仍能读取。
    function Test-PartialRestore([bool]$ExistingFirst) {
        $caseRoot = Join-Path $testRoot ('partial-' + $ExistingFirst)
        $caseData = Join-Path $caseRoot 'data'
        $caseSnapshot = Join-Path $caseRoot 'saved'
        New-Item -ItemType Directory -Path $caseData -Force | Out-Null
        $first = Join-Path $caseData 'preview.json'
        $second = Join-Path $caseData 'theme.json'
        '{"schema":1,"music":true}' | Set-Content -LiteralPath $first
        '{"schema":1,"palette":"ember"}' | Set-Content -LiteralPath $second
        & "$PSScriptRoot\config-snapshot.ps1" -Mode Backup -UserDataRoot $caseData -SnapshotPath $caseSnapshot | Out-Null
        # 损坏的旧配置也必须按原始字节恢复，不要求旧配置可解析。
        if ($ExistingFirst) { '{broken old data' | Set-Content -LiteralPath $first; $firstHash = (Get-FileHash -LiteralPath $first).Hash }
        else { Remove-Item -LiteralPath $first }
        '{"schema":1,"palette":"midnight"}' | Set-Content -LiteralPath $second
        $secondHash = (Get-FileHash -LiteralPath $second).Hash
        $media = Join-Path $caseData 'manual.wav'
        'user-owned-media' | Set-Content -LiteralPath $media
        $mediaHash = (Get-FileHash -LiteralPath $media).Hash
        $handle = [IO.File]::Open($second,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $failure = ''
        try {
            try { & "$PSScriptRoot\config-snapshot.ps1" -Mode Restore -UserDataRoot $caseData -SnapshotPath $caseSnapshot | Out-Null }
            catch { $failure = $_.Exception.Message }
        } finally { $handle.Dispose() }
        if ($failure -notlike '*已撤销本次配置修改*') { throw "未触发预期中途回滚：$failure" }
        if ($ExistingFirst) {
            if ((Get-FileHash -LiteralPath $first).Hash -ne $firstHash) { throw '旧配置字节未恢复。' }
        } elseif (Test-Path -LiteralPath $first) { throw '本次新建配置未撤销。' }
        if ((Get-FileHash -LiteralPath $second).Hash -ne $secondHash -or (Get-FileHash -LiteralPath $media).Hash -ne $mediaHash) { throw '未提交配置或手动素材被修改。' }
        if (@(Get-ChildItem -LiteralPath $caseData -File -Filter '*.tmp*').Count) { throw '恢复暂存文件残留。' }
        $saved = @(Get-ChildItem -LiteralPath (Join-Path $caseData 'config-snapshots') -Directory)
        if ($saved.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $saved[0].FullName 'snapshot.json'))) { throw '恢复前快照缺失。' }
        "PASS 中途替换失败撤销并保留素材 ExistingFirst=$ExistingFirst"
    }
    Test-PartialRestore $true
    Test-PartialRestore $false
} finally {
    # 删除前确认最终路径始终位于本项目固定构建区。
    if (-not $testRoot.StartsWith([IO.Path]::GetFullPath((Join-Path $project 'artifacts'))+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '测试清理越界。' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
