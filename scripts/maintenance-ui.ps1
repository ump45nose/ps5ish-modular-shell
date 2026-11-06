param(
    [Parameter(Mandatory=$true)][string]$UserDataRoot,
    [switch]$Smoke,
    [string]$CapturePath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$dataRoot = [IO.Path]::GetFullPath($UserDataRoot)
# 维护窗口只操作明确目录，长文件操作交给独立 runspace，界面线程保持响应。
[xml]$markup = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Modular Shell · 素材与配置维护" Width="1050" Height="650" MinWidth="800" MinHeight="520" Background="#101B32" Foreground="White" FontFamily="Microsoft YaHei UI" FontSize="14" WindowStartupLocation="CenterScreen">
  <Window.Resources><Style TargetType="Button"><Setter Property="Margin" Value="0,0,10,10"/><Setter Property="Padding" Value="14,8"/></Style></Window.Resources>
  <Grid Margin="22" Background="#101B32"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <StackPanel><TextBlock Text="素材与配置维护" FontSize="28"/><TextBlock x:Name="Location" TextWrapping="Wrap" Margin="0,8,0,8" Foreground="#BACADD"/><TextBlock Text="导入只复制到缓存；锁定或正在引用的素材不会被限额清理。配置恢复需先退出 Playnite。" TextWrapping="Wrap" Margin="0,0,0,16"/></StackPanel>
    <StackPanel Grid.Row="1" x:Name="Commands">
      <WrapPanel><Button x:Name="Refresh" Content="刷新列表"/><Button x:Name="Add" Content="添加本地素材"/><Button x:Name="ToggleLock" Content="锁定 / 解锁所选"/><TextBlock Text="额度 MiB" VerticalAlignment="Center" Margin="8,0,8,10"/><TextBox x:Name="Quota" Text="1024" Width="85" Height="32" Margin="0,0,10,10"/><Button x:Name="Prune" Content="按额度清理"/></WrapPanel>
      <WrapPanel><Button x:Name="Backup" Content="备份配置"/><Button x:Name="Restore" Content="恢复配置快照"/><Button x:Name="Import" Content="导入素材索引"/><Button x:Name="Export" Content="导出素材索引"/></WrapPanel>
    </StackPanel>
    <DataGrid Grid.Row="2" x:Name="Entries" AutoGenerateColumns="False" IsReadOnly="True" SelectionMode="Single" Background="#F4F6FA" Foreground="#18243A" CanUserAddRows="False">
      <DataGrid.Columns><DataGridTextColumn Header="来源" Binding="{Binding source}" Width="2*" MinWidth="200"/><DataGridTextColumn Header="字节" Binding="{Binding bytes}" Width="90"/><DataGridCheckBoxColumn Header="锁定" Binding="{Binding locked}" Width="55"/><DataGridCheckBoxColumn Header="引用" Binding="{Binding referenced}" Width="55"/><DataGridTextColumn Header="缓存路径" Binding="{Binding path}" Width="2*" MinWidth="240"/></DataGrid.Columns>
    </DataGrid>
    <TextBlock Grid.Row="3" x:Name="Status" Text="就绪" TextWrapping="Wrap" Margin="0,16,0,0" Foreground="#BACADD"/>
  </Grid>
</Window>
'@
$reader = New-Object System.Xml.XmlNodeReader $markup
$window = [Windows.Markup.XamlReader]::Load($reader)
$controls = @{}
foreach ($name in @('Location','Commands','Entries','Quota','Status','Refresh','Add','ToggleLock','Prune','Backup','Restore','Import','Export')) { $controls[$name] = $window.FindName($name) }
$controls.Location.Text = $dataRoot
$script:pending = $null
$script:engine = $null
$script:completion = $null
$script:actionLabel = ''
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(100)

# 新工作直接调用固定脚本和结构化参数，不构建可执行命令字符串。
function Start-Work([string]$ScriptName, [hashtable]$Parameters, [scriptblock]$Completed, [string]$Label) {
    if ($script:pending) { return }
    $script:engine = [PowerShell]::Create()
    $null = $script:engine.AddCommand((Join-Path $PSScriptRoot $ScriptName))
    foreach ($key in $Parameters.Keys) { $null = $script:engine.AddParameter($key,$Parameters[$key]) }
    $script:completion = $Completed
    $script:actionLabel = $Label
    $controls.Commands.IsEnabled = $false
    $controls.Status.Text = $Label + '…'
    $script:pending = $script:engine.BeginInvoke()
    $timer.Start()
}

# 刷新由缓存脚本提供审计结果，UI 不另写一套缓存业务逻辑。
function Refresh-Entries {
    Start-Work 'cache.ps1' @{Mode='Audit'; UserDataRoot=$dataRoot} {
        param($rows)
        $controls.Entries.ItemsSource = @($rows)
        $controls.Status.Text = '已列出 ' + @($rows).Count + ' 个登记素材。'
    } '读取缓存'
}

# 额度校验在动作开始前完成，避免把非法输入传入后台清理。
function Read-Quota {
    $value = 0
    if (-not [int]::TryParse($controls.Quota.Text,[ref]$value) -or $value -lt 1 -or $value -gt 1048576) { throw '额度必须为 1～1048576 MiB。' }
    return $value
}

# 文件对话框只返回用户选择，调用方决定是索引、快照还是本地媒体。
function Choose-File([string]$Filter) {
    $dialog = New-Object Microsoft.Win32.OpenFileDialog
    $dialog.Filter = $Filter
    if ($dialog.ShowDialog($window)) { return $dialog.FileName }
    return $null
}

$timer.Add_Tick({
    if (-not $script:pending -or -not $script:pending.IsCompleted) { return }
    $timer.Stop()
    $callback = $script:completion
    try {
        $output = $script:engine.EndInvoke($script:pending)
        if ($script:engine.HadErrors) { throw ($script:engine.Streams.Error | Out-String) }
        $controls.Status.Text = $script:actionLabel + '完成。' + ($output -join ' ')
        # 先释放工作状态，完成回调才能安排下一次只读刷新。
        $script:engine.Dispose(); $script:engine = $null; $script:pending = $null
        $controls.Commands.IsEnabled = $true
        if ($callback) { & $callback $output }
    } catch {
        $controls.Status.Text = '操作未完成：' + $_.Exception.Message
        if ($script:engine) { $script:engine.Dispose() }
        $script:engine = $null; $script:pending = $null; $controls.Commands.IsEnabled = $true
    }
})
$controls.Refresh.Add_Click({ Refresh-Entries })
$controls.Add.Add_Click({
    try {
        $quota = Read-Quota
        $chosen = Choose-File '媒体文件|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.wav;*.mp3;*.flac;*.ogg;*.m4a'
        if ($chosen) { Start-Work 'cache.ps1' @{Mode='Add';UserDataRoot=$dataRoot;SourcePath=$chosen;MaxMiB=$quota} { Refresh-Entries } '复制本地素材' }
    } catch { $controls.Status.Text = $_.Exception.Message }
})
$controls.ToggleLock.Add_Click({
    $selected = $controls.Entries.SelectedItem
    if (-not $selected) { $controls.Status.Text = '请先选择一行。'; return }
    Start-Work 'cache.ps1' @{Mode='SetLock';UserDataRoot=$dataRoot;CachePath=$selected.path;Locked=(-not $selected.locked)} { Refresh-Entries } '更新锁定'
})
$controls.Prune.Add_Click({
    try { Start-Work 'cache.ps1' @{Mode='Prune';UserDataRoot=$dataRoot;MaxMiB=(Read-Quota)} { Refresh-Entries } '清理超额缓存' }
    catch { $controls.Status.Text = $_.Exception.Message }
})
$controls.Backup.Add_Click({
    $target = Join-Path $dataRoot ('config-snapshots\manual-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    Start-Work 'config-snapshot.ps1' @{Mode='Backup';UserDataRoot=$dataRoot;SnapshotPath=$target} $null '备份配置'
})
$controls.Restore.Add_Click({
    if (Get-Process Playnite* -ErrorAction SilentlyContinue) { $controls.Status.Text = '请先正常退出 Playnite，再在此窗口恢复。'; return }
    $chosen = Choose-File '配置快照清单|snapshot.json'
    if ($chosen) { Start-Work 'config-snapshot.ps1' @{Mode='Restore';UserDataRoot=$dataRoot;SnapshotPath=(Split-Path $chosen);RequirePlayniteStopped=$true} { Refresh-Entries } '恢复配置' }
})
$controls.Import.Add_Click({
    $chosen = Choose-File '素材索引|*.json'
    if ($chosen) { Start-Work 'assets.ps1' @{Mode='Import';UserDataRoot=$dataRoot;IndexPath=$chosen} { Refresh-Entries } '导入素材索引' }
})
$controls.Export.Add_Click({
    $dialog = New-Object Microsoft.Win32.SaveFileDialog
    $dialog.Filter = '素材索引|*.json'; $dialog.FileName = 'assets-export.json'
    if ($dialog.ShowDialog($window)) { Start-Work 'assets.ps1' @{Mode='Export';UserDataRoot=$dataRoot;IndexPath=$dialog.FileName} $null '导出素材索引' }
})
$window.Add_Closing({
    param($sender,$eventArgs)
    # 不在文件提交期间强制中断后台任务，避免只完成一半的维护操作。
    if ($script:pending) { $eventArgs.Cancel = $true; $controls.Status.Text = '正在提交文件，请等待操作结束后关闭。' }
})
if ($Smoke) {
    # 测试强制替换为专用临时目录，验证后台复制、完成回调及表格绑定，不打开桌面窗口。
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('shell-maintenance-' + [Guid]::NewGuid().ToString('N'))
    $dataRoot = Join-Path $testRoot 'data'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $controls.Location.Text = '测试目录（非用户数据）'
    try {
        $source = Join-Path $testRoot 'test.wav'
        [IO.File]::WriteAllBytes($source,(New-Object byte[] 128))
        Start-Work 'cache.ps1' @{Mode='Add';UserDataRoot=$dataRoot;SourcePath=$source;MaxMiB=1} { Refresh-Entries } '测试缓存复制'
        $frame = New-Object Windows.Threading.DispatcherFrame
        $watch = New-Object Windows.Threading.DispatcherTimer
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $watch.Interval = [TimeSpan]::FromMilliseconds(50)
        $watch.Add_Tick({ if (-not $script:pending -or [DateTime]::UtcNow -gt $deadline) { $frame.Continue = $false } })
        $watch.Start()
        [Windows.Threading.Dispatcher]::PushFrame($frame)
        $watch.Stop()
        if ($script:pending -or @($controls.Entries.ItemsSource).Count -ne 1 -or -not $controls.Commands.IsEnabled) { throw ('后台维护链路失败：' + $controls.Status.Text) }
        'PASS 维护窗口后台复制、完成刷新与表格绑定'
    } finally {
        $timer.Stop()
        if ($script:engine) { $script:engine.Stop(); $script:engine.Dispose() }
        if (-not ([IO.Path]::GetFullPath($testRoot)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '测试清理越界' }
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
    $controls.Status.Text = '离屏与后台任务测试通过；表格数据为临时生成的测试素材。'
    $window.Content.Measure([Windows.Size]::new(1000,600)); $window.Content.Arrange([Windows.Rect]::new(0,0,1000,600)); $window.Content.UpdateLayout()
    if ($CapturePath) {
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(1000,600,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window.Content)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $file = [IO.File]::Create([IO.Path]::GetFullPath($CapturePath))
        try { $encoder.Save($file) } finally { $file.Dispose() }
    }
    'PASS 独立维护窗口控件与离屏布局'
    return
}
$window.Add_Loaded({ Refresh-Entries })
$null = $window.ShowDialog()

