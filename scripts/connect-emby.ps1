param(
    [Parameter(Mandatory=$true)][string]$Server,
    [Parameter(Mandatory=$true)][string]$Username,
    [Parameter(Mandatory=$true)][Security.SecureString]$Password,
    [Parameter(Mandatory=$true)][string]$UserDataRoot
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$uri = [Uri]$Server
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw '登录地址必须是无内嵌凭证的 HTTPS 地址。' }
$base = $uri.AbsoluteUri.TrimEnd('/')
if (-not $base.EndsWith('/emby',[StringComparison]::OrdinalIgnoreCase)) { $base += '/emby' }
$device = [Guid]::NewGuid().ToString('N')
$headers = @{ Authorization = 'Emby Client="Modular Shell", Device="Windows", DeviceId="'+$device+'", Version="0.6.0"' }
$pointer = [IntPtr]::Zero
try {
    # 明文仅用于本次 TLS 登录请求，不写入任何文件、诊断或输出。
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    $body = @{ Username=$Username; Pw=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) } | ConvertTo-Json -Compress
    try { $result = Invoke-RestMethod -Uri ($base+'/Users/AuthenticateByName') -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15 -MaximumRedirection 0 }
    catch { throw 'Emby 登录请求失败；未保存凭证。请检查连接或账号。' }
    if (-not $result.AccessToken -or -not $result.User.Id -or -not $result.ServerId) { throw '登录响应缺少必要身份字段。' }
    $root = [IO.Path]::GetFullPath((Join-Path $UserDataRoot 'emby'))
    $cursor = $root
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '拒绝通过链接目录保存连接。' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $path = Join-Path $root 'connection.json'
    foreach ($candidate in @($path,$path+'.bak')) { if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '拒绝覆盖链接连接文件。' } }
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]$result.AccessToken)
    $entropy = 'ModularShell.Emby.v1|' + $base + '|' + $result.ServerId + '|' + $result.User.Id
    try { $protected = [Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::UTF8.GetBytes($entropy),[Security.Cryptography.DataProtectionScope]::CurrentUser) }
    finally { [Array]::Clear($bytes,0,$bytes.Length) }
    $temporary = $path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [ordered]@{schema=1; Server=$base; UserId=[string]$result.User.Id; ServerId=[string]$result.ServerId; DeviceId=$device; ProtectedToken=[Convert]::ToBase64String($protected)} | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporary,$path,$path+'.bak') } else { [IO.File]::Move($temporary,$path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    'PASS Emby 已验证登录；令牌已使用 Windows CurrentUser 保护保存。'
} finally {
    $body = $null; $result = $null
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
