using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl
    {
        private readonly ContentControl hubSurface;

        /// <summary>影视首先列出独立服务，确认图标后才创建该服务页面。</summary>
        private void OpenHubPage()
        {
            if (busy || modules == null) return;
            ClearContextPage(); media.Cancel(); manualTrailer = false;
            ((Grid)layout.FindName("DetailsPage")).Visibility = Visibility.Collapsed;
            ((Grid)layout.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
            contextTitle.Text = "影视";
            contextContent.Content = new MediaHubView(modules.GetHubProviders().Where(p => !(p is IOptionalHubSettings) || ((IOptionalHubSettings)p).Enabled).ToArray(), games, OpenHubService, settings.reducedMotion);
            contextPage.Visibility = Visibility.Visible; RefreshPageBackground();
            contextContent.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }

        /// <summary>网页直接使用窗口客户区，不经过游戏主题的固定比例 Viewbox。</summary>
        private void OpenHubService(IHubViewProvider provider)
        {
            try {
                var page = provider.CreateView() as FrameworkElement;
                if (page == null) throw new InvalidOperationException("服务没有提供页面");
                var exit = page as IPageExit; if (exit != null) exit.ExitRequested += CloseHubService;
                media.Cancel(); StopPageVideo(); StopCue();
                hubSurface.Content = page; hubSurface.Visibility = Visibility.Visible;
                page.Focus();
            } catch { contextTitle.Text = "服务暂不可用，请返回后重试"; }
        }

        /// <summary>移除网页触发浏览器释放，并把焦点还给刚才选中的服务图标。</summary>
        private void CloseHubService()
        {
            if (hubSurface == null || hubSurface.Content == null) return;
            var exit = hubSurface.Content as IPageExit; if (exit != null) exit.ExitRequested -= CloseHubService;
            hubSurface.Content = null; hubSurface.Visibility = Visibility.Collapsed;
            var selector = contextContent.Content as MediaHubView;
            if (selector != null) selector.RestoreFocus();
        }
    }

    /// <summary>服务选择页复用游戏方卡模板和横向切换；不产生任何游戏条目。</summary>
    public sealed class MediaHubView : Grid, IPageInput
    {
        private readonly ListBox services;
        private readonly Action<IHubViewProvider> open;

        /// <summary>按注册顺序展示入口，样式来自当前主题游戏卡片，确认回调由宿主协调。</summary>
        public MediaHubView(IHubViewProvider[] providers, ListBox gameStyle, Action<IHubViewProvider> open, bool reducedMotion)
        {
            this.open = open;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
            RowDefinitions.Add(new RowDefinition());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            services = new ListBox {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                ItemContainerStyle = gameStyle.FindResource("MediaServiceStyle") as Style, ItemTemplate = gameStyle.FindResource("MediaServiceTemplate") as DataTemplate,
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(HomeCarouselPanel))),
                HorizontalContentAlignment = HorizontalAlignment.Left, FocusVisualStyle = null
            };
            // 与游戏首页保持同样的透明方卡、焦点边框和横向滚动语义。
            ScrollViewer.SetHorizontalScrollBarVisibility(services, ScrollBarVisibility.Hidden);
            ScrollViewer.SetVerticalScrollBarVisibility(services, ScrollBarVisibility.Disabled);
            foreach (var provider in providers) services.Items.Add(new ServiceCard(provider));
            Children.Add(services);
            var description = new TextBlock { Foreground = Brushes.White, FontSize = 38, Margin = new Thickness(10,35,0,0), TextWrapping = TextWrapping.Wrap };
            SetRow(description, 1); Children.Add(description);
            var hint = new TextBlock { Text = "← → 切换服务    × 进入    ○ 返回游戏\n网页中：方向键移动 · × 确认 / 输入 · ○ 后退 · Options / F10 返回影视", Foreground = Brushes.LightGray, FontSize = 17, Margin = new Thickness(10,10,0,10) };
            SetRow(hint, 2); Children.Add(hint);
            services.SelectionChanged += (s,e) => {
                var carousel = FindCarousel(services); if (carousel != null) carousel.InvalidateArrange();
                var selected = services.SelectedItem as ServiceCard;
                description.Text = selected == null ? "尚未安装影视服务" : selected.Title + "\n浏览、搜索与播放";
                if (!reducedMotion) description.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.45,1,TimeSpan.FromMilliseconds(180)));
            };
            services.PreviewMouseLeftButtonUp += (s,e) => { if (services.SelectedItem != null) OpenSelected(); };
            services.SelectedIndex = providers.Length > 0 ? 0 : -1;
            Loaded += (s,e) => { services.UpdateLayout(); var carousel = FindCarousel(services); if (carousel != null) { carousel.ReducedMotion = reducedMotion; carousel.InvalidateArrange(); } RestoreFocus(); };
        }

        /// <summary>定位当前已生成的横条面板，复用游戏首页固定锚点与选中放大。</summary>
        private static HomeCarouselPanel FindCarousel(DependencyObject parent)
        {
            var panel = parent as HomeCarouselPanel; if (panel != null) return panel;
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var found=FindCarousel(VisualTreeHelper.GetChild(parent,i)); if(found!=null)return found; }
            return null;
        }

        /// <summary>返回服务页时恢复当前选择，避免焦点落入被遮盖的游戏列表。</summary>
        public void RestoreFocus() { services.Focus(); if (services.SelectedItem != null) services.ScrollIntoView(services.SelectedItem); }
        /// <summary>仅在用户确认时创建网页，不因焦点变化访问服务。</summary>
        private void OpenSelected() { var selected = services.SelectedItem as ServiceCard; if (selected != null) open(selected.Provider); }
        /// <summary>方向移动使用有界索引，返回交给宿主，其他按键不穿透到游戏界面。</summary>
        public bool HandleInput(string action)
        {
            if (action == "back") return false;
            if (action == "accept") OpenSelected();
            else if (action == "left" || action == "right") {
                services.SelectedIndex = Math.Max(0, Math.Min(services.Items.Count-1, services.SelectedIndex + (action == "left" ? -1 : 1)));
                RestoreFocus();
            }
            return true;
        }

        /// <summary>仅适配主题卡片字段；影视身份仍保留为能力提供者。</summary>
        public sealed class ServiceCard
        {
            public IHubViewProvider Provider { get; private set; }
            public string Title { get { return Provider.Title; } }
            public ImageSource Cover { get; private set; }
            public bool IsLibraryEntry { get { return false; } }
            /// <summary>品牌元数据由服务提供；未知服务使用中性播放图标。</summary>
            public ServiceCard(IHubViewProvider provider)
            {
                Provider = provider;
                var appearance = provider as IHubAppearance;
                var group = new DrawingGroup();
                var color = appearance == null ? Color.FromRgb(89,124,165) : (Color)ColorConverter.ConvertFromString(appearance.AccentColor);
                group.Children.Add(new GeometryDrawing(new LinearGradientBrush(color, Color.FromArgb(80,color.R,color.G,color.B),45), new Pen(new SolidColorBrush(Color.FromArgb(70,255,255,255)),1), new RectangleGeometry(new Rect(0,0,100,100),8,8)));
                group.Children.Add(new GeometryDrawing(Brushes.White,null,Geometry.Parse(appearance == null ? "M 38,27 L 73,50 38,73 Z" : appearance.IconGeometry)));
                Cover = new DrawingImage(group); Cover.Freeze();
            }
        }
    }
}
