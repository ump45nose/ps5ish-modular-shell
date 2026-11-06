using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModularShell;
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("Shell.AutomaticAssetsTests")]

namespace LocalAssets
{
    /// <summary>自动目录与人工索引独立保存；状态只由本服务的锁内单写入者提交。</summary>
    public sealed class AutomaticState
    {
        public int schema=1;
        public bool enabled=true;
        public string preferredLanguage="zh-Hans";
        public List<AutomaticGame> games=new List<AutomaticGame>();
    }
    /// <summary>完整游戏身份、任务结果及已实测文件，不保存账号或访问凭据。</summary>
    public sealed class AutomaticGame
    {
        public string key,title,status;
        public bool locked;
        public long attempted;
        public long retryAfter;
        public int failures;
        public List<MediaRecord> files=new List<MediaRecord>();
    }
    /// <summary>两个后台工作者，共享去重队列；每个已验收文件立即通知宿主。</summary>
    public sealed partial class Module : IAutomaticAssets, IAssetLanguageSettings
    {
        private AutomaticState automatic;
        private readonly List<GameItem> pending=new List<GameItem>();
        private readonly HashSet<string> active=new HashSet<string>();
        private readonly Dictionary<string,CancellationTokenSource> jobs=new Dictionary<string,CancellationTokenSource>();
        private readonly HashSet<string> preempted=new HashSet<string>();
        private readonly HashSet<string> forced=new HashSet<string>();
        private readonly Dictionary<string,GameItem> knownItems=new Dictionary<string,GameItem>();
        private string foregroundKey;
        private readonly CancellationTokenSource shutdown=new CancellationTokenSource();
        private bool paused,workersStarted,diskBlocked;
        private string automaticError;
        private readonly Action<GameItem,CancellationToken> acquisitionOverride;
        /// <summary>正式运行使用固定的网络与探测服务。</summary>
        public Module(){ }
        /// <summary>离线回归注入可取消工作，不向正式共享契约暴露测试开关。</summary>
        internal Module(Action<GameItem,CancellationToken> acquisition){acquisitionOverride=acquisition;}
        public event EventHandler<AssetChangedEventArgs> AssetChanged;
        private string Tools {get{var location=Path.GetDirectoryName(typeof(Module).Assembly.Location);var bundled=Path.Combine(location,"tools");return Directory.Exists(bundled)?bundled:Path.Combine(location,"plugins","LocalAssets","tools");}}
        private string StatePath {get{return Path.Combine(root,"automatic-assets","state-v1.json");}}
        private string CacheRoot {get{return Path.Combine(root,"automatic-assets","files");}}
        public bool Enabled {get{lock(gate){EnsureAutomatic();return automatic.enabled;}}}
        public string PreferredLanguage {get{lock(gate){EnsureAutomatic();return automatic.preferredLanguage??"zh-Hans";}}}
        /// <summary>语言使用白名单；重新排队但不删除原预告，不覆盖人工锁定。</summary>
        public void SetPreferredLanguage(string language)
        {
            if(!new[]{"zh-Hans","zh-Hant","en","ja","de","fr","auto"}.Contains(language))throw new ArgumentException("不支持的预告语言");
            lock(gate){EnsureAutomatic();automatic.preferredLanguage=language;foreach(var state in automatic.games){state.attempted=0;state.retryAfter=0;}foreach(var job in jobs){preempted.Add(job.Key);forced.Add(job.Key);job.Value.Cancel();}SaveAutomatic();foreach(var item in knownItems.Values.ToList())Enqueue(item,false,true);}
        }

        /// <summary>用长度前缀消除分隔符碰撞，缓存文件名另用SHA256生成。</summary>
        private static string Key(GameItem item){return String.Join("",new[]{item.Provider,item.SourceInstance,item.ItemId}.Select(v=>(v??"").Length+":"+(v??"")));}
        /// <summary>只加载JSON，不在插件初始化的两秒时限内联网或探测整库。</summary>
        private void EnsureAutomatic()
        {
            if(automatic!=null)return;
            try{automatic=MediaFiles.Read(StatePath,new AutomaticState());if(automatic.schema!=1||automatic.games==null)throw new InvalidDataException("自动素材清单版本不支持");}
            catch(Exception ex){automatic=new AutomaticState {enabled=false};automaticError=ex.Message;}
            foreach(var game in automatic.games){if(game.files==null)game.files=new List<MediaRecord>();if(game.status=="获取中"){game.status="等待恢复";game.attempted=0;}if(game.retryAfter==0&&game.status!=null&&game.status.StartsWith("待补齐：",StringComparison.Ordinal))game.retryAfter=DateTime.UtcNow.Ticks;}
        }
        /// <summary>在同一锁中保存，避免两个下载工作者相互覆盖清单。</summary>
        private void SaveAutomatic(){if(!disposed)MediaFiles.Save(StatePath,automatic);}
        /// <summary>旧人工锁定与新自动锁定同时有效，不能用自动设置绕开旧索引。</summary>
        private bool Locked(GameItem item){return State(item).locked||manualLocks.Contains(Tuple.Create(item.Provider,item.SourceInstance,item.ItemId));}
        /// <summary>只按完整身份查找；缺项创建空状态，不伪造已获取素材。</summary>
        private AutomaticGame State(GameItem item)
        {
            EnsureAutomatic();var key=Key(item);var value=automatic.games.FirstOrDefault(g=>g.key==key);
            if(value==null){value=new AutomaticGame {key=key,title=item.Title,status="尚未获取"};automatic.games.Add(value);}return value;
        }
        /// <summary>开关保留已有缓存；关闭会取消当前网络进程和排队请求。</summary>
        public void SetEnabled(bool enabled){lock(gate){EnsureAutomatic();automatic.enabled=enabled;if(enabled)diskBlocked=false;else{pending.Clear();foreach(var job in jobs.Values)job.Cancel();}SaveAutomatic();}}
        /// <summary>库更新仅排队已安装游戏，近期失败一天后重试；当前选择可提前请求。</summary>
        public void Synchronize(IList<GameItem> catalog)
        {
            if(catalog==null)return;
            lock(gate){EnsureAutomatic();foreach(var item in catalog.Where(i=>i.IsInstalled).OrderByDescending(i=>i.LastActivity)){knownItems[Key(item)]=item;Enqueue(item,false,false);}}
        }
        /// <summary>当前游戏移至队首，同游戏运行中不会创建重复下载。</summary>
        public void Request(GameItem item,bool force){lock(gate){EnsureAutomatic();if(item!=null){foregroundKey=Key(item);knownItems[foregroundKey]=item;}Enqueue(item,true,force);}}
        /// <summary>两个长期后台任务等待队列，不为每次选择创建下载进程。</summary>
        private void Enqueue(GameItem item,bool priority,bool force)
        {
            if(disposed||!automatic.enabled||item==null||item.Provider!="playnite")return;
            var key=Key(item);var state=State(item);if(active.Contains(key))return;
            if(force)forced.Add(key);
            var existing=pending.FindIndex(i=>Key(i)==key);if(existing>=0){if(priority){pending.RemoveAt(existing);pending.Insert(0,item);Preempt();}return;}
            if(!force&&state.attempted>0&&DateTime.UtcNow.Ticks-state.attempted<TimeSpan.FromDays(1).Ticks&&(state.retryAfter==0||state.retryAfter>DateTime.UtcNow.Ticks)&&!NeedsAudit(item))return;
            if(priority)pending.Insert(0,item);else pending.Add(item);state.status="排队中";
            if(priority)Preempt();
            if(!workersStarted){workersStarted=true;Task.Run((Action)Worker);Task.Run((Action)Worker);}
        }
        /// <summary>当前任务取得一个下载槽；被打断的后台游戏回到队尾，不丢已验收文件。</summary>
        private void Preempt(){if(active.Count>=2){var victim=jobs.FirstOrDefault(j=>j.Key!=foregroundKey&&!preempted.Contains(j.Key));if(victim.Value!=null){preempted.Add(victim.Key);victim.Value.Cancel();}}}
        /// <summary>游戏运行时取消下载并重新排队，避免隐藏的外部进程继续争用磁盘。</summary>
        public void SetPaused(bool value){lock(gate){paused=value;if(value)foreach(var job in jobs.Values)job.Cancel();}}
        /// <summary>下载的最终提交再次检查锁定状态，用户中途锁定也不会被覆盖。</summary>
        private void Worker()
        {
            while(!shutdown.IsCancellationRequested){
                GameItem item=null;CancellationTokenSource job=null;
                lock(gate){if(!disposed&&!paused&&!diskBlocked&&automatic.enabled){
                    // 断网与限流使用持久化退避，恢复联网后无需用户逐游戏重试。
                    if(pending.Count==0)foreach(var retry in knownItems.Values.Where(i=>i.IsInstalled||Key(i)==foregroundKey).ToArray()){var state=State(retry);if(state.retryAfter>0&&state.retryAfter<=DateTime.UtcNow.Ticks)Enqueue(retry,false,false);}
                    if(pending.Count>0){item=pending[0];pending.RemoveAt(0);active.Add(Key(item));job=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);jobs[Key(item)]=job;State(item).status="获取中";}
                }}
                if(item==null){shutdown.Token.WaitHandle.WaitOne(200);continue;}
                try{
                    if(acquisitionOverride!=null)acquisitionOverride(item,job.Token);else{
                    ValidateTools(job.Token);
                    AuditLegacy(item,job.Token);
                    bool locked;lock(gate){locked=Locked(item);}
                    bool force;lock(gate){force=forced.Remove(Key(item));}
                    if(!locked)new SteamAssets(Tools,CacheRoot,PreferredLanguage).Acquire(item,()=>Existing(item),record=>Commit(item,record),job.Token,force);
                    }
                    lock(gate){var state=State(item);state.status="已检查 · "+state.files.Count(f=>f.status=="accepted")+" 项合格素材";state.attempted=DateTime.UtcNow.Ticks;state.retryAfter=0;state.failures=0;SaveAutomatic();}
                }catch(OperationCanceledException){lock(gate){State(item).status="已暂停";bool interrupted=preempted.Remove(Key(item));if((paused||interrupted)&&!disposed&&automatic.enabled)pending.Add(item);}}
                catch(DiskSpaceException ex){lock(gate){diskBlocked=true;automaticError=ex.Message;State(item).status=ex.Message;foreach(var other in jobs.Values)other.Cancel();try{SaveAutomatic();}catch{}}}
                catch(Exception ex){lock(gate){var state=State(item);state.status="待补齐："+ex.Message;state.attempted=DateTime.UtcNow.Ticks;state.failures=Math.Min(10,state.failures+1);bool transient=ex is System.Net.WebException||ex is TimeoutException;state.retryAfter=DateTime.UtcNow.AddSeconds(transient?Math.Min(3600,60*Math.Pow(2,state.failures-1)):86400).Ticks;try{SaveAutomatic();}catch{}}}
                finally{lock(gate){active.Remove(Key(item));jobs.Remove(Key(item));}job.Dispose();}
            }
        }
        private bool toolsValidated;
        /// <summary>每次进程生命周期核验固定依赖散列，拒绝悄悄调用开发机PATH工具。</summary>
        private void ValidateTools(CancellationToken token)
        {
            lock(gate){if(toolsValidated)return;}
            var manifest=MediaFiles.Json().DeserializeObject(File.ReadAllText(Path.Combine(Tools,"media-tools.lock.json")));
            foreach(var tool in MediaFiles.Items(MediaFiles.Get(manifest,"tools"))){token.ThrowIfCancellationRequested();string name=MediaFiles.Text(tool,"name");if(Path.GetFileName(name)!=name)throw new InvalidDataException("工具清单路径非法");if(!String.Equals(MediaFiles.Hash(Path.Combine(Tools,name)),MediaFiles.Text(tool,"sha256"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("素材工具散列不匹配："+name);}
            lock(gate){toolsValidated=true;}
        }
        /// <summary>返回快照供源选择器判断缺项，不把内部可变集合交给工作线程。</summary>
        private List<MediaRecord> Existing(GameItem item){lock(gate)return State(item).files.ToList();}
        /// <summary>人工索引的新路径或原文件变化不受联网重试间隔限制。</summary>
        private bool NeedsAudit(GameItem item)
        {
            PreviewAsset legacy;entries.TryGetValue(Tuple.Create(item.Provider,item.SourceInstance,item.ItemId),out legacy);
            var paths=new[]{item.BackgroundPath}.Concat(legacy==null?new string[0]:new[]{legacy.VideoPath,legacy.MusicPath,legacy.LogoPath}.Concat(legacy.BackgroundPaths??new string[0]));
            return paths.Where(p=>!String.IsNullOrWhiteSpace(p)&&File.Exists(p)).Any(p=>{var file=new FileInfo(p);return !State(item).files.Any(f=>f.path==file.FullName&&f.bytes==file.Length&&f.checkedTicks>=file.LastWriteTimeUtc.Ticks);});
        }
        /// <summary>旧源只探测一次；原文件永不搬移，低清记录用于界面解释和升级。</summary>
        private void AuditLegacy(GameItem item,CancellationToken token)
        {
            PreviewAsset legacy;lock(gate){entries.TryGetValue(Tuple.Create(item.Provider,item.SourceInstance,item.ItemId),out legacy);}
            var candidates=new List<Tuple<string,string>>();if(legacy!=null){foreach(var path in legacy.BackgroundPaths??new string[0])candidates.Add(Tuple.Create(path,"background"));candidates.Add(Tuple.Create(legacy.VideoPath,"video"));candidates.Add(Tuple.Create(legacy.LogoPath,"logo"));candidates.Add(Tuple.Create(legacy.MusicPath,"music"));}candidates.Add(Tuple.Create(item.BackgroundPath,"background"));
            foreach(var candidate in candidates){token.ThrowIfCancellationRequested();if(String.IsNullOrWhiteSpace(candidate.Item1)||!File.Exists(candidate.Item1))continue;
                var file=new FileInfo(candidate.Item1);if(Existing(item).Any(f=>f.path==file.FullName&&f.bytes==file.Length&&f.checkedTicks>=file.LastWriteTimeUtc.Ticks))continue;
                try{var record=MediaFiles.Probe(file.FullName,candidate.Item2,Tools,token);record.url=file.FullName;record.attribution="用户本地素材";Commit(item,record,true);}catch(OperationCanceledException){throw;}catch(Exception ex){Commit(item,new MediaRecord {path=file.FullName,kind=candidate.Item2,status="invalid_file",attribution=ex.Message,bytes=file.Length,checkedTicks=DateTime.UtcNow.Ticks},true);}
            }
        }
        /// <summary>只接纳完成探测的文件，图片事件不会要求播放器重启音乐。</summary>
        private void Commit(GameItem item,MediaRecord record,bool auditing=false)
        {
            EventHandler<AssetChangedEventArgs> changed;
            lock(gate){if(disposed)return;var state=State(item);if(Locked(item)&&!auditing)return;state.files.RemoveAll(f=>String.Equals(f.path,record.path,StringComparison.OrdinalIgnoreCase));state.files.Add(record);
                // 最多五张去重正式背景，其余保留文件和来源供回退，不继续解码轮播。
                var backgrounds=state.files.Where(f=>f.kind=="background"&&(f.status=="accepted"||f.status=="reserve")).OrderByDescending(f=>f.Rank).GroupBy(f=>f.sha256??f.path).Select(g=>g.First()).Take(5).ToList();
                foreach(var image in state.files.Where(f=>f.kind=="background"&&(f.status=="accepted"||f.status=="reserve")))image.status=backgrounds.Contains(image)?"accepted":"reserve";
                SaveAutomatic();changed=AssetChanged;}
            if(changed!=null)changed(this,new AssetChangedEventArgs {Item=item,ImagesChanged=record.kind=="background"||record.kind=="logo",PlaybackChanged=record.kind=="video"||record.kind=="music"});
        }
        /// <summary>正式候选必须仍存在且未被外部修改；已验收缓存无需等待网络。</summary>
        private static bool Usable(MediaRecord file){try{var info=new FileInfo(file.path);return file.status=="accepted"&&info.Exists&&info.Length==file.bytes&&info.LastWriteTimeUtc.Ticks<=file.checkedTicks;}catch{return false;}}
        /// <summary>锁定与人工优先，但任何游戏背景或视频都不能绕过质量探测。</summary>
        private PreviewAsset Merge(GameItem item,PreviewAsset legacy)
        {
            if(item.Provider!="playnite")return legacy;
            var state=State(item);var files=state.files.Where(Usable).ToList();
            var manualPaths=new[]{item.BackgroundPath}.Concat(legacy==null?new string[0]:new[]{legacy.MusicPath,legacy.VideoPath,legacy.LogoPath}.Concat(legacy.BackgroundPaths??new string[0])).ToArray();
            files=files.Where(f=>f.attribution!="用户本地素材"||manualPaths.Contains(f.path)).ToList();
            // 人工锁定仅允许人工索引中的路径，不能偷偷用旧自动缓存顶替低清锁定项。
            if(manualLocks.Contains(Tuple.Create(item.Provider,item.SourceInstance,item.ItemId))&&legacy!=null){var paths=new[]{legacy.MusicPath,legacy.VideoPath,legacy.LogoPath}.Concat(legacy.BackgroundPaths??new string[0]).ToArray();files=files.Where(f=>paths.Contains(f.path)).ToList();}
            Func<string,string,string> choose=(kind,manual)=>files.Any(f=>f.kind==kind&&f.path==manual)?manual:files.Where(f=>f.kind==kind).OrderByDescending(f=>f.Rank).ThenByDescending(f=>SteamAssets.LanguageScore(f.language,PreferredLanguage)).Select(f=>f.path).FirstOrDefault();
            return new PreviewAsset {MusicPath=choose("music",legacy==null?null:legacy.MusicPath),VideoPath=choose("video",legacy==null?null:legacy.VideoPath),LogoPath=choose("logo",legacy==null?null:legacy.LogoPath),BackgroundPaths=files.Where(f=>f.kind=="background").OrderByDescending(f=>f.Rank).GroupBy(f=>f.sha256??f.path).Select(g=>g.First().path).Take(5).ToArray(),MusicVolume=legacy==null?.35:legacy.MusicVolume,VideoVolume=legacy==null?.5:legacy.VideoVolume,TrailerAudio=legacy!=null&&legacy.TrailerAudio};
        }
        /// <summary>管理页同时说明缺失原因与实际尺寸，零项不冒充下载完成。</summary>
        public string Describe(GameItem item)
        {
            lock(gate){EnsureAutomatic();if(item==null)return "队列："+pending.Count+" · 运行："+active.Count+"\n"+automaticError;var state=State(item);return (Locked(item)?"已锁定 · ":"")+state.status+"\n队列："+pending.Count+" · 运行："+active.Count+"\n"+String.Join("\n",state.files.Select(f=>f.kind+" "+f.width+"×"+f.height+" "+f.status+(Locked(item)&&f.status!="accepted"?"（锁定素材不达标）":"")+"\n"+f.attribution+"\n"+f.url));}
        }
        /// <summary>用户主动操作同步人工与自动锁定；只修改匹配条目的locked字段并保留原索引备份。</summary>
        public void SetLocked(GameItem item,bool locked)
        {
            if(item==null)return;lock(gate){
                string path=Path.Combine(root,"assets.json");if(File.Exists(path)){
                    var index=MediaFiles.Json().DeserializeObject(File.ReadAllText(path));bool changed=false;
                    foreach(var entry in MediaFiles.Items(MediaFiles.Get(index,"items")))if(MediaFiles.Text(entry,"provider")==item.Provider&&MediaFiles.Text(entry,"sourceInstance")==item.SourceInstance&&MediaFiles.Text(entry,"itemId")==item.ItemId){((Dictionary<string,object>)entry)["locked"]=locked;changed=true;}
                    if(changed){MediaFiles.Save(path,index);Initialize();}
                }
                State(item).locked=locked;if(locked){pending.RemoveAll(i=>Key(i)==Key(item));CancellationTokenSource job;if(jobs.TryGetValue(Key(item),out job))job.Cancel();}SaveAutomatic();
            }
        }
        /// <summary>仅删除清单登记的自动文件，不跟随链接、不删除锁定或其他游戏正在引用的文件。</summary>
        public void ClearAutomatic(GameItem item)
        {
            if(item==null)return;
            lock(gate){var state=State(item);if(Locked(item)||active.Contains(Key(item)))return;
                string boundary=Path.GetFullPath(CacheRoot)+Path.DirectorySeparatorChar;
                foreach(var file in state.files.ToArray()){
                    if(String.IsNullOrEmpty(file.path)||!Path.GetFullPath(file.path).StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(Path.GetFullPath(file.path))!=Path.GetFullPath(CacheRoot))continue;
                    if(automatic.games.Any(g=>g!=state&&g.files.Any(f=>String.Equals(f.path,file.path,StringComparison.OrdinalIgnoreCase)))){state.files.Remove(file);continue;}
                    try{if(File.Exists(file.path)){
                        if((File.GetAttributes(file.path)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(CacheRoot)&FileAttributes.ReparsePoint)!=0)continue;
                        // 独占探测失败时保留条目，避免删除另一个播放会话正在使用的文件。
                        using(File.Open(file.path,FileMode.Open,FileAccess.Read,FileShare.None)){}File.Delete(file.path);
                    }state.files.Remove(file);}catch(IOException){}catch(UnauthorizedAccessException){}
                }
                state.attempted=0;state.status="已清理可释放的自动缓存，锁定及占用文件保留";SaveAutomatic();
            }
        }
    }
}
