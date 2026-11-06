using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModularShell;
using ModularShell.PlayniteIntegration;

/// <summary>隔离空库验证本地启屏视频、跳过与卸载；不读取或改变用户游戏库。</summary>
public static class StartupLive
{
    private sealed class EmptySource : IGameSource
    {
        /// <summary>空库仍可执行完整启屏生命周期。</summary>
        public IList<GameItem> Read(){return new List<GameItem>();}
        /// <summary>测试禁止启动游戏。</summary>
        public void Start(GameItem item){throw new InvalidOperationException("测试不能启动游戏");}
    }
    /// <summary>参数为真实本地视频路径；设置与探测日志保留在固定artifacts。</summary>
    [STAThread] public static int Main(string[] args)
    {
        int result=0;var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(s,e)=>{
            Window window=null;
            try{
                string root=AppDomain.CurrentDomain.BaseDirectory,data=Path.Combine(root,"startup-live-fixture");
                new ShellSettings {startupAnimation=true,startupVideo=args[0],music=false}.Save(data);
                var control=new ShellControl(new EmptySource(),root,data);window=new Window {Content=control,Width=960,Height=540,ShowInTaskbar=false};window.Show();
                var flags=BindingFlags.NonPublic|BindingFlags.Instance;var active=typeof(ShellControl).GetField("startupActive",flags);var layer=typeof(ShellControl).GetField("startupLayer",flags);
                bool ready=false;
                for(int i=0;i<120;i++){await Task.Delay(100);var grid=layer.GetValue(control) as Grid;if(grid!=null&&grid.Children.Count>0&&grid.Children[0] is Image&&((Image)grid.Children[0]).Source!=null){ready=true;break;}}
                if(!ready)throw new Exception("本地启屏视频没有呈现像素");Console.WriteLine("PASS 本地真实4K视频作为启屏动画已呈现");
                typeof(ShellControl).GetMethod("StopStartup",flags).Invoke(control,null);await Task.Delay(300);
                if((bool)active.GetValue(control)||layer.GetValue(control)!=null)throw new Exception("跳过未释放覆盖层");Console.WriteLine("PASS 跳过启屏释放视频与覆盖层，返回空库");
                if(!ShellSettings.Load(data).startupAnimation||ShellSettings.Load(data).startupVideo!=args[0])throw new Exception("设置未持久化");Console.WriteLine("PASS 本地启屏文件设置可重新读取");
            }catch(Exception ex){Console.Error.WriteLine(ex.Message);result=1;}
            finally{if(window!=null)window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}
