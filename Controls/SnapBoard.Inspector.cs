using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GraphiteCompanion.Services;
namespace GraphiteCompanion.Controls;

public sealed partial class SnapBoard
{
    private Window? inspectorWindow;
    private bool closingInspector,inspectorAppearance,embeddedInspector;
    private string inspectorDetail="";
    private bool UsesInspector=>true;
    private readonly Stack<CanvasLayout> undo=new();
    private CanvasLayout? operationBefore;
    internal bool IsInspector(Window window)=>ReferenceEquals(window,inspectorWindow);
    // Standalone editors already own a window; keep their actions in that same window.
    public FrameworkElement EmbedEditorTools()
    {
        if(Editing)throw new InvalidOperationException("Choose embedded editor tools before editing.");
        embeddedInspector=true;toolsLayer.Children.Remove(toolbarSurface);
        return toolbarSurface;
    }
    internal bool CloseSelectionList()=>false;
    private void BeginOperation(){if(Editing&&operationBefore==null)operationBefore=state.Copy();}
    private void CancelOperation(){var before=operationBefore;operationBefore=null;if(before!=null)Apply(before);}
    private void EndOperation()
    {
        if(operationBefore!=null&&JsonSerializer.Serialize(operationBefore)!=JsonSerializer.Serialize(state))undo.Push(operationBefore);
        operationBefore=null;if(Editing)RenderInspector();
    }
    private void Undo(){if(!Editing||undo.Count==0||DragPreview.Active)return;operationBefore=null;Apply(undo.Pop());}
    private void MoveBlockByKeyboard(string id,int delta)
    {
        BeginOperation();int from=state.Blocks.FindIndex(b=>b.Id==id),to=Math.Clamp(from+delta,0,state.Blocks.Count-1);
        var block=state.Blocks[from];state.Blocks.RemoveAt(from);state.Blocks.Insert(to,block);ArrangeBlocks();EndOperation();tiles[id].Focus();
    }
    private void OpenInspector()
    {
        if(embeddedInspector)return;
        var host=Window.GetWindow(this);if(host==null||inspectorWindow!=null)return;
        toolsLayer.Children.Remove(toolbarSurface);
        var window=new Window{Title=Appearance!=null?"Изменение вида оверлея":"Изменение вида SoundShot",Owner=host,Width=560,SizeToContent=SizeToContent.Height,MaxHeight=460,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=host.Topmost,ShowActivated=host.ShowActivated&&host.Left>-10000,Left=host.Left,Top=host.Top};
        window.SetResourceReference(Window.BackgroundProperty,"CardBrush");window.SetResourceReference(Window.ForegroundProperty,"TextBrush");
        var frame=new Border{BorderThickness=new Thickness(1),Padding=new Thickness(10),CornerRadius=new CornerRadius(10)};frame.SetResourceReference(Border.BorderBrushProperty,"CardBorderBrush");frame.SetResourceReference(Border.BackgroundProperty,"CardBrush");
        var dock=new DockPanel();frame.Child=dock;window.Content=frame;
        var header=new DockPanel{Background=Brushes.Transparent,Margin=new Thickness(2,0,0,8)};
        var close=Button("×",()=>Finish(false),"Закрыть и отменить изменения");DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var title=Text("⠿  "+window.Title);title.FontWeight=FontWeights.SemiBold;title.Cursor=Cursors.SizeAll;title.ToolTip="Перетащите панель за заголовок";title.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed){window.DragMove();e.Handled=true;}};header.Children.Add(title);DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        toolbarSurface.Padding=new Thickness();toolbarSurface.BorderThickness=new Thickness();dock.Children.Add(new ScrollViewer{Content=toolbarSurface,MaxHeight=380,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        window.PreviewKeyDown+=OwnerKeyDown;window.Closing+=(_,_)=>{if(closingInspector)return;closingInspector=true;try{Finish(false);}finally{closingInspector=false;}};inspectorWindow=window;
        window.Loaded+=(_,_)=>BlockDrag.PlaceBeside(window,host);window.Show();
    }
    private void CloseInspector()
    {
        if(inspectorWindow==null)return;var window=inspectorWindow;inspectorWindow=null;
        if(window.Content is Border frame&&frame.Child is DockPanel dock)foreach(var sc in dock.Children.OfType<ScrollViewer>())sc.Content=null;
        if(!closingInspector){closingInspector=true;try{window.Close();}finally{closingInspector=false;}}
        toolbarSurface.Padding=new Thickness(8);toolbarSurface.BorderThickness=new Thickness(1);toolsLayer.Children.Add(toolbarSurface);
    }
    private void RenderInspector()
    {
        toolbar.Children.Clear();toolbarSurface.Visibility=Editing?Visibility.Visible:Visibility.Collapsed;root.MinHeight=64;if(!Editing)return;
        if(!embeddedInspector){var hint=Text("Блок: ⠿ — двигать · угол — масштаб · линии помогают выровнять\nВнутри: переставляйте элементы · × — скрыть · ниже — вернуть");hint.FontSize=12;hint.Margin=new Thickness(3,0,3,8);hint.SetResourceReference(TextBlock.ForegroundProperty,"SubTextBrush");toolbar.Children.Add(hint);}
        var actions=new WrapPanel();var back=Button("↶ Отменить шаг",Undo,"Отменить последнее изменение · Ctrl+Z");back.IsEnabled=undo.Count>0;actions.Children.Add(back);
        var done=Button("Готово",()=>Finish(true),"Сохранить все изменения вида");done.SetResourceReference(StyleProperty,"AccentButton");actions.Children.Add(done);actions.Children.Add(Button("Отмена",()=>Finish(false),"Вернуть вид до начала редактирования"));
        if(Appearance!=null)actions.Children.Add(Button("Прозрачность и вид",()=>{inspectorAppearance=!inspectorAppearance;inspectorDetail="";RenderInspector();}));
        if(selectedElement?.HasOptions==true)actions.Children.Add(selectedElement.ContextActions());toolbar.Children.Add(actions);
        var hidden=new WrapPanel{Margin=new Thickness(3,8,3,0)};hidden.Children.Add(Text("Скрытое: "));int count=0;
        foreach(var block in state.Blocks.Where(b=>!b.Visible&&tiles.ContainsKey(b.Id)))
        {
            var card=cards.First(c=>c.Id==block.Id);count++;hidden.Children.Add(Button("＋ "+card.Name,()=>{BeginOperation();block.Visible=true;Render();EndOperation();tiles[block.Id].BringIntoView();}));
        }
        foreach(var card in cards.Where(c=>state.Blocks.Any(b=>b.Id==c.Id&&b.Visible)))foreach(var group in card.AllGroups)foreach(var entry in group.Snapshot().Where(x=>!x.Visible&&group.HasItem(x.Id)))
        {
            var id=entry.Id;count++;hidden.Children.Add(Button("＋ "+card.Name+" · "+group.NameFor(id),()=>{BeginOperation();var values=group.Snapshot();values.First(v=>v.Id==id).Visible=true;group.Apply(values);state.Elements[group.Id]=group.Snapshot();RenderInspector();EndOperation();}));
        }
        if(count==0){var none=Text("нет скрытых элементов");none.SetResourceReference(TextBlock.ForegroundProperty,"SubTextBrush");hidden.Children.Add(none);}toolbar.Children.Add(hidden);
        if(inspectorAppearance&&Appearance!=null){DetachOption(Appearance);toolbar.Children.Add(Appearance);}
        if(inspectorDetail=="options"&&options.Children.Count>0){DetachOption(options);toolbar.Children.Add(options);}
    }
    private static void DetachOption(FrameworkElement element){if(element.Parent is Panel p)p.Children.Remove(element);else if(element.Parent is ContentControl c)c.Content=null;}
}
