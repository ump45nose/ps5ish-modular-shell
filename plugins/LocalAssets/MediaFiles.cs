using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalAssets
{
    /// <summary>每项落盘证据，不以扩展名或URL猜测素材质量。</summary>
    public sealed class MediaRecord
    {
        public string path,kind,url,attribution,sha256,status,codec,frameRate,language;
        public int width,height,audioTracks,sampleRate;
        public long bytes,bitrate,checkedTicks;
        public double duration;
        public int Rank {get{return kind=="background"?MediaFiles.BackgroundRank(width,height):height;}}
    }
    /// <summary>JSON与固定工具调用；仅操作调用者明确提供的缓存或源路径。</summary>
    public static class MediaFiles
    {
        /// <summary>构造有界JSON解析器，避免读取任意巨大网络清单。</summary>
        internal static JavaScriptSerializer Json(){return new JavaScriptSerializer {MaxJsonLength=16*1024*1024,RecursionLimit=80};}
        /// <summary>缺文件返回缺省值，损坏文件必须由上层记录故障。</summary>
        internal static T Read<T>(string path,T fallback){return File.Exists(path)?Json().Deserialize<T>(File.ReadAllText(path)):fallback;}
        /// <summary>单写入者提交索引，失败保持原文件与备份。</summary>
        internal static void Save(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";File.WriteAllText(temp,Json().Serialize(value),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);}
        /// <summary>流式摘要避免整部4K视频读进托管内存。</summary>
        public static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        /// <summary>16:9容许编码舍入；Hero有独立优先级，不冒充4K全屏。</summary>
        public static int BackgroundRank(int width,int height){if(width<1920||height<1080)return 0;bool wide=Math.Abs(width/(double)height-16.0/9)<.03;if(wide&&width>=3840&&height>=2160)return 4;if(wide&&width>=2560&&height>=1440)return 3;if(width>=3840&&height>=1240)return 2;return wide?1:0;}
        /// <summary>Windows参数按CommandLineToArgvW规则引用，不交给shell展开。</summary>
        internal static string Quote(string value)
        {
            var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value??""){
                if(c=='\\'){slashes++;continue;}
                result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;
            }
            // 末尾反斜杠翻倍，避免吞掉参数结束引号；空格与Unicode保持原值。
            result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        /// <summary>隐藏工具进程；同时消费输出，取消/超时终止进程并等待释放文件。</summary>
        internal static string Run(string exe,IEnumerable<string> args,CancellationToken token,int timeout=120000)
        {
            if(!File.Exists(exe))throw new FileNotFoundException("素材工具缺失："+Path.GetFileName(exe));
            var info=new ProcessStartInfo(exe,String.Join(" ",args.Select(Quote))) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            using(var process=Process.Start(info))using(var tree=new ProcessTree(process)){
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();var watch=Stopwatch.StartNew();
                while(!process.WaitForExit(100))if(token.IsCancellationRequested||watch.ElapsedMilliseconds>timeout){try{process.Kill();process.WaitForExit();}catch{}token.ThrowIfCancellationRequested();throw new TimeoutException("素材工具超时："+Path.GetFileName(exe));}
                Task.WaitAll(output,error);token.ThrowIfCancellationRequested();
                if(process.ExitCode!=0)throw new IOException(Path.GetFileName(exe)+" 失败："+error.Result.Substring(0,Math.Min(600,error.Result.Length)));
                if(output.Result.Length>16*1024*1024)throw new IOException("素材工具输出过大");return output.Result;
            }
        }
        /// <summary>Windows Job统一拥有yt-dlp及其ffmpeg/Node子进程，取消不能留下后台下载。</summary>
        private sealed class ProcessTree : IDisposable
        {
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Basic {public long processTime,jobTime;public uint flags;public UIntPtr minimum,maximum;public uint active;public UIntPtr affinity;public uint priority,scheduling;}
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Io {public ulong readOperations,writeOperations,otherOperations,readBytes,writeBytes,otherBytes;}
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Limits {public Basic basic;public Io io;public UIntPtr processMemory,jobMemory,peakProcess,peakJob;}
            [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr security,string name);
            [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job,int kind,ref Limits limits,uint size);
            [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
            private IntPtr handle;
            /// <summary>仅绑定本次启动的进程，关闭句柄时系统终止其整个子进程树。</summary>
            internal ProcessTree(Process process)
            {
                handle=CreateJobObject(IntPtr.Zero,null);var limits=new Limits();limits.basic.flags=0x2000;
                if(handle==IntPtr.Zero||!SetInformationJobObject(handle,9,ref limits,(uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Limits)))||!AssignProcessToJobObject(handle,process.Handle)){
                    int error=System.Runtime.InteropServices.Marshal.GetLastWin32Error();try{if(!process.HasExited)process.Kill();}catch{}Dispose();throw new System.ComponentModel.Win32Exception(error,"无法建立可取消的素材工具进程树");
                }
            }
            /// <summary>正常结束或异常路径都释放内核句柄及迟到子进程。</summary>
            public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
        }
        /// <summary>字典缺项返回null，版本差异不变成错误的低质量默认URL。</summary>
        internal static object Get(object source,string name){var map=source as Dictionary<string,object>;object value;return map!=null&&map.TryGetValue(name,out value)?value:null;}
        /// <summary>网络标量采用固定文化转换。</summary>
        internal static string Text(object source,string name){return Convert.ToString(Get(source,name),System.Globalization.CultureInfo.InvariantCulture);}
        /// <summary>缺少列表等同无候选，不猜默认地址。</summary>
        internal static IEnumerable<object> Items(object source){return source as object[]??new object[0];}
        /// <summary>可缺省的测量字段不能转换出超范围整数。</summary>
        internal static long Number(object source,string name){long value;return Int64.TryParse(Text(source,name),out value)?value:0;}
        /// <summary>解析真实文件头及ffprobe流，记录缺音轨和低清状态；不修改原文件。</summary>
        public static MediaRecord Probe(string path,string kind,string tools,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(!File.Exists(path))throw new FileNotFoundException("素材文件不存在");
            var info=new FileInfo(path);var record=new MediaRecord {path=Path.GetFullPath(path),kind=kind,bytes=info.Length,checkedTicks=DateTime.UtcNow.Ticks,status="accepted"};
            if(kind=="background"||kind=="logo"){
                using(var stream=File.OpenRead(path))using(var image=System.Drawing.Image.FromStream(stream,false,true)){record.width=image.Width;record.height=image.Height;}
                if(kind=="background"&&BackgroundRank(record.width,record.height)==0)record.status="low_quality";
            }else{
                var data=Json().DeserializeObject(Run(Path.Combine(tools,"ffprobe.exe"),new[]{"-v","error","-show_streams","-show_format","-of","json",path},token,30000));
                var format=Get(data,"format");record.bitrate=Number(format,"bit_rate");double duration;Double.TryParse(Text(format,"duration"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out duration);record.duration=duration;
                foreach(var stream in Items(Get(data,"streams"))){if(Text(stream,"codec_type")=="video"&&record.width==0){record.width=(int)Number(stream,"width");record.height=(int)Number(stream,"height");record.codec=Text(stream,"codec_name");record.frameRate=Text(stream,"avg_frame_rate");}if(Text(stream,"codec_type")=="audio"){record.audioTracks++;record.sampleRate=(int)Number(stream,"sample_rate");}}
                if(kind=="video"&&(record.width<1920||record.height<1080))record.status="low_quality";
                // 无音轨视频可静音展示，audioTracks=0明确留证，不覆盖低清拒绝状态。
                if(kind=="music"&&(record.audioTracks==0||record.duration<=0))record.status="invalid_audio";
                if(kind=="video"&&record.status=="accepted")Run(Path.Combine(tools,"ffmpeg.exe"),new[]{"-v","error","-xerror","-i",path,"-frames:v","1","-an","-f","null","-"},token,30000);
            }
            record.sha256=Hash(path);return record;
        }
    }
}
