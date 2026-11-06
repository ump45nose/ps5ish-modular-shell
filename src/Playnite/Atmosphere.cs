using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl
    {
        private DispatcherTimer wallpaperTimer;
        private Image previousBackground;
        private string[] wallpapers=new string[0];
        private int wallpaperIndex;
        private bool wallpaperLoading;
        private DateTime lastCue;
        private string lastCueName;
        private CancellationTokenSource cueCancel;
        private IMusicSession cueSession;
        private Slider musicSlider,soundSlider;
        private CheckBox soundOption;

        /// <summary>宿主控制壁纸时钟和声音设置，主题仅决定呈现。</summary>
        private void InitializeAtmosphere()
        {
            // 独立绘制器显式计算居中目标矩形，避免 Image 的溢出画面从左上角裁切。
            var surface=(Panel)layout;int sceneIndex=surface.Children.IndexOf(background);
            surface.Children.Remove(background);layout.UnregisterName("Scene");
            background=new CenteredWallpaperImage{Name="Scene",Opacity=.8};layout.RegisterName("Scene",background);surface.Children.Insert(sceneIndex,background);
            background.Stretch=Stretch.UniformToFill;videoLayer.Stretch=Stretch.Uniform;
            previousBackground=new CenteredWallpaperImage {Stretch=Stretch.UniformToFill,IsHitTestVisible=false};
            // 大图的固有像素尺寸不能参与扩张 Grid，媒体始终受逻辑画布约束。
            foreach(var layer in new[]{background,videoLayer,previousBackground}){layer.Width=layout.Width;layer.Height=layout.Height;layer.HorizontalAlignment=HorizontalAlignment.Center;layer.VerticalAlignment=VerticalAlignment.Center;}
            var root=(Panel)layout;root.Children.Insert(root.Children.IndexOf(background),previousBackground);
            var initialClock=layout.FindName("Clock") as TextBlock;if(initialClock!=null)initialClock.Text=DateTime.Now.ToString("HH:mm");
            wallpaperTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(8)};
            wallpaperTimer.Tick+=async(s,e)=>{
                var clock=layout.FindName("Clock") as TextBlock;if(clock!=null)clock.Text=DateTime.Now.ToString("HH:mm");
                var currentSelection=selection;
                if(currentSelection==null||currentSelection.IsCancellationRequested)return;
                if(wallpaperLoading||!loaded||busy||OverlayOpen||showAll||videoSession!=null||ownerWindow==null||!ownerWindow.IsActive||wallpapers.Length<2)return;
                wallpaperLoading=true;
                try {wallpaperIndex=(wallpaperIndex+1)%wallpapers.Length;await SetWallpaper(wallpapers[wallpaperIndex],currentSelection.Token);}catch{}finally{wallpaperLoading=false;}
            };
            var settingsStack=(Panel)((Button)layout.FindName("SaveSettings")).Parent;
            settingsStack=settingsStack.Parent as Panel;
            soundOption=new CheckBox {Content="界面音效",IsChecked=settings.uiSounds};soundOption.SetResourceReference(StyleProperty,"OptionStyle");settingsStack.Children.Insert(settingsStack.Children.Count-2,soundOption);
            musicSlider=VolumeSlider(settings.musicVolume);soundSlider=VolumeSlider(settings.soundVolume);
            var volumes=new StackPanel {Orientation=Orientation.Horizontal};volumes.Children.Add(new TextBlock {Text="音乐音量",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center});volumes.Children.Add(musicSlider);volumes.Children.Add(new TextBlock {Text="音效音量",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center});volumes.Children.Add(soundSlider);settingsStack.Children.Insert(settingsStack.Children.Count-2,volumes);
            settingsStack.Children.Insert(settingsStack.Children.Count-2,new TextBlock {Text="默认音乐：Dreams Become Real — Kevin MacLeod (incompetech.com)\nCC BY 4.0 · creativecommons.org/licenses/by/4.0/",Foreground=Brushes.LightGray,FontSize=13,Margin=new Thickness(0,8,0,8)});
            AddHandler(Button.ClickEvent,new RoutedEventHandler((s,e)=>{
                // 返回命令已经发出专用音效，不在冒泡阶段追加确认音。
                if(lastCueName!="back"||DateTime.UtcNow-lastCue>TimeSpan.FromMilliseconds(150))PlayCue("confirm");
                var button=e.OriginalSource as Button;if(button!=null)AnimateOption(button);
            }));
            AddHandler(UIElement.GotKeyboardFocusEvent,new System.Windows.Input.KeyboardFocusChangedEventHandler((s,e)=>{
                // 游戏选择已有唯一发声入口；鼠标聚焦由点击确认音负责。
                if(!games.IsKeyboardFocusWithin && System.Windows.Input.Mouse.LeftButton!=System.Windows.Input.MouseButtonState.Pressed)PlayCue("move");
                UpdateOptionFocus(e.NewFocus as DependencyObject);
            }));
        }
        /// <summary>音量范围和步长使手柄方向键能直接调节。</summary>
        private Slider VolumeSlider(double value){return new Slider {Minimum=0,Maximum=1,SmallChange=.05,LargeChange=.1,Value=value,Width=150,Margin=new Thickness(15),IsSnapToTickEnabled=true,TickFrequency=.05};}

        /// <summary>切换内容时解析背景列表与 Logo，所有迟到结果受选择令牌约束。</summary>
        private async Task LoadAtmosphere(GameItem item,CancellationToken token,bool imagesOnly=false)
        {
            wallpapers=new string[0];wallpaperIndex=0;gameLogo.Source=null;if(!imagesOnly)summarySlot.Content=null;
            logoFallback.Text=item==null?"":item.Title;logoFallback.Visibility=showAll?Visibility.Collapsed:Visibility.Visible;
            if(item==null){ClearWallpaper();return;}
            var asset=assets==null?null:assets.Find(item);
            var asyncAssets=assets as IAsyncAssetProvider;
            if(asyncAssets!=null)asset=await asyncAssets.ResolveAsync(item,token);
            token.ThrowIfCancellationRequested();
            wallpapers=(asset==null||asset.BackgroundPaths==null?new string[0]:asset.BackgroundPaths).Where(File.Exists).ToArray();
            if(wallpapers.Length==0 && !String.IsNullOrWhiteSpace(item.BackgroundPath))wallpapers=new[]{item.BackgroundPath};
            // 有有效新图时保留旧图参与淡入；无素材条目明确恢复主题背景。
            if(wallpapers.Length>0){
                try {await SetWallpaper(wallpapers[0],token,true);}
                catch {if(!token.IsCancellationRequested)ClearWallpaper();throw;}
            }else ClearWallpaper();
            if(asset!=null && !String.IsNullOrEmpty(asset.LogoPath)){
                var logo=await GameCard.LoadImageAsync(asset.LogoPath,Math.Min(1920,ElementPixelWidth(gameLogo)),token);token.ThrowIfCancellationRequested();gameLogo.Source=logo;
                logoFallback.Visibility=logo==null&&!showAll?Visibility.Visible:Visibility.Collapsed;
                ThemePacks.RevealContent(gameLogo,layout,settings.reducedMotion);
            }
            if(!imagesOnly&&!token.IsCancellationRequested && modules!=null){
                var provider=modules.GetCapability<IContextSummaryProvider>();
                try {
                    var summary=provider==null?null:provider.CreateSummary(item);
                    var preferences=summary as IViewPreferences;
                    if(preferences!=null)preferences.SetReducedMotion(settings.reducedMotion);
                    summarySlot.Content=summary;
                }catch{summarySlot.Content=null;}
            }
        }
        /// <summary>清除无素材条目的旧画面及动画，避免跨游戏残留。</summary>
        private void ClearWallpaper()
        {
            background.BeginAnimation(OpacityProperty,null);previousBackground.BeginAnimation(OpacityProperty,null);
            background.Source=null;previousBackground.Source=null;
        }
        /// <summary>新图解码完成后才交叉淡入，取消请求不清空现有画面。</summary>
        private async Task SetWallpaper(string path,CancellationToken token,bool clearMissing=false)
        {
            var next=await GameCard.LoadImageAsync(path,Math.Min(3840,PhysicalWidth(ownerWindow==null?1920:ownerWindow.ActualWidth)),token,true);token.ThrowIfCancellationRequested();if(next==null){if(clearMissing)ClearWallpaper();return;}
            previousBackground.Source=background.Source;previousBackground.Opacity=.8;
            background.Source=next;background.Stretch=WallpaperStretch((settingsDraft??experience).wallpaperMode);previousBackground.Stretch=background.Stretch;
            var decoded=next as System.Windows.Media.Imaging.BitmapSource;if(decoded!=null)RecordOutput("wallpaper",path,decoded.PixelWidth,decoded.PixelHeight);
            background.RenderTransform=Transform.Identity;
            background.BeginAnimation(OpacityProperty,null);previousBackground.BeginAnimation(OpacityProperty,null);
            if(settings.reducedMotion||experience.transition=="none"||experience.backgroundMs==0){background.Opacity=.8;previousBackground.Source=null;return;}
            var duration=TimeSpan.FromMilliseconds(experience.backgroundMs);
            if(experience.transition=="slide"){var move=new TranslateTransform();background.RenderTransform=move;move.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(26,0,duration){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});}
            background.BeginAnimation(OpacityProperty,new DoubleAnimation(0,.8,duration));
            var fade=new DoubleAnimation(.8,0,duration);fade.Completed+=(s,e)=>{if(ReferenceEquals(background.Source,next))previousBackground.Source=null;};previousBackground.BeginAnimation(OpacityProperty,fade);
        }
        /// <summary>按显示器DPI换算物理像素，175%缩放下仍允许4K解码。</summary>
        private int PhysicalWidth(double logical){var presentation=PresentationSource.FromVisual(this);double scale=presentation==null||presentation.CompositionTarget==null?1:presentation.CompositionTarget.TransformToDevice.M11;return Math.Max(1,(int)Math.Ceiling((logical>0?logical:640)*scale));}
        /// <summary>Logo还需计入主题Viewbox缩放，不能只乘显示器DPI。</summary>
        private int ElementPixelWidth(FrameworkElement element){try{return PhysicalWidth(element.TransformToAncestor(ownerWindow).TransformBounds(new Rect(element.RenderSize)).Width);}catch{return PhysicalWidth(element.ActualWidth);}}
        /// <summary>优先用户音乐，没有专属素材时选择可持续循环的主题背景曲。</summary>
        private string DefaultMusicPath()
        {
            if(!String.IsNullOrWhiteSpace(settings.backgroundMusic)&&File.Exists(settings.backgroundMusic))return settings.backgroundMusic;
            return Path.Combine(packageRoot,"themes","audio","ambient.mp3");
        }
        /// <summary>界面音效复用独立音频后端；限速且每次最多一个短会话。</summary>
        private async void PlayCue(string name)
        {
            if(auditionActive||!loaded||busy||!(settingsDraft==null?settings.uiSounds:soundOption.IsChecked==true)||ownerWindow==null||!ownerWindow.IsActive||modules==null)return;
            // 长按只限制移动音；确认和返回可立即接管，避免被前一声移动吞掉。
            if((name=="move"||name=="switch") && DateTime.UtcNow-lastCue<TimeSpan.FromMilliseconds(130))return;
            lastCue=DateTime.UtcNow;lastCueName=name;
            var backend=modules.GetCapability<IMusicBackend>();if(backend==null)return;
            StopCue();cueCancel=new CancellationTokenSource();var token=cueCancel.Token;IMusicSession session=null;
            try{
                var soundConfig=settingsDraft??experience;
                string custom=name=="startup"?soundConfig.startupSound:name=="switch"?soundConfig.switchSound:name=="move"?soundConfig.moveSound:name=="back"?soundConfig.backSound:soundConfig.confirmSound;
                session=await backend.PrepareAsync(!String.IsNullOrWhiteSpace(custom)&&File.Exists(custom)?custom:Path.Combine(packageRoot,"themes","audio",name+".wav"),token);
                // 音乐默认循环，界面短提示必须显式只播一次，否则释放前会重复响起。
                var loopControl=session as IMusicLoopControl;if(loopControl!=null)loopControl.SetLooping(false);
                token.ThrowIfCancellationRequested();cueSession=session;session.SetVolume((settingsDraft==null?settings.soundVolume:soundSlider.Value)*(settings.nightMode?.25:1));session.Play();
                await Task.Delay(name=="startup"?Timeout.Infinite:650,token);
            }catch{}finally{if(session!=null)session.Dispose();if(ReferenceEquals(session,cueSession))cueSession=null;}
        }
        /// <summary>失焦和卸载时同步释放音效，不把提示音带入游戏。</summary>
        private void StopCue(){auditionActive=false;if(cueCancel!=null){cueCancel.Cancel();cueCancel.Dispose();cueCancel=null;}if(cueSession!=null){cueSession.Dispose();cueSession=null;}}
    }
}


