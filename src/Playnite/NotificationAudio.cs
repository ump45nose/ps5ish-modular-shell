using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl : INotificationAudio
    {
        /// <summary>通知声音在 UI 线程使用独立会话；不依赖窗口前台，不恢复背景音乐。</summary>
        public Task PlayAsync(string path,double volume,CancellationToken token)
        {
            if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(()=>PlayAsync(path,volume,token)).Task.Unwrap();
            return PlayNotificationCore(path,volume,token);
        }
        /// <summary>非循环短声音最大十秒，模块卸载的令牌可立即终止。</summary>
        private async Task PlayNotificationCore(string path,double volume,CancellationToken token)
        {
            if(modules==null)return;var backend=modules.GetCapability<IMusicBackend>();if(backend==null)return;
            string file=String.IsNullOrWhiteSpace(path)||!File.Exists(path)?Path.Combine(packageRoot,"themes","audio","trophy.wav"):path;
            using(var session=await backend.PrepareAsync(file,token)){
                token.ThrowIfCancellationRequested();var loop=session as IMusicLoopControl;if(loop!=null)loop.SetLooping(false);session.SetVolume(volume*(settings.nightMode?.25:1));session.Play();await Task.Delay(10000,token);
            }
        }
    }
}
