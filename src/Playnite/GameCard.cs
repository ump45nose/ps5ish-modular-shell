using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>展示层按可见卡片延迟解码，避免 500 个条目同时读图。</summary>
    public sealed class GameCard : INotifyPropertyChanged
    {
        public GameItem Item { get; private set; }
        public string Title { get { return Item.Title; } }
        public bool IsLibraryEntry { get { return Item.Provider=="shell" && Item.ItemId=="library"; } }
        private ImageSource cover;
        private bool requested;
        private readonly CancellationToken token;
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>绑定页面生命周期；卸载后的异步图片不再更新视图。</summary>
        public GameCard(GameItem item, CancellationToken token) { Item = item; this.token = token; }
        public ImageSource Cover
        {
            get { if (!requested) { requested = true; LoadCover(); } return cover; }
        }

        /// <summary>可见卡片首次绑定时异步解码；捕获全部异常，保留标题安全卡。</summary>
        private async void LoadCover()
        {
            try
            {
                cover = await LoadImageAsync(Item.CoverPath, 320, token);
                if (!token.IsCancellationRequested && PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Cover"));
            }
            catch { /* 单张图片失败不影响焦点与启动。 */ }
        }

        /// <summary>仅接受本地文件，在工作线程解码并冻结可跨线程的 BitmapImage。</summary>
        public static Task<ImageSource> LoadImageAsync(string path, int width, CancellationToken token,bool background=false)
        {
            return Task.Run<ImageSource>(() => {
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                int originalWidth,originalHeight;
                using(var input=File.OpenRead(path)){
                    var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                    originalWidth=decoder.Frames[0].PixelWidth;originalHeight=decoder.Frames[0].PixelHeight;
                }
                // 同样约束Playnite原背景回退，不能绕过素材插件的最低门槛。
                if(background&&(originalWidth<1920||originalHeight<1080))return null;
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Min(width,originalWidth); bitmap.UriSource = new Uri(Path.GetFullPath(path)); bitmap.EndInit(); bitmap.Freeze();
                token.ThrowIfCancellationRequested();
                return bitmap;
            }, token);
        }
    }
}
