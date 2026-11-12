using GraphiteCompanion.Services;

namespace GraphiteCompanion;

public partial class MainWindow
{
    public OverlayLayout GetOverlayLayout() => (settings?.Overlay ?? new OverlayLayout()).Copy().Normalize();
    public void SaveOverlayLayout(OverlayLayout layout)
    {
        if (settings == null) return;
        settings.Overlay = layout.Copy().Normalize();
        ScheduleSave();
    }
}
