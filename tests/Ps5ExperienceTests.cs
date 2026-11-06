using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModularShell;
using ModularShell.PlayniteIntegration;

/// <summary>本轮背景、设置及动态渲染专项；可选读取 SDK 已导出的只读游戏目录。</summary>
public static class Ps5ExperienceTests
{
    private sealed class Source : IGameSource
    {
        public IList<GameItem> Items;
        /// <summary>使用本地测试快照，不连接平台或启动游戏。</summary>
        public IList<GameItem> Read(){return Items;}
        /// <summary>验收程序禁止启动任何游戏。</summary>
        public void Start(GameItem item){throw new InvalidOperationException("测试禁止启动游戏");}
    }
    private static string root=AppDomain.CurrentDomain.BaseDirectory;
    private static string output=Path.Combine(root,"ps5-validation");
    /// <summary>单个 STA 窗口提供真实 WPF 生命周期，测试后正常关闭。</summary>
    [STAThread] public static int Main(string[] args)
    {
        int code=0;var app=new Application();app.Startup+=async(s,e)=>{try{await Run(args);}catch(Exception ex){Console.Error.WriteLine(ex);code=1;}finally{app.Shutdown();}};app.Run();return code;
    }
    /// <summary>测试私有协调状态以验证实际会话释放，不借助截图推测后台状态。</summary>
    private static object Field(object target,string name){return target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(target);}
    /// <summary>调用被真实按钮共用的行为，不构造独立假实现。</summary>
    private static void Call(object target,string name,params object[] args){target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(target,args);}
    /// <summary>统一 1920×1080 输出，记录实际组件渲染。</summary>
    private static void Capture(FrameworkElement view,string name)
    {
        view.UpdateLayout();var bitmap=new RenderTargetBitmap(1920,1080,144,144,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,name+".png")))png.Save(file);
    }
    /// <summary>失败立即终止，避免把没有生效的配置计入验收。</summary>
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    /// <summary>测试模式迁移、背景首帧、取消、设置可见性及组件焦点效果。</summary>
    private static async Task Run(string[] args)
    {
        // 通过实际 WPF 时钟验证方向与回弹，不能把静态渐变当作扫光。
        var ring=new Border{Width=180,Height=60,CornerRadius=new CornerRadius(3)};
        var motionWindow=new Window{Content=ring,Width=220,Height=100,ShowInTaskbar=false};motionWindow.Show();await Task.Delay(150);
        ThemePacks.AnimateBorder(ring,true);double smallest=1;
        for(int tick=0;tick<20;tick++){await Task.Delay(10);smallest=Math.Min(smallest,((ScaleTransform)ring.RenderTransform).ScaleX);}
        Check(smallest<.99,"焦点框切换先收缩");
        await Task.Delay(260);Check(Math.Abs(((ScaleTransform)ring.RenderTransform).ScaleX-1)<.001,"焦点框回弹回原尺寸");
        var sweep=(LinearGradientBrush)ring.Background;double initial=sweep.GradientStops[1].Offset;
        await Task.Delay(4850);double middle=sweep.GradientStops[1].Offset;
        await Task.Delay(450);Check(middle>initial&&sweep.GradientStops[1].Offset>middle,"稳定悬停光带从左向右移动");
        ThemePacks.AnimateBorder(ring,false);Check(ring.Background==null,"离开选项撤销扫光背景");motionWindow.Hide();
        // 显示值允许缓动，业务值必须立即提交；减少动效不得遗留动画时钟。
        var slider=new SmoothOptionSlider{Minimum=0,Maximum=1,Value=.2};var sliderWindow=new Window{Content=slider,Width=300,Height=100,ShowInTaskbar=false};sliderWindow.Show();await Task.Delay(50);
        slider.Value=.8;Check(slider.Value==.8,"滑块业务值同步提交");await Task.Delay(35);Check((double)slider.GetValue(SmoothOptionSlider.DisplayValueProperty)<.8,"滑块业务值立即提交、视觉值缓动");await Task.Delay(200);
        Check(Math.Abs((double)slider.GetValue(SmoothOptionSlider.DisplayValueProperty)-.8)<.001,"滑块缓动准确到达目标");
        slider.ReduceMotion=()=>true;slider.Value=.3;Check((double)slider.GetValue(SmoothOptionSlider.DisplayValueProperty)==.3,"减少动效时滑块立即定位");sliderWindow.Hide();
        // 三色测试图能区分居中裁剪和错误的左侧截取，并覆盖横图与竖图。
        foreach(bool portrait in new[]{false,true}){
            int sw=portrait?100:300,sh=portrait?300:100;var bytes=new byte[sw*sh*4];
            for(int y=0;y<sh;y++)for(int x=0;x<sw;x++){int band=(portrait?y:x)/100;int offset=(y*sw+x)*4;bytes[offset+(band==0?2:band==1?1:0)]=255;bytes[offset+3]=255;}
            var source=BitmapSource.Create(sw,sh,96,96,PixelFormats.Bgra32,null,bytes,sw*4);
            var image=new CenteredWallpaperImage{Source=source,Stretch=Stretch.UniformToFill,Width=100,Height=100};image.Measure(new Size(100,100));image.Arrange(new Rect(0,0,100,100));
            var render=new RenderTargetBitmap(100,100,96,96,PixelFormats.Pbgra32);render.Render(image);var pixel=new byte[4];render.CopyPixels(new Int32Rect(50,50,1,1),pixel,4,0);
            Check(pixel[1]>240&&pixel[0]<10&&pixel[2]<10,"居中裁剪像素验证："+(portrait?"竖图":"宽图"));
        }
        Directory.CreateDirectory(output);string data=Path.Combine(output,"data");Directory.CreateDirectory(data);
        // 直接由 WPF 装载交付的通知字典，确保模板与动效资源可实例化。
        Application.Current.Resources["Ps5ReducedMotion"]=false; Application.Current.Resources["SettingRowStyle"]=new Style(typeof(Grid));
        using(var toastFile=File.OpenRead(Path.Combine(root,"themes","PlayniteAchievements","AchievementToast.xaml"))){var dictionary=(ResourceDictionary)System.Windows.Markup.XamlReader.Load(toastFile);var template=(DataTemplate)dictionary["PlayAch.Template.AchievementToast"];var toast=(FrameworkElement)template.LoadContent();toast.DataContext=new {TitleText="测试奖杯",GameName="通知模板验证",TrophyType="gold"};toast.Measure(new Size(480,120));toast.Arrange(new Rect(0,0,480,120));Check(toast.ActualWidth==480,"PA 通知模板实际装载并生成紧凑横条");}
        File.WriteAllText(Path.Combine(data,"theme-experience.json"),"{\"schema\":1,\"columns\":6,\"startupSound\":\"preserved.wav\"}");
        var migrated=ThemeExperience.Load(data);Check(migrated.columns==5&&migrated.trophyLayout=="rows"&&migrated.wallpaperMode=="crop"&&migrated.startupSound=="preserved.wav","旧设置迁移保留素材并使用新默认布局");
        new ShellSettings{startupAnimation=false,music=false,autoVideo=false,uiSounds=false}.Save(data);
        IList<GameItem> items=Enumerable.Range(0,20).Select(i=>new GameItem{Provider="test",SourceInstance="fixture",ItemId=i.ToString(),Title="本地测试游戏 "+i,IsInstalled=true}).ToList();
        if(args.Length>0&&File.Exists(args[0]))using(var stream=File.OpenRead(args[0]))items=(List<GameItem>)new DataContractJsonSerializer(typeof(List<GameItem>)).ReadObject(stream);
        var config=new ThemeExperience{pageWallpaper=items.Select(i=>i.BackgroundPath).FirstOrDefault(File.Exists),pageVideo=Path.Combine(root,"fixtures","1080p.mp4")};config.Save(data);
        var view=new ShellControl(new Source{Items=items},root,data);var window=new Window{Content=view,Width=1280,Height=720,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Title="PS5 专项验收 · 禁止启动游戏"};window.Show();
        try{
            for(int i=0;i<100&&Field(view,"modules")==null;i++)await Task.Delay(100);await Task.Delay(2000);
            var layout=(FrameworkElement)((Viewbox)((Grid)view.Content).Children[0]).Child;Call(view,"ShowLibrary",false);await Task.Delay(1200);
            var games=(ListBox)layout.FindName("Games");
            var homeOptions=(ThemeExperience)Field(view,"experience");homeOptions.homeCount=5;homeOptions.homeSort="name";Call(view,"ShowLibrary",false);
            Check(games.Items.Count==6,"首页数量配置包含五款游戏及独立游戏库入口");
            homeOptions.homeCount=9;homeOptions.homeSort="recent";Call(view,"ShowLibrary",false);
            var scene=(Image)layout.FindName("Scene");
            foreach(string mode in new[]{"fit","crop","stretch"}){((ThemeExperience)Field(view,"experience")).wallpaperMode=mode;Call(view,"ApplyExperience");Check(scene.Stretch==ShellControl.WallpaperStretch(mode),"壁纸模式生效："+mode);Capture(view,"home-"+mode);}
            ((ThemeExperience)Field(view,"experience")).wallpaperMode="crop";Call(view,"ApplyExperience");Call(view,"ShowLibrary",true);await Task.Delay(500);Capture(view,"library");
            // 实际工具按钮必须有足够容器高度，搜索层必须加载主题下划线模板。
            var tool=(Button)Field(view,"librarySearch");tool.Focus();await Task.Delay(350);
            Check(tool.ActualWidth==tool.ActualHeight,"游戏库搜索按钮保持正圆尺寸");Capture(view,"library-circle-focus");
            Call(view,"OpenSearchKeyboard");await Task.Delay(200);Capture(view,"search-underlined");Call(view,"NavigateLibrary","back");
            Call(view,"OpenSettings");await Task.Delay(150);Check(((Grid)Field(view,"libraryPage")).Visibility==Visibility.Collapsed,"设置页不透出游戏库内容");Capture(view,"settings");
            var draft=(ThemeExperience)Field(view,"settingsDraft");draft.wallpaperMode="stretch";Call(view,"CloseOverlay");Check(((Grid)Field(view,"keyboardPage")).Visibility==Visibility.Visible,"改动退出显示确认层");var confirmation=(StackPanel)((Grid)Field(view,"keyboardPage")).Children[0];((Button)confirmation.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(((ThemeExperience)Field(view,"experience")).wallpaperMode=="crop","取消设置不提交草稿");
            var experience=(ThemeExperience)Field(view,"experience");experience.pageBackgroundType="video";Call(view,"RefreshPageBackground");
            var film=(Image)Field(view,"pageFilm");for(int i=0;i<100&&film.Source==null;i++)await Task.Delay(100);Check(film.Source!=null,"普通页视频壁纸呈现真实解码帧");Capture(view,"library-video");
            // 直接记录渲染帧供生成动态证据，不截取其他应用或用户桌面。
            for(int i=0;i<60;i++){Capture(view,"motion-"+i.ToString("D3"));await Task.Delay(33);}
            view.SetSession("测试游戏运行",true);Check(Field(view,"pageVideoCancel")==null&&film.Source==null,"进入游戏会话立即取消壁纸视频");view.SetSession("测试返回",false);await Task.Delay(300);
            ((ShellSettings)Field(view,"settings")).reducedMotion=true;Call(view,"RefreshPageBackground");Check(film.Source==null&&((FrameworkElement)Field(view,"pageDust")).Visibility==Visibility.Collapsed,"减少动效停用视频与粒子");
            ((ShellSettings)Field(view,"settings")).reducedMotion=false;experience.pageVideo=Path.Combine(output,"missing.mp4");Call(view,"RefreshPageBackground");await Task.Delay(300);Check(film.Source==null,"损坏动态壁纸保留静态回退");
            experience.pageBackgroundType="image";Call(view,"ShowLibrary",false);await Task.Delay(1200);Capture(view,"home-final");
            Console.WriteLine("证据目录："+output);
        }finally{window.Close();}
    }
}
