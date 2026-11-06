using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using System.Windows.Markup;
using System.Windows.Data;
using System.Globalization;
using ModularShell;

namespace AchievementAdapter
{
    /// <summary>仅适配 PA 公开主题控件；不引用其程序集、账号模型或私有缓存。</summary>
    public sealed class Module : IModule, IConfiguredModule, IContextViewProvider, IContextSummaryProvider
    {
        private const string ExtensionId = "e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b";
        private IPlatformViews bridge;
        private bool disposed;
        private ModuleContext context;private UnlockAudio audio;
        public string Title { get { return "奖杯（PA）"; } }
        public string Diagnostic { get { return "PA 提供奖杯与唯一通知窗口；"+(audio==null?"解锁音效尚未初始化":audio.Diagnostic); } }

        /// <summary>记录宿主平台桥；后台初始化不创建 WPF 或读取用户库。</summary>
        public void Configure(ModuleContext context) { this.context=context;bridge = context == null ? null : context.PlatformViews; }
        /// <summary>所有平台调用延迟到 UI 线程的实际页面请求。</summary>
        public void Initialize() { try{audio=new UnlockAudio(context);}catch{audio=null;} }
        /// <summary>不以未连接的占位数据填充首页。</summary>
        public IEnumerable<Contribution> GetSections() { yield break; }
        /// <summary>仅为当前 Playnite 命名空间和已安装 PA 提供入口。</summary>
        public bool CanDisplay(GameItem item)
        {
            Guid id;
            return !disposed && bridge != null && item != null && item.Provider == "playnite" && item.SourceInstance == "local"
                && Guid.TryParse(item.ItemId, out id) && bridge.HasExtension(ExtensionId);
        }

        /// <summary>首页摘要使用 PA 公开统计和进度控件，切换时由宿主移除旧上下文。</summary>
        public object CreateSummary(GameItem item)
        {
            if(!CanDisplay(item))return null;
            return new TrophySummary(RequireView("AchievementProgressBar",item),item.ItemId);
        }

        /// <summary>默认只展示已解锁项防剧透；完整列表由用户明确展开，数据仍由 PA 提供。</summary>
        public object CreateView(GameItem item)
        {
            if (!CanDisplay(item)) return null;
            return new TrophyDetails(RequireView("AchievementProgressBar",item),item.ItemId);
        }

        /// <summary>严格要求公开工厂返回真实 WPF 控件，拒绝静默空白页。</summary>
        private FrameworkElement RequireView(string name, GameItem item)
        {
            var view = bridge.CreateView(ExtensionId, name, item) as FrameworkElement;
            if (view == null) throw new InvalidOperationException("PA 未提供控件 " + name + "，请检查版本及主题集成开关。");
            return view;
        }
        /// <summary>模块没有额外通知订阅；移除模块不改变 PA 原有状态。</summary>
        public void Dispose() { disposed = true; if(audio!=null)audio.Dispose();audio=null;bridge = null; }
    }
    /// <summary>只将提供者公开统计转换为完成率，未就绪或异常统计不输出虚构百分比。</summary>
    public sealed class CompletionText : IMultiValueConverter
    {
        /// <summary>校验计数范围，保留零解锁的真实 0% 状态。</summary>
        public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
        {
            double unlocked,total;
            if(values.Length!=2||!Double.TryParse(System.Convert.ToString(values[0],culture),out unlocked)||!Double.TryParse(System.Convert.ToString(values[1],culture),out total)||total<=0||unlocked<0||unlocked>total||Double.IsNaN(total)||Double.IsInfinity(total))return "—";
            return Math.Floor(unlocked/total*100).ToString("0",culture)+"%";
        }
        /// <summary>展示转换从不反向修改 PA 统计。</summary>
        public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture){throw new NotSupportedException();}
    }
    /// <summary>只依据 PA 公开主题绑定控制空状态，不推断认证或奖杯等级。</summary>
    public sealed class CountVisibility : IValueConverter
    {
        private readonly bool inverse;
        public CountVisibility(bool inverse){this.inverse=inverse;}
        /// <summary>未绑定或非正数视为尚未提供数据。</summary>
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture){int count;bool available=Int32.TryParse(System.Convert.ToString(value,culture),out count)&&count>0;return available!=inverse?Visibility.Visible:Visibility.Collapsed;}
        /// <summary>显示状态不会反向写入提供者数据。</summary>
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture){return Binding.DoNothing;}
    }
}


namespace AchievementAdapter
{
    /// <summary>版本限定的 PA 公开刷新桥；账号、网络和通知仍由 PA 管理。</summary>
    internal static class TrophyRefresh
    {
        private static readonly System.Collections.Generic.Dictionary<string,DateTime> recent=new System.Collections.Generic.Dictionary<string,DateTime>();
        private static bool running;
        /// <summary>合并并发及短时间重复请求，不通过历史缓存模拟新解锁。</summary>
        internal static async System.Threading.Tasks.Task<string> Request(string id)
        {
            Guid game;DateTime previous;if(!Guid.TryParse(id,out game))return "游戏标识无效";
            if(running||recent.TryGetValue(id,out previous)&&DateTime.UtcNow-previous<TimeSpan.FromSeconds(30))return "已合并重复刷新请求";
            var assembly=System.Linq.Enumerable.FirstOrDefault(AppDomain.CurrentDomain.GetAssemblies(),a=>a.GetName().Name=="PlayniteAchievements");
            if(assembly==null)return "PA 未加载";
            try{
                string manifest=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assembly.Location),"extension.yaml");
                if(!System.IO.File.Exists(manifest)||!System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(manifest),@"(?m)^Version:\s*3\.1\.3\s*$"))return "PA 版本未适配，未请求网络刷新";
                var type=assembly.GetType("PlayniteAchievements.PlayniteAchievementsPlugin");
                var method=type==null?null:type.GetMethod("RequestSingleGameRefreshAsync",new[]{typeof(Guid)});
                var instance=type==null?null:type.GetProperty("Instance",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static);
                if(method==null||instance==null)return "PA 公开刷新接口不可用";
                running=true;if(recent.Count>256)recent.Clear();recent[id]=DateTime.UtcNow;
                var task=method.Invoke(instance.GetValue(null,null),new object[]{game}) as System.Threading.Tasks.Task;
                if(task==null)return "PA 刷新返回类型不匹配";
                await task;return "已请求 PA 更新；数据与登录状态由提供者确认";
            }catch(Exception ex){return "PA 刷新失败："+ex.GetBaseException().GetType().Name;}
            finally{running=false;}
        }
    }
}
