using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyBrowser
{
    /// <summary>单媒体本机代理：随机路径、仅 IPv4 回环、无任意 URL 转发，支持播放器 Range 定位。</summary>
    public sealed class StreamRelay : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly SemaphoreSlim slots=new SemaphoreSlim(4,4);
        private readonly HttpClient http;
        private readonly string upstream, credential, path;
        public string Location { get; private set; }
        /// <summary>认证头只添加到固定 HTTPS 上游；自定义处理器仅用于隔离协议测试。</summary>
        public StreamRelay(string upstream,string credential,HttpMessageHandler transport=null)
        {
            Uri uri;if(!Uri.TryCreate(upstream,UriKind.Absolute,out uri) || uri.Scheme!="https" || uri.UserInfo.Length!=0)throw new ArgumentException("上游必须为 HTTPS");
            this.upstream=upstream;this.credential=credential;
            path="/"+Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N")+"/stream";
            http=new HttpClient(transport??Client.CreateHandler());http.Timeout=Timeout.InfiniteTimeSpan;
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start(4);
            Location="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+path;
            // 所有接受循环异常在内部处理，释放不产生未观察任务异常。
            Task.Run((Func<Task>)Accept);
        }
        /// <summary>连接数量有界，退出中止监听与所有在途连接。</summary>
        private async Task Accept()
        {
            try { while(!lifetime.IsCancellationRequested){await slots.WaitAsync(lifetime.Token);TcpClient socket;
                try{socket=await listener.AcceptTcpClientAsync();}catch{slots.Release();throw;}
                Task.Run(()=>Serve(socket));
            } }catch(OperationCanceledException){}catch(ObjectDisposedException){}catch(SocketException){}
        }
        /// <summary>读取有界 HTTP 请求，只处理 GET/HEAD，忽略其他请求头且不转发外部 Cookie。</summary>
        private async Task Serve(TcpClient socket)
        {
            try {
                using(socket)
                using(var closed=lifetime.Token.Register(socket.Close))
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                using(var network=socket.GetStream()) {
                    deadline.CancelAfter(15000);
                    byte[] one=new byte[1];var header=new StringBuilder();
                    while(header.Length<16384 && !header.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)) {
                        if(await network.ReadAsync(one,0,1,deadline.Token)==0)return;header.Append((char)one[0]);
                    }
                    string[] lines=header.ToString().Split(new[]{"\r\n"},StringSplitOptions.None);
                    string[] requestLine=lines[0].Split(' ');
                    if(requestLine.Length!=3 || requestLine[1]!=path || (requestLine[0]!="GET" && requestLine[0]!="HEAD") || !header.ToString().EndsWith("\r\n\r\n")) { await Reply(network,"403 Forbidden",deadline.Token);return; }
                    string range=null;
                    foreach(string line in lines)if(line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase)) {
                        if(range!=null){await Reply(network,"400 Bad Request",deadline.Token);return;}
                        range=line.Substring(6).Trim();if(!Regex.IsMatch(range,@"\Abytes=(\d+-\d*|-\d+)\z")){await Reply(network,"416 Range Not Satisfiable",deadline.Token);return;}
                    }
                    using(var request=new HttpRequestMessage(requestLine[0]=="HEAD"?HttpMethod.Head:HttpMethod.Get,upstream)) {
                        request.Headers.TryAddWithoutValidation("X-Emby-Token",credential);
                        if(range!=null)request.Headers.TryAddWithoutValidation("Range",range);
                        using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token)) {
                            int code=(int)response.StatusCode;
                            if(code!=200 && code!=206){await Reply(network,"502 Bad Gateway",deadline.Token);return;}
                            var output=new StringBuilder("HTTP/1.1 "+code+" OK\r\nConnection: close\r\nAccept-Ranges: bytes\r\n");
                            if(response.Content.Headers.ContentLength.HasValue)output.Append("Content-Length: "+response.Content.Headers.ContentLength.Value+"\r\n");
                            if(response.Content.Headers.ContentRange!=null)output.Append("Content-Range: "+response.Content.Headers.ContentRange+"\r\n");
                            output.Append("Content-Type: application/octet-stream\r\n\r\n");
                            byte[] bytes=Encoding.ASCII.GetBytes(output.ToString());await network.WriteAsync(bytes,0,bytes.Length,deadline.Token);
                            if(requestLine[0]=="HEAD")return;
                            using(var stream=await response.Content.ReadAsStreamAsync())
                            using(var abort=deadline.Token.Register(stream.Dispose)) {
                                byte[] buffer=new byte[65536];int count;
                                // 每个读写块有超时，不对整部影片设置十五秒总限额。
                                while(true){deadline.CancelAfter(30000);count=await stream.ReadAsync(buffer,0,buffer.Length,deadline.Token);if(count==0)break;await network.WriteAsync(buffer,0,count,deadline.Token);}
                            }
                        }
                    }
                }
            }catch(Exception){/* 客户端跳转定位或关闭连接属于正常取消，响应正文和令牌不记录。 */}
            finally{slots.Release();}
        }
        /// <summary>固定错误响应不包含上游地址、标题或凭证。</summary>
        private static Task Reply(Stream stream,string status,CancellationToken cancel){byte[] data=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");return stream.WriteAsync(data,0,data.Length,cancel);}
        /// <summary>先取消连接再关闭 HTTP 客户端，监听仅在播放会话中存活。</summary>
        public void Dispose(){if(lifetime.IsCancellationRequested)return;lifetime.Cancel();listener.Stop();http.Dispose();}
    }
}

