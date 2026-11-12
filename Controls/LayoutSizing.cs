using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json.Serialization;

namespace GraphiteCompanion.Controls;

public sealed class LayoutSize
{
    public double Scale { get; set; } = 1;
    public double Width { get; set; }
    public double Height { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public double ReferenceWidth { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public double ReferenceHeight { get; set; }
    [JsonIgnore] public bool Proportional => ReferenceWidth > 0 && ReferenceHeight > 0;
    public LayoutSize Copy() { var copy=new LayoutSize {Scale=Scale,Width=Width,Height=Height,ReferenceWidth=ReferenceWidth,ReferenceHeight=ReferenceHeight};copy.Normalize();return copy; }
    public void Normalize() {
        // A legacy card can already be fitted below the interactive minimum.
        // Retain that effective scale when its column constraint is removed.
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, Proportional?.1:.75, 1.5) : 1;
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 0, 1600) : 0;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 0, 1000) : 0;
        ReferenceWidth = double.IsFinite(ReferenceWidth) ? Math.Clamp(ReferenceWidth, 0, 10000) : 0;
        ReferenceHeight = double.IsFinite(ReferenceHeight) ? Math.Clamp(ReferenceHeight, 0, 10000) : 0;
        if(Proportional){Width=ReferenceWidth*Scale;Height=ReferenceHeight*Scale;}
        else ReferenceWidth=ReferenceHeight=0;
    }
}
