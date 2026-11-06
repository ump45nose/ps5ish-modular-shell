using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using ModularShell;

/// <summary>M0 桌面验证入口；模拟条目不写入 Playnite 数据库。</summary>
public static class Prototype
{
    /// <summary>打开共用布局，验证键盘焦点、主题资源和外挂分区。</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        string root = AppDomain.CurrentDomain.BaseDirectory;
        bool safe = args.Contains("--safe");
        var loader = new ModuleLoader();
        loader.Load(Path.Combine(root, "plugins"), safe);
        var preview = new PreviewCoordinator();
        var window = new Window { Title = "Modular Shell · M0 验证原型", Width = 1280, Height = 760,
            MinWidth = 800, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        // 安全主题内置；外部字典失败不会留下不完整资源。
        window.Resources["Surface"] = new SolidColorBrush(Color.FromRgb(16, 27, 50));
        window.Resources["Card"] = new SolidColorBrush(Color.FromRgb(32, 59, 96));
        window.Resources["Accent"] = Brushes.LightSkyBlue;
        window.SetResourceReference(Window.BackgroundProperty, "Surface");
        window.Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(52), VerticalAlignment = VerticalAlignment.Center };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Content = scroll;
        panel.Children.Add(new TextBlock { Text = "游戏     /     M0 实验室", FontSize = 22 });
        panel.Children.Add(new TextBlock { Text = "你的下一段旅程", FontSize = 44, Margin = new Thickness(0, 30, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "模拟内容 · 尚未接入游戏库与播放后端", FontSize = 18, Opacity = .65 });
        var cards = new WrapPanel { Margin = new Thickness(0, 24, 0, 20) };
        var status = new TextBlock { FontSize = 19, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(cards);
        panel.Children.Add(status);
        Button first = null;
        foreach (string title in new[] { "探索未知", "穿越群山", "静谧之海", "很长的中文游戏标题排版验证" })
        {
            string selected = title;
            var button = new Button { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
                Width = 210, Height = 130, Margin = new Thickness(0, 0, 16, 12), FontSize = 23,
                Foreground = Brushes.White, BorderThickness = new Thickness(2), Padding = new Thickness(18) };
            button.SetResourceReference(Control.BackgroundProperty, "Card");
            button.SetResourceReference(Control.BorderBrushProperty, "Accent");
            button.GotKeyboardFocus += async (s, e) => await preview.SelectAsync(selected, text => status.Text = text, 200, 2500);
            button.Click += (s, e) => { preview.Cancel(); status.Text = selected + " · 原型未提供启动能力"; };
            cards.Children.Add(button);
            if (first == null) first = button;
        }
        var switcher = new Button { Content = "切换氛围：深海 / 暖夜", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 20, 0, 12), FontSize = 16 };
        int palette = 0;
        switcher.IsEnabled = !safe;
        switcher.Click += (s, e) => {
            try
            {
                string name = (++palette % 2 == 1) ? "ember.xaml" : "midnight.xaml";
                ResourceDictionary dictionary;
                using (var stream = File.OpenRead(Path.Combine(root, "themes", name)))
                    dictionary = (ResourceDictionary)XamlReader.Load(stream);
                // 原型只接受三个明确的画刷键；生产包契约在 M0 后半段实现。
                foreach (string key in new[] { "Surface", "Card", "Accent" })
                    if (!(dictionary[key] is SolidColorBrush)) throw new InvalidDataException("缺少画刷：" + key);
                foreach (string key in new[] { "Surface", "Card", "Accent" }) window.Resources[key] = dictionary[key];
            }
            catch (Exception ex) { status.Text = "主题未应用，保留原组合：" + ex.Message; }
        };
        panel.Children.Add(switcher);
        foreach (var section in loader.Sections)
            panel.Children.Add(new TextBlock { Text = section.Title + "\n" + section.Text, FontSize = 18,
                Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = safe ? "安全启动：已跳过外挂与外部主题" : String.Join("\n", loader.Diagnostics),
            Opacity = .6, TextWrapping = TextWrapping.Wrap });
        window.PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Right || e.Key == Key.Left)
            {
                var focused = Keyboard.FocusedElement as UIElement;
                if (focused != null) focused.MoveFocus(new TraversalRequest(e.Key == Key.Right ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
                e.Handled = true;
            }
            if (e.Key == Key.Escape) window.Close();
        };
        window.Deactivated += (s, e) => { preview.Cancel(); status.Text = "已暂停预览时序"; };
        window.Closed += (s, e) => { preview.Dispose(); loader.Dispose(); };
        window.Loaded += (s, e) => first.Focus();
        // 离屏截图验证实际 WPF 布局，不打开或接管用户的前台窗口。
        if (args.Contains("--snapshot"))
        {
            window.Width = 1280;
            window.Height = 760;
            scroll.Background = (Brush)window.Resources["Surface"];
            scroll.Measure(new Size(1280, 760));
            scroll.Arrange(new Rect(0, 0, 1280, 760));
            scroll.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(scroll);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(root, "prototype.png"))) encoder.Save(stream);
            preview.Dispose();
            loader.Dispose();
            app.Shutdown();
            return;
        }
        app.Run(window);
    }
}
