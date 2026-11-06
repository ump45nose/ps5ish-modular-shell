using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ModularShell.QuickPanel
{
    [DataContract] public sealed class Context { [DataMember] public int schema; [DataMember] public string id,title,state; }
    [DataContract] public sealed class Preference { [DataMember] public int schema=1,key=32; }
    [DataContract] public sealed class Note { [DataMember] public int schema=1; [DataMember] public string text="",tracked="",url=""; }
    /// <summary>独立快捷面板进程：无游戏注入、无全局输入转发、关闭后归还原前台窗口。</summary>
    public sealed class Panel : Window
    {
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window,int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
        private readonly string root,pipeName;
        private readonly int parent;
        private IntPtr handle,previous;
        private HwndSource source;
        private NamedPipeClientStream pipe;
        private Context context;
        private bool dirty,closing,loading;
        private Preference preference;
        private readonly TextBlock title=new TextBlock {FontSize=28,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock status=new TextBlock {Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap};
        private readonly TextBox notes=new TextBox {AcceptsReturn=true,Height=170,MaxLength=16000,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        private readonly TextBox tracked=new TextBox {MaxLength=1000};
        private readonly TextBox url=new TextBox {MaxLength=2048};
        /// <summary>仅用户显式启动后注册热键；个人数据与安装目录分离。</summary>
        public Panel(string root,string pipeName,int parent)
        {
            this.root=Path.Combine(root,"quick-panel");this.pipeName=pipeName;this.parent=parent;
            Title="Modular Shell · 快捷面板";Width=620;Height=670;WindowStartupLocation=WindowStartupLocation.CenterScreen;Topmost=true;Background=new SolidColorBrush(Color.FromRgb(12,22,40));
            var content=new StackPanel {Margin=new Thickness(26)};Content=new ScrollViewer {Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            content.Children.Add(title);content.Children.Add(status);
            Label(content,"个人追踪目标（手动记录，不代表已解锁）");content.Children.Add(tracked);
            Label(content,"个人笔记");content.Children.Add(notes);Label(content,"攻略链接（HTTPS）");content.Children.Add(url);
            var row=new WrapPanel{Margin=new Thickness(0,16,0,0)};content.Children.Add(row);
            Add(row,"保存",()=>Save());Add(row,"打开攻略",()=>OpenGuide());Add(row,"返回游戏",()=>HidePanel());Add(row,"退出面板",()=>Close());
            Label(content,"呼出快捷键：Ctrl + Alt + 按键");
            var keys=new ComboBox{SelectedIndex=0};keys.Items.Add("Space");keys.Items.Add("F8");keys.Items.Add("F9");keys.Items.Add("F10");content.Children.Add(keys);
            Add(row,"应用快捷键",()=>Rebind(keys.SelectedIndex==0?32:118+keys.SelectedIndex));
            notes.TextChanged+=(s,e)=>{if(!loading)dirty=true;};tracked.TextChanged+=(s,e)=>{if(!loading)dirty=true;};url.TextChanged+=(s,e)=>{if(!loading)dirty=true;};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){HidePanel();e.Handled=true;}};
            KeyboardNavigation.SetTabNavigation(content,KeyboardNavigationMode.Cycle);
            preference=Read<Preference>(Path.Combine(this.root,"settings.json"))??new Preference();
            if(preference.schema!=1 || (preference.key!=32 && (preference.key<119 || preference.key>121)))preference=new Preference();
            previous=GetForegroundWindow();
            SourceInitialized+=(s,e)=>{handle=new WindowInteropHelper(this).Handle;source=HwndSource.FromHwnd(handle);source.AddHook(Hook);if(!RegisterHotKey(handle,1,0x4003,(uint)preference.key))status.Text="快捷键被占用，请选择其他按键。";};
            Loaded+=(s,e)=>Connect();
            Closing+=(s,e)=>{if(!closing && !Save()){e.Cancel=true;return;}closing=true;UnregisterHotKey(handle,1);if(source!=null)source.RemoveHook(Hook);if(pipe!=null)pipe.Dispose();};
        }
        /// <summary>构造带边距的分组标签。</summary>
        private static void Label(StackPanel parent,string text){parent.Children.Add(new TextBlock{Text=text,Foreground=Brushes.LightGray,Margin=new Thickness(0,14,0,5)});}
        /// <summary>按钮命令异常显示在面板，不传播到宿主进程。</summary>
        private void Add(WrapPanel row,string text,Action action){var b=new Button{Content=text,Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,8,8)};b.Click+=(s,e)=>{try{action();}catch{status.Text="操作失败，原数据保留。";}};row.Children.Add(b);}
        /// <summary>使用同机命名管道并确认宿主 PID，最多读取 64 KiB 快照。</summary>
        private async void Connect()
        {
            try {
                pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
                await Task.Run(()=>pipe.Connect(5000));uint pid;
                if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out pid) || pid!=(uint)parent)throw new IOException();
                using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true)){writer.AutoFlush=true;
                    while(!closing){await writer.WriteLineAsync("snapshot");var buffer=new MemoryStream();byte[] b=new byte[1];
                        while(true){int n=await pipe.ReadAsync(b,0,1);if(n==0)throw new IOException();if(b[0]==10)break;if(buffer.Length>=65536)throw new IOException();buffer.WriteByte(b[0]);}
                        buffer.Position=0;var next=(Context)new DataContractJsonSerializer(typeof(Context)).ReadObject(buffer);buffer.Dispose();
                        if(next==null || next.schema!=1 || (next.id!="home" && !ValidGuid(next.id)))throw new IOException();
                        if(context==null || context.id!=next.id){if(!Save()){status.Text="笔记未保存，继续保留当前条目。";}else{context=next;LoadNote();}}
                        if(context!=null && context.id==next.id){context=next;title.Text=next.title+" · "+next.state;}
                        await Task.Delay(1000);
                    }
                }
            }catch{if(!closing){status.Text="宿主连接已关闭，快捷键已释放。";UnregisterHotKey(handle,1);closing=Save();if(closing)Close();else Show();}}
        }
        /// <summary>内容 ID 只允许 GUID 或固定首页标识，防止 IPC 路径跳转。</summary>
        private static bool ValidGuid(string value){Guid id;return Guid.TryParse(value,out id);}
        /// <summary>新条目独立载入个人记录，永不写平台游戏数据库。</summary>
        private void LoadNote(){loading=true;try{var note=Read<Note>(Path.Combine(root,context.id+".json"))??new Note();notes.Text=note.text??"";tracked.Text=note.tracked??"";url.Text=note.url??"";dirty=false;}finally{loading=false;}}
        /// <summary>原子保存个人记录；失败保留编辑内容并阻止关闭。</summary>
        private bool Save(){if(context==null || !dirty)return true;try{Write(Path.Combine(root,context.id+".json"),new Note{text=notes.Text,tracked=tracked.Text,url=url.Text});dirty=false;status.Text="笔记已保存";return true;}catch{status.Text="保存失败，请保留窗口并检查磁盘。";return false;}}
        /// <summary>只响应用户点击的 HTTPS 攻略，拒绝文件、脚本与带账号地址。</summary>
        private void OpenGuide(){Uri target;if(!Uri.TryCreate(url.Text,UriKind.Absolute,out target) || target.Scheme!="https" || target.UserInfo.Length!=0){status.Text="请输入有效的 HTTPS 攻略地址。";return;}Process.Start(new ProcessStartInfo(target.AbsoluteUri){UseShellExecute=true});}
        /// <summary>先试注册新键再保存，冲突或保存失败时恢复旧快捷键。</summary>
        private void Rebind(int key){UnregisterHotKey(handle,1);if(!RegisterHotKey(handle,1,0x4003,(uint)key)){bool restored=RegisterHotKey(handle,1,0x4003,(uint)preference.key);status.Text=restored?"快捷键冲突，原快捷键保留。":"快捷键冲突且旧键无法恢复，请重新选择。";return;}try{Write(Path.Combine(root,"settings.json"),new Preference{key=key});preference.key=key;status.Text="快捷键已保存";}catch{UnregisterHotKey(handle,1);if(!RegisterHotKey(handle,1,0x4003,(uint)preference.key))status.Text="保存失败且快捷键未注册，请重新选择。";else status.Text="保存失败，旧快捷键已恢复。";}}
        /// <summary>呼出时记住前台；焦点获取失败不留下覆盖游戏的面板。</summary>
        private IntPtr Hook(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled){if(msg==0x312){handled=true;if(IsVisible && IsActive)HidePanel();else{previous=GetForegroundWindow();Show();Activate();if(GetForegroundWindow()!=handle){Hide();return IntPtr.Zero;}notes.Focus();}}return IntPtr.Zero;}
        /// <summary>只在面板仍拥有前台时恢复原窗口，不在用户切换应用后抢焦点。</summary>
        private void HidePanel(){if(!Save())return;bool restore=GetForegroundWindow()==handle;Hide();if(restore && previous!=IntPtr.Zero && previous!=handle && IsWindow(previous))SetForegroundWindow(previous);}
        /// <summary>读取有界个人配置；坏文件不阻止面板启动。</summary>
        private static T Read<T>(string path) where T:class{try{if(new FileInfo(path).Length>131072)return null;using(var stream=File.OpenRead(path))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);}catch{return null;}}
        /// <summary>临时文件原子替换，保留上一份配置以便恢复。</summary>
        private static void Write<T>(string path,T value){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var stream=File.Create(temp))new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
        /// <summary>仅由可信宿主传入固定参数启动；无后台计划、无自启动注册。</summary>
        [STAThread] public static int Main(string[] args){try{if(args.Length!=6 || args[0]!="--pipe" || args[2]!="--parent" || args[4]!="--data" || !args[1].StartsWith("ModularShell.Panel.",StringComparison.Ordinal))return 2;int pid;if(!Int32.TryParse(args[3],out pid) || pid<=0 || !Path.IsPathRooted(args[5]))return 2;new Application().Run(new Panel(args[5],args[1],pid));return 0;}catch{return 1;}}
    }
}
