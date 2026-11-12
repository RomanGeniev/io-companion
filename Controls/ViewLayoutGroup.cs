using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GraphiteCompanion.Controls;

public sealed class ViewBlockSetting
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
    public LayoutSize Size { get; set; } = new();
}

// Wrappers keep feature-dependent Visibility on the actual control independent of user layout.
public sealed class ViewLayoutGroup
{
    public Action<string, System.Collections.Generic.IReadOnlyList<ViewLayoutGroup>>? EnterRequested { get; set; }
    public Action<System.Windows.FrameworkElement>? OptionsRequested { get; set; }
    public Action<EditTile>? SelectionRequested { get; set; }
    public Action? OperationStarting {get;set;}
    public Action? OperationFinished {get;set;}
    public Action? OperationCanceled {get;set;}
    private readonly Dictionary<int,List<ViewLayoutGroup>> nested = new();
    private Dictionary<string,EditTile> available=new();
    private IReadOnlyList<ViewLayoutGroup> registry=Array.Empty<ViewLayoutGroup>();
    private Dictionary<string,string> placements=new();
    internal Action? DragSessionStarted {get;set;}
    internal Action<bool>? DragSessionFinished {get;set;}
    internal Func<EditTile.Payload,ViewLayoutGroup,Point,bool>? DragPreviewRequested {get;set;}
    internal bool Horizontal=>horizontal;
    internal EditTile OriginalTile(string id)=>wrappers[int.Parse(id)];
    internal FrameworkElement HostElement=>host;
    internal IEnumerable<(string Id,EditTile Tile)> Entries=>current.Where(x=>x.Visible&&available.ContainsKey(x.Id)).Select(x=>(x.Id,available[x.Id]));
    internal bool HasItem(string id)=>available.ContainsKey(id);
    internal string NameFor(string id)=>available.TryGetValue(id,out var tile)?tile.Title:id;
    internal static string Key(string group,string id)=>group+"::"+id;
    private (ViewLayoutGroup Group,string Id) OwnerOf(string id) {
        string key=Key(Id,id);var target=placements.TryGetValue(key,out var destination)?registry.FirstOrDefault(g=>g.Id==destination):null;
        return target==null||target==this?(this,id):(target,key);
    }
    internal void ConfigureTransfers(IReadOnlyList<ViewLayoutGroup> groups,Dictionary<string,string> locations) {
        registry=groups;placements=locations;available.Clear();
        foreach(var group in groups)for(int i=0;i<group.wrappers.Length;i++) {
            string key=Key(group.Id,i.ToString());string target=locations.TryGetValue(key,out var to)&&groups.Any(g=>g.Id==to)?to:group.Id;
            if(group.wrappers[i].IsContainer)target=group.Id;
            if(target==Id){
                var tile=group.wrappers[i];available[group==this?i.ToString():key]=tile;
                if(group!=this){tile.SetBinding(FrameworkElement.DataContextProperty,new Binding("DataContext"){Source=group.host});tile.SetBinding(UIElement.IsEnabledProperty,new Binding("IsEnabled"){Source=group.host});}
                else{BindingOperations.ClearBinding(tile,FrameworkElement.DataContextProperty);BindingOperations.ClearBinding(tile,UIElement.IsEnabledProperty);}
            }
        }
    }
    internal void ReleaseForReparent(){flow?.Children.Clear();host.Children.Clear();}
    private void HideItem(string id){if(edited==null)return;OperationStarting?.Invoke();var next=Normalize(current);next.Find(x=>x.Id==id)!.Visible=false;Apply(next);edited(Snapshot());OperationFinished?.Invoke();}
    private void MoveByKey(string id,int delta){OperationStarting?.Invoke();int from=current.FindIndex(x=>x.Id==id);if(from<0)return;int to=Math.Clamp(from+delta,0,current.Count-1);var value=current[from];current.RemoveAt(from);current.Insert(to,value);Apply(current);edited?.Invoke(Snapshot());OperationFinished?.Invoke();available[id].Focus();}
    private void ResizeItem(string id,LayoutSize size){current.First(x=>x.Id==id).Size=size;available[id].SetSize(size);flow?.InvalidateMeasure();host.InvalidateMeasure();edited?.Invoke(Snapshot());}

    public IEnumerable<ViewLayoutGroup> AllGroups => new[]{this}.Concat(nested.Values.SelectMany(v=>v).SelectMany(g=>g.AllGroups));
    public void Expand()
    {
        if (nested.Count>0) return;
        for(int i=0;i<wrappers.Length;i++) { var groups=CanvasParts.Discover(Id+"."+i,wrappers[i].Element); nested[i]=groups; wrappers[i].SetInnerAvailable(groups.Count>0); foreach(var group in groups)group.Expand(); }
    }
    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<string> Names { get; }
    internal IEnumerable<EditTile> VisibleTiles => current.Where(x=>x.Visible&&available.ContainsKey(x.Id)).Select(x=>available[x.Id]);
    private readonly Panel host;
    private readonly EditTile[] wrappers;
    private readonly bool horizontal;
    private readonly int uniformColumns;
    private readonly LayoutFlowPanel? flow;
    private List<ViewBlockSetting> current = new();
    private Action<List<ViewBlockSetting>>? edited;
    private List<ViewBlockSetting>? dragOriginal;
    private Dictionary<string, Rect> dragSlots = new();
    public void SetEditing(Action<List<ViewBlockSetting>>? changed)
    {
        edited = changed;
        if (changed == null) { dragOriginal = null; dragSlots.Clear(); }
        foreach (var wrapper in available.Values) wrapper.SetEditing(changed != null);
        UpdateColumns();
    }
    private void UpdateColumns()
    {
        if (host is System.Windows.Controls.Primitives.UniformGrid uniform)
            uniform.Columns = flow!=null?1:Math.Max(1, Math.Min(current.Count(x => x.Visible), uniformColumns > 0 ? uniformColumns : wrappers.Length));
        if(flow!=null)flow.Columns=Math.Max(1,Math.Min(current.Count(x=>x.Visible),uniformColumns>0?uniformColumns:wrappers.Length));
    }
    private void Move(string source, string target, bool after)
    {
        if (edited == null || source == target) return;
        var value = current.Find(x => x.Id == source); if (value == null) return;
        var next = Normalize(dragOriginal ?? current); next.RemoveAll(x => x.Id == source);
        int index = next.FindIndex(x => x.Id == target); if (index < 0) return;
        next.Insert(index + (after ? 1 : 0), value); SetPreview(next);
    }
    private readonly GridLength[] lengths;
    private readonly int[] slots;
    public ViewLayoutGroup(string id, string title, Panel host, params string[] names)
    {
        Id = id; Title = title; this.host = host; Names = names;
        var children = host.Children.Cast<FrameworkElement>().ToArray();
        if (children.Length != names.Length) throw new ArgumentException("Layout block count mismatch: " + id);
        horizontal = host is Grid g && g.ColumnDefinitions.Count > 1 || host is StackPanel { Orientation: Orientation.Horizontal } || host is WrapPanel || host is System.Windows.Controls.Primitives.UniformGrid;
        children = host is Grid ? children.OrderBy(x => horizontal ? Grid.GetColumn(x) : Grid.GetRow(x)).ToArray() : children;
        slots = children.Select(x => horizontal ? Grid.GetColumn(x) : Grid.GetRow(x)).ToArray();
        lengths = host is Grid grid ? horizontal ? grid.ColumnDefinitions.Select(x => x.Width).ToArray() : grid.RowDefinitions.Select(x => x.Height).ToArray() : Array.Empty<GridLength>();
        uniformColumns = host is System.Windows.Controls.Primitives.UniformGrid uniform ? uniform.Columns : 0;
        wrappers = new EditTile[children.Length]; host.Children.Clear();
        if(horizontal) {
            flow=new LayoutFlowPanel{Columns=Math.Max(1,uniformColumns>0?uniformColumns:children.Length),EqualColumns=host is System.Windows.Controls.Primitives.UniformGrid};
            if(host is StackPanel stack)stack.Orientation=Orientation.Vertical;
            if(host is Grid flowGrid) {Grid.SetColumnSpan(flow,Math.Max(1,flowGrid.ColumnDefinitions.Count));foreach(var col in flowGrid.ColumnDefinitions)col.Width=new GridLength(1,GridUnitType.Star);}
        }
        for (int i = 0; i < children.Length; i++)
        {
            string blockId = i.ToString();
            wrappers[i]=new EditTile(children[i],this,blockId,names[i],()=>{var owner=OwnerOf(blockId);owner.Group.HideItem(owner.Id);},()=>{},()=>{}){InlineActions=true,ShowResizeGrip=true};
            available[blockId]=wrappers[i];
            wrappers[i].Selected += tile => SelectionRequested?.Invoke(tile);
            if(flow!=null&&slots[i]<lengths.Length)flow.Lengths[wrappers[i]]=lengths[slots[i]];
            wrappers[i].DragStarted += ()=>{if(DragSessionStarted!=null)DragSessionStarted();else{OperationStarting?.Invoke();BeginDrag();}};
            wrappers[i].ResizeStarted+=()=>OperationStarting?.Invoke();wrappers[i].ResizeFinished+=accepted=>{if(accepted)OperationFinished?.Invoke();else OperationCanceled?.Invoke();};
            wrappers[i].MoveByKeyboard+=delta=>{var owner=OwnerOf(blockId);owner.Group.MoveByKey(owner.Id,delta);};
            wrappers[i].SizeChangedByUser += size => {var owner=OwnerOf(blockId);owner.Group.ResizeItem(owner.Id,size);};
            wrappers[i].DragFinished += accepted => {
                if(DragSessionFinished!=null){DragSessionFinished(accepted);return;}
                if (!accepted && dragOriginal != null) { Apply(dragOriginal); edited?.Invoke(Snapshot()); }
                dragOriginal = null; dragSlots.Clear();OperationFinished?.Invoke();
            };
            host.Children.Add(wrappers[i]);
        }
        host.AllowDrop = true;
        host.PreviewDragOver += (_, e) => {
            if(edited==null)return;
            if(e.Data.GetData(typeof(EditTile.Payload)) is not EditTile.Payload payload)return;
            if(DragPreviewRequested!=null&&payload.Scope is ViewLayoutGroup source&&registry.Contains(source)) {
                // Tunnelling events first reach ancestor groups. The innermost host wins.
                for(var n=e.OriginalSource as DependencyObject;n!=null;n=n is System.Windows.Media.Visual?System.Windows.Media.VisualTreeHelper.GetParent(n):LogicalTreeHelper.GetParent(n)) {
                    var closest=registry.FirstOrDefault(g=>ReferenceEquals(g.host,n));if(closest!=null){if(closest!=this)return;break;}
                }
                e.Handled=true;e.Effects=DragPreviewRequested(payload,this,e.GetPosition(host))?DragDropEffects.Move:DragDropEffects.None;return;
            }
            if(!ReferenceEquals(payload.Scope,this))return;
            e.Handled=true;e.Effects=DragDropEffects.Move;PreviewAt(payload.Id,e.GetPosition(host));
        };
        host.Drop+=(_,e)=>{if(edited==null||e.Data.GetData(typeof(EditTile.Payload)) is not EditTile.Payload p)return;if(ReferenceEquals(p.Scope,this)||p.Scope is ViewLayoutGroup g&&registry.Contains(g)){e.Handled=true;e.Effects=DragPreviewRequested==null||DragPreviewRequested(p,this,e.GetPosition(host))?DragDropEffects.Move:DragDropEffects.None;}};
        Apply(null);
    }
    private void BeginDrag()
    {
        dragOriginal = Snapshot();
        dragSlots = current.Where(b => b.Visible).ToDictionary(b => b.Id, b => {
            var tile = available[b.Id]; return new Rect(tile.TranslatePoint(new Point(), host), tile.RenderSize);
        });
    }
    private void PreviewAt(string id, Point at)
    {
        if (dragOriginal == null) return;
        var hit = dragSlots.OrderBy(x => {
            var r = x.Value; return (new Point(Math.Clamp(at.X, r.Left, r.Right), Math.Clamp(at.Y, r.Top, r.Bottom)) - at).LengthSquared;
        }).FirstOrDefault();
        if (hit.Key == null) return;
        if (hit.Key == id) { SetPreview(Normalize(dragOriginal)); return; }
        // Crossing into another item's original slot moves past that item. This
        // avoids requiring a large item to chase a smaller moving midpoint.
        int source = dragOriginal.FindIndex(b => b.Id == id), target = dragOriginal.FindIndex(b => b.Id == hit.Key);
        Move(id, hit.Key, target > source);
    }
    private void SetPreview(List<ViewBlockSetting> next)
    {
        if (current.Select(b => (b.Id, b.Visible)).SequenceEqual(next.Select(b => (b.Id, b.Visible)))) return;
        Apply(next); edited?.Invoke(Snapshot()); DragPreview.Refresh();
    }
    private void ShowSize(string id) => OptionsRequested?.Invoke(LayoutSizing.Editor(()=>current.First(x=>x.Id==id).Size, value=>{current.First(x=>x.Id==id).Size=value; Apply(current); edited?.Invoke(Snapshot());},()=>available[id].ReadableMinimumWidth));
    public List<ViewBlockSetting> Snapshot() => Normalize(current);
    // Used only once while migrating the old nested page containers to semantic cards.
    public FrameworkElement Take(int index) => wrappers[index].Detach();
    public List<ViewBlockSetting> Normalize(IEnumerable<ViewBlockSetting>? source)
    {
        bool Disconnected(string id){int split=id.LastIndexOf("::",StringComparison.Ordinal);if(split<0)return false;string origin=id[..split];return !registry.Any(g=>g.Id==origin);}
        var result=(source??Array.Empty<ViewBlockSetting>()).Where(x=>x!=null&&(available.ContainsKey(x.Id)||Disconnected(x.Id))).Select(x=>new ViewBlockSetting{Id=x.Id,Visible=x.Visible,Size=(x.Size??new()).Copy()}).DistinctBy(x=>x.Id).ToList();
        foreach(var id in available.Keys)if(!result.Any(x=>x.Id==id))result.Add(new(){Id=id});
        return result;
    }
    public void Apply(IEnumerable<ViewBlockSetting>? value)
    {
        var settings = current = Normalize(value); UpdateColumns();
        flow?.Children.Clear();
        host.Children.Clear();
        int visible = settings.Count(x => x.Visible), index = 0;
        if (flow==null && host is Grid grid)
        {
            for (int i = 0; i < (horizontal?grid.ColumnDefinitions.Count:grid.RowDefinitions.Count); i++)
            {
                if (horizontal) grid.ColumnDefinitions[i].Width = new GridLength(0);
                else grid.RowDefinitions[i].Height = new GridLength(0);
            }
        }
        foreach (var setting in settings)
        {
            if(!available.TryGetValue(setting.Id,out var wrapper))continue;
            int original=int.TryParse(setting.Id,out var local)?local:0;
            if(wrapper.Parent is Panel previous)previous.Children.Remove(wrapper);
            wrapper.SetSize(setting.Size);
            wrapper.Visibility = setting.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (flow==null && setting.Visible && host is Grid target)
            {
                int slot = index<slots.Length?slots[index]:index;int sourceSlot=slots[original];
                if(horizontal)while(target.ColumnDefinitions.Count<=slot)target.ColumnDefinitions.Add(new());else while(target.RowDefinitions.Count<=slot)target.RowDefinitions.Add(new());
                if (horizontal)
                {
                    Grid.SetColumn(wrapper, slot); Grid.SetColumnSpan(wrapper, visible == 1 ? target.ColumnDefinitions.Count : 1);
                    target.ColumnDefinitions[slot].Width = visible == 1 ? new GridLength(1, GridUnitType.Star) : lengths[sourceSlot];
                    if (index > 0) for (int j = slots[index - 1] + 1; j < slot; j++) target.ColumnDefinitions[j].Width = lengths[j];
                }
                else
                {
                    Grid.SetRow(wrapper, slot); target.RowDefinitions[slot].Height = setting.Size.Height>0||!int.TryParse(setting.Id,out _)?GridLength.Auto:lengths[sourceSlot];
                    if (index > 0) for (int j = (index-1<slots.Length?slots[index-1]:index-1) + 1; j < slot; j++) target.RowDefinitions[j].Height = j<lengths.Length?lengths[j]:GridLength.Auto;
                }
                index++;
            }
            if(flow!=null)flow.Children.Add(wrapper);else host.Children.Add(wrapper);
        }
        if(flow!=null)host.Children.Add(flow);
    }
}
