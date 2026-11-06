using System;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ModularShell;

namespace EmbyBrowser
{
    /// <summary>直接承载 Emby Web；只增加本地输入桥，不重写服务端页面、登录与播放协议。</summary>
    public sealed class WebLibrary : Grid, IPageInput, IPageExit, IPageActivity
    {
        public const string ServerUrl = "https://emby_strm-home.172906573.xyz:28443/";
        private readonly WebView2 browser;
        private readonly TextBlock message;
        private readonly string root;
        private bool disposed, started, inputPending;
        private readonly Queue<string> inputs = new Queue<string>();
        private int navigationGeneration;
        private static bool loaderConfigured;
        public event Action ExitRequested;

        /// <summary>网页入口默认可用；显式关闭仅影响网页入口，不修改旧 API 连接配置。</summary>
        public static bool IsEnabled(string userDataRoot) { return !File.Exists(Path.Combine(userDataRoot,"emby","web-disabled")); }

        /// <summary>网页模式设置只管理入口开关，登录和服务配置在 Emby 页面内完成。</summary>
        public static object CreateSettingsView(string userDataRoot)
        {
            var panel = new StackPanel { Margin = new Thickness(28) };
            var enabled = new CheckBox { Content = "启用 Emby 网页入口", IsChecked = IsEnabled(userDataRoot), Margin = new Thickness(0,0,0,24) };
            panel.Children.Add(enabled);
            panel.Children.Add(new TextBlock { Text = ServerUrl + "\n\n进入影视 → Emby，在网页中登录。\n会话保存在本机，下次进入继续使用。\n\n方向键：移动焦点\n×：确认 / 文字输入\n○：关闭键盘 / 网页后退\n□：暂停或继续视频\nOptions / F10：返回影视入口", TextWrapping = TextWrapping.Wrap });
            var save = new Button { Content = "保存", Margin = new Thickness(0,24,0,12), Padding = new Thickness(14) }; panel.Children.Add(save);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
            save.Click += (s,e) => {
                try {
                    string path=Path.Combine(userDataRoot,"emby","web-disabled"); Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (enabled.IsChecked == true) { if(File.Exists(path))File.Delete(path); } else File.WriteAllText(path,"");
                    status.Text="已保存，关闭设置后生效。";
                } catch { status.Text="未能保存，原设置保留。"; }
            };
            return panel;
        }

        /// <summary>失去前台暂停网页媒体；返回时由用户主动继续，不自动抢占声音。</summary>
        public async void SetActive(bool active)
        {
            if (active || disposed || browser.CoreWebView2 == null) return;
            try { await browser.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(e=>e.pause())"); } catch { /* 卸载时无需继续操作页面。 */ }
        }

        /// <summary>浏览器使用独立用户数据目录；网页会话与原 REST 令牌互不转换。</summary>
        public WebLibrary(string userDataRoot)
        {
            root = userDataRoot;
            Background = new SolidColorBrush(Color.FromRgb(12,16,23));
            message = new TextBlock { Text = "正在打开 Emby…\nOptions / F10 返回影视", Foreground = Brushes.White, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            Children.Add(message);
            browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255,12,16,23), Visibility = Visibility.Hidden };
            Children.Add(browser);
            PreviewKeyDown += (s,e) => { if (e.Key == System.Windows.Input.Key.F10 || e.SystemKey == System.Windows.Input.Key.F10) { e.Handled = true; Dispatcher.BeginInvoke(new Action(() => { if (ExitRequested != null) ExitRequested(); })); } };
            Loaded += (s,e) => InitializeBrowser();
            // 移除页面立即销毁控制器，停止网页音频和播放，不删除持久登录数据。
            Unloaded += (s,e) => { disposed = true; inputs.Clear(); browser.Dispose(); };
        }

        /// <summary>初始化仅发生在进入服务后，失败保留可退出的提示，不记录网页凭证。</summary>
        private async void InitializeBrowser()
        {
            if (started || disposed) return; started = true;
            try {
                string package = Path.GetDirectoryName(typeof(WebLibrary).Assembly.Location);
                if (!loaderConfigured) {
                    // SDK 禁止在 loader 已载入后再次设置目录；同进程的后续网页复用它。
                    try { CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(package,"runtimes",Environment.Is64BitProcess?"win-x64":"win-x86","native")); }
                    catch (InvalidOperationException) { /* 其他 WebView2 消费者已经装入 loader，沿用 SDK 当前实例。 */ }
                    loaderConfigured = true;
                }
                var environment = await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"emby","web-profile"));
                if (disposed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (disposed) return;
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
                // 本地桥统一消费 SDK 与浏览器 Gamepad，避免 WebView 原生焦点使 Playnite 停发事件。
                string script;
                using (var reader = new StreamReader(typeof(WebLibrary).Assembly.GetManifestResourceStream("Controller.js"))) script = reader.ReadToEnd();
                await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
                if (disposed) return;
                browser.CoreWebView2.WebMessageReceived += (s,e) => {
                    if (!disposed && Allowed(e.Source) && e.WebMessageAsJson == "\"shell:exit\"")
                        Dispatcher.BeginInvoke(new Action(() => { if(!disposed && ExitRequested!=null)ExitRequested(); }));
                };
                browser.CoreWebView2.NewWindowRequested += (s,e) => { e.Handled = true; if (Allowed(e.Uri)) browser.CoreWebView2.Navigate(e.Uri); };
                browser.CoreWebView2.NavigationStarting += (s,e) => { if (!Allowed(e.Uri)) { e.Cancel = true; return; } navigationGeneration++; inputs.Clear(); };
                browser.CoreWebView2.NavigationCompleted += (s,e) => {
                    if (disposed) return;
                    browser.Visibility = e.IsSuccess ? Visibility.Visible : Visibility.Hidden;
                    if (e.IsSuccess) browser.Focus();
                    message.Text = e.IsSuccess ? "" : "Emby 网页加载失败：" + e.WebErrorStatus + "\n按 × 重试 · Options / F10 返回影视";
                };
                browser.CoreWebView2.Navigate(ServerUrl);
            } catch { if (!disposed) message.Text = "浏览器初始化失败，请检查 WebView2 Runtime。\nOptions / F10 返回影视"; }
        }

        /// <summary>顶层页面只留在给定服务源内，跨站跳转不会继承本地输入桥。</summary>
        private static bool Allowed(string value)
        {
            Uri uri; return Uri.TryCreate(value,UriKind.Absolute,out uri) && uri.Scheme == "https" && uri.Authority == new Uri(ServerUrl).Authority;
        }

        /// <summary>有限动作经脚本操作页面；退出由宿主处理，失败页确认可以重试导航。</summary>
        public bool HandleInput(string action)
        {
            if (action == "settings") return false;
            if (disposed) return false;
            if (browser.CoreWebView2 == null) return action != "back";
            if (browser.Visibility != Visibility.Visible) { if (action == "accept") browser.CoreWebView2.Navigate(ServerUrl); return action != "back"; }
            if (action == "left" || action == "right" || action == "up" || action == "down" || action == "accept" || action == "back" || action == "previousPage" || action == "nextPage" || action == "trailer") SendInput(action);
            return true;
        }

        /// <summary>串行消费有界输入队列，导航时清空旧动作，不丢弃正常快速确认。</summary>
        private async void SendInput(string action)
        {
            if (disposed || inputs.Count >= 32) return;
            inputs.Enqueue(action); if(inputPending)return; inputPending = true;
            try {
                int generation=navigationGeneration;
                while(!disposed && inputs.Count>0 && generation==navigationGeneration) {
                    string next=inputs.Dequeue();
                    await browser.CoreWebView2.ExecuteScriptAsync("window.shellController && window.shellController('" + next + "')");
                }
            } catch { /* 导航和卸载期间的输入不重放到下一页。 */ }
            finally { inputPending = false; }
        }
    }
}
