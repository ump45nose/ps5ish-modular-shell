param([ValidateRange(60,7200)][int]$Seconds = 1800)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
# 长测独立于快速构建；运行期间不要重编译同一 artifacts 中正在加载的程序集。
& "$root\artifacts\Shell.VideoSoak.exe" $Seconds 2>&1 | Tee-Object -FilePath "$root\artifacts\video-soak.log"
if ($LASTEXITCODE -ne 0) { throw '长时视频测试失败，保留 CSV 供诊断。' }
