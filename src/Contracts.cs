using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ModularShell
{
    /// <summary>宿主提供的用户目录上下文；安装目录与用户数据保持分离。</summary>
    public sealed class ModuleContext { public string UserDataRoot { get; set; } public IPlatformViews PlatformViews { get; set; } public IPageMedia Media { get; set; } public INotificationAudio NotificationAudio {get;set;} }
    /// <summary>通知短音频独立于前台预览；调用方拥有事件过滤，宿主只负责会话。</summary>
    public interface INotificationAudio
    {
        /// <summary>播放一次本地短音频；空路径使用主题通知声，支持调用方取消。</summary>
        Task PlayAsync(string path,double volume,System.Threading.CancellationToken token);
    }
    /// <summary>独立内容页消费宿主预览规则，不复制时序状态机。</summary>
    public interface IPageMedia
    {
        /// <summary>按完整引用解析手动素材，首帧交回页面呈现。</summary>
        Task Preview(GameItem item, Action<IVideoSession> present);
        /// <summary>网络来源提供已缓存的候选，宿主仍优先使用本地手动覆盖。</summary>
        Task PreviewResolved(GameItem item, PreviewAsset fallback, Action<IVideoSession> present);
        /// <summary>离开页面、失去前台或进入正片时释放预览。</summary>
        void Cancel();
        IMediaPlaybackBackend Playback { get; }
    }
    /// <summary>宿主将前台/游戏会话变化交给独立页面，后台禁止继续发声。</summary>
    public interface IPageActivity { void SetActive(bool active); }
    /// <summary>正片后端与预览接口分离，不持有账号或服务器业务模型。</summary>
    public interface IMediaPlaybackBackend
    {
        string Capabilities { get; }
        /// <summary>只接受本地文件或本机受控流，首帧准备阶段静音。</summary>
        Task<IMediaPlaybackSession> OpenAsync(string location, CancellationToken cancel);
    }
    public sealed class PlaybackTrack { public int Id; public string Name; }
    /// <summary>正片会话具有定位与音轨/字幕选择，不向服务器自行写进度。</summary>
    public interface IMediaPlaybackSession : IVideoSession
    {
        long PositionTicks { get; }
        bool CanSeek { get; }
        void Seek(long ticks);
        PlaybackTrack[] AudioTracks { get; }
        PlaybackTrack[] SubtitleTracks { get; }
        void SelectAudio(int id);
        void SelectSubtitle(int id);
    }
    /// <summary>平台公开扩展控件桥；只在 UI 线程调用，返回值由具体宿主验证。</summary>
    public interface IPlatformViews
    {
        /// <summary>查询平台实际装载的扩展，不自行装载第三方程序集。</summary>
        bool HasExtension(string extensionId);
        /// <summary>通过平台公开入口为完整内容引用创建控件。</summary>
        object CreateView(string extensionId, string elementName, GameItem item);
    }
    /// <summary>通用详情扩展页，不向核心引入奖杯或影视领域类型。</summary>
    public interface IContextViewProvider
    {
        string Title { get; }
        string Diagnostic { get; }
        /// <summary>判断当前内容与依赖是否可展示；宿主捕获异常并收起入口。</summary>
        bool CanDisplay(GameItem item);
        /// <summary>由 UI 线程按用户动作创建独立页面；移出树时释放其界面订阅。</summary>
        object CreateView(GameItem item);
    }
    /// <summary>独立内容入口，不将影视条目伪装为平台游戏。</summary>
    public interface IHubViewProvider
    {
        string Title { get; }
        /// <summary>UI 线程按用户动作创建页面，页面负责取消自身请求。</summary>
        object CreateView();
    }
    /// <summary>服务品牌由插件声明，宿主主题决定卡片布局与焦点效果。</summary>
    public interface IHubAppearance
    {
        string AccentColor { get; }
        string IconGeometry { get; }
    }
    /// <summary>原生网页可请求回到服务入口，退出操作仍由宿主协调。</summary>
    public interface IPageExit
    {
        event Action ExitRequested;
    }
    /// <summary>可选内容组件自己提供设置页；宿主不包含服务器与登录业务。</summary>
    public interface IOptionalHubSettings
    {
        bool Enabled { get; }
        /// <summary>UI线程创建独立配置页；关闭后宿主重新检查入口开关。</summary>
        object CreateSettingsView();
    }
    /// <summary>可选素材选源偏好，旧素材插件无需实现。</summary>
    public interface IAssetLanguageSettings
    {
        string PreferredLanguage { get; }
        /// <summary>保存预告语言并重新评估未锁定素材。</summary>
        void SetPreferredLanguage(string language);
    }
    /// <summary>内容页可消费通用手柄动作，未处理动作仍由宿主负责导航。</summary>
    public interface IPageInput
    {
        /// <summary>返回 true 表示该动作已被页面处理，不再传递给其他控件。</summary>
        bool HandleInput(string action);
    }
    /// <summary>需要用户配置的模块在初始化前接收上下文。</summary>
    public interface IConfiguredModule
    {
        /// <summary>记录目录，不在构造函数中猜测便携版数据位置。</summary>
        void Configure(ModuleContext context);
    }
    /// <summary>素材来源只返回候选，不决定播放和焦点时序。</summary>
    public interface IAssetProvider
    {
        string Diagnostic { get; }
        /// <summary>完整内容引用决定素材归属，缺失返回 null。</summary>
        PreviewAsset Find(GameItem item);
    }
    /// <summary>播放候选与主题资源解耦，不持有来源数据库对象。</summary>
    public sealed class PreviewAsset
    {
        public string MusicPath, VideoPath;
        public string LogoPath;
        public string[] BackgroundPaths;
        public double MusicVolume = .35, VideoVolume = .5;
        public bool TrailerAudio;
    }
    /// <summary>视频后端返回真正可读的首帧，准备阶段必须静音。</summary>
    public interface IVideoBackend
    {
        /// <summary>等待首个完整 BGR32 解码帧；取消时释放自身解码器。</summary>
        Task<IVideoSession> PrepareAsync(string path, CancellationToken token);
    }

    /// <summary>通用像素帧契约，不引用 WPF 或任何解码实现。</summary>
    public interface IVideoSession : IDisposable
    {
        int Width { get; }
        int Height { get; }
        bool Ended { get; }
        /// <summary>复制最近完整帧到调用方复用缓冲，返回单调帧编号；没有新帧则 false。</summary>
        bool CopyFrame(byte[] destination, ref long version);
        /// <summary>设置视频原声音量，0 为静音。</summary>
        void SetVolume(double volume);
        /// <summary>主动预告可暂停；背景预览由宿主控制。</summary>
        void SetPaused(bool paused);
    }

    /// <summary>可替换的音乐能力；所有方法在宿主 UI 线程调用。</summary>
    public interface IMusicBackend
    {
        /// <summary>仅准备本地音乐，不自行发声；返回的独立会话由宿主释放。</summary>
        Task<IMusicSession> PrepareAsync(string path, CancellationToken token);
    }

    /// <summary>可选循环控制；界面提示音只播一次，旧音乐后端仍保持兼容。</summary>
    public interface IMusicLoopControl
    {
        /// <summary>设置当前会话是否在自然结束后循环。</summary>
        void SetLooping(bool enabled);
    }

    /// <summary>独立预览会话防止旧请求的释放误伤新选择。</summary>
    public interface IMusicSession : IDisposable
    {
        /// <summary>从零音量开始播放，音量变化由宿主决定。</summary>
        void Play();
        /// <summary>设置 0～1 的音量。</summary>
        void SetVolume(double volume);
    }

    /// <summary>静态游戏浏览的最小调用方契约；分页能力在真实规模验证后扩展。</summary>
    public interface IGameSource
    {
        /// <summary>返回当前可浏览条目的快照。</summary>
        IList<GameItem> Read();
        /// <summary>将用户启动意图交给拥有条目的平台。</summary>
        void Start(GameItem item);
    }

    /// <summary>平台无关的游戏展示模型；平台原始 ID 由适配层持有。</summary>
    public sealed class GameItem
    {
        public string Provider { get; set; }
        public string SourceInstance { get; set; }
        public string ItemId { get; set; }
        public string Title { get; set; }
        public string CoverPath { get; set; }
        public string BackgroundPath { get; set; }
        public bool IsInstalled { get; set; }
        public DateTime? LastActivity { get; set; }
        public string Platform { get; set; }
        public string Description { get; set; }
        public string SourceId { get; set; }
        public string InstallDirectory { get; set; }
        public ulong Playtime { get; set; }
        public bool Favorite { get; set; }
        public bool? CanStart { get; set; }
        public bool? CanInstall { get; set; }
    }

    /// <summary>可选平台操作，不改变旧的最小游戏来源接口。</summary>
    public interface IGameActions
    {
        void Install(GameItem item);
        void SetFavorite(GameItem item, bool favorite);
    }
    /// <summary>插件提供通用摘要，宿主不解释奖杯字段。</summary>
    public interface IContextSummaryProvider
    {
        object CreateSummary(GameItem item);
    }
    /// <summary>可选视图偏好，不将 WPF 或领域业务引入共享契约。</summary>
    public interface IViewPreferences
    {
        /// <summary>减少动效时终止装饰动画并保留静态状态。</summary>
        void SetReducedMotion(bool reduced);
    }
    /// <summary>后台素材解析支持取消，旧同步提供者仍可使用。</summary>
    public interface IAsyncAssetProvider
    {
        Task<PreviewAsset> ResolveAsync(GameItem item, CancellationToken token);
    }
    /// <summary>可选自动素材管理；宿主提交目录与活动状态，插件持有下载和来源策略。</summary>
    public interface IAutomaticAssets
    {
        event EventHandler<AssetChangedEventArgs> AssetChanged;
        bool Enabled {get;}
        /// <summary>控制未来获取，关闭不撤销已经验收的缓存。</summary>
        void SetEnabled(bool enabled);
        /// <summary>提交只读库快照，后台仅自动处理已安装内容。</summary>
        void Synchronize(IList<GameItem> catalog);
        /// <summary>将当前内容提升到队首，可显式重新获取。</summary>
        void Request(GameItem item,bool force);
        /// <summary>游戏运行时暂停后台获取。</summary>
        void SetPaused(bool paused);
        /// <summary>返回来源、实际尺寸和队列原因供通用设置界面显示。</summary>
        string Describe(GameItem item);
        /// <summary>用户明确锁定或解锁指定内容的素材选择。</summary>
        void SetLocked(GameItem item,bool locked);
        /// <summary>只清理可释放且未锁定的自动缓存。</summary>
        void ClearAutomatic(GameItem item);
    }
    /// <summary>更新只携带完整内容身份与改变的媒体类别，不包含播放器或主题对象。</summary>
    public sealed class AssetChangedEventArgs : EventArgs
    {
        public GameItem Item;
        public bool ImagesChanged, PlaybackChanged;
    }

    /// <summary>首页分区的最小展示数据；不引用任何平台或 UI 类型。</summary>
    public sealed class Contribution
    {
        public string Title { get; set; }
        public string Text { get; set; }
    }

    /// <summary>M0 契约 1：可信模块在启动时初始化，在退出时释放。</summary>
    public interface IModule : IDisposable
    {
        /// <summary>初始化模块；异常由装载器记录并隔离。</summary>
        void Initialize();
        /// <summary>获取首页分区；空集合表示无贡献。</summary>
        IEnumerable<Contribution> GetSections();
    }
}
