using System;
using System.Threading;
using System.Threading.Tasks;

namespace ModularShell
{
    /// <summary>游戏和影视共用的素材意图，领域提供者不控制播放时钟。</summary>
    public sealed class PreviewRequest
    {
        public string MusicPath;
        public string VideoPath;
        public double MusicVolume = .35;
        public double VideoVolume = .5;
        public bool TrailerAudio;
        public bool AutoVideo = true;
    }

    /// <summary>统一协调单个背景视频和音乐；全部入口由宿主 UI 线程串行调用。</summary>
    public sealed class MediaPreview : IDisposable
    {
        private static MediaPreview owner;
        private readonly MusicPreview music = new MusicPreview();
        /// <summary>宿主可核验正在播放的音乐，未建立会话时返回空。</summary>
        public string ActiveMusicPath {get{return music.ActivePath;}}
        private CancellationTokenSource pending;
        private IVideoSession video;
        private Action<IVideoSession> present;
        private int generation;
        private IMusicBackend selectedMusicBackend;
        private PreviewRequest selectedRequest;
        public string LastError { get; private set; }

        /// <summary>立即取消旧选择，按独立停留阈值准备音乐与视频。</summary>
        public async Task SelectAsync(IMusicBackend musicBackend, IVideoBackend videoBackend, PreviewRequest request,
            Action<IVideoSession> presentVideo, int musicDwell = 200, int videoDwell = 2500)
        {
            CancelCore(true); LastError = null;
            if (owner != null && owner != this) owner.Cancel();
            owner = this; present = presentVideo;
            selectedMusicBackend=musicBackend;selectedRequest=request;
            pending = new CancellationTokenSource(); var token = pending.Token; int context = generation;
            if (request == null) return;
            var musicTask = music.SelectAsync(musicBackend, request.MusicPath, request.MusicVolume, musicDwell);
            ObserveMusic(musicTask, context);
            if (!request.AutoVideo || videoBackend == null || String.IsNullOrWhiteSpace(request.VideoPath))
            {
                await musicTask;
                if (context == generation) LastError = music.LastError;
                return;
            }
            IVideoSession prepared = null;
            bool videoFailed = false;
            try
            {
                await Task.Delay(videoDwell, token);
                prepared = await videoBackend.PrepareAsync(request.VideoPath, token);
                if (prepared == null) throw new InvalidOperationException("视频后端没有返回可用会话");
                if (token.IsCancellationRequested || generation != context) return;
                video = prepared; video.SetVolume(0);
                // 后端已确认首帧；展示层先上传像素，之后才执行淡入。
                presentVideo(video);
                var trackSource=video as IMediaPlaybackSession;
                // 无音轨预告保持音乐，不能因为打开原声开关而把主页变成静音。
                bool hasAudio=trackSource==null||Array.Exists(trackSource.AudioTracks??new PlaybackTrack[0],track=>track.Id>=0);
                if (request.TrailerAudio&&hasAudio)
                {
                    await music.FadeOutAsync(300, token);
                    if (!token.IsCancellationRequested && generation == context) video.SetVolume(request.VideoVolume);
                }
            }
            catch (OperationCanceledException) { /* 快速切换与离开正常失效。 */ }
            catch (Exception ex)
            {
                videoFailed = true;
                if (generation == context)
                {
                    LastError = ex.Message;
                    try { presentVideo(null); } catch { /* 展示回退异常不能逃出预览并终止宿主。 */ }
                }
            }
            finally
            {
                if (prepared != null && (token.IsCancellationRequested || generation != context || videoFailed))
                {
                    try { prepared.Dispose(); } catch { }
                    if (ReferenceEquals(video, prepared)) video = null;
                }
            }
        }

        /// <summary>视频分支也观察音乐结果；过期任务的诊断不覆盖新选择。</summary>
        private async void ObserveMusic(Task work, int context)
        {
            try { await work; if (context == generation && music.LastError != null) LastError = music.LastError; }
            catch (Exception ex) { if (context == generation) LastError = ex.Message; }
        }

        /// <summary>结束视频只清理视频层；氛围音乐仍可继续。</summary>
        public void EndVideo(bool resumeMusic=true)
        {
            bool hadVideo=video!=null;
            if (video != null) { try { video.Dispose(); } catch { } video = null; }
            if (present != null) try { present(null); } catch (Exception ex) { LastError = ex.Message; }
            // 原声接管时旧音乐已淡出；正常片尾恢复当前曲目，离开或切换不恢复旧曲目。
            if(resumeMusic&&hadVideo&&selectedRequest!=null&&selectedRequest.TrailerAudio)
                ObserveMusic(music.SelectAsync(selectedMusicBackend,selectedRequest.MusicPath,selectedRequest.MusicVolume,0),generation);
        }

        /// <summary>在游戏启动前同步解除声音和解码器所有权。</summary>
        public void Cancel()
        {
            CancelCore(false);
        }
        /// <summary>换内容时允许同一背景曲继续，离开页面仍同步释放全部声音。</summary>
        private void CancelCore(bool preserveMusic)
        {
            generation++;
            if (pending != null) { pending.Cancel(); pending.Dispose(); pending = null; }
            if (!preserveMusic) music.Cancel(); EndVideo(false); present = null;
            selectedMusicBackend=null;selectedRequest=null;
            if (owner == this) owner = null;
        }
        /// <summary>释放当前选择的一切媒体。</summary>
        public void Dispose() { Cancel(); }
    }
}
