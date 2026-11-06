using System;
using System.Collections;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ModularShell;

namespace AchievementAdapter
{
    /// <summary>只读公开主题字段生成的当前游戏快照，不引用 PA 实现程序集。</summary>
    public sealed class TrophySnapshot
    {
        public bool Available, Platinum;
        public int Unlocked, Total;
        public string Gold="—", Silver="—", Bronze="—"; public object[] Items=new object[0];

        /// <summary>读取公开属性；缺字段作为未就绪，不猜测内部缓存或私有类型。</summary>
        internal static object Property(object value,string name)
        {
            if(value==null)return null;
            var property=value.GetType().GetProperty(name);
            return property==null?null:property.GetValue(value,null);
        }
        /// <summary>按 GUID 比较来源，字符串大小写或格式不影响同一游戏识别。</summary>
        internal static bool SameId(object value,string id)
        {
            Guid left,right;return Guid.TryParse(System.Convert.ToString(value),out left)&&Guid.TryParse(id,out right)&&left==right;
        }
        /// <summary>计数只接受真实非负整数，缺失字段不能显示为零。</summary>
        private static string Count(object value)
        {
            int count;return value!=null&&Int32.TryParse(System.Convert.ToString(value),out count)&&count>=0?count.ToString(CultureInfo.InvariantCulture):"—";
        }
        /// <summary>当前上下文匹配后取逐游戏兼容计数，Capstone 优先于全成就完成标志。</summary>
        public static TrophySnapshot Read(string id,object selectedId,object summaries,object achievements,object completed,object unlocked,object total)
        {
            var result=new TrophySnapshot();
            if(!SameId(selectedId,id)||!Int32.TryParse(System.Convert.ToString(total),out result.Total)||result.Total<=0||!Int32.TryParse(System.Convert.ToString(unlocked),out result.Unlocked)||result.Unlocked<0||result.Unlocked>result.Total)return result;
            result.Available=true;
            foreach(var summary in summaries as IEnumerable ?? new object[0])
                if(SameId(Property(summary,"GameId"),id)){
                    result.Gold=Count(Property(summary,"GoldCount"));result.Silver=Count(Property(summary,"SilverCount"));result.Bronze=Count(Property(summary,"BronzeCount"));break;
                }
            // null 列表代表尚未加载，不能误判为“没有最终成就”。
            var list=achievements as IEnumerable;
            if(list==null)return result; result.Items=list.Cast<object>().ToArray();
            bool hasCapstone=false,capstoneUnlocked=false;
            foreach(var achievement in list)if(Object.Equals(Property(achievement,"IsCapstone"),true)){
                hasCapstone=true;capstoneUnlocked|=Object.Equals(Property(achievement,"Unlocked"),true);
            }
            result.Platinum=hasCapstone?capstoneUnlocked:Object.Equals(completed,true);
            return result;
        }
    }

    /// <summary>WPF 只读多绑定；PA 替换公开集合或统计时重新生成完整快照。</summary>
    public sealed class TrophySnapshotConverter : IMultiValueConverter
    {
        public string GameId;
        /// <summary>保持一个来源快照，避免换游戏时沿用上一游戏颜色计数。</summary>
        public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
        {
            return values.Length==6?TrophySnapshot.Read(GameId,values[0],values[1],values[2],values[3],values[4],values[5]):new TrophySnapshot();
        }
        /// <summary>摘要不反向写入 PA。</summary>
        public object[] ConvertBack(object value,Type[] types,object parameter,CultureInfo culture){throw new NotSupportedException();}
    }

    /// <summary>三色和白金仅为摘要呈现；不发送通知、不改变提供者数据。</summary>
    public sealed class TrophySummary : Border, IViewPreferences
    {
        public const string TrophyGeometry="M8,2 L24,2 23,14 Q22,20 17,22 L17,28 23,30 23,33 9,33 9,30 15,28 15,22 Q10,20 9,14 Z M8,5 L2,5 2,10 Q2,17 10,18 L9,14 Q5,14 5,10 L5,8 8,8 Z M24,5 L30,5 30,10 Q30,17 22,18 L23,14 Q27,14 27,10 L27,8 24,8 Z";
        /// <summary>全套奖杯共用分段金属反光，保留等级色。</summary>
        public static Brush MetalBrush(Color color)
        {
            var brush=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,1)};
            brush.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(color.R*.45),(byte)(color.G*.45),(byte)(color.B*.45)),0));brush.GradientStops.Add(new GradientStop(color,.26));brush.GradientStops.Add(new GradientStop(Colors.White,.43));brush.GradientStops.Add(new GradientStop(color,.54));brush.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(color.R*.55),(byte)(color.G*.55),(byte)(color.B*.55)),.8));brush.GradientStops.Add(new GradientStop(color,1));brush.Freeze();return brush;
        }
        public static readonly DependencyProperty SnapshotProperty=DependencyProperty.Register("Snapshot",typeof(TrophySnapshot),typeof(TrophySummary),new PropertyMetadata(null,SnapshotChanged));
        public TrophySnapshot Snapshot {get{return (TrophySnapshot)GetValue(SnapshotProperty);}set{SetValue(SnapshotProperty,value);}}
        private readonly FrameworkElement source;
        private readonly MultiBinding snapshotBinding;
        private readonly TextBlock percent,counts,gold,silver,bronze,missing,platinumCount;
        private readonly StackPanel available,platinum;
        private readonly Border sweep; private readonly Grid tiers; private readonly bool detailed;
        private readonly TranslateTransform sweepPosition=new TranslateTransform(-300,0);
        private bool reduced,wasPlatinum;
        private TrophySnapshot lastValidSnapshot;
        private System.Windows.Shapes.Path homeTrophy;
        private TextBlock platinumLabel;
        private ProgressBar homeProgress;
        private RotateTransform platinumRotation;
        private readonly System.Windows.Threading.DispatcherTimer refreshTimer=new System.Windows.Threading.DispatcherTimer();
        private string refreshId;
        /// <summary>只在当前控件可见、宿主前台时请求 PA 刷新，隐藏及卸载不轮询。</summary>
        private async void RefreshCurrent(object sender,EventArgs args)
        {
            refreshTimer.Interval=TimeSpan.FromSeconds(45);
            var window=Window.GetWindow(this);if(!IsVisible||window!=null&&!window.IsActive)return;
            string status=await TrophyRefresh.Request(refreshId);
            if(!IsLoaded)return;ToolTip=status;
            var binding=BindingOperations.GetMultiBindingExpression(this,SnapshotProperty);if(binding!=null)binding.UpdateTarget();
        }

        /// <summary>建立隐藏公开控件与绑定生命周期，卸载时清除绑定并停止动画。</summary>
        public TrophySummary(FrameworkElement source,string id,bool detailed=false)
        {
            this.source=source;this.detailed=detailed;refreshId=id;refreshTimer.Tick+=RefreshCurrent;Padding=new Thickness(16,12,16,14);CornerRadius=new CornerRadius(3);ClipToBounds=true;
            Background=new SolidColorBrush(Color.FromArgb(195,12,19,26));BorderThickness=new Thickness(1);BorderBrush=Brushes.Transparent;
            var root=new Grid();Child=root;
            var panel=new StackPanel();root.Children.Add(panel);
            var heading=new DockPanel();heading.Children.Add(Label("奖杯",12));
            platinum=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Visibility=Visibility.Collapsed};
            platinum.Children.Add(Icon(Color.FromRgb(171,227,255),17));platinum.Children.Add(Label(" 白金",12));heading.Children.Add(platinum);panel.Children.Add(heading);
            available=new StackPanel {Margin=new Thickness(0,8,0,0)};panel.Children.Add(available);
            var stats=new Grid();stats.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(84)});stats.ColumnDefinitions.Add(new ColumnDefinition());
            var a=new StackPanel();a.Children.Add(Label("完成度",12));percent=Label("—",19);a.Children.Add(percent);stats.Children.Add(a);
            var b=new StackPanel();b.Children.Add(Label("已获得",12));counts=Label("—",19);b.Children.Add(Fit(counts));Grid.SetColumn(b,1);stats.Children.Add(b);available.Children.Add(stats);
            tiers=new Grid {Margin=new Thickness(0,12,0,0),Visibility=detailed?Visibility.Visible:Visibility.Collapsed};
            gold=Tier(tiers,0,Color.FromRgb(237,192,80));silver=Tier(tiers,1,Color.FromRgb(201,213,226));bronze=Tier(tiers,2,Color.FromRgb(192,127,80));platinumCount=Tier(tiers,3,Color.FromRgb(171,227,255));available.Children.Add(tiers);
            missing=Label("奖杯数据暂不可用",13);missing.Margin=new Thickness(0,12,0,4);panel.Children.Add(missing);
            // 首页回归简洁比例与细进度线；详情使用更大的数字适合远距离阅读。
            percent.FontSize=counts.FontSize=detailed?30:19;
            stats.ColumnDefinitions[0].Width=new GridLength(detailed?160:84);
            if(!detailed){var progress=new ProgressBar{Height=2,Maximum=100,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(65,255,255,255)),Margin=new Thickness(0,10,0,0)};progress.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty,new Binding("DataContext.Percent"){Source=source});available.Children.Add(progress);homeProgress=progress;
                // 首页采用奖杯、完成度、获得数量三列，底部保留用户要求的细进度线。
                heading.Visibility=Visibility.Collapsed;Padding=new Thickness(14,12,14,10);CornerRadius=new CornerRadius(0);
                available.Margin=new Thickness(0);stats.ColumnDefinitions.Clear();
                stats.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(48)});
                stats.ColumnDefinitions.Add(new ColumnDefinition());stats.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(a,1);Grid.SetColumn(b,2);a.Margin=new Thickness(7,0,0,0);b.HorizontalAlignment=HorizontalAlignment.Right;
                percent.FontSize=counts.FontSize=24;percent.FontWeight=counts.FontWeight=FontWeights.Light;
                counts.TextAlignment=TextAlignment.Right;
                var emblem=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
                homeTrophy=(System.Windows.Shapes.Path)Icon(Color.FromRgb(194,205,215),35);emblem.Children.Add(homeTrophy);
                platinumLabel=Label("白金",10);platinumLabel.Visibility=Visibility.Collapsed;emblem.Children.Add(platinumLabel);stats.Children.Add(emblem);
            }
            source.Width=1;source.Height=1;source.Opacity=0;source.IsHitTestVisible=false;panel.Children.Add(source);
            sweep=new Border {Width=70,HorizontalAlignment=HorizontalAlignment.Left,IsHitTestVisible=false,Opacity=0,RenderTransform=sweepPosition,Background=new LinearGradientBrush(Colors.Transparent,Color.FromArgb(85,210,241,255),0)};root.Children.Add(sweep);
            snapshotBinding=new MultiBinding {Converter=new TrophySnapshotConverter {GameId=id},Mode=BindingMode.OneWay};
            // SelectedGame 是 Playnite 全局选择；主题控件 GameContext 对应 ModernTheme 的独立选择。
            foreach(var path in new[]{"ModernTheme.SelectedGameId","GamesWithAchievements","Achievements","IsCompleted","UnlockedCount","AchievementCount"})snapshotBinding.Bindings.Add(new Binding("DataContext."+path){Source=source,Mode=BindingMode.OneWay});
            SetBinding(SnapshotProperty,snapshotBinding);
            Loaded+=(s,e)=>{SetBinding(SnapshotProperty,snapshotBinding);Render();refreshTimer.Interval=TimeSpan.FromMilliseconds(800);refreshTimer.Start();};
            Unloaded+=(s,e)=>{refreshTimer.Stop();BindingOperations.ClearBinding(this,SnapshotProperty);StopSweep();wasPlatinum=false;};
        }
        /// <summary>通用偏好由宿主传入，减少动效立即结束进行中的扫光。</summary>
        public void SetReducedMotion(bool value){reduced=value;if(value)StopSweep();}
        /// <summary>属性变更仅在 UI 线程应用。</summary>
        private static void SnapshotChanged(DependencyObject target,DependencyPropertyChangedEventArgs args){((TrophySummary)target).Render();}
        /// <summary>布局统一字号；数字由局部 Viewbox 只缩小以支持四位数。</summary>
        private static TextBlock Label(string text,double size){return new TextBlock {Text=text,FontSize=size,Foreground=Brushes.White,FontWeight=size>=18?FontWeights.SemiBold:FontWeights.Normal};}
        /// <summary>缩小过长数字，不扩大短数字，不裁切。</summary>
        private static Viewbox Fit(UIElement child){return new Viewbox {Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left,Child=child};}
        /// <summary>自有矢量奖杯，颜色区分兼容等级，无原机资源依赖。</summary>
        internal static UIElement Icon(Color color,double size){return new System.Windows.Shapes.Path {Width=size,Height=size+3,Margin=new Thickness(0,0,5,0),Stretch=Stretch.Uniform,Fill=MetalBrush(color),Data=Geometry.Parse("M8,2 L24,2 23,14 Q22,20 17,22 L17,28 23,30 23,33 9,33 9,30 15,28 15,22 Q10,20 9,14 Z M8,5 L2,5 2,10 Q2,17 10,18 L9,14 Q5,14 5,10 L5,8 8,8 Z M24,5 L30,5 30,10 Q30,17 22,18 L23,14 Q27,14 27,10 L27,8 24,8 Z")};}
        /// <summary>三列独立测量，计数与图标一并按剩余空间适配。</summary>
        private static TextBlock Tier(Grid grid,int column,Color color){grid.ColumnDefinitions.Add(new ColumnDefinition());var row=new StackPanel {Orientation=Orientation.Horizontal};row.Children.Add(Icon(color,19));var number=Label("—",18);row.Children.Add(number);var fit=Fit(row);fit.Margin=new Thickness(0,0,8,0);Grid.SetColumn(fit,column);grid.Children.Add(fit);return number;}
        /// <summary>展示有效快照；白金只在本次进入或由未完成变为完成时扫光一次。</summary>
        private void Render()
        {
            if(available==null)return;
            var value=Snapshot??new TrophySnapshot();
            // PA 刷新时会短暂清空全局选择和集合；同一控件仅保留自己游戏的有效快照。
            if(value.Available)lastValidSnapshot=value;
            else if(lastValidSnapshot!=null){value=lastValidSnapshot;ToolTip="正在更新，暂显示上次成功获取的奖杯数据";}
            Visibility=!detailed&&Object.Equals(TryFindResource("HomeSummary"),false)?Visibility.Collapsed:Visibility.Visible;tiers.Visibility=detailed&&!Object.Equals(TryFindResource("DetailColors"),false)?Visibility.Visible:Visibility.Collapsed;bool glow=value.Platinum&&!Object.Equals(TryFindResource("PlatinumGlow"),false);available.Visibility=value.Available?Visibility.Visible:Visibility.Collapsed;missing.Visibility=value.Available?Visibility.Collapsed:Visibility.Visible;
            percent.Text=value.Available?Math.Floor(value.Unlocked*100.0/value.Total).ToString("0")+"%":"—";counts.Text=value.Unlocked+" / "+value.Total;
            if(homeProgress!=null)homeProgress.Value=value.Available?value.Unlocked*100.0/value.Total:0;
            if(homeTrophy!=null){
                platinumLabel.Visibility=value.Platinum?Visibility.Visible:Visibility.Collapsed;
                // 图标和白金文字共享金属渐变，动画仅在可见且允许动效时运行。
                if(!value.Platinum){homeTrophy.Fill=MetalBrush(Color.FromRgb(194,205,215));homeTrophy.Effect=null;platinumLabel.Effect=null;}
                else if(!wasPlatinum||platinumRotation==null){
                    var metal=(LinearGradientBrush)MetalBrush(Color.FromRgb(171,227,255)).Clone();platinumRotation=new RotateTransform(0,.5,.5);metal.RelativeTransform=platinumRotation;
                    homeTrophy.Fill=metal;platinumLabel.Foreground=metal;
                    homeTrophy.Effect=platinumLabel.Effect=glow?new DropShadowEffect{Color=Color.FromRgb(149,223,255),BlurRadius=10,ShadowDepth=0,Opacity=.65}:null;
                }
                if(platinumRotation!=null){if(value.Platinum&&glow&&!reduced&&IsLoaded){if(!platinumRotation.HasAnimatedProperties)platinumRotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(5)){RepeatBehavior=RepeatBehavior.Forever});}else platinumRotation.BeginAnimation(RotateTransform.AngleProperty,null);}
            }
            platinumCount.Text=value.Available?(value.Platinum?"1":"0"):"—";gold.Text=value.Gold;silver.Text=value.Silver;bronze.Text=value.Bronze;platinum.Visibility=value.Platinum?Visibility.Visible:Visibility.Collapsed;
            BorderBrush=glow?new SolidColorBrush(Color.FromArgb(160,172,228,255)):Brushes.Transparent;
            Effect=glow?new DropShadowEffect {Color=Color.FromRgb(135,213,255),BlurRadius=16,ShadowDepth=0,Opacity=.3}:null;
            if(!glow||reduced)StopSweep();
            else if(!wasPlatinum&&IsLoaded){sweep.Opacity=1;sweepPosition.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(-80,ActualWidth+80,TimeSpan.FromMilliseconds(800)){FillBehavior=FillBehavior.Stop});var fade=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(800));sweep.BeginAnimation(OpacityProperty,fade);}
            wasPlatinum=value.Platinum&&IsLoaded;
        }
        /// <summary>离开页面或减少动效时释放动画时钟。</summary>
        private void StopSweep(){if(platinumRotation!=null)platinumRotation.BeginAnimation(RotateTransform.AngleProperty,null);sweepPosition.BeginAnimation(TranslateTransform.XProperty,null);sweep.BeginAnimation(OpacityProperty,null);sweep.Opacity=0;}
    }
}


