using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModularShell;

namespace AchievementAdapter
{
    /// <summary>只读奖杯模型；隐藏内容在进入模板之前遮蔽。</summary>
    public sealed class TrophyCard
    {
        public string Key{get;set;} public string Title{get;set;} public string Description{get;set;} public string State{get;set;} public ImageSource Image{get;set;}
        public bool Unlocked{get;set;} public bool Masked{get;set;}
        public string Tier{get;set;} public string UnlockInfo{get;set;} public string RarityInfo{get;set;} public Brush Metal{get;set;}
        /// <summary>只展示来源实际提供的等级、日期及百分比，不按稀有度发明奖杯。</summary>
        public static TrophyCard Read(object row,bool reveal)
        {
            bool unlocked=Object.Equals(TrophySnapshot.Property(row,"Unlocked"),true);
            bool masked=!unlocked&&!reveal&&!Object.Equals(TrophySnapshot.Property(row,"Hidden"),false);
            string tier=Convert.ToString(TrophySnapshot.Property(row,"TrophyType"));
            if(String.IsNullOrWhiteSpace(tier))tier=Convert.ToString(TrophySnapshot.Property(row,"RarityTier"));
            if(String.IsNullOrWhiteSpace(tier))tier=Convert.ToString(TrophySnapshot.Property(row,"Rarity"));
            if(Object.Equals(TrophySnapshot.Property(row,"IsCapstone"),true))tier="Platinum";
            var result=new TrophyCard{Key=Convert.ToString(TrophySnapshot.Property(row,"ApiName")),Unlocked=unlocked,Masked=masked,Title=masked?"隐藏奖杯":Convert.ToString(TrophySnapshot.Property(row,"Name")),Description=masked?"此奖杯的内容尚未公开。":Convert.ToString(TrophySnapshot.Property(row,"Description")),State=unlocked?"已获得":"尚未解锁",Tier=TierLabel(tier),Metal=TrophySummary.MetalBrush(TierColor(tier))};
            if(String.IsNullOrWhiteSpace(result.Key))result.Key=result.Title;
            // 未解锁或缺失日期的项目不显示伪造时间。
            object time=TrophySnapshot.Property(row,"UnlockTimeUtc")??TrophySnapshot.Property(row,"DateUnlocked");DateTime date;
            if(time is DateTime)date=(DateTime)time;else if(time is DateTimeOffset)date=((DateTimeOffset)time).UtcDateTime;else DateTime.TryParse(Convert.ToString(time),out date);
            if(unlocked&&date.Year>1970)result.UnlockInfo=date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            object rarity=TrophySnapshot.Property(row,"GlobalPercentUnlocked")??TrophySnapshot.Property(row,"GlobalPercent");double percent;
            if(!masked&&rarity!=null&&Double.TryParse(Convert.ToString(rarity),out percent)&&percent>=0&&percent<=100)result.RarityInfo=percent.ToString("0.0")+"% 玩家获得";
            var path=masked?null:Convert.ToString(TrophySnapshot.Property(row,"Icon"));
            try{if(!String.IsNullOrWhiteSpace(path)&&System.IO.File.Exists(path)){var image=new BitmapImage();image.BeginInit();image.UriSource=new Uri(System.IO.Path.GetFullPath(path));image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=240;image.EndInit();image.Freeze();result.Image=image;}}catch{}
            return result;
        }
        /// <summary>未知等级保留提供者文字，不设定推测阈值。</summary>
        private static string TierLabel(string tier){switch((tier??"").ToLowerInvariant()){case "common":return "稀有度 · 普通";case "uncommon":return "稀有度 · 少见";case "rare":return "稀有度 · 稀有";case "ultrarare":return "稀有度 · 极稀有";case "gold":return "金奖杯";case "silver":return "银奖杯";case "bronze":return "铜奖杯";case "platinum":return "白金奖杯";default:return String.IsNullOrWhiteSpace(tier)?"未分级奖杯":tier;}}
        /// <summary>未知等级使用中性金属色。</summary>
        private static Color TierColor(string tier){switch((tier??"").ToLowerInvariant()){case "rare":case "gold":return Color.FromRgb(226,182,84);case "common":case "bronze":return Color.FromRgb(184,119,73);case "ultrarare":case "platinum":return Color.FromRgb(151,219,248);default:return Color.FromRgb(191,202,214);}}
    }
    /// <summary>两套独立模板及有限视口，列表不会再扩张成单个全屏卡片。</summary>
    public sealed class TrophyDetails : Grid,IViewPreferences,IPageInput
    {
        public static readonly DependencyProperty SnapshotProperty=DependencyProperty.Register("Snapshot",typeof(TrophySnapshot),typeof(TrophyDetails),new PropertyMetadata(null,(o,e)=>((TrophyDetails)o).Refresh()));
        private readonly TrophySummary summary;
        private readonly ListBox cards=new ListBox{Background=Brushes.Transparent,BorderThickness=new Thickness(0),Margin=new Thickness(0,8,0,8),HorizontalContentAlignment=HorizontalAlignment.Stretch};
        private readonly TextBlock description=new TextBlock{Foreground=Brushes.White,FontSize=16,TextWrapping=TextWrapping.Wrap};
        private readonly Button reveal,layoutButton;
        private readonly Grid detail=new Grid{Background=new SolidColorBrush(Color.FromArgb(245,15,20,28)),Visibility=Visibility.Collapsed};
        private bool showHidden,unlockedFirst,horizontal;
        private string selectedKey;
        private Style baseItemStyle;
        private const string Header="xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        /// <summary>摘要、工具栏、虚拟列表和说明分行，单项详情覆盖同一视口。</summary>
        public TrophyDetails(FrameworkElement source,string gameId)
        {
            Margin=new Thickness(14,0,14,0);RowDefinitions.Add(new RowDefinition{Height=new GridLength(150)});RowDefinitions.Add(new RowDefinition{Height=new GridLength(50)});RowDefinitions.Add(new RowDefinition());RowDefinitions.Add(new RowDefinition{Height=new GridLength(65)});
            summary=new TrophySummary(source,gameId,true){Width=570,HorizontalAlignment=HorizontalAlignment.Left,Background=Brushes.Transparent};Children.Add(summary);
            var controls=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetRow(controls,1);Children.Add(controls);
            Button sort=null;sort=ActionButton("排序：游戏默认",()=>{unlockedFirst=!unlockedFirst;sort.Content=unlockedFirst?"排序：已获得优先":"排序：游戏默认";Refresh();});controls.Children.Add(sort);
            reveal=ActionButton("显示隐藏内容",()=>{showHidden=!showHidden;reveal.Content=showHidden?"隐藏剧透内容":"显示隐藏内容";Refresh();});controls.Children.Add(reveal);
            layoutButton=ActionButton("横向卡片",()=>SetLayout(!horizontal));controls.Children.Add(layoutButton);
            // 明确传递模板与对齐，避免全局样式改变 ContentPresenter 的默认行为。
            cards.ItemContainerStyle=(Style)XamlReader.Parse("<Style "+Header+" TargetType='ListBoxItem'><Setter Property='Margin' Value='3'/><Setter Property='Padding' Value='0'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='VerticalContentAlignment' Value='Center'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border x:Name='Frame' Background='#102D3642' BorderBrush='Transparent' BorderThickness='1'><ContentPresenter Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/></Border><ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='#BFDDE7F3'/><Setter TargetName='Frame' Property='Background' Value='#243B4653'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
            baseItemStyle=cards.ItemContainerStyle;ScrollViewer.SetCanContentScroll(cards,true);VirtualizingStackPanel.SetIsVirtualizing(cards,true);VirtualizingStackPanel.SetVirtualizationMode(cards,VirtualizationMode.Recycling);Grid.SetRow(cards,2);Children.Add(cards);
            var textScroll=new ScrollViewer{Content=description,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(textScroll,3);Children.Add(textScroll);
            cards.SelectionChanged+=(s,e)=>{var selected=cards.SelectedItem as TrophyCard;selectedKey=selected==null?null:selected.Key;description.Text=selected==null?"":selected.Description;if(selected!=null)cards.ScrollIntoView(selected);};
            cards.PreviewMouseDoubleClick+=(s,e)=>{OpenDetail();e.Handled=true;};cards.PreviewMouseWheel+=(s,e)=>{if(horizontal){Move(e.Delta<0?1:-1);e.Handled=true;}};
            Grid.SetRowSpan(detail,4);Children.Add(detail);SetLayout(false);
            Loaded+=(s,e)=>{SetLayout(Object.Equals(TryFindResource("TrophyLayout"),"cards"));SetBinding(SnapshotProperty,new Binding("Snapshot"){Source=summary});};
            Unloaded+=(s,e)=>{BindingOperations.ClearBinding(this,SnapshotProperty);cards.ItemsSource=null;detail.Children.Clear();};
        }
        /// <summary>通过主题资源提供材质，领域模块只提供动作。</summary>
        private Button ActionButton(string label,Action action){var b=new Button{Content=label,Margin=new Thickness(0,0,12,0),Padding=new Thickness(14,6,14,6),FontSize=16,MinHeight=38};b.SetResourceReference(StyleProperty,"ActionStyle");b.Click+=(s,e)=>action();return b;}
        /// <summary>切换真正的横行或卡片模板，排序、隐藏设置与选中项目保留。</summary>
        public void SetLayout(bool useCards)
        {
            horizontal=useCards;var itemStyle=new Style(typeof(ListBoxItem),baseItemStyle);itemStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty,horizontal?268.0:104.0));cards.ItemContainerStyle=itemStyle;layoutButton.Content=horizontal?"纵向列表":"横向卡片";
            string icon="<Grid Width='64' Height='64'><Path Fill='{Binding Metal}' Margin='12' Stretch='Uniform' Data='"+TrophySummary.TrophyGeometry+"'/><Image Source='{Binding Image}' Stretch='Uniform'/></Grid>";
            string badge="<StackPanel Orientation='Horizontal'><Path Width='17' Height='21' Fill='{Binding Metal}' Stretch='Uniform' Data='"+TrophySummary.TrophyGeometry+"'/><TextBlock Text='{Binding Tier}' Margin='8,0,0,0' Foreground='#BCC7D6' FontSize='13'/></StackPanel>";
            string body=horizontal?
                "<Grid Width='222' Height='236' Margin='14'><Grid.RowDefinitions><RowDefinition Height='78'/><RowDefinition Height='52'/><RowDefinition Height='30'/><RowDefinition Height='*'/></Grid.RowDefinitions>"+icon+"<TextBlock Grid.Row='1' Text='{Binding Title}' Foreground='White' FontSize='18' TextWrapping='Wrap' TextTrimming='CharacterEllipsis'/><StackPanel Grid.Row='2'>"+badge+"</StackPanel><StackPanel Grid.Row='3'><TextBlock Text='{Binding State}' Foreground='#C0C6CE' FontSize='14'/><TextBlock Text='{Binding UnlockInfo}' Foreground='#8996A6' FontSize='12'/></StackPanel></Grid>":
                "<Grid Height='90' Margin='12,5'><Grid.ColumnDefinitions><ColumnDefinition Width='82'/><ColumnDefinition Width='*'/><ColumnDefinition Width='210'/></Grid.ColumnDefinitions>"+icon+"<StackPanel Grid.Column='1' VerticalAlignment='Center'><TextBlock Text='{Binding Title}' Foreground='White' FontSize='20' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Description}' Foreground='#B6C0CC' FontSize='14' Margin='0,5,14,0' TextTrimming='CharacterEllipsis'/></StackPanel><StackPanel Grid.Column='2' VerticalAlignment='Center'>"+badge+"<TextBlock Text='{Binding State}' Foreground='#CCD4DE' FontSize='13' Margin='0,5,0,0'/><TextBlock Text='{Binding UnlockInfo}' Foreground='#929EAE' FontSize='12'/></StackPanel></Grid>";
            cards.ItemTemplate=(DataTemplate)XamlReader.Parse("<DataTemplate "+Header+">"+body+"</DataTemplate>");
            var factory=new FrameworkElementFactory(typeof(VirtualizingStackPanel));factory.SetValue(VirtualizingStackPanel.OrientationProperty,horizontal?Orientation.Horizontal:Orientation.Vertical);cards.ItemsPanel=new ItemsPanelTemplate(factory);
            ScrollViewer.SetHorizontalScrollBarVisibility(cards,horizontal?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled);ScrollViewer.SetVerticalScrollBarVisibility(cards,horizontal?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto);if(cards.SelectedItem!=null)cards.ScrollIntoView(cards.SelectedItem);
        }
        /// <summary>以稳定项目键恢复选择，数据缺失显示明确空状态。</summary>
        private void Refresh(){if(cards==null)return;var value=GetValue(SnapshotProperty) as TrophySnapshot;var rows=(value==null||!value.Available?new object[0]:value.Items).Select(row=>TrophyCard.Read(row,showHidden));if(unlockedFirst)rows=rows.OrderByDescending(row=>row.Unlocked);string key=selectedKey;var items=rows.ToArray();cards.ItemsSource=items;cards.SelectedItem=items.FirstOrDefault(x=>x.Key==key)??items.FirstOrDefault();if(items.Length==0)description.Text="奖杯数据尚未就绪";}
        /// <summary>完整描述使用独立滚动详情，返回保留列表位置。</summary>
        private void OpenDetail()
        {
            var item=cards.SelectedItem as TrophyCard;if(item==null)return;detail.Children.Clear();var panel=new StackPanel{Margin=new Thickness(36)};
            panel.Children.Add(new Image{Source=item.Image,Width=100,Height=100,HorizontalAlignment=HorizontalAlignment.Left});panel.Children.Add(new TextBlock{Text=item.Title,FontSize=29,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,22,0,15)});
            panel.Children.Add(new TextBlock{Text=item.Tier+"  ·  "+item.State+"\n"+item.UnlockInfo+"\n"+item.RarityInfo,Foreground=Brushes.LightGray,FontSize=17});panel.Children.Add(new TextBlock{Text=item.Description,FontSize=20,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,22,0,20)});
            var back=ActionButton("返回奖杯",()=>{detail.Visibility=Visibility.Collapsed;cards.Focus();});panel.Children.Add(back);detail.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});detail.Visibility=Visibility.Visible;back.Focus();
        }
        /// <summary>限制索引并将新选中项滚入视口。</summary>
        private void Move(int delta){if(cards.Items.Count==0)return;cards.SelectedIndex=Math.Max(0,Math.Min(cards.Items.Count-1,cards.SelectedIndex+delta));cards.ScrollIntoView(cards.SelectedItem);cards.Focus();}
        /// <summary>手柄动作由当前详情或列表消费，防止穿透到后面的游戏。</summary>
        public bool HandleInput(string action)
        {
            if(detail.Visibility==Visibility.Visible){if(action=="back"){detail.Visibility=Visibility.Collapsed;cards.Focus();return true;}if(action=="up"||action=="down"){var scroll=detail.Children[0] as ScrollViewer;if(scroll!=null)scroll.ScrollToVerticalOffset(scroll.VerticalOffset+(action=="up"?-90:90));return true;}return false;}
            if(action=="back")return false;
            if(cards.IsKeyboardFocusWithin){if(action=="accept"){OpenDetail();return true;}if(action=="up"&&(horizontal||cards.SelectedIndex<=0)){layoutButton.Focus();return true;}if(action=="left"||action=="right"||action=="up"||action=="down"){Move(action=="left"||action=="up"?-1:1);return true;}}
            if(action=="down"){cards.Focus();if(cards.SelectedIndex<0)Move(0);return true;}return false;
        }
        /// <summary>减少动效不改变数据与排序。</summary>
        public void SetReducedMotion(bool value){summary.SetReducedMotion(value);}
    }
}
