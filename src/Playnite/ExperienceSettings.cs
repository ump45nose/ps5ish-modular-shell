using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl
    {
        private static bool startupShown;
        private bool startupActive;
        private CancellationTokenSource startupCancel;
        private Grid startupLayer;
        /// <summary>将主题媒体和可选内容设置接到旧主题已有操作槽，不要求主题引用插件实现。</summary>
        private void InitializeExperienceSettings()
        {
            experience=ThemeExperience.Load(dataRoot);ApplyExperience();
            var actions=((Button)layout.FindName("SaveSettings")).Parent as Panel;
            var panel=new WrapPanel();((Panel)actions.Parent).Children.Add(panel);
            var button=new Button {Content="启屏动画 / 主题曲",Margin=new Thickness(10)};
            button.SetResourceReference(StyleProperty,"ActionStyle");button.Click+=(s,e)=>OpenExperienceSettings();panel.Children.Add(button);
            var theme=new Button{Content="PS5 主题 · 动效与布局",Margin=new Thickness(10)};theme.SetResourceReference(StyleProperty,"ActionStyle");theme.Click+=(s,e)=>OpenThemeExperience();panel.Children.Add(theme);
            var hub=new Button {Content="额外组件 · 影视库",Margin=new Thickness(10)};
            hub.SetResourceReference(StyleProperty,"ActionStyle");hub.Click+=(s,e)=>{
                var provider=hubProvider as IOptionalHubSettings;
                if(provider==null){status.Text="未安装可配置的影视组件";return;}
                var window=new Window {Owner=ownerWindow,Title="额外组件 · 影视库",Background=new SolidColorBrush(Color.FromRgb(20,27,42)),Foreground=Brushes.White,FontSize=18,Width=780,Height=680,Content=provider.CreateSettingsView(),WindowStartupLocation=WindowStartupLocation.CenterOwner};
                window.ShowDialog();RefreshHubVisibility();
            };panel.Children.Add(hub);
        }
        /// <summary>关闭组件仅隐藏入口，模块不联网；旧内容插件继续按原契约显示。</summary>
        private void RefreshHubVisibility()
        {
            var button=layout.FindName("HubAction") as Button;var optional=hubProvider as IOptionalHubSettings;
            if(button!=null){button.Visibility=modules!=null && modules.GetHubProviders().Length>0?Visibility.Visible:Visibility.Collapsed;button.Content="影视";}
        }
        /// <summary>本地文件只引用，不上传服务器；保存失败不改变当前设置。</summary>
        private void OpenExperienceSettings()
        {
            var window=new Window {Owner=ownerWindow,Title="启屏动画与主题曲",Background=new SolidColorBrush(Color.FromRgb(20,27,42)),Foreground=Brushes.White,FontSize=18,Width=760,Height=490,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var panel=new StackPanel {Margin=new Thickness(24)};window.Content=panel;
            var enabled=new CheckBox {Content="启用启屏动画（每次启动一次，确认/返回可跳过）",IsChecked=settings.startupAnimation,Margin=new Thickness(0,0,0,16)};panel.Children.Add(enabled);
            string video=settings.startupVideo,music=settings.backgroundMusic;
            var videoLabel=new TextBlock {Text=video??"默认启屏动画",TextWrapping=TextWrapping.Wrap};panel.Children.Add(videoLabel);
            var videoPick=new Button {Content="选择本地动画视频"};panel.Children.Add(videoPick);
            videoPick.Click+=(s,e)=>{var dialog=new Microsoft.Win32.OpenFileDialog {Filter="视频|*.mp4;*.mkv;*.webm;*.mov;*.avi",CheckFileExists=true};if(dialog.ShowDialog(window)==true){video=dialog.FileName;videoLabel.Text=video;}};
            var musicLabel=new TextBlock {Text=music??"默认主题曲",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,0)};panel.Children.Add(musicLabel);
            var musicPick=new Button {Content="选择本地主题曲"};panel.Children.Add(musicPick);
            musicPick.Click+=(s,e)=>{var dialog=new Microsoft.Win32.OpenFileDialog {Filter="音频|*.mp3;*.wav;*.m4a;*.flac;*.ogg",CheckFileExists=true};if(dialog.ShowDialog(window)==true){music=dialog.FileName;musicLabel.Text=music;}};
            var reset=new Button {Content="恢复默认动画和主题曲",Margin=new Thickness(0,16,0,0)};panel.Children.Add(reset);reset.Click+=(s,e)=>{video=null;music=null;videoLabel.Text="默认启屏动画";musicLabel.Text="默认主题曲";};
            var error=new TextBlock {TextWrapping=TextWrapping.Wrap};panel.Children.Add(error);
            var save=new Button {Content="保存"};panel.Children.Add(save);save.Click+=(s,e)=>{
                var next=ShellSettings.Load(dataRoot);next.startupAnimation=enabled.IsChecked==true;next.startupVideo=video;next.backgroundMusic=music;
                try{next.Save(dataRoot);settings=next;window.Close();}catch{error.Text="保存失败，原设置保留";}
            };
            window.ShowDialog();
        }
        /// <summary>停止启屏等待；后台、跳过和卸载都释放同一会话，防止与主页视频争用。</summary>
        private void StopStartup(){if(startupCancel!=null)startupCancel.Cancel();}
        /// <summary>内置短入场或本地视频每进程一次；减少动效直接进入主页，坏文件也能继续。</summary>
        private async Task PlayStartupAsync()
        {
            if(startupShown||!settings.startupAnimation||settings.reducedMotion||!loaded)return;
            startupShown=true;startupActive=true;StopPageVideo();media.Cancel();
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);startupCancel=cancel;
            var surface=layout as Panel;startupLayer=new Grid {Background=Brushes.Black};Panel.SetZIndex(startupLayer,1000);surface.Children.Add(startupLayer);
            startupLayer.MouseDown+=(s,e)=>StopStartup();
            IVideoSession session=null;
            try{
                if(!String.IsNullOrWhiteSpace(settings.startupVideo)){
                    if(!File.Exists(settings.startupVideo))throw new IOException("启屏视频不存在");
                    var backend=modules.GetCapability<IVideoBackend>();if(backend==null)throw new IOException("视频组件不可用");
                    cancel.CancelAfter(TimeSpan.FromSeconds(120));session=await backend.PrepareAsync(settings.startupVideo,cancel.Token);cancel.Token.ThrowIfCancellationRequested();
                    var pixels=new byte[checked(session.Width*session.Height*4)];long version=0;
                    var bitmap=new WriteableBitmap(session.Width,session.Height,96,96,PixelFormats.Bgr32,null);
                    startupLayer.Children.Add(new Image {Source=bitmap,Stretch=Stretch.Uniform});
                    session.SetVolume(experience.startupAudioMode=="source"?settings.soundVolume*(settings.nightMode?.25:1):0);
                    if(experience.startupAudioMode=="separate")PlayCue("startup");
                    while(!session.Ended){cancel.Token.ThrowIfCancellationRequested();if(session.CopyFrame(pixels,ref version))bitmap.WritePixels(new Int32Rect(0,0,session.Width,session.Height),pixels,session.Width*4,0);await Task.Delay(33,cancel.Token);}
                }else{
                    startupLayer.Background=new LinearGradientBrush(Color.FromRgb(6,9,15),Color.FromRgb(32,39,53),90);
                    if(experience.particles)startupLayer.Children.Add(new StartupParticles());
                    if(experience.startupAudioMode=="separate")PlayCue("startup");
                    var title=new TextBlock {Text="MODULAR SHELL",FontSize=36,FontWeight=FontWeights.Light,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};startupLayer.Children.Add(title);
                    title.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(1200)) {AutoReverse=true});
                    await Task.Delay(2600,cancel.Token);
                }
                // 片尾保留光尘并渐隐至真实首页，避免结束后突然黑切。
                if(experience.particles){startupLayer.BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(700)));await Task.Delay(700,cancel.Token);}
            }catch(OperationCanceledException){}catch{status.Text="启屏媒体不可用，已进入游戏库";}
            {if(session!=null)session.Dispose();if(cueSession!=null){try{for(int step=4;step>=0;step--){if(cueSession==null)break;cueSession.SetVolume(settings.soundVolume*step/5.0);await Task.Delay(25);}}catch{}}StopCue();surface.Children.Remove(startupLayer);startupLayer=null;startupActive=false;startupCancel=null;cancel.Dispose();}
        }
    }
}

