using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ModularShell;

namespace LocalAssets
{
    /// <summary>磁盘不足单独上报，队列暂停而不是逐项重试写满磁盘。</summary>
    internal sealed class DiskSpaceException : IOException {internal DiskSpaceException():base("磁盘空间不足，自动获取已暂停"){} }
    /// <summary>自动发现与网络选源只存在于素材插件；标题相似不构成来源身份。</summary>
    internal sealed class SteamAssets
    {
        private readonly string tools,cache;
        private readonly string language;
        /// <summary>工具和缓存根来自插件目录与用户目录，不调用Shell。</summary>
        internal SteamAssets(string tools,string cache,string language="zh-Hans"){this.tools=tools;this.cache=cache;this.language=language;}
        /// <summary>只根据明确标签标记语言，不将未知视频猜作中文或英文。</summary>
        internal static string DetectLanguage(string title)
        {
            title=title??"";
            if(Regex.IsMatch(title,"繁體|繁体|traditional chinese|(?:^|[_ -])(TC|CHT|ZHTW)(?:[_ -]|$)",RegexOptions.IgnoreCase))return "zh-Hant";
            if(Regex.IsMatch(title,"简体|簡體|中文|中文字幕|simplified chinese|(?:^|[_ -])(SC|CHS|ZHCN)(?:[_ -]|$)",RegexOptions.IgnoreCase))return "zh-Hans";
            if(Regex.IsMatch(title,"deutsch|german|(?:^|[_ -])(GE|DE|GER)(?:[_ -]|$)",RegexOptions.IgnoreCase))return "de";
            if(Regex.IsMatch(title,"français|french|(?:^|[_ -])(FR|FRE)(?:[_ -]|$)",RegexOptions.IgnoreCase))return "fr";
            if(Regex.IsMatch(title,"日本語|japanese|(?:^|[_ -])(JP|JA)(?:[_ -]|$)",RegexOptions.IgnoreCase))return "ja";
            if(Regex.IsMatch(title,"english|(?:^|[_ -])(EN|ENG|US|UK)(?:[_ -]|$)",RegexOptions.IgnoreCase))return "en";
            return "unknown";
        }
        /// <summary>同清晰度下优先所选语言，其次英语，再未知；不限语言不改变原排序。</summary>
        internal static int LanguageScore(string actual,string preferred){return preferred=="auto"?0:actual==preferred?4:actual=="en"?2:String.IsNullOrEmpty(actual)||actual=="unknown"?1:0;}
        /// <summary>Steam请求使用官方语言参数；作品匹配仍依靠App ID和官方频道。</summary>
        private string SteamLanguage {get{return language=="zh-Hans"?"schinese":language=="zh-Hant"?"tchinese":language=="ja"?"japanese":language=="de"?"german":language=="fr"?"french":"english";}}
        /// <summary>磁盘预留一GiB供宿主工作；不设置缓存总量上限，也不删除用户文件。</summary>
        private void Space(){Directory.CreateDirectory(cache);if(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cache))).AvailableFreeSpace<1024L*1024*1024)throw new DiskSpaceException();}
        /// <summary>有界HTTPS读取，每次请求可取消；网络失败不回退到模糊低清URL。</summary>
        private byte[] Fetch(string url,CancellationToken token,long maximum)
        {
            var uri=new Uri(url);if(uri.Scheme!="https")throw new InvalidDataException("素材来源必须HTTPS");
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(uri);request.Timeout=30000;request.ReadWriteTimeout=15000;request.UserAgent="ModularShell/0.8 automatic-assets";
            // 独立连接组与系统代理，避免宿主其他插件修改默认代理或占满连接池。
            request.Proxy=WebRequest.GetSystemWebProxy();request.ConnectionGroupName="ModularShellAssets";request.KeepAlive=false;
            try{using(token.Register(()=>request.Abort()))using(var response=request.GetResponse())using(var input=response.GetResponseStream())using(var output=new MemoryStream()){
                var buffer=new byte[65536];int read;while((read=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();if(output.Length+read>maximum)throw new IOException("素材响应超过单文件安全限制");output.Write(buffer,0,read);}return output.ToArray();
            }}catch(WebException){token.ThrowIfCancellationRequested();throw;}
        }
        /// <summary>源URL摘要构成稳定文件名，不把标题或远程路径当本地路径。</summary>
        private static string UrlKey(string url){using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","").ToLowerInvariant();}
        /// <summary>下载到临时文件、验证真实文件头后发布；坏图只保留拒绝记录。</summary>
        private MediaRecord Image(string url,string kind,string attribution,CancellationToken token)
        {
            Space();string destination=Path.Combine(cache,UrlKey(url)+".image"),temp=destination+"."+Guid.NewGuid().ToString("N")+".partial";
            try{File.WriteAllBytes(temp,Fetch(url,token,40*1024*1024));var record=MediaFiles.Probe(temp,kind,tools,token);record.url=url;record.attribution=attribution;destination=Path.Combine(cache,UrlKey(url)+"-"+record.sha256.Substring(0,16)+".image");if(!File.Exists(destination))File.Move(temp,destination);else File.Delete(temp);record.path=destination;record.checkedTicks=DateTime.UtcNow.Ticks;return record;}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        /// <summary>全部外部程序使用固定路径和有限重试；优先原编码封装，不设置缩放滤镜。</summary>
        private List<string> Arguments(){return new List<string>{"--ignore-config","--no-playlist","--no-progress","--no-warnings","--socket-timeout","15","--retries","1","--fragment-retries","1","--ffmpeg-location",tools,"--js-runtimes","node:"+Path.Combine(tools,"node.exe")};}
        /// <summary>yt-dlp元数据读取不下载媒体，短超时防止后台任务永远占位。</summary>
        private object Metadata(string url,CancellationToken token){var args=Arguments();if(Regex.IsMatch(url,@"youtube\.com/(channel/|user/|c/|@)"))args.AddRange(new[]{"--playlist-end","1","--flat-playlist"});args.AddRange(new[]{"--dump-single-json","--skip-download",url});return MediaFiles.Json().DeserializeObject(MediaFiles.Run(Path.Combine(tools,"yt-dlp.exe"),args,token,90000));}
        /// <summary>2160p以下最高合格原始轨道，绝不调用movie480或微预告路径。</summary>
        private MediaRecord Video(string url,string attribution,CancellationToken token)
        {
            Space();var directory=Path.Combine(cache,"work-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try{
                var args=Arguments();args.AddRange(new[]{"-f","bestvideo[height>=1080][height<=2160]+bestaudio/best[height>=1080][height<=2160]","--merge-output-format","mkv","--max-filesize","2G","-o",Path.Combine(directory,"source.%(ext)s"),url});
                MediaFiles.Run(Path.Combine(tools,"yt-dlp.exe"),args,token,1200000);token.ThrowIfCancellationRequested();
                var source=Directory.GetFiles(directory).FirstOrDefault(f=>!f.EndsWith(".part")&&!f.EndsWith(".ytdl"));if(source==null)throw new IOException("预告没有合格轨道");
                var record=MediaFiles.Probe(source,"video",tools,token);record.url=url;record.attribution=attribution;record.language=DetectLanguage(attribution);
                string destination=Path.Combine(cache,UrlKey(url)+"-"+record.sha256.Substring(0,16)+Path.GetExtension(source));if(!File.Exists(destination))File.Move(source,destination);record.path=destination;record.checkedTicks=DateTime.UtcNow.Ticks;return record;
            }finally{CleanWork(directory);}
        }
        /// <summary>仅清理本次随机工作目录，不跟随目录链接，不递归触及用户素材。</summary>
        private void CleanWork(string directory){if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(cache)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return;foreach(var file in Directory.GetFiles(directory))try{File.Delete(file);}catch{}try{Directory.Delete(directory);}catch{}}
        /// <summary>本机OST必须是独立音频且名称明确，不解包游戏资源，不抽取预告音轨。</summary>
        private void LocalMusic(GameItem item,Action<MediaRecord> publish,CancellationToken token)
        {
            string installed=FindInstalledDirectory(item);if(String.IsNullOrEmpty(installed)||!Directory.Exists(installed))return;
            var folders=new Queue<Tuple<string,int>>();folders.Enqueue(Tuple.Create(installed,0));int visited=0;
            var candidates=new List<Tuple<string,int>>();
            // Steam独立原声通常放在steamapps/music，并不位于游戏安装目录内。
            var common=Directory.GetParent(installed);var steamapps=common==null?null:common.Parent;
            if(steamapps!=null){string music=Path.Combine(steamapps.FullName,"music");if(Directory.Exists(music))foreach(string album in Directory.GetDirectories(music))if(Normalize(Path.GetFileName(album)).Contains(Normalize(item.Title)))folders.Enqueue(Tuple.Create(album,0));}
            while(folders.Count>0&&visited++<200){token.ThrowIfCancellationRequested();var folder=folders.Dequeue();
                string[] files,children;try{files=Directory.GetFiles(folder.Item1);children=Directory.GetDirectories(folder.Item1);}catch{continue;}
                foreach(var file in files){if(!Regex.IsMatch(file,@"\.(mp3|m4a|wav|flac|ogg)$",RegexOptions.IgnoreCase))continue;
                    int score=Regex.IsMatch(Path.GetFileNameWithoutExtension(file),@"main[ _-]*(theme|menu)|title[ _-]*theme",RegexOptions.IgnoreCase)?100:Regex.IsMatch(folder.Item1,@"[\\/]main[_ -]?menu[\\/]",RegexOptions.IgnoreCase)?90:Regex.IsMatch(folder.Item1,@"[\\/][^\\/]*(soundtrack|original[_ -]?score|\bost\b)[^\\/]*[\\/]",RegexOptions.IgnoreCase)?50:0;
                    if(score>0)candidates.Add(Tuple.Create(file,score+(Regex.IsMatch(Path.GetFileName(file),@"^0?1\D")?5:0)));
                }
                if(folder.Item2<6)foreach(var child in children.OrderByDescending(p=>Regex.IsMatch(p,"music|soundtrack|main.menu|ost",RegexOptions.IgnoreCase)))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)folders.Enqueue(Tuple.Create(child,folder.Item2+1));
            }
            foreach(var candidate in candidates.OrderByDescending(c=>c.Item2).ThenBy(c=>c.Item1,StringComparer.OrdinalIgnoreCase).Take(3)){
                try{var record=MediaFiles.Probe(candidate.Item1,"music",tools,token);record.url=candidate.Item1;record.attribution="本机已安装原声 · "+item.Title;publish(record);if(record.status=="accepted")return;}catch(OperationCanceledException){throw;}catch{}
            }
        }
        /// <summary>SDK缺少安装目录时只读取Steam公开本地清单，不猜游戏路径、不修改主库。</summary>
        private string FindInstalledDirectory(GameItem item)
        {
            if(!String.IsNullOrEmpty(item.InstallDirectory)&&Directory.Exists(item.InstallDirectory))return item.InstallDirectory;
            ulong id;if(!String.Equals(item.Platform,"Steam",StringComparison.OrdinalIgnoreCase)||!UInt64.TryParse(item.SourceId,out id))return null;
            string steam=Convert.ToString(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null));if(String.IsNullOrEmpty(steam))return null;
            var libraries=new List<string>{steam};string folders=Path.Combine(steam,"steamapps","libraryfolders.vdf");
            if(File.Exists(folders))foreach(Match match in Regex.Matches(File.ReadAllText(folders),"\"path\"\\s*\"([^\"]+)\""))libraries.Add(match.Groups[1].Value.Replace("\\\\","\\"));
            foreach(string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase)){
                string manifest=Path.Combine(library,"steamapps","appmanifest_"+id+".acf");if(!File.Exists(manifest))continue;
                var match=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s*\"([^\"]+)\"");if(!match.Success)continue;
                string boundary=Path.GetFullPath(Path.Combine(library,"steamapps","common"))+Path.DirectorySeparatorChar;
                string candidate=Path.GetFullPath(Path.Combine(boundary,match.Groups[1].Value));if(candidate.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(candidate))return candidate;
            }return null;
        }
        /// <summary>同App ID来源是身份依据；非Steam或非数字ID保持本地回退。</summary>
        internal void Acquire(GameItem item,Func<List<MediaRecord>> existing,Action<MediaRecord> publish,CancellationToken token,bool force=false)
        {
            Space();
            if(!existing().Any(f=>f.kind=="music"&&f.status=="accepted"))LocalMusic(item,publish,token);
            ulong id;if(!String.Equals(item.Platform,"Steam",StringComparison.OrdinalIgnoreCase)||!UInt64.TryParse(item.SourceId,out id))return;
            LocalSteamImages(id,existing,publish,token);
            var response=MediaFiles.Json().DeserializeObject(Encoding.UTF8.GetString(Fetch("https://store.steampowered.com/api/appdetails?appids="+id+"&l="+SteamLanguage,token,8*1024*1024)));
            var app=MediaFiles.Get(MediaFiles.Get(response,id.ToString()),"data");if(app==null)throw new IOException("Steam商店未返回当前App ID");
            string attribution="Steam · "+MediaFiles.Text(app,"name");var issues=new List<string>();
            // path_full提供源图；每次测量真实尺寸后排序，最多保留五张正式候选。
            var screenshots=MediaFiles.Items(MediaFiles.Get(app,"screenshots")).Select(s=>MediaFiles.Text(s,"path_full")).Where(s=>s.StartsWith("https://",StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            foreach(string url in screenshots){token.ThrowIfCancellationRequested();if(!force&&existing().Any(f=>f.url==url))continue;if(!force&&existing().Count(f=>f.kind=="background"&&f.Rank==4&&f.status=="accepted")>=5)break;
                try{publish(Image(url,"background",attribution,token));}catch(OperationCanceledException){throw;}catch(Exception ex){issues.Add(ex.Message);}}
            if(!existing().Any(f=>f.kind=="background"&&f.Rank>=2&&f.status=="accepted")){
                foreach(string url in ImageSources(app,id,"library_hero_2x.jpg"))try{var hero=Image(url,"background",attribution+" · Hero 2x（宽屏备选）",token);publish(hero);if(hero.status=="accepted")break;}catch(OperationCanceledException){throw;}catch(Exception ex){issues.Add(ex.Message);}
            }
            if(force||!existing().Any(f=>f.kind=="logo"&&f.status=="accepted"&&f.width>=1280))foreach(var url in new[]{"logo_2x.png","logo.png"}.SelectMany(name=>ImageSources(app,id,name))){
                try{publish(Image(url,"logo",attribution,token));break;}catch(OperationCanceledException){throw;}catch(Exception ex){issues.Add(ex.Message);}}
            if(force||!existing().Any(f=>f.kind=="video"&&f.status=="accepted"&&f.height>=2160)){
                foreach(var movie in MediaFiles.Items(MediaFiles.Get(app,"movies")).OrderByDescending(m=>LanguageScore(DetectLanguage(MediaFiles.Text(m,"name")),language)).Take(3)){
                    string url=MediaFiles.Text(MediaFiles.Get(movie,"mp4"),"max");if(String.IsNullOrEmpty(url))url=MediaFiles.Text(MediaFiles.Get(movie,"webm"),"max");if(String.IsNullOrEmpty(url))url=MediaFiles.Text(movie,"hls_h264");if(String.IsNullOrEmpty(url))url=MediaFiles.Text(movie,"hls_av1");if(String.IsNullOrEmpty(url))continue;
                    url=url.Replace("http://","https://");if(!force&&existing().Any(f=>f.url==url&&f.status=="accepted"))continue;
                    try{
                        // HLS主清单可先比较轨道高度，已有同等或更高质量时不重复下载另一部低清预告。
                        if(!force&&url.Contains(".m3u8")){string master=Encoding.UTF8.GetString(Fetch(url,token,2*1024*1024));int best=Regex.Matches(master,@"RESOLUTION=\d+x(\d+)").Cast<Match>().Select(m=>Int32.Parse(m.Groups[1].Value)).Where(h=>h<=2160).DefaultIfEmpty(0).Max();if(best>0&&existing().Any(f=>f.kind=="video"&&f.status=="accepted"&&f.height>=best))continue;}
                        publish(Video(url,attribution+" · "+MediaFiles.Text(movie,"name"),token));if(existing().Any(f=>f.kind=="video"&&f.status=="accepted"&&f.height>=2160))break;
                    }catch(OperationCanceledException){throw;}catch(Exception ex){issues.Add(ex.Message);}
                }
            }
            // 原声发现独立于预告是否已经4K，不能因为视频齐全而跳过音乐。
            if(!existing().Any(f=>f.kind=="video"&&f.status=="accepted"&&f.height>=2160)||!existing().Any(f=>f.kind=="music"&&f.status=="accepted"))
                try{OfficialYoutube(app,existing,publish,token);}catch(OperationCanceledException){throw;}catch(Exception ex){issues.Add("官方YouTube："+ex.Message);}
            var missing=new[]{"background","logo","video","music"}.Where(kind=>!existing().Any(f=>f.kind==kind&&f.status=="accepted")).ToArray();
            if(missing.Length>0)throw new IOException("缺少 "+String.Join(" / ",missing)+(issues.Count==0?"；无可核验来源":"；"+String.Join("；",issues.Distinct().Take(2))));
        }
        /// <summary>先复用Steam已缓存的原图；按App ID目录查找，不按相似名称串素材。</summary>
        private void LocalSteamImages(ulong id,Func<List<MediaRecord>> existing,Action<MediaRecord> publish,CancellationToken token)
        {
            string steam=Convert.ToString(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null));if(String.IsNullOrEmpty(steam))return;
            string directory=Path.Combine(steam,"appcache","librarycache",id.ToString());if(!Directory.Exists(directory))return;
            var folders=new[]{directory}.Concat(Directory.GetDirectories(directory).Where(d=>(File.GetAttributes(d)&FileAttributes.ReparsePoint)==0)).ToArray();
            foreach(string folder in folders)foreach(string name in new[]{"library_hero_2x.jpg","logo_2x.png","logo.png","library_hero.jpg"}){
                string path=Path.Combine(folder,name);if(!File.Exists(path)||existing().Any(f=>f.path==path))continue;token.ThrowIfCancellationRequested();
                try{var record=MediaFiles.Probe(path,name.StartsWith("logo",StringComparison.Ordinal)?"logo":"background",tools,token);record.url=path;record.attribution="Steam本地素材缓存 · App "+id;publish(record);}catch(OperationCanceledException){throw;}catch{}
            }
        }
        /// <summary>优先元数据2x地址，其次元数据中的版本目录，最后官方固定2x命名；不请求普通Hero。</summary>
        private IEnumerable<string> ImageSources(object app,ulong id,string name)
        {
            string field=name.StartsWith("logo",StringComparison.Ordinal)?"logo":"library_hero";
            var assets=MediaFiles.Get(app,"library_assets");string direct=MediaFiles.Text(assets,Path.GetFileNameWithoutExtension(name));if(String.IsNullOrEmpty(direct))direct=MediaFiles.Text(MediaFiles.Get(assets,field),name.Contains("2x")?"image2x":"image");
            var urls=new List<string>();Uri uri;if(Uri.TryCreate(direct,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase))urls.Add(direct);
            if(Uri.TryCreate(MediaFiles.Text(app,"header_image"),UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase))urls.Add(new Uri(uri,name).AbsoluteUri);
            urls.Add("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/"+id+"/"+name);return urls.Distinct();
        }
        /// <summary>从官方页面取得频道归属后再比较检索结果，模糊名称匹配不得自动采用。</summary>
        private void OfficialYoutube(object app,Func<List<MediaRecord>> existing,Action<MediaRecord> publish,CancellationToken token)
        {
            string content=MediaFiles.Text(app,"detailed_description"),website=MediaFiles.Text(app,"website");
            if(website.StartsWith("https://",StringComparison.OrdinalIgnoreCase))try{content+=" "+Encoding.UTF8.GetString(Fetch(website,token,2*1024*1024));}catch(OperationCanceledException){throw;}catch{}
            content=WebUtility.HtmlDecode(content).Replace("\\/","/");
            var links=Regex.Matches(content,@"https://(?:www\.)?(?:youtube\.com/(?:watch\?v=|channel/|user/|c/|@)[A-Za-z0-9_?=&/-]+|youtu\.be/[A-Za-z0-9_-]+)").Cast<Match>().Select(m=>m.Value).Distinct().Take(3);
            var channels=new HashSet<string>(StringComparer.Ordinal);foreach(string link in links){try{var info=Metadata(link,token);string channel=MediaFiles.Text(info,"channel_id");if(!String.IsNullOrEmpty(channel))channels.Add(channel);}catch(OperationCanceledException){throw;}catch{/* 单个失效官网链接不能阻止其他已核验频道。 */}}
            if(channels.Count==0)throw new IOException("官网未提供可核验频道，保留缺失状态");
            string title=MediaFiles.Text(app,"name");
            // 音乐优先补齐，避免先等待大型预告下载导致悬停始终没有主题曲。
            foreach(string kind in new[]{"music","video"}){
                if(existing().Any(f=>f.kind==kind&&f.status=="accepted"&&(kind=="music"||f.height>=2160)))continue;
                var search=Metadata("ytsearch5:"+title+(kind=="video"?" official trailer":" main theme official soundtrack"),token);
                foreach(var hit in MediaFiles.Items(MediaFiles.Get(search,"entries")).OrderByDescending(h=>MediaFiles.Items(MediaFiles.Get(h,"formats")).Select(f=>MediaFiles.Number(f,"height")).Where(v=>v<=2160).DefaultIfEmpty(0).Max()).ThenByDescending(h=>LanguageScore(DetectLanguage(MediaFiles.Text(h,"title")),language))){
                    string name=MediaFiles.Text(hit,"title");if(!channels.Contains(MediaFiles.Text(hit,"channel_id"))||!Normalize(name).Contains(Normalize(title))||Regex.IsMatch(name,"upscal|remaster|gameplay|walkthrough|review|reaction|直播|攻略|实况",RegexOptions.IgnoreCase))continue;
                    if(kind=="music"&&!Regex.IsMatch(name,"main theme|main menu|title theme|主题曲|主菜单|メインテーマ",RegexOptions.IgnoreCase))continue;
                    if(kind=="video"&&!Regex.IsMatch(name,"trailer|teaser|预告",RegexOptions.IgnoreCase))continue;
                    if(MediaFiles.Number(hit,"duration")>600)continue;
                    string url=MediaFiles.Text(hit,"webpage_url");if(!url.StartsWith("https://www.youtube.com/watch?",StringComparison.OrdinalIgnoreCase))continue;
                    if(kind=="video"){
                        long height=MediaFiles.Items(MediaFiles.Get(hit,"formats")).Select(f=>MediaFiles.Number(f,"height")).Where(h=>h<=2160).DefaultIfEmpty(0).Max();
                        if(height<1080||existing().Any(f=>f.kind=="video"&&f.status=="accepted"&&f.height>=height))continue;publish(Video(url,"官网关联频道 · "+MediaFiles.Text(hit,"channel")+" · "+name,token));
                    }else DownloadMusic(url,"官网关联频道 · "+MediaFiles.Text(hit,"channel")+" · "+name,publish,token);
                    break;
                }
            }
        }
        /// <summary>名称规范化只辅助同频道、同作品核验，不替代来源归属。</summary>
        private static string Normalize(string value){return Regex.Replace(value??"",@"[^\p{L}\p{N}]","").ToLowerInvariant();}
        /// <summary>专属音乐单独下载官方原声；优先AAC原音频，绝不从预告提取。</summary>
        private void DownloadMusic(string url,string attribution,Action<MediaRecord> publish,CancellationToken token)
        {
            Space();var directory=Path.Combine(cache,"work-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try{var args=Arguments();args.AddRange(new[]{"-f","bestaudio[ext=m4a]","--max-filesize","100M","-o",Path.Combine(directory,"music.m4a"),url});MediaFiles.Run(Path.Combine(tools,"yt-dlp.exe"),args,token,300000);string source=Path.Combine(directory,"music.m4a");var record=MediaFiles.Probe(source,"music",tools,token);string destination=Path.Combine(cache,UrlKey(url)+".m4a");if(!File.Exists(destination))File.Move(source,destination);record.path=destination;record.url=url;record.attribution=attribution;record.checkedTicks=DateTime.UtcNow.Ticks;publish(record);}finally{CleanWork(directory);}
        }
    }
}
