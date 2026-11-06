using System.Collections.Generic;
using ModularShell;

namespace Demo
{
    /// <summary>只依赖契约的独立首页贡献插件。</summary>
    public sealed class Module : IModule
    {
        /// <summary>示例无需外部服务。</summary>
        public void Initialize() { }
        /// <summary>返回可移除的首页分区。</summary>
        public IEnumerable<Contribution> GetSections()
        {
            yield return new Contribution { Title = "独立外挂已连接", Text = "此分区来自 Demo.dll；移除包并重新启动后消失。" };
        }
        /// <summary>示例未持有资源。</summary>
        public void Dispose() { }
    }
}
