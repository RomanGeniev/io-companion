using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GraphiteCompanion.Controls;

public sealed class LayoutSize
{
    public double Scale { get; set; } = 1;
    public double Width { get; set; }
    public double Height { get; set; }
    public LayoutSize Copy() { var copy=new LayoutSize {Scale=Scale,Width=Width,Height=Height};copy.Normalize();return copy; }
    public void Normalize() { Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .75, 1.5) : 1; Width = double.IsFinite(Width) ? Math.Clamp(Width, 0, 1600) : 0; Height = double.IsFinite(Height) ? Math.Clamp(Height, 0, 1000) : 0; }
}

internal static class LayoutSizing
{
    public static FrameworkElement Editor(Func<LayoutSize> read, Action<LayoutSize> write, Func<double>? minimumWidth = null)
    {
        var panel = new StackPanel { Width = 250 };
        var refreshers = new System.Collections.Generic.List<Action>();
        bool refreshing=false;
        void Refresh() { refreshing=true;try {foreach(var refresh in refreshers)refresh();}finally{refreshing=false;} }
        void Commit(LayoutSize value) {
            write(value);
            double minimum=minimumWidth?.Invoke()??80;
            if(value.Width>0 && value.Width<minimum) { value=value.Copy();value.Width=minimum;write(value); }
            Refresh();
        }
        panel.Children.Add(SnapBoard.Text("Масштаб увеличивает текст и кнопки вместе."));
        panel.Children.Add(SnapBoard.Button("Автоматический размер",()=>Commit(new())));
        void Slider(string name, double min, double max, double step, Func<LayoutSize,double> get, Action<LayoutSize,double> set) {
            var caption = SnapBoard.Text(""); panel.Children.Add(caption);
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = get(read()), Margin = new Thickness(0,6,0,12) };
            void Label() => caption.Text = name + ": " + (slider.Value == 0 ? "авто" : Math.Round(slider.Value).ToString() + (max == 150 ? "%" : " px"));
            slider.SetResourceReference(FrameworkElement.StyleProperty,"VolumeSlider");
            System.Windows.Automation.AutomationProperties.SetName(slider,name);
            Label();refreshers.Add(()=>{slider.Value=get(read());Label();});
            slider.ValueChanged += (_,_) => { if(refreshing)return;var value=read().Copy(); set(value,slider.Value); Commit(value); }; panel.Children.Add(slider);
        }
        Slider("Масштаб",75,150,5,s=>s.Scale*100,(s,v)=>s.Scale=v/100);
        Slider("Ширина",0,1200,16,s=>s.Width,(s,v)=>s.Width=v);
        Slider("Высота",0,800,16,s=>s.Height,(s,v)=>s.Height=v);
        var advanced = new StackPanel { Margin = new Thickness(0,4,0,0) };
        var details = new StackPanel { Visibility=Visibility.Collapsed, Margin=new Thickness(0,10,0,0) };
        // Keep the common size action short; exact dimensions are optional.
        while(panel.Children.Count>4) { var child=panel.Children[4];panel.Children.RemoveAt(4);details.Children.Add(child); }
        details.Children.Add(SnapBoard.Text("Блоки выравниваются автоматически. Содержимое не обрезается."));
        Button? expand=null;
        expand=SnapBoard.Button("Ширина и высота ▾",()=>{bool open=details.Visibility!=Visibility.Visible;details.Visibility=open?Visibility.Visible:Visibility.Collapsed;expand!.Content=open?"Ширина и высота ▴":"Ширина и высота ▾";});
        advanced.Children.Add(expand);advanced.Children.Add(details);panel.Children.Add(advanced);
        if(read().Width>0 && read().Width<(minimumWidth?.Invoke()??80))Commit(read().Copy());
        return panel;
    }
}
