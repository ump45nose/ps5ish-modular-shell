# 所有安装和打包入口只从扩展清单读取版本，避免脚本引用过期包。
$manifestPath = Join-Path (Split-Path $PSScriptRoot) 'src\Playnite\extension.yaml'
$versionLine = @(Get-Content -LiteralPath $manifestPath | Where-Object { $_ -match '^Version:' })
if ($versionLine.Count -ne 1) { throw '扩展版本字段无效。' }
$shellVersion = ($versionLine[0] -replace '^Version:\s*','').Trim()
if ($shellVersion -notmatch '^\d+\.\d+\.\d+$') { throw '扩展版本格式无效。' }
