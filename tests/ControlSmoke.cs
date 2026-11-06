using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using ModularShell;
using ModularShell.PlayniteIntegration;

/// <summary>使用明确标注的测试源验证真实控件的 WPF 生命周期和布局。</summary>
public static class ControlSmoke
{
    private sealed class TestSource : IGameSource, IPlatformViews
    {
        private readonly int count;
        public bool PlatformAvailable;
        public int PlatformRequests;
        /// <summary>指定样例数，覆盖空列表与大列表。</summary>
        public TestSource(int count) { this.count = count; }
        /// <summary>只返回测试数据，不读写任何 Playnite 数据。</summary>
        public IList<GameItem> Read()
        {
            var items = new List<GameItem>();
            for (int i = 0; i < count; i++) items.Add(new GameItem {
                Provider = PlatformAvailable ? "playnite" : "test", SourceInstance = PlatformAvailable ? "local" : "smoke", ItemId = PlatformAvailable ? new Guid(i+1,0,0,new byte[8]).ToString("D") : i.ToString(),
                Title = "测试条目 " + i + " · 中文长标题与空素材回退", IsInstalled = true });
            return items;
        }
        /// <summary>离屏验证禁止触发真实启动。</summary>
        public void Start(GameItem item) { throw new InvalidOperationException("测试源禁止启动"); }
        /// <summary>模拟可选平台扩展是否存在，普通样例默认缺失。</summary>
        public bool HasExtension(string id) { return PlatformAvailable; }
        /// <summary>提供带醒目标识的模拟控件，验证通用插槽而非真实 PA 账号数据。</summary>
        public object CreateView(string id, string name, GameItem item)
        {
            PlatformRequests++;
            return new TextBlock { Text = "模拟 PA 控件（非真实账号）\n" + name + "\n内容引用：" + item.ItemId, FontSize = 24, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        }
    }

    /// <summary>分别渲染空库及 500 条目视图，并验证加载卸载可重复执行。</summary>
    [STAThread]
    public static int Main()
    {
        int result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) => {
            try { await Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        };
        app.Run(); return result;
    }

    /// <summary>使用真实 Dispatcher 上下文等待异步外挂，保持界面操作在线程内。</summary>
    private static async Task Run()
    {
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            foreach (int count in new[] { 0, 500 })
            {
                var view = new ShellControl(new TestSource(count), root, root);
                for (int pass = 0; pass < 2; pass++)
                {
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Task.Delay(100);
                    var layout = (FrameworkElement)((Viewbox)((Grid)view.Content).Children[0]).Child;
                    var list = (ListBox)layout.FindName("Games");
                    ((Button)layout.FindName("Home")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    // 首页末尾是导航入口，不计入真实库条目，也不能调用平台启动。
                    if (list.Items.Count != Math.Min(9, count)+1 || !((GameCard)list.Items[list.Items.Count-1]).IsLibraryEntry) throw new Exception("首页最近游戏或库入口错误");
                    if(count==500 && pass==0){
                        // 首页横条选中锚点固定，切换后的后继卡不能被放大卡覆盖。
                        view.Measure(new Size(1280,720));view.Arrange(new Rect(0,0,1280,720));view.UpdateLayout();
                        // 等待当前配置的动效完成，再验证终态几何；不将旧固定时长当成布局约束。
                        int settle=(int)(double)layout.TryFindResource("FocusFadeMs")+100;
                        list.SelectedIndex=0;await Task.Delay(settle);view.UpdateLayout();
                        var first=(FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
                        double anchor=first.TransformToAncestor(list).Transform(new Point()).X;
                        list.SelectedIndex=1;await Task.Delay(settle);view.UpdateLayout();
                        var selected=(FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
                        var next=(FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(2);
                        double moved=selected.TransformToAncestor(list).Transform(new Point()).X;
                        double right=selected.TransformToAncestor(list).Transform(new Point(selected.ActualWidth,0)).X;
                        if(Math.Abs(anchor-moved)>1 || next.TransformToAncestor(list).Transform(new Point()).X<right-1)throw new Exception("首页锚点漂移或放大卡重叠");
                        Console.WriteLine("PASS 首页切换固定锚点与相邻卡无重叠");
                    }
                    ((Button)layout.FindName("Library")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (list.Items.Count != count) throw new Exception("全部游戏数量错误");
                    if (count == 500 && pass == 0)
                    {
                        var samples = new List<double>();
                        view.Measure(new Size(1280,720)); view.Arrange(new Rect(0,0,1280,720)); view.UpdateLayout();
                        for (int index = 0; index < count; index++)
                        {
                            var timer = Stopwatch.StartNew();
                            list.SelectedIndex = index; list.ScrollIntoView(list.SelectedItem); view.UpdateLayout();
                            samples.Add(timer.Elapsed.TotalMilliseconds);
                            await Task.Yield();
                        }
                        if (list.SelectedIndex != 499) throw new Exception("500 条导航未到达末项");
                        samples.Sort();
                        File.WriteAllText(Path.Combine(root,"navigation-metrics.txt"), "样例元数据 500 条，选择+布局 P95 ms=" + samples[(int)(samples.Count*.95)-1] + "；不是硬件输入到屏幕呈现延迟。\n");
                        list.SelectedIndex = 0; list.ScrollIntoView(list.SelectedItem);
                    }
                    if (count > 0)
                    {
                        ((Button)layout.FindName("Details")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (pass == 0) Capture(view, Path.Combine(root,"control-details-1280.png"),1280);
                        ((Button)layout.FindName("Settings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (pass == 0) Capture(view, Path.Combine(root,"control-settings-1280.png"),1280);
                        if (((Grid)layout.FindName("DetailsPage")).Visibility != Visibility.Collapsed) throw new Exception("详情覆盖设置");
                        ((Button)layout.FindName("CancelSettings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (((Grid)layout.FindName("SettingsPage")).Visibility != Visibility.Collapsed) throw new Exception("设置未返回");
                    }
                    foreach (string palette in new[] { "midnight", "ember" })
                        foreach (int width in new[] { 1280, 3840 })
                        {
                            ThemeComposer.ApplyPalette((FrameworkElement)((Viewbox)((Grid)view.Content).Children[0]).Child, root, palette);
                            Capture(view, Path.Combine(root, "control-" + count + "-" + palette + "-" + width + ".png"), width);
                        }
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    if (pass == 0) Capture(view, Path.Combine(root,"control-"+count+"-3840-175percent.png"),3840,168);
                }
                Console.WriteLine("PASS 控件加载/卸载及离屏渲染，测试条目数：" + count);
            }
            // 影院结构独立用户配置，不改变既有默认布局样例的证据文件。
            string cinemaData = Path.Combine(root, "cinema-smoke-data");
            Directory.CreateDirectory(cinemaData);
            File.WriteAllText(Path.Combine(cinemaData, "theme.json"), "{\"schema\":1,\"palette\":\"ember\",\"layout\":\"cinema\"}");
            var cinemaView = new ShellControl(new TestSource(500), root, cinemaData);
            try
            {
                cinemaView.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Task.Delay(100);
                Capture(cinemaView, Path.Combine(root, "control-cinema-1280.png"), 1280);
                Capture(cinemaView, Path.Combine(root, "control-cinema-3840-175percent.png"), 3840, 168);
                Console.WriteLine("PASS 影院布局 720p 与 4K/175% 离屏渲染");
            }
            finally { cinemaView.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
            var adapterSource = new TestSource(3) { PlatformAvailable = true };
            var adapterView = new ShellControl(adapterSource,root,cinemaData);
            try
            {
                adapterView.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Task.Delay(200);
                var adapterLayout = (Grid)((Viewbox)((Grid)adapterView.Content).Children[0]).Child;
                var action = (Button)adapterLayout.FindName("ContextPageAction");
                for(int wait=0;wait<30 && action.Visibility!=Visibility.Visible;wait++) await Task.Delay(100);
                if (action.Visibility != Visibility.Visible || adapterSource.PlatformRequests != 1) throw new Exception("扩展入口缺失或提前创建控件："+action.Visibility+" / "+adapterSource.PlatformRequests+" / "+((TextBlock)adapterLayout.FindName("Status")).Text);
                ((Button)adapterLayout.FindName("Details")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var overlay = adapterLayout.Children.OfType<Grid>().First(g=>g.Children.Count==2 && g.Children[0] is DockPanel && g.Children[1] is ContentControl);
                var pluginContent = (ContentControl)overlay.Children[1];
                if (overlay.Visibility != Visibility.Visible || pluginContent.Content == null || adapterSource.PlatformRequests != 2) throw new Exception("通用扩展页面未接通");
                Capture(adapterView,Path.Combine(root,"control-context-page-1280.png"),1280);
                var back = (Button)((DockPanel)overlay.Children[0]).Children[0];
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (overlay.Visibility != Visibility.Collapsed || pluginContent.Content != null) throw new Exception("返回未清理扩展控件");
                // 影视先显示服务卡片，默认构建验证不打开网络浏览器。
                ((Button)adapterLayout.FindName("HubAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!(pluginContent.Content is MediaHubView)) throw new Exception("影视未先展示服务入口");
                Capture(adapterView,Path.Combine(root,"control-media-services-1280.png"),1280);
                if (!((IPageInput)pluginContent.Content).HandleInput("right")) throw new Exception("服务入口未消费方向输入");
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (pluginContent.Content != null) throw new Exception("影视返回后仍保留页面");
                Console.WriteLine("PASS 影视服务入口布局、输入与返回清理");
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                adapterView.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                if (pluginContent.Content != null) throw new Exception("卸载仍持有第三方控件");
                Console.WriteLine("PASS 通用扩展页按需创建、返回移除和卸载清理（模拟 PA）");
            }
            finally { adapterView.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
            await Task.Delay(100);
        }
        catch { throw; }
    }
    /// <summary>离屏捕获当前真实控件布局；不声称是桌面或电视截图。</summary>
    private static void Capture(ShellControl view, string path, int width, double dpi = 96)
    {
        int height = width * 9 / 16;
        double scale = dpi / 96;
        view.Measure(new Size(width/scale,height/scale)); view.Arrange(new Rect(0,0,width/scale,height/scale)); view.UpdateLayout();
        var image = new RenderTargetBitmap(width,height,dpi,dpi,PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(path)) encoder.Save(file);
    }
}
