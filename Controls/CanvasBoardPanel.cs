using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Services;
namespace GraphiteCompanion.Controls;

// Keep saved legacy geometry until a gesture. Positioned cards keep independent
// coordinates; dynamic content can grow without covering the next card.
internal sealed class CanvasBoardPanel:Panel
{
    public CanvasLayout Layout {get;set;}=new();
    public double LeftWeight {get;set;}=1;
    public double RightWeight {get;set;}=1;
    public bool SingleColumn {get;set;}
    public string? MovingId {get;set;}
    public double Fit {get;private set;}=1;
    private readonly Dictionary<UIElement,Rect> slots=new();
    protected override Size MeasureOverride(Size available)
    {
        slots.Clear();double width=double.IsFinite(available.Width)?Math.Max(1,available.Width):800;
        var elements=Children.OfType<EditTile>().ToDictionary(t=>t.Id);
        var blocks=Layout.Blocks.Where(b=>b.Visible&&elements.ContainsKey(b.Id)).ToArray();
        Fit=Layout.DesignWidth>0&&!SingleColumn?Math.Min(1,width/Layout.DesignWidth):1;
        foreach(var tile in elements.Values)tile.Margin=new Thickness(0,0,0,10*Fit);
        double Measure(CanvasBlock b,double w,double x,double y){var t=elements[b.Id];t.Measure(new Size(Math.Max(1,w),double.PositiveInfinity));double h=t.DesiredSize.Height;slots[t]=new Rect(x,y,Math.Max(1,w),h);return h;}
        if(Layout.DesignWidth>0&&!SingleColumn)
        {
            var pending=new Dictionary<string,Rect>();double bottom=0;
            foreach(var b in blocks.Where(b=>b.Position!=null).Concat(blocks.Where(b=>b.Position==null)))
            {
                double w=Math.Min(width,b.Size.Width>0?b.Size.Width*Fit:width/2-7);
                var t=elements[b.Id];t.Measure(new Size(w,double.PositiveInfinity));
                var p=b.Position;double px=Math.Clamp((p?.X??0)*Fit,0,Math.Max(0,width-w));
                double py=p!=null?p.Y*Fit:bottom;
                pending[b.Id]=new Rect(px,py,w,t.DesiredSize.Height);bottom=Math.Max(bottom,py+t.DesiredSize.Height);
            }
            var placed=Resolve(pending,MovingId);
            foreach(var pair in placed)slots[elements[pair.Key]]=pair.Value;
            return new Size(width,placed.Count==0?0:placed.Values.Max(r=>r.Bottom));
        }
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
    // Only intersecting cards move. Sorting is from the authored baseline, so
    // repeated previews and layout passes never accumulate pushes.
    internal static Dictionary<string,Rect> Resolve(Dictionary<string,Rect> pending,string? priority)
    {
        var placed=new Dictionary<string,Rect>();
        foreach(var pair in pending.OrderBy(p=>p.Key==priority?0:1).ThenBy(p=>p.Value.Top).ThenBy(p=>p.Value.Left))
        {
            var r=pair.Value;
            for(int pass=0;pass<=pending.Count;pass++)
            {
                var hits=placed.Values.Where(p=>r.Left<p.Right-.1&&r.Right>p.Left+.1&&r.Top<p.Bottom-.1&&r.Bottom>p.Top+.1).ToArray();
                if(hits.Length==0)break;r.Y=hits.Max(p=>p.Bottom);
            }
            placed[pair.Key]=r;
        }
        return placed;
    }
}
