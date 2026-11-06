param([string]$UserDataRoot="$env:APPDATA\Playnite\ExtensionsData\f13fdbe6-52ba-4198-8bea-457be1a609cf",[string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot) 'artifacts\automatic-quality-20260907'),[int]$First=10)
$ErrorActionPreference='Stop'
# 只导出自动服务的实测清单，不生成来源、不下载素材、不改缓存状态。
$state=Get-Content -LiteralPath (Join-Path $UserDataRoot 'automatic-assets\state-v1.json') -Raw | ConvertFrom-Json
if($state.schema -ne 1){throw '自动素材版本不支持'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$games=@($state.games | Select-Object -First $First)
$rows=@(foreach($game in $games){foreach($file in $game.files){
    [pscustomobject][ordered]@{Game=$game.title;Kind=$file.kind;Status=$file.status;Width=$file.width;Height=$file.height;Bitrate=$file.bitrate;FrameRate=$file.frameRate;Codec=$file.codec;Duration=$file.duration;AudioTracks=$file.audioTracks;SampleRate=$file.sampleRate;Sha256=$file.sha256;CheckedUtc=if($file.checkedTicks){[DateTime]::new([long]$file.checkedTicks,[DateTimeKind]::Utc).ToString('o')}else{''};Attribution=$file.attribution;Source=$file.url;Path=$file.path}
}})
$rows | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'quality.csv') -Encoding utf8 -NoTypeInformation
$games | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'quality.json') -Encoding utf8
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('# 自动素材实测清单')
$lines.Add('')
$lines.Add('背景最低1920×1080、预告最低1080p。Hero 3840×1240属于宽屏备选，不计作4K全屏背景。reserve为留档文件，不进入正式轮换。')
foreach($game in $games){
    $lines.Add('');$lines.Add('## '+$game.title);$lines.Add('');$lines.Add($game.status);$lines.Add('');$lines.Add('| 素材 | 状态 | 实测尺寸 | 来源归属 |');$lines.Add('|---|---|---|---|')
    foreach($file in $game.files){$owner=($file.attribution -replace '\|','/' -replace '[\r\n]+',' ');$lines.Add("| $($file.kind) | $($file.status) | $($file.width)×$($file.height) | $owner |")}
}
$lines.Add('');$lines.Add('完整来源URL、散列、时间与音视频参数见同目录 quality.csv / quality.json。电视观感及听感不由此清单自动判定。')
$lines | Set-Content -LiteralPath (Join-Path $OutputDirectory 'README.md') -Encoding utf8
Write-Output "已导出 $($games.Count) 款游戏、$($rows.Count) 项实测记录到 $OutputDirectory"
