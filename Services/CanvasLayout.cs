using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using GraphiteCompanion.Controls;
namespace GraphiteCompanion.Services;

public sealed class CanvasPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public CanvasPosition Copy() => new() { X=double.IsFinite(X)?System.Math.Clamp(X,0,10000):0, Y=double.IsFinite(Y)?System.Math.Clamp(Y,0,100000):0 };
}
public sealed class CanvasBlock
{
    public string Id { get; set; } = "";
    public int Zone { get; set; }
    public bool Visible { get; set; } = true;
    public LayoutSize Size { get; set; } = new();
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public CanvasPosition? Position { get; set; }
}
public sealed class CanvasLayout
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flow {get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public double DesignWidth {get;set;}
    [JsonIgnore]
    public Dictionary<string,string> ElementHosts {get;set;}=new();
    [JsonPropertyName("ElementHosts"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,string>? SavedElementHosts {get=>ElementHosts.Count>0?ElementHosts:null;set=>ElementHosts=value??new();}
    public List<CanvasBlock> Blocks { get; set; } = new();
    public Dictionary<string, List<ViewBlockSetting>> Elements { get; set; } = new();
    public CanvasLayout Copy() => new()
    {
        Flow=Flow,
        DesignWidth=double.IsFinite(DesignWidth)?System.Math.Clamp(DesignWidth,0,10000):0,
        ElementHosts=new(ElementHosts??new()),
        Blocks = (Blocks ?? new()).Where(x => x != null).Select(x => new CanvasBlock { Id = x.Id, Zone = x.Zone, Visible = x.Visible, Size = (x.Size ?? new()).Copy(), Position=x.Position?.Copy() }).ToList(),
        Elements = (Elements ?? new()).ToDictionary(x => x.Key, x => (x.Value ?? new()).Where(v => v != null).Select(v => new ViewBlockSetting { Id = v.Id, Visible = v.Visible, Size = (v.Size ?? new()).Copy() }).ToList())
    };
}
