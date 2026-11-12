using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GraphiteCompanion.Controls;

public static class CanvasParts
{
    public static List<ViewLayoutGroup> Discover(string id, FrameworkElement element)
    {
        if (element is EditTile || element is System.Windows.Controls.Primitives.Popup || element is ItemsControl) return new();
        if (element is Border { Child: FrameworkElement child }) return Discover(id+".body",child);
        if (element is UserControl { Content: FrameworkElement content }) return Discover(id+".content",content);
        if (element is ContentControl { Content: FrameworkElement hosted } && element is not System.Windows.Controls.Primitives.ButtonBase) return Discover(id+".host",hosted);
        if (element is ScrollViewer { Content: FrameworkElement scrolled }) return Discover(id+".scroll",scrolled);
        if (element is not Panel panel || panel is Canvas) return new();
        var children=panel.Children.OfType<FrameworkElement>().ToArray();
        if(panel is Grid && children.Length==2 && children.Any(c=>c is TextBlock) && children.Any(c=>c is Slider or ComboBox or TextBox))return new();
        if(panel is StackPanel && children.Length==2 && children[0] is Grid row && row.Children.OfType<TextBlock>().Any() && children[1] is Slider)return new();
        if(panel is StackPanel && children.Length==2 && children[0] is TextBlock && children[1] is Slider or ComboBox or TextBox) return new();
        if(children.Any(c=>c is EditTile)) return new();
        bool hasWrapped=children.Any(ContainsTile);
        bool linear=panel is StackPanel || panel is WrapPanel || panel is System.Windows.Controls.Primitives.UniformGrid;
        if(panel is Grid grid) {
            bool horizontal=grid.ColumnDefinitions.Count>1;
            linear=children.All(c=>Grid.GetRowSpan(c)==1&&Grid.GetColumnSpan(c)==1) &&
                children.Select(c=>horizontal?Grid.GetColumn(c):Grid.GetRow(c)).Distinct().Count()==children.Length &&
                children.Select(c=>horizontal?Grid.GetRow(c):Grid.GetColumn(c)).Distinct().Count()<=1;
        }
        if(linear && children.Length>1 && !hasWrapped) {
            // Keep a caption with the field it describes; moving a slider must not detach its label.
            if(panel is StackPanel { Orientation:Orientation.Vertical } stack) {
                for(int i=stack.Children.Count-2;i>=0;i--) if(stack.Children[i] is TextBlock && stack.Children[i+1] is Slider or ComboBox or TextBox) Join(stack,i,2);
                children=panel.Children.OfType<FrameworkElement>().ToArray();
            }
            if(children.Length>1) return new(){new ViewLayoutGroup(id,"Элементы",panel,children.Select((c,i)=>Name(c,i)).ToArray())};
        }
        return children.SelectMany((c,i)=>Discover(id+"."+i,c)).ToList();
    }
    private static bool ContainsTile(FrameworkElement e) => e is EditTile || e is Border {Child:FrameworkElement b} && ContainsTile(b) || e is Panel p && p.Children.OfType<FrameworkElement>().Any(ContainsTile);
    public static string Name(FrameworkElement e,int index=0)
    {
        string name=System.Windows.Automation.AutomationProperties.GetName(e);
        if(!string.IsNullOrWhiteSpace(name))return name;
        if(e is TextBlock t && !string.IsNullOrWhiteSpace(t.Text))return t.Text.Length>45?t.Text[..45]+"…":t.Text;
        if(e is ContentControl {Content:string text} && !string.IsNullOrWhiteSpace(text))return text;
        if(e is Border {Child:FrameworkElement b})return Name(b,index);
        if(e is Panel p) {
            var label=p.Children.OfType<TextBlock>().FirstOrDefault(t=>!string.IsNullOrWhiteSpace(t.Text)&&!t.Text.All(c=>char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.PrivateUse));if(label!=null)return Name(label,index);
            var captions=p.Children.OfType<ContentControl>().Select(c=>c.Content).OfType<string>().Where(t=>!string.IsNullOrWhiteSpace(t)).Take(3).ToArray();if(captions.Length>0)return string.Join(" / ",captions);
        }
        return e is Slider ? "Регулятор" : e is Button ? "Кнопка" : e is ComboBox ? "Выбор значения" : e is TextBox ? "Поле ввода" : "Группа "+(index+1);
    }
    public static void Enrich(CanvasCard card)
    {
        card.Inside.AddRange(Discover(card.Id+".parts",card.Element));
        foreach(var group in card.Inside) group.Expand();
    }
    // A label, its control and value remain a single unit. Only explicit groups are editable.
    public static ViewLayoutGroup Tail(string id, StackPanel panel, int first, params string[] names)
    {
        var children = panel.Children.Cast<UIElement>().Skip(first).ToArray();
        foreach (var child in children) panel.Children.Remove(child);
        var host = new StackPanel(); foreach (var child in children) host.Children.Add(child);
        panel.Children.Add(host);
        return new ViewLayoutGroup(id, "Элементы", host, names);
    }
    public static Panel Wrap(StackPanel panel)
    {
        if (panel.Orientation != Orientation.Horizontal) return panel;
        var host = new WrapPanel(); var children = panel.Children.Cast<UIElement>().ToArray(); panel.Children.Clear();
        foreach (var child in children) host.Children.Add(child);
        panel.Orientation = Orientation.Vertical; panel.Children.Add(host); return host;
    }
    public static void Join(StackPanel panel, int first, int count)
    {
        var children = panel.Children.Cast<UIElement>().Skip(first).Take(count).ToArray();
        foreach (var child in children) panel.Children.Remove(child);
        var unit = new StackPanel(); foreach (var child in children) unit.Children.Add(child);
        panel.Children.Insert(first, unit);
    }
}
