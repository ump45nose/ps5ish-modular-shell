using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using LocalAssets;
using ModularShell;

/// <summary>从SDK目录快照自动取最近已安装Steam游戏，不注入逐游戏素材URL。</summary>
public static class AutomaticAssetsLive
{
    /// <summary>参数为SDK快照、空验收缓存目录、最多游戏数和运行秒数。</summary>
    public static int Main(string[] args)
    {
        try{
            List<GameItem> catalog;using(var stream=File.OpenRead(args[0]))catalog=(List<GameItem>)new DataContractJsonSerializer(typeof(List<GameItem>)).ReadObject(stream);
            var items=catalog.Where(i=>i.IsInstalled&&String.Equals(i.Platform,"Steam",StringComparison.OrdinalIgnoreCase)).OrderByDescending(i=>i.LastActivity).Take(Int32.Parse(args[2])).ToList();
            using(var module=new Module()){
                module.Configure(new ModuleContext {UserDataRoot=args[1]});module.Initialize();module.AssetChanged+=(s,e)=>Console.WriteLine(DateTime.Now.ToString("HH:mm:ss")+" 入库刷新 "+e.Item.Title+(e.ImagesChanged?" 图片":" 播放素材"));module.Synchronize(items);
                var until=DateTime.UtcNow.AddSeconds(Int32.Parse(args[3]));while(DateTime.UtcNow<until){Thread.Sleep(2000);if(module.Describe(null).Contains("队列：0 · 运行：0"))break;}
                Directory.CreateDirectory(args[1]);File.WriteAllText(Path.Combine(args[1],"quality-report.txt"),String.Join("\n\n",items.Select(i=>i.Title+"\n"+module.Describe(i))));
                Console.WriteLine(module.Describe(null));
            }return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
