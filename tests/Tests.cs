using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using ModularShell;

/// <summary>独立进程验证真实 DLL 发现与异步旧上下文取消。</summary>
public static class Tests
{
    private static int count;
    public sealed class SlowModule : IModule
    {
        public static volatile bool Released;
        /// <summary>模拟不响应取消的缓慢可信插件。</summary>
        public void Initialize() { Thread.Sleep(200); }
        /// <summary>超时后的贡献绝不能进入首页。</summary>
        public IEnumerable<Contribution> GetSections() { yield return new Contribution { Title = "late" }; }
        /// <summary>用于核验迟到对象被释放。</summary>
        public void Dispose() { Released = true; AppDomain.CurrentDomain.SetData("SlowReleased", true); }
    }
    public sealed class FastModule : IModule
    {
        /// <summary>无外部依赖的测试初始化。</summary>
        public void Initialize() { }
        /// <summary>产生可计数贡献以验证依赖顺序。</summary>
        public IEnumerable<Contribution> GetSections() { yield return new Contribution { Title = "ready" }; }
        /// <summary>测试模块没有资源。</summary>
        public void Dispose() { }
    }
    /// <summary>两个声明同类影视能力的独立包，用于验证目录注册不再冲突。</summary>
    public sealed class HubModule : IModule, IHubViewProvider
    {
        public string Title { get { return "测试服务"; } }
        /// <summary>测试不创建 WPF 页面。</summary>
        public object CreateView() { return null; }
        /// <summary>测试模块初始化不联网。</summary>
        public void Initialize() { }
        /// <summary>服务入口不产生游戏贡献。</summary>
        public IEnumerable<Contribution> GetSections() { yield break; }
        /// <summary>无外部资源。</summary>
        public void Dispose() { }
    }
    /// <summary>仅在临时测试目录创建真实可装载包，不改已安装插件。</summary>
    private static void Package(string root, string name, string type, string dependencies, string capabilities)
    {
        string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        File.Copy(typeof(Tests).Assembly.Location, Path.Combine(directory, "Fixture.dll"));
        File.WriteAllText(Path.Combine(directory, "plugin.json"), "{\"schema\":2,\"hostContract\":1,\"version\":\"1.0.0\",\"id\":\"" + name + "\",\"entry\":\"Fixture.dll\",\"type\":\"Tests+" + type + "\",\"enabled\":true,\"dependencies\":" + dependencies + ",\"capabilities\":" + capabilities + "}");
    }
    /// <summary>断言失败立即返回非零状态。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        count++;
        Console.WriteLine("PASS " + name);
    }
    /// <summary>执行无需电视和游戏账号的 M0 回归。</summary>
    public static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("Passed: " + count); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    /// <summary>覆盖有插件、无插件、安全启动、错误清单与快速切换。</summary>
    private static async Task Run()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        using (var loader = new ModuleLoader())
        {
            loader.Load(Path.Combine(root, "plugins"), false);
            Check(loader.Sections.Count == 1, "独立 DLL 贡献分区");
        }
        using (var loader = new ModuleLoader())
        {
            loader.Load(Path.Combine(root, "plugins"), true);
            Check(loader.Sections.Count == 0, "安全启动跳过插件");
        }
        using (var loader = new ModuleLoader())
        {
            loader.Load(Path.Combine(root, "missing"), false);
            Check(loader.Sections.Count == 0, "无插件静态回退");
        }
        string temp = Path.Combine(Path.GetTempPath(), "modular-shell-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(temp, "bad"));
        try
        {
            foreach (string manifest in new[] {
                "{\"schema\":99}",
                "{\"schema\":1,\"hostContract\":1,\"id\":\"bad\",\"enabled\":true,\"entry\":\"../Demo.dll\"}",
                "{broken" })
            {
                File.WriteAllText(Path.Combine(temp, "bad", "plugin.json"), manifest);
                using (var loader = new ModuleLoader())
                {
                    loader.Load(temp, false);
                    Check(loader.Sections.Count == 0 && loader.Diagnostics.Count == 1, "错误包隔离");
                }
            }
        }
        finally { Directory.Delete(temp, true); }
        temp = Path.Combine(root, "test-plugin-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Package(temp, "slow", "SlowModule", "[]", "[]");
            using (var loader = new ModuleLoader())
            {
                await loader.LoadAsync(temp, false, CancellationToken.None, 30);
                Check(loader.Sections.Count == 0 && loader.Diagnostics.Any(d => d.Contains("超时")), "慢插件超时不提交贡献");
                await Task.Delay(300);
                Check(Equals(AppDomain.CurrentDomain.GetData("SlowReleased"), true) && loader.Sections.Count == 0, "迟到初始化释放且不会重新注册");
            }
            string graph = Path.Combine(temp, "graph");
            string hubs = Path.Combine(temp, "hubs");
            Package(hubs, "first", "HubModule", "[]", "[\"hubView\"]");
            Package(hubs, "second", "HubModule", "[]", "[\"hubView\"]");
            using (var loader = new ModuleLoader()) {
                await loader.LoadAsync(hubs,false,CancellationToken.None);
                Check(loader.GetHubProviders().Length == 2 && loader.Sections.Count == 0, "多个影视入口并存且不产生游戏贡献");
                Check(Object.ReferenceEquals(loader.GetCapability<IHubViewProvider>(),loader.GetHubProviders()[0]), "旧单入口消费者保持首个服务兼容");
                loader.Dispose(); Check(loader.GetHubProviders().Length == 0, "释放清空影视目录");
            }
            Package(graph, "a", "FastModule", "[\"z\"]", "[]");
            Package(graph, "z", "FastModule", "[]", "[]");
            using (var loader = new ModuleLoader())
            {
                await loader.LoadAsync(graph, false, CancellationToken.None);
                Check(loader.Sections.Count == 2 && loader.Diagnostics[0].StartsWith("z："), "依赖拓扑优先于目录顺序");
            }
            string missing = Path.Combine(temp, "missing");
            Package(missing, "a", "FastModule", "[\"missing\"]", "[]");
            Package(missing, "b", "FastModule", "[]", "[\"music\"]");
            using (var loader = new ModuleLoader())
            {
                await loader.LoadAsync(missing, false, CancellationToken.None);
                Check(loader.Sections.Count == 0 && loader.Diagnostics.Count == 2, "缺失依赖和虚假能力分别隔离");
            }
        }
        finally { /* CLR 锁定的测试 DLL 由构建脚本在此进程退出后清理。 */ }
        using (var preview = new PreviewCoordinator())
        {
            var results = new List<string>();
            var tasks = new List<Task>();
            for (int i = 0; i < 100; i++) tasks.Add(preview.SelectAsync("item" + i, results.Add, 100, 200));
            await Task.WhenAll(tasks);
            Check(results.Count == 102 && results[101].StartsWith("item99 ·"), "100 次切换仅当前上下文继续");
            results.Clear();
            var pending = preview.SelectAsync("stopped", results.Add, 20, 40);
            preview.Cancel();
            await pending;
            Check(results.Count == 1, "离开立即取消延迟事件");
        }
    }
}
