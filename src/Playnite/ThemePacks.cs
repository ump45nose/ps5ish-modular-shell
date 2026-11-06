using System;
using System.IO;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ModularShell.PlayniteIntegration
{
    [DataContract]
    public sealed class MotionPack
    {
        [DataMember] public int schema;
        [DataMember] public double video, background, page, focus;
    }
    [DataContract]
    public sealed class AmbiencePack
    {
        [DataMember] public int schema;
        [DataMember] public string top, bottom, symbol;
    }
    /// <summary>数据化动效与程序化氛围资产；只覆盖明确资源，不解释任意 XAML。</summary>
    public static class ThemePacks
    {
        /// <summary>只在现有焦点框上旋转渐变，不叠加第二条白线；停用时释放动画时钟。</summary>
        public static void AnimateRing(FrameworkElement element,bool enabled)
        {
            var item=element as ListBoxItem;if(item==null||item.Template==null)return;
            var ring=item.Template.FindName("Ring",item) as Border;if(ring==null)return;
            AnimateBorder(ring,enabled);
        }
        /// <summary>游戏、设置、筛选和奖杯焦点共用同一条渐变动画与周期。</summary>
        public static void AnimateBorder(Border ring,bool enabled)
        {
            if(ring==null)return;
            var old=ring.BorderBrush as LinearGradientBrush;
            var rotation=old==null?null:old.RelativeTransform as RotateTransform;
            if(enabled&&rotation!=null&&rotation.HasAnimatedProperties)return;
            if(rotation!=null)rotation.BeginAnimation(RotateTransform.AngleProperty,null);
            if(!enabled){ring.ClearValue(Border.BorderBrushProperty);ring.ClearValue(Border.BackgroundProperty);ring.ClearValue(UIElement.RenderTransformProperty);return;}
            // 移动窄光带：长时间停在框外，稳定悬停后从左向右扫过，再回到框外。
            // 使用 Border 的背景绘制，光带天然受微圆角/圆形轮廓限制，不产生方形底边。
            var shine=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,0)};
            for(int i=0;i<3;i++){
                var stop=new GradientStop(i==1?Color.FromArgb(30,224,237,255):Colors.Transparent,-.5+i*.18);shine.GradientStops.Add(stop);
                var sweep=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromSeconds(7),RepeatBehavior=RepeatBehavior.Forever};
                sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-.5+i*.18,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
                sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-.5+i*.18,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.8))));
                sweep.KeyFrames.Add(new LinearDoubleKeyFrame(1.1+i*.18,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(6))));
                stop.BeginAnimation(GradientStop.OffsetProperty,sweep);
            }
            ring.Background=shine;
            Bounce(ring);
            var brush=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,1)};
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(65,119,158,179),0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(190,222,231,232),.48));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(70,145,126,158),1));
            rotation=new RotateTransform(0,.5,.5);brush.RelativeTransform=rotation;ring.BorderBrush=brush;
            rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(8)){RepeatBehavior=RepeatBehavior.Forever});
        }
        /// <summary>框体先收缩再回弹；只使用模板边框变换，不覆盖首页排列与封面缩放。</summary>
        public static void Bounce(Border border)
        {
            var scale=new ScaleTransform(1,1);border.RenderTransform=scale;border.RenderTransformOrigin=new Point(.5,.5);
            foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty}){
                var bounce=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(300)};
                bounce.KeyFrames.Add(new LinearDoubleKeyFrame(.965,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65))));
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.012,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)),new CubicEase{EasingMode=EasingMode.EaseOut}));
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),new CubicEase{EasingMode=EasingMode.EaseOut}));
                scale.BeginAnimation(property,bounce);
            }
        }
        /// <summary>选中封面与边框在独立子元素上缓慢呼吸，不覆盖横条的排列变换。</summary>
        public static void Breathe(FrameworkElement element,bool enabled)
        {
            if(element==null)return;var frame=FindCover(element);if(frame==null)return;
            var old=frame.BorderBrush as SolidColorBrush;if(old!=null&&!old.IsFrozen)old.BeginAnimation(SolidColorBrush.ColorProperty,null);
            frame.ClearValue(Border.BorderBrushProperty);
            var content=frame.Child as FrameworkElement;if(content==null)return;
            var scale=new ScaleTransform(1,1);content.RenderTransform=scale;content.RenderTransformOrigin=new Point(.5,.5);
            // 封面不单独呼吸缩放；整个卡片由导航层缩放，环绕颜色由唯一焦点框负责。

        }
        /// <summary>模板内部只查找主题命名的封面边框，缺少该槽位时安全跳过。</summary>
        private static Border FindCover(DependencyObject root){var border=root as Border;if(border!=null&&border.Name=="CoverFrame")return border;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var result=FindCover(VisualTreeHelper.GetChild(root,i));if(result!=null)return result;}return null;}
        /// <summary>选择状态只驱动卡片缩放；减少动效时保持静态尺寸和焦点环。</summary>
        public static void FocusCard(FrameworkElement element,bool selected,bool reduced,double selectedScale=1.1)
        {
            if(element==null)return;
            // 库页使用稳定细环；首页的放大和位移由专用横条统一负责。
            var container=element as ListBoxItem;
            var ring=container==null||container.Template==null?null:container.Template.FindName("Ring",container) as Border;
            if(ring!=null){
                ring.ClearValue(Border.BorderBrushProperty);
                if(selected)ring.SetResourceReference(Border.BorderBrushProperty,"Ps5FocusBrush");
            }
            var transform=element.RenderTransform as ScaleTransform;
            if(transform==null){transform=new ScaleTransform(1,1);element.RenderTransform=transform;element.RenderTransformOrigin=new Point(.5,.5);}
            // 使用动效包时长；减少动效仍保留选中层级，只取消过程动画。
            double target=selected?selectedScale:1;
            var configured=element.TryFindResource("FocusFadeMs");
            double duration=configured is double?(double)configured:170;
            foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty}){
                if(reduced){transform.BeginAnimation(property,null);transform.SetValue(property,target);}
                else transform.BeginAnimation(property,new DoubleAnimation(target,TimeSpan.FromMilliseconds(duration)){EasingFunction=new BackEase{Amplitude=.12,EasingMode=EasingMode.EaseOut}});
            }
        }
        /// <summary>校验整组数据后提交独立字典，失败保持上一组资源。</summary>
        public static void Apply(FrameworkElement view, string root, string motion, string ambience)
        {
            if ((motion != "soft" && motion != "brisk") || (ambience != "ocean" && ambience != "dusk")) throw new InvalidDataException("未知主题包");
            var m = ThemeComposer.Read<MotionPack>(Path.Combine(root,"themes","motion."+motion+".json"));
            var a = ThemeComposer.Read<AmbiencePack>(Path.Combine(root,"themes","ambience."+ambience+".json"));
            if (m.schema != 1 || a.schema != 1 || String.IsNullOrWhiteSpace(a.symbol) || a.symbol.Length > 4) throw new InvalidDataException("主题包契约错误");
            foreach (double v in new[] {m.video,m.background,m.page,m.focus}) if (Double.IsNaN(v) || Double.IsInfinity(v) || v < 0 || v > 1000) throw new InvalidDataException("动效时间越界");
            var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a.top),(Color)ColorConverter.ConvertFromString(a.bottom),45);
            brush.Freeze();
            var next = new ResourceDictionary { {"VideoFadeMs",m.video},{"BackgroundFadeMs",m.background},{"PageFadeMs",m.page},{"FocusFadeMs",m.focus},{"AmbienceBrush",brush},{"AmbienceSymbol",a.symbol} };
            var previous = view.Resources["__packs"] as ResourceDictionary;
            if (previous != null) view.Resources.MergedDictionaries.Remove(previous);
            // VideoFadeMs 原是直接资源，移除后让有序字典获得覆盖权。
            view.Resources.Remove("VideoFadeMs");
            view.Resources.MergedDictionaries.Add(next); view.Resources["__packs"] = next;
        }
        /// <summary>只表现已发生的状态切换；减少动效直接显示，不改变播放时序。</summary>
        public static void Reveal(UIElement element, FrameworkElement resources, string key, bool reduced, double opacity)
        {
            element.BeginAnimation(UIElement.OpacityProperty,null); element.Opacity = opacity;
            object duration = resources.TryFindResource(key);
            if (!reduced && duration is double && (double)duration > 0)
                element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,opacity,TimeSpan.FromMilliseconds((double)duration)));
        }

        /// <summary>标题与 Logo 轻微上移入场，不影响布局测量或快速选择后的最终位置。</summary>
        public static void RevealContent(FrameworkElement element,FrameworkElement resources,bool reduced)
        {
            Reveal(element,resources,"PageFadeMs",reduced,1);
            var transform=new TranslateTransform();element.RenderTransform=transform;
            var duration=resources.TryFindResource("PageFadeMs");
            if(!reduced && duration is double && (double)duration>0)
                transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(10,0,TimeSpan.FromMilliseconds((double)duration)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
        }
    }
}
