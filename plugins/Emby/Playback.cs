using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using ModularShell;

namespace EmbyBrowser
{
    [DataContract] public sealed class MediaSource
    {
        [DataMember] public string Id, Container;
        [DataMember] public bool SupportsDirectStream, SupportsDirectPlay, RequiresOpening, IsInfiniteStream;
    }
    [DataContract] public sealed class PlaybackInfo
    {
        [DataMember] public MediaSource[] MediaSources;
        [DataMember] public string PlaySessionId, ErrorCode;
    }
    [DataContract] public sealed class PlaybackReport
    {
        [DataMember] public string ItemId, MediaSourceId, PlaySessionId;
        [DataMember] public string PlayMethod="DirectStream", EventName="TimeUpdate";
        [DataMember] public long PositionTicks;
        [DataMember] public bool IsPaused, IsMuted, CanSeek;
        [DataMember] public int VolumeLevel=70;
    }
    public sealed partial class Client
    {
        public string SourceInstance { get { return Hash(connection.Server+"|"+connection.ServerId+"|"+connection.UserId); } }
        /// <summary>获取独立播放会话；不采纳服务器提供的任意外站地址。</summary>
        public async Task<PlaybackInfo> PlaybackInformation(string id,CancellationToken cancel)
        {
            CheckId(id);
            var body=System.Text.Encoding.UTF8.GetBytes("{\"UserId\":\""+connection.UserId+"\",\"EnableDirectPlay\":true,\"EnableDirectStream\":true,\"EnableTranscoding\":false,\"IsPlayback\":true,\"AutoOpenLiveStream\":false}");
            var value=Decode<PlaybackInfo>(await Request("Items/"+id+"/PlaybackInfo",cancel,2*1024*1024,body).ConfigureAwait(false));
            if(value==null || !String.IsNullOrEmpty(value.ErrorCode) || value.MediaSources==null)throw new IOException("当前片源不支持播放。");
            if(String.IsNullOrEmpty(value.PlaySessionId))throw new InvalidDataException("服务器未提供播放会话标识");
            CheckPlaybackId(value.PlaySessionId); return value;
        }
        /// <summary>首次范围仅接受普通静态直传片源；直播和转码明确拒绝。</summary>
        public static MediaSource DirectSource(PlaybackInfo value)
        {
            var source=value.MediaSources.FirstOrDefault(s=>!s.RequiresOpening && !s.IsInfiniteStream && (s.SupportsDirectStream || s.SupportsDirectPlay));
            if(source==null)throw new NotSupportedException("片源需要转码或直播会话，当前后端不支持。");
            CheckPlaybackId(source.Id);return source;
        }
        /// <summary>Emby 媒体源 ID 可含下划线，与内容路径段 ID 分开校验。</summary>
        public static void CheckPlaybackId(string id){if(id==null || !System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,256}\z"))throw new InvalidDataException("播放源或会话标识格式不兼容");}
        /// <summary>固定 API 路径只转发一个已授权媒体，令牌保留在客户端。</summary>
        public StreamRelay OpenStream(string id,string source)
        {
            CheckId(id);CheckPlaybackId(source);
            return new StreamRelay(connection.Server.TrimEnd('/')+"/Videos/"+id+"/stream?Static=true&MediaSourceId="+source,token,transport==null?null:transport());
        }
        /// <summary>正片开始、进度和停止使用官方会话接口，预览没有此调用方。</summary>
        public Task Report(string stage,PlaybackReport value,CancellationToken cancel)
        {
            if(stage!="start" && stage!="progress" && stage!="stop")throw new ArgumentException("未知播放阶段");
            CheckId(value.ItemId);CheckPlaybackId(value.MediaSourceId);CheckPlaybackId(value.PlaySessionId);
            if(value.PositionTicks<0)throw new ArgumentException("进度无效");
            byte[] body;using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(PlaybackReport)).WriteObject(stream,value);body=stream.ToArray();}
            return Request("Sessions/Playing"+(stage=="start"?"":stage=="stop"?"/Stopped":"/Progress"),cancel,65536,body);
        }
    }
    /// <summary>正片会话独立于预告。同步失败暂停本地且不重放离线写入，保护其他设备的新进度。</summary>
    public sealed class PlaybackController
    {
        private readonly Client client;
        private readonly IMediaPlaybackBackend backend;
        private StreamRelay relay;
        private PlaybackReport report;
        private bool started, stopped, syncFailed;
        private readonly SemaphoreSlim writes=new SemaphoreSlim(1,1);
        public IMediaPlaybackSession Session { get; private set; }
        public bool Paused { get; private set; }
        public bool SyncFailed { get { return syncFailed; } }
        /// <summary>注入独立后端，不让服务器数据进入宿主业务层。</summary>
        public PlaybackController(Client client,IMediaPlaybackBackend backend){this.client=client;this.backend=backend;}
        /// <summary>重新读取在线进度后准备首帧；确认开始上报成功才打开声音。</summary>
        public async Task Start(string id,CancellationToken cancel,double volume=.7)
        {
            if(stopped || Session!=null)throw new InvalidOperationException("会话已使用");
            var detail=await client.Detail(id,cancel);if(detail.Offline)throw new IOException("离线时不开始正片，请恢复连接后重试。");
            var info=await client.PlaybackInformation(id,cancel);var source=Client.DirectSource(info);
            cancel.ThrowIfCancellationRequested();
            relay=client.OpenStream(id,source.Id);
            Exception failure=null;
            try {
                var session=await backend.OpenAsync(relay.Location,cancel);
                if(cancel.IsCancellationRequested || stopped){session.Dispose();cancel.ThrowIfCancellationRequested();throw new OperationCanceledException();}
                Session=session;session.SetPaused(true);
                long position=detail.UserData==null?0:Math.Max(0,detail.UserData.PlaybackPositionTicks);
                if(position>0){if(!session.CanSeek)throw new NotSupportedException("片源不支持续播定位");session.Seek(position);}
                report=new PlaybackReport {ItemId=id,MediaSourceId=source.Id,PlaySessionId=info.PlaySessionId,PositionTicks=position,CanSeek=session.CanSeek,IsPaused=Paused,VolumeLevel=(int)(Math.Max(0,Math.Min(1,volume))*100),IsMuted=volume==0};
                await writes.WaitAsync(cancel);
                try { if(stopped)throw new OperationCanceledException();await client.Report("start",report,cancel);started=true; }
                finally { writes.Release(); }
                cancel.ThrowIfCancellationRequested();if(stopped)throw new OperationCanceledException();
                session.SetVolume(volume);session.SetPaused(Paused);
            } catch(Exception ex) { failure=ex; }
            if(failure!=null){await Stop();System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();}
        }
        /// <summary>串行提交进度；网络失败后停止所有后续写入，用户重新进入会重新读取服务器状态。</summary>
        public async Task Progress(string reason)
        {
            await writes.WaitAsync();
            try {
                if(!started || stopped || syncFailed || Session==null)return;
                report.PositionTicks=Session.PositionTicks;report.IsPaused=Paused;report.EventName=reason;
                try { await client.Report("progress",report,CancellationToken.None); }
                catch { syncFailed=true;if(Session!=null)Session.SetPaused(true);Paused=true;throw; }
            } finally { writes.Release(); }
        }
        /// <summary>焦点丢失或用户暂停不会回到预览；恢复必须由用户明确继续。</summary>
        public void Pause(bool value){if(stopped || syncFailed)return;Paused=value;if(Session!=null)Session.SetPaused(value);}
        /// <summary>立刻释放本地资源，再等待在途进度完成后发送最终停止。离线状态不排队重放。</summary>
        public async Task Stop()
        {
            if(stopped)return;stopped=true;
            long position=Session==null?0:Session.PositionTicks;
            if(relay!=null){relay.Dispose();relay=null;}
            if(Session!=null){Session.Dispose();Session=null;}
            await writes.WaitAsync();
            try { if(started && !syncFailed){report.PositionTicks=position;await client.Report("stop",report,CancellationToken.None);} }
            catch { syncFailed=true; }
            finally { writes.Release(); }
        }
    }
}
