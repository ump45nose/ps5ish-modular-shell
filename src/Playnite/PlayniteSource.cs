using System;
using System.Collections.Generic;
using System.Linq;
using ModularShell;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>只通过公开 SDK 查询游戏和解析媒体路径，不创建第二份游戏主库。</summary>
    public sealed class PlayniteSource : IGameSource, IPlatformViews, IGameActions
    {
        private readonly IPlayniteAPI api;
        internal static bool PlatformReady;
        private static bool refreshingSteam;
        private static DateTime lastSteamRefresh;
        /// <summary>复用 Playnite 10.56 的公开平台刷新方法；保留 Steam 扩展的账号与导入规则。</summary>
        public async System.Threading.Tasks.Task RefreshSteamAsync(object host,string logPath)
        {
            if(!PlatformReady||host==null||refreshingSteam||DateTime.UtcNow-lastSteamRefresh<TimeSpan.FromMinutes(1))return;
            var type=host.GetType();if(type.Namespace!="Playnite.FullscreenApp.ViewModels"&&type.Namespace!="Playnite.DesktopApp.ViewModels")return;
            // 全屏程序的程序集版本固定为 1.0；以公开方法签名校验能力，不能误用产品版本。
            var method=type.GetMethod("UpdateLibrary",new[]{typeof(Playnite.SDK.Plugins.LibraryPlugin)});
            var allowed=type.GetProperty("GameAdditionAllowed");if(method==null||allowed==null||!Object.Equals(allowed.GetValue(host,null),true))return;
            var steam=api.Addons.Plugins.OfType<Playnite.SDK.Plugins.LibraryPlugin>().FirstOrDefault(p=>p.GetType().Assembly.GetName().Name=="SteamLibrary");if(steam==null)return;
            refreshingSteam=true;lastSteamRefresh=DateTime.UtcNow;
            try{var task=method.Invoke(host,new object[]{steam}) as System.Threading.Tasks.Task;if(task==null)throw new InvalidOperationException("平台刷新返回类型不匹配");await task;System.IO.File.AppendAllText(logPath,DateTime.UtcNow.ToString("o")+" Steam 平台刷新完成"+Environment.NewLine);}
            catch(Exception ex){try{System.IO.File.AppendAllText(logPath,DateTime.UtcNow.ToString("o")+" Steam 刷新失败："+ex.GetBaseException().GetType().Name+Environment.NewLine);}catch{}}
            finally{refreshingSteam=false;}
        }
        /// <summary>接受宿主提供的 API；所有 SDK 调用限定在 UI 线程。</summary>
        public PlayniteSource(IPlayniteAPI api) { this.api = api; }

        /// <summary>将 Playnite 引用映射为内容命名空间，不把 Steam App ID 当作 Playnite ID。</summary>
        public static GameItem Map(Game game, Func<string, string> resolve)
        {
            if (game == null) throw new ArgumentNullException("game");
            return new GameItem {
                Provider = "playnite", SourceInstance = "local", ItemId = game.Id.ToString("D"),
                Title = String.IsNullOrWhiteSpace(game.Name) ? "未命名游戏" : game.Name,
                CoverPath = Resolve(game.CoverImage, resolve),
                BackgroundPath = Resolve(game.BackgroundImage, resolve),
                IsInstalled = game.IsInstalled, LastActivity = game.LastActivity,
                Description = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(game.Description ?? "", "<[^>]+>", " ")),
                Platform = game.Source == null ? "本地游戏" : game.Source.Name,
                SourceId = game.GameId, Playtime = game.Playtime, Favorite = game.Favorite,InstallDirectory=game.InstallDirectory
            };
        }

        /// <summary>单张图片解析失败不会阻止基础条目出现。</summary>
        private static string Resolve(string path, Func<string, string> resolve)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            try { return resolve(path); } catch { return null; }
        }

        /// <summary>获取可见游戏快照；图片本身由视图按选择加载。</summary>
        public IList<GameItem> Read()
        {
            return api.Database.Games.Where(g => !g.Hidden).OrderByDescending(g => g.LastActivity)
                .ThenBy(g => g.Name).Select(MapCapabilities).ToList();
        }

        /// <summary>能力来自用户配置的启动动作和插件公开控制器，不把“已安装”等同可启动。</summary>
        private GameItem MapCapabilities(Game game)
        {
            var item=Map(game,api.Database.GetFullFilePath);
            item.CanStart=game.GameActions!=null && game.GameActions.Any(action=>action.IsPlayAction);
            item.CanInstall=false;
            var plugin=api.Addons.Plugins.FirstOrDefault(value=>value.Id==game.PluginId);
            if(plugin!=null)try{
                var play=plugin.GetPlayActions(new Playnite.SDK.Plugins.GetPlayActionsArgs {Game=game});
                item.CanStart=item.CanStart==true || (play!=null && play.Any());
                if(!game.IsInstalled){var install=plugin.GetInstallActions(new Playnite.SDK.Plugins.GetInstallActionsArgs {Game=game});item.CanInstall=install!=null && install.Any();}
            }catch{ /* 查询失败保留已知手动动作，不创建失效按钮。 */ }
            return item;
        }

        /// <summary>验证命名空间和当前库存在性后，将启动交回 Playnite。</summary>
        public void Start(GameItem item)
        {
            Guid id;
            if (item == null || item.Provider != "playnite" || item.SourceInstance != "local"
                || !Guid.TryParse(item.ItemId, out id) || api.Database.Games.Get(id) == null)
                throw new InvalidOperationException("当前游戏已不在可用游戏库中。");
            api.StartGame(id);
        }

        /// <summary>安装动作交由 Playnite 对应平台处理。</summary>
        public void Install(GameItem item) { api.InstallGame(new Guid(item.ItemId)); }
        /// <summary>收藏写回真实游戏库，不创建第二份状态。</summary>
        public void SetFavorite(GameItem item, bool favorite)
        {
            var game = api.Database.Games.Get(new Guid(item.ItemId));
            if (game == null) return;
            game.Favorite = favorite; api.Database.Games.Update(game); item.Favorite = favorite;
        }

        /// <summary>查询已由 Playnite 装载的插件；不从磁盘扫描或自行装入第三方 DLL。</summary>
        public bool HasExtension(string extensionId)
        {
            Guid id;
            return Guid.TryParse(extensionId, out id) && api.Addons.Plugins.Any(p => p.Id == id);
        }

        /// <summary>使用公开控件工厂和 GameContext，平台桥不解释扩展的业务字段。</summary>
        public object CreateView(string extensionId, string elementName, GameItem item)
        {
            Guid pluginId, gameId;
            if (item == null || item.Provider != "playnite" || item.SourceInstance != "local"
                || !Guid.TryParse(item.ItemId, out gameId) || !Guid.TryParse(extensionId, out pluginId)) return null;
            var game = api.Database.Games.Get(gameId);
            var plugin = api.Addons.Plugins.FirstOrDefault(p => p.Id == pluginId);
            if (game == null || plugin == null) return null;
            var control = plugin.GetGameViewControl(new Playnite.SDK.Plugins.GetGameViewControlArgs { Name = elementName, Mode = api.ApplicationInfo.Mode });
            var contextControl = control as Playnite.SDK.Controls.PluginUserControl;
            if (contextControl == null) throw new InvalidOperationException("平台控件不支持公开 GameContext 契约");
            contextControl.GameContext = game;
            return control;
        }
    }
}
