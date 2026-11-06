$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$testRoot = [IO.Path]::GetFullPath((Join-Path $root ('artifacts\asset-tests-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    '{"schema":1,"items":[{"provider":"playnite","sourceInstance":"local","itemId":"1","music":"old.wav","locked":true}]}' | Set-Content (Join-Path $testRoot 'assets.json')
    '{"schema":1,"items":[{"provider":"playnite","sourceInstance":"local","itemId":"1","music":"new.wav"},{"provider":"emby","sourceInstance":"server","itemId":"1","video":"movie.mp4"}]}' | Set-Content (Join-Path $testRoot 'incoming.json')
    & "$PSScriptRoot\assets.ps1" -Mode Import -UserDataRoot $testRoot -IndexPath (Join-Path $testRoot 'incoming.json')
    $result = Get-Content (Join-Path $testRoot 'assets.json') -Raw | ConvertFrom-Json
    if ($result.items.Count -ne 2 -or $result.items[0].music -ne 'old.wav' -or -not (Test-Path (Join-Path $testRoot 'assets.json.bak'))) { throw '锁定覆盖保护或备份失败。' }
    'PASS 素材导入保护锁定选择和原索引备份'
    & "$PSScriptRoot\assets.ps1" -Mode Export -UserDataRoot $testRoot -IndexPath (Join-Path $testRoot 'export.json')
    if ((Get-FileHash (Join-Path $testRoot 'export.json')).Hash -ne (Get-FileHash (Join-Path $testRoot 'assets.json')).Hash) {throw '导出不一致。'}
    'PASS 素材导出保留原始索引'
    $before=(Get-FileHash (Join-Path $testRoot 'assets.json')).Hash
    '{"schema":1,"items":[{"provider":"p","sourceInstance":"s","itemId":"1","volume":2}]}' | Set-Content (Join-Path $testRoot 'bad.json')
    $rejected=$false
    try { & "$PSScriptRoot\assets.ps1" -Mode Import -UserDataRoot $testRoot -IndexPath (Join-Path $testRoot 'bad.json') } catch {$rejected=$true}
    if (-not $rejected -or $before -ne (Get-FileHash (Join-Path $testRoot 'assets.json')).Hash) {throw '坏索引改变了当前配置。'}
    'PASS 非法音量导入保留当前索引'
} finally {
    # 仅清理本测试创建的固定构建区子目录。
    if (-not $testRoot.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + '\',[StringComparison]::OrdinalIgnoreCase)) {throw '测试清理越界。'}
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
