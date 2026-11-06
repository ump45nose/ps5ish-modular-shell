using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using EmbyBrowser;

/// <summary>无真实账号的身份隔离、取消、历史语义与本地断网回退测试。</summary>
public static class EmbyTests
{
    /// <summary>断言失败中止构建，不输出连接密文或令牌。</summary>
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    /// <summary>测试资料仅写入本轮随机临时目录。</summary>
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"shell-emby-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"emby"));
        try
        {
            bool rejected=false;try{Client.CheckId("../other");}catch{rejected=true;}Check(rejected,"Emby 引用拒绝目录跳转");
            var played=new MediaItem{UserData=new UserData{PlayCount=1}};var resume=new MediaItem{UserData=new UserData{PlaybackPositionTicks=10}};
            Check(Client.RecentItems(new[]{played,resume,new MediaItem{UserData=new UserData()}}).Length==2,"最近观看包含未完播记录并排除未观看内容");
            var connection=new Connection{schema=1,Server="https://example.invalid/emby",UserId="u1",ServerId="s1",DeviceId="d1"};
            connection.ProtectedToken=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("test-only-token"),Encoding.UTF8.GetBytes(Client.Entropy(connection)),DataProtectionScope.CurrentUser));
            string path=Path.Combine(root,"emby","connection.json");Save(path,connection);
            var client=new Client(root);Check(client!=null,"同一 Windows 用户可解密绑定身份的测试令牌");
            Check(!LibrarySettings.Load(root).enabled,"未配置影视额外组件时默认关闭");
            new LibrarySettings {enabled=true,libraryId="folder1",identity=client.Identity}.Save(root);
            var scoped=new Client(root);var scope=typeof(Client).GetField("configuredLibrary",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            Check((string)scope.GetValue(scoped)=="folder1","已登录账号恢复所选Emby服务器目录");
            new LibrarySettings {enabled=true,libraryId="folder1",identity="other-account"}.Save(root);
            Check(scope.GetValue(new Client(root))==null,"切换账号不能沿用其他账号目录");
            Check(Client.NormalizeServer("https://example.invalid:28443")=="https://example.invalid:28443/emby","登录地址规范化保留服务器端口");
            rejected=false;try{Client.NormalizeServer("http://example.invalid");}catch(ArgumentException){rejected=true;}Check(rejected,"登录配置拒绝明文HTTP而不降低证书校验");
            using(var cancellation=new CancellationTokenSource())
            {
                cancellation.Cancel();rejected=false;
                try{client.Browse("library",0,null,cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){rejected=true;}
                Check(rejected,"取消请求在发起网络访问前终止");
            }
            // 关闭的本机端口模拟离线，不接触外部服务器或真实凭证。
            var offline=new Connection{schema=1,Server="https://127.0.0.1:1/emby",UserId="u1",ServerId="s1",DeviceId="d1"};
            offline.ProtectedToken=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("test-only-token"),Encoding.UTF8.GetBytes(Client.Entropy(offline)),DataProtectionScope.CurrentUser));Save(path,offline);
            string cache=Path.Combine(root,"emby","cache",Hash(offline.Server+"|s1|u1"));Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache,Hash("Users/u1/Items/m1")+".json"),"{\"Id\":\"m1\",\"Name\":\"fixture\"}");
            byte[] fixture=new byte[]{1,2,3};File.WriteAllBytes(Path.Combine(cache,Hash("Items/m1/Images/Primary?MaxWidth=300&Quality=80")+".image"),fixture);
            var disconnected=new Client(root);
            Check(disconnected.Detail("m1",CancellationToken.None).GetAwaiter().GetResult().Offline,"详情断网回退明确标记缓存状态");
            Check(disconnected.Poster("m1",CancellationToken.None).GetAwaiter().GetResult().SequenceEqual(fixture),"新鲜海报缓存无需联网");
            using(var stopped=new CancellationTokenSource())
            {
                stopped.Cancel();rejected=false;try{disconnected.Poster("m1",stopped.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){rejected=true;}
                Check(rejected,"取消后不能回流已有海报缓存");
            }
            connection.Server="https://other.invalid/emby";Save(path,connection);rejected=false;
            try{new Client(root);}catch(CryptographicException){rejected=true;}Check(rejected,"修改服务器地址不能转发原登录令牌");
            connection.Server="http://example.invalid/emby";Save(path,connection);rejected=false;
            try{new Client(root);}catch(InvalidDataException){rejected=true;}Check(rejected,"账号客户端拒绝明文 HTTP 连接");
            Check(!typeof(Client).Assembly.GetReferencedAssemblies().Any(a=>a.Name=="Shell.Core" || a.Name=="Shell.Playnite" || a.Name=="Playnite.SDK"),"Emby 独立于游戏宿主实现程序集");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+"："+ex.Message);return 1;}
        finally{Directory.Delete(root,true);}
    }
    /// <summary>构造隔离缓存夹具，测试不修改真实用户缓存。</summary>
    private static string Hash(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","");}
    /// <summary>序列化专用测试连接，不使用真实用户配置。</summary>
    private static void Save(string path,Connection value){using(var stream=File.Create(path))new DataContractJsonSerializer(typeof(Connection)).WriteObject(stream,value);}
}
