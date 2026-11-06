using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>主题装饰偏好独立于媒体、账号与游戏数据；旧安装采用完整默认值。</summary>
    [DataContract]
    public sealed class ThemeExperience
    {
        [DataMember] public int schema=2,columns=5;
        [DataMember] public int homeCount=9;
        [DataMember] public string homeSort="recent";
        [DataMember] public bool homeEntrance=true;
        [DataMember] public bool particles=true,focusBreathing=true,platinumGlow=true,homeSummary=true,detailColors=true,libraryTitles=false;
        [DataMember] public string transition="fade",libraryIcon="collection",trophyLayout="rows";
        [DataMember] public double backgroundMs=650,focusMs=260;
        [DataMember] public string startupSound,moveSound,confirmSound,backSound,switchSound;
        [DataMember] public string wallpaperMode="crop",pageBackgroundType="image",pageWallpaper,pageVideo,startupAudioMode="separate",trophySound;
        [DataMember] public bool pageParticles=true,focusParticles=true,trophySounds=true;
        [DataMember] public double pageDim=.35,trophyVolume=.65;
        /// <summary>设置页使用独立草稿，取消不修改生效中的配置。</summary>
        public ThemeExperience Copy(){return (ThemeExperience)MemberwiseClone();}
        /// <summary>JSON反序列化不执行初始化器，因此显式恢复缺省字段。</summary>
        [OnDeserializing] private void Defaults(StreamingContext c){homeCount=9;homeSort="recent";homeEntrance=true;wallpaperMode="crop";pageBackgroundType="image";startupAudioMode="separate";pageParticles=focusParticles=trophySounds=true;pageDim=.35;trophyVolume=.65;schema=2;columns=5;particles=focusBreathing=platinumGlow=homeSummary=detailColors=true;transition="fade";libraryIcon="collection";trophyLayout="rows";backgroundMs=650;focusMs=260;}
        /// <summary>限制数值和模式，不允许畸形配置产生不可浏览的布局。</summary>
        public void Validate(){
            if(homeCount<1||homeCount>30||(homeSort!="recent"&&homeSort!="name"))throw new InvalidDataException("首页列表参数无效");
            if(wallpaperMode!="fit"&&wallpaperMode!="crop"&&wallpaperMode!="stretch")throw new InvalidDataException("未知壁纸模式");
            if(pageBackgroundType!="image"&&pageBackgroundType!="video")throw new InvalidDataException("未知背景类型");
            if(startupAudioMode!="separate"&&startupAudioMode!="source"&&startupAudioMode!="silent")throw new InvalidDataException("未知启屏声音模式");
            if(Double.IsNaN(pageDim)||pageDim<0||pageDim>1||Double.IsNaN(trophyVolume)||trophyVolume<0||trophyVolume>1)throw new InvalidDataException("背景或音量越界");
            if(schema!=2||columns<5||columns>8||Double.IsNaN(backgroundMs)||backgroundMs<0||backgroundMs>1500||Double.IsNaN(focusMs)||focusMs<0||focusMs>1000)throw new InvalidDataException("主题参数越界");if(transition!="fade"&&transition!="slide"&&transition!="none")throw new InvalidDataException("未知转场");if(libraryIcon!="collection"&&libraryIcon!="stack")throw new InvalidDataException("未知图标");if(trophyLayout!="cards"&&trophyLayout!="rows")throw new InvalidDataException("未知奖杯布局");}
        /// <summary>读取失败保留原文件并使用安全主题。</summary>
        public static ThemeExperience Load(string root){try{var value=ThemeComposer.Read<ThemeExperience>(Path.Combine(root,"theme-experience.json"));if(value.schema==1){value.schema=2;value.columns=value.columns==6?5:value.columns;value.trophyLayout="rows";}value.Validate();return value;}catch{return new ThemeExperience();}}
        /// <summary>校验后原子提交，独立备份避免改变已有组合。</summary>
        public void Save(string root){Validate();Directory.CreateDirectory(root);var path=Path.Combine(root,"theme-experience.json");var temp=path+".tmp";try{using(var file=File.Create(temp))new DataContractJsonSerializer(typeof(ThemeExperience)).WriteObject(file,this);if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
    }
    public sealed partial class ShellControl
    {
        private ThemeExperience experience;
        /// <summary>主题通过资源提供外观，宿主不读取奖杯领域数据。</summary>
        private void ApplyExperience()
        {
            layout.Resources["FocusFadeMs"]=experience.focusMs;layout.Resources["BackgroundFadeMs"]=experience.backgroundMs;
            layout.Resources["FocusBreathing"]=experience.focusBreathing;layout.Resources["PlatinumGlow"]=experience.platinumGlow;
            layout.Resources["HomeSummary"]=experience.homeSummary;layout.Resources["DetailColors"]=experience.detailColors;layout.Resources["TrophyLayout"]=experience.trophyLayout;
            layout.Resources["LibraryColumns"]=experience.columns;
            if(Application.Current!=null)Application.Current.Resources["Ps5ReducedMotion"]=settings.reducedMotion;
            background.Stretch=WallpaperStretch(experience.wallpaperMode);
            if(previousBackground!=null)previousBackground.Stretch=background.Stretch;
            RefreshPageBackground();
            layout.Resources["LibraryIconGeometry"]=Geometry.Parse(experience.libraryIcon=="stack"?"M3,7 L22,2 32,7 13,12 Z M3,13 L13,18 32,13 32,17 13,22 3,17 Z M3,23 L13,28 32,23 32,27 13,32 3,27 Z":"M2,6 H8 V31 H2 Z M11,6 H17 V31 H11 Z M21,5 L27,3 35,29 29,31 Z");
        }
        /// <summary>所有新增装饰集中配置；局部保存不重置音量、账号或手动素材。</summary>
        private void OpenThemeExperience()
        {
            var next=ThemeExperience.Load(dataRoot);
            var window=new Window{Owner=ownerWindow,Title="PS5 主题 · 动效与布局",Width=780,Height=760,Background=new SolidColorBrush(Color.FromRgb(16,21,31)),Foreground=Brushes.White,FontSize=18,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var panel=new StackPanel{Margin=new Thickness(24)};window.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            AddThemeToggle(panel,"启屏粒子与首页入场",next.particles,v=>next.particles=v);
            AddThemeToggle(panel,"选中边框与封面缓慢呼吸",next.focusBreathing,v=>next.focusBreathing=v);
            AddThemeToggle(panel,"首页简洁奖杯摘要",next.homeSummary,v=>next.homeSummary=v);
            AddThemeToggle(panel,"白金微光与一次扫光",next.platinumGlow,v=>next.platinumGlow=v);
            AddThemeToggle(panel,"详情显示四色奖杯计数",next.detailColors,v=>next.detailColors=v);
            AddThemeToggle(panel,"游戏库显示标题和平台",next.libraryTitles,v=>next.libraryTitles=v);
            AddThemeChoice(panel,"壁纸转场",new[]{"交叉淡入","轻移淡入（完整画面）","立即切换"},new[]{"fade","slide","none"},next.transition,v=>next.transition=v);
            AddThemeChoice(panel,"游戏库图标",new[]{"收藏书脊","叠放卡片"},new[]{"collection","stack"},next.libraryIcon,v=>next.libraryIcon=v);
            AddThemeChoice(panel,"奖杯详情布局",new[]{"横向奖杯卡片","纵向奖杯列表"},new[]{"cards","rows"},next.trophyLayout,v=>next.trophyLayout=v);
            AddThemeChoice(panel,"海报密度",new[]{"5 列","6 列","7 列","8 列"},new[]{"5","6","7","8"},next.columns.ToString(),v=>next.columns=Int32.Parse(v));
            panel.Children.Add(new TextBlock{Text="壁纸转场时长（毫秒）",Margin=new Thickness(0,12,0,0)});var duration=new Slider{Minimum=0,Maximum=1500,Value=next.backgroundMs,TickFrequency=50,IsSnapToTickEnabled=true};panel.Children.Add(duration);
            panel.Children.Add(new TextBlock{Text="卡片切换时长（毫秒）",Margin=new Thickness(0,12,0,0)});var focus=new Slider{Minimum=0,Maximum=1000,Value=next.focusMs,TickFrequency=20,IsSnapToTickEnabled=true};panel.Children.Add(focus);
            AddThemeAudio(panel,window,"开机音效",next.startupSound,v=>next.startupSound=v);AddThemeAudio(panel,window,"游戏切换",next.switchSound,v=>next.switchSound=v);
            AddThemeAudio(panel,window,"焦点移动",next.moveSound,v=>next.moveSound=v);AddThemeAudio(panel,window,"确认 / 选中",next.confirmSound,v=>next.confirmSound=v);AddThemeAudio(panel,window,"返回",next.backSound,v=>next.backSound=v);
            var message=new TextBlock{Text="减少动效覆盖以上动画；声音开关及音量沿用界面音效设置。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,14)};panel.Children.Add(message);
            var save=new Button{Content="保存并应用",Padding=new Thickness(12)};panel.Children.Add(save);save.Click+=(s,e)=>{try{next.backgroundMs=duration.Value;next.focusMs=focus.Value;next.Save(dataRoot);experience=next;ApplyExperience();SetLibraryLayout(showAll);AnimateSelection();window.Close();}catch{message.Text="保存失败，原设置保留";}};
            window.ShowDialog();
        }
        /// <summary>设置项直接维护草稿，关闭窗口不会隐式保存。</summary>
        private static void AddThemeToggle(Panel panel,string label,bool value,Action<bool> save){var field=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,8,0,8)};field.Checked+=(s,e)=>save(true);field.Unchecked+=(s,e)=>save(false);panel.Children.Add(field);}
        /// <summary>显示名与稳定配置键分开，不依赖WPF绑定字段。</summary>
        private static void AddThemeChoice(Panel panel,string label,string[] names,string[] keys,string selected,Action<string> save){panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,5)});var box=new ComboBox();for(int i=0;i<keys.Length;i++){var item=new ComboBoxItem{Content=names[i],Tag=keys[i]};box.Items.Add(item);if(keys[i]==selected)box.SelectedItem=item;}box.SelectionChanged+=(s,e)=>{if(box.SelectedItem!=null)save((string)((ComboBoxItem)box.SelectedItem).Tag);};panel.Children.Add(box);}
        /// <summary>本地音效可逐项替换或恢复，不复制或修改用户文件。</summary>
        private static void AddThemeAudio(Panel panel,Window owner,string label,string value,Action<string> save){var row=new StackPanel{Margin=new Thickness(0,12,0,0)};var text=new TextBlock{Text=label+"："+(value??"主题默认"),TextWrapping=TextWrapping.Wrap};row.Children.Add(text);var actions=new WrapPanel();row.Children.Add(actions);var pick=new Button{Content="选择本地音频",Margin=new Thickness(0,4,12,4)};actions.Children.Add(pick);pick.Click+=(s,e)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="音频|*.wav;*.mp3;*.flac;*.ogg;*.m4a",CheckFileExists=true};if(dialog.ShowDialog(owner)==true){save(dialog.FileName);text.Text=label+"："+dialog.FileName;}};var reset=new Button{Content="恢复默认"};actions.Children.Add(reset);reset.Click+=(s,e)=>{save(null);text.Text=label+"：主题默认";};panel.Children.Add(row);}
    }
    /// <summary>轻量绘制启屏光尘；单个绘制表面，无逐粒子控件或永久全局事件订阅。</summary>
    public sealed class StartupParticles : FrameworkElement
    {
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(33)};
        private readonly System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        /// <summary>仅可见生命周期刷新；卸载立即释放时钟。</summary>
        public StartupParticles(){IsHitTestVisible=false;timer.Tick+=(s,e)=>InvalidateVisual();Loaded+=(s,e)=>{clock.Restart();if(IsVisible)timer.Start();};IsVisibleChanged+=(s,e)=>{if(IsVisible)timer.Start();else timer.Stop();};Unloaded+=(s,e)=>{timer.Stop();clock.Stop();};}
        /// <summary>缓慢上浮光尘和扩散光晕，固定种子轨迹确保无闪烁。</summary>
        protected override void OnRender(DrawingContext dc){base.OnRender(dc);double t=clock.Elapsed.TotalSeconds;for(int i=0;i<72;i++){double x=((i*0.61803398875+t*.006)%1)*ActualWidth;double y=(1-((i*.371+t*(.018+i%3*.004))%1))*ActualHeight;double alpha=.12+.45*(.5+.5*Math.Sin(t*.8+i));var brush=new SolidColorBrush(Color.FromArgb((byte)(alpha*255),206,226,245));dc.DrawEllipse(brush,null,new Point(x,y),i%7==0?2:1,i%7==0?2:1);}var glow=new RadialGradientBrush(Color.FromArgb(45,175,202,232),Colors.Transparent);dc.DrawEllipse(glow,null,new Point(ActualWidth*.5,ActualHeight*.75),ActualWidth*.45,ActualHeight*.7);}
    }
}
