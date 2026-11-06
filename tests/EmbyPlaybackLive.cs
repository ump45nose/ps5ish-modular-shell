using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbyBrowser;
using ModularShell;

/// <summary>只在显式命令调用时运行；find 只读，play 会短时播放并写入用户授权条目进度。</summary>
public static class EmbyPlaybackLive
{
    [STAThread] public static int Main(string[] args){if(args.Length<3)return 2;int code=0;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async(s,e)=>{try{await Run(args);}catch(Exception ex){Console.Error.WriteLine("FAIL "+ex.GetType().Name+" "+ex.Message);code=1;}finally{app.Shutdown();}};app.Run();return code;}
    /// <summary>先按真实库名定位，只有用户已授权的明确 ID 可以进入播放模式。</summary>
    private static async Task Run(string[] args)
    {
        var client=new Client(args[1]);
        if(args[0]=="find"){
            foreach(var library in await client.Libraries(CancellationToken.None)){
                if(!library.Name.Contains("电视剧") || !library.Name.Contains("下载"))continue;
                Console.WriteLine("分区 "+library.Id+" "+library.Name);
                var page=await client.Browse("library",0,library.Id,CancellationToken.None,args[2],"Series");
                foreach(var series in page.Items){Console.WriteLine("剧集 "+series.Id+" "+series.Name);
                    var seasons=await client.Browse("children",0,series.Id,CancellationToken.None);
                    foreach(var season in seasons.Items.Take(2)){Console.WriteLine("季 "+season.Id+" "+season.Name);var episodes=await client.Browse("children",0,season.Id,CancellationToken.None);foreach(var episode in episodes.Items.Take(2))Console.WriteLine("单集 "+episode.Id+" "+episode.Name);}
                }
            }return;
        }
        if(args[0]!="play")throw new ArgumentException("未知模式");
        var before=await client.Detail(args[2],CancellationToken.None);if(before.Type!="Episode" && before.Type!="Movie")throw new Exception("只能测试具体影片或单集");
        long initial=before.UserData==null?0:before.UserData.PlaybackPositionTicks;
        Console.WriteLine("基线 PositionTicks="+initial+" Played="+(before.UserData!=null && before.UserData.Played));
        var module=(IModule)Activator.CreateInstance(System.Reflection.Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugins","LocalVideo","LocalVideo.dll")).GetType("LocalVideo.Module"));module.Initialize();
        var controller=new PlaybackController(client,(IMediaPlaybackBackend)module);long last=0;
        Exception failure=null;
        try{
            await controller.Start(args[2],CancellationToken.None,0);Console.WriteLine("PASS 真实正片首帧与播放开始同步（静音）");
            // 用户授权条目的测试可显式定位到片内，避免服务器的短片头进度归零策略。
            if(args.Length>3){int seconds=Int32.Parse(args[3]);controller.Session.Seek(TimeSpan.FromSeconds(seconds).Ticks);await controller.Progress("TimeUpdate");}
            await Task.Delay(5500);var session=controller.Session;last=session.PositionTicks;
            if(last<initial || last-initial<TimeSpan.FromSeconds(2).Ticks)throw new Exception("真实播放位置未前进");
            var pixels=new byte[session.Width*session.Height*4];long version=0;if(!session.CopyFrame(pixels,ref version))throw new Exception("无可读真实画面");
            var image=BitmapSource.Create(session.Width,session.Height,96,96,PixelFormats.Bgr32,null,pixels,session.Width*4);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"emby-playback-live.png")))png.Save(file);
            controller.Pause(true);await controller.Progress("Pause");Console.WriteLine("PASS 真实暂停与进度同步 PositionTicks="+last+" AudioTracks="+session.AudioTracks.Length+" SubtitleTracks="+session.SubtitleTracks.Length);
        }catch(Exception ex){failure=ex;}
        await controller.Stop();module.Dispose();if(failure!=null)throw failure;
        if(controller.SyncFailed)throw new Exception("最终停止未确认同步");
        await Task.Delay(1500);var after=await client.Detail(args[2],CancellationToken.None);long actual=after.UserData==null?0:after.UserData.PlaybackPositionTicks;
        Console.WriteLine("停止后 PositionTicks="+actual+" Played="+(after.UserData!=null && after.UserData.Played));
        // Emby 会对片头短进度归零；为零时如实记录策略，不能声称已经证明续播位置保留。
        if(Math.Abs(actual-last)<TimeSpan.FromSeconds(3).Ticks)Console.WriteLine("PASS 真实停止后回读续播位置一致");
        else Console.WriteLine("LIMIT 服务器未保留短片头进度，停止接口成功但续播持久化尚未证明");
    }
}
