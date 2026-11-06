using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;

namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl
    {
        private Grid libraryPage, keyboardPage;
        private Grid homeGrid;
        private ItemsPanelTemplate homePanel;
        private TextBlock libraryCount;
        private TextBox searchBox;
        private Button filterButton, platformButton, sortButton;
        private Button installedTab, favoriteTab,libraryTools,librarySearch;
        private StackPanel libraryFilters;
        private Button keyboardReturn;
        private Button detailStart,detailTrailer,detailFavorite;
        private string filter, platform;
        private bool sortByName;
        private bool libraryReady;
        private bool pendingLibraryRefresh;
        private double libraryScrollOffset;
        private bool restoringScroll;
        private ContentControl summarySlot;
        private Image gameLogo;
        private TextBlock logoFallback;
        private Point lastHoverPoint=new Point(Double.NaN,Double.NaN);
        private FrameworkElement focusedCard;
        private int hoverGeneration;
        private DateTime hoverBlockedUntil;
        /// <summary>稳定停留后才选中；位移动画期间阻止连续撞上经过鼠标的封面。</summary>
        private async void SelectHovered(ListBoxItem container)
        {
            int generation=++hoverGeneration;if(container==null||container.IsSelected||DateTime.UtcNow<hoverBlockedUntil)return;
            await System.Threading.Tasks.Task.Delay(170);
            if(!loaded||generation!=hoverGeneration||!container.IsMouseOver)return;
            hoverBlockedUntil=DateTime.UtcNow.AddMilliseconds(350);container.IsSelected=true;
        }

        /// <summary>在容器生成后应用焦点过渡，过期选择不影响新卡片。</summary>
        private void AnimateSelection()
        {
            StopFocusParticles();ThemePacks.Breathe(focusedCard,false);
            if(showAll)ThemePacks.FocusCard(focusedCard,false,settings.reducedMotion);
            var selected=games.SelectedItem;
            Dispatcher.BeginInvoke(new Action(()=>{
                if(!loaded||games.SelectedItem!=selected)return;
                focusedCard=games.ItemContainerGenerator.ContainerFromItem(selected) as FrameworkElement;
                ThemePacks.Breathe(focusedCard,experience.focusBreathing&&!settings.reducedMotion);
                StartFocusParticles();
                if(showAll)ThemePacks.FocusCard(focusedCard,true,settings.reducedMotion,1.035);
                else {var carousel=FindCarousel(games);if(carousel!=null){carousel.ReducedMotion=settings.reducedMotion;carousel.InvalidateArrange();}}
            }),System.Windows.Threading.DispatcherPriority.Loaded);
        }
        /// <summary>定位首页横条面板，库页仍使用独立虚拟化排列。</summary>
        private static HomeCarouselPanel FindCarousel(DependencyObject parent)
        {
            var panel=parent as HomeCarouselPanel;if(panel!=null)return panel;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindCarousel(VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}
            return null;
        }
        private readonly Queue<string> inputTrace=new Queue<string>();
        private HwndSource inputWindow;

        /// <summary>只在本控件所处窗口过滤 Playnite 合成方向键，避免与 SDK 导航重复。</summary>
        private IntPtr FilterNavigationMessage(IntPtr hwnd,int message,IntPtr key,IntPtr flags,ref bool handled)
        {
            if(loaded && !busy && !maintenanceActive && IsVisible && settings.controller && ownerWindow!=null && ownerWindow.IsActive
                && ControllerInput.IsSyntheticNavigationMessage(message,key,flags)) handled=true;
            return IntPtr.Zero;
        }
        /// <summary>安装窗口级输入过滤，独立模拟窗口不改变键盘路径。</summary>
        private void AttachInputWindow()
        {
            if(!(source is PlayniteSource)||ownerWindow==null||inputWindow!=null)return;
            inputWindow=PresentationSource.FromVisual(ownerWindow) as HwndSource;
            if(inputWindow!=null)inputWindow.AddHook(FilterNavigationMessage);
        }
        /// <summary>卸载后撤销过滤，不影响 Playnite 的其他视图。</summary>
        private void DetachInputWindow(){if(inputWindow!=null){inputWindow.RemoveHook(FilterNavigationMessage);inputWindow=null;}}

        /// <summary>数据库就绪与平台同步完成时刷新，保留原选择和查询。</summary>
        public void RefreshLibrary()
        {
            if(!loaded||lifetime==null)return;
            // 覆盖页正在编辑或浏览奖杯时延后应用，避免数据事件夺取焦点并关闭页面。
            if(OverlayOpen){pendingLibraryRefresh=true;return;}pendingLibraryRefresh=false;
            var incoming=source.Read();
            // 数据库批量更新常包含未变化条目；逐字段相同则保留当前封面、媒体与焦点。
            var properties=typeof(GameItem).GetProperties();
            if(incoming.Count==allCards.Count&&incoming.Select((item,index)=>properties.All(property=>Object.Equals(property.GetValue(item,null),property.GetValue(allCards[index].Item,null)))).All(equal=>equal))return;
            var previousFocus=Keyboard.FocusedElement as UIElement;
            allCards=incoming.Select(g=>new GameCard(g,lifetime.Token)).ToList();
            var automatic=assets as IAutomaticAssets;if(automatic!=null)automatic.Synchronize(allCards.Select(c=>c.Item).ToList());
            ShowLibrary(showAll);
            try{using(var file=System.IO.File.Create(System.IO.Path.Combine(dataRoot,"game-catalog.json")))new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<GameItem>)).WriteObject(file,allCards.Select(c=>c.Item).ToList());}catch{}
            if(previousFocus!=null&&previousFocus.IsVisible)previousFocus.Focus();
        }

        /// <summary>保留最近 128 次设备事件，方便实机确认事件、焦点与消费门控。</summary>
        private void RecordInput(string input,string state)
        {
            inputTrace.Enqueue(DateTime.UtcNow.ToString("o")+" "+input+" "+state+" loaded="+loaded+" busy="+busy+" active="+(ownerWindow!=null&&ownerWindow.IsActive)+" focus="+(Keyboard.FocusedElement==null?"none":Keyboard.FocusedElement.GetType().Name));
            while(inputTrace.Count>128)inputTrace.Dequeue();
            try{System.IO.File.WriteAllLines(System.IO.Path.Combine(dataRoot,"controller-trace.log"),inputTrace);}catch{}
        }

        /// <summary>建立真实库页及搜索键盘；旧主题仍通过通用插槽获得完整功能。</summary>
        private void InitializeLibrary()
        {
            homeGrid=(Grid)games.Parent; homePanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(HomeCarouselPanel)));
            filter=navigation.filter??"全部";platform=navigation.platform??"所有平台";sortByName=navigation.sortByName;
            libraryScrollOffset=Double.IsNaN(navigation.scrollOffset)||Double.IsInfinity(navigation.scrollOffset)?0:Math.Max(0,navigation.scrollOffset);
            games.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((s,e)=>{if(showAll&&!restoringScroll)libraryScrollOffset=e.VerticalOffset;}));
            libraryPage=new Grid { Visibility=Visibility.Collapsed, Background=Brushes.Transparent, Margin=new Thickness(0) };
            libraryPage.RowDefinitions.Add(new RowDefinition {Height=new GridLength(82)});
            libraryPage.RowDefinitions.Add(new RowDefinition {Height=new GridLength(0)});
            libraryPage.RowDefinitions.Add(new RowDefinition {Height=new GridLength(78)});
            libraryPage.RowDefinitions.Add(new RowDefinition());
            libraryPage.RowDefinitions.Add(new RowDefinition {Height=new GridLength(45)});
            var heading=new DockPanel { Margin=new Thickness(70,24,66,8) };
            // 返回首页统一交给手柄与 Esc，不再占用页面操作区。
            heading.Children.Add(new TextBlock {Text="游戏库",Foreground=Brushes.White,FontSize=30,FontWeight=FontWeights.Light,VerticalAlignment=VerticalAlignment.Center});
            libraryPage.Children.Add(heading);
            var tabs=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(70,0,66,0)};Grid.SetRow(tabs,1);
            filterButton=LibraryTab("你的游戏","全部");installedTab=LibraryTab("已安装","已安装");favoriteTab=LibraryTab("收藏","收藏");
            tabs.Children.Add(filterButton);tabs.Children.Add(installedTab);tabs.Children.Add(favoriteTab);
            var bar=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(70,7,66,12)};Grid.SetRow(bar,2);
            platformButton=LibraryButton(platform,()=>{
                var values=new[]{"所有平台"}.Concat(allCards.Select(x=>x.Item.Platform??"本地游戏").Distinct().OrderBy(x=>x)).ToList();
                platform=values[(values.IndexOf(platform)+1)%values.Count];ApplyLibraryFilter();
            });bar.Children.Add(platformButton);
            sortButton=LibraryButton("最近游玩",()=>{sortByName=!sortByName;ApplyLibraryFilter();});bar.Children.Add(sortButton);
            searchBox=new TextBox {Text=navigation.query??"",Width=205,FontSize=18,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(20,255,255,255)),BorderBrush=new SolidColorBrush(Color.FromArgb(80,255,255,255)),CaretBrush=Brushes.White,Margin=new Thickness(8),Padding=new Thickness(10,4,10,4),VerticalContentAlignment=VerticalAlignment.Center};
            searchBox.Tag="搜索游戏";searchBox.SetResourceReference(StyleProperty,"Ps5SearchBoxStyle");
            searchBox.TextChanged+=(s,e)=>{if(libraryReady && showAll)ApplyLibraryFilter();};
            libraryFilters=new StackPanel {Width=390,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(38)};
            tabs.Orientation=Orientation.Vertical;tabs.Margin=new Thickness(0);libraryFilters.Children.Add(tabs);bar.Orientation=Orientation.Vertical;bar.Margin=new Thickness(0,15,0,0);libraryFilters.Children.Add(bar);
            var tools=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(38,4,0,8)};Grid.SetRow(tools,2);
            libraryTools=LibraryButton("",OpenLibraryFilters);libraryTools.Content=Ps5Glyph("sort");libraryTools.ToolTip="排序与筛选";libraryTools.Width=50;
            librarySearch=LibraryButton("",OpenSearchKeyboard);librarySearch.Content=Ps5Glyph("search");librarySearch.ToolTip="搜索";librarySearch.Width=50;
            foreach(var tool in new[]{libraryTools,librarySearch}){tool.SetResourceReference(StyleProperty,"Ps5CircleButtonStyle");tool.Height=50;tool.Padding=new Thickness(0);tool.VerticalAlignment=VerticalAlignment.Top;}
            tools.Children.Add(libraryTools);tools.Children.Add(librarySearch);libraryPage.Children.Add(tools);
            libraryCount=new TextBlock {Foreground=Brushes.LightGray,FontSize=12,Margin=new Thickness(70,10,66,6)};Grid.SetRow(libraryCount,4);libraryPage.Children.Add(libraryCount);
            var root=(Panel)layout;root.Children.Insert(root.Children.IndexOf(contextPage),libraryPage);
            keyboardPage=new Grid {Background=new SolidColorBrush(Color.FromArgb(248,20,24,33)),Visibility=Visibility.Collapsed};root.Children.Add(keyboardPage);
            summarySlot=new ContentControl {Width=270,MaxHeight=175,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,100,105)};
            root.Children.Insert(root.Children.IndexOf(homeGrid)+1,summarySlot);
            gameLogo=new Image {Width=300,Height=142,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(112,0,0,208),IsHitTestVisible=false};
            // 深色透明 Logo 加浅色柔光以便辨认，不改变原素材。
            gameLogo.Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.White,BlurRadius=14,ShadowDepth=0,Opacity=.6};
            root.Children.Insert(root.Children.IndexOf(homeGrid)+1,gameLogo);
            // 无 Logo 使用同一区域的游戏名称回退，首页主体不会只剩操作按钮。
            logoFallback=new TextBlock {Width=340,MaxHeight=140,FontSize=32,FontWeight=FontWeights.Light,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(112,0,0,230),IsHitTestVisible=false};
            root.Children.Insert(root.Children.IndexOf(homeGrid)+1,logoFallback);
            Panel.SetZIndex(libraryPage,1);Panel.SetZIndex((UIElement)layout.FindName("DetailsPage"),2);Panel.SetZIndex((UIElement)layout.FindName("SettingsPage"),2);Panel.SetZIndex(contextPage,3);Panel.SetZIndex(keyboardPage,4);
            var detailStack=(Panel)((Button)layout.FindName("BackDetails")).Parent;
            var detailActions=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,16)};
            detailStart=LibraryButton("开始游戏",StartSelected);detailActions.Children.Add(detailStart);
            detailFavorite=LibraryButton("收藏",()=>{var card=games.SelectedItem as GameCard;var actions=source as IGameActions;if(card!=null&&actions!=null){actions.SetFavorite(card.Item,!card.Item.Favorite);detailFavorite.Content=card.Item.Favorite?"取消收藏":"收藏";}});detailActions.Children.Add(detailFavorite);
            detailTrailer=LibraryButton("观看预告",()=>{CloseOverlay();BeginPreview(true);});detailActions.Children.Add(detailTrailer);detailStack.Children.Insert(detailStack.Children.Count-1,detailActions);
            libraryReady=true;
            // 悬停改变实际选择，因此鼠标与手柄共用预览取消代次。
            games.MouseMove+=(s,e)=>{
                // 横条位移不能让静止指针连锁选中新移过来的卡片。
                var point=e.GetPosition(layout);if(!Double.IsNaN(lastHoverPoint.X)&&(point-lastHoverPoint).Length<2)return;lastHoverPoint=point;
                var container=ItemsControl.ContainerFromElement(games,e.OriginalSource as DependencyObject) as ListBoxItem;
                SelectHovered(container);
            };
            // 卡片类处理可能消费冒泡双击；在预览阶段明确聚焦所点条目。
            games.PreviewMouseLeftButtonDown+=(s,e)=>{
                var container=ItemsControl.ContainerFromElement(games,e.OriginalSource as DependencyObject) as ListBoxItem;
                if(container==null)return;
                ++hoverGeneration;container.IsSelected=true;container.Focus();
                PlayCue("confirm");StartSelected();e.Handled=true;
            };
        }

        /// <summary>按钮统一采用主题动作样式和真实命令。</summary>
        private Button LibraryButton(string text, Action action)
        {
            var button=new Button {Content=text,Margin=new Thickness(0,0,8,0),Padding=new Thickness(14,8,14,8),FontSize=17};
            button.SetResourceReference(StyleProperty,"ActionStyle");button.Click+=(s,e)=>action();button.MouseEnter+=(s,e)=>button.Focus();return button;
        }

        /// <summary>分类标签直接选中目标，底部细线指示当前类别。</summary>
        private Button LibraryTab(string label,string value)
        {
            var button=LibraryButton(label,()=>{filter=value;ApplyLibraryFilter();});
            button.Tag=value;button.Background=Brushes.Transparent;button.FontSize=20;
            button.Margin=new Thickness(0,0,30,0);button.Padding=new Thickness(0,5,0,9);
            var border=new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BorderBrushProperty,new System.Windows.Data.Binding("BorderBrush"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            border.SetBinding(Border.BorderThicknessProperty,new System.Windows.Data.Binding("BorderThickness"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            border.SetValue(Border.PaddingProperty,new Thickness(0,5,0,9));
            var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);
            button.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
            button.GotKeyboardFocus+=(s,e)=>button.Opacity=1;button.LostKeyboardFocus+=(s,e)=>button.Opacity=(string)button.Tag==filter?1:.7;
            return button;
        }

        /// <summary>同一游戏模型在首页横条与虚拟网格之间移动，保留选中项。</summary>
        private void SetLibraryLayout(bool all)
        {
            if(!libraryReady)return;
            focusedCard=null;
            games.Resources["TileRadius"]=new CornerRadius(all?0:10);games.Resources["TileImageRadius"]=all?0.0:10.0;
            games.Resources["TileSize"]=all?(1156.0/experience.columns-14):71.0;
            games.Resources["TileFrameSize"]=(double)games.Resources["TileSize"]+4;
            games.Resources["LibraryRowHeight"]=1156.0/experience.columns+(experience.libraryTitles?44:0);
            games.Resources["TitleStripVisibility"]=all&&experience.libraryTitles?Visibility.Visible:Visibility.Collapsed;
            var parent=games.Parent as Panel;if(parent!=null)parent.Children.Remove(games);
            if(all)
            {
                games.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(GameGridPanel)));
                games.Padding=new Thickness(5,10,5,5);games.Margin=new Thickness(62,8,62,0);Grid.SetRow(games,3);libraryPage.Children.Add(games);
                ScrollViewer.SetVerticalScrollBarVisibility(games,ScrollBarVisibility.Auto);
            }
            else
            {
                games.ItemsPanel=homePanel;games.Padding=new Thickness(0);games.Margin=new Thickness(0);Grid.SetRow(games,1);homeGrid.Children.Add(games);
                ScrollViewer.SetVerticalScrollBarVisibility(games,ScrollBarVisibility.Disabled);
            }
            libraryPage.Visibility=all?Visibility.Visible:Visibility.Collapsed;RefreshPageBackground();
            ThemePacks.Reveal(all?(UIElement)libraryPage:homeGrid,layout,"PageFadeMs",settings.reducedMotion,1);
            homeGrid.Visibility=all?Visibility.Collapsed:Visibility.Visible;
            summarySlot.Visibility=gameLogo.Visibility=all?Visibility.Collapsed:Visibility.Visible;
            logoFallback.Visibility=all||gameLogo.Source!=null?Visibility.Collapsed:Visibility.Visible;
        }

        /// <summary>查询只过滤展示快照，不改用户隐藏状态或安装状态。</summary>
        public static IEnumerable<GameCard> QueryLibrary(IEnumerable<GameCard> cards,string filter,string platform,string query,bool byName)
        {
            var result=cards.Where(c=>(filter!="已安装"||c.Item.IsInstalled)&&(filter!="收藏"||c.Item.Favorite)
                &&(String.IsNullOrEmpty(platform)||platform=="所有平台"||(c.Item.Platform??"本地游戏")==platform)
                &&(String.IsNullOrWhiteSpace(query)||c.Title.IndexOf(query.Trim(),StringComparison.CurrentCultureIgnoreCase)>=0));
            return byName?result.OrderBy(c=>c.Title):result.OrderByDescending(c=>c.Item.LastActivity).ThenBy(c=>c.Title);
        }

        /// <summary>筛选后仍可从工具栏返回，空结果不遗留上一游戏的启动动作。</summary>
        private void ApplyLibraryFilter()
        {
            if(allCards==null)return;
            string previous=selectedId;
            var items=QueryLibrary(allCards,filter,platform,searchBox.Text,sortByName).ToList();
            games.ItemsSource=items;games.SelectedItem=items.FirstOrDefault(c=>c.Item.ItemId==previous)??items.FirstOrDefault();
            foreach(var tab in new[]{filterButton,installedTab,favoriteTab}){
                bool active=(string)tab.Tag==filter;tab.Foreground=active?Brushes.White:Brushes.Gray;
                tab.FontWeight=active?FontWeights.SemiBold:FontWeights.Normal;
                tab.BorderBrush=active?Brushes.White:Brushes.Transparent;tab.BorderThickness=new Thickness(0,0,0,2);
            }
            platformButton.Content=platform;sortButton.Content=sortByName?"名称 A—Z":"最近游玩";
            libraryCount.Text=items.Count+" 款游戏    × 详情    ○ 返回    L1 / R1 页面    Options 操作菜单";
            if(items.Count==0)ShowSelection();
            SaveNavigation();
        }

        /// <summary>屏幕键盘支持拉丁字母与本库中文标题字符，不依赖鼠标输入。</summary>
        private void OpenSearchKeyboard()
        {
            keyboardReturn=librarySearch;media.Cancel();keyboardPage.Children.Clear();
            var panel=new StackPanel {Width=890,VerticalAlignment=VerticalAlignment.Center};
            panel.Children.Add(new TextBlock {Text="搜索游戏",Foreground=Brushes.White,FontSize=30,Margin=new Thickness(8,0,0,18)});
            var text=new TextBox {Text=searchBox.Text,FontSize=25,Margin=new Thickness(8),Padding=new Thickness(12)};text.Tag="搜索游戏和追加内容";text.SetResourceReference(StyleProperty,"Ps5SearchBoxStyle");panel.Children.Add(text);
            var keys=new UniformGrid {Columns=10};
            string letters="ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var chinese=allCards.SelectMany(c=>TextElements(c.Title)).Where(c=>c[0]>127&&!Char.IsPunctuation(c,0)).Distinct().OrderBy(c=>c).ToArray();
            int page=0;Action fill=null;
            fill=()=>{
                keys.Children.Clear();var chars=page==0?TextElements(letters):chinese.Skip((page-1)*40).Take(40);
                foreach(string c in chars){string key=c;keys.Children.Add(LibraryButton(key,()=>text.Text+=key));}
            };fill();panel.Children.Add(keys);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(8,16,8,0)};
            actions.Children.Add(LibraryButton("字符页",()=>{page=(page+1)%(1+(int)Math.Ceiling(chinese.Length/40.0));fill();}));
            actions.Children.Add(LibraryButton("空格",()=>text.Text+=" "));
            actions.Children.Add(LibraryButton("删除",()=>{var positions=StringInfo.ParseCombiningCharacters(text.Text);if(positions.Length>0)text.Text=text.Text.Substring(0,positions[positions.Length-1]);}));
            actions.Children.Add(LibraryButton("清空",()=>text.Text=""));
            actions.Children.Add(LibraryButton("搜索",()=>{searchBox.Text=text.Text;keyboardPage.Visibility=Visibility.Collapsed;games.Focus();}));
            actions.Children.Add(LibraryButton("取消",()=>{keyboardPage.Visibility=Visibility.Collapsed;(keyboardReturn??librarySearch).Focus();}));panel.Children.Add(actions);
            keyboardPage.Children.Add(panel);keyboardPage.Visibility=Visibility.Visible;
            if(keys.Children.Count>0)keys.Children[0].Focus();
        }

        /// <summary>按完整文本元素枚举，键盘翻页和删除不拆开扩展汉字与组合字符。</summary>
        private static IEnumerable<string> TextElements(string text)
        {
            var elements=StringInfo.GetTextElementEnumerator(text??"");
            while(elements.MoveNext())yield return elements.GetTextElement();
        }

        /// <summary>按方向寻找邻居，列表按六列移动，工具栏不被左右键抢走。</summary>
        private bool NavigateLibrary(string action)
        {
            if(keyboardPage.Visibility==Visibility.Visible)
            {
                if(action=="back"){keyboardPage.Visibility=Visibility.Collapsed;(keyboardReturn??librarySearch).Focus();PlayCue("back");return true;}
                if(action=="accept")return false;
                MoveSpatial(action);return true;
            }
            if(action=="previousPage"){ShowLibrary(false);return true;}
            if(action=="nextPage"){ShowLibrary(true);return true;}
            if(OverlayOpen)return false;
            if(games.IsKeyboardFocusWithin && (action=="left"||action=="right"||action=="up"||action=="down"))
            {
                if(showAll && action=="up" && games.SelectedIndex<experience.columns){libraryTools.Focus();return true;}
                if(!showAll && action=="up"){((Button)layout.FindName("Home")).Focus();return true;}
                if(!showAll && action=="down"){start.Focus();return true;}
                int delta=action=="left"?-1:action=="right"?1:action=="up"?-experience.columns:experience.columns;
                if(games.Items.Count>0){games.SelectedIndex=Math.Max(0,Math.Min(games.Items.Count-1,games.SelectedIndex+delta));games.ScrollIntoView(games.SelectedItem);}
                return true;
            }
            if(action=="up"||action=="down"||action=="left"||action=="right"){MoveSpatial(action);return true;}
            return false;
        }
        /// <summary>使用空间方向而非 Tab 顺序导航操作区。</summary>
        private void MoveSpatial(string action)
        {
            var focused=Keyboard.FocusedElement as UIElement;
            if(focused!=null)focused.MoveFocus(new TraversalRequest(action=="left"?FocusNavigationDirection.Left:action=="right"?FocusNavigationDirection.Right:action=="up"?FocusNavigationDirection.Up:FocusNavigationDirection.Down));
        }
        /// <summary>递归定位列表自身的滚动容器，恢复值最终由面板钳制在有效范围。</summary>
        private static ScrollViewer FindScroller(DependencyObject root)
        {
            var viewer=root as ScrollViewer;if(viewer!=null)return viewer;
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++){var result=FindScroller(VisualTreeHelper.GetChild(root,index));if(result!=null)return result;}
            return null;
        }
        /// <summary>模板完成后恢复原来的库页像素偏移，避免生成容器期间覆盖保存值。</summary>
        private void RestoreLibraryScroll()
        {
            double offset=libraryScrollOffset;restoringScroll=true;
            Dispatcher.BeginInvoke(new Action(()=>{try{if(showAll){var viewer=FindScroller(games);if(viewer!=null)viewer.ScrollToVerticalOffset(offset);}}finally{restoringScroll=false;}}),System.Windows.Threading.DispatcherPriority.Loaded);
        }
        /// <summary>Options 打开当前游戏可执行操作，命令仍交给相同平台桥。</summary>
        private void OpenGameMenu()
        {
            var card=games.SelectedItem as GameCard;
            if(card==null||card.IsLibraryEntry){OpenSettings();return;}
            media.Cancel();ClearContextPage();
            var panel=new StackPanel {Width=620,VerticalAlignment=VerticalAlignment.Center};
            var launch=LibraryButton(card.Item.IsInstalled?"开始游戏":"安装游戏",()=>{CloseOverlay();StartSelected();});launch.IsEnabled=start.IsEnabled;panel.Children.Add(launch);
            panel.Children.Add(LibraryButton("查看详情",()=>{ClearContextPage();OpenDetails();}));
            var favorite=LibraryButton(card.Item.Favorite?"取消收藏":"收藏",()=>{var actions=source as IGameActions;if(actions!=null){actions.SetFavorite(card.Item,!card.Item.Favorite);CloseOverlay();}});favorite.IsEnabled=source is IGameActions;panel.Children.Add(favorite);
            panel.Children.Add(LibraryButton("浏览与播放设置",OpenSettings));
            contextTitle.Text=card.Title;contextContent.Content=panel;contextPage.Visibility=Visibility.Visible;
            panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        /// <summary>退出和筛选时原子保存浏览位置，磁盘故障不阻止游戏。</summary>
        private void SaveNavigation()
        {
            var card=games.SelectedItem as GameCard;
            try{new NavigationPreference {provider=card==null?"playnite":card.Item.Provider,sourceInstance=card==null?"local":card.Item.SourceInstance,itemId=card==null?selectedId:card.Item.ItemId,showAll=showAll,filter=filter,platform=platform,query=searchBox.Text,sortByName=sortByName,scrollOffset=libraryScrollOffset}.Save(dataRoot);}catch{}
        }
    }
}
