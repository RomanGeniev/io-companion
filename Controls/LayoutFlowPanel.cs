using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
namespace GraphiteCompanion.Controls;

// Used only after a row has been resized/reparented. Untouched rows retain their
// original WPF layout; customized rows wrap at the same bounds in edit and normal view.
internal sealed class LayoutFlowPanel:Panel
{
    public int Columns {get;set;}=3;
    public bool EqualColumns {get;set;}
    public Dictionary<UIElement,GridLength> Lengths {get;}=new();
    private readonly List<Rect> slots=new();
    protected override Size MeasureOverride(Size available)
    {
        slots.Clear();double width=double.IsFinite(available.Width)?Math.Max(1,available.Width):600;
        double basis=width/Math.Max(1,Columns),x=0,y=0,rowHeight=0;
        double used=0,weight=0;var natural=new Dictionary<UIElement,double>();
        foreach(UIElement child in Children) {
            if(child.Visibility==Visibility.Collapsed){natural[child]=0;continue;}
            child.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));var length=Lengths.TryGetValue(child,out var known)?known:GridLength.Auto;
            natural[child]=length.IsAbsolute?length.Value:child.DesiredSize.Width;
            if(length.IsStar)weight+=length.Value;else used+=natural[child];
        }
        if(!double.IsFinite(available.Width)){
            width=EqualColumns?natural.Values.DefaultIfEmpty(0).Max()*Math.Max(1,Columns):natural.Values.Sum();
            width=Math.Max(1,width);basis=width/Math.Max(1,Columns);
        }
        foreach(UIElement child in Children)
        {
            if(child.Visibility==Visibility.Collapsed){slots.Add(new Rect());continue;}
            var length=Lengths.TryGetValue(child,out var known)?known:GridLength.Auto;
            double automatic=EqualColumns?basis:length.IsStar?Math.Max(natural[child],Math.Max(0,width-used)*length.Value/Math.Max(1,weight)):natural[child];
            double preferred=child is EditTile t&&t.PreferredWidth>0?t.PreferredWidth:automatic;
            double w=Math.Min(width,Math.Max(1,preferred));child.Measure(new Size(w,double.PositiveInfinity));
            w=Math.Min(width,Math.Max(w,child.DesiredSize.Width));if(x>0&&x+w>width+.5){y+=rowHeight+6;x=0;rowHeight=0;}
            slots.Add(new Rect(x,y,w,child.DesiredSize.Height));x+=w;rowHeight=Math.Max(rowHeight,child.DesiredSize.Height);
        }
        return new Size(width,y+rowHeight);
    }
    protected override Size ArrangeOverride(Size final)
    {
        for(int i=0;i<Children.Count&&i<slots.Count;i++)Children[i].Arrange(slots[i]);return final;
    }
}
