$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$cache = Join-Path $root 'artifacts\dependencies'
$package = Join-Path $cache 'videolan.libvlc.windows.3.0.23.1.nupkg'
$expected = '70927AFA9AD34B77E7D9A5E6D02CAE099771F6EB3114DA18111A4B76F65B836F'
$runtime = Join-Path $root 'artifacts\plugins\LocalVideo\runtime'
New-Item -ItemType Directory -Force $cache, $runtime | Out-Null
# 锁定官方 NuGet 包版本和 SHA-256，不在构建时追随 latest。
if (-not (Test-Path -LiteralPath $package)) {
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/3.0.23.1/videolan.libvlc.windows.3.0.23.1.nupkg' -OutFile $package
}
if ((Get-FileHash -LiteralPath $package).Hash -ne $expected) { throw 'LibVLC 包校验失败。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -match '^build/(x64|x86)/' -and $entry.FullName -match '(\.dll|plugins\.dat)$') {
            # 保留旧 x64 根路径兼容；32 位宿主从独立子目录解析同架构依赖。
            $architecture = $entry.FullName.Split('/')[1]
            $relative = $entry.FullName.Substring(('build/' + $architecture + '/').Length)
            if ($architecture -eq 'x86') { $relative = Join-Path 'x86' $relative }
            $destination = [IO.Path]::GetFullPath((Join-Path $runtime $relative))
            if (-not $destination.StartsWith([IO.Path]::GetFullPath($runtime) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '原生依赖路径越界。' }
            New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
            # 每次从已验证的官方包恢复，不信任可能已被修改的旧构建 DLL。
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    }
} finally { $zip.Dispose() }
Write-Output 'LibVLC 3.0.23.1 x64 与 x86 运行时已准备。'
