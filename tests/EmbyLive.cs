using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbyBrowser;

/// <summary>显式传入用户目录才运行的只读实连接验收，不随默认构建访问账号。</summary>
public static class EmbyLive
{
    /// <summary>仅输出数量和布尔结果，输出中不包含令牌、账号或媒体名称。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Length!=2)return 2;
        int result=0;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(s,e)=>{
            try{await Run(args[0],args[1]);}
            catch(Exception ex){Console.Error.WriteLine("实连接验收失败："+ex.GetType().Name);result=1;}
            finally{app.Shutdown();}
        };app.Run();return result;
    }
    /// <summary>真实分页、详情和海报验证不会调用任何播放状态写入接口。</summary>
    private static async Task Run(string root,string output)
    {
        var client=new Client(root);
        foreach(string section in new[]{"resume","recent","latest","library"})
        {
            var page=await client.Browse(section,0,null,CancellationToken.None);
            if(page.Offline)throw new Exception("实连接返回离线缓存");
            Console.WriteLine("PASS 真实分区 "+section+"：本页="+page.Items.Length+"，总数="+page.Total);
        }
        var first=await client.Browse("library",0,null,CancellationToken.None);
        var second=await client.Browse("library",30,null,CancellationToken.None);
        if(first.Items.Select(i=>i.Id).Intersect(second.Items.Select(i=>i.Id)).Any())throw new Exception("分页条目重复");
        Console.WriteLine("PASS 真实影视库前两页引用不重复");
        var detail=await client.Detail(first.Items[0].Id,CancellationToken.None);
        if(detail.Id!=first.Items[0].Id)throw new Exception("详情上下文不一致");
        Console.WriteLine("PASS 真实详情引用一致");
        bool poster=false;
        foreach(var item in first.Items.Take(3))try{var bytes=await client.Poster(item.Id,CancellationToken.None);if(bytes.Length>0){poster=true;break;}}catch(IOException){}
        if(!poster)throw new Exception("未取得真实海报");Console.WriteLine("PASS 真实认证海报读取成功");
        var view=new Browser(root);
        try
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var toolbar=view.Children.OfType<WrapPanel>().First();((Button)toolbar.Children[3]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list=view.Children.OfType<ListBox>().First();
            for(int i=0;i<100 && list.Items.Count==0;i++)await Task.Delay(100);
            if(list.Items.Count==0)throw new Exception("真实海报墙没有条目");
            await Task.Delay(2500);
            view.Measure(new Size(1180,580));view.Arrange(new Rect(0,0,1180,580));view.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1180,580,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(output))encoder.Save(stream);
            Console.WriteLine("PASS 真实媒体数据离屏海报墙已渲染");
        }
        finally{view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));}
    }
}
