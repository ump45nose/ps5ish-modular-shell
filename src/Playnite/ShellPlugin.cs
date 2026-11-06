using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>Playnite 10 伴生扩展入口，注册可被全屏主题引用的控件。</summary>
    public sealed class ShellPlugin : GenericPlugin
    {
        private Window browser;
        private readonly List<ShellControl> controls = new List<ShellControl>();
        private bool sessionBusy;
        private string sessionMessage;
        private DispatcherTimer sessionMonitor;
        private Playnite.SDK.Models.Game sessionGame;
        private bool observedPlatformSession;
        private readonly QuickPanelBridge quickPanel=new QuickPanelBridge();
        private DispatcherTimer databaseRefresh;
        public override Guid Id { get { return new Guid("f13fdbe6-52ba-4198-8bea-457be1a609cf"); } }

        /// <summary>只注册入口，不在扩展构造阶段读取未就绪的游戏数据库。</summary>
        public ShellPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = false };
            AddCustomElementSupport(new AddCustomElementSupportArgs {
                SourceName = "ModularShell", ElementList = new List<string> { "Browser" }
            });
            // 平台安装、游玩时长和收藏更新不一定触发整库同步完成事件。
            api.Database.Games.ItemUpdated+=DatabaseChanged;
            api.Database.Games.ItemCollectionChanged+=DatabaseCollectionChanged;
        }
        /// <summary>单条变化通过同一个有界防抖队列交给 UI。</summary>
        private void DatabaseChanged(object sender,ItemUpdatedEventArgs<Playnite.SDK.Models.Game> args){QueueDatabaseRefresh();}
        /// <summary>新增或移除游戏同样刷新当前库，不建立第二份平台数据库。</summary>
        private void DatabaseCollectionChanged(object sender,ItemCollectionChangedEventArgs<Playnite.SDK.Models.Game> args){QueueDatabaseRefresh();}
        /// <summary>合并批量导入期间的密集事件，避免每条变更都重建视图。</summary>
        private void QueueDatabaseRefresh()
        {
            PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(()=>{
                if(databaseRefresh==null){databaseRefresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(600)};databaseRefresh.Tick+=(s,e)=>{databaseRefresh.Stop();RefreshControls();};}
                databaseRefresh.Stop();databaseRefresh.Start();
            }));
        }

        /// <summary>在 Playnite 主菜单提供可操作的真实游戏库验证入口。</summary>
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem { Description = "打开 Modular Shell 验证界面", MenuSection = "@Modular Shell",
                Action = a => PlayniteApi.MainView.UIDispatcher.Invoke(new Action(OpenBrowser)) };
            yield return new MainMenuItem { Description = "启动独立快捷面板（Ctrl+Alt+Space）", MenuSection = "@Modular Shell",
                Action = a => { try { quickPanel.Start(Path.GetDirectoryName(typeof(ShellPlugin).Assembly.Location),GetPluginUserDataPath()); } catch { PlayniteApi.Dialogs.ShowErrorMessage("快捷面板启动失败，游戏主页仍可使用。"); } } };
        }

        /// <summary>复用一个验证窗口；数据仍来自当前 Playnite 进程。</summary>
        private void OpenBrowser()
        {
            if (browser != null) { browser.Activate(); return; }
            var control = CreateControl();
            browser = new Window { Title = "Modular Shell · Playnite 游戏库", Width = 1280, Height = 760,
                MinWidth = 800, MinHeight = 500, Content = control, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            browser.Closed += (s, e) => { controls.Remove(control); browser = null; };
            browser.Show();
        }

        /// <summary>向主题提供稳定名称 ModularShell_Browser，不要求改动 Playnite 本体。</summary>
        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            return args.Name == "Browser" ? CreateControl() : null;
        }

        /// <summary>创建控件，并把用户数据路径交由 SDK 决定以支持便携安装。</summary>
        private ShellControl CreateControl()
        {
            string root = Path.GetDirectoryName(typeof(ShellPlugin).Assembly.Location);
            var control = new ShellControl(new PlayniteSource(PlayniteApi), root, GetPluginUserDataPath());
            control.Loaded += (s, e) => { if (!controls.Contains(control)) controls.Add(control); if (sessionBusy) control.SetSession(sessionMessage, true); };
            control.Unloaded += (s, e) => controls.Remove(control);
            return control;
        }

        /// <summary>把会话事件投递到 UI 线程，避免 SDK 事件线程直接更新控件。</summary>
        private void Session(string message, bool busy)
        {
            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() => {
                sessionBusy = busy; sessionMessage = message;
                foreach (var control in controls.ToArray()) control.SetSession(message, busy);
            }));
        }
        /// <summary>全屏主题创建早于数据库就绪；平台启动完成后重新读取真实库。</summary>
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args) { PlayniteSource.PlatformReady=true;RefreshControls(); }
        /// <summary>平台同步安装状态后刷新当前快照，不重启媒体插件。</summary>
        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args) { RefreshControls(); }
        /// <summary>所有活跃视图在 UI 线程接收平台库变更。</summary>
        private void RefreshControls(){PlayniteApi.MainView.UIDispatcher.Invoke(new Action(()=>{foreach(var control in controls.ToArray())control.RefreshLibrary();}));}
        /// <summary>启动前暂停交互；后续媒体后端在同一路径释放。</summary>
        public override void OnGameStarting(OnGameStartingEventArgs args) { quickPanel.Update(args.Game.Id.ToString("D"),args.Game.Name,"启动中");Session("游戏启动中", true); WatchSession(args.Game); }
        /// <summary>公开平台状态兜底取消回调丢失；不通过超时猜测游戏已经结束。</summary>
        private void WatchSession(Playnite.SDK.Models.Game game)
        {
            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() => {
                if(sessionMonitor!=null)sessionMonitor.Stop();
                sessionGame=game;
                observedPlatformSession=game.IsLaunching||game.IsRunning;
                sessionMonitor=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
                sessionMonitor.Tick+=(s,e)=>{
                    if(!sessionBusy||sessionGame==null){sessionMonitor.Stop();return;}
                    // 先观察到真实活动，再允许根据两个公开标记都归零恢复，避免启动初始化竞态。
                    if(sessionGame.IsLaunching||sessionGame.IsRunning){observedPlatformSession=true;return;}
                    if(!observedPlatformSession)return;
                    sessionMonitor.Stop();
                    quickPanel.Update(sessionGame.Id.ToString("D"),sessionGame.Name,"已结束");
                    Session("启动或运行已结束，可继续浏览",false);
                };
                sessionMonitor.Start();
            }));
        }
        /// <summary>运行期间禁止重复启动。</summary>
        public override void OnGameStarted(OnGameStartedEventArgs args) { quickPanel.Update(args.Game.Id.ToString("D"),args.Game.Name,"运行中");Session("游戏运行中", true); }
        /// <summary>停止后恢复原选择的可操作状态，不抢占用户前台。</summary>
        public override void OnGameStopped(OnGameStoppedEventArgs args) { quickPanel.Update(args.Game.Id.ToString("D"),args.Game.Name,"已退出");Session("游戏已退出，可继续浏览", false); }
        /// <summary>启动取消时恢复基础浏览。</summary>
        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args) { Session("启动取消，可重试", false); }
        /// <summary>桌面模式由 Playnite 的 SDL 设备支持提供 DS5 语义，不要求模拟 Xbox 手柄。</summary>
        public override void OnDesktopControllerButtonStateChanged(OnControllerButtonStateChangedArgs args) { ControllerChanged(args); }
        /// <summary>全屏输入经同一去重状态机处理；实际主题路由仍须目标设备验收。</summary>
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args) { ControllerChanged(args); }
        /// <summary>任意设备断开都释放保留输入，等待下一次真实按下。</summary>
        public override void OnControllerDisconnected(OnControllerDisconnectedArgs args)
        {
            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() => { foreach (var control in controls.ToArray()) control.ClearController(); }));
        }
        /// <summary>SDK 回调可能来自非 UI 线程，串行转交当前活动控件。</summary>
        private void ControllerChanged(OnControllerButtonStateChangedArgs args)
        {
            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() => {
                foreach (var control in controls.ToArray()) control.ReceiveController(args.Button, args.State);
            }));
        }
        /// <summary>应用退出时关闭验证窗口，控件 Unloaded 释放独立模块。</summary>
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            PlayniteApi.Database.Games.ItemUpdated-=DatabaseChanged;PlayniteApi.Database.Games.ItemCollectionChanged-=DatabaseCollectionChanged;if(databaseRefresh!=null)databaseRefresh.Stop();
            if(sessionMonitor!=null)sessionMonitor.Stop();
            quickPanel.Dispose();
            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() => { if (browser != null) browser.Close(); }));
        }
    }
}
