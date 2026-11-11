using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GraphiteCompanion.Controls;

public static class HelpTips
{
    public static void Set(FrameworkElement control, string text)
    {
        if (control.ToolTip is ToolTip && AutomationProperties.GetHelpText(control) == text) return;
        control.ToolTip = new ToolTip
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, LineHeight = 19 },
            Padding = new Thickness(12, 9, 12, 9),
        };
        ToolTipService.SetInitialShowDelay(control, 700);
        ToolTipService.SetBetweenShowDelay(control, 0);
        ToolTipService.SetShowDuration(control, 30000);
        ToolTipService.SetShowOnDisabled(control, true);
        AutomationProperties.SetHelpText(control, text);
    }

    public static void Prepare(DependencyObject root)
    {
        if (root is FrameworkElement { ToolTip: string text } element && text.Length > 0) Set(element, text);
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) Prepare(dependency);
    }
}
