param([Parameter(Mandatory=$true)][string]$ExtensionsPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$parent = (Resolve-Path -LiteralPath $ExtensionsPath).Path
$target = [IO.Path]::GetFullPath((Join-Path $parent 'ModularShell_f13fdbe6-52ba-4198-8bea-457be1a609cf'))
$receipt = Get-Content -LiteralPath "$root\artifacts\installation-receipt.json" -Raw | ConvertFrom-Json
# 仅允许删除本次安装记录对应的固定目录，拒绝用户数据目录与修改过的文件。
if ($target -ne [IO.Path]::GetFullPath($receipt.target)) { throw '目标与本次安装记录不一致。' }
if (-not (Test-Path -LiteralPath $target)) { throw '目标不存在。' }
if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '拒绝链接目录。' }
$actual = @(Get-ChildItem -LiteralPath $target -Recurse -File)
if ($actual.Count -ne $receipt.files.Count) { throw '扩展内容已变化，请先核对，未执行删除。' }
foreach ($file in $actual) {
    $record = @($receipt.files | Where-Object { $_.path -eq $file.FullName })
    if ($record.Count -ne 1 -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $record[0].sha256) { throw "文件已修改：$($file.Name)，未执行删除。" }
}
# 已确认规范化目标为指定 Extensions 下唯一的任务目录；用户数据与其他插件保持独立。
Remove-Item -LiteralPath $target -Recurse -Force
Write-Output '本次安装的 Modular Shell 已移除；用户数据保留，下次启动生效。'
