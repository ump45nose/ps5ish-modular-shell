using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using AchievementAdapter;

/// <summary>直接使用真实的两个模板验证500条、尺寸、防剧透和通知去重。</summary>
public static class TrophyViewTests
{
    /// <summary>离屏测试不连接平台，不触发真实成就。</summary>
    [STAThread] public static int Main()
    {
        try{
            var summary=new TrophySnapshot{Available=true,Unlocked=45,Total=500,Gold="6",Silver="31",Bronze="8",Platinum=false};
            summary.Items=Enumerable.Range(0,500).Select(i=>(object)new {ApiName="id-"+i,Name="测试奖杯 "+i,Description="完整说明："+new string('长',240),Hidden=i%3==0,Unlocked=i<45}).ToArray();
            var page=new TrophyDetails(new Border(),Guid.NewGuid().ToString());page.Background=new SolidColorBrush(Color.FromRgb(14,19,27));
            var resources=(ResourceDictionary)System.Windows.Markup.XamlReader.Load(File.OpenRead(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"themes","components.controls.xaml")));page.Resources.MergedDictionaries.Add(resources);page.Resources["Text"]=Brushes.White;
            ((TrophySummary)page.Children[0]).Snapshot=summary;page.SetValue(TrophyDetails.SnapshotProperty,summary);
            var list=page.Children.OfType<ListBox>().Single();
            foreach(bool horizontal in new[]{false,true}){
                page.SetLayout(horizontal);page.Measure(new Size(1180,610));page.Arrange(new Rect(0,0,1180,610));page.UpdateLayout();
                list.SelectedIndex=499;list.ScrollIntoView(list.SelectedItem);page.UpdateLayout();
                var last=list.ItemContainerGenerator.ContainerFromIndex(499) as ListBoxItem;if(last==null||last.ActualHeight<=0)throw new Exception("末项未生成");
                if(horizontal&&last.ActualWidth>275||!horizontal&&last.ActualHeight>110)throw new Exception("奖杯项目被拉伸为巨型卡片");
                Console.WriteLine("PASS 500项"+(horizontal?"横向":"纵向")+"末项可达且项目尺寸受限");
                page.SetLayout(!horizontal);page.UpdateLayout();if(list.SelectedIndex!=499)throw new Exception("切换布局丢失选择");page.SetLayout(horizontal);
                list.SelectedIndex=0;list.ScrollIntoView(list.SelectedItem);page.UpdateLayout();
                var bitmap=new RenderTargetBitmap(1180,610,96,96,PixelFormats.Pbgra32);bitmap.Render(page);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,horizontal?"trophy-cards-fixture.png":"trophy-rows-fixture.png")))png.Save(file);
            }
            // 概览进度必须来自同一个已验证快照，防止 PA 的两个绑定短暂不一致。
            var home=new TrophySummary(new Border(),Guid.NewGuid().ToString());home.Snapshot=summary;
            var progress=(ProgressBar)typeof(TrophySummary).GetField("homeProgress",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(home);
            if(Math.Abs(progress.Value-9)>0.001)throw new Exception("首页进度线与快照不一致");
            home.Snapshot=new TrophySnapshot{Available=true,Unlocked=500,Total=500,Platinum=true};home.SetReducedMotion(true);
            var label=(TextBlock)typeof(TrophySummary).GetField("platinumLabel",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(home);
            if(label.Visibility!=Visibility.Visible||!(label.Foreground is LinearGradientBrush)||progress.Value!=100)throw new Exception("白金文字与完成进度缺失");
            Console.WriteLine("PASS 首页进度线、白金渐变文字与快照一致");
            home.Snapshot=new TrophySnapshot();if(progress.Value!=100||label.Visibility!=Visibility.Visible)throw new Exception("刷新空窗丢失同游戏有效快照");
            var fresh=new TrophySummary(new Border(),Guid.NewGuid().ToString());if(fresh.Snapshot!=null&&fresh.Snapshot.Available)throw new Exception("快照跨游戏泄漏");
            Console.WriteLine("PASS PA 刷新空窗保留当前游戏统计且不跨游戏共享");
            var hidden=TrophyCard.Read(new {Hidden=true,Unlocked=false,Name="剧透",Description="剧透",Icon="secret.png"},false);if(!hidden.Masked||hidden.Title=="剧透"||hidden.Image!=null)throw new Exception("隐藏内容泄露");Console.WriteLine("PASS 隐藏内容不进入展示模型");
            if(TrophyCard.Read(new {Unlocked=false,Hidden=false,TrophyType="gold"},false).Tier!="金奖杯"||TrophyCard.Read(new {Unlocked=false,Hidden=false,Rarity="Rare"},false).Tier!="稀有度 · 稀有")throw new Exception("PA 等级或稀有度未呈现");
            Console.WriteLine("PASS 原生奖杯等级与 PA 稀有度兼容");
            var gate=new UnlockGate();var now=DateTime.UtcNow;var unlocked=new {PlayniteGameId="game",ApiName="new",UnlockTimeUtc=now};
            if(!gate.Accept(unlocked,now)||gate.Accept(unlocked,now))throw new Exception("去重失败");
            if(gate.Accept(new {PlayniteGameId="game",ApiName="old",UnlockTimeUtc=now.AddDays(-1)},now)||gate.Accept(new {PlayniteGameId="game",ApiName="preview",IsPreview=true},now)||gate.Accept(new {PlayniteGameId="game",ApiName="progress",IsProgressUpdate=true},now))throw new Exception("历史或非解锁事件误响");
            Console.WriteLine("PASS 解锁去重及历史、预览、进度过滤");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
