using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModularShell;

namespace EmbyBrowser
{
    /// <summary>影视作为可移除能力页存在，初始化阶段不联网或创建界面。</summary>
    public sealed class Module : IModule, IConfiguredModule, IHubViewProvider, IOptionalHubSettings, IHubAppearance
    {
        private string root;
        private IPageMedia media;
        public string Title { get { return "Emby"; } }
        public string AccentColor { get { return "#52B54B"; } }
        public string IconGeometry { get { return "M 50,16 L 84,50 50,84 16,50 Z M 42,34 L 42,66 65,50 Z"; } }
        public bool Enabled {get{return WebLibrary.IsEnabled(root);}}
        /// <summary>配置界面由影视组件提供，不在宿主存储服务器或账号。</summary>
        public object CreateSettingsView(){return WebLibrary.CreateSettingsView(root);}
        /// <summary>使用 SDK 提供的用户目录，保持账号与安装包分离。</summary>
        public void Configure(ModuleContext context) { root = context.UserDataRoot; media=context.Media; }
        /// <summary>不让未配置或离线服务器影响游戏首页启动。</summary>
        public void Initialize() { }
        /// <summary>首页暂不预取用户媒体，进入影视后才发请求。</summary>
        public IEnumerable<Contribution> GetSections() { yield break; }
        /// <summary>每次打开创建独立请求生命周期。</summary>
        public object CreateView() { return new WebLibrary(root); }
        /// <summary>页面随可视树卸载释放请求，模块不持有后台任务。</summary>
        public void Dispose() { }
    }

    public sealed class PosterCard
    {
        public MediaItem Item;
        public string Label { get; set; }
        public ImageSource Poster { get; set; }
    }

    /// <summary>原生 WPF 海报墙与详情；请求代次隔离，离开后不回流旧内容。</summary>
    public sealed class Browser : Grid, IPageInput, IPageActivity
    {
        private readonly Client client;
        private readonly string diagnosticRoot;
        private readonly ListBox list = new ListBox();
        private readonly TextBlock message = new TextBlock { Foreground=Brushes.LightGray, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,10,0,10) };
        private readonly TextBlock detail = new TextBlock { Foreground=Brushes.White, FontSize=20, TextWrapping=TextWrapping.Wrap };
        private readonly Grid details = new Grid { Background=new SolidColorBrush(Color.FromRgb(8,16,29)), Visibility=Visibility.Collapsed };
        private readonly Button previous, next;
        private CancellationTokenSource pending;
        private CancellationTokenSource previewPending;
        private readonly SemaphoreSlim images = new SemaphoreSlim(4);
        private string section="resume", parent;
        private int offset;
        private bool active;
        private readonly IPageMedia media;
        private readonly TextBox search=new TextBox {Width=190,MaxLength=120,FontSize=18,Margin=new Thickness(8)};
        private string kind;
        private readonly Image preview=new Image {Opacity=.3,Stretch=Stretch.Uniform};
        private readonly Image picture=new Image {Stretch=Stretch.Uniform};
        private readonly Grid playerPanel=new Grid {Visibility=Visibility.Collapsed,Background=Brushes.Black};
        private readonly TextBlock playerStatus=new TextBlock {Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};
        private readonly DispatcherTimer frames=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(33)};
        private readonly DispatcherTimer progress=new DispatcherTimer {Interval=TimeSpan.FromSeconds(10)};
        private IVideoSession previewSession;
        private PlaybackController playback;
        private WriteableBitmap bitmap;
        private byte[] pixels;
        private long version;
        private bool reporting,foreground=true;
        private string pendingReport;
        private string detailId;
        private readonly Button play;

        /// <summary>建立消费界面，按钮和海报均可通过键盘焦点操作。</summary>
        public Browser(string root) : this(root,null) { }
        /// <summary>页面只消费共享媒体服务，插件不引用宿主核心程序集。</summary>
        public Browser(string root,IPageMedia media)
        {
            this.media=media;diagnosticRoot=root;
            SetRowSpan(preview,3);preview.IsHitTestVisible=false;Children.Add(preview);
            RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto}); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var toolbar = new WrapPanel();
            string[] ids = {"resume","recent","latest","library"}, labels={"继续观看","最近观看","最近添加","影视库"};
            for (int i=0;i<ids.Length;i++)
            {
                string selected=ids[i]; var button=Button(labels[i]);
                button.Click += (s,e) => { section=selected; parent=null; offset=0; LoadPage(); }; toolbar.Children.Add(button);
            }
            var selectedDetail=Button("打开所选"); selectedDetail.Click+=(s,e)=>OpenSelected(); toolbar.Children.Add(selectedDetail);
            // 搜索外观由可替换主题提供，影视模块只负责查询。
            search.Tag="搜索影视";search.SetResourceReference(FrameworkElement.StyleProperty,"Ps5SearchBoxStyle");
            toolbar.Children.Add(search);var find=Button("搜索");toolbar.Children.Add(find);
            find.Click+=(s,e)=>{section="library";parent=null;offset=0;LoadPage();};
            search.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Enter){section="library";parent=null;offset=0;LoadPage();e.Handled=true;}};
            var filter=Button("类型：全部");toolbar.Children.Add(filter);
            filter.Click+=(s,e)=>{kind=kind==null?"Movie":kind=="Movie"?"Series":kind=="Series"?"Episode":null;filter.Content="类型："+(kind??"全部");section="library";parent=null;offset=0;LoadPage();};
            Children.Add(toolbar);
            list.Background=Brushes.Transparent; list.BorderThickness=new Thickness(0); list.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
            // 30 项一页且图片并发上限 4，避免整库海报同时解码或联网。
            list.ItemsPanel=(ItemsPanelTemplate)XamlReader.Parse("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><WrapPanel/></ItemsPanelTemplate>");
            list.ItemTemplate=(DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Width='164' Margin='4'><Border Height='218' Background='#24334B' CornerRadius='8'><Image Source='{Binding Poster}' Stretch='Uniform'/></Border><TextBlock Text='{Binding Label}' Foreground='White' TextWrapping='Wrap' TextTrimming='CharacterEllipsis' MaxHeight='48' FontSize='16' Margin='0,6,0,6'/></StackPanel></DataTemplate>");
            list.MouseDoubleClick+=(s,e)=>OpenSelected();
            list.PreviewKeyDown+=(s,e)=>{ if(e.Key==Key.Enter) { OpenSelected(); e.Handled=true; } };
            SetRow(list,1); Children.Add(list);
            var footer=new DockPanel(); previous=Button("上一页"); next=Button("下一页");
            DockPanel.SetDock(previous,Dock.Right); DockPanel.SetDock(next,Dock.Right); footer.Children.Add(next); footer.Children.Add(previous); footer.Children.Add(message); SetRow(footer,2); Children.Add(footer);
            previous.Click+=(s,e)=>{offset=Math.Max(0,offset-Client.PageSize);LoadPage();}; next.Click+=(s,e)=>{offset+=Client.PageSize;LoadPage();};
            details.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto}); details.RowDefinitions.Add(new RowDefinition());
            var back=Button("返回海报墙"); back.HorizontalAlignment=HorizontalAlignment.Left; back.Click+=(s,e)=>CloseDetail();
            var detailActions=new StackPanel{Orientation=Orientation.Horizontal};detailActions.Children.Add(back);
            play=Button("播放 / 续播正片");play.IsEnabled=false;play.Click+=(s,e)=>StartPlayback();detailActions.Children.Add(play);details.Children.Add(detailActions);
            var scroll=new ScrollViewer {Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}; SetRow(scroll,1); details.Children.Add(scroll); SetRowSpan(details,3); Children.Add(details);
            BuildPlayer();
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){if(playerPanel.Visibility==Visibility.Visible){StopPlayback();e.Handled=true;}else if(details.Visibility==Visibility.Visible){CloseDetail();e.Handled=true;}}};
            try { client=new Client(root); }
            catch { message.Text="尚未建立可用连接，请在设置 → 额外组件 · 影视库中登录。"; toolbar.IsEnabled=false; next.IsEnabled=false; previous.IsEnabled=false; }
            Loaded+=(s,e)=>{if(active)return;active=true; if(client!=null)LoadPage();};
            Unloaded+=(s,e)=>{active=false;CancelPending();if(media!=null)media.Cancel();StopPlayback();frames.Stop();progress.Stop();};
            list.SelectionChanged+=(s,e)=>SelectPreview();
            frames.Tick+=(s,e)=>Render();progress.Tick+=(s,e)=>ReportProgress("TimeUpdate");
        }

        /// <summary>复用宿主稳定样式，能力逻辑不依赖具体配色。</summary>
        private static Button Button(string title) { var value=new Button {Content=title,Margin=new Thickness(0,0,10,10),Padding=new Thickness(12,8,12,8)}; value.SetResourceReference(FrameworkElement.StyleProperty,"ActionStyle"); return value; }
        /// <summary>换页、离开和进入详情均取消上一组网络请求。</summary>
        private void CancelPending() { if(pending!=null){pending.Cancel();pending.Dispose();pending=null;} CancelPreviewRequest(); }
        /// <summary>选择素材获取与页面列表请求独立取消，退出和失焦均中止下载。</summary>
        private void CancelPreviewRequest(){if(previewPending!=null){previewPending.Cancel();previewPending.Dispose();previewPending=null;}}

        /// <summary>每次页面请求持有独立取消标记，迟到结果不能更新当前页。</summary>
        private async void LoadPage()
        {
            if(!active || client==null)return;
            CancelPending(); pending=new CancellationTokenSource(); var token=pending.Token;
            if(media!=null)media.Cancel();previewSession=null;
            details.Visibility=Visibility.Collapsed; list.ItemsSource=null; message.Text="正在读取影视内容…"; next.IsEnabled=false; previous.IsEnabled=false;
            try
            {
                var page=await client.Browse(section,offset,parent,token,search.Text,kind);
                if(token.IsCancellationRequested || !active)return;
                var cards=new List<PosterCard>(); foreach(var item in page.Items) cards.Add(new PosterCard {Item=item,Label=String.IsNullOrEmpty(item.SeriesName)?item.Name:item.SeriesName+" · "+item.Name});
                list.ItemsSource=cards; list.SelectedIndex=cards.Count>0?0:-1;
                message.Text=(page.Offline?"离线缓存 · ":"已连接 · ")+"本页 "+cards.Count+" 项"+(section=="latest" || page.Total<0?"":" / 共 "+page.Total+" 项");
                if(cards.Count==0)message.Text+=" · 当前分区暂无内容";
                previous.IsEnabled=offset>0; next.IsEnabled=page.HasMore;
                foreach(var card in cards) LoadPoster(card,token);
                list.Focus();
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(!token.IsCancellationRequested && active){WriteDiagnostic(ex);message.Text=SafeError(ex);} }
        }

        /// <summary>异步缩略图有并发上限，缺图只保留可识别的文字海报卡。</summary>
        private async void LoadPoster(PosterCard card,CancellationToken token)
        {
            bool entered=false;
            try
            {
                await images.WaitAsync(token); entered=true;
                byte[] bytes=await client.Poster(card.Item.Id,token);
                var bitmap=await Task.Run(()=>{using(var stream=new MemoryStream(bytes)){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=300;image.StreamSource=stream;image.EndInit();image.Freeze();return image;}});
                if(token.IsCancellationRequested || !active)return;
                card.Poster=bitmap; list.Items.Refresh();
            }
            catch { /* 缺图或取消不反复弹错，也不触发额外重试。 */ }
            finally { if(entered)images.Release(); }
        }

        /// <summary>剧集进入真实子项，电影和单集读取详情；不调用播放/已看接口。</summary>
        private async void OpenSelected()
        {
            var card=list.SelectedItem as PosterCard; if(card==null || !active)return;
            if(card.Item.Type=="Series" || card.Item.Type=="Season") {parent=card.Item.Id;section="children";offset=0;LoadPage();return;}
            CancelPending(); pending=new CancellationTokenSource(); var token=pending.Token;
            if(media!=null)media.Cancel();detailId=null;play.IsEnabled=false;
            details.Visibility=Visibility.Visible; detail.Text="正在读取详情…";
            try
            {
                var item=await client.Detail(card.Item.Id,token);
                if(token.IsCancellationRequested || !active)return;
                detailId=item.Id;play.IsEnabled=!item.Offline && media!=null && media.Playback!=null;
                detail.Text=(item.Offline?"离线缓存 · 进度可能已变化\n":"")+item.Name+"\n"+(item.ProductionYear.HasValue?item.ProductionYear.Value.ToString():"")+" · "+item.Type+"\n\n"+(item.Overview??"暂无简介")+"\n\n"+"续播位置："+TimeSpan.FromTicks(item.UserData==null?0:Math.Max(0,item.UserData.PlaybackPositionTicks)).ToString(@"hh\:mm\:ss")+"\n"+(media==null || media.Playback==null?"未安装正片播放后端":media.Playback.Capabilities);
                details.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) {if(!token.IsCancellationRequested && active)detail.Text=SafeError(ex);}
        }
        /// <summary>退出详情取消迟到结果并恢复海报焦点。</summary>
        private void CloseDetail()
        {
            CancelPending();details.Visibility=Visibility.Collapsed;list.Focus();
            // 进入详情取消了缩略图请求，返回后仅补齐缺图。
            if(!active)return;pending=new CancellationTokenSource();
            foreach(PosterCard card in list.Items)if(card.Poster==null)LoadPoster(card,pending.Token);
            SelectPreview();
        }
        /// <summary>十字键在海报网格按行列移动；返回优先关闭详情或返回影视库。</summary>
        public bool HandleInput(string action)
        {
            if(playerPanel.Visibility==Visibility.Visible){if(action=="back"){StopPlayback();return true;}return false;}
            if(action=="back")
            {
                if(details.Visibility==Visibility.Visible){CloseDetail();return true;}
                if(section=="children"){section="library";parent=null;offset=0;LoadPage();return true;}
                return false;
            }
            if(!list.IsKeyboardFocusWithin || details.Visibility==Visibility.Visible)return false;
            if(action=="accept"){OpenSelected();return true;}
            int columns=Math.Max(1,(int)(list.ActualWidth/182));
            int delta=action=="left"?-1:action=="right"?1:action=="up"?-columns:action=="down"?columns:0;
            if(delta==0)return false;
            if(list.Items.Count>0){list.SelectedIndex=Math.Max(0,Math.Min(list.Items.Count-1,list.SelectedIndex+delta));list.ScrollIntoView(list.SelectedItem);}
            return true;
        }
        /// <summary>只输出固定诊断类型，第三方响应和敏感头不进入界面错误文本。</summary>
        private static string SafeError(Exception ex) { return ex is UnauthorizedAccessException?"登录失效或没有访问权限，请重新登录。":"影视内容暂不可用（"+ex.GetBaseException().GetType().Name+"），请检查连接后重试。"; }
        /// <summary>仅记录异常类型与调用栈，不记录响应、URL、账号或令牌。</summary>
        private void WriteDiagnostic(Exception error){try{var text=new System.Text.StringBuilder();for(var ex=error;ex!=null;ex=ex.InnerException)text.AppendLine(ex.GetType().FullName).AppendLine(ex.StackTrace);Directory.CreateDirectory(Path.Combine(diagnosticRoot,"emby"));File.WriteAllText(Path.Combine(diagnosticRoot,"emby","last-error.txt"),text.ToString());}catch{}}

        /// <summary>本地素材沿完整 Emby 引用查找，使用宿主的取消和声音协调。</summary>
        private async void SelectPreview()
        {
            if(media==null)return;media.Cancel();previewSession=null;preview.Source=null;
            CancelPreviewRequest();
            var card=list.SelectedItem as PosterCard;
            if(!active || !foreground || card==null || playback!=null || details.Visibility==Visibility.Visible)return;
            previewPending=new CancellationTokenSource();var token=previewPending.Token;
            var item=new GameItem {Provider="emby",SourceInstance=client.SourceInstance,ItemId=card.Item.Id,Title=card.Item.Name};
            Action<IVideoSession> present=session=>{
                previewSession=session;bitmap=null;pixels=null;version=0;if(session==null)preview.Source=null;else frames.Start();
            };
            try {
                await media.Preview(item,present);await Task.Delay(600,token);
                var fallback=await client.PreviewAssets(card.Item.Id,token);token.ThrowIfCancellationRequested();
                if(fallback!=null && active && foreground && ReferenceEquals(card,list.SelectedItem))await media.PreviewResolved(item,fallback,present);
            }catch(OperationCanceledException){}catch{if(!token.IsCancellationRequested)message.Text="素材预览不可用，仍可浏览影视。";}
        }
        /// <summary>播放器控制保持在原生页面；输入焦点限定于当前播放器控件。</summary>
        private void BuildPlayer()
        {
            playerPanel.RowDefinitions.Add(new RowDefinition());playerPanel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            playerPanel.Children.Add(picture);var controls=new WrapPanel{Margin=new Thickness(16)};SetRow(controls,1);playerPanel.Children.Add(controls);
            var pause=Button("暂停 / 继续");pause.Click+=(s,e)=>{if(playback!=null){playback.Pause(!playback.Paused);ReportProgress(playback.Paused?"Pause":"Unpause");}};controls.Children.Add(pause);
            foreach(int seconds in new[]{-30,30}){int delta=seconds;var seek=Button(delta<0?"后退 30 秒":"前进 30 秒");seek.Click+=(s,e)=>{try{if(playback!=null && playback.Session!=null){playback.Session.Seek(playback.Session.PositionTicks+TimeSpan.FromSeconds(delta).Ticks);ReportProgress("TimeUpdate");}}catch{playerStatus.Text="当前片源不能定位。";}};controls.Children.Add(seek);}
            foreach(bool subtitle in new[]{false,true}){bool sub=subtitle;var track=Button(sub?"切换字幕":"切换音轨");int index=0;track.Click+=(s,e)=>{try{if(playback==null || playback.Session==null)return;var tracks=sub?playback.Session.SubtitleTracks:playback.Session.AudioTracks;if(tracks.Length==0){playerStatus.Text="无可切换轨道";return;}var selected=tracks[index++%tracks.Length];if(sub)playback.Session.SelectSubtitle(selected.Id);else playback.Session.SelectAudio(selected.Id);playerStatus.Text=selected.Name;ReportProgress(sub?"SubtitleTrackChange":"AudioTrackChange");}catch{playerStatus.Text="轨道切换失败";}};controls.Children.Add(track);}
            var stop=Button("停止并返回");stop.Click+=(s,e)=>StopPlayback();controls.Children.Add(stop);controls.Children.Add(playerStatus);
            KeyboardNavigation.SetTabNavigation(playerPanel,KeyboardNavigationMode.Cycle);SetRowSpan(playerPanel,3);Children.Add(playerPanel);
        }
        /// <summary>只有显式正片操作才创建写回会话；不自动播放用户影视。</summary>
        private async void StartPlayback()
        {
            if(playback!=null || detailId==null || media==null || media.Playback==null)return;
            media.Cancel();CancelPending();pending=new CancellationTokenSource();var token=pending.Token;
            var current=new PlaybackController(client,media.Playback);playback=current;
            playerPanel.Visibility=Visibility.Visible;playerStatus.Text="正在准备正片…";picture.Source=null;bitmap=null;version=0;
            bool failed=false;
            try{await current.Start(detailId,token);if(!active || !ReferenceEquals(playback,current)){await current.Stop();return;}frames.Start();progress.Start();playerStatus.Text="正片播放中";playerPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));}
            catch{failed=true;}
            if(failed){await current.Stop();if(ReferenceEquals(playback,current)){playback=null;playerStatus.Text="正片未能开始，请返回后检查网络和片源能力。";}}
        }
        /// <summary>停止立即撤销播放请求与画面，最终同步失败明确显示，不自动重放旧进度。</summary>
        private async void StopPlayback()
        {
            CancelPending();var current=playback;playback=null;progress.Stop();frames.Stop();picture.Source=null;bitmap=null;previewSession=null;
            playerPanel.Visibility=Visibility.Collapsed;
            if(current!=null){await current.Stop();if(current.SyncFailed)detail.Text+="\n观看状态未确认同步；恢复连接后重新打开详情，不会自动覆盖服务器进度。";}
            if(active)play.Focus();
        }
        /// <summary>报告有节流且不堆积任务，故障暂停正片并提示用户重新进入。</summary>
        private async void ReportProgress(string reason)
        {
            if(playback==null)return;
            if(reporting){if(reason!="TimeUpdate")pendingReport=reason;return;}
            reporting=true;var current=playback;
            try{
                do { pendingReport=null;await current.Progress(reason);reason=pendingReport; }
                while(reason!=null && ReferenceEquals(current,playback) && !current.SyncFailed);
            }catch{if(ReferenceEquals(current,playback))playerStatus.Text="同步连接中断，已暂停；请停止并重新打开，旧进度不会自动重放。";}
            finally{reporting=false;}
        }
        /// <summary>一份像素缓冲服务当前会话，不为每帧分配数组。</summary>
        private void Render()
        {
            var session=playback==null?previewSession:playback.Session;if(session==null)return;
            try{
                if(session.Ended){if(playback!=null)StopPlayback();else{if(media!=null)media.Cancel();frames.Stop();}return;}
                if(bitmap==null){pixels=new byte[checked(session.Width*session.Height*4)];bitmap=new WriteableBitmap(session.Width,session.Height,96,96,PixelFormats.Bgr32,null);version=0;}
                if(session.CopyFrame(pixels,ref version)){bitmap.WritePixels(new Int32Rect(0,0,session.Width,session.Height),pixels,session.Width*4,0);if(playback==null)preview.Source=bitmap;else picture.Source=bitmap;}
            }catch{if(playback!=null)StopPlayback();else if(media!=null)media.Cancel();}
        }
        /// <summary>后台立即停止预览并暂停正片；重新获得前台不会自行恢复正片声音。</summary>
        public void SetActive(bool value){foreground=value;if(!value){CancelPreviewRequest();if(media!=null)media.Cancel();if(playback!=null){playback.Pause(true);ReportProgress("Pause");}}else if(playback==null)SelectPreview();}
    }
}
