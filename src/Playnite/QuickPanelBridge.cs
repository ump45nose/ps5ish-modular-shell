using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace ModularShell.PlayniteIntegration
{
    [DataContract] public sealed class QuickContext
    {
        [DataMember] public int schema=1;
        [DataMember] public string id="home", title="游戏首页", state="浏览中";
    }
    /// <summary>独立面板只读快照桥；同用户 ACL、双端 PID 检查，不向子进程开放启动或脚本命令。</summary>
    public sealed class QuickPanelBridge : IDisposable
    {
        private readonly object gate=new object();
        private NamedPipeServerStream pipe;
        private Process process;
        private bool disposed;
        private int snapshots;
        public int SnapshotCount { get { return System.Threading.Volatile.Read(ref snapshots); } }
        private QuickContext context=new QuickContext();
        /// <summary>平台事件提供当前内容，不枚举游戏进程或读取其内存。</summary>
        public void Update(string id,string title,string state){lock(gate){context=new QuickContext{id=id??"home",title=title??"游戏首页",state=state};}}
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
        /// <summary>显式菜单操作启动一份面板；运行期进程失败不向主页抛出后台异常。</summary>
        public void Start(string packageRoot,string dataRoot)
        {
            lock(gate){
                if(disposed)throw new ObjectDisposedException("QuickPanelBridge");
                if(process!=null && !process.HasExited)return;
                if(pipe!=null)pipe.Dispose();if(process!=null)process.Dispose();
                string name="ModularShell.Panel."+Guid.NewGuid().ToString("N");
                var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
                try { process=Process.Start(new ProcessStartInfo(Path.Combine(packageRoot,"Shell.QuickPanel.exe")){
                    Arguments="--pipe "+name+" --parent "+Process.GetCurrentProcess().Id+" --data \""+Path.Combine(dataRoot,".")+"\"",UseShellExecute=false,CreateNoWindow=true }); }
                catch{pipe.Dispose();pipe=null;throw;}
                var channel=pipe;int expected=process.Id;Task.Run(()=>Serve(channel,expected));
            }
        }
        /// <summary>仅接受 snapshot 请求，消息定长上限防止无界内存读取。</summary>
        private async Task Serve(NamedPipeServerStream channel,int expected)
        {
            try {
                await Task.Factory.FromAsync(channel.BeginWaitForConnection,channel.EndWaitForConnection,null);
                uint actual;if(!GetNamedPipeClientProcessId(channel.SafePipeHandle.DangerousGetHandle(),out actual) || actual!=(uint)expected)return;
                using(var writer=new StreamWriter(channel,new UTF8Encoding(false),1024,true)){writer.AutoFlush=true;
                    byte[] input=new byte[1];var command=new StringBuilder();
                    while(channel.IsConnected){int count=await channel.ReadAsync(input,0,1);if(count==0)return;
                        if(input[0]!=10){if(command.Length>=16)return;command.Append((char)input[0]);continue;}
                        if(command.ToString().TrimEnd('\r')!="snapshot")return;command.Clear();
                        QuickContext current;lock(gate){if(disposed)return;current=context;}
                        using(var buffer=new MemoryStream()){new DataContractJsonSerializer(typeof(QuickContext)).WriteObject(buffer,current);await writer.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray()));System.Threading.Interlocked.Increment(ref snapshots);}
                    }
                }
            }catch{ /* 面板崩溃、断开和宿主关闭只终止该通道，不影响主页。 */ }
            finally{channel.Dispose();}
        }
        /// <summary>断开通道使子进程退出；不终止任意用户进程。</summary>
        public void Dispose(){lock(gate){disposed=true;if(pipe!=null)pipe.Dispose();if(process!=null)process.Dispose();}}
    }
}
