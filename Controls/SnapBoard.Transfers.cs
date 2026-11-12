using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using GraphiteCompanion.Services;
namespace GraphiteCompanion.Controls;

public sealed partial class SnapBoard
{
    private CanvasLayout? elementBaseline;
    private Dictionary<string,Dictionary<string,Rect>> elementSlots=new();
    private Dictionary<ViewLayoutGroup,Rect> elementHostSlots=new();
    private void BeginElementDrag()
    {
        BeginOperation();elementBaseline=state.Copy();elementSlots.Clear();elementHostSlots.Clear();
        foreach(var group in AllGroups)elementSlots[group.Id]=group.Entries.Where(e=>e.Tile.IsVisible).ToDictionary(e=>e.Id,e=>new Rect(e.Tile.TranslatePoint(new Point(),body),e.Tile.RenderSize));
        foreach(var group in AllGroups.Where(g=>g.HostElement.IsVisible&&g.HostElement.ActualHeight>0))elementHostSlots[group]=new Rect(group.HostElement.TranslatePoint(new Point(),body),group.HostElement.RenderSize);
    }
    private void FinishElementDrag(bool accepted)
    {
        if(!accepted&&elementBaseline!=null)Apply(elementBaseline);elementBaseline=null;elementSlots.Clear();elementHostSlots.Clear();EndOperation();
    }
    private bool PreviewElement(EditTile.Payload payload,ViewLayoutGroup target,Point point)
    {
        if(elementBaseline==null||payload.Scope is not ViewLayoutGroup source)return false;
        var at=target.HostElement.TranslatePoint(point,body);
        var originalTarget=elementHostSlots.Where(p=>p.Value.Contains(at)).OrderBy(p=>p.Value.Width*p.Value.Height).FirstOrDefault();
        if(originalTarget.Key!=null)target=originalTarget.Key;
        var tile=source.OriginalTile(payload.Id);string key=ViewLayoutGroup.Key(source.Id,payload.Id);
        string home=elementBaseline.ElementHosts.TryGetValue(key,out var destination)?destination:source.Id;
        string fromId=home==source.Id?payload.Id:key,toId=target==source?payload.Id:key;
        if(tile.IsContainer&&target.Id!=home)return false;
        for(DependencyObject? n=target.HostElement;n!=null;n=VisualTreeHelper.GetParent(n))if(n==tile)return false;
        var candidates=elementSlots.TryGetValue(target.Id,out var captured)?captured:new();
        if(target.Id==home&&candidates.TryGetValue(fromId,out var originalSlot)&&originalSlot.Contains(at)){if(JsonSerializer.Serialize(state)!=JsonSerializer.Serialize(elementBaseline))Apply(elementBaseline);return true;}
        var closest=candidates.Where(p=>!(target.Id==home&&p.Key==fromId)).OrderBy(p=>{var r=p.Value;return(new Point(Math.Clamp(at.X,r.Left,r.Right),Math.Clamp(at.Y,r.Top,r.Bottom))-at).LengthSquared;}).FirstOrDefault();
        bool after=closest.Key!=null&&(target.Horizontal&&at.Y>=closest.Value.Top&&at.Y<=closest.Value.Bottom?at.X>closest.Value.Left+closest.Value.Width/2:at.Y>closest.Value.Top+closest.Value.Height/2);
        if(target.Id==home&&closest.Key!=null){var order=elementBaseline.Elements[home];after=order.FindIndex(v=>v.Id==closest.Key)>order.FindIndex(v=>v.Id==fromId);}
        var next=elementBaseline.Copy();if(!next.Elements.TryGetValue(home,out var from))return false;var moving=from.FirstOrDefault(v=>v.Id==fromId);if(moving==null)return false;
        from.Remove(moving);if(target==source)next.ElementHosts.Remove(key);else next.ElementHosts[key]=target.Id;
        if(!next.Elements.TryGetValue(target.Id,out var into))next.Elements[target.Id]=into=new();
        moving.Id=toId;int index=closest.Key==null?into.Count:into.FindIndex(v=>v.Id==closest.Key)+(after?1:0);into.Insert(Math.Clamp(index,0,into.Count),moving);
        if(JsonSerializer.Serialize(state)!=JsonSerializer.Serialize(next)){Apply(next);DragPreview.Refresh();}
        return true;
    }
}
