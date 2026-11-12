using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GraphiteCompanion.Controls;

// Measures at the captured logical width, then scales the complete result once.
// Window constraints change only the uniform factor, never the layout inside a card.
internal sealed class UniformBlockHost : Decorator
{
    private LayoutSize size = new();
    private Size logical;
    private double factor = 1;
    public void Configure(LayoutSize value) { size=value.Copy();InvalidateMeasure(); }
    protected override Size MeasureOverride(Size available)
    {
        if(Child==null)return new Size();
        Child.Measure(new Size(size.ReferenceWidth,double.PositiveInfinity));
        logical=new Size(size.ReferenceWidth,Math.Max(size.ReferenceHeight,Child.DesiredSize.Height));
        factor=Math.Min(size.Scale,double.IsFinite(available.Width)?Math.Max(1,available.Width)/logical.Width:size.Scale);
        return new Size(logical.Width*factor,logical.Height*factor);
    }
    protected override Size ArrangeOverride(Size final)
    {
        if(Child!=null){Child.RenderTransform=new ScaleTransform(factor,factor);Child.Arrange(new Rect(logical));}
        return final;
    }
}
