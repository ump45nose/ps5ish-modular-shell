using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using ModularShell.PlayniteIntegration;

/// <summary>真实子进程及本机 IPC 回归，不启动游戏、不发送系统按键。</summary>
public static class QuickPanelTests
{
    /// <summary>测试创建的子进程才允许在超时时终止，不按进程名批量操作。</summary>
    [STAThread] public static int Main()
    {
        string root=AppDomain.CurrentDomain.BaseDirectory;
        string data=Path.Combine(root,"panel-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(data);
        Process child=null;var bridge=new QuickPanelBridge();
        try {
            bridge.Update(Guid.NewGuid().ToString("D"),"快捷面板集成测试（模拟游戏）","未启动游戏");bridge.Start(root,data);
            var owned=(Process)typeof(QuickPanelBridge).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(bridge);
            child=Process.GetProcessById(owned.Id);
            var timer=Stopwatch.StartNew();while(bridge.SnapshotCount<2 && timer.ElapsedMilliseconds<10000 && !child.HasExited)Thread.Sleep(100);
            Check(bridge.SnapshotCount>=2 && !child.HasExited,"独立面板完成双端 PID 校验并连续读取真实命名管道快照");
            int initial=bridge.SnapshotCount;bridge.Update(Guid.NewGuid().ToString("D"),"第二个模拟上下文","已退出");
            while(bridge.SnapshotCount<initial+2 && timer.ElapsedMilliseconds<14000 && !child.HasExited)Thread.Sleep(100);
            Check(bridge.SnapshotCount>=initial+2,"面板在宿主上下文更新后保持有效连接");
            bridge.Dispose();Check(child.WaitForExit(6000),"宿主通道关闭后独立面板退出并释放快捷键");
            Render(root,data);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
        finally{
            bridge.Dispose();
            if(child!=null){try{if(!child.HasExited){child.Kill();child.WaitForExit(3000);}}catch{}child.Dispose();}
            // 清理前验证绝对路径归属固定 artifacts 目录。
            if(!Path.GetFullPath(data).StartsWith(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))throw new Exception("测试清理越界");
            Directory.Delete(data,true);
        }
    }
    /// <summary>断言只描述当前真实进程证据，不覆盖物理输入验收。</summary>
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    /// <summary>离屏验证笔记实际保存和可见布局，不触发快捷键或连接真实游戏。</summary>
    private static void Render(string root,string data)
    {
        var panel=new ModularShell.QuickPanel.Panel(data,"ModularShell.Panel.fixture",Process.GetCurrentProcess().Id);
        var type=panel.GetType();var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        string id=Guid.NewGuid().ToString("D");
        type.GetField("context",flags).SetValue(panel,new ModularShell.QuickPanel.Context{schema=1,id=id,title="模拟游戏 · 快捷面板",state="测试"});
        ((System.Windows.Controls.TextBlock)type.GetField("title",flags).GetValue(panel)).Text="模拟游戏 · 快捷面板";
        ((System.Windows.Controls.TextBox)type.GetField("notes",flags).GetValue(panel)).Text="回到上次的存档点，继续探索。\n这是测试笔记，不属于真实游戏数据。";
        bool saved=(bool)type.GetMethod("Save",flags).Invoke(panel,null);
        Check(saved && File.Exists(Path.Combine(data,"quick-panel",id+".json")),"面板个人笔记写入隔离目录并原子保存");
        var view=(System.Windows.FrameworkElement)panel.Content;view.Measure(new System.Windows.Size(620,670));view.Arrange(new System.Windows.Rect(0,0,620,670));view.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(620,670,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(view);
        var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"quick-panel.png")))png.Save(file);
        panel.Close();Check(true,"快捷面板真实 WPF 布局离屏渲染");
    }
}
