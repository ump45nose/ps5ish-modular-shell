using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using ModularShell;

namespace LocalMusic
{
    /// <summary>独立本地音乐插件；不引用 Playnite、主题或核心实现。</summary>
    public sealed class Module : IModule, IMusicBackend
    {
        /// <summary>后端仅按需创建播放器。</summary>
        public void Initialize() { }
        /// <summary>播放后端没有首页展示分区。</summary>
        public IEnumerable<Contribution> GetSections() { yield break; }
        /// <summary>媒体会话由宿主负责释放。</summary>
        public void Dispose() { }

        /// <summary>通过 MediaOpened 确认音频准备完成；超时或取消时关闭播放器。</summary>
        public async Task<IMusicSession> PrepareAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\") || !File.Exists(path)) throw new FileNotFoundException("音乐文件不可用");
            var player = new MediaPlayer { Volume = 0 };
            var ready = new TaskCompletionSource<bool>();
            EventHandler opened = (s, e) => ready.TrySetResult(true);
            EventHandler<ExceptionEventArgs> failed = (s, e) => ready.TrySetException(e.ErrorException);
            player.MediaOpened += opened; player.MediaFailed += failed;
            try
            {
                player.Open(new Uri(Path.GetFullPath(path)));
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(5000);
                    using (timeout.Token.Register(() => ready.TrySetCanceled())) await ready.Task;
                }
                token.ThrowIfCancellationRequested();
                if (!player.HasAudio || player.HasVideo) throw new InvalidDataException("音乐后端只接受纯音频文件");
                return new Session(player);
            }
            catch (OperationCanceledException)
            {
                player.Close();
                if (token.IsCancellationRequested) throw;
                throw new TimeoutException("音乐准备超过 5 秒，已停止本次预览");
            }
            catch { player.Close(); throw; }
            finally { player.MediaOpened -= opened; player.MediaFailed -= failed; }
        }
    }

    /// <summary>可重复释放的单首音乐会话；不自动切换或循环素材。</summary>
    internal sealed class Session : IMusicSession, IMusicLoopControl
    {
        private MediaPlayer player;
        private bool looping=true;
        /// <summary>提示音可关闭循环；背景音乐默认继续循环，互不改变其他会话。</summary>
        public void SetLooping(bool enabled){looping=enabled;}
        /// <summary>接收已准备且静音的播放器。</summary>
        public Session(MediaPlayer player) { this.player = player; player.MediaEnded+=Loop; }
        /// <summary>音乐会话持续循环，是否停止仍由宿主所有权决定。</summary>
        private void Loop(object sender,EventArgs args){if(player!=null&&looping){player.Position=TimeSpan.Zero;player.Play();}}
        /// <summary>宿主确认当前上下文后才开始播放。</summary>
        public void Play() { if (player != null) player.Play(); }
        /// <summary>将音量限制在有效范围。</summary>
        public void SetVolume(double value) { if (player != null) player.Volume = Math.Max(0, Math.Min(1, value)); }
        /// <summary>先静音再关闭解码资源，允许多次取消。</summary>
        public void Dispose() { if (player != null) { player.MediaEnded-=Loop;player.Volume = 0; player.Close(); player = null; } }
    }
}
