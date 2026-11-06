using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl
    {
        private Grid pageBackdrop,ps5Settings;
        private Image pageImage,pageFilm;
        private Border pageShade;
        private StartupParticles pageDust;
        private DispatcherTimer presentationTimer;
        private CancellationTokenSource pageVideoCancel;
        private string pageVideoKey,pageImageKey;
        private int pageImageGeneration;
        private ThemeExperience settingsDraft;
        private ShellSettings mediaSettingsDraft;
        private StackPanel settingRows,settingCategories;
        private Border focusOverlay;
        private FocusSparkles focusSparkles;
        private int settingsCategory;
        private string settingsBaseline;
        private Border activeOptionBorder;
        private bool auditionActive;
        private DateTime nextSteamRefresh;

        /// <summary>只定位控件模板内的焦点边框，不修改内容图片或领域模块。</summary>
        private static Border FindOptionBorder(DependencyObject node)
        {
            var border=node as Border;
            if(border!=null&&(border.Name=="Row"||border.Name=="Body"||border.Name=="FocusRing"||border.Name=="Frame"))return border;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var found=FindOptionBorder(VisualTreeHelper.GetChild(node,i));if(found!=null)return found;}return null;
        }
        /// <summary>焦点移动立即撤销旧动画，新焦点复用游戏图标的渐变效果。</summary>
        private void UpdateOptionFocus(DependencyObject node)
        {
            if(activeOptionBorder!=null)ThemePacks.AnimateBorder(activeOptionBorder,false);activeOptionBorder=null;
            while(node!=null&&!(node is Control))node=VisualTreeHelper.GetParent(node);
            var control=node as Control;if(control==null||control is ListBox)return;
            // 滑块嵌入设置行时，焦点动效属于整行，不能围住滑块圆点。
            if(control is Slider){var parent=VisualTreeHelper.GetParent(control);while(parent!=null&&!(parent is Button))parent=VisualTreeHelper.GetParent(parent);if(parent is Button)control=(Button)parent;}
            activeOptionBorder=FindOptionBorder(control);ThemePacks.AnimateBorder(activeOptionBorder,!settings.reducedMotion);
        }
        /// <summary>选项确认使用短淡入，保持布局位置不晃动；减少动效直接提交。</summary>
        private void AnimateOption(Button button)
        {
            if(settings.reducedMotion)return;var content=button.Content as UIElement??button;
            if(content!=null)content.BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(.5,1,TimeSpan.FromMilliseconds(160)));
        }

        /// <summary>用户定义的三种模式；填充刻意保留原有等比完整画面语义。</summary>
        public static Stretch WallpaperStretch(string mode){return mode=="stretch"?Stretch.Fill:mode=="fit"?Stretch.Uniform:Stretch.UniformToFill;}
        /// <summary>游戏库和通用覆盖页使用全局壁纸，影视页自行管理正片与预览。</summary>
        private bool IsGeneralPage {get{return hubSurface.Visibility != Visibility.Visible && (showAll||((Grid)layout.FindName("SettingsPage")).Visibility==Visibility.Visible||contextPage.Visibility==Visibility.Visible&&!(contextContent.Content is IPageMedia)&&!(contextContent.Content is IPageActivity));}}

        /// <summary>背景仅有一个绘制层；轮询只协调状态，解码与图像载入均可取消。</summary>
        private void InitializePs5Presentation()
        {
            var root=(Panel)layout;root.ClipToBounds=true;
            pageBackdrop=new Grid {IsHitTestVisible=false,Visibility=Visibility.Collapsed,Background=new LinearGradientBrush(Color.FromRgb(40,42,49),Color.FromRgb(5,10,17),20)};
            pageImage=new CenteredWallpaperImage{Width=1280,Height=720};pageFilm=new CenteredWallpaperImage{Width=1280,Height=720};pageBackdrop.Children.Add(pageImage);pageBackdrop.Children.Add(pageFilm);
            var haze=new Border {Background=new RadialGradientBrush(Color.FromArgb(45,192,181,165),Colors.Transparent){Center=new Point(.13,.18),GradientOrigin=new Point(.13,.18),RadiusX=.8,RadiusY=1}};pageBackdrop.Children.Add(haze);
            pageShade=new Border {Background=Brushes.Black};pageBackdrop.Children.Add(pageShade);
            root.Children.Insert(root.Children.IndexOf(homeGrid),pageBackdrop);
            pageDust=new StartupParticles{Opacity=.35,Visibility=Visibility.Collapsed};root.Children.Insert(root.Children.IndexOf(homeGrid),pageDust);
            focusSparkles=new FocusSparkles();focusOverlay=new Border {Child=focusSparkles,IsHitTestVisible=false,Visibility=Visibility.Collapsed,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};root.Children.Add(focusOverlay);Panel.SetZIndex(focusOverlay,1);
            // 旧控件保留为兼容槽位，新的全屏设置负责其编辑，不打开额外配置窗口。
            var settingsPage=(Grid)layout.FindName("SettingsPage");foreach(UIElement child in settingsPage.Children)child.Visibility=Visibility.Collapsed;
            ps5Settings=new Grid{Margin=new Thickness(50,25,65,32)};
            ps5Settings.RowDefinitions.Add(new RowDefinition{Height=new GridLength(80)});ps5Settings.RowDefinitions.Add(new RowDefinition());ps5Settings.RowDefinitions.Add(new RowDefinition{Height=new GridLength(44)});
            ps5Settings.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(350)});ps5Settings.ColumnDefinitions.Add(new ColumnDefinition());
            ps5Settings.Children.Add(new TextBlock{Text="设置",FontSize=30,FontWeight=FontWeights.Light,Foreground=Brushes.White});
            var categories=new StackPanel{Margin=new Thickness(40,16,42,0)};settingCategories=categories;Grid.SetRow(categories,1);ps5Settings.Children.Add(categories);
            string[] names={"显示与壁纸","动效","声音","启屏","游戏库","奖杯","浏览与维护"};
            for(int i=0;i<names.Length;i++){int index=i;var b=SettingButton(names[i],"",()=>{settingsCategory=index;BuildSettingRows();});b.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Padding='14,12' Background='Transparent'><ContentPresenter/></Border></ControlTemplate>");b.GotKeyboardFocus+=(s,e)=>{settingsCategory=index;BuildSettingRows();};categories.Children.Add(b);}
            settingRows=new StackPanel();var scroll=new ScrollViewer{Content=settingRows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetColumn(scroll,1);Grid.SetRow(scroll,1);ps5Settings.Children.Add(scroll);
            var footer=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetColumn(footer,1);Grid.SetRow(footer,2);ps5Settings.Children.Add(footer);
            footer.Children.Add(new TextBlock{Text="○ / Esc 返回",Foreground=Brushes.LightGray,FontSize=14,VerticalAlignment=VerticalAlignment.Center});
            settingsPage.Children.Add(ps5Settings);
            presentationTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};presentationTimer.Tick+=(s,e)=>RefreshPageBackground();
            Loaded+=(s,e)=>presentationTimer.Start();Unloaded+=(s,e)=>{presentationTimer.Stop();StopPageVideo();if(activeOptionBorder!=null)ThemePacks.AnimateBorder(activeOptionBorder,false);};
        }
        /// <summary>绘制原生矢量操作图标，不依赖字体中的平台符号。</summary>
        private static UIElement Ps5Glyph(string kind){return new System.Windows.Shapes.Path{Width=22,Height=22,Stroke=Brushes.White,StrokeThickness=2,Stretch=Stretch.Uniform,Data=Geometry.Parse(kind=="search"?"M10,2 A8,8 0 1 0 10,18 A8,8 0 1 0 10,2 M16,16 L24,24":"M3,2 L3,21 M0,17 L3,21 6,17 M10,4 H25 M10,10 H21 M10,16 H17")};}
        /// <summary>筛选工具使用独立侧栏，关闭后焦点返回触发按钮。</summary>
        private void OpenLibraryFilters()
        {
            keyboardReturn=libraryTools;keyboardPage.Children.Clear();keyboardPage.Children.Add(libraryFilters);keyboardPage.Visibility=Visibility.Visible;ThemePacks.RevealContent(libraryFilters,layout,settings.reducedMotion);filterButton.Focus();
        }
        /// <summary>背景显示、主页装饰和动态绘制共同服从页面、前台及游戏状态。</summary>
        private void RefreshPageBackground()
        {
            if(pageBackdrop==null)return;
            if(pendingLibraryRefresh&&!OverlayOpen)RefreshLibrary();var config=settingsDraft??experience;if(config==null)return;
            bool general=IsGeneralPage;bool active=loaded&&!busy&&IsVisible&&ownerWindow!=null&&ownerWindow.IsActive&&!startupActive;
            // 打开界面或保持前台时刷新平台；来源内部负责单飞、节流和宿主版本边界。
            if(active&&DateTime.UtcNow>=nextSteamRefresh){nextSteamRefresh=DateTime.UtcNow.AddSeconds(15);var platform=source as PlayniteSource;if(platform!=null)platform.RefreshSteamAsync(ownerWindow.DataContext,Path.Combine(dataRoot,"platform-sync.log"));}
            pageBackdrop.Visibility=general?Visibility.Visible:Visibility.Collapsed;
            libraryPage.Visibility=showAll&&!OverlayOpen?Visibility.Visible:Visibility.Collapsed;
            homeGrid.Visibility=general||OverlayOpen||manualTrailer?Visibility.Collapsed:Visibility.Visible;
            summarySlot.Visibility=gameLogo.Visibility=general||OverlayOpen?Visibility.Collapsed:Visibility.Visible;
            logoFallback.Visibility=general||OverlayOpen||gameLogo.Source!=null?Visibility.Collapsed:Visibility.Visible;
            pageDust.Visibility=active&&!settings.reducedMotion&&config.pageParticles&&!manualTrailer?Visibility.Visible:Visibility.Collapsed;
            if(!active||OverlayOpen)StopFocusParticles();
            else { var selected=games.ItemContainerGenerator.ContainerFromItem(games.SelectedItem) as FrameworkElement; if(!ReferenceEquals(focusedCard,selected)){StopFocusParticles();focusedCard=selected;} if(focusOverlay.Visibility!=Visibility.Visible&&focusedCard!=null)StartFocusParticles(); }
            pageImage.Stretch=pageFilm.Stretch=WallpaperStretch(config.wallpaperMode);pageShade.Opacity=config.pageDim;
            if(general&&pageImageKey!=config.pageWallpaper){pageImageKey=config.pageWallpaper;LoadPageImage(config.pageWallpaper);}
            bool play=general&&active&&!settings.reducedMotion&&config.pageBackgroundType=="video"&&!String.IsNullOrWhiteSpace(config.pageVideo)&&modules!=null;
            if(!play){StopPageVideo();return;}
            if(pageVideoKey==config.pageVideo)return;
            StopPageVideo();if(!showAll)media.Cancel();pageVideoKey=config.pageVideo;pageVideoCancel=new CancellationTokenSource();PlayPageVideo(config.pageVideo,pageVideoCancel.Token);
        }
        /// <summary>普通页图片按代次提交，清空设置立即恢复主题安全底色。</summary>
        private async void LoadPageImage(string path)
        {
            int generation=++pageImageGeneration;pageImage.Source=null;
            if(String.IsNullOrWhiteSpace(path))return;
            try{var image=await GameCard.LoadImageAsync(path,3840,CancellationToken.None);if(generation==pageImageGeneration)pageImage.Source=image;}catch{if(generation==pageImageGeneration)pageImage.Source=null;}
        }
        /// <summary>每次循环释放旧解码器后重新准备；坏文件只尝试一次，保留静态背景。</summary>
        private async void PlayPageVideo(string path,CancellationToken token)
        {
            try{
                var backend=modules.GetCapability<IVideoBackend>();if(backend==null)return;
                while(!token.IsCancellationRequested){
                    using(var session=await backend.PrepareAsync(path,token)){
                        token.ThrowIfCancellationRequested();session.SetVolume(0);
                        if(session.Width<=0||session.Height<=0||session.Width>3840||session.Height>2160)throw new InvalidDataException("壁纸视频尺寸不受支持");
                        var bytes=new byte[checked(session.Width*session.Height*4)];long version=0;
                        var bitmap=new WriteableBitmap(session.Width,session.Height,96,96,PixelFormats.Bgr32,null);
                        while(!session.Ended){token.ThrowIfCancellationRequested();if(session.CopyFrame(bytes,ref version)){bitmap.WritePixels(new Int32Rect(0,0,session.Width,session.Height),bytes,session.Width*4,0);pageFilm.Source=bitmap;}await Task.Delay(33,token);}
                    }
                    await Task.Delay(15,token);
                }
            }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested)status.Text="动态壁纸已回退："+ex.GetBaseException().Message;}
        }
        /// <summary>取消先使迟到帧失效；会话在异步 finally 中释放。</summary>
        private void StopPageVideo(){if(pageVideoCancel!=null){pageVideoCancel.Cancel();pageVideoCancel.Dispose();pageVideoCancel=null;}pageVideoKey=null;if(pageFilm!=null)pageFilm.Source=null;}
        /// <summary>选中稳定后才显示粒子，位置随卡片排列更新且不会拦截输入。</summary>
        private void StartFocusParticles(){if(focusedCard==null)return;ThemePacks.AnimateRing(focusedCard,experience.focusParticles&&!settings.reducedMotion);}
        /// <summary>切换选择立即停止原卡片光效，不保留不可见动画时钟。</summary>
        private void StopFocusParticles(){if(focusOverlay!=null)focusOverlay.Visibility=Visibility.Collapsed;if(focusedCard!=null)ThemePacks.AnimateRing(focusedCard,false);}
        /// <summary>进入设置创建两份独立草稿，媒体路径也支持取消。</summary>
        private void OpenPs5Settings(){settingsDraft=experience.Copy();mediaSettingsDraft=ShellSettings.Load(dataRoot);settingsBaseline=SettingsFingerprint();BuildSettingRows();ps5Settings.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));}
        /// <summary>记录所有草稿与兼容控件值，恢复原值后不再提示保存。</summary>
        private string SettingsFingerprint()
        {
            Func<object,string> fields=value=>String.Join("|",value.GetType().GetFields().OrderBy(f=>f.Name).Select(f=>f.Name+":"+Convert.ToString(f.GetValue(value),System.Globalization.CultureInfo.InvariantCulture)));
            return fields(settingsDraft)+fields(mediaSettingsDraft)+String.Join(",",new[]{"OptionAutoVideo","OptionMusic","OptionTrailerAudio","OptionReducedMotion","OptionNightMode","OptionController"}.Select(n=>GetOption(n).ToString()))+soundOption.IsChecked+":"+soundSlider.Value+":"+musicSlider.Value;
        }
        /// <summary>退出时的三向确认复用支持手柄的顶层操作层；继续编辑保留当前焦点。</summary>
        private bool ConfirmSettingsExit()
        {
            if(settingsDraft==null||settingsBaseline==SettingsFingerprint())return false;
            keyboardReturn=Keyboard.FocusedElement as Button;keyboardPage.Children.Clear();
            var panel=new StackPanel{Width=560,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            panel.Children.Add(new TextBlock{Text="保存设置改动？",Foreground=Brushes.White,FontSize=28,Margin=new Thickness(12,0,0,24)});
            var save=LibraryButton("保存改动",()=>{keyboardPage.Visibility=Visibility.Collapsed;SaveSettings();});panel.Children.Add(save);
            panel.Children.Add(LibraryButton("放弃改动",()=>{keyboardPage.Visibility=Visibility.Collapsed;settingsBaseline=SettingsFingerprint();CloseOverlay();}));
            panel.Children.Add(LibraryButton("继续编辑",()=>{keyboardPage.Visibility=Visibility.Collapsed;if(keyboardReturn!=null)keyboardReturn.Focus();}));
            keyboardPage.Children.Add(panel);keyboardPage.Visibility=Visibility.Visible;save.Focus();return true;
        }
        /// <summary>统一设置行使用左名称、右值和细焦点描边。</summary>
        private Button SettingButton(string label,string value,Action action)
        {
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            grid.Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap});var text=new TextBlock{Text=value,Foreground=new SolidColorBrush(Color.FromRgb(177,183,192)),MaxWidth=230,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(20,0,0,0)};Grid.SetColumn(text,1);grid.Children.Add(text);
            var b=new Button{Content=grid};b.Style=(Style)layout.FindResource("Ps5ShellSettingRowStyle");b.Click+=(s,e)=>{action();};b.MouseEnter+=(s,e)=>b.Focus();return b;
        }
        /// <summary>循环选项避免手柄无法操作原生下拉框；保存时使用稳定键。</summary>
        private void ChoiceRow(string label,string[] labels,string[] keys,Func<string> get,Action<string> set)
        {
            Action add=null;Button b=null;Action redraw=()=>{int index=Array.IndexOf(keys,get());((TextBlock)((Grid)b.Content).Children[1]).Text=labels[Math.Max(0,index)];};
            add=()=>{
                keyboardReturn=b;keyboardPage.Children.Clear();var options=new StackPanel{Width=650,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
                options.Children.Add(new TextBlock{Text=label,FontSize=26,Foreground=Brushes.White,Margin=new Thickness(14,0,0,20)});Button selected=null;
                for(int i=0;i<keys.Length;i++){int index=i;var option=SettingButton(labels[i],keys[i]==get()?"✓":"",()=>{set(keys[index]);redraw();RefreshPageBackground();background.Stretch=WallpaperStretch(settingsDraft.wallpaperMode);previousBackground.Stretch=background.Stretch;keyboardPage.Visibility=Visibility.Collapsed;b.Focus();});options.Children.Add(option);if(keys[i]==get())selected=option;}
                keyboardPage.Children.Add(options);keyboardPage.Visibility=Visibility.Visible;ThemePacks.RevealContent(options,layout,settings.reducedMotion);(selected??options.Children.OfType<Button>().First()).Focus();
            };
            b=SettingButton(label,"",add);redraw();settingRows.Children.Add(b);
        }
        /// <summary>布尔设置与持久化草稿共享单一状态。</summary>
        private void ToggleRow(string label,Func<bool> get,Action<bool> set){ChoiceRow(label,new[]{"关闭","开启"},new[]{"off","on"},()=>get()?"on":"off",v=>set(v=="on"));}
        /// <summary>本地素材引用不复制原文件；文件选择仅在明确操作后打开。</summary>
        private void FileRow(string label,Func<string> get,Action<string> set,string filter)
        {
            Button b=null;b=SettingButton(label,String.IsNullOrWhiteSpace(get())?"默认":Path.GetFileName(get()),()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter=filter,CheckFileExists=true};if(dialog.ShowDialog(ownerWindow)==true){set(dialog.FileName);((TextBlock)((Grid)b.Content).Children[1]).Text=Path.GetFileName(dialog.FileName);RefreshPageBackground();}});settingRows.Children.Add(b);
            settingRows.Children.Add(SettingButton("恢复默认 · "+label,"",()=>{set(null);((TextBlock)((Grid)b.Content).Children[1]).Text="默认";RefreshPageBackground();}));
        }
        /// <summary>一页一类设置，所有日常参数可在全屏手柄界面访问。</summary>
        private void BuildSettingRows()
        {
            if(settingsDraft==null)return;for(int i=0;i<settingCategories.Children.Count;i++)settingCategories.Children[i].Opacity=i==settingsCategory?1:.45;settingRows.Children.Clear();var c=settingsDraft;
            if(!settings.reducedMotion)settingRows.BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)));
            const string audio="音频|*.wav;*.mp3;*.flac;*.ogg;*.m4a";const string video="视频|*.mp4;*.mkv;*.webm;*.mov";
            if(settingsCategory==0){
                ChoiceRow("壁纸显示模式",new[]{"填充","裁剪","拉伸"},new[]{"fit","crop","stretch"},()=>c.wallpaperMode,v=>c.wallpaperMode=v);
                ChoiceRow("普通页背景",new[]{"图片","循环视频"},new[]{"image","video"},()=>c.pageBackgroundType,v=>c.pageBackgroundType=v);
                FileRow("静态壁纸 / 视频备用图",()=>c.pageWallpaper,v=>c.pageWallpaper=v,"图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp");FileRow("循环视频",()=>c.pageVideo,v=>c.pageVideo=v,video);
                ChoiceRow("背景遮暗",new[]{"轻","中","深"},new[]{"0.2","0.35","0.55"},()=>c.pageDim.ToString(System.Globalization.CultureInfo.InvariantCulture),v=>c.pageDim=Double.Parse(v,System.Globalization.CultureInfo.InvariantCulture));
            }else if(settingsCategory==1){
                ToggleRow("减少动态效果",()=>GetOption("OptionReducedMotion"),v=>{SetOption("OptionReducedMotion",v);settings.reducedMotion=v;});ToggleRow("背景光尘",()=>c.pageParticles,v=>c.pageParticles=v);ToggleRow("选中扫光与粒子",()=>c.focusParticles,v=>c.focusParticles=v);ToggleRow("选中柔光呼吸",()=>c.focusBreathing,v=>c.focusBreathing=v);ToggleRow("启屏光尘",()=>c.particles,v=>c.particles=v);
                ChoiceRow("背景转场",new[]{"淡入","轻移","立即"},new[]{"fade","slide","none"},()=>c.transition,v=>c.transition=v);
            }else if(settingsCategory==2){
                ToggleRow("界面音效",()=>soundOption.IsChecked==true,v=>soundOption.IsChecked=v);ToggleRow("背景音乐",()=>GetOption("OptionMusic"),v=>SetOption("OptionMusic",v));ToggleRow("夜间低音量",()=>GetOption("OptionNightMode"),v=>SetOption("OptionNightMode",v));
                AddVolumeRow("界面音量",soundSlider);AddVolumeRow("音乐音量",musicSlider);
                FileRow("开机音效",()=>c.startupSound,v=>c.startupSound=v,audio);FileRow("游戏切换",()=>c.switchSound,v=>c.switchSound=v,audio);FileRow("焦点移动",()=>c.moveSound,v=>c.moveSound=v,audio);FileRow("确认",()=>c.confirmSound,v=>c.confirmSound=v,audio);FileRow("返回",()=>c.backSound,v=>c.backSound=v,audio);
                foreach(string name in new[]{"startup","switch","move","confirm","back"}){string cue=name;settingRows.Children.Add(SettingButton("试听 · "+name,"",()=>AuditionCue(cue)));}
            }else if(settingsCategory==3){
                ToggleRow("首页入场动画",()=>c.homeEntrance,v=>c.homeEntrance=v);
                ToggleRow("启屏动画",()=>mediaSettingsDraft.startupAnimation,v=>mediaSettingsDraft.startupAnimation=v);
                ChoiceRow("启屏声音",new[]{"独立开机音效","视频原声","静音"},new[]{"separate","source","silent"},()=>c.startupAudioMode,v=>c.startupAudioMode=v);
                FileRow("启屏视频",()=>mediaSettingsDraft.startupVideo,v=>mediaSettingsDraft.startupVideo=v,video);FileRow("主题曲",()=>mediaSettingsDraft.backgroundMusic,v=>mediaSettingsDraft.backgroundMusic=v,audio);
            }else if(settingsCategory==4){
                ChoiceRow("首页排序",new[]{"最近游玩","名称"},new[]{"recent","name"},()=>c.homeSort,v=>c.homeSort=v);
                ChoiceRow("首页游戏数量",new[]{"5","9","12","20","30"},new[]{"5","9","12","20","30"},()=>c.homeCount.ToString(),v=>c.homeCount=Int32.Parse(v));
                ChoiceRow("游戏库密度",new[]{"5 列","6 列","7 列","8 列"},new[]{"5","6","7","8"},()=>c.columns.ToString(),v=>c.columns=Int32.Parse(v));ToggleRow("显示封面标题",()=>c.libraryTitles,v=>c.libraryTitles=v);
                ChoiceRow("游戏库图标",new[]{"收藏","叠卡"},new[]{"collection","stack"},()=>c.libraryIcon,v=>c.libraryIcon=v);
            }else if(settingsCategory==5){
                ChoiceRow("奖杯布局",new[]{"纵向列表","横向卡片"},new[]{"rows","cards"},()=>c.trophyLayout,v=>c.trophyLayout=v);ToggleRow("首页摘要",()=>c.homeSummary,v=>c.homeSummary=v);ToggleRow("四色汇总",()=>c.detailColors,v=>c.detailColors=v);ToggleRow("白金微光",()=>c.platinumGlow,v=>c.platinumGlow=v);ToggleRow("解锁音效",()=>c.trophySounds,v=>c.trophySounds=v);
                ChoiceRow("奖杯音量",new[]{"低","中","高"},new[]{"0.3","0.65","1"},()=>c.trophyVolume.ToString(System.Globalization.CultureInfo.InvariantCulture),v=>c.trophyVolume=Double.Parse(v,System.Globalization.CultureInfo.InvariantCulture));FileRow("奖杯解锁音效",()=>c.trophySound,v=>c.trophySound=v,audio);
                settingRows.Children.Add(SettingButton("试听奖杯音效","",()=>AuditionCue("trophy")));
                settingRows.Children.Add(new TextBlock{Text="通知窗口：Playnite Achievements\n声音：独立奖杯适配模块",Foreground=Brushes.LightGray,FontSize=14,Margin=new Thickness(14),TextWrapping=TextWrapping.Wrap});
            }else{
                ToggleRow("自动预告",()=>GetOption("OptionAutoVideo"),v=>SetOption("OptionAutoVideo",v));ToggleRow("预告原声",()=>GetOption("OptionTrailerAudio"),v=>SetOption("OptionTrailerAudio",v));ToggleRow("手柄",()=>GetOption("OptionController"),v=>SetOption("OptionController",v));
                settingRows.Children.Add(SettingButton("素材与缓存维护","",OpenMaintenance));settingRows.Children.Add(SettingButton("布局预设","",SwitchLayout));
                settingRows.Children.Add(new TextBlock{Text=((TextBlock)layout.FindName("PluginDiagnostic")).Text,Foreground=Brushes.LightGray,FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(14)});
            }
        }
        /// <summary>按钮逐档调整音量，避免方向导航和滑块处理冲突。</summary>
        private void AddVolumeRow(string label,Slider slider)
        {
            var row=SettingButton(label,"",()=>{});var grid=(Grid)row.Content;
            var control=new SmoothOptionSlider{Minimum=0,Maximum=1,Value=slider.Value,SmallChange=.05,LargeChange=.1,Width=220,VerticalAlignment=VerticalAlignment.Center,Focusable=true,ReduceMotion=()=>settings.reducedMotion};
            control.Style=layout.TryFindResource(typeof(Slider)) as Style;
            control.ValueChanged+=(s,e)=>{slider.Value=control.Value;PlayCue("move");};grid.Children.RemoveAt(1);Grid.SetColumn(control,1);grid.Children.Add(control);
            row.Click+=(s,e)=>control.Focus();settingRows.Children.Add(row);
        }
        /// <summary>试听使用草稿素材，关闭设置或发起下一声音时释放。</summary>
        private async void AuditionCue(string cue)
        {
            if(modules==null)return;StopCue();var backend=modules.GetCapability<IMusicBackend>();if(backend==null)return;auditionActive=true;
            var c=settingsDraft??experience;string path=cue=="startup"?c.startupSound:cue=="switch"?c.switchSound:cue=="move"?c.moveSound:cue=="back"?c.backSound:cue=="trophy"?c.trophySound:c.confirmSound;
            cueCancel=new CancellationTokenSource();var token=cueCancel.Token;IMusicSession session=null;
            try{session=await backend.PrepareAsync(String.IsNullOrWhiteSpace(path)?Path.Combine(packageRoot,"themes","audio",cue+".wav"):path,token);token.ThrowIfCancellationRequested();cueSession=session;var loop=session as IMusicLoopControl;if(loop!=null)loop.SetLooping(false);session.SetVolume(cue=="trophy"?c.trophyVolume:soundSlider.Value);session.Play();await Task.Delay(10000,token);}catch{}finally{if(session!=null)session.Dispose();if(ReferenceEquals(cueSession,session)){cueSession=null;auditionActive=false;}}
        }
    }

    /// <summary>键盘和手柄调整时平滑移动滑块；拖动时一比一跟随，不延迟保存真实值。</summary>
    public sealed class SmoothOptionSlider : Slider
    {
        public static readonly DependencyProperty DisplayValueProperty=DependencyProperty.Register("DisplayValue",typeof(double),typeof(SmoothOptionSlider),new PropertyMetadata(0.0));
        public Func<bool> ReduceMotion;
        private bool dragging;
        /// <summary>模板轨道绑定显示值，实际 Value 仍是设置的唯一保存来源。</summary>
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();var track=GetTemplateChild("PART_Track") as System.Windows.Controls.Primitives.Track;
            if(track==null)return;track.SetBinding(System.Windows.Controls.Primitives.Track.ValueProperty,new System.Windows.Data.Binding{Source=this,Path=new PropertyPath(DisplayValueProperty),Mode=System.Windows.Data.BindingMode.OneWay});
            if(track.Thumb!=null){track.Thumb.DragStarted+=(s,e)=>{dragging=true;BeginAnimation(DisplayValueProperty,null);SetValue(DisplayValueProperty,Value);};track.Thumb.DragCompleted+=(s,e)=>dragging=false;}SetValue(DisplayValueProperty,Value);
        }
        /// <summary>快按从当前显示位置续接；减少动效、拖动和初始化均立即定位。</summary>
        protected override void OnValueChanged(double oldValue,double newValue)
        {
            base.OnValueChanged(oldValue,newValue);
            if(!IsLoaded||dragging||ReduceMotion!=null&&ReduceMotion()){BeginAnimation(DisplayValueProperty,null);SetValue(DisplayValueProperty,newValue);return;}
            double current=(double)GetValue(DisplayValueProperty);BeginAnimation(DisplayValueProperty,null);SetValue(DisplayValueProperty,newValue);
            BeginAnimation(DisplayValueProperty,new System.Windows.Media.Animation.DoubleAnimation(current,newValue,TimeSpan.FromMilliseconds(140)){FillBehavior=System.Windows.Media.Animation.FillBehavior.Stop,EasingFunction=new System.Windows.Media.Animation.CubicEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut}});
        }
    }

    /// <summary>按目标视口计算居中缩放，宽图和竖图从两侧等量裁去溢出部分。</summary>
    public sealed class CenteredWallpaperImage : Image
    {
        /// <summary>图像固有比例不得扩张绘制视口，否则溢出裁剪会再次偏向左上角。</summary>
        protected override Size MeasureOverride(Size available){return new Size(Double.IsInfinity(available.Width)?1280:available.Width,Double.IsInfinity(available.Height)?720:available.Height);}
        /// <summary>保持父级安排的视口尺寸，缩放仅发生在绘制矩形中。</summary>
        protected override Size ArrangeOverride(Size finalSize){return finalSize;}
        /// <summary>绘制只使用来源尺寸与固定视口；裁剪与缩放共享同一个中心。</summary>
        protected override void OnRender(DrawingContext dc)
        {
            if(Source==null||Source.Width<=0||Source.Height<=0)return;
            double w=ActualWidth,h=ActualHeight;if(w<=0||h<=0)return;
            double sx=w/Source.Width,sy=h/Source.Height;
            if(Stretch==Stretch.Uniform)sx=sy=Math.Min(sx,sy);
            else if(Stretch==Stretch.UniformToFill)sx=sy=Math.Max(sx,sy);
            double dw=Source.Width*sx,dh=Source.Height*sy;
            dc.PushClip(new RectangleGeometry(new Rect(0,0,w,h)));
            dc.DrawImage(Source,new Rect((w-dw)/2,(h-dh)/2,dw,dh));dc.Pop();
        }
    }

    /// <summary>单一选中元素上的间歇光效，闲置时不生成可交互子元素。</summary>
    public sealed class FocusSparkles : FrameworkElement
    {
        public FrameworkElement Target,Surface;
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(33)};
        private readonly System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        /// <summary>只在可见时刷新，离页立即停表。</summary>
        public FocusSparkles(){IsHitTestVisible=false;IsVisibleChanged+=(s,e)=>{if(IsVisible)timer.Start();else timer.Stop();};Unloaded+=(s,e)=>timer.Stop();timer.Tick+=(s,e)=>{if(Target==null||Surface==null)return;try{var bounds=Target.TransformToAncestor(Surface).TransformBounds(new Rect(Target.RenderSize));var parent=(Border)Parent;parent.Margin=new Thickness(bounds.X,bounds.Y,0,0);parent.Width=bounds.Width;parent.Height=Math.Min(bounds.Width,bounds.Height);InvalidateVisual();}catch{timer.Stop();}};}
        /// <summary>每次选择重新计时，首次稳定停留一秒后才扫光。</summary>
        public void Restart(){clock.Restart();}
        /// <summary>粒子限定在封面周边，不在游戏图案上铺大面积亮色。</summary>
        protected override void OnRender(DrawingContext dc)
        {
            double t=clock.Elapsed.TotalSeconds-1;if(t<0)return;double phase=t%6;if(phase>1.4)return;
            double a=Math.Sin(phase/1.4*Math.PI);var pen=new Pen(new SolidColorBrush(Color.FromArgb((byte)(a*130),212,236,255)),1);
            dc.DrawRoundedRectangle(null,pen,new Rect(2,2,Math.Max(0,ActualWidth-4),Math.Max(0,ActualHeight-4)),2,2);
            for(int i=0;i<9;i++){double x=(i*.137+phase*.25)%1*ActualWidth;double y=i%2==0?3:ActualHeight-3;dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(a*170),231,244,255)),null,new Point(x,y),i%3==0?1.8:.8,1);}
        }
    }
}
