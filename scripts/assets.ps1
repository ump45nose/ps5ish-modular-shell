param(
    [Parameter(Mandatory=$true)][ValidateSet('Audit','Export','Import')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$UserDataRoot,
    [string]$IndexPath
)
$ErrorActionPreference = 'Stop'
$dataRoot = [IO.Path]::GetFullPath($UserDataRoot)
$target = Join-Path $dataRoot 'assets.json'

# 验证完整引用和音量；不要求素材已在本机，缺文件由 Audit 单独列出。
function Read-AssetIndex([string]$Path) {
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($null -eq $value -or $value.schema -ne 1 -or $value.items -isnot [Array]) { throw '素材索引必须为 schema 1 和 items 数组。' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $value.items) {
        foreach ($key in @('provider','sourceInstance','itemId')) { if ([string]::IsNullOrWhiteSpace($item.$key)) { throw "内容引用缺少 $key" } }
        # JSON 编码的三元组避免分隔符出现在真实 ID 中产生碰撞。
        $identity = @($item.provider,$item.sourceInstance,$item.itemId) | ConvertTo-Json -Compress
        if (-not $seen.Add($identity)) { throw '内容引用重复，拒绝导入。' }
        foreach ($key in @('volume','videoVolume')) {
            if ($null -ne $item.$key) {
                $number = 0.0
                if (-not [double]::TryParse([string]$item.$key,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$number) -or [double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0 -or $number -gt 1) { throw "$key 超出 0 到 1。" }
            }
        }
        if ($null -ne $item.locked -and $item.locked -isnot [bool]) { throw 'locked 必须为布尔值。' }
    }
    return $value
}

if ($Mode -eq 'Audit') {
    $index = Read-AssetIndex $target
    foreach ($item in $index.items) {
        foreach ($kind in @('music','video')) {
            $path = [string]$item.$kind
            $state = if ([string]::IsNullOrWhiteSpace($path)) {'未指定'} elseif (-not [IO.Path]::IsPathRooted($path) -or $path.StartsWith('\\')) {'需要本地绝对路径'} elseif (Test-Path -LiteralPath $path -PathType Leaf) {'文件存在'} else {'文件缺失'}
            [pscustomobject]@{provider=$item.provider; sourceInstance=$item.sourceInstance; itemId=$item.itemId; kind=$kind; state=$state; path=$path}
        }
    }
    return
}
if ([string]::IsNullOrWhiteSpace($IndexPath)) { throw '导入/导出必须指定 -IndexPath。' }
$external = [IO.Path]::GetFullPath($IndexPath)
if ($external -eq $target) { throw '外部索引不能与当前索引相同。' }
if ($Mode -eq 'Export') {
    $null = Read-AssetIndex $target
    if (Test-Path -LiteralPath $external) { throw '导出目标已存在，拒绝覆盖。' }
    Copy-Item -LiteralPath $target -Destination $external
    return
}

$incoming = Read-AssetIndex $external
$merged = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '拒绝替换链接索引。' }
    $existing = Read-AssetIndex $target
    foreach ($item in $existing.items) { $key = @($item.provider,$item.sourceInstance,$item.itemId) | ConvertTo-Json -Compress; $merged[$key] = $item }
}
foreach ($item in $incoming.items) {
    $key = @($item.provider,$item.sourceInstance,$item.itemId) | ConvertTo-Json -Compress
    # 锁定内容的人工选择不被导入覆盖，未知来源元数据原样保留。
    if ($merged.Contains($key) -and $merged[$key].locked -eq $true) { continue }
    $merged[$key] = $item
}
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [ordered]@{schema=1; items=@($merged.Values)} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding UTF8
    $null = Read-AssetIndex $temporary
    if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary,$target,$target+'.bak') } else { [IO.File]::Move($temporary,$target) }
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
Write-Output "索引合并完成，共 $($merged.Count) 项；用户素材文件未复制或删除。"
