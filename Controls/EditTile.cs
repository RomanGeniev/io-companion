using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
namespace GraphiteCompanion.Controls;

// Chrome overlays the original layout. The closest wrapper owns input, including
// nested controls, so moving a capture/audio button never invokes its real action.
public sealed class EditTile : Border
{
    internal sealed record Payload(object Scope,string Id,string Name,double Height);
    public FrameworkElement Element {get;}
    private readonly Grid surface=new();
    private readonly OverlayChrome chrome=new();
    private readonly Border outline;
    private readonly Rectangle landing;
    private readonly StackPanel actions=new(){Orientation=Orientation.Horizontal};
    private readonly TextBlock moveGrip;
    private readonly Thumb widthGrip,heightGrip,cornerGrip;
    private readonly object scope;
    private readonly string id,name;
    private readonly Action? insideAction,sizeAction;
    private readonly double naturalWidth,naturalHeight;
    private readonly VerticalAlignment naturalAlignment;
    private static Thumb? activeResize;
    internal static bool CancelActiveResize(){if(activeResize?.IsDragging!=true)return false;activeResize.CancelDrag();return true;}
    private bool selected,editable,dragging;
    private double elementOpacity=1;
    private LayoutSize size=new();
    private LayoutSize? resizeBefore;
    private KeyboardNavigationMode originalNavigation;
    public bool InlineActions {get;set;}=true;
    public bool ShowResizeGrip {get;set;}=true;
    public bool IsContainer {get;set;}
    public bool HasOptions {get;set;}
    public string Title=>name;
    public string InsideLabel {get;set;}="Содержимое";
    public bool Editable=>editable;
    internal string Id=>id;
    internal object Scope=>scope;
    internal double PreferredWidth=>size.Width;
    public event Action<EditTile>? Selected;
    public event Action? DragStarted;
    public event Action<bool>? DragFinished;
    public event Action? ResizeStarted;
    public event Action<bool>? ResizeFinished;
    public event Action<LayoutSize>? SizeChangedByUser;
    public event Action<int>? MoveByKeyboard;
    public EditTile(FrameworkElement element,object scope,string id,string name,Action hide,Action? inside=null,Action? sizing=null)
    {
        Element=element;this.scope=scope;this.id=id;this.name=name;insideAction=inside;sizeAction=sizing;
        naturalWidth=element.Width;naturalHeight=element.Height;naturalAlignment=element.VerticalAlignment;Background=Brushes.Transparent;Focusable=true;surface.Children.Add(element);chrome.Outer=scope is SnapBoard;
        outline=new Border{BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),IsHitTestVisible=false};outline.SetResourceReference(BorderBrushProperty,"CardBorderBrush");chrome.Children.Add(outline);
        landing=new Rectangle{RadiusX=7,RadiusY=7,StrokeThickness=2,StrokeDashArray=new DoubleCollection{3,2},IsHitTestVisible=false,Visibility=Visibility.Collapsed};landing.SetResourceReference(Shape.StrokeProperty,"AccentBrush");chrome.Children.Add(landing);
        if(inside!=null)actions.Children.Add(IconButton(false,"Настроить: "+name,inside));
        actions.Children.Add(IconButton(true,"Скрыть «"+name+"». Вернуть можно на полке «Скрытое»",hide));chrome.Children.Add(actions);
        moveGrip=new TextBlock{Text="⠿",FontSize=12,Width=12,Height=14,TextAlignment=TextAlignment.Center,Cursor=Cursors.SizeAll,ToolTip="Перетащите весь блок: "+name};moveGrip.SetResourceReference(TextBlock.ForegroundProperty,"SubTextBrush");moveGrip.SetResourceReference(TextBlock.BackgroundProperty,"CardBrush");chrome.Children.Add(moveGrip);
        widthGrip=MakeGrip("width",Cursors.SizeWE);heightGrip=MakeGrip("height",Cursors.SizeNS);cornerGrip=MakeGrip("both",Cursors.SizeNWSE);chrome.Children.Add(widthGrip);chrome.Children.Add(heightGrip);chrome.Children.Add(cornerGrip);surface.Children.Add(chrome);Child=surface;
        Point start=default;bool pressed=false;
        PreviewMouseLeftButtonDown+=(_,e)=>{if(!editable||Nearest(e.OriginalSource as DependencyObject)!=this||IsTool(e.OriginalSource as DependencyObject))return;Selected?.Invoke(this);Focus();pressed=true;start=e.GetPosition(this);CaptureMouse();e.Handled=true;};
        PreviewMouseLeftButtonUp+=(_,e)=>{if(!pressed)return;pressed=false;ReleaseMouseCapture();e.Handled=true;};
        PreviewMouseWheel+=(_,e)=>{if(!editable||Nearest(e.OriginalSource as DependencyObject)!=this)return;for(var n=GetParentNode(this);n!=null;n=GetParentNode(n))if(n is SnapBoard board){board.ScrollEditing(e.Delta);break;}e.Handled=true;};
        LostMouseCapture+=(_,_)=>pressed=false;
        MouseMove+=(_,e)=>{
            if(!pressed||!editable||e.LeftButton!=MouseButtonState.Pressed)return;var at=e.GetPosition(this);
            if(Math.Abs(at.X-start.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(at.Y-start.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            pressed=false;ReleaseMouseCapture();bool accepted=false;using var preview=DragPreview.Begin(this,start);
            try{ToolTipService.SetIsEnabled(this,false);DragStarted?.Invoke();dragging=true;Element.Opacity=.08;RefreshChrome();accepted=DragDrop.DoDragDrop(this,new DataObject(typeof(Payload),new Payload(scope,id,name,ActualHeight)),DragDropEffects.Move)==DragDropEffects.Move;}
            finally{dragging=false;Element.Opacity=elementOpacity;RefreshChrome();DragFinished?.Invoke(accepted);ToolTipService.SetIsEnabled(this,editable);}
        };
        PreviewKeyDown+=(_,e)=>{
            if(!editable||Nearest(e.OriginalSource as DependencyObject)!=this)return;
            if(e.Key==Key.Escape&&resizeBefore!=null){foreach(var t in new[]{widthGrip,heightGrip,cornerGrip})if(t.IsDragging)t.CancelDrag();e.Handled=true;return;}
            if(e.OriginalSource is Thumb)return;
            if(e.Key is Key.Left or Key.Up or Key.Right or Key.Down){MoveByKeyboard?.Invoke(e.Key is Key.Left or Key.Up?-1:1);e.Handled=true;}else if(e.Key is Key.Enter or Key.Space)e.Handled=true;
        };
        Element.SizeChanged+=(_,_)=>RefreshChrome();MouseEnter+=(_,_)=>RefreshChrome();MouseLeave+=(_,_)=>RefreshChrome();SetEditing(false);
    }
    private bool IsTool(DependencyObject? n){bool tool=false;for(;n!=null&&n!=this;n=GetParentNode(n)){if(n is ButtonBase or Thumb)tool=true;if(n==chrome)return tool;}return false;}
    private static DependencyObject? GetParentNode(DependencyObject n)=>n is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(n):LogicalTreeHelper.GetParent(n);
    private static EditTile? Nearest(DependencyObject? n){for(;n!=null;n=GetParentNode(n))if(n is EditTile t)return t;return null;}
    private Thumb MakeGrip(string axis,Cursor cursor)
    {
        var grip=new Thumb{Width=axis=="height"?26:18,Height=axis=="width"?26:18,Cursor=cursor,ToolTip=axis=="width"?"Тяните: ширина":axis=="height"?"Тяните: высота":"Тяните: ширина и высота"};AutomationProperties.SetName(grip,name+": "+(axis=="width"?"ширина":axis=="height"?"высота":"размер"));
        var mark=new FrameworkElementFactory(typeof(Border));mark.SetValue(Border.WidthProperty,axis=="height"?18d:7d);mark.SetValue(Border.HeightProperty,axis=="width"?18d:7d);mark.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));mark.SetValue(Border.BorderThicknessProperty,new Thickness(1));mark.SetResourceReference(Border.BorderBrushProperty,"SubTextBrush");mark.SetResourceReference(Border.BackgroundProperty,"CardBrush");grip.Template=new ControlTemplate(typeof(Thumb)){VisualTree=mark};
        double w=0,h=0;
        grip.DragStarted+=(_,e)=>{activeResize=grip;Selected?.Invoke(this);resizeBefore=size.Copy();w=ActualWidth;h=ActualHeight;ResizeStarted?.Invoke();e.Handled=true;};
        grip.DragDelta+=(_,e)=>{w+=e.HorizontalChange;h+=e.VerticalChange;var next=size.Copy();if(axis!="height")next.Width=Math.Max(ReadableMinimumWidth,Math.Round(w/16)*16);if(axis!="width")next.Height=Math.Max(32,Math.Round(h/16)*16);SizeChangedByUser?.Invoke(next);e.Handled=true;};
        grip.DragCompleted+=(_,e)=>{if(e.Canceled&&resizeBefore!=null)SizeChangedByUser?.Invoke(resizeBefore);resizeBefore=null;activeResize=null;ResizeFinished?.Invoke(!e.Canceled);e.Handled=true;};
        grip.KeyDown+=(_,e)=>{if(e.Key is not(Key.Left or Key.Right or Key.Up or Key.Down))return;ResizeStarted?.Invoke();var next=size.Copy();if(axis!="height"&&e.Key is Key.Left or Key.Right)next.Width=Math.Max(ReadableMinimumWidth,ActualWidth+(e.Key==Key.Left?-16:16));if(axis!="width"&&e.Key is Key.Up or Key.Down)next.Height=Math.Max(32,ActualHeight+(e.Key==Key.Up?-16:16));SizeChangedByUser?.Invoke(next);ResizeFinished?.Invoke(true);e.Handled=true;};return grip;
    }
    private static Button IconButton(bool remove,string title,Action action)
    {
        var p=new Path{Width=9,Height=9,Stretch=Stretch.Uniform,StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Data=Geometry.Parse(remove?"M3,3 L13,13 M13,3 L3,13":"M2,4 L14,4 M5,1 L5,7 M2,12 L14,12 M11,9 L11,15")};if(remove)p.Stroke=new SolidColorBrush(Color.FromRgb(240,78,100));else p.SetResourceReference(Shape.StrokeProperty,"TextBrush");var b=new Button{Content=p,Width=18,Height=18,MinWidth=0,MinHeight=0,Padding=new Thickness(),Margin=new Thickness(2,0,0,0),ToolTip=title};b.SetResourceReference(StyleProperty,"RoundGhostButton");b.SetResourceReference(BackgroundProperty,"CardBrush");AutomationProperties.SetName(b,title);b.Click+=(_,e)=>{e.Handled=true;action();};return b;
    }
    public void SetSize(LayoutSize? value)
    {
        var next=(value??new()).Copy();next.Normalize();if(size.Scale==next.Scale&&size.Width==next.Width&&size.Height==next.Height)return;size=next;surface.LayoutTransform=size.Scale==1?Transform.Identity:new ScaleTransform(size.Scale,size.Scale);
        if(Element is ButtonBase){Element.Width=size.Width>0?double.NaN:naturalWidth;Element.Height=size.Height>0?double.NaN:naturalHeight;}Element.VerticalAlignment=size.Height>0?VerticalAlignment.Stretch:naturalAlignment;HorizontalAlignment=size.Width>0?HorizontalAlignment.Left:HorizontalAlignment.Stretch;InvalidateMeasure();
    }
    public double ReadableMinimumWidth{get{double min=Math.Max(48,(Element.MinWidth+Element.Margin.Left+Element.Margin.Right)*size.Scale);if(Element is ButtonBase){Element.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));min=Math.Max(min,Element.DesiredSize.Width*size.Scale);}return Math.Ceiling(min/16)*16;}}
    protected override Size MeasureOverride(Size constraint){double width=size.Width>0?Math.Min(constraint.Width,Math.Max(ReadableMinimumWidth,size.Width)):constraint.Width;var measured=base.MeasureOverride(new Size(width,constraint.Height));return new Size(Math.Max(measured.Width,double.IsFinite(width)&&size.Width>0?width:0),Math.Max(measured.Height,size.Height));}
    public FrameworkElement Detach(){((Panel)Element.Parent).Children.Remove(Element);return Element;}
    public void SetInnerAvailable(bool value){IsContainer=value;RefreshChrome();}
    public void SetSelected(bool value){selected=value;RefreshChrome();}
    public FrameworkElement ContextActions(){var p=new WrapPanel();if(HasOptions&&insideAction!=null)p.Children.Add(SnapBoard.Button(InsideLabel,insideAction));return p;}
    public void SetEditing(bool value){if(value&&!editable){originalNavigation=KeyboardNavigation.GetTabNavigation(Element);KeyboardNavigation.SetTabNavigation(Element,KeyboardNavigationMode.None);elementOpacity=Element.Opacity;}if(!value&&editable)KeyboardNavigation.SetTabNavigation(Element,originalNavigation);editable=value;if(!value)selected=false;Cursor=value?Cursors.SizeAll:null;ToolTipService.SetIsEnabled(this,value);RefreshChrome();}
    private void RefreshChrome()
    {
        chrome.Visibility=editable&&Element.ActualWidth>1&&Element.ActualHeight>1?Visibility.Visible:Visibility.Collapsed;outline.SetResourceReference(BorderBrushProperty,selected?"AccentBrush":"CardBorderBrush");outline.BorderThickness=new Thickness(selected?2:1);outline.Opacity=selected?1:.45;
        actions.Visibility=InlineActions&&selected&&!dragging?Visibility.Visible:Visibility.Collapsed;if(actions.Children.Count>1)actions.Children[0].Visibility=HasOptions?Visibility.Visible:Visibility.Collapsed;
        moveGrip.Visibility=IsContainer&&!dragging&&(scope is SnapBoard||selected||IsMouseOver)?Visibility.Visible:Visibility.Collapsed;widthGrip.Visibility=heightGrip.Visibility=ShowResizeGrip&&sizeAction!=null&&selected&&!dragging?Visibility.Visible:Visibility.Collapsed;cornerGrip.Visibility=ShowResizeGrip&&sizeAction!=null&&!dragging&&(!IsContainer||scope is SnapBoard||selected)?Visibility.Visible:Visibility.Collapsed;landing.Visibility=dragging?Visibility.Visible:Visibility.Collapsed;
    }
    public void Dim(bool value){IsHitTestVisible=!value;}
    internal static Payload? Read(DragEventArgs e,object scope)=>e.Data.GetData(typeof(Payload)) is Payload p&&ReferenceEquals(p.Scope,scope)?p:null;
    internal ImageSource Snapshot(){var dpi=VisualTreeHelper.GetDpi(this);double scale=Math.Min(Math.Max(dpi.DpiScaleX,dpi.DpiScaleY),Math.Min(2400/Math.Max(1,ActualWidth),2400/Math.Max(1,ActualHeight)));var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(ActualWidth*scale)),Math.Max(1,(int)Math.Ceiling(ActualHeight*scale)),96*scale,96*scale,PixelFormats.Pbgra32);var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var destination=Element.TransformToAncestor(this).TransformBounds(new Rect(Element.RenderSize));dc.DrawRectangle(new VisualBrush(Element){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(Element.RenderSize),Stretch=Stretch.Fill},null,destination);}bitmap.Render(drawing);bitmap.Freeze();return bitmap;}
    private sealed class OverlayChrome:Panel
    {
        public bool Outer {get;set;}
        protected override Size MeasureOverride(Size available){foreach(UIElement c in Children)c.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return new Size();}
        protected override Size ArrangeOverride(Size f){Children[0].Arrange(new Rect(f));Children[1].Arrange(new Rect(f));var a=Children[2].DesiredSize;Children[2].Arrange(new Rect(Math.Max(0,f.Width-a.Width-2),Outer?2:-9,a.Width,a.Height));Children[3].Arrange(new Rect(Outer?0:Math.Max(0,f.Width-a.Width-16),0,12,14));Children[4].Arrange(new Rect(Math.Max(0,f.Width-9),Math.Max(0,f.Height/2-13),18,26));Children[5].Arrange(new Rect(Math.Max(0,f.Width/2-13),Math.Max(0,f.Height-9),26,18));Children[6].Arrange(new Rect(Math.Max(0,f.Width-14),Math.Max(0,f.Height-14),18,18));return f;}
    }
}
