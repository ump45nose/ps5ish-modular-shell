using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyBrowser;
using ModularShell;

/// <summary>显式参数触发真实Emby只读目录和真实4K预告验证；不播放正片或上报进度。</summary>
public static class ExperienceLive
{
    /// <summary>参数：用户数据目录、自动下载的4K文件、输出证据路径；不接收密码。</summary>
    public static int Main(string[] args){try{Run(args).GetAwaiter().GetResult();return 0;}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1;}}
    /// <summary>复用正式插件校验完整帧，随后启用用户要求的额外影视组件。</summary>
    private static async Task Run(string[] args)
    {
        if(args.Length!=3)throw new ArgumentException("需要用户目录、4K文件和证据路径");
        var client=new Client(args[0]);var directories=await client.Libraries(CancellationToken.None);
        Console.WriteLine("PASS Emby真实登录令牌有效，可见目录数="+directories.Length);
        var config=LibrarySettings.Load(args[0]);config.enabled=true;
        if(config.identity!=client.Identity){config.libraryId=null;config.libraryName="全部可见影视目录";config.identity=client.Identity;}config.Save(args[0]);
        var page=await new Client(args[0]).Browse("library",0,null,CancellationToken.None);if(page.Offline)throw new IOException("不能用离线缓存完成验收");Console.WriteLine("PASS 额外影视组件已启用，真实目录本页="+page.Items.Length);
        using(var modules=new ModuleLoader()){
            modules.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugins"),false);
            var backend=modules.GetCapability<IVideoBackend>();
            using(var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            using(var video=await backend.PrepareAsync(args[1],cancel.Token)){
                video.SetVolume(0);if(video.Width!=3840||video.Height!=2160)throw new IOException("真实下载预告未以3840×2160输出");
                var pixels=new byte[checked(video.Width*video.Height*4)];long version=0;if(!video.CopyFrame(pixels,ref version))throw new IOException("首帧不可复制");
                long first=version;await Task.Delay(1500);if(!video.CopyFrame(pixels,ref version)||version<=first)throw new IOException("真实预告帧未更新");
                File.WriteAllText(args[2],"{\"outputWidth\":3840,\"outputHeight\":2160,\"framesAdvanced\":true,\"source\":\""+args[1].Replace("\\","\\\\")+"\"}");
                Console.WriteLine("PASS 自动获取的真实4K预告输出3840×2160，首帧与后续帧均通过");
            }
        }
    }
}
