using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EmbyBrowser
{
    /// <summary>影视组件开关和服务器媒体目录独立保存，目录ID按账号隔离，不保存密码。</summary>
    [DataContract] public sealed class LibrarySettings
    {
        [DataMember] public int schema=1;
        [DataMember] public bool enabled;
        [DataMember] public string libraryId,libraryName,identity;
        /// <summary>未配置时默认关闭；旧连接保留供用户启用。</summary>
        public static LibrarySettings Load(string root){try{var value=Client.Read<LibrarySettings>(Path.Combine(root,"emby","library-settings.json"));return value.schema==1?value:new LibrarySettings();}catch{return new LibrarySettings();}}
        /// <summary>完整JSON先落盘再原子替换，保留上一版本。</summary>
        public static void Save<T>(string path,T value){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var stream=File.Create(temp))new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
        /// <summary>仅本地保存开关与可见目录选择。</summary>
        public void Save(string root){Save(Path.Combine(root,"emby","library-settings.json"),this);}
    }
    public sealed partial class Client
    {
        /// <summary>兼容Playnite自带处理器；不支持单实例TLS选择时沿用系统协议，证书校验和拒绝重定向始终保留。</summary>
        internal static HttpClientHandler CreateHandler()
        {
            var handler=new HttpClientHandler {AllowAutoRedirect=false};
            try{handler.SslProtocols=SslProtocols.Tls12;}catch(PlatformNotSupportedException){ /* 旧System.Net.Http依赖系统TLS协商，不修改宿主全局设置。 */ }
            return handler;
        }
        [DataContract] private sealed class LoginBody { [DataMember] public string Username,Pw; }
        [DataContract] private sealed class LoginUser { [DataMember] public string Id; }
        [DataContract] private sealed class LoginResult { [DataMember] public string AccessToken,ServerId; [DataMember] public LoginUser User; }
        public string Identity {get{return connection.Server+"|"+connection.ServerId+"|"+connection.UserId;}}
        /// <summary>校验HTTPS及纯服务器路径；更换地址必须重新认证，禁止重定向携带密码。</summary>
        public static string NormalizeServer(string server){Uri uri;if(!Uri.TryCreate(server,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new ArgumentException("请输入有效HTTPS服务器地址");string value=uri.AbsoluteUri.TrimEnd('/');return value.EndsWith("/emby",StringComparison.OrdinalIgnoreCase)?value:value+"/emby";}
        /// <summary>密码只用于单次TLS请求；返回令牌使用CurrentUser DPAPI并绑定账号与服务器。</summary>
        public static async Task LoginAsync(string root,string server,string username,string password,CancellationToken token)
        {
            server=NormalizeServer(server);string device=Guid.NewGuid().ToString("N");byte[] payload;
            using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(LoginBody)).WriteObject(stream,new LoginBody {Username=username,Pw=password});payload=stream.ToArray();}password=null;
            try{
                using(var handler=CreateHandler())
                using(var http=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(20)})
                using(var request=new HttpRequestMessage(HttpMethod.Post,server+"/Users/AuthenticateByName")){
                    request.Headers.TryAddWithoutValidation("Authorization","Emby Client=\"Modular Shell\", Device=\"Windows\", DeviceId=\""+device+"\", Version=\"0.8.0\"");request.Content=new ByteArrayContent(payload);request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    using(var response=await http.SendAsync(request,token).ConfigureAwait(false)){
                        if(!response.IsSuccessStatusCode)throw new IOException("Emby登录失败，请检查地址、账号及密码");
                        var bytes=await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);if(bytes.Length>1024*1024)throw new IOException("登录响应过大");
                        LoginResult result;try{result=Decode<LoginResult>(bytes);}finally{Array.Clear(bytes,0,bytes.Length);}
                        if(result==null||result.User==null||String.IsNullOrEmpty(result.AccessToken))throw new IOException("登录响应无效");CheckId(result.User.Id);CheckId(result.ServerId);
                        var value=new Connection {schema=1,Server=server,ServerId=result.ServerId,UserId=result.User.Id,DeviceId=device};
                        var plain=Encoding.UTF8.GetBytes(result.AccessToken);try{value.ProtectedToken=Convert.ToBase64String(ProtectedData.Protect(plain,Encoding.UTF8.GetBytes(Entropy(value)),DataProtectionScope.CurrentUser));}finally{Array.Clear(plain,0,plain.Length);result.AccessToken=null;}
                        token.ThrowIfCancellationRequested();LibrarySettings.Save(Path.Combine(root,"emby","connection.json"),value);
                    }
                }
            }catch(HttpRequestException){throw new IOException("Emby连接失败，请检查网络与地址");}finally{Array.Clear(payload,0,payload.Length);}
        }
    }
    /// <summary>组件提供配置页，宿主不读取凭据。媒体目录选择服务器真实Views，不猜本地文件路径。</summary>
    public sealed class ConfigurationView : StackPanel
    {
        private readonly string root;
        private readonly CheckBox enabled=new CheckBox {Content="启用额外影视库组件 · Emby",Margin=new Thickness(0,0,0,16)};
        private readonly TextBox server=new TextBox(),username=new TextBox();
        private readonly PasswordBox password=new PasswordBox();
        private readonly ComboBox libraries=new ComboBox {Margin=new Thickness(0,8,0,8)};
        private readonly TextBlock status=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private CancellationTokenSource cancel=new CancellationTokenSource();
        private Client client;
        /// <summary>构建可选组件入口；页面卸载中止登录和媒体目录读取。</summary>
        public ConfigurationView(string root)
        {
            this.root=root;Margin=new Thickness(24);
            // 显式设置输入框对比度，不依赖宿主全局的白色文字样式。
            foreach(var field in new[]{server,username}){field.Background=new SolidColorBrush(Color.FromRgb(35,45,62));field.Foreground=Brushes.White;}password.Background=new SolidColorBrush(Color.FromRgb(35,45,62));password.Foreground=Brushes.White;var saved=LibrarySettings.Load(root);enabled.IsChecked=saved.enabled;Children.Add(enabled);
            AddLabel("Emby服务器地址（HTTPS）");Children.Add(server);AddLabel("用户名");Children.Add(username);AddLabel("密码（不保存明文）");Children.Add(password);
            try{server.Text=Client.Read<Connection>(Path.Combine(root,"emby","connection.json")).Server;}catch{}
            var login=new Button {Content="登录并读取影视目录",Margin=new Thickness(0,12,0,12)};Children.Add(login);
            login.Click+=async(s,e)=>{login.IsEnabled=false;try{string secret=password.Password;password.Clear();await Client.LoginAsync(root,server.Text,username.Text,secret,cancel.Token);secret=null;await LoadLibraries();}catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}finally{login.IsEnabled=true;}};
            AddLabel("影视剧目录（Emby账号可见媒体库；不是本地磁盘路径）");Children.Add(libraries);
            var refresh=new Button {Content="读取已登录账号的目录"};Children.Add(refresh);refresh.Click+=async(s,e)=>{try{await LoadLibraries();}catch(OperationCanceledException){}catch{status.Text="目录读取失败，请重新登录或检查网络";}};
            var save=new Button {Content="保存组件与目录",Margin=new Thickness(0,14,0,8)};Children.Add(save);
            save.Click+=(s,e)=>{try{var choice=libraries.SelectedItem as ComboBoxItem;var item=choice==null?null:choice.Tag as MediaItem;var next=LibrarySettings.Load(root);next.enabled=enabled.IsChecked==true;if(item!=null&&client!=null){next.libraryId=item.Id;next.libraryName=item.Name;next.identity=client.Identity;}next.Save(root);status.Text="已保存，关闭本窗口后生效";}catch{status.Text="设置未保存，原配置保留";}};Children.Add(status);
            Loaded+=async(s,e)=>{try{await LoadLibraries();}catch(OperationCanceledException){}catch{status.Text="填写服务器与账号后登录";}};
            Unloaded+=(s,e)=>{cancel.Cancel();password.Clear();};
        }
        /// <summary>统一字段标签与间距。</summary>
        private void AddLabel(string text){Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,5)});}
        /// <summary>从公开Views读取准确ID，旧目录仅在服务器及账号相同时恢复。</summary>
        private async Task LoadLibraries()
        {
            var current=new Client(root);var items=await current.Libraries(cancel.Token);cancel.Token.ThrowIfCancellationRequested();client=current;
            // DTO 使用公开字段，WPF 属性绑定无法读取；显式显示名称并保留原始目录 ID。
            var choices=new[]{new MediaItem {Id=null,Name="全部可见影视目录"}}.Concat(items).Select(item=>new ComboBoxItem {Content=item.Name,Tag=item}).ToList();
            libraries.ItemsSource=choices;var saved=LibrarySettings.Load(root);
            libraries.SelectedItem=saved.identity==client.Identity?choices.FirstOrDefault(i=>((MediaItem)i.Tag).Id==saved.libraryId)??choices[0]:choices[0];
            status.Text="已连接，读取到 "+items.Length+" 个影视目录";
        }
    }
}

