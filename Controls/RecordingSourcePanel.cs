using System;
using System.Windows;
using System.Windows.Controls;

namespace GraphiteCompanion.Controls;

// A device name is content, not a requested column width. The layout editor first
// probes rows with infinite width; use a modest preferred width for that probe,
// then stretch normally within the actual card (including user resizing).
public sealed class RecordingSourcePanel : StackPanel
{
    protected override Size MeasureOverride(Size available)
    {
        if (double.IsPositiveInfinity(available.Width)) available.Width = 280;
        return base.MeasureOverride(available);
    }
}
