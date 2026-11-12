using System;
using System.Collections.Generic;
using System.Linq;

namespace GraphiteCompanion.Services;

public sealed class OverlayBlock
{
    public string Id { get; set; } = "";
    public int Zone { get; set; } // 0 full width, 1 left, 2 right
    public bool Visible { get; set; } = true;
}

public sealed class OverlayLayout
{
    public CanvasLayout? Composition { get; set; }
    public double Opacity { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public double Width { get; set; } = 960;
    public bool Footer { get; set; } = true;
    public bool RememberPosition { get; set; } = true;
    public double? X { get; set; }
    public double? Y { get; set; }
    public string Note { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public List<OverlayBlock> Blocks { get; set; } = Defaults();
    public static readonly (string Id, string Name, int Zone, bool Visible)[] Catalog =
    {
        ("music", "Музыкальный плеер", 0, true), ("sound", "Режимы и эквалайзер", 1, true),
        ("volume", "Громкость и микрофон", 1, true), ("voice", "Голосовые эффекты", 1, true),
        ("output", "Устройство вывода", 1, true), ("recording", "Запись и снимки", 2, true),
        ("timer", "Таймер", 2, true), ("apps", "Громкость программ", 2, true),
        ("note", "Моя заметка", 1, false), ("image", "Моя картинка", 2, false)
    };
    private static List<OverlayBlock> Defaults() => Catalog.Select(x => new OverlayBlock { Id = x.Id, Zone = x.Zone, Visible = x.Visible }).ToList();
    public OverlayLayout Copy() => new()
    {
        Composition = Composition?.Copy(), Opacity = Opacity, Scale = Scale, Width = Width, Footer = Footer, RememberPosition = RememberPosition,
        X = X, Y = Y, Note = Note, ImagePath = ImagePath,
        Blocks = (Blocks ?? new()).Where(x => x != null).Select(x => new OverlayBlock { Id = x.Id, Zone = x.Zone, Visible = x.Visible }).ToList()
    };
    public OverlayLayout Normalize()
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .35, 1) : 1;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .8, 1.25) : 1;
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 820, 1200) : 960;
        X = X is double x && double.IsFinite(x) ? Math.Clamp(x, 0, 1) : null;
        Y = Y is double y && double.IsFinite(y) ? Math.Clamp(y, 0, 1) : null;
        Note ??= ""; ImagePath ??= ""; if (Note.Length > 500) Note = Note[..500];
        Blocks = (Blocks ?? new()).Where(x => x != null && Catalog.Any(c => c.Id == x.Id)).DistinctBy(x => x.Id).ToList();
        foreach (var entry in Catalog) if (!Blocks.Any(x => x.Id == entry.Id)) Blocks.Add(new() { Id = entry.Id, Zone = entry.Zone, Visible = entry.Visible });
        foreach (var block in Blocks) block.Zone = Math.Clamp(block.Zone, 0, 2);
        return this;
    }
}
