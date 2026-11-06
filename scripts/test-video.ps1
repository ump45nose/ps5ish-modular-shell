$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force "$out\fixtures" | Out-Null
# 每次生成固定内容的测试视频，不读取用户的游戏和媒体。
& ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'color=c=red:s=320x180:r=30' -f lavfi -i 'anullsrc=r=48000:cl=stereo' -t 3 -c:v libx264 -pix_fmt yuv420p -c:a aac "$out\fixtures\红色首帧.mp4"
if ($LASTEXITCODE -ne 0) { throw '生成视频测试素材失败。' }
foreach ($profile in @(@('1080p','1920x1080'),@('2160p','3840x2160'))) {
    & "$out\plugins\LocalAssets\tools\ffmpeg.exe" -hide_banner -loglevel error -y -f lavfi -i "color=c=red:s=$($profile[1]):r=30" -t 2 -c:v libx264 -preset ultrafast -pix_fmt yuv420p "$out\fixtures\$($profile[0]).mp4"
    if ($LASTEXITCODE -ne 0) { throw '生成高清测试素材失败。' }
}
& "$out\Shell.VideoTests.exe"
if ($LASTEXITCODE -ne 0) { throw '原生视频回归失败。' }
