using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Linq;

namespace EmbyBrowser
{
    [DataContract]
    public sealed class Connection
    {
        [DataMember] public int schema;
        [DataMember] public string Server, UserId, ServerId, DeviceId, ProtectedToken;
    }
    [DataContract]
    public sealed class UserData
    {
        [DataMember] public long PlaybackPositionTicks;
        [DataMember] public bool Played;
        [DataMember] public int PlayCount;
        [DataMember] public string LastPlayedDate;
    }
    [DataContract]
    public sealed class MediaItem
    {
        [DataMember] public string Id, Name, Type, Overview, SeriesName, ParentId;
        [DataMember] public int? ProductionYear, IndexNumber, ParentIndexNumber;
        [DataMember] public long? RunTimeTicks;
        [DataMember] public UserData UserData;
        public bool Offline;
    }
    [DataContract]
    public sealed class ItemResult
    {
        [DataMember] public MediaItem[] Items;
        [DataMember] public int TotalRecordCount;
    }
    public sealed class Page
    {
        public MediaItem[] Items;
        public int Total;
        public bool Offline;
        public bool HasMore;
    }

    /// <summary>Emby 只读客户端；令牌绑定服务器和用户，不放入图片或媒体 URL。</summary>
    public sealed partial class Client
    {
        private readonly Connection connection;
        private readonly string token, cacheRoot;
        private readonly string configuredLibrary;
        private readonly Func<HttpMessageHandler> transport;
        public const int PageSize = 30;

        /// <summary>读取 DPAPI 连接；篡改服务器、用户或密文时在发送请求前失败。</summary>
        public Client(string userRoot) : this(userRoot,null) { }
        /// <summary>测试可注入消息处理器；实际连接仍始终校验 HTTPS 与 DPAPI 身份。</summary>
        public Client(string userRoot,Func<HttpMessageHandler> transport)
        {
            this.transport=transport;
            string root = Path.Combine(userRoot,"emby");
            connection = Read<Connection>(Path.Combine(root,"connection.json"));
            Uri address;
            if (connection == null || connection.schema != 1 || !Uri.TryCreate(connection.Server,UriKind.Absolute,out address)
                || address.Scheme != "https" || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new InvalidDataException("连接配置不兼容，请重新登录。");
            CheckId(connection.UserId); CheckId(connection.ServerId); CheckId(connection.DeviceId);
            byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(connection.ProtectedToken),Encoding.UTF8.GetBytes(Entropy(connection)),DataProtectionScope.CurrentUser);
            try { token = Encoding.UTF8.GetString(decrypted); }
            finally { Array.Clear(decrypted,0,decrypted.Length); }
            if (String.IsNullOrWhiteSpace(token) || token.IndexOfAny(new[] {'\r','\n'}) >= 0) throw new InvalidDataException("连接令牌无效。");
            cacheRoot = Path.Combine(root,"cache",Hash(connection.Server+"|"+connection.ServerId+"|"+connection.UserId));
            var library=LibrarySettings.Load(userRoot);if(library.identity==Identity&&!String.IsNullOrEmpty(library.libraryId)){CheckId(library.libraryId);configuredLibrary=library.libraryId;}
        }

        /// <summary>DPAPI 附加熵将连接地址与身份一并绑定，阻止明文地址替换转发令牌。</summary>
        public static string Entropy(Connection value) { return "ModularShell.Emby.v1|"+value.Server+"|"+value.ServerId+"|"+value.UserId; }
        /// <summary>仅允许服务器的稳定 ID，不让路径段解释为目录跳转。</summary>
        public static void CheckId(string id) { if (id == null || !Regex.IsMatch(id,@"\A[a-zA-Z0-9-]{1,128}\z")) throw new InvalidDataException("媒体引用无效。"); }
        /// <summary>按服务器、账号隔离可重建缓存，不保存令牌到缓存文件。</summary>
        private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-",""); }
        /// <summary>解析独立数据模型，不引用 Playnite 或宿主业务类型。</summary>
        public static T Read<T>(string path) { using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        /// <summary>反序列化真实 JSON 响应；失败只产生本地诊断，不输出响应或令牌。</summary>
        private static T Decode<T>(byte[] bytes) { using (var stream = new MemoryStream(bytes)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }

        /// <summary>有界请求拒绝重定向，取消会中止网络 I/O；不记录响应正文。</summary>
        private async Task<byte[]> Request(string relative, CancellationToken cancel, int limit, byte[] body = null)
        {
            cancel.ThrowIfCancellationRequested();
            // 为本客户端限定 TLS，不改 Playnite 进程的全局网络选项。
            using (var handler = transport==null ? (HttpMessageHandler)CreateHandler() : transport())
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,connection.Server.TrimEnd('/')+"/"+relative))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Token",token);
                if(body!=null){request.Content=new ByteArrayContent(body);request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");}
                request.Headers.TryAddWithoutValidation("Authorization","Emby Client=\"Modular Shell\", Device=\"Windows\", DeviceId=\""+connection.DeviceId+"\", Version=\"0.6.0\"");
                deadline.CancelAfter(15000);
                try
                {
                    using (var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false))
                    {
                        int code=(int)response.StatusCode;
                        if(code==401 || code==403)throw new UnauthorizedAccessException("登录已失效或无权访问，请重新登录。");
                        if(code>=300 && code<400)throw new IOException("服务器返回重定向，已拒绝转发登录令牌。");
                        if(code>=400)throw new IOException("Emby 请求失败（HTTP "+code+"）。");
                        using(var source=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using(var abort=deadline.Token.Register(source.Dispose))
                        using(var output=new MemoryStream())
                        {
                            byte[] buffer=new byte[32768];int count;
                            while((count=await source.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false))>0)
                            {
                                if(output.Length+count>limit)throw new IOException("响应超过客户端限额。");
                                output.Write(buffer,0,count);
                            }
                            cancel.ThrowIfCancellationRequested();
                            return output.ToArray();
                        }
                    }
                }
                catch(HttpRequestException) { cancel.ThrowIfCancellationRequested(); throw new IOException("连接失败或网络不可用。"); }
                catch(OperationCanceledException) { cancel.ThrowIfCancellationRequested(); throw new IOException("连接超时。"); }
                catch(ObjectDisposedException) { cancel.ThrowIfCancellationRequested(); throw new IOException("连接已中止。"); }
            }
        }

        /// <summary>三类首页分区语义独立；影视库与剧集子项分页，不创建伪游戏。</summary>
        public async Task<Page> Browse(string section, int start, string parent, CancellationToken cancel)
        {
            return await Browse(section,start,parent,cancel,null,null).ConfigureAwait(false);
        }
        /// <summary>服务端搜索筛选参与缓存键和分页，不过滤当前页伪造总数。</summary>
        public async Task<Page> Browse(string section, int start, string parent, CancellationToken cancel, string search, string kind)
        {
            if (start < 0 || start > 1000000) throw new ArgumentOutOfRangeException("start");
            string path = "Users/"+connection.UserId+"/Items";
            string common = "Limit="+PageSize+"&StartIndex="+start+"&Fields=Overview,UserData&EnableUserData=true";
            // 所选服务器目录约束首页分区和搜索；进入季/单集时使用具体父级。
            string scope=section=="children"?null:(!String.IsNullOrEmpty(parent)?parent:configuredLibrary);
            if(!String.IsNullOrEmpty(scope)){CheckId(scope);common+="&ParentId="+scope;}
            bool array = false;
            switch (section)
            {
                case "resume": path += "/Resume?"+common+"&MediaTypes=Video"; break;
                case "recent": path += "?"+common+"&Recursive=true&IncludeItemTypes=Movie,Episode&SortBy=DatePlayed&SortOrder=Descending"; break;
                case "latest": path += "/Latest?"+common+"&IncludeItemTypes=Movie,Episode&GroupItems=false"; array = true; break;
                case "library": path += "?"+common+"&Recursive=true"+SearchQuery(search,kind)+"&SortBy=SortName&SortOrder=Ascending"; break;
                case "children": CheckId(parent); path += "?"+common+"&ParentId="+parent+"&SortBy=ParentIndexNumber,IndexNumber,SortName&SortOrder=Ascending"; break;
                default: throw new ArgumentException("未知影视分区");
            }
            string cache = Path.Combine(cacheRoot,Hash(path)+".json");
            byte[] data; bool offline = false;
            try
            {
                data = await Request(path,cancel,8*1024*1024).ConfigureAwait(false);
                // 先校验响应，坏响应不覆盖最后一次有效缓存。
                Normalize(data,array);
                cancel.ThrowIfCancellationRequested();
                SaveCache(cache,data);
            }
            catch (IOException)
            {
                cancel.ThrowIfCancellationRequested();
                if (!File.Exists(cache)) throw;
                data = File.ReadAllBytes(cache); offline = true;
            }
            var page = Normalize(data,array); page.Offline = offline; page.HasMore=!array && start+page.Items.Length<page.Total;
            // 最近观看包含尚未看完的内容；不能用 Played=true 代替观看历史。
            if(section=="recent")
            {
                page.Items=RecentItems(page.Items);
                // 过滤后的数量不能代表服务器是否还有下一页。
                page.Total=-1;
            }
            return page;
        }

        /// <summary>搜索词按参数编码，类型为白名单；空搜索保持现有影片与剧集列表。</summary>
        public static string SearchQuery(string search,string kind)
        {
            if(!String.IsNullOrEmpty(kind) && kind!="Movie" && kind!="Series" && kind!="Episode")throw new ArgumentException("未知影视类型");
            search=(search??"").Trim(); if(search.Length>120)throw new ArgumentException("搜索词过长");
            return "&IncludeItemTypes="+(String.IsNullOrEmpty(kind)?"Movie,Series":kind)+(search.Length==0?"":"&SearchTerm="+Uri.EscapeDataString(search));
        }
        /// <summary>获取账号可见的真实媒体库分区，供显式分区选择和实连接定位。</summary>
        public async Task<MediaItem[]> Libraries(CancellationToken cancel)
        {
            var value=Decode<ItemResult>(await Request("Users/"+connection.UserId+"/Views",cancel,2*1024*1024).ConfigureAwait(false));
            if(value==null || value.Items==null)throw new InvalidDataException("分区响应无效");return value.Items;
        }

        /// <summary>兼容不返回 LastPlayedDate 的服务器，保留有播放次数或续播位置的未完播内容。</summary>
        public static MediaItem[] RecentItems(MediaItem[] items)
        {
            return items.Where(i=>i.UserData!=null && (i.UserData.PlayCount>0 || i.UserData.PlaybackPositionTicks>0 || i.UserData.Played || !String.IsNullOrWhiteSpace(i.UserData.LastPlayedDate))).ToArray();
        }

        /// <summary>统一数组和分页响应；非法结构不当作正常空库。</summary>
        private static Page Normalize(byte[] bytes, bool array)
        {
            if (array) { var items = Decode<MediaItem[]>(bytes); if (items == null) throw new InvalidDataException("列表响应无效。"); return new Page {Items=items,Total=items.Length}; }
            var value = Decode<ItemResult>(bytes);
            if (value == null || value.Items == null || value.TotalRecordCount < 0) throw new InvalidDataException("分页响应无效。");
            return new Page { Items=value.Items, Total=value.TotalRecordCount };
        }

        /// <summary>只读详情带用户进度，不上报播放开始、已看或收藏动作。</summary>
        public async Task<MediaItem> Detail(string id, CancellationToken cancel)
        {
            CheckId(id);
            string path="Users/"+connection.UserId+"/Items/"+id;
            string cache=Path.Combine(cacheRoot,Hash(path)+".json");
            try
            {
                var bytes=await Request(path,cancel,2*1024*1024).ConfigureAwait(false);
                var item=Decode<MediaItem>(bytes);
                if(item==null || item.Id!=id)throw new InvalidDataException("详情引用无效。");
                cancel.ThrowIfCancellationRequested();SaveCache(cache,bytes);return item;
            }
            catch(IOException)
            {
                cancel.ThrowIfCancellationRequested();if(!File.Exists(cache))throw;
                var item=Decode<MediaItem>(File.ReadAllBytes(cache));
                if(item==null || item.Id!=id)throw new InvalidDataException("详情缓存无效。");
                item.Offline=true;return item;
            }
        }
        /// <summary>认证海报缓存一天；网络失败回退旧图，认证失败不回退。</summary>
        public async Task<byte[]> Poster(string id, CancellationToken cancel)
        {
            CheckId(id);cancel.ThrowIfCancellationRequested();
            string path="Items/"+id+"/Images/Primary?MaxWidth=300&Quality=80";
            string cache=Path.Combine(cacheRoot,Hash(path)+".image");
            if(File.Exists(cache) && DateTime.UtcNow-File.GetLastWriteTimeUtc(cache)<TimeSpan.FromDays(1))return File.ReadAllBytes(cache);
            try
            {
                var bytes=await Request(path,cancel,3*1024*1024).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();SaveCache(cache,bytes);return bytes;
            }
            catch(IOException) { cancel.ThrowIfCancellationRequested();if(!File.Exists(cache))throw;return File.ReadAllBytes(cache); }
        }
        /// <summary>缓存失败不影响本次浏览，缓存总量受 64 MiB 上限控制。</summary>
        private void SaveCache(string path, byte[] bytes)
        {
            // 同目录短临时名避免旧 Framework MAX_PATH 限制；不叠加完整哈希文件名。
            string temporary = Path.Combine(cacheRoot,"w-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                Directory.CreateDirectory(cacheRoot);
                long size = 0; foreach (var file in new DirectoryInfo(cacheRoot).GetFiles()) if(file.FullName!=path) size += file.Length;
                if (size+bytes.Length > 64L*1024*1024) return;
                File.WriteAllBytes(temporary,bytes);
                if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            }
            catch (IOException) { /* 缓存可重建，磁盘写入失败不丢弃当前网络结果。 */ }
            catch (UnauthorizedAccessException) { /* 只读目录仍允许当前会话浏览。 */ }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
        }
    }
}
