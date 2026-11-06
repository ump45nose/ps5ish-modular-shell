using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModularShell;

/// <summary>长时真实静音视频资源采样；单独运行，不混同 UI 或电视性能验收。</summary>
public static class VideoSoak
{
    /// <summary>指定持续秒数，默认 30 分钟，失败以非零状态交付。</summary>
    public static int Main(string[] args)
    {
        try { Run(args.Length == 0 ? 1800 : Int32.Parse(args[0])).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    /// <summary>每轮准备、复制帧并释放，持续采样释放后的进程资源。</summary>
    private static async Task Run(int seconds)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        var watch = Stopwatch.StartNew(); var process = Process.GetCurrentProcess();
        long baseline = 0, final = 0; int baselineHandles = 0, finalHandles = 0, cycle = 0;
        using (var modules = new ModuleLoader())
        using (var csv = new StreamWriter(Path.Combine(root, "video-soak.csv"), false))
        {
            modules.Load(Path.Combine(root, "plugins"), false);
            var backend = modules.GetCapability<IVideoBackend>();
            if (backend == null) throw new Exception("没有视频能力");
            csv.WriteLine("seconds,cycle,private_bytes,working_set_bytes,handles,threads,cpu_seconds");
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                using (var session = await backend.PrepareAsync(Path.Combine(root, "fixtures", "红色首帧.mp4"), CancellationToken.None))
                {
                    session.SetVolume(0);
                    var bytes = new byte[session.Width * session.Height * 4]; long version = 0;
                    for (int i = 0; i < 30; i++) { session.CopyFrame(bytes, ref version); await Task.Delay(33); }
                    if (version == 0) throw new Exception("长测未得到帧");
                }
                cycle++;
                // 每轮结束后采样，避免把不同数量的活跃解码器混在一条曲线里。
                process.Refresh(); final = process.PrivateMemorySize64; finalHandles = process.HandleCount;
                if (cycle == 10) { baseline = final; baselineHandles = finalHandles; }
                csv.WriteLine(String.Join(",", watch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture), cycle, final, process.WorkingSet64, finalHandles, process.Threads.Count, process.TotalProcessorTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)));
                csv.Flush();
                if (cycle % 40 == 0) Console.WriteLine("采样秒数=" + (int)watch.Elapsed.TotalSeconds + " cycles=" + cycle + " privateMiB=" + final / 1048576 + " handles=" + finalHandles);
            }
        }
        if (baseline != 0 && (final - baseline > 64 * 1048576L || finalHandles - baselineHandles > 50)) throw new Exception("资源增长超过本轮诊断阈值，请查看 CSV；不能判定通过");
        Console.WriteLine("PASS 静音解码资源长测 seconds=" + watch.Elapsed.TotalSeconds.ToString("F1") + " cycles=" + cycle + " privateDeltaMiB=" + (final - baseline) / 1048576 + " handleDelta=" + (finalHandles - baselineHandles));
    }
}
