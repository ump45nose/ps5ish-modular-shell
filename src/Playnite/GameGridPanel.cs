using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>固定六列的回收式游戏网格，只生成可见行及一行预取。</summary>
    public sealed class GameGridPanel : VirtualizingPanel, IScrollInfo
    {
        private int Columns {get{var value=TryFindResource("LibraryColumns");return value is int?Math.Max(5,Math.Min(8,(int)value)):6;}}
        // 为封面、两行文字、焦点环和缩放留出完整测量空间，避免平台行被裁掉。
        private double RowHeight {get{var value=TryFindResource("LibraryRowHeight");return value is double?(double)value:228;}}
        private Size extent, viewport;
        private double offset,scrollTarget;
        private readonly System.Windows.Threading.DispatcherTimer scrollTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};
        private DateTime scrollStarted;private double scrollFrom;
        /// <summary>滚轮和翻页共用短缓动，拖动滚动条始终直接跟手；卸载停止时钟。</summary>
        public GameGridPanel(){scrollTimer.Tick+=(s,e)=>{
            double t=Math.Min(1,(DateTime.UtcNow-scrollStarted).TotalMilliseconds/150);double eased=1-Math.Pow(1-t,3);
            ApplyOffset(scrollFrom+(scrollTarget-scrollFrom)*eased);if(t>=1)scrollTimer.Stop();
        };Unloaded+=(s,e)=>scrollTimer.Stop();}
        /// <summary>连续滚轮累积到目标位置；减少动效立即到位。</summary>
        private void SmoothScroll(double delta){double target=(scrollTimer.IsEnabled?scrollTarget:offset)+delta;
            if(!IsLoaded||Object.Equals(TryFindResource("Ps5ReducedMotion"),true)){SetVerticalOffset(target);return;}
            scrollFrom=offset;scrollTarget=Math.Max(0,Math.Min(target,Math.Max(0,extent.Height-viewport.Height)));scrollStarted=DateTime.UtcNow;scrollTimer.Start();}

        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public double ExtentWidth { get { return extent.Width; } }
        public double ExtentHeight { get { return extent.Height; } }
        public double ViewportWidth { get { return viewport.Width; } }
        public double ViewportHeight { get { return viewport.Height; } }
        public double HorizontalOffset { get { return 0; } }
        public double VerticalOffset { get { return offset; } }
        public ScrollViewer ScrollOwner { get; set; }

        /// <summary>按滚动视口计算容器范围，通过回收生成器避免全库解码封面。</summary>
        protected override Size MeasureOverride(Size available)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            if (owner == null) return new Size();
            double width = Double.IsInfinity(available.Width) ? 1160 : available.Width;
            double height = Double.IsInfinity(available.Height) ? 450 : available.Height;
            viewport = new Size(width, height);
            extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)Columns) * RowHeight+12);
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, extent.Height-height)));
            if (ScrollOwner != null) ScrollOwner.InvalidateScrollInfo();
            int first = Math.Max(0, (int)(offset / RowHeight) * Columns);
            int last = Math.Min(owner.Items.Count-1, ((int)((offset+height)/RowHeight)+2)*Columns-1);
            var children = InternalChildren; // 访问子集合先让 WPF 初始化生成器。
            var generator = ItemContainerGenerator;
            if(generator==null)return viewport;
            for (int i = InternalChildren.Count-1; i >= 0; i--)
            {
                int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i,0));
                if (index < first || index > last)
                {
                    var recycling = generator as IRecyclingItemContainerGenerator;
                    if (recycling != null) recycling.Recycle(new GeneratorPosition(i,0),1);
                    else generator.Remove(new GeneratorPosition(i,0),1);
                    RemoveInternalChildRange(i,1);
                }
            }
            if (last >= first)
            {
                var position = generator.GeneratorPositionFromIndex(first);
                int childIndex = position.Offset == 0 ? position.Index : position.Index+1;
                using (generator.StartAt(position,GeneratorDirection.Forward,true))
                    for (int index=first; index<=last; index++,childIndex++)
                    {
                        bool fresh;
                        var child = (UIElement)generator.GenerateNext(out fresh);
                        if (fresh)
                        {
                            if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                            else InsertInternalChild(childIndex,child);
                            generator.PrepareItemContainer(child);
                        }
                        child.Measure(new Size(width/Columns,RowHeight));
                    }
            }
            return viewport;
        }

        /// <summary>位置由真实索引计算；首尾额外安全区容纳选中缩放，避免上边框被视口裁掉。</summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            for (int i=0;i<InternalChildren.Count;i++)
            {
                int index=ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i,0));
                InternalChildren[i].Arrange(new Rect(index%Columns*viewport.Width/Columns,index/Columns*RowHeight+6-offset,viewport.Width/Columns,RowHeight));
            }
            return finalSize;
        }
        /// <summary>ScrollIntoView 可以定位尚未实例化的游戏。</summary>
        protected override void BringIndexIntoView(int index)
        {
            double top=index/Columns*RowHeight;
            if(top<offset)SetVerticalOffset(top);
            else if(top+RowHeight>offset+viewport.Height)SetVerticalOffset(top+RowHeight-viewport.Height);
        }
        /// <summary>将焦点容器滚入当前视口。</summary>
        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            var owner=ItemsControl.GetItemsOwner(this);
            var child=visual as DependencyObject;
            while(child!=null && VisualTreeHelper.GetParent(child)!=this)child=VisualTreeHelper.GetParent(child);
            if(child!=null && owner!=null)BringIndexIntoView(owner.ItemContainerGenerator.IndexFromContainer(child));
            return rectangle;
        }
        /// <summary>约束滚动范围并重新生成可见容器。</summary>
        public void SetVerticalOffset(double value) {scrollTimer.Stop();ApplyOffset(value);}
        /// <summary>动画帧与直接拖动使用相同的边界约束和虚拟化测量。</summary>
        private void ApplyOffset(double value) { offset=Math.Max(0,Math.Min(value,Math.Max(0,extent.Height-viewport.Height)));InvalidateMeasure();if(ScrollOwner!=null)ScrollOwner.InvalidateScrollInfo(); }
        public void SetHorizontalOffset(double value) { }
        public void LineUp(){SmoothScroll(-RowHeight);}
        public void LineDown(){SmoothScroll(RowHeight);}
        public void MouseWheelUp(){LineUp();}
        public void MouseWheelDown(){LineDown();}
        public void PageUp(){SmoothScroll(-viewport.Height);}
        public void PageDown(){SmoothScroll(viewport.Height);}
        public void LineLeft(){} public void LineRight(){} public void MouseWheelLeft(){} public void MouseWheelRight(){} public void PageLeft(){} public void PageRight(){}
    }
}
