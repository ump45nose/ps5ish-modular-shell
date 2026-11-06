using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyBrowser;
using ModularShell;

/// <summary>正片协议与代理回归完全使用隔离响应，不写真实观看状态。</summary>
public static class PlaybackTests
{
    private sealed class State
    {
        public readonly List<string> Stages=new List<string>();
        public readonly List<PlaybackReport> Reports=new List<PlaybackReport>();
        public bool FailProgress;public string Range,Auth;public int Reads;
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly State state;
        public Handler(State state){this.state=state;}
        /// <summary>模拟官方字段及 Range 响应，记录实际请求体而非调用次数猜测。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)
        {
            string path=request.RequestUri.AbsolutePath;
            if(path.EndsWith("PlaybackInfo"))return Json("{\"PlaySessionId\":\"session1\",\"MediaSources\":[{\"Id\":\"source1\",\"SupportsDirectStream\":true}]}");
            if(path.EndsWith("ThemeMedia"))return Json("{\"ThemeSongsResult\":{\"Items\":[{\"Id\":\"song1\"}]},\"ThemeVideosResult\":{\"Items\":[{\"Id\":\"video1\"}]}}");
            if(request.Method==HttpMethod.Post){
                string stage=path.EndsWith("Progress")?"progress":path.EndsWith("Stopped")?"stop":"start";
                var bytes=await request.Content.ReadAsByteArrayAsync();
                using(var stream=new MemoryStream(bytes)){state.Stages.Add(stage);state.Reports.Add((PlaybackReport)new DataContractJsonSerializer(typeof(PlaybackReport)).ReadObject(stream));}
                if(stage=="progress" && state.FailProgress)throw new HttpRequestException();
                return new HttpResponseMessage(HttpStatusCode.NoContent){Content=new ByteArrayContent(new byte[0])};
            }
            if(path.EndsWith("PlaybackInfo"))return Json("{\"PlaySessionId\":\"session1\",\"MediaSources\":[{\"Id\":\"source1\",\"SupportsDirectStream\":true}]}");
            if(path.EndsWith("/stream")){
                state.Reads++;state.Range=request.Headers.Range==null?null:request.Headers.Range.ToString();state.Auth=request.Headers.GetValues("X-Emby-Token").Single();
                var response=new HttpResponseMessage(state.Range==null?HttpStatusCode.OK:HttpStatusCode.PartialContent){Content=new ByteArrayContent(new byte[]{2,3,4})};
                if(state.Range!=null)response.Content.Headers.ContentRange=new System.Net.Http.Headers.ContentRangeHeaderValue(2,4,10);
                return response;
            }
            return Json("{\"Id\":\"movie1\",\"Type\":\"Movie\",\"UserData\":{\"PlaybackPositionTicks\":50000000}}");
        }
        /// <summary>模拟服务器返回结构，响应正文不包含真实数据。</summary>
        private static HttpResponseMessage Json(string body){return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};}
    }
    private sealed class Session : IMediaPlaybackSession
    {
        public bool Disposed,Paused;public long PositionTicks {get;set;}public bool CanSeek{get{return true;}}public int Width{get{return 8;}}public int Height{get{return 8;}}public bool Ended{get{return false;}}
        public PlaybackTrack[] AudioTracks{get{return new PlaybackTrack[0];}}public PlaybackTrack[] SubtitleTracks{get{return new PlaybackTrack[0];}}
        public void Seek(long ticks){PositionTicks=ticks;}public void SelectAudio(int id){}public void SelectSubtitle(int id){}
        public bool CopyFrame(byte[] bytes,ref long version){return false;}public void SetVolume(double value){}public void SetPaused(bool value){Paused=value;}public void Dispose(){Disposed=true;}
    }
    private sealed class Backend : IMediaPlaybackBackend
    {
        public readonly Session Value=new Session();public string Location;
        public string Capabilities{get{return "测试后端";}}
        /// <summary>只模拟后端，不把协议测试误称为真实解码。</summary>
        public Task<IMediaPlaybackSession> OpenAsync(string location,CancellationToken cancel){Location=location;return Task.FromResult<IMediaPlaybackSession>(Value);}
    }
    private static int count;
    /// <summary>失败立即终止回归。</summary>
    private static void Check(bool value,string message){if(!value)throw new Exception(message);count++;Console.WriteLine("PASS "+message);}
    /// <summary>创建专用测试身份，退出只清理此处创建的目录。</summary>
    public static int Main(){string root=Path.Combine(Path.GetTempPath(),"shell-playback-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"emby"));try{Run(root).GetAwaiter().GetResult();return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{Directory.Delete(root,true);}}
    /// <summary>覆盖正常开始/续播/停止、故障不重放、搜索注入与代理权限。</summary>
    private static async Task Run(string root)
    {
        var connection=new Connection{schema=1,Server="https://example.invalid/emby",UserId="user1",ServerId="server1",DeviceId="device1"};
        connection.ProtectedToken=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("fixture-token"),Encoding.UTF8.GetBytes(Client.Entropy(connection)),DataProtectionScope.CurrentUser));
        using(var file=File.Create(Path.Combine(root,"emby","connection.json")))new DataContractJsonSerializer(typeof(Connection)).WriteObject(file,connection);
        var state=new State();var client=new Client(root,()=>new Handler(state));var backend=new Backend();var controller=new PlaybackController(client,backend);
        await controller.Start("movie1",CancellationToken.None);
        Check(backend.Value.PositionTicks==50000000 && state.Stages.SequenceEqual(new[]{"start"}) && state.Reports[0].PositionTicks==50000000,"正片从在线进度定位并只上报一次开始");
        Check(!backend.Location.Contains("fixture-token") && backend.Location.StartsWith("http://127.0.0.1:"),"播放器只接收本机随机媒体路径且没有账号令牌");
        controller.Pause(true);backend.Value.PositionTicks=90000000;await controller.Progress("Pause");await controller.Stop();await controller.Stop();
        Check(state.Stages.SequenceEqual(new[]{"start","progress","stop"}) && state.Reports[1].IsPaused && state.Reports[2].PositionTicks==90000000 && backend.Value.Disposed,"正片暂停及最终停止按同一会话串行同步，重复停止幂等");
        var failedState=new State{FailProgress=true};var failedBackend=new Backend();var failed=new PlaybackController(new Client(root,()=>new Handler(failedState)),failedBackend);
        await failed.Start("movie1",CancellationToken.None);try{await failed.Progress("TimeUpdate");}catch(IOException){}await failed.Progress("TimeUpdate");await failed.Stop();
        Check(failed.SyncFailed && failedBackend.Value.Paused && failedState.Stages.SequenceEqual(new[]{"start","progress"}),"同步失败暂停且不重放离线进度覆盖其他设备");
        var assets=await client.PreviewAssets("movie1",CancellationToken.None);
        Check(assets!=null && File.Exists(assets.MusicPath) && File.Exists(assets.VideoPath) && File.Exists(assets.VideoPath+".source.json") && state.Stages.Count==3,"网络主题素材先缓存并记录来源，预览获取不写正片进度");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool canceled=false;try{await client.PreviewAssets("movie1",cancel.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled,"取消的主题素材获取不返回缓存或重新联网");}
        var pausedBackend=new Backend();var pausedState=new State();var pausedController=new PlaybackController(new Client(root,()=>new Handler(pausedState)),pausedBackend);
        pausedController.Pause(true);await pausedController.Start("movie1",CancellationToken.None);Check(pausedBackend.Value.Paused && pausedState.Reports[0].IsPaused,"准备期间失焦暂停状态在首帧后仍保留");await pausedController.Stop();
        Check(Client.SearchQuery("中文 &x=1","Movie").Contains("%26x%3D1"),"搜索参数编码且不注入其他查询字段");
        bool rejected=false;try{Client.SearchQuery("x","Movie&x=1");}catch(ArgumentException){rejected=true;}Check(rejected,"影视类型筛选拒绝非白名单参数");
        rejected=false;try{Client.DirectSource(new PlaybackInfo{MediaSources=new[]{new MediaSource{Id="s",RequiresOpening=true,SupportsDirectStream=true}}});}catch(NotSupportedException){rejected=true;}Check(rejected,"直播及需打开的媒体源不误报直传支持");
        var relayState=new State();using(var relay=new StreamRelay("https://example.invalid/stream","fixture-token",new Handler(relayState)))
        using(var http=new HttpClient(new HttpClientHandler{UseProxy=false})){
            var wrong=new UriBuilder(relay.Location){Path="/wrong"};var denied=await http.GetAsync(wrong.Uri);
            Check(denied.StatusCode==HttpStatusCode.Forbidden && relayState.Reads==0,"本机代理错误路径在访问上游前被拒绝");
            var request=new HttpRequestMessage(HttpMethod.Get,relay.Location);request.Headers.Range=new System.Net.Http.Headers.RangeHeaderValue(2,4);
            using(var response=await http.SendAsync(request)){Check(response.StatusCode==HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[]{2,3,4}) && relayState.Range=="bytes=2-4" && relayState.Auth=="fixture-token","本机媒体代理保留 Range/206 字节并只在上游附加认证头");}
            relay.Dispose();bool closed=false;try{await http.GetAsync(relay.Location);}catch(HttpRequestException){closed=true;}Check(closed,"播放结束释放本机媒体监听端口");
        }
    }
}
