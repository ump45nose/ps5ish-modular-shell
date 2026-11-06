using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ModularShell.PlayniteIntegration
{
    public sealed partial class ShellControl
    {
        [DataContract] private sealed class OutputEvidence
        {
            [DataMember] public string gameId,kind,path,utc;
            [DataMember] public int outputWidth,outputHeight;
        }
        /// <summary>实际上传到WPF的像素证据，源尺寸另见素材清单，不把窗口大小当解码尺寸。</summary>
        private void RecordOutput(string kind,string path,int width,int height)
        {
            try{var evidence=new OutputEvidence {gameId=selectedId,kind=kind,path=path,utc=DateTime.UtcNow.ToString("o"),outputWidth=width,outputHeight=height};using(var file=File.Create(Path.Combine(dataRoot,"game-"+kind+"-output.json")))new DataContractJsonSerializer(typeof(OutputEvidence)).WriteObject(file,evidence);}catch{}
        }
        private Button automaticSettings;
        private System.Threading.CancellationTokenSource assetRefresh;
        private bool pendingImages,pendingPlayback;
        /// <summary>换游戏和卸载撤销旧刷新，最多保留一项等待中的图片解码。</summary>
        private void CancelAssetRefresh(){if(assetRefresh!=null){assetRefresh.Cancel();assetRefresh.Dispose();assetRefresh=null;}pendingImages=false;pendingPlayback=false;}
        /// <summary>可选能力存在时才显示管理入口，旧素材插件不受影响。</summary>
        private void InitializeAutomaticAssets()
        {
            var automatic=assets as IAutomaticAssets;if(automatic==null)return;
            automatic.AssetChanged+=AutomaticAssetChanged;automatic.SetPaused(busy);
            automatic.Synchronize(allCards.Select(c=>c.Item).ToList());
            // 本次音乐查找修复后只重试一次缺失项，不覆盖锁定素材，也不逐次启动重复下载。
            var musicRepair=Path.Combine(dataRoot,"music-discovery-20260911.done");
            if(automatic.Enabled&&!File.Exists(musicRepair)){foreach(var card in allCards){var found=assets.Find(card.Item);if(found==null||String.IsNullOrWhiteSpace(found.MusicPath))automatic.Request(card.Item,true);}try{File.WriteAllText(musicRepair,DateTime.UtcNow.ToString("o"));}catch{}}
            if(automaticSettings==null){
                var row=((Button)layout.FindName("SaveSettings")).Parent as Panel;var parent=row.Parent as Panel;
                automaticSettings=new Button {Content="自动素材 · 85 英寸 4K",Margin=new Thickness(10)};
                automaticSettings.SetResourceReference(StyleProperty,"ActionStyle");automaticSettings.Click+=(s,e)=>OpenAutomaticAssets();parent.Children.Add(automaticSettings);
            }
        }
        /// <summary>后台通知回到UI后再核对完整身份与页面生命周期；不重建奖杯或重播扫光。</summary>
        private void AutomaticAssetChanged(object sender,AssetChangedEventArgs args)
        {
            Dispatcher.BeginInvoke(new Action(async()=>{
                var card=games.SelectedItem as GameCard;var current=card==null?null:card.Item;
                if(!loaded||busy||current==null||selection==null||selection.IsCancellationRequested||current.Provider!=args.Item.Provider||current.SourceInstance!=args.Item.SourceInstance||current.ItemId!=args.Item.ItemId)return;
                pendingImages|=args.ImagesChanged;pendingPlayback|=args.PlaybackChanged;
                if(assetRefresh!=null){assetRefresh.Cancel();assetRefresh.Dispose();}
                assetRefresh=System.Threading.CancellationTokenSource.CreateLinkedTokenSource(selection.Token);var token=assetRefresh.Token;
                try{
                    // 合并短时间内多张图片到达，避免同时启动多份4K解码。
                    await System.Threading.Tasks.Task.Delay(150,token);
                    if(pendingImages)await LoadAtmosphere(current,token,true);token.ThrowIfCancellationRequested();
                    if(pendingPlayback){UpdateTrailerAvailability();BeginPreview(false);}pendingImages=false;pendingPlayback=false;
                }catch{}
            }));
        }
        /// <summary>设置直接调用可选素材接口，主题不包含下载与官方来源判断。</summary>
        private void OpenAutomaticAssets()
        {
            var automatic=assets as IAutomaticAssets;if(automatic==null)return;
            var card=games.SelectedItem as GameCard;var item=card==null?null:card.Item;
            var window=new Window {Owner=ownerWindow,Title="自动素材 · 85 英寸 4K",Width=900,Height=640,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=new SolidColorBrush(Color.FromRgb(20,27,42)),Foreground=Brushes.White};
            var panel=new DockPanel {Margin=new Thickness(24)};window.Content=panel;
            var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);
            var enabled=new CheckBox {Content="自动获取已安装及当前游戏",IsChecked=automatic.Enabled,Margin=new Thickness(8)};DockPanel.SetDock(enabled,Dock.Top);panel.Children.Add(enabled);
            var preferences=assets as IAssetLanguageSettings;
            if(preferences!=null){
                var languages=new[]{"zh-Hans","zh-Hant","en","ja","de","fr","auto"};var labels=new[]{"简体中文","繁体中文","英语","日语","德语","法语","不限语言"};
                var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(8)};DockPanel.SetDock(row,Dock.Top);panel.Children.Add(row);
                row.Children.Add(new TextBlock {Text="预告优先语言（同清晰度优先）",VerticalAlignment=VerticalAlignment.Center});
                var choice=new ComboBox {ItemsSource=labels,SelectedIndex=Array.IndexOf(languages,preferences.PreferredLanguage),MinWidth=150,Margin=new Thickness(12,0,0,0)};row.Children.Add(choice);
                choice.SelectionChanged+=(s,e)=>{if(choice.SelectedIndex>=0)preferences.SetPreferredLanguage(languages[choice.SelectedIndex]);};
            }
            var text=new TextBox {TextWrapping=TextWrapping.Wrap,IsReadOnly=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.Transparent,Foreground=Brushes.White};panel.Children.Add(text);
            Action refresh=()=>text.Text=(item==null?"未选择游戏":item.Title)+"\n"+automatic.Describe(item);
            Action<string,Action> button=(title,action)=>{var control=new Button {Content=title,Padding=new Thickness(14,8,14,8),Margin=new Thickness(5)};control.Click+=(s,e)=>{try{action();refresh();}catch(Exception ex){text.Text=ex.Message;}};actions.Children.Add(control);};
            enabled.Click+=(s,e)=>{automatic.SetEnabled(enabled.IsChecked==true);if(automatic.Enabled)automatic.Synchronize(allCards.Select(c=>c.Item).ToList());refresh();};
            button("重新获取",()=>automatic.Request(item,true));button("锁定",()=>automatic.SetLocked(item,true));button("解锁",()=>automatic.SetLocked(item,false));button("清理自动缓存",()=>{media.Cancel();automatic.ClearAutomatic(item);});button("返回",()=>window.Close());
            var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(s,e)=>refresh();window.Closed+=(s,e)=>timer.Stop();refresh();timer.Start();window.ShowDialog();
        }
    }
}
