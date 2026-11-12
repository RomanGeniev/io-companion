using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Services;
namespace GraphiteCompanion.Controls;

// Keep legacy geometry until a move/resize is made. Afterwards the same panel
// packs blocks on a 12-column grid, without reparenting a captured resize thumb.
internal sealed class CanvasBoardPanel:Panel
{
    public CanvasLayout Layout {get;set;}=new();
    public double LeftWeight {get;set;}=1;
    public double RightWeight {get;set;}=1;
    public bool SingleColumn {get;set;}
    private readonly Dictionary<UIElement,Rect> slots=new();
    protected override Size MeasureOverride(Size available)
    {
        slots.Clear();double width=double.IsFinite(available.Width)?Math.Max(1,available.Width):800;
        var elements=Children.OfType<EditTile>().ToDictionary(t=>t.Id);
        var blocks=Layout.Blocks.Where(b=>b.Visible&&elements.ContainsKey(b.Id)).ToArray();
        double Measure(CanvasBlock b,double w,double x,double y){var t=elements[b.Id];t.Measure(new Size(Math.Max(1,w),double.PositiveInfinity));double h=t.DesiredSize.Height;slots[t]=new Rect(x,y,Math.Max(1,w),h);return h;}
        if(!Layout.Flow||SingleColumn)
        {
            double top=0;foreach(var b in blocks.Where(b=>b.Zone==0))top+=Measure(b,width,0,top);
            bool l=blocks.Any(b=>b.Zone==1),r=blocks.Any(b=>b.Zone==2);double lw=l&&r?(width-14)*LeftWeight/(LeftWeight+RightWeight):width;
            double ly=top,ry=top;foreach(var b in blocks.Where(b=>b.Zone!=0))if(b.Zone==1)ly+=Measure(b,lw,0,ly);else ry+=Measure(b,l&&r?width-lw-14:width,l&&r?lw+14:0,ry);
            return new Size(width,Math.Max(ly,ry));
        }
        double x=0,y=0,rowHeight=0,pitch=(width+14)/12;
        foreach(var b in blocks)
        {
            int span=b.Zone==0?12:(int)Math.Round(12*(b.Zone==1?LeftWeight:RightWeight)/(LeftWeight+RightWeight));
            if(b.Size.Width>0)span=(int)Math.Ceiling((b.Size.Width+14)/pitch);
            span=Math.Clamp(span,Math.Min(4,12),12);double w=Math.Min(width,span*pitch-14);
            if(x>0&&x+w>width+.5){y+=rowHeight;x=0;rowHeight=0;}
            rowHeight=Math.Max(rowHeight,Measure(b,w,x,y));x+=w+14;
        }
        return new Size(width,y+rowHeight);
    }
    protected override Size ArrangeOverride(Size final){foreach(UIElement child in Children)child.Arrange(slots.TryGetValue(child,out var r)?r:new Rect());return final;}
}
