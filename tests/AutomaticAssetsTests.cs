using System;
using System.IO;
using System.Linq;
using System.Threading;
using LocalAssets;
using ModularShell;

/// <summary>真实文件探测回归，不用扩展名或元数据声称的4K冒充质量证据。</summary>
public static class AutomaticAssetsTests
{
    /// <summary>每项失败立即以非零返回，测试目录只位于固定artifacts。</summary>
    public static int Main()
    {
        string root=AppDomain.CurrentDomain.BaseDirectory,tools=Path.Combine(root,"plugins","LocalAssets","tools"),directory=Path.Combine(root,"automatic-tests");Directory.CreateDirectory(directory);
        try{
            Check(MediaFiles.BackgroundRank(1920,620)==0,"普通Hero低于最低高度被拒绝");
            Check(MediaFiles.BackgroundRank(3840,2160)>MediaFiles.BackgroundRank(2560,1440)&&MediaFiles.BackgroundRank(2560,1440)>MediaFiles.BackgroundRank(3840,1240)&&MediaFiles.BackgroundRank(3840,1240)>MediaFiles.BackgroundRank(1920,1080),"4K / 1440p / Hero2x / 1080p背景排序");
            string disguised=Path.Combine(directory,"真实图片.mp4");using(var image=new System.Drawing.Bitmap(1920,1080))image.Save(disguised,System.Drawing.Imaging.ImageFormat.Png);
            Check(MediaFiles.Probe(disguised,"background",tools,CancellationToken.None).status=="accepted","伪扩展名以真实文件头验收1080p图片");
            string bad=Path.Combine(directory,"broken.jpg");File.WriteAllText(bad,"not an image");bool rejected=false;try{MediaFiles.Probe(bad,"background",tools,CancellationToken.None);}catch{rejected=true;}Check(rejected,"损坏图像不能入库");
            var low=MediaFiles.Probe(Path.Combine(root,"fixtures","红色首帧.mp4"),"video",tools,CancellationToken.None);Check(low.status=="low_quality","低于1080p预告被拒绝");
            var hd=MediaFiles.Probe(Path.Combine(root,"fixtures","1080p.mp4"),"video",tools,CancellationToken.None);Check(hd.status=="accepted"&&hd.width==1920&&hd.height==1080&&hd.audioTracks==0,"1080p无音轨可静音展示且明确记录零音轨");
            var uhd=MediaFiles.Probe(Path.Combine(root,"fixtures","2160p.mp4"),"video",tools,CancellationToken.None);Check(uhd.status=="accepted"&&uhd.width==3840&&uhd.height==2160&&uhd.bitrate>0&&uhd.duration>0&&!String.IsNullOrEmpty(uhd.codec),"4K真实探测记录尺寸码率编码时长");
            using(var module=new Module()){
                module.Configure(new ModuleContext {UserDataRoot=directory});module.Initialize();module.SetEnabled(false);
                var item=new GameItem {Provider="playnite",SourceInstance="local",ItemId="quality-test",BackgroundPath=disguised};
                for(int i=0;i<100;i++)module.Request(item,true);
                Check(module.Describe(null).Contains("运行：0"),"关闭自动获取后100次请求不启动网络任务");
                module.SetLocked(item,true);module.ClearAutomatic(item);Check(module.Describe(item).Contains("已锁定"),"清理不撤销锁定状态");
            }
            QueueTests(directory);
            Check(SteamAssets.DetectLanguage("14_PV5_Multi-re_GE_None_FullHD")=="de","Steam德语预告标签可识别，未知语言不冒充中文");
            Check(SteamAssets.LanguageScore("zh-Hans","zh-Hans")>SteamAssets.LanguageScore("en","zh-Hans")&&SteamAssets.LanguageScore("en","zh-Hans")>SteamAssets.LanguageScore("de","zh-Hans"),"同清晰度预告按优先语言及英语回退排序");
            using(var preferences=new Module()){preferences.Configure(new ModuleContext {UserDataRoot=directory});preferences.SetPreferredLanguage("ja");}
            using(var preferences=new Module()){preferences.Configure(new ModuleContext {UserDataRoot=directory});Check(preferences.PreferredLanguage=="ja","预告优先语言跨重启持久化");}
            LockedLegacyTests(directory,root);
            string localRoot=Path.Combine(directory,"local-ost-"+Guid.NewGuid().ToString("N"));string album=Path.Combine(localRoot,"installed","game","main_menu","music","mp3");Directory.CreateDirectory(album);
            File.Copy(Path.Combine(root,"themes","audio","ambient.mp3"),Path.Combine(album,"01-menu-score.mp3"));
            using(var local=new Module()){
                local.Configure(new ModuleContext {UserDataRoot=localRoot});local.Initialize();var game=new GameItem {Provider="playnite",SourceInstance="local",ItemId="local-ost",Title="本机OST测试",InstallDirectory=Path.Combine(localRoot,"installed"),IsInstalled=true};
                local.Request(game,false);Wait(()=>local.Find(game).MusicPath!=null);Check(local.Find(game).MusicPath.EndsWith("01-menu-score.mp3"),"无人工索引时自动发现安装目录中的独立主菜单原声");
            }
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    /// <summary>输出可收集的逐项验收结果。</summary>
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    /// <summary>真实调度器配可取消阻塞工作，核验去重、抢占、暂停，不依赖公网时序。</summary>
    private static void QueueTests(string directory)
    {
        int currentCalls=0,parallel=0,maximum=0;
        using(var module=new Module((item,token)=>{
            int now=Interlocked.Increment(ref parallel);int old;do{old=maximum;if(old>=now)break;}while(Interlocked.CompareExchange(ref maximum,now,old)!=old);
            if(item.ItemId=="current")Interlocked.Increment(ref currentCalls);
            try{token.WaitHandle.WaitOne();token.ThrowIfCancellationRequested();}finally{Interlocked.Decrement(ref parallel);}
        })){
            module.Configure(new ModuleContext {UserDataRoot=Path.Combine(directory,"queue-"+Guid.NewGuid().ToString("N"))});module.Initialize();
            Func<string,GameItem> item=id=>new GameItem {Provider="playnite",SourceInstance="local",ItemId=id,Title=id,IsInstalled=true};
            module.Synchronize(new[]{item("background-a"),item("background-b")});Wait(()=>parallel==2);
            var current=item("current");for(int i=0;i<100;i++)module.Request(current,true);Wait(()=>currentCalls==1);
            Check(currentCalls==1&&maximum==2,"100次重复请求去重，当前游戏抢占后台且并发不超过2");
            module.SetPaused(true);Wait(()=>parallel==0);Check(parallel==0,"启动游戏暂停并取消两个后台任务");
            module.SetEnabled(false);module.SetPaused(false);Thread.Sleep(250);Check(parallel==0,"暂停期间关闭自动获取后不恢复下载");
        }
    }
    /// <summary>有限期限等待真实线程状态，失败保留有意义的测试错误。</summary>
    private static void Wait(Func<bool> condition){var end=DateTime.UtcNow.AddSeconds(5);while(!condition()){if(DateTime.UtcNow>end)throw new Exception("队列状态等待超时");Thread.Sleep(20);}}
    /// <summary>真实旧索引与低清文件证明锁定不会绕过门槛，也不会删除原文件。</summary>
    private static void LockedLegacyTests(string directory,string root)
    {
        string data=Path.Combine(directory,"locked-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(data);string image=Path.Combine(data,"user-hero.jpg");using(var bitmap=new System.Drawing.Bitmap(1920,620))bitmap.Save(image,System.Drawing.Imaging.ImageFormat.Jpeg);
        string video=Path.Combine(root,"fixtures","红色首帧.mp4");string before=MediaFiles.Hash(image);
        File.WriteAllText(Path.Combine(data,"assets.json"),"{\"schema\":1,\"items\":[{\"provider\":\"playnite\",\"sourceInstance\":\"local\",\"itemId\":\"locked\",\"locked\":true,\"customNote\":\"keep\",\"backgrounds\":[\""+image.Replace("\\","\\\\")+"\"],\"video\":\""+video.Replace("\\","\\\\")+"\"}]}");
        using(var module=new Module()){
            module.Configure(new ModuleContext {UserDataRoot=data});module.Initialize();var item=new GameItem {Provider="playnite",SourceInstance="local",ItemId="locked",Title="锁定低清测试"};module.Request(item,false);
            Wait(()=>module.Describe(null).Contains("队列：0 · 运行：0"));var value=module.Find(item);
            Check(value.BackgroundPaths.Length==0&&value.VideoPath==null&&module.Describe(item).Contains("锁定素材不达标"),"旧人工锁定低清图和预告不能进入正式候选");
            module.ClearAutomatic(item);Check(File.Exists(image)&&MediaFiles.Hash(image)==before,"锁定清理不删除或修改用户原文件");
            module.SetLocked(item,false);string index=File.ReadAllText(Path.Combine(data,"assets.json"));Check(index.Contains("\"locked\":false")&&index.Contains("customNote")&&File.Exists(Path.Combine(data,"assets.json.bak")),"解锁保留旧索引自定义字段和回退备份");
        }
    }
}
