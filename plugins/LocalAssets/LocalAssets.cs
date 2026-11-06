using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using ModularShell;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAssets
{
    [DataContract] public sealed class Config
    {
        [DataMember] public int schema;
        [DataMember] public List<Entry> items;
    }
    [DataContract] public sealed class Entry
    {
        [DataMember] public string provider, sourceInstance, itemId, music, video;
        [DataMember] public string logo;
        [DataMember] public string[] backgrounds;
        [DataMember] public double volume, videoVolume;
        [DataMember] public bool trailerAudio;
        [DataMember] public bool locked;
        /// <summary>省略音量时保留产品默认；明确的零音量保持静音。</summary>
        [OnDeserializing] private void Defaults(StreamingContext context) { volume = .35; videoVolume = .5; }
    }
    /// <summary>独立本地人工素材提供者；只读索引，不复制或删除用户文件。</summary>
    public sealed partial class Module : IModule, IConfiguredModule, IAssetProvider, IAsyncAssetProvider
    {
        private string root;
        private DateTime indexTime;
        private bool disposed;
        private readonly object gate=new object();
        private readonly Dictionary<Tuple<string,string,string>, PreviewAsset> entries = new Dictionary<Tuple<string,string,string>, PreviewAsset>();
        private readonly HashSet<Tuple<string,string,string>> manualLocks=new HashSet<Tuple<string,string,string>>();
        public string Diagnostic { get; private set; }
        /// <summary>接收 SDK 用户数据目录，保持已有 assets.json 路径兼容。</summary>
        public void Configure(ModuleContext context) { root = context.UserDataRoot; }
        /// <summary>后台读取一次索引，先完整校验再提交，损坏配置回退为空素材。</summary>
        public void Initialize()
        {
            lock(gate) {
            if(disposed)return;
            try
            {
                string path = Path.Combine(root, "assets.json");
                // 删除索引代表撤销关联，不能继续使用旧素材。
                if (!File.Exists(path)) {entries.Clear();manualLocks.Clear();indexTime=File.GetLastWriteTimeUtc(path);return;}
                Config config;
                using (var file = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path)))) config = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(file);
                if (config == null || config.schema != 1 || config.items == null) throw new InvalidDataException("素材配置版本不支持");
                var validated = new Dictionary<Tuple<string,string,string>, PreviewAsset>();
                foreach (var entry in config.items)
                {
                    if (entry == null || String.IsNullOrWhiteSpace(entry.provider) || String.IsNullOrWhiteSpace(entry.sourceInstance) || String.IsNullOrWhiteSpace(entry.itemId)) throw new InvalidDataException("素材内容引用不完整");
                    if (!VolumeValid(entry.volume) || !VolumeValid(entry.videoVolume)) throw new InvalidDataException("素材音量须在 0 到 1 范围");
                    var key = Tuple.Create(entry.provider, entry.sourceInstance, entry.itemId);
                    if (validated.ContainsKey(key)) throw new InvalidDataException("素材内容引用重复");
                    validated.Add(key, new PreviewAsset { MusicPath = entry.music, VideoPath = entry.video, MusicVolume = entry.volume, VideoVolume = entry.videoVolume, TrailerAudio = entry.trailerAudio, LogoPath=entry.logo,BackgroundPaths=entry.backgrounds });
                }
                entries.Clear();foreach (var pair in validated) entries.Add(pair.Key, pair.Value);
                manualLocks.Clear();foreach(var entry in config.items)if(entry.locked)manualLocks.Add(Tuple.Create(entry.provider,entry.sourceInstance,entry.itemId));
                indexTime=File.GetLastWriteTimeUtc(path);Diagnostic=null;
            }
            catch (Exception ex) { Diagnostic = ex.Message; entries.Clear(); }
            }
        }
        /// <summary>数据层拒绝 NaN、无穷和超范围音量。</summary>
        private static bool VolumeValid(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 && value <= 1; }
        /// <summary>按命名空间查找；同名游戏和跨来源 ID 不会串素材。</summary>
        public PreviewAsset Find(GameItem item)
        {
            lock(gate){
                if (disposed || item == null) return null; PreviewAsset value;
                if(File.GetLastWriteTimeUtc(Path.Combine(root,"assets.json"))!=indexTime)Initialize();
                entries.TryGetValue(Tuple.Create(item.Provider,item.SourceInstance,item.ItemId),out value);
                return Merge(item,value);
            }
        }
        /// <summary>后台解析可取消，脚本导入新索引后不要求重启整个 Playnite。</summary>
        public Task<PreviewAsset> ResolveAsync(GameItem item,CancellationToken token)
        {
            // 缓存立即返回；只有稳定停留才把当前游戏提升到下载队首。
            QueueSelection(item,token);token.ThrowIfCancellationRequested();return Task.FromResult(Find(item));
        }
        /// <summary>选择取消只取消界面等待，已安装游戏的后台任务保持独立生命周期。</summary>
        private async void QueueSelection(GameItem item,CancellationToken token){try{await Task.Delay(200,token);Request(item,false);}catch(OperationCanceledException){}catch(Exception ex){Diagnostic=ex.Message;}}
        /// <summary>素材能力不占据首页展示分区。</summary>
        public IEnumerable<Contribution> GetSections() { yield break; }
        /// <summary>只清理内存，不删除人工资产。</summary>
        public void Dispose() { lock(gate){disposed=true;shutdown.Cancel();foreach(var job in jobs.Values)job.Cancel();entries.Clear();AssetChanged=null;} }
    }
}
