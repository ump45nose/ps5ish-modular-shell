$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$directory = Join-Path $root 'artifacts\plugins\LocalAssets\tools'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
# 固定版本及散列，不让用户运行时依赖开发机Python、PATH或自动更新的工具。
$manifest = Get-Content -LiteralPath (Join-Path $root 'third-party\media-tools.lock.json') -Raw | ConvertFrom-Json
foreach ($tool in $manifest.tools) {
    $target = Join-Path $directory $tool.name
    if (-not (Test-Path -LiteralPath $target)) {
        if ($tool.localSource -and (Test-Path -LiteralPath $tool.localSource)) { Copy-Item -LiteralPath $tool.localSource -Destination $target }
        elseif ($tool.url) { Invoke-WebRequest -Uri $tool.url -OutFile $target -UseBasicParsing -TimeoutSec 120 }
        else { throw "缺少锁定工具 $($tool.name)，请恢复清单指定版本。" }
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $tool.sha256) { throw "工具散列不匹配：$($tool.name)" }
}
Copy-Item -LiteralPath (Join-Path $root 'third-party\media-tools.lock.json') -Destination $directory -Force
