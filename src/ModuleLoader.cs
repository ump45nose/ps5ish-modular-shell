using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModularShell
{
    [DataContract]
    public sealed class Manifest
    {
        [DataMember] public string id;
        [DataMember] public int schema;
        [DataMember] public int hostContract;
        [DataMember] public string entry;
        [DataMember] public string type;
        [DataMember] public bool enabled;
        [DataMember] public string version;
        [DataMember] public string[] capabilities;
        [DataMember] public string[] dependencies;
    }

    /// <summary>可信模块的有界启动；超时隔离不等于原生代码安全沙箱。</summary>
    public sealed class ModuleLoader : IDisposable
    {
        private sealed class Candidate { public Manifest Manifest; public string Entry; public ModuleContext Context; }
        private sealed class Loaded { public IModule Module; public List<Contribution> Sections; }
        private readonly List<IModule> modules = new List<IModule>();
        private readonly Dictionary<string, object> capabilities = new Dictionary<string, object>();
        private readonly List<IHubViewProvider> hubs = new List<IHubViewProvider>();
        private readonly object gate = new object();
        private bool disposed, started;
        public readonly List<string> Diagnostics = new List<string>();
        public readonly List<Contribution> Sections = new List<Contribution>();

        /// <summary>提供启动过程中的稳定诊断快照，避免 UI 枚举后台可变列表。</summary>
        public string[] GetDiagnostics() { lock (gate) { return Diagnostics.ToArray(); } }

        /// <summary>只公开清单声明并通过类型检查的能力。</summary>
        public T GetCapability<T>() where T : class
        {
            lock (gate) { object value; return capabilities.TryGetValue(typeof(T).FullName, out value) ? value as T : null; }
        }
        /// <summary>返回所有已声明的影视服务；其他能力仍保持单提供者隔离。</summary>
        public IHubViewProvider[] GetHubProviders() { lock (gate) { return hubs.ToArray(); } }
        /// <summary>兼容无界面测试入口；界面必须使用异步入口。</summary>
        public void Load(string root, bool safe) { LoadAsync(root, safe, CancellationToken.None).GetAwaiter().GetResult(); }

        /// <summary>每个模块最多等待指定毫秒；依赖失败只禁用其消费者。</summary>
        public async Task LoadAsync(string root, bool safe, CancellationToken token, int timeoutMs = 2000, ModuleContext context = null)
        {
            lock (gate) { if (started || disposed) return; started = true; }
            if (safe || !Directory.Exists(root)) return;
            var pending = new List<Candidate>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    Manifest manifest;
                    // Windows 编辑器常写 UTF-8 BOM；先按文本读取，避免序列化器把 BOM 当 JSON 字符。
                    string json=File.ReadAllText(Path.Combine(directory,"plugin.json"));
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                        manifest = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream);
                    if (manifest == null || manifest.schema != 2 || manifest.hostContract != 1)
                        throw new InvalidDataException("需要清单 2 / 宿主契约 1");
                    Version version;
                    if (String.IsNullOrWhiteSpace(manifest.id) || !ids.Add(manifest.id) || !Version.TryParse(manifest.version, out version))
                        throw new InvalidDataException("标识重复、缺失或版本无效");
                    if (!manifest.enabled) { Record(manifest.id + "：已禁用"); continue; }
                    if (manifest.capabilities == null || manifest.dependencies == null) throw new InvalidDataException("缺少能力或依赖清单");
                    if (manifest.dependencies.Any(String.IsNullOrWhiteSpace) || manifest.dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.dependencies.Length)
                        throw new InvalidDataException("依赖标识无效或重复");
                    if (String.IsNullOrWhiteSpace(manifest.entry) || manifest.entry != Path.GetFileName(manifest.entry)
                        || manifest.entry.IndexOfAny(new[] { ':', '/', '\\' }) >= 0 || !manifest.entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("入口路径无效");
                    string entry = Path.Combine(directory, manifest.entry);
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("不支持链接包路径");
                    pending.Add(new Candidate { Manifest = manifest, Entry = entry, Context = context ?? new ModuleContext { UserDataRoot = AppDomain.CurrentDomain.BaseDirectory } });
                }
                catch (Exception ex) { Record(Path.GetFileName(directory) + "：" + ex.GetBaseException().Message); }
            }
            var ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                var candidate = pending.FirstOrDefault(c => c.Manifest.dependencies.All(ready.Contains));
                if (candidate == null) { foreach (var c in pending) Record(c.Manifest.id + "：依赖缺失、失败或成环"); break; }
                pending.Remove(candidate);
                // 初始化与贡献枚举不能占用 UI 线程；插件不得在初始化期间访问 WPF。
                var work = Task.Run(() => Initialize(candidate));
                var limit = Task.Delay(Math.Max(1, timeoutMs), token);
                if (await Task.WhenAny(work, limit).ConfigureAwait(false) != work)
                {
                    Record(candidate.Manifest.id + "：初始化超时或已取消");
                    DisposeLate(work); continue;
                }
                try
                {
                    var result = await work.ConfigureAwait(false);
                    lock (gate)
                    {
                        if (disposed || token.IsCancellationRequested) { DisposeModule(result.Module); continue; }
                        foreach (string capability in candidate.Manifest.capabilities)
                        {
                            string key = CapabilityType(capability).FullName;
                            if (capability != "hubView" && capabilities.ContainsKey(key)) throw new InvalidDataException("能力提供者冲突：" + capability);
                        }
                        foreach (string capability in candidate.Manifest.capabilities) {
                            string key = CapabilityType(capability).FullName;
                            if (capability == "hubView") hubs.Add((IHubViewProvider)result.Module);
                            if (!capabilities.ContainsKey(key)) capabilities.Add(key, result.Module);
                        }
                        modules.Add(result.Module); Sections.AddRange(result.Sections); ready.Add(candidate.Manifest.id);
                        Diagnostics.Add(candidate.Manifest.id + "：已启用 " + candidate.Manifest.version);
                    }
                }
                catch (Exception ex) { if (work.Status == TaskStatus.RanToCompletion) DisposeModule(work.Result.Module); Record(candidate.Manifest.id + "：" + ex.GetBaseException().Message); }
            }
        }
        /// <summary>固定共享能力表，插件实现不进入核心依赖。</summary>
        private static Type CapabilityType(string name)
        {
            if (name == "music") return typeof(IMusicBackend);
            if (name == "video") return typeof(IVideoBackend);
            if (name == "assets") return typeof(IAssetProvider);
            if (name == "contextSummary") return typeof(IContextSummaryProvider);
            if (name == "contextView") return typeof(IContextViewProvider);
            if (name == "hubView") return typeof(IHubViewProvider);
            if (name == "playback") return typeof(IMediaPlaybackBackend);
            throw new InvalidDataException("未知能力：" + name);
        }
        /// <summary>在后台完整校验并初始化，任何失败均释放已创建实例。</summary>
        private static Loaded Initialize(Candidate candidate)
        {
            IModule instance = null;
            try
            {
                var type = Assembly.LoadFrom(candidate.Entry).GetType(candidate.Manifest.type, true);
                if (!typeof(IModule).IsAssignableFrom(type)) throw new InvalidDataException("未实现模块契约");
                instance = (IModule)Activator.CreateInstance(type);
                var configured = instance as IConfiguredModule;
                if (configured != null) configured.Configure(candidate.Context);
                if (candidate.Manifest.capabilities.Distinct().Count() != candidate.Manifest.capabilities.Length) throw new InvalidDataException("重复能力");
                foreach (string name in candidate.Manifest.capabilities)
                    if (!CapabilityType(name).IsInstanceOfType(instance)) throw new InvalidDataException("未实现声明能力：" + name);
                instance.Initialize();
                return new Loaded { Module = instance, Sections = instance.GetSections().Where(s => s != null).ToList() };
            }
            catch { DisposeModule(instance); throw; }
        }
        /// <summary>观察迟到异常并释放迟到实例，不把超时任务重新加入宿主。</summary>
        private static async void DisposeLate(Task<Loaded> work)
        {
            try { var result = await work.ConfigureAwait(false); DisposeModule(result.Module); } catch { }
        }
        /// <summary>诊断写入与退出清理采用同一锁。</summary>
        private void Record(string message) { lock (gate) { Diagnostics.Add(message); } }
        /// <summary>释放异常不阻断其他模块。</summary>
        private static void DisposeModule(IModule module) { if (module != null) try { module.Dispose(); } catch { } }
        /// <summary>反序清理，迟到初始化由其续接自行释放。</summary>
        public void Dispose()
        {
            lock (gate) { disposed = true; for (int i = modules.Count - 1; i >= 0; i--) DisposeModule(modules[i]); modules.Clear(); hubs.Clear(); capabilities.Clear(); Sections.Clear(); }
        }
    }
}
