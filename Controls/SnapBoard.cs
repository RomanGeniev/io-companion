using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using GraphiteCompanion.Services;
namespace GraphiteCompanion.Controls;

public sealed class CanvasCard
{
    public string Id { get; }
    public string Name { get; }
    public FrameworkElement Element { get; }
    public int Zone { get; }
    public bool Visible { get; }
    public List<ViewLayoutGroup> Inside { get; } = new();
    public FrameworkElement? Options { get; set; }
    public string InsideLabel { get; set; } = "Содержимое";
    public bool Resizable { get; set; } = true;
    public IEnumerable<ViewLayoutGroup> AllGroups => Inside.SelectMany(g=>g.AllGroups);
    public CanvasCard(string id, string name, FrameworkElement element, int zone = 1, bool visible = true) { Id = id; Name = name; Element = element; Zone = zone; Visible = visible; }
}
public sealed partial class SnapBoard : UserControl
{
    // Fluent's default scrollbar floats above content, including resize handles.
    // Give it a separate permanent column in every theme, with native scroll parts.
    private static readonly ControlTemplate BoardScrollTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollViewer">
          <Grid Background="{TemplateBinding Background}">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="17"/></Grid.ColumnDefinitions>
            <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Grid.Column="0" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}" ClipToBounds="True"/>
            <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Orientation="Vertical" Minimum="0" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}" Value="{TemplateBinding VerticalOffset}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
          </Grid>
        </ControlTemplate>
        """);
    private static readonly ControlTemplate MenuTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
          <Border Background="{DynamicResource CardBrush}" BorderBrush="{DynamicResource CardBorderBrush}" BorderThickness="1" CornerRadius="9" Padding="4"><ItemsPresenter/></Border>
        </ControlTemplate>
        """);
    private static readonly ControlTemplate MenuItemTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
          <Border x:Name="surface" Background="Transparent" CornerRadius="6" Padding="12,8"><ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/></Border>
          <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource GhostHoverBrush}"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private readonly List<CanvasCard> cards;
    private readonly double leftWeight, rightWeight;
    private readonly Dictionary<string, EditTile> tiles = new();
    private readonly StackPanel full = new(), left = new(), right = new();
    private readonly Grid columns = new();
    private readonly StackPanel toolbar = new(), library = new(), options = new();
    private readonly Border toolbarSurface = new();
    private readonly ScrollViewer scroll;
    private readonly Grid root = new() { MinHeight = 64 };
    private readonly Popup libraryPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Top };
    private readonly Popup optionsPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Top };
    private ContextMenu? currentMenu;
    private readonly FloatingTools toolsLayer = new();
    private CanvasLayout? dragOriginal;
    private Dictionary<string, Rect> dragSlots = new();
    private readonly StackPanel body = new();
    private readonly CanvasBoardPanel canvas=new();
    private double dragWidth;
    internal FrameworkElement DragSurface => root;
    internal void ScrollEditing(int delta)=>scroll.ScrollToVerticalOffset(scroll.VerticalOffset-delta);
    private CanvasLayout state = new(), original = new();
    private string? active;
    private readonly Stack<(string Name, IReadOnlyList<ViewLayoutGroup> Groups)> path = new();
    public bool SingleColumn { get; set; }
    private IEnumerable<ViewLayoutGroup> ActiveGroups => path.Count>0 ? path.Peek().Groups : cards.FirstOrDefault(c=>c.Id==active)?.Inside ?? Enumerable.Empty<ViewLayoutGroup>();
    private IEnumerable<ViewLayoutGroup> AllGroups => cards.SelectMany(c=>c.AllGroups);
    private Window? owner;
    private Button? addButton;
    private EditTile? selectedElement;
    private readonly WrapPanel selectionTools = new() { Margin = new Thickness(4, 4, 4, 0) };
    public bool Editing { get; private set; }
    public string? ActiveBlock => active;
    public FrameworkElement? Appearance { get; set; }
    public event Action<bool>? Finished;
    public event Action? ModeChanged;
    public CanvasLayout Value => state.Copy();
    public SnapBoard(IEnumerable<CanvasCard> source, CanvasLayout? saved = null, double leftWeight = 1, double rightWeight = 1)
    {
        cards = source.ToList(); foreach(var card in cards) CanvasParts.Enrich(card); this.leftWeight = leftWeight; this.rightWeight = rightWeight;
        toolbarSurface.Child = toolbar; toolbarSurface.Padding = new Thickness(8); toolbarSurface.CornerRadius = new CornerRadius(12);
        toolbarSurface.BorderThickness = new Thickness(1); toolbarSurface.SetResourceReference(Border.BackgroundProperty, "CardBrush"); toolbarSurface.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        toolbarSurface.SizeChanged+=(_,_)=>{root.MinHeight=Editing&&!UsesInspector?Math.Max(64,toolbarSurface.ActualHeight+16):64;};
        canvas.LeftWeight=leftWeight;canvas.RightWeight=rightWeight;body.Children.Add(canvas);
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(leftWeight, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(rightWeight, GridUnitType.Star) });
        columns.Children.Add(left); Grid.SetColumn(right, 2); columns.Children.Add(right);
        // Reserve the scrollbar gutter in normal and edit mode alike. Otherwise the
        // first growing card can create a scrollbar and unexpectedly shrink itself.
        scroll = new ScrollViewer { Template = BoardScrollTemplate, Content = body, Background = Brushes.Transparent, AllowDrop = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);root.Children.Add(placementGuides); toolsLayer.Children.Add(toolbarSurface); root.Children.Add(toolsLayer);
        foreach (var pair in new[] { (libraryPopup, library), (optionsPopup, options) }) {
            var frame = new Border { Child = new ScrollViewer { Content = pair.Item2, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Padding = new Thickness(10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), MaxWidth = 480 };
            frame.SetResourceReference(Border.BackgroundProperty, "CardBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush"); pair.Item1.Child = frame; root.Children.Add(pair.Item1);
            pair.Item1.Placement=PlacementMode.Custom;
            pair.Item1.CustomPopupPlacementCallback=(popupSize,targetSize,offset)=>{
                double targetX=(pair.Item1.PlacementTarget as FrameworkElement)?.TranslatePoint(new Point(),root).X??0;
                double x=Math.Clamp(targetX+targetSize.Width-popupSize.Width,0,Math.Max(0,root.ActualWidth-popupSize.Width))-targetX;
                return new[]{new CustomPopupPlacement(new Point(x,-popupSize.Height),PopupPrimaryAxis.Vertical),new CustomPopupPlacement(new Point(x,targetSize.Height),PopupPrimaryAxis.Vertical)};
            };
        }
        Content = new AdornerDecorator { Child = root };
        foreach (var card in cards) AddTile(card);
        void PreviewBlockDrag(object sender,DragEventArgs e) {
            var payload = EditTile.Read(e, this); if (payload == null || !Editing || active != null) return;
            e.Effects = DragDropEffects.Move; e.Handled = true;
            AutoScroll(e); PreviewAt(payload.Id, DragPreview.PointerOn(body,e.GetPosition(body)));
        }
        scroll.PreviewDragEnter += PreviewBlockDrag;
        scroll.PreviewDragOver += PreviewBlockDrag;
        scroll.PreviewDrop += AcceptDrop;
        Loaded += (_, _) => { if(owner!=null)owner.PreviewKeyDown-=OwnerKeyDown;owner = Window.GetWindow(this); if (owner != null) owner.PreviewKeyDown += OwnerKeyDown; };
        Apply(saved); Unloaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
            // Theme/template changes can deliver a queued Unloaded after a new visual tree is attached.
            if(PresentationSource.FromVisual(this)!=null)return;
            if(owner!=null)owner.PreviewKeyDown-=OwnerKeyDown;owner=null;if(Editing)Finish(false);
        }));
        PreviewMouseDown += (_, e) =>
        {
            if (!Editing || active == null) return;
            for (var node = e.OriginalSource as DependencyObject; node != null && node != this; node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is EditTile tile) { if (!tile.Editable) e.Handled = true; return; }
        };
    }
    private void AddTile(CanvasCard card)
    {
        var tile = new EditTile(card.Element,this,card.Id,card.Name,()=>Hide(card.Id),card.Inside.Count>0||card.Options!=null?()=>Enter(card.Id):null,card.Resizable?()=>{}:null){Margin=new Thickness(0,0,0,10),AllowDrop=true,InlineActions=true,InsideLabel=card.InsideLabel,IsContainer=card.Inside.Count>0,HasOptions=card.Options!=null};
        tile.Selected+=SelectElement;
        tile.ToolTip = card.Name + " · за ⠿ — переместить, за угол — масштабировать";
        tile.DragStarted+=()=>{BeginOperation();BeginDrag();};
        tile.ResizeStarted+=BeginBlockResize;tile.ResizeFinished+=accepted=>{CompletePlacement(accepted);if(accepted)EndOperation();else CancelOperation();};
        tile.MoveByKeyboard+=delta=>MoveBlockByKeyboard(card.Id,delta);
        tile.Nudge+=delta=>NudgeBlock(card.Id,delta);
        tile.SizeChangedByUser+=size=>ResizeBlock(card.Id,size);
        tile.DragFinished+=accepted=>{if(!accepted&&dragOriginal!=null)SetPreview(dragOriginal);CompletePlacement(accepted);dragOriginal=null;dragSlots.Clear();EndOperation();};
        tiles[card.Id]=tile;
        foreach(var group in card.AllGroups) { group.EnterRequested=(name,groups)=>{path.Push((name,groups));Render();ModeChanged?.Invoke();}; group.OptionsRequested=ShowOptions; group.SelectionRequested=SelectElement;group.OperationStarting=BeginOperation;group.OperationFinished=EndOperation;group.OperationCanceled=CancelOperation;group.DragSessionStarted=BeginElementDrag;group.DragSessionFinished=FinishElementDrag;group.DragPreviewRequested=PreviewElement; }
    }
    public void ReplaceCards(string prefix,IEnumerable<CanvasCard> replacements)
    {
        if(Editing)Finish(false);
        foreach(var card in cards.Where(c=>c.Id.StartsWith(prefix,StringComparison.Ordinal)).ToArray()) {
            var tile=tiles[card.Id]; if(tile.Parent is Panel parent)parent.Children.Remove(tile);tile.Detach();tiles.Remove(card.Id);cards.Remove(card);
        }
        foreach(var card in replacements){CanvasParts.Enrich(card);cards.Add(card);AddTile(card);}
        Apply(state);
    }
    private void ShowOptions(FrameworkElement value) {
        options.Children.Clear(); options.Children.Add(value);
        if (UsesInspector) { inspectorDetail = "options"; RenderInspector(); return; }
        libraryPopup.IsOpen=false;optionsPopup.IsOpen=true;
    }
    private void OwnerKeyDown(object sender, KeyEventArgs e) { if(Editing && e.Key==Key.Escape && EditTile.CancelActiveResize()){e.Handled=true;return;} if(Editing && e.Key==Key.Z && Keyboard.Modifiers==ModifierKeys.Control && e.OriginalSource is not TextBoxBase){Undo();e.Handled=true;return;} if (e.Key != Key.Escape || !Editing || e.Handled || DragPreview.Active) return; e.Handled = true; if (CloseSelectionList() || CloseFlyouts()) return; if (active != null) Back(); else Finish(false); }
    public void Apply(CanvasLayout? value)
    {
        state = (value ?? new()).Copy(); state.Blocks = state.Blocks.Where(x => x != null).DistinctBy(x => x.Id).ToList();
        foreach (var card in cards) if (!state.Blocks.Any(x => x.Id == card.Id)) state.Blocks.Add(new() { Id = card.Id, Zone = card.Zone, Visible = card.Visible });
        foreach (var block in state.Blocks) block.Zone = Math.Clamp(block.Zone, 0, 2);
        var groups=AllGroups.Distinct().ToArray();
        foreach(var group in groups)group.ConfigureTransfers(groups,state.ElementHosts);
        foreach(var group in groups)group.ReleaseForReparent();
        foreach (var group in groups) { state.Elements.TryGetValue(group.Id, out var valueList); group.Apply(valueList); state.Elements[group.Id] = group.Snapshot(); }
        Render();
    }
    public void Begin() {
        if (Editing) { inspectorWindow?.Activate(); return; }
        original = state.Copy(); undo.Clear(); operationBefore=null; Editing = true; active = null; path.Clear();
        inspectorAppearance = false; inspectorDetail = "";
        Render(); if (UsesInspector) OpenInspector(); ModeChanged?.Invoke();
    }
    public void Finish(bool save)
    {
        if (!Editing) return; Editing = false; CloseInspector(); active = null; path.Clear(); dragOriginal = null; dragSlots.Clear();
        CompletePlacement(save);
        foreach (var group in AllGroups) group.SetEditing(null);
        if (!save) Apply(original); else Render(); Finished?.Invoke(save); ModeChanged?.Invoke();
    }
    public void Enter(string id) { if (!Editing) return; var card=cards.FirstOrDefault(c=>c.Id==id);if(card?.Options!=null)ShowOptions(card.Options);else if(tiles.TryGetValue(id,out var tile))SelectElement(tile); }
    public void Back() { if(path.Count>0)path.Pop();else active=null; Render(); ModeChanged?.Invoke(); }
    private void Hide(string id) { BeginOperation();state.Blocks.First(x => x.Id == id).Visible = false; Render();EndOperation(); }
    private void Render()
    {
        selectedElement?.SetSelected(false); selectedElement=null; inspectorDetail="";
        foreach (var group in AllGroups) group.SetEditing(Editing ? values => { state.Elements[group.Id] = values; RenderLibrary(); } : null);
        ArrangeBlocks(); RenderToolbar();
    }
    private void ArrangeBlocks()
    {
        canvas.Children.Clear();canvas.Layout=state;canvas.SingleColumn=SingleColumn;
        foreach(var block in state.Blocks) {
            if(!tiles.TryGetValue(block.Id,out var tile))continue;
            tile.SetSize(block.Size);tile.Visibility=block.Visible?Visibility.Visible:Visibility.Collapsed;tile.SetEditing(Editing);tile.Dim(false);canvas.Children.Add(tile);
        }
        PrepareRestoredCards();
    }

    internal void ShowDragTools(bool dragging) { if (dragging) CloseFlyouts(); toolbarSurface.Visibility = Editing && (UsesInspector || !dragging) ? Visibility.Visible : Visibility.Collapsed; }
    internal bool CloseFlyouts() { if (UsesInspector && inspectorDetail.Length>0) { inspectorDetail=""; RenderInspector(); return true; } bool open = libraryPopup.IsOpen || optionsPopup.IsOpen || currentMenu?.IsOpen == true; libraryPopup.IsOpen = optionsPopup.IsOpen = false; if (currentMenu != null) currentMenu.IsOpen = false; return open; }
    private void RenderToolbar()
    {
        if (UsesInspector) { RenderInspector(); return; }
        CloseFlyouts(); toolbar.Children.Clear(); toolbarSurface.Visibility = Editing ? Visibility.Visible : Visibility.Collapsed;root.MinHeight=Editing&&!UsesInspector?Math.Max(64,toolbarSurface.ActualHeight+16):64; if (!Editing) return;
        var current = cards.FirstOrDefault(x => x.Id == active);
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var grip = Text("⠿"); grip.FontSize = 18; grip.Margin = new Thickness(4, 0, 8, 0); grip.Cursor = Cursors.SizeAll; grip.ToolTip = "Перетащить панель редактора";
        toolsLayer.MakeDraggable(grip); actions.Children.Add(grip);
        if (current != null) actions.Children.Add(Button("←", Back, path.Count>0 ? "На уровень выше · Esc" : "Ко всем блокам · Esc"));
        string fullPath=current == null ? "Все блоки" : "Внутри: " + current.Name + string.Concat(path.Reverse().Select(p=>" › "+p.Name));
        var heading = Text(path.Count>0 ? "Внутри: … › "+path.Peek().Name : fullPath); heading.FontWeight = FontWeights.SemiBold; heading.MaxWidth = 230; heading.TextWrapping=TextWrapping.NoWrap;heading.TextTrimming=TextTrimming.CharacterEllipsis;heading.ToolTip=fullPath;heading.Margin = new Thickness(0, 0, 10, 0); actions.Children.Add(heading);
        addButton = Button("＋ Скрытые", () => { RenderLibrary(); optionsPopup.IsOpen = false; libraryPopup.IsOpen = !libraryPopup.IsOpen; }, current == null ? "Вернуть скрытые блоки" : "Вернуть скрытые элементы"); actions.Children.Add(addButton);
        var done = Button("Готово", () => Finish(true)); done.SetResourceReference(StyleProperty, "AccentButton"); actions.Children.Add(done);
        actions.Children.Add(Button("Отмена", () => Finish(false), "Отменить изменения"));
        if(current==null && Appearance!=null)actions.Children.Add(Button("Внешний вид",()=>ShowOptions(Appearance),"Прозрачность, масштаб и положение оверлея"));
        var more = Button("⋯", () => { }); more.ToolTip = "Другие действия";
        var menu = new ContextMenu { Template = MenuTemplate }; menu.SetResourceReference(Control.BackgroundProperty, "CardBrush"); menu.SetResourceReference(Control.ForegroundProperty, "TextBrush"); menu.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");
        if (current == null && Appearance != null) { var appearance = new MenuItem { Header = "Внешний вид оверлея" }; appearance.Click += (_, _) => ShowOptions(Appearance); menu.Items.Add(appearance); }
        var reset = new MenuItem { Header = "Исходное расположение" }; reset.Click += (_, _) => { if (active == null) Apply(null); else { foreach (var g in ActiveGroups.SelectMany(g=>g.AllGroups)) { g.Apply(null); state.Elements[g.Id] = g.Snapshot(); } Render(); } }; menu.Items.Add(reset);
        foreach (MenuItem item in menu.Items) item.Template = MenuItemTemplate;
        currentMenu = menu; more.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; }), true); actions.Children.Add(more);
        toolbar.Children.Add(actions);
        toolbar.Children.Add(selectionTools); RenderSelectionTools();
        libraryPopup.PlacementTarget = addButton; optionsPopup.PlacementTarget = more; RenderLibrary();
        options.Children.Clear();
        var option = current?.Options ?? (current == null ? Appearance : null); if (option != null) { options.Children.Add(option); if (current != null) optionsPopup.IsOpen = true; }

    }
    private void SelectElement(EditTile tile)
    {
        if(!Editing || !tile.Editable)return;
        CloseFlyouts(); inspectorAppearance=false; selectedElement?.SetSelected(false); selectedElement=tile;tile.SetSelected(true); if(UsesInspector)RenderInspector();else RenderSelectionTools();
    }
    private void RenderSelectionTools()
    {
        selectionTools.Children.Clear();
        if(selectedElement?.Visibility != Visibility.Visible || !selectedElement.Editable) { selectedElement?.SetSelected(false); selectedElement=null; }
        var title=Text(selectedElement == null ? (active==null ? "Выберите блок или перетащите его" : "Выберите элемент или перетащите его") : selectedElement.Title);
        title.MaxWidth=290;title.TextTrimming=TextTrimming.CharacterEllipsis;title.ToolTip=title.Text;title.Margin=new Thickness(0,0,8,0);selectionTools.Children.Add(title);
        if(selectedElement != null)selectionTools.Children.Add(selectedElement.ContextActions());
    }
    private void RenderLibrary(int? destination = null)
    {
        if (UsesInspector) { if(selectedElement?.Visibility != Visibility.Visible)selectedElement=null; RenderInspector(); return; }
        if(selectedElement != null && selectedElement.Visibility != Visibility.Visible)RenderSelectionTools();
        library.Children.Clear(); var catalog = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; library.Children.Add(catalog); int count = 0;
        if (active == null) { foreach (var block in state.Blocks.Where(x => !x.Visible && tiles.ContainsKey(x.Id))) { count++; var card = cards.First(x => x.Id == block.Id); catalog.Children.Add(Button("＋ " + card.Name, () => { block.Visible = true; if (destination.HasValue) block.Zone = destination.Value; Render(); tiles[block.Id].BringIntoView(); })); } }
        else foreach (var group in ActiveGroups) foreach (var item in group.Snapshot().Where(x => !x.Visible)) { count++; string id = item.Id; catalog.Children.Add(Button("＋ " + group.NameFor(id), () => { var values = group.Snapshot(); values.First(x => x.Id == id).Visible = true; group.Apply(values); state.Elements[group.Id] = values; RenderLibrary(); libraryPopup.IsOpen = false; })); }
        if (addButton != null) addButton.Content = count > 0 ? $"＋ Скрытые · {count}" : "＋ Скрытые";
        if (count == 0) catalog.Children.Add(Text("Все доступные элементы уже на месте. Скрытые появятся здесь."));
    }
    private StackPanel Zone(int zone) => zone == 0 ? full : zone == 1 ? left : right;
    private void SortVisiblePositions(CanvasLayout value)
    {
        var positions=dragSlots.Count>0?dragSlots:tiles.Where(p=>p.Value.IsVisible).ToDictionary(p=>p.Key,p=>new Rect(p.Value.TranslatePoint(new Point(),body),p.Value.RenderSize));
        value.Blocks=value.Blocks.OrderBy(b=>b.Visible&&positions.TryGetValue(b.Id,out var r)?Math.Round(r.Top/8):double.MaxValue).ThenBy(b=>b.Visible&&positions.TryGetValue(b.Id,out var r)?r.Left:double.MaxValue).ToList();
    }
    private void EnableFlow()
    {
        if(state.Flow)return;SortVisiblePositions(state);state.Flow=true;canvas.Layout=state;
    }
    private void PreviewMove(string id, string? target, int zone, bool after)
    {
        PreviewFromBaseline(id, target, zone, after, false);
    }
    private void BeginDrag()
    {
        dragOriginal = state.Copy(); dragWidth = scroll.ActualWidth;
        dragSlots = state.Blocks.Where(b => b.Visible && tiles.ContainsKey(b.Id) && tiles[b.Id].HasLayoutContent && tiles[b.Id].ActualHeight > 1)
            .ToDictionary(b => b.Id, b => new Rect(tiles[b.Id].TranslatePoint(new Point(), body), tiles[b.Id].RenderSize));
        if(!SingleColumn)placementBaseline=PositionedSnapshot();
    }
    // Resolve against the positions at grab time, never the positions created by a
    // previous preview. A stationary pointer therefore cannot undo its own move.
    private void PreviewAt(string id, Point at)
    {
        if (dragOriginal == null) return;
        if(!SingleColumn){PreviewPosition(id,at);return;}
        var hit=dragSlots.OrderBy(x=>{var r=x.Value;return(new Point(Math.Clamp(at.X,r.Left,r.Right),Math.Clamp(at.Y,r.Top,r.Bottom))-at).LengthSquared;}).FirstOrDefault();
        if(hit.Key==null)return;
        if(hit.Key==id){SetPreview(dragOriginal.Copy());return;}
        var target=dragOriginal.Blocks.First(b=>b.Id==hit.Key);
        var ordered=dragOriginal.Copy();if(!ordered.Flow)SortVisiblePositions(ordered);
        bool after=ordered.Blocks.FindIndex(b=>b.Id==hit.Key)>ordered.Blocks.FindIndex(b=>b.Id==id);
        PreviewFromBaseline(id,hit.Key,target.Zone,after,false);
    }

    private void PreviewFromBaseline(string id, string? target, int zone, bool after, bool swap)
    {
        var next = (dragOriginal ?? state).Copy();
        if(!next.Flow)SortVisiblePositions(next);
        int sourceIndex = next.Blocks.FindIndex(b => b.Id == id), targetIndex = next.Blocks.FindIndex(b => b.Id == target);
        if (sourceIndex < 0 || id == target) return;
        var moving = next.Blocks[sourceIndex];
        next.Flow=true;
        if (swap && targetIndex >= 0) {
            var other = next.Blocks[targetIndex];
            (moving.Zone, other.Zone) = (other.Zone, moving.Zone);
            (next.Blocks[sourceIndex], next.Blocks[targetIndex]) = (other, moving);
        } else {
            next.Blocks.RemoveAt(sourceIndex); if(SingleColumn)moving.Zone=1;
            int index = target == null ? next.Blocks.FindLastIndex(b => b.Zone == zone) + 1 : next.Blocks.FindIndex(b => b.Id == target) + (after ? 1 : 0);
            next.Blocks.Insert(Math.Clamp(index, 0, next.Blocks.Count), moving);
        }
        SetPreview(next);
    }
    private void SetPreview(CanvasLayout next)
    {
        if (System.Text.Json.JsonSerializer.Serialize(state)==System.Text.Json.JsonSerializer.Serialize(next)) return;
        state = next;canvas.MovingId=null; ArrangeBlocks(); DragPreview.Refresh();
    }
    private void AcceptDrop(object sender, DragEventArgs e) { var payload=EditTile.Read(e,this);if (!Editing || active != null || payload == null) return; PreviewAt(payload.Id,DragPreview.PointerOn(body,e.GetPosition(body)));e.Effects = DragDropEffects.Move; e.Handled = true; }
    private void AutoScroll(DragEventArgs e) { var point = DragPreview.PointerOn(scroll,e.GetPosition(scroll)); if (point.Y < 40) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12); else if (point.Y > scroll.ActualHeight - 40) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12); }
    public static TextBlock Text(string value) { var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return text; }
    public static Button Button(string text, Action action, string? tip = null) { var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(2), MinHeight = 28 }; b.SetResourceReference(StyleProperty, "GhostButton"); b.Click += (_, e) => { e.Handled = true; action(); }; return b; }
}
