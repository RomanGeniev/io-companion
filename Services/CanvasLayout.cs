using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using GraphiteCompanion.Controls;
namespace GraphiteCompanion.Services;

public sealed class CanvasBlock
{
    public string Id { get; set; } = "";
    public int Zone { get; set; }
    public bool Visible { get; set; } = true;
    public LayoutSize Size { get; set; } = new();
}
public sealed class CanvasLayout
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flow {get;set;}
    [JsonIgnore]
    public Dictionary<string,string> ElementHosts {get;set;}=new();
    [JsonPropertyName("ElementHosts"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,string>? SavedElementHosts {get=>ElementHosts.Count>0?ElementHosts:null;set=>ElementHosts=value??new();}
    public List<CanvasBlock> Blocks { get; set; } = new();
    public Dictionary<string, List<ViewBlockSetting>> Elements { get; set; } = new();
    public CanvasLayout Copy() => new()
    {
        Flow=Flow,
        ElementHosts=new(ElementHosts??new()),
        Blocks = (Blocks ?? new()).Where(x => x != null).Select(x => new CanvasBlock { Id = x.Id, Zone = x.Zone, Visible = x.Visible, Size = (x.Size ?? new()).Copy() }).ToList(),
        Elements = (Elements ?? new()).ToDictionary(x => x.Key, x => (x.Value ?? new()).Where(v => v != null).Select(v => new ViewBlockSetting { Id = v.Id, Visible = v.Visible, Size = (v.Size ?? new()).Copy() }).ToList())
    };
}
