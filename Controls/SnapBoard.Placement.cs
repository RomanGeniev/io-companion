using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls;

public sealed partial class SnapBoard
{
    private CanvasLayout? placementBaseline;
    private readonly PlacementGuides placementGuides=new();
    internal bool HasPositions=>state.DesignWidth>0&&!SingleColumn;

    private void PrepareRestoredCards()
    {
        if(!HasPositions)return;
        foreach(var b in state.Blocks.Where(b=>b.Visible&&!b.Size.Proportional&&tiles.ContainsKey(b.Id)))
        {
            // A new/restored card has no previous screen rectangle. Measure its
            // natural template at design width, then give it the same fit as peers.
            var tile=tiles[b.Id];double width=b.Size.Width>0?b.Size.Width:b.Zone==0?state.DesignWidth:(state.DesignWidth-14)*(b.Zone==1?leftWeight:rightWeight)/(leftWeight+rightWeight);
            width=Math.Clamp(width,1,state.DesignWidth);tile.Measure(new Size(width,double.PositiveInfinity));
            b.Size.ReferenceWidth=width/b.Size.Scale;
            b.Size.ReferenceHeight=Math.Max(1,tile.DesiredSize.Height-tile.Margin.Top-tile.Margin.Bottom)/b.Size.Scale;
            b.Size.Normalize();tile.SetSize(b.Size);
        }
    }

    // Capture only on an actual gesture, never on entering edit mode. Preserve
    // each card's rendered size, including old independently sized inner items.
    private CanvasLayout PositionedSnapshot()
    {
        UpdateLayout();var next=state.Copy();
        double fit=canvas.Fit;bool migrating=next.DesignWidth<=0;
        if(next.DesignWidth<=0||canvas.ActualWidth>next.DesignWidth)next.DesignWidth=Math.Max(1,canvas.ActualWidth);
        foreach(var b in next.Blocks.Where(b=>b.Visible&&tiles.ContainsKey(b.Id)))
        {
            var tile=tiles[b.Id];if(tile.ActualWidth<=1)continue;
            var point=tile.TranslatePoint(new Point(),body);
            b.Position=new(){X=point.X/fit,Y=point.Y/fit};
            if(!b.Size.Proportional)
            {
                b.Size.ReferenceWidth=tile.ActualWidth/fit/b.Size.Scale;
                b.Size.ReferenceHeight=tile.ActualHeight/fit/b.Size.Scale;
                b.Size.Normalize();
            }
            else if(migrating)
            {
                // Legacy rows may constrain a saved 150% card to a 100% column.
                // Preserve the actual uniform factor when removing that column.
                b.Size.Scale=tile.ActualWidth/b.Size.ReferenceWidth;b.Size.Normalize();
            }
        }
        return next;
    }

    private void ApplyPositionPreview(CanvasLayout next,string id)
    {
        if(JsonSerializer.Serialize(state)==JsonSerializer.Serialize(next))return;
        state=next;canvas.Layout=state;canvas.MovingId=id;
        // Reparenting here would release the captured resize thumb.
        foreach(var b in state.Blocks)if(tiles.TryGetValue(b.Id,out var tile))tile.SetSize(b.Size);
        canvas.InvalidateMeasure();UpdateLayout();DragPreview.Refresh();
    }
    private void BeginBlockResize()
    {
        BeginOperation();if(!SingleColumn)placementBaseline=PositionedSnapshot();
    }
    private void ResizeBlock(string id,LayoutSize size)
    {
        if(SingleColumn){state.Blocks.First(b=>b.Id==id).Size=size;tiles[id].SetSize(size);canvas.InvalidateMeasure();return;}
        var next=(placementBaseline??PositionedSnapshot()).Copy();var b=next.Blocks.First(b=>b.Id==id);
        b.Size=size.Copy();
        b.Position!.X=Math.Min(b.Position.X,Math.Max(0,next.DesignWidth-b.Size.Width));
        ApplyPositionPreview(next,id);
    }
    private void CompletePlacement(bool accepted)
    {
        if(accepted&&state.DesignWidth>0&&!SingleColumn)state=PositionedSnapshot();
        canvas.Layout=state;canvas.MovingId=null;placementBaseline=null;placementGuides.Clear();canvas.InvalidateMeasure();
    }
    private void NudgeBlock(string id,Vector delta)
    {
        if(SingleColumn){MoveBlockByKeyboard(id,delta.X<0||delta.Y<0?-1:1);return;}
        BeginOperation();var next=PositionedSnapshot();var b=next.Blocks.First(b=>b.Id==id);
        b.Position!.X=Math.Clamp(b.Position.X+delta.X,0,Math.Max(0,next.DesignWidth-b.Size.Width));
        b.Position.Y=Math.Max(0,b.Position.Y+delta.Y);ApplyPositionPreview(next,id);CompletePlacement(true);EndOperation();tiles[id].Focus();
    }
    private void PreviewPosition(string id,Point at)
    {
        if(placementBaseline==null||!dragSlots.TryGetValue(id,out var original))return;
        var next=placementBaseline.Copy();var b=next.Blocks.First(x=>x.Id==id);
        var grab=tiles[id].DragGrab;double fit=canvas.Fit;
        var rect=new Rect(at.X-grab.X,at.Y-grab.Y,original.Width,original.Height);
        double width=canvas.ActualWidth;
        // Align in screen DIPs: an overlay's own scale must not widen the magnet.
        var host=Window.GetWindow(this);double screenScale=host==null?1:canvas.TransformToAncestor(host).TransformBounds(new Rect(0,0,1,1)).Width;
        double tolerance=7/Math.Max(.1,screenScale);
        double? guideX=null,guideY=null;
        if((Keyboard.Modifiers&ModifierKeys.Alt)==0)
        {
            var xs=new List<(double Offset,double Line)> {(-rect.Left,0),(width/2-(rect.Left+rect.Width/2),width/2),(width-rect.Right,width)};
            var ys=new List<(double Offset,double Line)> {(-rect.Top,0)};
            foreach(var pair in dragSlots.Where(p=>p.Key!=id))
            {
                var other=pair.Value;
                foreach(double x in new[]{other.Left,other.Left+other.Width/2,other.Right})
                    foreach(double anchor in new[]{rect.Left,rect.Left+rect.Width/2,rect.Right})xs.Add((x-anchor,x));
                xs.Add((other.Right+10*fit-rect.Left,other.Right+10*fit));xs.Add((other.Left-10*fit-rect.Right,other.Left-10*fit));
                foreach(double y in new[]{other.Top,other.Bottom+10*fit})ys.Add((y-rect.Top,y));
                ys.Add((other.Top-10*fit-rect.Bottom,other.Top-10*fit));
            }
            var sx=xs.OrderBy(x=>Math.Abs(x.Offset)).First();var sy=ys.OrderBy(x=>Math.Abs(x.Offset)).First();
            if(Math.Abs(sx.Offset)<=tolerance){rect.X+=sx.Offset;guideX=sx.Line;}
            if(Math.Abs(sy.Offset)<=tolerance){rect.Y+=sy.Offset;guideY=sy.Line;}
        }
        rect.X=Math.Clamp(rect.X,0,Math.Max(0,width-rect.Width));rect.Y=Math.Max(0,rect.Y);
        // Returning to the grab position restores the exact old JSON as well.
        if(Math.Abs(rect.X-original.X)<.1&&Math.Abs(rect.Y-original.Y)<.1)
        {
            SetPreview(dragOriginal!.Copy());placementGuides.Clear();return;
        }
        b.Position=new(){X=rect.X/fit,Y=rect.Y/fit};ApplyPositionPreview(next,id);
        var offset=body.TranslatePoint(new Point(),root);
        placementGuides.Show(guideX.HasValue?guideX+offset.X:null,guideY.HasValue?guideY+offset.Y:null);
    }
    // A drawn guide never intercepts the pointer or reserves layout space.
    private sealed class PlacementGuides:FrameworkElement
    {
        private double? x,y;
        public PlacementGuides(){IsHitTestVisible=false;}
        public void Show(double? gx,double? gy){x=gx;y=gy;InvalidateVisual();}
        public void Clear()=>Show(null,null);
        protected override void OnRender(DrawingContext dc)
        {
            var pen=new Pen(TryFindResource("AccentBrush") as Brush??Brushes.DeepSkyBlue,1){DashStyle=DashStyles.Dash};
            if(x is double px)dc.DrawLine(pen,new Point(px,0),new Point(px,ActualHeight));
            if(y is double py)dc.DrawLine(pen,new Point(0,py),new Point(ActualWidth,py));
        }
    }
}
