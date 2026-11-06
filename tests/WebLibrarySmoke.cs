using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using EmbyBrowser;

/// <summary>独立真实 WebView2 验证：不使用既有账号、不播放媒体、不写服务器状态。</summary>
public static class WebLibrarySmoke
{
    private static int result;
    /// <summary>STA Dispatcher 运行浏览器生命周期，失败返回非零退出码。</summary>
    [STAThread] public static int Main()
    {
        var app = new Application();
        app.Startup += async (s,e) => { try { await Run(); } catch (Exception ex) { Console.WriteLine(ex); result=1; } finally { app.Shutdown(); } };
        app.Run(); return result;
    }
    /// <summary>断言网页执行返回的布尔值，日志不输出页面正文和输入内容。</summary>
    private static async Task Check(WebView2 browser, string expression, string name)
    {
        if (await browser.ExecuteScriptAsync(expression) != "true") throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    /// <summary>验证目标站点后，拦截专用测试路径提供本地夹具，绝不向服务器提交测试表单。</summary>
    private static async Task Run()
    {
        string root=AppDomain.CurrentDomain.BaseDirectory;
        string profile=Path.Combine(root,"web-smoke-profile",Guid.NewGuid().ToString("N"));
        var page = new WebLibrary(profile);
        var window = new Window { Width=1280,Height=800,Content=page,ShowActivated=false,Title="Emby 网页只读验证" };
        window.Show();
        var browser=(WebView2)typeof(WebLibrary).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(page);
        DateTime limit=DateTime.UtcNow.AddSeconds(40);
        while (browser.CoreWebView2==null || browser.Visibility!=Visibility.Visible) { if(DateTime.UtcNow>limit)throw new Exception("目标网页在 40 秒内未加载"); await Task.Delay(150); }
        await Task.Delay(3000);
        await Check(browser,"location.origin === 'https://emby_strm-home.172906573.xyz:28443' && typeof shellController === 'function'","真实站点加载且注入输入桥");
        limit=DateTime.UtcNow.AddSeconds(15);
        while(await browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button,a[href],input,[tabindex]')).some(e=>e.tabIndex>=0 && e.getBoundingClientRect().width>0 && e.getBoundingClientRect().height>0)")!="true") {
            if(DateTime.UtcNow>limit)throw new Exception("登录页交互控件尚未就绪"); await Task.Delay(150);
        }
        await Check(browser,"shellController('right'); document.activeElement !== document.body && document.activeElement.hasAttribute('data-shell-focus')","真实登录页可获得手柄焦点");
        using(var image=File.Create(Path.Combine(root,"emby-web-live.png"))) await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
        browser.CoreWebView2.AddWebResourceRequestedFilter(WebLibrary.ServerUrl+"shell-controller-test*",CoreWebView2WebResourceContext.Document);
        browser.CoreWebView2.WebResourceRequested += (s,e) => {
            string html="<!doctype html><html><head><style>button,input{position:absolute;width:160px;height:50px}#a{left:30px;top:30px}#b{left:260px;top:30px}#text{left:260px;top:160px}</style></head><body><button id='a'>A</button><button id='b' onclick='window.hit=(window.hit||0)+1'>B</button><input id='text' type='password'><script>window.hit=0</script></body></html>";
            e.Response=browser.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)),200,"OK","Content-Type: text/html");
        };
        var completed=new TaskCompletionSource<bool>();
        browser.CoreWebView2.NavigationCompleted += (s,e) => completed.TrySetResult(e.IsSuccess);
        browser.CoreWebView2.Navigate(WebLibrary.ServerUrl+"shell-controller-test");
        if(await Task.WhenAny(completed.Task,Task.Delay(10000))!=completed.Task || !await completed.Task)throw new Exception("本地导航夹具失败");
        await browser.ExecuteScriptAsync("document.getElementById('a').focus()");
        page.HandleInput("right"); page.HandleInput("accept"); await Task.Delay(250);
        await Check(browser,"hit===1","快速方向加确认经过页面输入队列不丢失");
        await browser.ExecuteScriptAsync("hit=0");
        await Check(browser,"document.getElementById('a').focus(); shellController('right'); document.activeElement.id==='b'","方向键按空间位置移动焦点");
        await Check(browser,"shellController('accept'); hit===1","确认只点击一次");
        await Check(browser,"shellController('down'); document.activeElement.id==='text'","上下移动到文本框");
        await Check(browser,"shellController('accept'); !!document.getElementById('shell-keyboard')","确认文本框打开手柄键盘");
        await Check(browser,"shellController('accept'); document.getElementById('text').value.length===1","手柄键盘可以输入密码字段");
        await Check(browser,"shellController('back'); !document.getElementById('shell-keyboard') && document.activeElement.id==='text'","返回关闭键盘并恢复焦点");
        await Check(browser,"shellController('accept'); const old=document.getElementById('text'); old.replaceWith(old.cloneNode(true)); shellController('back'); document.activeElement===document.getElementById('text')","输入框重建后仍恢复真实节点焦点");
        await Check(browser,"navigator.getGamepads().length===0","网页不重复消费原生手柄");
        using(var image=File.Create(Path.Combine(root,"emby-controller-fixture.png")))await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
        window.Content=null; await Task.Delay(300);
        if (!(bool)typeof(WebLibrary).GetField("disposed",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(page))throw new Exception("卸载未释放浏览器");
        Console.WriteLine("PASS 卸载页面释放浏览器");
        // 同一进程连续开关，覆盖 loader 已加载时重建浏览器的实际路径。
        for(int i=0;i<2;i++) {
            var reopened=new WebLibrary(profile); window.Content=reopened;
            var next=(WebView2)typeof(WebLibrary).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(reopened);
            limit=DateTime.UtcNow.AddSeconds(30);
            while(next.CoreWebView2==null || next.Visibility!=Visibility.Visible) { if(DateTime.UtcNow>limit)throw new Exception("重复进入初始化失败"); await Task.Delay(100); }
            await CheckNativeInput(next,reopened);
            window.Content=null; await Task.Delay(100);
        }
        Console.WriteLine("PASS 同进程连续三次进入退出浏览器"); window.Close();
    }

    /// <summary>同一会话二次进入后，以标准 Gamepad 数据驱动真实脚本，不经过 SDK 输入方法。</summary>
    private static async Task CheckNativeInput(WebView2 browser,WebLibrary page)
    {
        browser.CoreWebView2.AddWebResourceRequestedFilter(WebLibrary.ServerUrl+"shell-native-test*",CoreWebView2WebResourceContext.Document);
        browser.CoreWebView2.WebResourceRequested += (s,e) => {
            string html="<!doctype html><html><head></head><body><button id='a' style='position:absolute;left:10px;top:10px;width:100px;height:40px'>A</button><button id='b' style='position:absolute;left:220px;top:10px;width:100px;height:40px' onclick='window.hit++'>B</button><script>window.hit=0</script></body></html>";
            e.Response=browser.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)),200,"OK","Content-Type: text/html");
        };
        var complete=new TaskCompletionSource<bool>(); browser.CoreWebView2.NavigationCompleted+=(s,e)=>complete.TrySetResult(e.IsSuccess);
        browser.CoreWebView2.Navigate(WebLibrary.ServerUrl+"shell-native-test");
        if(await Task.WhenAny(complete.Task,Task.Delay(10000))!=complete.Task || !await complete.Task)throw new Exception("原生输入夹具未加载");
        string adapter; using(var reader=new StreamReader(typeof(WebLibrary).Assembly.GetManifestResourceStream("Controller.js")))adapter=reader.ReadToEnd();
        // 只替换硬件读取和测试窗口激活判断，其余 DOM、rAF、脚本与宿主消息仍是真实浏览器实现。
        await browser.ExecuteScriptAsync("window.testPad={index:0,connected:true,mapping:'standard',axes:[0,0],buttons:Array.from({length:17},()=>({pressed:false}))}; document.getElementById('a').focus(); (()=>{const navigator={getGamepads:()=>[window.testPad]}; const document=new Proxy(window.document,{get:(d,k)=>k==='hasFocus'?()=>true:k==='visibilityState'?'visible':typeof d[k]==='function'?d[k].bind(d):d[k]}); "+adapter+"})();");
        await Task.Delay(100);
        await browser.ExecuteScriptAsync("testPad.buttons[15].pressed=true"); await Task.Delay(100);
        await Check(browser,"document.activeElement.id==='b'","再次进入后浏览器原生手柄独立移动焦点");
        await browser.ExecuteScriptAsync("testPad.buttons[15].pressed=false"); await Task.Delay(60);
        await browser.ExecuteScriptAsync("testPad.buttons[0].pressed=true"); page.HandleInput("accept"); await Task.Delay(100);
        await Check(browser,"hit===1","原生与 SDK 同时输入只确认一次");
        await browser.ExecuteScriptAsync("testPad.buttons[0].pressed=false"); await Task.Delay(60);
        bool exit=false; page.ExitRequested+=()=>exit=true;
        await browser.ExecuteScriptAsync("testPad.buttons[9].pressed=true"); await Task.Delay(100);
        if(!exit)throw new Exception("网页原生 Options 未请求返回");
        Console.WriteLine("PASS 无 SDK 回调时原生 Options 仍可退出");
    }
}
