using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Services;

namespace GraphiteCompanion;
public partial class MainWindow
{
    private Dictionary<string,RadioButton>? navigationButtons;
    private Window? navigationEditor;
    private void InitNavigationLayout()
    {
        navigationButtons=SidebarNavigation.Children.OfType<RadioButton>().ToDictionary(b=>(string)b.Tag);
        var edit=SnapBoard.Button("✎  Меню",()=>EditNavigation(),"Порядок, названия и видимость разделов");
        edit.Margin=new Thickness(6,8,6,0);DockPanel.SetDock(edit,Dock.Bottom);SidebarHost.Children.Insert(1,edit);
        if(settings.Navigation!=null)ApplyNavigationLayout();
    }
    private string NavigationName(string id,string fallback) => NavigationLayout.Normalize(settings.Navigation).FirstOrDefault(n=>n.Id==id)?.Name??fallback;
    private void ApplyNavigationLayout()
    {
        if(navigationButtons==null)return;
        SidebarNavigation.Children.Clear();
        foreach(var item in NavigationLayout.Normalize(settings.Navigation)) {
            var button=navigationButtons[item.Id];button.Visibility=item.Visible?Visibility.Visible:Visibility.Collapsed;
            if(button.Content is StackPanel panel && panel.Children.Count>1 && panel.Children[1] is TextBlock title) {title.Text=item.Name;title.TextTrimming=TextTrimming.CharacterEllipsis;title.MaxWidth=136;title.Visibility=settings.SidebarCollapsed?Visibility.Hidden:Visibility.Visible;}
            button.ToolTip=item.Name;System.Windows.Automation.AutomationProperties.SetName(button,item.Name);SidebarNavigation.Children.Add(button);
        }
        PageTitle.Text=NavigationName(layoutPage,PageTitle.Text);
    }
    private void EditNavigation()
    {
        if(navigationEditor!=null){navigationEditor.Activate();return;}
        var draft=NavigationLayout.Normalize(settings.Navigation);
        var cards=new List<CanvasCard>();
        foreach(var item in draft) {
            var label=SnapBoard.Text(item.Name);label.Margin=new Thickness(14,12,14,12);
            var box=new Border{Child=label,CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1)};box.SetResourceReference(Border.BackgroundProperty,"CardBrush");box.SetResourceReference(Border.BorderBrushProperty,"CardBorderBrush");
            var card=new CanvasCard(item.Id,item.Name,box,1,item.Visible){Resizable=false,InsideLabel="Название"};
            var options=new StackPanel{Width=260};options.Children.Add(SnapBoard.Text("Название в меню"));
            var name=new TextBox{Text=item.Name,MaxLength=32,Margin=new Thickness(0,8,0,8)};name.TextChanged+=(_,_)=>{item.Name=name.Text;label.Text=name.Text;};options.Children.Add(name);
            options.Children.Add(SnapBoard.Button("Исходное название",()=>name.Text=NavigationLayout.Catalog.First(c=>c.Id==item.Id).Name));
            options.Children.Add(SnapBoard.Text("Раздел: "+NavigationLayout.Catalog.First(c=>c.Id==item.Id).Name));card.Options=options;cards.Add(card);
        }
        var board=new SnapBoard(cards){SingleColumn=true};
        var root=new DockPanel{Margin=new Thickness(18)};
        var hint=SnapBoard.Text("Перетащите пункт, чтобы изменить порядок. Нажмите пункт: «Название» — переименовать, × — скрыть.");hint.Margin=new Thickness(0,0,0,16);DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
        var actions=board.EmbedEditorTools();actions.Margin=new Thickness(0,12,0,0);DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);root.Children.Add(board);
        var host=Window.GetWindow(SidebarHost);
        var window=new Window{Title="Настроить меню SoundShot",Width=610,Height=690,MinWidth=500,MinHeight=460,Owner=host,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=root};
        window.SetResourceReference(BackgroundProperty,"BgBrush");window.SetResourceReference(ForegroundProperty,"TextBrush");
        bool closing=false;
        board.Finished+=save=>{if(save){settings.Navigation=NavigationLayout.Normalize(board.Value.Blocks.Select(b=>new NavigationItem{Id=b.Id,Visible=b.Visible,Name=draft.First(x=>x.Id==b.Id).Name}));ApplyNavigationLayout();ScheduleSave();}if(!closing)window.Close();};
        window.Closing+=(_,_)=>{closing=true;if(board.Editing)board.Finish(false);};
        navigationEditor=window;window.Closed+=(_,_)=>{navigationEditor=null;if(host is {IsVisible:true}&&host.WindowState!=WindowState.Minimized)host.Activate();};window.Show();board.Begin();
    }
}
