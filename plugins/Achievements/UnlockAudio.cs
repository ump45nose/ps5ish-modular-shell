using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using ModularShell;

namespace AchievementAdapter
{
    /// <summary>仅读取自身声音所需的主题字段，不读 PA 私有账户和缓存。</summary>
    [DataContract] internal sealed class UnlockSoundSettings
    {
        [DataMember] public bool trophySounds=true;
        [DataMember] public string trophySound;
        [DataMember] public double trophyVolume=.65;
        /// <summary>旧 JSON 缺字段时采用默认声音策略。</summary>
        [OnDeserializing] private void Defaults(StreamingContext c){trophySounds=true;trophyVolume=.65;}
    }
    /// <summary>纯事件过滤与有界去重，可独立验证历史导入和重复事件。</summary>
    public sealed class UnlockGate
    {
        private readonly HashSet<string> seen=new HashSet<string>();
        private readonly Queue<string> order=new Queue<string>();
        /// <summary>只接受当前新解锁；历史时间、好友、测试和进度通知不发声。</summary>
        public bool Accept(object args,DateTime now)
        {
            if(args==null)return false;
            foreach(string flag in new[]{"IsPreview","IsTestFire","IsFriendUnlock","IsProgressUpdate"})if(Object.Equals(TrophySnapshot.Property(args,flag),true))return false;
            object time=TrophySnapshot.Property(args,"UnlockTimeUtc");DateTime parsed;
            // DateTime 转文字会丢失 UTC Kind，必须保留类型，避免东八区把刚解锁误判为历史。
            if(time is DateTime)parsed=(DateTime)time;
            else if(time is DateTimeOffset)parsed=((DateTimeOffset)time).UtcDateTime;
            else DateTime.TryParse(Convert.ToString(time),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out parsed);
            if(parsed.Kind==DateTimeKind.Unspecified)parsed=DateTime.SpecifyKind(parsed,DateTimeKind.Utc);
            if(time!=null&&parsed.Year>1970&&(now-parsed.ToUniversalTime()).TotalMinutes>10)return false;
            string game=Convert.ToString(TrophySnapshot.Property(args,"PlayniteGameId"));string api=Convert.ToString(TrophySnapshot.Property(args,"ApiName"));
            bool complete=Object.Equals(TrophySnapshot.Property(args,"IsGameCompleted"),true);
            if(String.IsNullOrWhiteSpace(game)||String.IsNullOrWhiteSpace(api)&&!complete)return false;
            string key=game+"|"+Convert.ToString(TrophySnapshot.Property(args,"ProviderKey"))+"|"+(complete?"__completed":api)+"|"+Convert.ToString(time);
            if(!seen.Add(key))return false;order.Enqueue(key);while(order.Count>2048)seen.Remove(order.Dequeue());return true;
        }
    }
    /// <summary>反射订阅公开 PA 事件；类型和必要参数不匹配时停用声音而非影响游戏库。</summary>
    internal sealed class UnlockAudio : IDisposable
    {
        private static UnlockAudio currentOwner;
        private readonly ModuleContext context;
        private readonly object sync=new object();
        private readonly UnlockGate gate=new UnlockGate();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private EventInfo eventInfo;
        private Delegate handler;
        private Timer timer;
        private bool disposed,pending;
        public string Diagnostic{get;private set;}
        /// <summary>只订阅已由 Playnite 加载的程序集，不自行加载 PA DLL。</summary>
        public UnlockAudio(ModuleContext context)
        {
            this.context=context;Diagnostic="解锁音效未连接";
            if(context==null||context.NotificationAudio==null)return;
            var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="PlayniteAchievements");if(assembly==null)return;
            // 3.x 公共事件签名为版本适配边界；未来主版本不得静默沿用。
            string manifest=Path.Combine(Path.GetDirectoryName(assembly.Location),"extension.yaml");
            if(!File.Exists(manifest)||!System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(manifest),@"(?m)^Version:\s*3\.1\.3\s*$")){Diagnostic="PA 版本未适配，解锁声音停用";return;}
            var type=assembly.GetType("PlayniteAchievements.PlayniteAchievementsPlugin");eventInfo=type==null?null:type.GetEvent("AchievementUnlocked",BindingFlags.Public|BindingFlags.Static);if(eventInfo==null)return;
            var invoke=eventInfo.EventHandlerType.GetMethod("Invoke");var parameters=invoke.GetParameters();
            if(parameters.Length!=2||parameters[1].ParameterType.GetProperty("PlayniteGameId")==null||parameters[1].ParameterType.GetProperty("ApiName")==null)return;
            var sender=Expression.Parameter(parameters[0].ParameterType,"sender");var args=Expression.Parameter(parameters[1].ParameterType,"args");
            handler=Expression.Lambda(eventInfo.EventHandlerType,Expression.Call(Expression.Constant(this),typeof(UnlockAudio).GetMethod("Receive",BindingFlags.NonPublic|BindingFlags.Instance),Expression.Convert(args,typeof(object))),sender,args).Compile();
            // 全屏视图重建时移交声音所有权，防止同一静态事件有多个播放者。
            if(currentOwner!=null)currentOwner.Dispose();currentOwner=this;
            eventInfo.AddEventHandler(null,handler);Diagnostic="PA 弹窗；Shell 奖杯声音（PA 3.1.3）";Log(Diagnostic);
        }
        /// <summary>同一波次延迟合并，避免成就及随后完成事件重复播放。</summary>
        private void Receive(object args)
        {
            lock(sync){if(disposed||!gate.Accept(args,DateTime.UtcNow))return;pending=true;if(timer==null)timer=new Timer(Play,null,700,Timeout.Infinite);else timer.Change(700,Timeout.Infinite);}
        }
        /// <summary>每一波只播放一次；配置每次重读以支持保存后立即生效。</summary>
        private async void Play(object state)
        {
            lock(sync){if(disposed||!pending)return;pending=false;}
            try{
                var config=new UnlockSoundSettings();string path=Path.Combine(context.UserDataRoot,"theme-experience.json");if(File.Exists(path))using(var file=File.OpenRead(path))config=(UnlockSoundSettings)new DataContractJsonSerializer(typeof(UnlockSoundSettings)).ReadObject(file);
                if(!config.trophySounds)return;
                // 已安装其他声音所有者时避免叠加，不改变其设置。
                if(AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="UniPlaySong")){Log("检测到 UniPlaySong，Shell 奖杯声音让出所有权");return;}
                await context.NotificationAudio.PlayAsync(config.trophySound,Math.Max(0,Math.Min(1,config.trophyVolume)),lifetime.Token);Log("已播放解锁波次音效");
            }catch(OperationCanceledException){}catch(Exception ex){Log("声音不可用："+ex.GetType().Name);}
        }
        /// <summary>只记录事件处理状态，不记录成就内容或账户。</summary>
        private void Log(string text){try{Directory.CreateDirectory(context.UserDataRoot);File.AppendAllText(Path.Combine(context.UserDataRoot,"trophy-audio.log"),DateTime.UtcNow.ToString("o")+" "+text+Environment.NewLine);}catch{}}
        /// <summary>取消排队声音并撤销相同委托，允许插件完全移除。</summary>
        public void Dispose(){lock(sync){if(disposed)return;disposed=true;if(timer!=null)timer.Dispose();lifetime.Cancel();if(eventInfo!=null&&handler!=null)eventInfo.RemoveEventHandler(null,handler);}lifetime.Dispose();}
    }
}
