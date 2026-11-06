using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ModularShell;

namespace LocalVideo
{
    /// <summary>独立纯本地视频能力，原生库和解码策略不进入宿主。</summary>
    public sealed class Module : IModule, IVideoBackend, IMediaPlaybackBackend
    {
        private Native api;
        private IntPtr instance;
        private readonly object gate = new object();
        private bool disposed;
        private Session currentSession;
        /// <summary>不在启动时创建解码器，运行时按需载入。</summary>
        public void Initialize() { }
        /// <summary>视频后端不贡献首页分区。</summary>
        public IEnumerable<Contribution> GetSections() { yield break; }
        /// <summary>会话由协调器释放；原生模块保留到进程退出。</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return; disposed = true;
                if (currentSession != null) { currentSession.Dispose(); currentSession = null; }
                if (instance != IntPtr.Zero) { api.ReleaseInstance(instance); instance = IntPtr.Zero; }
            }
        }

        /// <summary>只在解码完成并被播放时钟提交后，返回首帧就绪会话。</summary>
        public async Task<IVideoSession> PrepareAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\") || !File.Exists(path)) throw new FileNotFoundException("本地视频不可用");
            return await PrepareCore(Path.GetFullPath(path), false, token);
        }
        public string Capabilities { get { return "LibVLC 3 · SDR 原比例输出，最高3840×2160 · 可定位片源 · 内嵌音轨/字幕选择；HDR、透传、外置字幕和转码未验收"; } }
        /// <summary>正片仅接受本地文件或受控本机代理，禁止向任意网络地址传递隐式凭证。</summary>
        public async Task<IMediaPlaybackSession> OpenAsync(string location, CancellationToken token)
        {
            Uri uri;
            if (Uri.TryCreate(location,UriKind.Absolute,out uri) && uri.Scheme=="http" && uri.Host=="127.0.0.1" && uri.UserInfo.Length==0)
                return (IMediaPlaybackSession)await PrepareCore(location,true,token);
            return (IMediaPlaybackSession)await PrepareAsync(location,token);
        }
        /// <summary>预览与正片共用唯一解码器，但调用接口及服务器状态写入职责分离。</summary>
        private async Task<IVideoSession> PrepareCore(string path, bool network, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Session session;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("LocalVideo");
                if (api == null) api = new Native(Path.Combine(Path.GetDirectoryName(typeof(Module).Assembly.Location), "runtime"));
                // 解码引擎按模块复用，选择只创建播放器；避免反复初始化系统音视频子系统。
                if (instance == IntPtr.Zero) { instance = api.CreateInstance(); if (instance == IntPtr.Zero) throw new InvalidOperationException("无法创建 LibVLC 实例"); }
                // 准备阶段也占用解码器；新请求同步关闭上一会话，避免取消续接排队时短暂双解码。
                if (currentSession != null) currentSession.Dispose();
                session = new Session(api, instance, path, network); currentSession = session;
            }
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(8000);
                    using (timeout.Token.Register(() => session.Ready.TrySetCanceled())) await session.Ready.Task;
                }
                token.ThrowIfCancellationRequested();
                return session;
            }
            catch (OperationCanceledException)
            {
                session.Dispose();
                if (token.IsCancellationRequested) throw;
                throw new TimeoutException("视频在 8 秒内未产生首帧，已回退背景图");
            }
            catch { session.Dispose(); throw; }
        }
    }

    /// <summary>单个 BGR32 缓冲；本机渲染通过 CopyFrame 复用调用方内存。</summary>
    internal sealed class Session : IMediaPlaybackSession
    {
        private readonly Native api;
        private readonly object frameLock = new object();
        private readonly object lifetimeLock = new object();
        private IntPtr instance, player, allocation, pixels;
        private readonly Native.Lock locked;
        private readonly Native.Unlock unlocked;
        private readonly Native.Display displayed;
        private readonly Native.FormatSetup formatSetup;
        private readonly Native.Release formatCleanup;
        private int width,height,pitch;
        private bool formatFailed;
        private long version;
        private bool disposed;
        internal readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Width { get { return width; } }
        public int Height { get { return height; } }
        public bool Ended { get { lock (lifetimeLock) { return disposed || formatFailed || api.GetState(player) == 6 || api.GetState(player) == 7; } } }

        /// <summary>分配 32 字节对齐的回调缓冲；原生播放开始前音量固定为零。</summary>
        internal Session(Native api, IntPtr sharedInstance, string path, bool network = false)
        {
            this.api = api;
            locked = LockFrame; unlocked = UnlockFrame; displayed = DisplayFrame;
            formatSetup=SetupFormat;formatCleanup=CleanupFormat;
            try
            {
                // 会话保留独立引用，模块退出后迟到的会话仍可安全完成清理。
                api.RetainInstance(sharedInstance); instance = sharedInstance;
                IntPtr text = Native.Utf8(path); IntPtr media;
                try { media = network ? api.CreateLocation(instance,text) : api.CreateMedia(instance, text); } finally { Marshal.FreeHGlobal(text); }
                if (media == IntPtr.Zero) throw new InvalidOperationException("无法创建本地媒体");
                try { player = api.CreatePlayer(media); } finally { api.ReleaseMedia(media); }
                if (player == IntPtr.Zero) throw new InvalidOperationException("无法创建视频播放器");
                api.SetCallbacks(player, locked, unlocked, displayed, IntPtr.Zero);
                api.SetFormatCallbacks(player,formatSetup,formatCleanup);
                api.SetVolume(player, 0);
                if (api.Play(player) != 0) throw new InvalidOperationException("本地视频启动失败");
            }
            catch { Dispose(); throw; }
        }

        /// <summary>采用解码器报告的原始尺寸，仅对超过4K的帧等比缩小，绝不上采样。</summary>
        private uint SetupFormat(ref IntPtr opaque,IntPtr chroma,ref uint sourceWidth,ref uint sourceHeight,IntPtr pitches,IntPtr lines)
        {
            lock(frameLock)try{
                if(disposed||sourceWidth==0||sourceHeight==0)return 0;
                // vmem回调可能报告编码填充尺寸（例如4K H.264的额外边行）。
                // 公开媒体轨道给出可见尺寸，不能把填充算入比例后把3840缩成3808。
                VisibleSize(ref sourceWidth,ref sourceHeight);
                double scale=Math.Min(1,Math.Min(3840.0/sourceWidth,2160.0/sourceHeight));
                int nextWidth=Math.Max(1,(int)Math.Floor(sourceWidth*scale)),nextHeight=Math.Max(1,(int)Math.Floor(sourceHeight*scale));
                // 播放中变更分辨率需重新协商展示缓冲；本版安全结束，不写入旧尺寸数组。
                if(Ready.Task.IsCompleted&&(nextWidth!=width||nextHeight!=height)){formatFailed=true;return 0;}
                width=nextWidth;height=nextHeight;pitch=(width*4+31)&~31;
                if(allocation!=IntPtr.Zero)Marshal.FreeHGlobal(allocation);
                allocation=Marshal.AllocHGlobal(checked(pitch*((height+31)&~31)+31));pixels=new IntPtr((allocation.ToInt64()+31)&~31L);
                Marshal.Copy(new byte[]{(byte)'R',(byte)'V',(byte)'3',(byte)'2'},0,chroma,4);
                sourceWidth=(uint)width;sourceHeight=(uint)height;Marshal.WriteInt32(pitches,pitch);Marshal.WriteInt32(lines,(height+31)&~31);return 1;
            }catch(Exception ex){formatFailed=true;Ready.TrySetException(ex);return 0;}
        }
        /// <summary>保留最后完整帧直到宿主释放，内存由 Dispose 统一回收。</summary>
        private void CleanupFormat(IntPtr opaque){ }
        /// <summary>读取LibVLC公开视频轨道可见宽高，所有原生描述及时释放。</summary>
        private void VisibleSize(ref uint width,ref uint height)
        {
            IntPtr media=api.GetMedia(player);if(media==IntPtr.Zero)return;
            IntPtr tracks=IntPtr.Zero;uint count=0;
            try{count=api.GetMediaTracks(media,out tracks);for(int i=0;i<count;i++){
                var track=(Native.MediaTrack)Marshal.PtrToStructure(Marshal.ReadIntPtr(tracks,i*IntPtr.Size),typeof(Native.MediaTrack));
                if(track.Type!=1||track.Detail==IntPtr.Zero)continue;
                uint h=(uint)Marshal.ReadInt32(track.Detail),w=(uint)Marshal.ReadInt32(track.Detail,4);
                // 旋转轨道在vmem已经旋转，公开轨道描述仍按编码坐标，需交换宽高。
                int orientation=Marshal.ReadInt32(track.Detail,24);if(orientation>=4&&orientation<=7){uint previous=w;w=h;h=previous;}
                if(w>0&&h>0){width=w;height=h;return;}
            }}finally{if(tracks!=IntPtr.Zero)api.ReleaseMediaTracks(tracks,count);api.ReleaseMedia(media);}
        }

        /// <summary>解码线程获取独占缓冲，阻止 UI 复制半帧。</summary>
        private IntPtr LockFrame(IntPtr opaque, IntPtr planes)
        {
            Monitor.Enter(frameLock); Marshal.WriteIntPtr(planes, pixels); return IntPtr.Zero;
        }
        /// <summary>完整解码后释放缓冲锁。</summary>
        private void UnlockFrame(IntPtr opaque, IntPtr picture, IntPtr planes) { Monitor.Exit(frameLock); }
        /// <summary>播放时钟提交画面后更新版本并通知首帧；不直接操作 UI。</summary>
        private void DisplayFrame(IntPtr opaque, IntPtr picture) { Interlocked.Increment(ref version); Ready.TrySetResult(true); }

        /// <summary>只复制完整帧，不为每次解码分配托管数组。</summary>
        public bool CopyFrame(byte[] destination, ref long previous)
        {
            lock (lifetimeLock)
            {
            if (disposed||formatFailed) return false;
            if (destination == null || destination.Length < Width * Height * 4) throw new ArgumentException("帧缓冲不足");
            lock (frameLock)
            {
                long current = Interlocked.Read(ref version);
                if (current == 0 || current == previous) return false;
                // 解码器每行32字节对齐，宿主接收紧密BGR32，逐行跳过填充。
                if(pitch==Width*4)Marshal.Copy(pixels,destination,0,Width*Height*4);
                else for(int y=0;y<Height;y++)Marshal.Copy(IntPtr.Add(pixels,y*pitch),destination,y*Width*4,Width*4);
                previous = current; return true;
            }
            }
        }
        /// <summary>由宿主决定静音或预告原声。</summary>
        public void SetVolume(double value) { lock (lifetimeLock) { if (!disposed) api.SetVolume(player, (int)(Math.Max(0, Math.Min(1, value)) * 100)); } }
        /// <summary>主动预告暂停/恢复。</summary>
        public void SetPaused(bool value) { lock (lifetimeLock) { if (!disposed) api.Pause(player, value ? 1 : 0); } }
        public long PositionTicks { get { lock(lifetimeLock) { return disposed ? 0 : Math.Max(0,api.GetTime(player))*TimeSpan.TicksPerMillisecond; } } }
        public bool CanSeek { get { lock(lifetimeLock) { return !disposed && api.IsSeekable(player)!=0; } } }
        /// <summary>定位单位由共享 ticks 转为 LibVLC 毫秒，拒绝不可定位片源。</summary>
        public void Seek(long ticks) { lock(lifetimeLock) { if(!CanSeek)throw new InvalidOperationException("当前片源不能定位"); api.SetTime(player,Math.Max(0,ticks)/TimeSpan.TicksPerMillisecond); } }
        public PlaybackTrack[] AudioTracks { get { return Tracks(false); } }
        public PlaybackTrack[] SubtitleTracks { get { return Tracks(true); } }
        /// <summary>复制原生轨道描述后立即释放列表，不缓存失效原生指针。</summary>
        private PlaybackTrack[] Tracks(bool subtitle)
        {
            lock(lifetimeLock) {
                if(disposed)return new PlaybackTrack[0];
                IntPtr first=subtitle?api.GetSubtitleTracks(player):api.GetAudioTracks(player), cursor=first;
                var result=new List<PlaybackTrack>();
                try { while(cursor!=IntPtr.Zero && result.Count<128) {
                    var track=(Native.TrackDescription)Marshal.PtrToStructure(cursor,typeof(Native.TrackDescription));
                    result.Add(new PlaybackTrack { Id=track.Id,Name=track.Name==IntPtr.Zero?track.Id.ToString():ReadTrackName(track.Name) }); cursor=track.Next;
                } } finally { if(first!=IntPtr.Zero)api.ReleaseTracks(first); }
                return result.ToArray();
            }
        }
        /// <summary>LibVLC 描述使用 UTF-8，读取有界字符串。</summary>
        private static string ReadTrackName(IntPtr pointer) { var bytes=new List<byte>(); for(int i=0;i<1024;i++){byte b=Marshal.ReadByte(pointer,i);if(b==0)break;bytes.Add(b);}return System.Text.Encoding.UTF8.GetString(bytes.ToArray()); }
        /// <summary>只允许播放器实际公布的音轨标识。</summary>
        public void SelectAudio(int id) { lock(lifetimeLock) { if(disposed || !Array.Exists(AudioTracks,t=>t.Id==id) || api.SetAudioTrack(player,id)!=0)throw new InvalidOperationException("音轨不可用"); } }
        /// <summary>字幕关闭轨通常为 -1，同样以实际轨道列表为准。</summary>
        public void SelectSubtitle(int id) { lock(lifetimeLock) { if(disposed || !Array.Exists(SubtitleTracks,t=>t.Id==id) || api.SetSubtitleTrack(player,id)!=0)throw new InvalidOperationException("字幕不可用"); } }
        /// <summary>先停止并等待原生回调退出，再释放像素内存和实例。</summary>
        public void Dispose()
        {
            lock (lifetimeLock)
            {
            if (disposed) return;
            disposed = true;
            // LibVLC 3 stop 同步关闭输入和视频输出。不能持有 frameLock 调用它，否则会阻塞原生回调退出。
            if (player != IntPtr.Zero) { api.SetVolume(player, 0); api.Stop(player); api.ReleasePlayer(player); player = IntPtr.Zero; }
            if (instance != IntPtr.Zero) { api.ReleaseInstance(instance); instance = IntPtr.Zero; }
            if (allocation != IntPtr.Zero) { Marshal.FreeHGlobal(allocation); allocation = IntPtr.Zero; pixels = IntPtr.Zero; }
            GC.KeepAlive(locked); GC.KeepAlive(unlocked); GC.KeepAlive(displayed);
            GC.KeepAlive(formatSetup);GC.KeepAlive(formatCleanup);
            Ready.TrySetCanceled();
            }
        }
    }
}
