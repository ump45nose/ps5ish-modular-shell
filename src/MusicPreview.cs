using System;
using System.Threading;
using System.Threading.Tasks;

namespace ModularShell
{
    /// <summary>音乐预览时序与单一所有权；由同一 UI 线程串行调用。</summary>
    public sealed class MusicPreview : IDisposable
    {
        private static MusicPreview owner;
        private CancellationTokenSource pending;
        private IMusicSession active;
        private int generation;
        private double gain = 1;
        private double baseVolume;
        private string currentPath;
        public string LastError { get; private set; }
        /// <summary>仅报告已开始播放的本地音乐路径，用于验证实际输出来源。</summary>
        public string ActivePath {get{return active==null?null:currentPath;}}

        /// <summary>停留后准备并淡入；旧请求即使忽略取消，其返回会话也会被释放。</summary>
        public async Task SelectAsync(IMusicBackend backend, string path, double volume, int dwellMs = 200, int fadeMs = 700)
        {
            if(active!=null && owner==this && currentPath==path && gain==1 && LastError==null) { baseVolume=volume;active.SetVolume(volume);return; }
            Cancel(); currentPath=path;
            LastError = null;
            if (backend == null || String.IsNullOrWhiteSpace(path)) return;
            if (owner != null && owner != this) owner.Cancel();
            owner = this;
            gain = 1; baseVolume = 0;
            pending = new CancellationTokenSource(); var token = pending.Token; int context = generation;
            IMusicSession prepared = null;
            LastError = null;
            try
            {
                await Task.Delay(dwellMs, token);
                prepared = await backend.PrepareAsync(path, token);
                if (prepared == null) throw new InvalidOperationException("音乐后端未返回会话");
                if (token.IsCancellationRequested || context != generation) return;
                active = prepared;
                prepared.SetVolume(0); prepared.Play();
                // 淡入由宿主驱动，主题动画不参与播放决策。
                for (int step = 1; step <= 20; step++)
                {
                    await Task.Delay(Math.Max(1, fadeMs / 20), token);
                    if (context != generation) return;
                    baseVolume = Math.Max(0, Math.Min(1, volume)) * step / 20;
                    prepared.SetVolume(baseVolume * gain);
                }
            }
            catch (OperationCanceledException) { /* 选择切换和离开属于正常取消。 */ }
            catch (Exception ex) { if (context == generation) LastError = ex.Message; }
            finally
            {
                // 不在旧回调中关闭新的 active 会话；成功的当前会话继续播放。
                if (prepared != null && (token.IsCancellationRequested || context != generation || LastError != null))
                {
                    try { prepared.Dispose(); } catch { /* 故障释放仍不阻止静态浏览。 */ }
                    if (ReferenceEquals(active, prepared)) active = null;
                }
            }
        }

        /// <summary>视频原声接管前淡出；正在进行的音乐淡入也服从同一增益。</summary>
        public async Task FadeOutAsync(int milliseconds, CancellationToken token)
        {
            int context = generation;
            double initial = gain;
            for (int step = 1; step <= 10; step++)
            {
                await Task.Delay(Math.Max(1, milliseconds / 10), token);
                if (context != generation) return;
                gain = initial * (1 - step / 10.0);
                if (active != null) active.SetVolume(baseVolume * gain);
            }
            if (context == generation) Cancel();
        }

        /// <summary>失去前台、游戏启动或卸载时立即静音并释放当前会话。</summary>
        public void Cancel()
        {
            generation++;
            if (pending != null) { pending.Cancel(); pending.Dispose(); pending = null; }
            if (active != null) { try { active.Dispose(); } catch { } active = null; }
            if (owner == this) owner = null;
        }
        /// <summary>停止全部本对象持有的预览。</summary>
        public void Dispose() { Cancel(); }
    }
}
