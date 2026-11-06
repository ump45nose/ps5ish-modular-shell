using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>首页少量最近游戏的横条：选中卡固定锚点，其他卡随选择滑动，库页仍独立虚拟化。</summary>
    public sealed class HomeCarouselPanel : Panel
    {
        public bool ReducedMotion { get; set; }
        /// <summary>首页横条进入后再放大首项，避免先出现大卡片再滑入。</summary>
        public void PlayEntrance()
        {
            if(ReducedMotion||InternalChildren.Count==0)return;
            var group=((FrameworkElement)InternalChildren[0]).RenderTransform as TransformGroup;if(group==null)return;
            var scale=group.Children[0] as ScaleTransform;if(scale==null)return;
            foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty})scale.BeginAnimation(property,new DoubleAnimation(1,1.5,TimeSpan.FromMilliseconds(350)){BeginTime=TimeSpan.FromMilliseconds(250),EasingFunction=new BackEase{Amplitude=.12,EasingMode=EasingMode.EaseOut}});
        }
        /// <summary>卡片按统一基准测量，放大只发生在呈现层，不让图片原始尺寸撑开页面。</summary>
        protected override Size MeasureOverride(Size available)
        {
            foreach(UIElement child in InternalChildren)child.Measure(new Size(80,80));
            return new Size(Double.IsInfinity(available.Width)?1180:available.Width,140);
        }
        /// <summary>计算顶部对齐的位置，并从当前动画值续接；快速连按不会跳回旧位置。</summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            var owner=ItemsControl.GetItemsOwner(this) as ListBox;
            int selected=owner==null?0:Math.Max(0,owner.SelectedIndex);
            for(int i=0;i<InternalChildren.Count;i++){
                var child=(FrameworkElement)InternalChildren[i];
                var group=child.RenderTransform as TransformGroup;
                bool fresh=group==null;
                if(fresh){group=new TransformGroup();group.Children.Add(new ScaleTransform());group.Children.Add(new TranslateTransform());child.RenderTransform=group;child.RenderTransformOrigin=new Point(0,0);}
                var scale=(ScaleTransform)group.Children[0];var move=(TranslateTransform)group.Children[1];
                // 锚点前保留上一张小卡；后继卡为选中卡额外留出 40 像素，避免放大遮挡。
                double x=80+(i-selected)*84+(i>selected?40:0);
                double factor=i==selected?1.5:1;
                child.Arrange(new Rect(0,6,80,80));Panel.SetZIndex(child,i==selected?10:0);
                Animate(move,TranslateTransform.XProperty,x,fresh||ReducedMotion);
                Animate(scale,ScaleTransform.ScaleXProperty,factor,fresh||ReducedMotion);
                Animate(scale,ScaleTransform.ScaleYProperty,factor,fresh||ReducedMotion);
            }
            return finalSize;
        }
        /// <summary>使用短促减速过渡；减少动态效果直接落在最终位置。</summary>
        private void Animate(Animatable target,DependencyProperty property,double value,bool immediate)
        {
            if(immediate){target.BeginAnimation(property,null);target.SetValue(property,value);return;}
            if(Math.Abs((double)target.GetValue(property)-value)<.001)return;
            var duration=TryFindResource("FocusFadeMs");target.BeginAnimation(property,new DoubleAnimation(value,TimeSpan.FromMilliseconds(duration is double?(double)duration:260)){EasingFunction=new BackEase{Amplitude=.12,EasingMode=EasingMode.EaseOut}});
        }
    }
}
