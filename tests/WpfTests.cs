using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ModularShell;
using ModularShell.PlayniteIntegration;

/// <summary>真实 WPF 消息循环内验证主题回退、媒体取消和本地 PCM 解码。</summary>
public static class WpfTests
{
    public sealed class AchievementCounts : System.ComponentModel.INotifyPropertyChanged
    {
        private int total;
        public int AchievementCount {get{return total;}set{total=value;if(PropertyChanged!=null)PropertyChanged(this,new System.ComponentModel.PropertyChangedEventArgs("AchievementCount"));}}
        public int UnlockedCount {get{return 2;}}
        public object SelectedGame {get;set;}
        public object ModernTheme {get;set;}
        public object[] GamesWithAchievements {get;set;}
        public object[] Achievements {get;set;}
        public bool IsCompleted {get;set;}
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }
    private sealed class TestPlatformViews : IPlatformViews
    {
        public bool Available, Fail;
        public readonly List<string> Requested = new List<string>();
        public GameItem LastItem;
        public readonly AchievementCounts Counts=new AchievementCounts();
        /// <summary>模拟真实平台缺依赖或依赖可用，不加载 PA 本体。</summary>
        public bool HasExtension(string id) { return Available; }
        /// <summary>记录公开控件调用，防止默认页面提前请求隐藏内容列表。</summary>
        public object CreateView(string id, string name, GameItem item)
        {
            if (Fail) throw new InvalidOperationException("模拟外部控件失败");
            Requested.Add(name); LastItem = item;
            Counts.SelectedGame=new {Id=Guid.Parse(item.ItemId)};
            Counts.ModernTheme=new {SelectedGameId=Guid.Parse(item.ItemId)};
            Counts.GamesWithAchievements=new object[]{new {GameId=Guid.Parse(item.ItemId),GoldCount=1,SilverCount=0,BronzeCount=1}};
            Counts.Achievements=new object[0];
            return new System.Windows.Controls.TextBlock { Text = "模拟 PA 控件：" + name,DataContext=Counts };
        }
    }
    private static int count;
    private sealed class TestSession : IMusicSession
    {
        public bool Played; public bool Disposed; public double Volume;
        /// <summary>记录宿主是否调用播放。</summary>
        public void Play() { if (Disposed) throw new Exception("已释放会话被播放"); Played = true; }
        /// <summary>记录当前淡入音量。</summary>
        public void SetVolume(double value) { if (Disposed) throw new Exception("已释放会话被调音"); Volume = value; }
        /// <summary>重复释放保持幂等。</summary>
        public void Dispose() { Disposed = true; }
    }
    private sealed class FakeBackend : IMusicBackend
    {
        public readonly TestSession Session = new TestSession();
        public readonly TaskCompletionSource<IMusicSession> Pending = new TaskCompletionSource<IMusicSession>();
        public bool Delay; public int Requests;
        /// <summary>可模拟忽略取消且迟到的后端。</summary>
        public Task<IMusicSession> PrepareAsync(string path, CancellationToken token)
        {
            Requests++; return Delay ? Pending.Task : Task.FromResult<IMusicSession>(Session);
        }
    }
    private sealed class ReopeningMusic : IMusicBackend
    {
        public int Requests; public TestSession Last;
        /// <summary>每次准备返回新会话，覆盖片尾恢复而不是重用已释放对象。</summary>
        public Task<IMusicSession> PrepareAsync(string path,CancellationToken token){Requests++;Last=new TestSession();return Task.FromResult<IMusicSession>(Last);}
    }
    private sealed class FakeVideo : IVideoBackend, IVideoSession
    {
        public bool Disposed; public double Volume; public int Requests;
        public bool Delay;
        public readonly TaskCompletionSource<IVideoSession> Pending = new TaskCompletionSource<IVideoSession>();
        public int Width { get { return 2; } } public int Height { get { return 2; } }
        public bool Ended { get { return false; } }
        /// <summary>模拟视频首帧准备及忽略取消的迟到结果。</summary>
        public Task<IVideoSession> PrepareAsync(string path, CancellationToken token) { Requests++; return Delay ? Pending.Task : Task.FromResult<IVideoSession>(this); }
        /// <summary>测试帧版本只用于协调器，不冒充真实解码。</summary>
        public bool CopyFrame(byte[] bytes, ref long version) { version++; return true; }
        /// <summary>记录当前声音归属。</summary>
        public void SetVolume(double value) { if (Disposed) throw new Exception("已释放视频被调音"); Volume = value; }
        /// <summary>测试会话无需实际播放时钟。</summary>
        public void SetPaused(bool value) { }
        /// <summary>记录生命周期结束。</summary>
        public void Dispose() { Disposed = true; }
    }
    private sealed class FailedMusic : IMusicBackend
    {
        /// <summary>模拟纯音乐失败，视频必须独立继续。</summary>
        public Task<IMusicSession> PrepareAsync(string path, CancellationToken token) { throw new IOException("测试音乐失败"); }
    }
    /// <summary>断言失败使本轮构建失败。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        count++; Console.WriteLine("PASS " + message);
    }
    /// <summary>无可见窗口地启动 WPF Dispatcher，所有媒体操作在同一线程。</summary>
    [STAThread]
    public static int Main()
    {
        int result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) => {
            try { await Run(); Console.WriteLine("WPF checks: " + count); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        };
        app.Run(); return result;
    }
    /// <summary>测试只使用新建临时目录和生成的静音素材。</summary>
    private static async Task Run()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "modular-shell-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "themes"));
        try
        {
            string diagnostic;
            var achievementAssembly = System.Reflection.Assembly.LoadFrom(Path.Combine(root,"plugins","Achievements","Achievements.dll"));
            var achievementModule = (IModule)Activator.CreateInstance(achievementAssembly.GetType("AchievementAdapter.Module"));
            var platform = new TestPlatformViews();
            ((IConfiguredModule)achievementModule).Configure(new ModuleContext { UserDataRoot = temp, PlatformViews = platform });
            achievementModule.Initialize();
            var provider = (IContextViewProvider)achievementModule;
            var game = new GameItem { Provider = "playnite", SourceInstance = "local", ItemId = Guid.NewGuid().ToString("D") };
            Check(!provider.CanDisplay(game), "缺 PA 时收起可选奖杯入口");
            platform.Available = true;
            Check(provider.CanDisplay(game) && !provider.CanDisplay(new GameItem { Provider = "emby", ItemId = game.ItemId }), "成就适配限定完整 Playnite 内容引用");
            var achievementView = (System.Windows.Controls.Grid)provider.CreateView(game);
            Check(platform.Requested.SequenceEqual(new[] { "AchievementProgressBar" }) && ReferenceEquals(platform.LastItem,game), "奖杯详情只借用PA公开上下文，不嵌入第三方表格");
            var cardType=achievementAssembly.GetType("AchievementAdapter.TrophyCard");
            var secret=new { Name="秘密标题", Description="秘密条件", Hidden=true, Unlocked=false, Icon="private.png" };
            var masked=cardType.GetMethod("Read").Invoke(null,new object[]{secret,false});
            Check((bool)cardType.GetProperty("Masked").GetValue(masked,null) && (string)cardType.GetProperty("Title").GetValue(masked,null)=="隐藏奖杯" && cardType.GetProperty("Image").GetValue(masked,null)==null,"隐藏奖杯从展示模型剔除标题、描述和图片");
            var revealed=cardType.GetMethod("Read").Invoke(null,new object[]{secret,true});
            Check((string)cardType.GetProperty("Title").GetValue(revealed,null)=="秘密标题","用户明确展开后可查看隐藏奖杯");
            var theme=new ThemeExperience { columns=8,transition="slide",startupSound="local.wav" };theme.Save(temp);
            var restoredTheme=ThemeExperience.Load(temp);
            Check(restoredTheme.columns==8&&restoredTheme.transition=="slide"&&restoredTheme.startupSound=="local.wav","主题密度、转场和本地声音独立持久化");
            bool invalidTheme=false;try{theme.columns=99;theme.Save(temp);}catch{invalidTheme=true;}
            Check(invalidTheme&&ThemeExperience.Load(temp).columns==8,"非法主题配置不覆盖已保存配置");
            var summary=(System.Windows.Controls.Border)((IContextSummaryProvider)achievementModule).CreateSummary(game);
            await Task.Delay(1);
            var snapshotProperty=summary.GetType().GetProperty("Snapshot");
            Func<object,string,object> field=(obj,name)=>obj.GetType().GetField(name).GetValue(obj);
            Check(!(bool)field(snapshotProperty.GetValue(summary,null),"Available"),"PA 零计数显示不可用而非四等级零奖杯");
            platform.Counts.AchievementCount=12;await Task.Delay(1);
            var snapshot=snapshotProperty.GetValue(summary,null);
            Check((bool)field(snapshot,"Available")&&(string)field(snapshot,"Gold")=="1"&&(string)field(snapshot,"Silver")=="0","PA 当前游戏公开计数刷新，保留真实零数量");
            var snapshotType=achievementAssembly.GetType("AchievementAdapter.TrophySnapshot");
            Func<object,object,bool,object> readTrophies=(selected,achievements,completed)=>snapshotType.GetMethod("Read").Invoke(null,new object[]{game.ItemId,selected,new object[]{new {GameId=Guid.Parse(game.ItemId),GoldCount=1234,SilverCount=5678,BronzeCount=9012}},achievements,completed,12,12});
            Check(!(bool)field(readTrophies(Guid.NewGuid(),new object[0],true),"Available"),"上一游戏上下文不得进入当前奖杯栏");
            Check(!(bool)field(readTrophies(game.ItemId,new object[]{new {IsCapstone=true,Unlocked=false}},true),"Platinum"),"最终成就未解锁时不由完成率绕过白金条件");
            Check((bool)field(readTrophies(game.ItemId,new object[]{new {IsCapstone=true,Unlocked=true}},false),"Platinum"),"最终成就已解锁显示主题白金");
            Check((bool)field(readTrophies(game.ItemId,new object[0],true),"Platinum")&&!(bool)field(readTrophies(game.ItemId,null,true),"Platinum"),"无最终成就的完成游戏显示白金，未加载列表不能误判");
            Check((string)field(readTrophies(game.ItemId,new object[0],true),"Gold")=="1234","三色保留提供者四位数原始计数");
            var percentageConverter=(System.Windows.Data.IMultiValueConverter)Activator.CreateInstance(achievementAssembly.GetType("AchievementAdapter.CompletionText"));
            Check((string)percentageConverter.Convert(new object[]{3,12},typeof(string),null,System.Globalization.CultureInfo.InvariantCulture)=="25%" && (string)percentageConverter.Convert(new object[]{0,0},typeof(string),null,System.Globalization.CultureInfo.InvariantCulture)=="—","奖杯完成率使用公开计数且缺数据不伪造百分比");
            achievementModule.Dispose();
            Check(!provider.CanDisplay(game), "释放成就模块后撤销可展示能力");
            Check(!achievementAssembly.GetReferencedAssemblies().Any(a => a.Name == "Shell.Core" || a.Name == "PlayniteAchievements" || a.Name == "Playnite.SDK"), "成就外挂独立于宿主核心和 PA 实现程序集");
            var layout = ThemeComposer.Load(root, false, out diagnostic);
            Check(diagnostic == null, "真实主题文件可加载并满足槽位契约");
            var cinema = ThemeComposer.Load(root, false, "cinema", out diagnostic);
            Check(diagnostic == null && System.Windows.Controls.Grid.GetRow((System.Windows.Controls.ListBox)cinema.FindName("Games")) == 2, "影院布局改变结构并保持通用槽位");
            File.WriteAllText(Path.Combine(temp, "theme.json"), "{\"schema\":1,\"palette\":\"ember\",\"layout\":\"cinema\"}");
            ThemeComposer.SavePreference(temp, "midnight");
            Check(ThemeComposer.ReadLayoutPreference(temp) == "cinema", "切换配色保留独立布局选择");
            File.WriteAllText(Path.Combine(temp, "theme.json"), "{\"schema\":1,\"palette\":\"ember\"}");
            Check(ThemeComposer.ReadLayoutPreference(temp) == "ps5ish", "旧预设无需迁移即可回退默认布局");
            ThemeComposer.SavePreference(temp, "ember", "cinema");
            Check(ThemeComposer.ReadLayoutPreference(temp) == "cinema" && ThemeComposer.ReadPreference(temp) == "ember", "设置可保存布局与配色组合");
            bool badLayout = false;
            try { ThemeComposer.SavePreference(temp, "midnight", "invalid"); } catch { badLayout = true; }
            Check(badLayout && ThemeComposer.ReadPreference(temp) == "ember", "非法布局保存不修改现有组合");
            cinema = ThemeComposer.Load(root, false, "../invalid", out diagnostic);
            Check(diagnostic != null && cinema.FindName("Games") != null, "未知布局标识回退并拒绝路径拼接");
            var component = layout.FindResource("ActionStyle");
            ThemeComposer.ApplyPalette(layout, root, "midnight");
            var first = layout.FindResource("Accent");
            ThemeComposer.ApplyPalette(layout, root, "ember");
            Check(!Equals(first, layout.FindResource("Accent")), "同布局应用两套配色");
            Check(ReferenceEquals(component, layout.FindResource("ActionStyle")), "配色切换保留独立组件字典");
            ThemePacks.Apply(layout,root,"brisk","dusk");
            Check((double)layout.FindResource("VideoFadeMs")==150 && layout.FindResource("AmbienceBrush") is System.Windows.Media.LinearGradientBrush,"动效与氛围资产独立组合并参与实际资源解析");
            ThemeComposer.SaveCombination(temp,new ThemePreference{palette="ember",layout="cinema",motion="brisk",ambience="dusk"});
            ThemeComposer.SavePreference(temp,"midnight");
            Check(ThemeComposer.ReadCombination(temp).motion=="brisk" && ThemeComposer.ReadCombination(temp).ambience=="dusk","修改配色保留动效与资产包组合");
            var pack=layout.FindResource("AmbienceBrush");bool invalidPack=false;
            try{ThemePacks.Apply(layout,root,"../bad","ocean");}catch{invalidPack=true;}
            Check(invalidPack && ReferenceEquals(pack,layout.FindResource("AmbienceBrush")),"非法组合在资源应用前拒绝并保留原氛围");
            var animated=new System.Windows.Controls.Border();ThemePacks.Reveal(animated,layout,"PageFadeMs",true,1);
            Check(animated.Opacity==1 && !animated.HasAnimatedProperties,"减少动效不创建页面动画");
            ThemePacks.RevealContent(animated,layout,true);
            var reducedTransform=(System.Windows.Media.TranslateTransform)animated.RenderTransform;
            Check(reducedTransform.Y==0 && !reducedTransform.HasAnimatedProperties,"减少动效清除标题位移过程");
            var margin = layout.FindResource("SafeMargin"); bool invalidMetrics = false;
            try { ThemeComposer.ApplyMetrics(layout, new ThemeMetrics { schema = 1, safeMargin = Double.NaN }); } catch { invalidMetrics = true; }
            Check(invalidMetrics && Equals(margin, layout.FindResource("SafeMargin")), "非法设计变量不留下半更新");
            var before = layout.FindResource("Accent");
            File.WriteAllText(Path.Combine(temp, "themes", "palette.ember.json"), "{\"schema\":1,\"id\":\"ember\",\"surface\":\"bad\"}");
            bool rejected = false;
            try { ThemeComposer.ApplyPalette(layout, temp, "ember"); } catch { rejected = true; }
            Check(rejected && ReferenceEquals(before, layout.FindResource("Accent")), "损坏配色不留下半更新");
            File.WriteAllText(Path.Combine(temp, "themes", "layout.ps5ish.xaml"), "<broken>");
            layout = ThemeComposer.Load(temp, false, out diagnostic);
            Check(diagnostic != null && layout.FindName("Games") != null, "坏布局回退到内置副本");
            layout = ThemeComposer.Load(temp, true, out diagnostic);
            Check(diagnostic == null, "安全模式不解析坏外部布局");
            ThemeComposer.SavePreference(temp, "ember"); ThemeComposer.SavePreference(temp, "midnight");
            Check(ThemeComposer.ReadPreference(temp) == "midnight" && File.Exists(Path.Combine(temp, "theme.json.bak")), "预设原子保存与上一版本备份");
            File.WriteAllText(Path.Combine(temp, "theme.json"), "broken");
            Check(ThemeComposer.ReadPreference(temp) == "midnight", "损坏用户预设回退");

            var settings = new ShellSettings { autoVideo = false, nightMode = true };
            settings.Save(temp);
            Check(!ShellSettings.Load(temp).autoVideo && ShellSettings.Load(temp).nightMode, "媒体设置持久化");
            File.WriteAllText(Path.Combine(temp, "preview.json"), "broken");
            Check(ShellSettings.Load(temp).autoVideo, "损坏媒体设置回退默认值");
            new NavigationPreference {provider="playnite",sourceInstance="local",itemId="42",showAll=true}.Save(temp);
            var navigation = NavigationPreference.Load(temp);
            Check(navigation.showAll && navigation.itemId == "42" && navigation.provider == "playnite", "浏览位置按完整内容引用持久化");
            var input = new ControllerInput(); var actions = new List<string>(); input.Action += actions.Add;
            var now = DateTime.UtcNow;
            input.Process(0x1000, now); input.Process(0x1000, now.AddSeconds(1));
            Check(actions.SequenceEqual(new[] { "accept" }), "手柄确认长按不重复启动");
            actions.Clear(); input.Process(8, now); input.Process(8, now.AddMilliseconds(349)); input.Process(8, now.AddMilliseconds(350)); input.Process(8, now.AddMilliseconds(469)); input.Process(8, now.AddMilliseconds(470));
            Check(actions.Count == 3 && actions.All(a => a == "right"), "手柄方向重复遵守 350/120ms 限速");
            Check(ControllerInput.MapSdk(Playnite.SDK.Events.ControllerInput.A) == 0x1000 && ControllerInput.MapSdk(Playnite.SDK.Events.ControllerInput.LeftStickLeft) == ControllerInput.MapSdk(Playnite.SDK.Events.ControllerInput.DPadLeft), "SDK 手柄确认与摇杆语义映射到统一输入");

            Check(ControllerInput.IsSyntheticNavigationMessage(0x100,new IntPtr(0x27),IntPtr.Zero),"识别 Playnite 合成方向键，交给 SDK 唯一消费");
            Check(!ControllerInput.IsSyntheticNavigationMessage(0x100,new IntPtr(0x27),new IntPtr(0x014D0001)),"真实键盘扫描码不被手柄过滤器吞掉");
            Check(!ControllerInput.IsSyntheticNavigationMessage(0x100,new IntPtr(0x0D),IntPtr.Zero),"方向过滤不改变确认键和原生弹窗");
            using (var preview = new MediaPreview())
            {
                var music = new FakeBackend(); var video = new FakeVideo();
                await preview.SelectAsync(music, video, new PreviewRequest { MusicPath = "m", VideoPath = "v", TrailerAudio = true, VideoVolume = .6 }, v => { }, 0, 0);
                Check(music.Session.Disposed && Math.Abs(video.Volume - .6) < .001, "视频原声接管前释放音乐");
                preview.Cancel(); Check(video.Disposed, "离开统一释放视频");
            }
            using (var preview = new MediaPreview())
            {
                var music=new ReopeningMusic();var video=new FakeVideo();
                await preview.SelectAsync(music,video,new PreviewRequest {MusicPath="default",VideoPath="trailer",TrailerAudio=true},v=>{},0,0);
                preview.EndVideo();await Task.Delay(800);
                Check(music.Requests==2&&music.Last.Played&&!music.Last.Disposed&&music.Last.Volume>0,"预告原声结束后重新淡入默认音乐");
                preview.Cancel();int requests=music.Requests;await Task.Delay(50);
                Check(music.Last.Disposed&&music.Requests==requests,"离开页面不触发片尾音乐恢复");
            }
            using (var preview = new MediaPreview())
            {
                var old = new FakeVideo { Delay = true }; var current = new FakeVideo(); IVideoSession shown = null;
                var pending = preview.SelectAsync(null, old, new PreviewRequest { VideoPath = "old" }, v => shown = v, 0, 0);
                await preview.SelectAsync(null, current, new PreviewRequest { VideoPath = "new" }, v => shown = v, 0, 0);
                old.Pending.SetResult(old); await pending;
                Check(old.Disposed && !current.Disposed && ReferenceEquals(shown, current), "迟到视频不能覆盖新首帧");
            }
            using (var preview = new MediaPreview())
            {
                var video = new FakeVideo();
                await preview.SelectAsync(null, video, new PreviewRequest { VideoPath = "v", AutoVideo = false }, v => { }, 0, 0);
                Check(video.Requests == 0, "关闭自动视频不调用后端");
            }
            using (var preview = new MediaPreview())
            {
                var video = new FakeVideo();
                await preview.SelectAsync(new FailedMusic(), video, new PreviewRequest { MusicPath = "m", VideoPath = "v" }, v => { }, 0, 0);
                Check(preview.LastError != null && !video.Disposed, "音乐失败可诊断且不会释放成功视频");
            }

            using (var preview = new MusicPreview())
            {
                var old = new FakeBackend { Delay = true }; var current = new FakeBackend();
                var oldTask = preview.SelectAsync(old, "old", .3, 0, 20);
                var newTask = preview.SelectAsync(current, "new", .4, 0, 20);
                old.Pending.SetResult(old.Session);
                await Task.WhenAll(oldTask, newTask);
                Check(!old.Session.Played && old.Session.Disposed && current.Session.Played && !current.Session.Disposed, "迟到的旧媒体被释放且不关闭新媒体");
                Check(Math.Abs(current.Session.Volume - .4) < .0001, "宿主完成音量淡入");
                preview.Cancel(); Check(current.Session.Disposed, "离开后释放音乐");
            }
            using (var preview = new MusicPreview())
            {
                var backend = new FakeBackend(); var tasks = new List<Task>();
                for (int i = 0; i < 100; i++) tasks.Add(preview.SelectAsync(backend, "sample", .2, 30, 20));
                await Task.WhenAll(tasks);
                Check(backend.Requests == 1 && backend.Session.Played, "100 次切换仅最终选择准备音乐");
                await preview.SelectAsync(backend,"sample",.4,0,20);
                Check(backend.Requests==1 && !backend.Session.Disposed && Math.Abs(backend.Session.Volume-.4)<.001,"无专属音乐连续切换保留已播放默认曲目并更新音量");
            }
            using (var one = new MusicPreview()) using (var two = new MusicPreview())
            {
                var a = new FakeBackend(); var b = new FakeBackend();
                await one.SelectAsync(a, "one", .2, 0, 20);
                await two.SelectAsync(b, "two", .2, 0, 20);
                Check(a.Session.Disposed && b.Session.Played, "多个视图只有一个音乐所有者");
            }
            using (var modules = new ModuleLoader())
            {
                File.WriteAllText(Path.Combine(temp,"assets.json"), "{\"schema\":1,\"items\":[{\"provider\":\"playnite\",\"sourceInstance\":\"local\",\"itemId\":\"a\",\"music\":\"local.wav\",\"volume\":0}]}");
                await modules.LoadAsync(Path.Combine(root,"plugins"), false, CancellationToken.None, 2000, new ModuleContext {UserDataRoot=temp});
                var source = modules.GetCapability<IAssetProvider>();
                var asset = source.Find(new GameItem {Provider="playnite",SourceInstance="local",ItemId="a"});
                Check(asset != null && asset.MusicVolume == 0 && Math.Abs(asset.VideoVolume-.5)<.001, "独立素材插件保留零音量与缺省视频音量");
                Check(source.Find(new GameItem {Provider="emby",SourceInstance="local",ItemId="a"}) == null, "跨来源相同 ID 不共享素材");
            }
            using (var modules = new ModuleLoader())
            {
                modules.Load(Path.Combine(root, "plugins"), false);
                var backend = modules.GetCapability<IMusicBackend>();
                Check(backend != null, "从独立插件发现音乐能力");
                string wav = Path.Combine(temp, "silence.wav");
                WriteSilence(wav);
                using (var session = await backend.PrepareAsync(wav, CancellationToken.None))
                {
                    session.SetVolume(0); session.Play(); await Task.Delay(100);
                    Check(true, "真实 PCM 音频触发 MediaOpened 并执行静音播放");
                }
                using (File.Open(wav, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(true, "媒体关闭后文件可独占打开");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); bool stopped = false;
                    try { await backend.PrepareAsync(wav, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                    Check(stopped, "真实后端拒绝已取消请求");
                }
                using (var preview = new MusicPreview())
                {
                    await preview.SelectAsync(backend, Path.Combine(temp, "missing.wav"), .2, 0, 20);
                    Check(preview.LastError != null, "缺失音乐形成可恢复诊断");
                }
            }
        }
        finally { Directory.Delete(temp, true); }
    }
    /// <summary>生成一秒 PCM 静音 WAV，无第三方素材且不会发出可听音量。</summary>
    private static void WriteSilence(string path)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(32000); writer.Write(new byte[32000]);
        }
    }
}

