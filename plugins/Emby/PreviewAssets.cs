using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModularShell;

namespace EmbyBrowser
{
    [DataContract] public sealed class ThemeMedia
    {
        [DataMember] public ItemResult ThemeSongsResult,ThemeVideosResult;
    }
    public sealed partial class Client
    {
        /// <summary>公开主题素材接口只读获取候选，下载完成后才交给本地预览；从不写正片进度。</summary>
        public async Task<PreviewAsset> PreviewAssets(string id,CancellationToken cancel)
        {
            CheckId(id);string path="Items/"+id+"/ThemeMedia?UserId="+connection.UserId+"&InheritFromParent=true";
            string metadata=Path.Combine(cacheRoot,Hash(path)+".json");byte[] bytes;
            try{bytes=await Request(path,cancel,2*1024*1024).ConfigureAwait(false);Decode<ThemeMedia>(bytes);cancel.ThrowIfCancellationRequested();SaveCache(metadata,bytes);}
            catch(IOException){cancel.ThrowIfCancellationRequested();if(!File.Exists(metadata))return null;bytes=File.ReadAllBytes(metadata);}
            var media=Decode<ThemeMedia>(bytes);if(media==null)return null;var result=new PreviewAsset();
            if(media.ThemeSongsResult!=null && media.ThemeSongsResult.Items!=null && media.ThemeSongsResult.Items.Length>0)
                result.MusicPath=await CacheTheme(media.ThemeSongsResult.Items[0].Id,false,cancel).ConfigureAwait(false);
            if(media.ThemeVideosResult!=null && media.ThemeVideosResult.Items!=null && media.ThemeVideosResult.Items.Length>0)
                result.VideoPath=await CacheTheme(media.ThemeVideosResult.Items[0].Id,true,cancel).ConfigureAwait(false);
            return result.MusicPath==null && result.VideoPath==null?null:result;
        }
        /// <summary>有限额且按完整服务器账号隔离的网络素材缓存，失败保留旧副本和手动素材。</summary>
        private async Task<string> CacheTheme(string id,bool video,CancellationToken cancel)
        {
            CheckId(id);cancel.ThrowIfCancellationRequested();string path=(video?"Videos/":"Audio/")+id+"/stream?Static=true";
            string destination=Path.Combine(cacheRoot,Hash(path)+(video?".mp4":".mp3"));
            if(File.Exists(destination))return destination;
            try {
                var bytes=await Request(path,cancel,32*1024*1024).ConfigureAwait(false);cancel.ThrowIfCancellationRequested();
                SaveCache(destination,bytes);
                // 来源记录只包含稳定媒体 ID、来源类别和时间，不包含令牌或服务器地址。
                if(File.Exists(destination))SaveCache(destination+".source.json",Encoding.UTF8.GetBytes("{\"schema\":1,\"provider\":\"emby\",\"itemId\":\""+id+"\",\"fetchedUtc\":\""+DateTime.UtcNow.ToString("o")+"\"}"));
                return File.Exists(destination)?destination:null;
            }catch(IOException){cancel.ThrowIfCancellationRequested();return null;}
        }
    }
}
