using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModularShell;

/// <summary>真实 H.264 解码与完整首帧测试；不启动任何用户游戏。</summary>
public static class VideoTests
{
    /// <summary>失败返回非零退出码，避免把解码器存在当作可用。</summary>
    public static int Main()
    {
        try { Run().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    /// <summary>断言像素、取消和文件释放等可观测结果。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message);
    }
    /// <summary>读取测试脚本生成的红色静音视频，验证中文路径和真实图像。</summary>
    private static async Task Run()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string file = Path.Combine(root, "fixtures", "红色首帧.mp4");
        using (var modules = new ModuleLoader())
        {
            modules.Load(Path.Combine(root, "plugins"), false);
            var backend = modules.GetCapability<IVideoBackend>(); Check(backend != null, "从外挂发现视频能力："+String.Join("; ",modules.GetDiagnostics()));
            var timer = Stopwatch.StartNew();
            using (var session = await backend.PrepareAsync(file, CancellationToken.None))
            {
                var bytes = new byte[session.Width * session.Height * 4]; long version = 0;
                Check(session.CopyFrame(bytes, ref version) && version > 0, "准备返回时已有完整可复制的首帧");
                int center = (session.Width * session.Height / 2 + session.Width / 2) * 4;
                Check(bytes[center + 2] > 230 && bytes[center] < 20 && bytes[center + 1] < 20, "H.264 解码像素为预期红色，不是空缓冲");
                session.SetVolume(0); session.SetPaused(true); session.SetPaused(false);
                Console.WriteLine("首帧耗时 ms=" + timer.ElapsedMilliseconds);
            }
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(true, "视频停止后文件可独占打开");
            using(var session=await backend.PrepareAsync(Path.Combine(root,"fixtures","2160p.mp4"),CancellationToken.None)){
                Console.WriteLine("实际解码输出："+session.Width+"x"+session.Height); Check(session.Width==3840&&session.Height==2160,"4K源实际输出3840×2160，不再固定720p");
                long frame=0;var pixels=new byte[session.Width*session.Height*4];Check(session.CopyFrame(pixels,ref frame),"4K完整帧可复制到复用缓冲");
            }
            using(var preview=new MediaPreview()){
                var music=new TestMusic();await preview.SelectAsync(music,backend,new PreviewRequest {MusicPath="test",VideoPath=Path.Combine(root,"fixtures","2160p.mp4"),TrailerAudio=true},s=>{},0,0);await Task.Delay(350);
                Check(!music.Session.Disposed&&music.Session.Volume>0,"真实无音轨4K预告不淡出默认音乐");
            }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool rejected = false;
                try { await backend.PrepareAsync(file, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "视频后端拒绝已取消选择");
            }
            // 多次原生创建/释放覆盖回调和非托管内存的清理路径。
            for (int i = 0; i < 5; i++) using (var session = await backend.PrepareAsync(file, CancellationToken.None)) { session.SetVolume(0); }
            Check(true, "连续五次视频准备与释放无原生崩溃");
            var concurrent = await backend.PrepareAsync(file, CancellationToken.None);
            var copy = Task.Run(() => { var bytes = new byte[concurrent.Width * concurrent.Height * 4]; long version = 0; for (int i = 0; i < 100; i++) concurrent.CopyFrame(bytes, ref version); });
            await Task.WhenAll(copy, Task.Run(() => concurrent.Dispose()), Task.Run(() => concurrent.Dispose()));
            Check(concurrent.Ended, "并发复制与重复释放由生命周期锁串行保护");
            using (var previous = await backend.PrepareAsync(file, CancellationToken.None))
            using (var next = await backend.PrepareAsync(file, CancellationToken.None))
                Check(previous.Ended && !next.Ended, "新准备同步关闭旧解码器，未等待宿主迟到清理");
            var playback=modules.GetCapability<IMediaPlaybackBackend>();Check(playback!=null,"独立发现正片后端能力");
            using(var movie=await playback.OpenAsync(file,CancellationToken.None)){
                movie.SetPaused(true);Check(movie.CanSeek,"真实本地 H.264 正片报告可定位能力");
                movie.Seek(TimeSpan.FromSeconds(1).Ticks);await Task.Delay(300);
                Check(Math.Abs(movie.PositionTicks-TimeSpan.FromSeconds(1).Ticks)<TimeSpan.FromSeconds(.5).Ticks,"正片定位接口使用 ticks 与真实 LibVLC 播放位置一致");
                var tracks=movie.AudioTracks;Check(tracks.Length>0,"真实正片返回可切换内嵌音轨列表");
                foreach(var track in tracks)if(track.Id>=0){movie.SelectAudio(track.Id);break;}
            }
        }
    }
    /// <summary>只记录音量与释放，用真实无音轨视频检查声音协调，不输出测试声音。</summary>
    private sealed class TestMusic : IMusicBackend
    {
        internal readonly TestMusicSession Session=new TestMusicSession();
        public Task<IMusicSession> PrepareAsync(string path,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult<IMusicSession>(Session);}
    }
    /// <summary>最小静音测试会话，保留协调器可观测行为。</summary>
    private sealed class TestMusicSession : IMusicSession
    {
        internal double Volume;internal bool Disposed;
        public void Play(){} public void SetVolume(double value){Volume=value;}public void Dispose(){Disposed=true;}
    }
}
