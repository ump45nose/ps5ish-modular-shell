using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>把通用内容和命令连接到已校验的主题槽位。</summary>
    public sealed partial class ShellControl : PluginUserControl, IPageMedia
    {
        private readonly IGameSource source;
        private readonly string packageRoot;
        private readonly string dataRoot;
        private readonly FrameworkElement layout;
        private readonly ListBox games;
        private readonly TextBlock title;
        private readonly TextBlock metadata;
        private readonly TextBlock status;
        private Image background;
        private readonly Button start;
        private readonly StackPanel sections;
        private ModuleLoader modules;
        private CancellationTokenSource lifetime;
        private CancellationTokenSource selection;
        private bool busy;
        private string palette;
        private string selectedId;
        private bool loaded;
        private readonly MediaPreview media = new MediaPreview();
        private readonly Image videoLayer;
        private readonly Button trailer;
        private readonly DispatcherTimer frameTimer;
        private IVideoSession videoSession;
        private WriteableBitmap videoBitmap;
        private byte[] videoBytes;
        private long frameVersion;
        private bool manualTrailer, paused;
        private ShellSettings settings;
        private readonly ControllerInput controllerInput = new ControllerInput();
        private readonly DispatcherTimer controllerTimer;
        private bool sdkInputActive;
        private readonly HashSet<Playnite.SDK.Events.ControllerInput> sdkPressed = new HashSet<Playnite.SDK.Events.ControllerInput>();
        private IList<GameCard> allCards;
        private bool showAll;
        private readonly NavigationPreference navigation;
        private IAssetProvider assets;
        private Window ownerWindow;
        private bool maintenanceActive;
        private IContextViewProvider contextProvider;
        private IHubViewProvider hubProvider;
        private readonly Grid contextPage;
        private readonly ContentControl contextContent;
        private readonly TextBlock contextTitle;
        public string ThemeDiagnostic { get; private set; }

        /// <summary>组合可信主题，再绑定稳定槽位；安全模式跳过外部包。</summary>
        public ShellControl(IGameSource source, string packageRoot, string dataRoot)
        {
            this.source = source; this.packageRoot = packageRoot; this.dataRoot = dataRoot;
            Focusable = true; settings = ShellSettings.Load(dataRoot);
            navigation = NavigationPreference.Load(dataRoot); showAll = navigation.showAll;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            bool safe = File.Exists(Path.Combine(dataRoot, "safe-mode"));
            string diagnostic;
            layout = ThemeComposer.Load(packageRoot, safe, ThemeComposer.ReadLayoutPreference(dataRoot), out diagnostic); ThemeDiagnostic = diagnostic;
            // 以 16:9 逻辑画布等比缩放，4K 和不同 DPI 下保留安全边距。
            layout.Width = 1280; layout.Height = 720;
            var shellSurface = new Grid();
            shellSurface.Children.Add(new Viewbox { Child = layout, Stretch = Stretch.Uniform });
            hubSurface = new ContentControl { Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            shellSurface.Children.Add(hubSurface); Content = shellSurface;
            // 可选插件页面由通用覆盖层承载，主题不会解析任何第三方 CLR 类型。
            contextPage = new Grid { Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Margin = new Thickness(0) };
            contextPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contextPage.RowDefinitions.Add(new RowDefinition());
            var contextHeader = new DockPanel { Margin = new Thickness(48,26,48,16) };
            var contextBack = new Button { Content = "返回游戏", Padding = new Thickness(18,10,18,10) };
            contextBack.SetResourceReference(FrameworkElement.StyleProperty, "ActionStyle");
            DockPanel.SetDock(contextBack, Dock.Right); contextHeader.Children.Add(contextBack);
            contextTitle = new TextBlock { FontSize = 28, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; contextHeader.Children.Add(contextTitle);
            contextContent = new ContentControl { Margin = new Thickness(36,0,36,24), HorizontalContentAlignment=HorizontalAlignment.Stretch, VerticalContentAlignment=VerticalAlignment.Stretch }; Grid.SetRow(contextContent,1);
            contextPage.Children.Add(contextHeader); contextPage.Children.Add(contextContent);
            KeyboardNavigation.SetTabNavigation(contextPage, KeyboardNavigationMode.Cycle);
            var surface = layout as Panel;
            if (surface != null) surface.Children.Add(contextPage);
            contextBack.Click += (s,e) => CloseOverlay();
            Background = new SolidColorBrush(Color.FromRgb(8, 16, 29));
            games = (ListBox)layout.FindName("Games"); title = (TextBlock)layout.FindName("Title");
            metadata = (TextBlock)layout.FindName("Metadata"); status = (TextBlock)layout.FindName("Status");
            background = (Image)layout.FindName("Scene"); start = (Button)layout.FindName("Start");
            videoLayer = (Image)layout.FindName("VideoLayer"); trailer = (Button)layout.FindName("Trailer");
            frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            frameTimer.Tick += (s, e) => RenderVideo();
            controllerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            controllerTimer.Tick += (s, e) => { if (sdkInputActive) controllerInput.Process(SdkButtons(), DateTime.UtcNow); else controllerInput.Poll(); };
            controllerInput.Action += HandleController;
            sections = (StackPanel)layout.FindName("Sections");
            var toggle = (Button)layout.FindName("Palette");
            palette = safe ? "midnight" : ThemeComposer.ReadPreference(dataRoot);
            if (!safe) try { ThemeComposer.ApplyPalette(layout, packageRoot, palette); } catch (Exception ex) { ThemeDiagnostic = ex.Message; }
            if (!safe) try { var combination=ThemeComposer.ReadCombination(dataRoot); ThemePacks.Apply(layout,packageRoot,combination.motion,combination.ambience); } catch (Exception ex) { ThemeDiagnostic=ex.Message; }
            foreach (string packName in new[] {"MotionChoice","AmbienceChoice"}) {
                var packButton=layout.FindName(packName) as Button;
                if(packButton!=null){packButton.IsEnabled=!safe;string chosen=packName;packButton.Click+=(s,e)=>SwitchPack(chosen);}
            }
            toggle.IsEnabled = !safe;
            toggle.Click += (s, e) => SwitchPalette();
            games.SelectionChanged += (s, e) => ShowSelection();
            start.Click += (s, e) => StartSelected();
            trailer.Click += (s, e) => BeginPreview(true);
            ((Button)layout.FindName("Home")).Click += (s, e) => ShowLibrary(false);
            ((Button)layout.FindName("Library")).Click += (s, e) => ShowLibrary(true);
            ((Button)layout.FindName("Settings")).Click += (s, e) => OpenSettings();
            ((Button)layout.FindName("SaveSettings")).Click += (s, e) => SaveSettings();
            ((Button)layout.FindName("CancelSettings")).Click += (s, e) => CloseOverlay();
            ((Button)layout.FindName("Details")).Click += (s, e) => OpenDetails();
            ((Button)layout.FindName("BackDetails")).Click += (s, e) => CloseOverlay();
            // 新增可选设置槽位，旧契约 2 主题无需增加业务引用即可继续使用。
            var layoutChoice = layout.FindName("LayoutChoice") as Button;
            if (layoutChoice != null) { layoutChoice.IsEnabled = !safe; layoutChoice.Click += (s, e) => SwitchLayout(); }
            var maintenance = layout.FindName("Maintenance") as Button;
            if (maintenance != null) maintenance.Click += (s, e) => OpenMaintenance();
            var contextAction = layout.FindName("ContextPageAction") as Button;
            if (contextAction != null) { contextAction.Visibility = Visibility.Collapsed; contextAction.Click += (s,e) => OpenContextPage(); }
            var hubAction = layout.FindName("HubAction") as Button;
            if(hubAction!=null){hubAction.Visibility=Visibility.Collapsed;hubAction.Click+=(s,e)=>OpenHubPage();}
            InitializeLibrary();
            InitializeAtmosphere();
            InitializeExperienceSettings();
            InitializePs5Presentation();
            PreviewKeyDown += (s,e) => {
                // 浏览器尚未取得原生焦点时也能退出；延后释放避免阻塞 WebView2 按键回调。
                if (hubSurface.Visibility == Visibility.Visible && (e.Key == Key.F10 || e.SystemKey == Key.F10)) { e.Handled = true; Dispatcher.BeginInvoke(new Action(CloseHubService)); return; }
                if (Keyboard.FocusedElement is TextBox || hubSurface.Visibility == Visibility.Visible) return;
                string action=e.Key==Key.Left?"left":e.Key==Key.Right?"right":e.Key==Key.Up?"up":e.Key==Key.Down?"down":e.Key==Key.Enter?"accept":null;
                if(action!=null){HandleController(action);e.Handled=true;}
            };
            Loaded += (s, e) => LoadLibrary();
            Unloaded += (s, e) => Release();
            IsVisibleChanged += (s, e) => { if(ownerWindow==null||!ownerWindow.IsActive)StopStartup(); UpdateInputPolling(); BeginPreview(false); if(ownerWindow==null||!ownerWindow.IsActive)StopCue(); };
            PreviewKeyDown += (s, e) => {
                if(e.Key==Key.Escape&&keyboardPage.Visibility==Visibility.Visible){NavigateLibrary("back");e.Handled=true;return;}
                if(e.Key==Key.Escape&&startupActive){StopStartup();e.Handled=true;return;}
                if (e.Key == Key.Escape && (manualTrailer || OverlayOpen || showAll)) { var pageInput=(hubSurface.Content ?? contextContent.Content) as IPageInput; if(pageInput==null || !pageInput.HandleInput("back"))GoBack(); e.Handled = true; }
                else if (manualTrailer && e.Key == Key.Space && videoSession != null) { paused = !paused; videoSession.SetPaused(paused); e.Handled = true; }
            };
        }

        /// <summary>保存用户预设；失败时显示诊断，仍保留有效布局。</summary>
        private void SwitchPalette()
        {
            string next = palette == "midnight" ? "ember" : "midnight";
            try { ThemeComposer.ApplyPalette(layout, packageRoot, next); ThemeComposer.SavePreference(dataRoot, next); palette = next; }
            catch (Exception ex) { status.Text = "氛围设置未保存：" + ex.Message; }
        }

        /// <summary>布局属于独立预设，保存后下次打开应用，不干扰当前媒体控件生命周期。</summary>
        private void SwitchLayout()
        {
            try
            {
                string next = ThemeComposer.ReadLayoutPreference(dataRoot) == "ps5ish" ? "cinema" : "ps5ish";
                ThemeComposer.SavePreference(dataRoot, palette, next);
                ((Button)layout.FindName("LayoutChoice")).Content = "布局：" + (next == "cinema" ? "影院" : "PS5ish");
                ((TextBlock)layout.FindName("SettingsError")).Text = "布局已保存，下次打开界面生效。";
            }
            catch (Exception ex) { ((TextBlock)layout.FindName("SettingsError")).Text = "布局未保存：" + ex.Message; }
        }

        /// <summary>独立更换动效或氛围资产，持久化不触碰手动素材索引。</summary>
        private void SwitchPack(string name)
        {
            try {
                var previous=ThemeComposer.ReadCombination(dataRoot); var next=ThemeComposer.ReadCombination(dataRoot);
                if(name=="MotionChoice")next.motion=next.motion=="soft"?"brisk":"soft";
                else next.ambience=next.ambience=="ocean"?"dusk":"ocean";
                ThemePacks.Apply(layout,packageRoot,next.motion,next.ambience);
                try { ThemeComposer.SaveCombination(dataRoot,next); }
                catch { ThemePacks.Apply(layout,packageRoot,previous.motion,previous.ambience); throw; }
                ((TextBlock)layout.FindName("SettingsError")).Text="组合已保存："+next.motion+" / "+next.ambience;
            } catch(Exception ex){((TextBlock)layout.FindName("SettingsError")).Text="组合未应用："+ex.Message;}
        }

        /// <summary>桌面维护由独立进程承载；期间停止预览，完成后重新读取素材与设置。</summary>
        private async void OpenMaintenance()
        {
            if (maintenanceActive || busy) return;
            maintenanceActive = true; media.Cancel(); UpdateInputPolling();
            IsEnabled = false;
            try
            {
                string script = Path.Combine(packageRoot, "scripts", "maintenance-ui.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("维护工具缺失", script);
                // Windows 路径不允许双引号；直接启动可信固定脚本，不经 cmd 拼接命令。
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe")) {
                    Arguments = "-NoProfile -STA -File \"" + script + "\" -UserDataRoot \"" + Path.Combine(dataRoot, ".") + "\"",
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var process = Process.Start(info))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0) throw new InvalidOperationException("维护工具退出异常：" + process.ExitCode);
                }
                if (loaded)
                {
                    Release(); settings = ShellSettings.Load(dataRoot); LoadLibrary();
                    ((TextBlock)layout.FindName("SettingsError")).Text = "维护已结束，素材已重新加载；布局变更下次打开生效。";
                }
            }
            catch (Exception ex) { if (loaded) ((TextBlock)layout.FindName("SettingsError")).Text = "维护未完成：" + ex.Message; }
            finally { maintenanceActive = false; IsEnabled = true; UpdateInputPolling(); }
        }

        /// <summary>读取游戏快照；仅可见卡片实际读取缩略图。</summary>
        private async void LoadLibrary()
        {
            if (loaded) return;
            loaded = true; lifetime = new CancellationTokenSource();
            try
            {
                var items = source.Read().Select(g => new GameCard(g, lifetime.Token)).ToList();
                // 仅在本机用户目录输出 SDK 快照，供素材关联和验收使用，不读取私有数据库。
                if(source is PlayniteSource)try{
                    Directory.CreateDirectory(dataRoot);
                    using(var snapshot=File.Create(Path.Combine(dataRoot,"game-catalog.json")))
                        new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<GameItem>)).WriteObject(snapshot,items.Select(c=>c.Item).ToList());
                }catch{}
                if (selectedId == null)
                {
                    var previous = items.FirstOrDefault(c => c.Item.ItemId == navigation.itemId && c.Item.Provider == navigation.provider && c.Item.SourceInstance == navigation.sourceInstance);
                    if (previous != null) selectedId = previous.Item.ItemId;
                }
                allCards = items; ShowLibrary(showAll);
                if (GameContext != null) GameContextChanged(null, GameContext);
                status.Text = items.Count == 0 ? "游戏库为空，请先在 Playnite 导入游戏。" : "← → 选择    Enter 开始    Tab 切换操作";
                modules = new ModuleLoader();
                var currentModules = modules;
                assets = null;
                ownerWindow = Window.GetWindow(this);
                if (ownerWindow != null) { ownerWindow.Activated += ForegroundChanged; ownerWindow.Deactivated += ForegroundChanged; }
                wallpaperTimer.Start();
                AttachInputWindow();
                UpdateInputPolling();
                ShowSelection();
                await currentModules.LoadAsync(Path.Combine(packageRoot, "plugins"), File.Exists(Path.Combine(dataRoot, "safe-mode")), lifetime.Token, 2000, new ModuleContext { UserDataRoot = dataRoot, PlatformViews = source as IPlatformViews, Media = this, NotificationAudio=this });
                if (!loaded || !ReferenceEquals(currentModules, modules)) return;
                status.Text="× 详情    ○ 返回    L1 / R1 页面    Options 操作菜单";
                assets = modules.GetCapability<IAssetProvider>();
                InitializeAutomaticAssets();
                contextProvider = modules.GetCapability<IContextViewProvider>();
                hubProvider = modules.GetCapability<IHubViewProvider>();
                var hubAction=layout.FindName("HubAction") as Button;
                RefreshHubVisibility();
                sections.Children.Clear();
                foreach (var section in modules.Sections)
                    sections.Children.Add(new TextBlock { Text = section.Title + " · " + section.Text, FontSize = 12, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
                if (ThemeDiagnostic != null) status.Text = ThemeDiagnostic;
                await PlayStartupAsync();
                // 启屏结束固定进入首页第一项，不恢复上次库页或上次焦点。
                ShowLibrary(false);games.SelectedIndex=games.Items.Count>0?0:-1;
                if(experience.homeEntrance&&!settings.reducedMotion){var entry=new TranslateTransform();games.RenderTransform=entry;entry.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(600,0,TimeSpan.FromMilliseconds(650)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});games.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(350)));games.UpdateLayout();var carousel=FindCarousel(games);if(carousel!=null)carousel.PlayEntrance();}
                ShowSelection();
            }
            catch (Exception ex) { status.Text = "游戏库暂不可用：" + ex.Message; start.IsEnabled = false; }
        }

        /// <summary>每次选择取消旧解码；异步完成后再次核对当前上下文。</summary>
        private async void ShowSelection()
        {
            CancelAssetRefresh();
            ClearContextPage();
            if (selection != null) { selection.Cancel(); selection.Dispose(); }
            selection = new CancellationTokenSource(); var token = selection.Token;
            var card = games.SelectedItem as GameCard; var item = card == null ? null : card.Item;
            selectedId = item == null ? null : item.ItemId;
            if(libraryReady && item!=null)AnimateSelection();
            title.Text = item == null ? "欢迎回到游戏库" : item.Title;
            ThemePacks.RevealContent(title,layout,settings.reducedMotion);
            metadata.Text = item == null ? "连接你的游戏，继续下一段旅程" : card.IsLibraryEntry ? "浏览全部游戏、筛选与搜索" : (item.IsInstalled ? "已安装" : "尚未安装") + "  ·  " + (item.Platform ?? "PLAYNITE");
            start.IsEnabled = item != null && !busy && (card.IsLibraryEntry || (item.IsInstalled ? item.CanStart!=false : item.CanInstall!=false));
            start.Content = card!=null && card.IsLibraryEntry ? "打开游戏库" : item != null && !item.IsInstalled ? "安装游戏" : "开始游戏";
            PlayCue("switch");
            UpdateTrailerAvailability();
            BeginPreview(false);
            if (item == null || !loaded) return;
            try
            {
                await LoadAtmosphere(item,token);
            }
            catch { /* 缺图、损坏或取消时保留安全背景。 */ }
        }

        /// <summary>只响应用户主动启动，失败恢复基础操作。</summary>
        private void StartSelected()
        {
            var card = games.SelectedItem as GameCard;
            if (busy || card == null) return;
            if(card.IsLibraryEntry){ShowLibrary(true);return;}
            if(card.Item.IsInstalled?card.Item.CanStart==false:card.Item.CanInstall==false){status.Text="此游戏未提供可用的启动或安装动作";return;}
            if(!card.Item.IsInstalled && source is IGameActions){try{((IGameActions)source).Install(card.Item);status.Text="已请求平台安装";}catch(Exception ex){status.Text="安装未启动："+ex.Message;}return;}
            try { SetSession("已请求 Playnite 启动游戏", true); SaveNavigation(); source.Start(card.Item); }
            catch (Exception ex) { SetSession("启动失败：" + ex.Message, false); }
        }

        /// <summary>保留选择，不自动从游戏夺回系统焦点。</summary>
        public void SetSession(string message, bool running)
        {
            var pageActivity=(hubSurface.Content ?? contextContent.Content) as IPageActivity;
            if(pageActivity!=null)pageActivity.SetActive(!running && ownerWindow!=null && ownerWindow.IsActive);
            busy = running; status.Text = message; start.IsEnabled = !busy && games.SelectedItem != null;
            var automatic=assets as IAutomaticAssets;if(automatic!=null)automatic.SetPaused(running);
            if (running && selection != null) selection.Cancel();
            UpdateTrailerAvailability();
            if (running) {media.Cancel();StopPageVideo();StopCue();} else BeginPreview(false);
            UpdateInputPolling();
        }

        /// <summary>选择、前台或会话变化都走同一音乐协调器；后台禁止恢复。</summary>
        private async void BeginPreview(bool manual)
        {
            manualTrailer = false; paused = false;
            if(IsGeneralPage&&!showAll){media.Cancel();RefreshPageBackground();return;}
            if(!showAll)StopPageVideo();
            if (!loaded || startupActive || busy || maintenanceActive || !IsVisible || OverlayOpen || ownerWindow == null || !ownerWindow.IsActive || modules == null) {media.Cancel();return;}
            var card = games.SelectedItem as GameCard;
            var asset = assets == null ? null : assets.Find(card == null ? null : card.Item);
            if (asset == null) asset = new PreviewAsset();
            if (manual && (String.IsNullOrWhiteSpace(asset.VideoPath) || modules.GetCapability<IVideoBackend>() == null)) return;
            manualTrailer = manual;
            var request = new PreviewRequest { MusicPath = manual || !settings.music ? null : (String.IsNullOrWhiteSpace(asset.MusicPath)?DefaultMusicPath():asset.MusicPath), VideoPath = asset.VideoPath,
                MusicVolume = settings.musicVolume * (settings.nightMode ? .25 : 1), VideoVolume = asset.VideoVolume * (settings.nightMode ? .25 : 1),
                TrailerAudio = manual || asset.TrailerAudio || settings.trailerAudio, AutoVideo = manual || (!showAll&&settings.autoVideo) };
            await media.SelectAsync(modules.GetCapability<IMusicBackend>(), modules.GetCapability<IVideoBackend>(), request, PresentVideo, 0, manual ? 0 : 2500);
            if(loaded&&!busy&&ReferenceEquals(card,games.SelectedItem)&&media.ActiveMusicPath!=null)RecordOutput("music",media.ActiveMusicPath,0,0);
            if (loaded && !busy && ReferenceEquals(card, games.SelectedItem) && media.LastError != null)
                status.Text = "预览暂不可用，静态浏览仍可用：" + media.LastError;
        }

        /// <summary>窗口失去前台立即停止音乐，重新获得前台按当前选择重算。</summary>
        private void ForegroundChanged(object sender, EventArgs args) {
            if(ownerWindow==null||!ownerWindow.IsActive)StopStartup(); UpdateInputPolling(); BeginPreview(false); if(ownerWindow==null||!ownerWindow.IsActive)StopCue();
            var page=(hubSurface.Content ?? contextContent.Content) as IPageActivity;
            if(page!=null)page.SetActive(!busy && ownerWindow!=null && ownerWindow.IsActive);
        }

        /// <summary>同一协调器服务影视页，仍服从用户音量、自动视频与前台策略。</summary>
        public async Task Preview(GameItem item, Action<IVideoSession> present)
        {
            await PreviewResolved(item,null,present);
        }
        /// <summary>每类素材分别应用手动覆盖，网络提供者不控制播放时序或声音。</summary>
        public async Task PreviewResolved(GameItem item, PreviewAsset fallback, Action<IVideoSession> present)
        {
            media.Cancel();
            if(!loaded || busy || modules==null || ownerWindow==null || !ownerWindow.IsActive)return;
            var manual=assets==null?null:assets.Find(item);
            var asset=manual??fallback;if(asset==null)return;
            if(manual!=null && fallback!=null)asset=new PreviewAsset {MusicPath=manual.MusicPath??fallback.MusicPath,VideoPath=manual.VideoPath??fallback.VideoPath,MusicVolume=manual.MusicVolume,VideoVolume=manual.VideoVolume,TrailerAudio=manual.TrailerAudio};
            await media.SelectAsync(modules.GetCapability<IMusicBackend>(),modules.GetCapability<IVideoBackend>(),
                new PreviewRequest {MusicPath=settings.music?asset.MusicPath:null,VideoPath=asset.VideoPath,MusicVolume=asset.MusicVolume*(settings.nightMode ? .25 : 1),VideoVolume=asset.VideoVolume*(settings.nightMode ? .25 : 1),TrailerAudio=settings.trailerAudio||asset.TrailerAudio,AutoVideo=settings.autoVideo},present,200,2500);
        }
        /// <summary>页面离开立即使所有待返回预览失效。</summary>
        public void Cancel() { media.Cancel(); }
        public IMediaPlaybackBackend Playback { get { return modules==null?null:modules.GetCapability<IMediaPlaybackBackend>(); } }

        /// <summary>首帧先上传再淡入；背景图从不因视频准备而被清除。</summary>
        private void PresentVideo(IVideoSession session)
        {
            frameTimer.Stop(); videoLayer.BeginAnimation(UIElement.OpacityProperty, null);
            videoLayer.Opacity = 0; videoLayer.Source = null; videoSession = session;
            ((Grid)layout.FindName("Chrome")).Visibility = showAll ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)layout.FindName("TrailerHints")).Visibility = Visibility.Collapsed;
            videoBytes = null; videoBitmap = null; frameVersion = 0;
            if (session == null) { if (manualTrailer && ownerWindow != null && ownerWindow.IsActive) games.Focus(); return; }
            if (session.Width <= 0 || session.Height <= 0 || session.Width > 3840 || session.Height > 2160) throw new InvalidOperationException("视频尺寸超出预览契约");
            if(session.Width<1920||session.Height<1080)throw new InvalidOperationException("游戏预告实际输出低于1080p，已回退壁纸");
            var selectedCard=games.SelectedItem as GameCard;var selectedAsset=assets==null?null:assets.Find(selectedCard==null?null:selectedCard.Item);
            RecordOutput("video",selectedAsset==null?null:selectedAsset.VideoPath,session.Width,session.Height);
            videoBytes = new byte[checked(session.Width * session.Height * 4)];
            videoBitmap = new WriteableBitmap(session.Width, session.Height, 96, 96, PixelFormats.Bgr32, null);
            if (!session.CopyFrame(videoBytes, ref frameVersion)) throw new InvalidOperationException("后端首帧尚不可读");
            videoBitmap.WritePixels(new Int32Rect(0, 0, session.Width, session.Height), videoBytes, session.Width * 4, 0);
            videoLayer.Source = videoBitmap;
            if (settings.reducedMotion) videoLayer.Opacity = 1;
            else videoLayer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds((double)layout.FindResource("VideoFadeMs"))));
            if (manualTrailer) { ((Grid)layout.FindName("Chrome")).Visibility = Visibility.Collapsed; ((TextBlock)layout.FindName("TrailerHints")).Visibility = Visibility.Visible; Focus(); }
            frameTimer.Start();
        }

        /// <summary>固定上限频率复制最新帧，不把解码事件无限排入 UI 队列。</summary>
        private void RenderVideo()
        {
            if (videoSession == null) return;
            try
            {
                if (videoSession.Ended) { media.EndVideo(); manualTrailer = false; return; }
                if (videoSession.CopyFrame(videoBytes, ref frameVersion))
                    videoBitmap.WritePixels(new Int32Rect(0, 0, videoSession.Width, videoSession.Height), videoBytes, videoSession.Width * 4, 0);
            }
            catch (Exception ex) { media.EndVideo(); status.Text = "视频已回退到背景图：" + ex.Message; }
        }

        /// <summary>主题上下文按内容引用匹配，不按标题关联。</summary>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            if (newContext == null) return;
            foreach (GameCard card in games.Items)
                if (card.Item.ItemId == newContext.Id.ToString("D")) { games.SelectedItem = card; break; }
        }

        /// <summary>离开时取消任务并释放外挂，重新进入可再次初始化。</summary>
        private void Release()
        {
            StopStartup();StopPageVideo();StopFocusParticles();if(pageDust!=null)pageDust.Visibility=Visibility.Collapsed;ThemePacks.Breathe(focusedCard,false);
            CancelAssetRefresh();
            ClearContextPage(); contextProvider = null; hubProvider=null;
            var card = games.SelectedItem as GameCard;
            if (loaded && libraryReady) SaveNavigation();
            loaded = false;
            var automatic=assets as IAutomaticAssets;if(automatic!=null)automatic.AssetChanged-=AutomaticAssetChanged;
            DetachInputWindow();
            wallpaperTimer.Stop();StopCue();summarySlot.Content=null;
            media.Cancel(); frameTimer.Stop(); controllerTimer.Stop();
            if (ownerWindow != null) { ownerWindow.Activated -= ForegroundChanged; ownerWindow.Deactivated -= ForegroundChanged; ownerWindow = null; }
            if (selection != null) { selection.Cancel(); selection.Dispose(); selection = null; }
            if (lifetime != null) { lifetime.Cancel(); lifetime.Dispose(); lifetime = null; }
            if (modules != null) { modules.Dispose(); modules = null; }
        }

        private bool OverlayOpen { get { return (keyboardPage != null && keyboardPage.Visibility == Visibility.Visible) || contextPage.Visibility == Visibility.Visible || ((Grid)layout.FindName("SettingsPage")).Visibility == Visibility.Visible || ((Grid)layout.FindName("DetailsPage")).Visibility == Visibility.Visible; } }

        /// <summary>首页取最近十项，全部游戏复用同一展示模型与焦点规则。</summary>
        private void ShowLibrary(bool all)
        {
            if (allCards == null) return;
            showAll = all;
            SetLibraryLayout(all);
            if(all && libraryReady){RestoreLibraryScroll();ApplyLibraryFilter();games.Focus();return;}
            var items = all ? allCards : (experience.homeSort=="name"?allCards.OrderBy(c=>c.Item.Title):allCards.OrderByDescending(c=>c.Item.LastActivity).ThenBy(c=>c.Item.Title)).Take(experience.homeCount).ToList();
            if(!all)items.Add(new GameCard(new GameItem {Provider="shell",SourceInstance="navigation",ItemId="library",Title="游戏库"},lifetime.Token));
            string previous = selectedId;
            games.ItemsSource = items;
            games.SelectedItem = items.FirstOrDefault(c => c.Item.ItemId == previous) ?? items.FirstOrDefault();
            games.Focus();
        }

        /// <summary>设置页打开时停止媒体，编辑值与已保存值分离。</summary>
        private void OpenSettings()
        {
            ClearContextPage();
            media.Cancel();
            manualTrailer = false;
            ((Grid)layout.FindName("DetailsPage")).Visibility = Visibility.Collapsed;
            ((TextBlock)layout.FindName("SettingsError")).Text = "";
            var layoutChoice = layout.FindName("LayoutChoice") as Button;
            if (layoutChoice != null) layoutChoice.Content = "布局：" + (ThemeComposer.ReadLayoutPreference(dataRoot) == "cinema" ? "影院" : "PS5ish");
            ((TextBlock)layout.FindName("PluginDiagnostic")).Text = modules == null ? "插件尚未加载" : String.Join("\n", modules.GetDiagnostics());
            if (assets != null && assets.Diagnostic != null) ((TextBlock)layout.FindName("PluginDiagnostic")).Text += "\n素材索引：" + assets.Diagnostic;
            if (contextProvider != null) ((TextBlock)layout.FindName("PluginDiagnostic")).Text += "\n" + contextProvider.Diagnostic;
            SetOption("OptionAutoVideo", settings.autoVideo); SetOption("OptionMusic", settings.music);
            SetOption("OptionTrailerAudio", settings.trailerAudio); SetOption("OptionReducedMotion", settings.reducedMotion);
            SetOption("OptionNightMode", settings.nightMode); SetOption("OptionController", settings.controller);
            soundOption.IsChecked=settings.uiSounds;musicSlider.Value=settings.musicVolume;soundSlider.Value=settings.soundVolume;
            ((Grid)layout.FindName("SettingsPage")).Visibility = Visibility.Visible;
            ThemePacks.Reveal((Grid)layout.FindName("SettingsPage"),layout,"PageFadeMs",settings.reducedMotion,1);
            OpenPs5Settings();RefreshPageBackground();
        }
        /// <summary>填充单个主题设置槽位。</summary>
        private void SetOption(string name, bool value) { ((CheckBox)layout.FindName(name)).IsChecked = value; }
        /// <summary>读取单个主题设置槽位。</summary>
        private bool GetOption(string name) { return ((CheckBox)layout.FindName(name)).IsChecked == true; }

        /// <summary>保存成功后才应用新策略，失败保留旧设置。</summary>
        private void SaveSettings()
        {
            var next = new ShellSettings { autoVideo = GetOption("OptionAutoVideo"), music = GetOption("OptionMusic"),
                trailerAudio = GetOption("OptionTrailerAudio"), reducedMotion = GetOption("OptionReducedMotion"),
                nightMode = GetOption("OptionNightMode"), controller = GetOption("OptionController"), uiSounds=soundOption.IsChecked==true,musicVolume=musicSlider.Value,soundVolume=soundSlider.Value,backgroundMusic=(mediaSettingsDraft??settings).backgroundMusic,startupAnimation=(mediaSettingsDraft??settings).startupAnimation,startupVideo=(mediaSettingsDraft??settings).startupVideo };
            var previous=ThemeExperience.Load(dataRoot);
            try { if(settingsDraft!=null)settingsDraft.Save(dataRoot); next.Save(dataRoot); experience=settingsDraft??experience;settingsDraft=null;settings = next; ApplyExperience(); CloseOverlay(); UpdateInputPolling(); }
            catch (Exception ex) { try{previous.Save(dataRoot);}catch{} status.Text="保存失败："+ex.Message; }
        }

        /// <summary>详情使用通用字段，没有成就插件时也可独立显示。</summary>
        private void OpenDetails()
        {
            ClearContextPage();
            var card = games.SelectedItem as GameCard;
            if (card == null) return;
            if(card.IsLibraryEntry){ShowLibrary(true);return;}
            media.Cancel();
            ((Grid)layout.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
            ((TextBlock)layout.FindName("DetailTitle")).Text = card.Title;
            ((TextBlock)layout.FindName("DetailInfo")).Text = (card.Item.IsInstalled ? "已安装" : "尚未安装") + "\n最近游玩：" + (card.Item.LastActivity.HasValue ? card.Item.LastActivity.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "暂无记录") + "\n" + (card.Item.Platform??"本地游戏") + " · 游玩 " + TimeSpan.FromSeconds(card.Item.Playtime).TotalHours.ToString("0.0") + " 小时\n\n" + (card.Item.Description??"");
            ((Grid)layout.FindName("DetailsPage")).Visibility = Visibility.Visible;
            detailStart.Content=card.Item.IsInstalled?"开始游戏":"安装游戏";detailStart.IsEnabled=start.IsEnabled;
            detailTrailer.IsEnabled=trailer.IsEnabled;detailFavorite.IsEnabled=source is IGameActions;detailFavorite.Content=card.Item.Favorite?"取消收藏":"收藏";
            var detailCover=layout.FindName("DetailCover") as Image;if(detailCover!=null)detailCover.Source=card.Cover;
            var detailScene=layout.FindName("DetailScene") as Image;if(detailScene!=null)detailScene.Source=background.Source;
            ThemePacks.Reveal((Grid)layout.FindName("DetailsPage"),layout,"PageFadeMs",settings.reducedMotion,1);
            ((TextBlock)layout.FindName("DetailInfo")).MaxHeight=215;
            ((TextBlock)layout.FindName("DetailInfo")).TextTrimming=TextTrimming.CharacterEllipsis;
            ((Button)layout.FindName("BackDetails")).Focus();
        }

        /// <summary>关闭覆盖层时恢复原卡片，不修改未保存设置。</summary>
        private void CloseOverlay()
        {
            if(ConfirmSettingsExit())return;
            ClearContextPage();
            bool editedSettings=settingsDraft!=null;
            if(editedSettings)settings=ShellSettings.Load(dataRoot);
            settingsDraft=null;mediaSettingsDraft=null;StopCue();ApplyExperience();
            ((Grid)layout.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
            ((Grid)layout.FindName("DetailsPage")).Visibility = Visibility.Collapsed;
            if(editedSettings)ShowLibrary(showAll);else games.Focus(); BeginPreview(false);
        }
        /// <summary>手柄和键盘共用页面返回语义。</summary>
        private void GoBack()
        {
            if (hubSurface.Visibility == Visibility.Visible) { CloseHubService(); return; }
            if (OverlayOpen) CloseOverlay();
            else if (manualTrailer) { manualTrailer = false; BeginPreview(false); games.Focus(); }
            else if (showAll) ShowLibrary(false);
            PlayCue("back");
        }
        /// <summary>只在本界面可见、前台且不在游戏会话中时轮询手柄。</summary>
        private void UpdateInputPolling()
        {
            if (loaded && settings.controller && !busy && !maintenanceActive && IsVisible && ownerWindow != null && ownerWindow.IsActive) controllerTimer.Start();
            else { controllerTimer.Stop(); sdkPressed.Clear(); sdkInputActive = false; controllerInput.Reset(); }
        }
        /// <summary>使用 Playnite 的公开设备事件支持 DS5；收到 SDK 输入后停止 XInput 轮询，避免双重触发。</summary>
        public void ReceiveController(Playnite.SDK.Events.ControllerInput input, Playnite.SDK.Events.ControllerInputState state)
        {
            RecordInput(input.ToString(),state.ToString());
            if (!loaded || busy || maintenanceActive || !settings.controller || !IsVisible || ownerWindow == null || !ownerWindow.IsActive) return;
            sdkInputActive = true;
            if (state == Playnite.SDK.Events.ControllerInputState.Pressed) sdkPressed.Add(input); else sdkPressed.Remove(input);
            controllerInput.Process(SdkButtons(), DateTime.UtcNow);
        }
        /// <summary>设备断开时清除保持按键，防止方向持续重复。</summary>
        public void ClearController() { sdkPressed.Clear(); sdkInputActive = false; controllerInput.Reset(); }
        /// <summary>合并摇杆与十字键状态，一种输入释放不会清除另一种仍按住的方向。</summary>
        private ushort SdkButtons()
        {
            ushort result = 0;
            foreach (var input in sdkPressed) result |= ControllerInput.MapSdk(input);
            return result;
        }
        /// <summary>将设备动作映射为宿主命令，不向游戏或系统注入输入。</summary>
        private void HandleController(string action)
        {
            if (busy || !loaded || ownerWindow == null || !ownerWindow.IsActive) return;
            if(startupActive){if(action=="accept"||action=="back")StopStartup();return;}
            var pageInput=(hubSurface.Content ?? contextContent.Content) as IPageInput;
            if(hubSurface.Visibility==Visibility.Visible) { if(action=="settings" || pageInput==null || !pageInput.HandleInput(action)) CloseHubService(); return; }
            if(contextPage.Visibility==Visibility.Visible && pageInput!=null && pageInput.HandleInput(action)){PlayCue(action=="back"?"back":action=="accept"?"confirm":"move");return;}
            if (libraryReady && keyboardPage.Visibility == Visibility.Visible && action == "back") { NavigateLibrary(action); return; }
            if (action == "back") { GoBack(); return; }
            if (action == "settings") { OpenGameMenu(); return; }
            if (manualTrailer) { if (action == "accept" && videoSession != null) { paused = !paused; videoSession.SetPaused(paused); } return; }
            if (action == "trailer" && !OverlayOpen) { BeginPreview(true); return; }
            if (libraryReady && NavigateLibrary(action)) return;
            var focused = Keyboard.FocusedElement as UIElement;
            // 方向键在音量滑块上调整值；上下仍沿设置项移动。
            var slider=focused as Slider;
            if(slider!=null && (action=="left"||action=="right")){
                slider.Value=Math.Max(slider.Minimum,Math.Min(slider.Maximum,slider.Value+(action=="left"?-slider.SmallChange:slider.SmallChange)));return;
            }
            if (action == "accept")
            {
                var button = focused as Button; var option = focused as CheckBox;
                if (button != null && button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (option != null) option.IsChecked = option.IsChecked != true;
                else if (!OverlayOpen){OpenDetails();PlayCue("confirm");}
            }
            else if (!OverlayOpen && (action == "left" || action == "right"))
            {
                if (games.Items.Count == 0) return;
                games.SelectedIndex = Math.Max(0, Math.Min(games.Items.Count - 1, games.SelectedIndex + (action == "left" ? -1 : 1)));
                if (games.SelectedItem != null) games.ScrollIntoView(games.SelectedItem); games.Focus();
            }
            else if (focused != null) focused.MoveFocus(new TraversalRequest(action == "up" || action == "left" ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }

        /// <summary>只有已声明视频素材且视频能力可用时才允许主动播放。</summary>
        private void UpdateTrailerAvailability()
        {
            var card = games.SelectedItem as GameCard;
            var asset = assets == null ? null : assets.Find(card == null ? null : card.Item);
            trailer.IsEnabled = !busy && asset != null && !String.IsNullOrWhiteSpace(asset.VideoPath) && modules != null && modules.GetCapability<IVideoBackend>() != null;
            var contextAction = layout.FindName("ContextPageAction") as Button;
            if (contextAction != null)
            {
                bool available = false;
                try { available = layout is Panel && contextProvider != null && contextProvider.CanDisplay(card == null ? null : card.Item); }
                catch (Exception ex) { status.Text = "可选页面不可用：" + ex.GetBaseException().Message; }
                contextAction.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                contextAction.IsEnabled = available && !busy;
                if (available) contextAction.Content = contextProvider.Title;
            }
        }

        /// <summary>只在用户请求时创建可选页面，失败回到原详情，不影响游戏启动。</summary>
        private void OpenContextPage()
        {
            var card = games.SelectedItem as GameCard;
            if (busy || card == null || contextProvider == null) return;
            try
            {
                var page = contextProvider.CreateView(card.Item) as FrameworkElement;
                if (page == null) throw new InvalidOperationException("插件未提供可显示的页面");
                media.Cancel(); manualTrailer = false;
                ((Grid)layout.FindName("DetailsPage")).Visibility = Visibility.Collapsed;
                ((Grid)layout.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
                var viewPreferences=page as IViewPreferences;if(viewPreferences!=null)viewPreferences.SetReducedMotion(settings.reducedMotion);
                contextContent.Content = page; contextTitle.Text = contextProvider.Title + " · " + card.Title;
                contextPage.Visibility = Visibility.Visible;RefreshPageBackground();
                contextPage.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            catch (Exception ex)
            {
                string message = "页面暂不可用：" + ex.GetBaseException().Message;
                status.Text = message;
                ((TextBlock)layout.FindName("DetailInfo")).Text += "\n" + message;
            }
        }

        /// <summary>移除内容使第三方控件收到 Unloaded，不把旧上下文留在隐藏页面。</summary>
        private void ClearContextPage()
        {
            if (contextPage == null) return;
            CloseHubService();
            contextContent.Content = null; contextPage.Visibility = Visibility.Collapsed;
        }
    }
}






