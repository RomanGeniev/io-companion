using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Controls;

namespace GraphiteCompanion;

public partial class MainWindow
{
    private void SyncEffectLayout()
    {
        if(layoutBoards.TryGetValue("Effects",out var board)) {
            bool visible=board.Value.Blocks.FirstOrDefault(b=>b.Id=="Effects.device")?.Visible??true;
            var cards=outputCards.Select((card,i)=>{
                if(card.Parent is Panel parent)parent.Children.Remove(card);
                return new CanvasCard("Effects.effect."+card.Definition.Format+"."+card.Definition.EnablePid,card.Definition.Title,card,i%2+1,visible);
            }).ToArray();
            board.ReplaceCards("Effects.effect.",cards);
        }
        if(layoutBoards.TryGetValue("Mic",out var micBoard)) {
            bool visible=micBoard.Value.Blocks.FirstOrDefault(b=>b.Id=="Mic.effects")?.Visible??true;
            var cards=micCards.Select((card,i)=>{
                if(card.Parent is Panel parent)parent.Children.Remove(card);
                return new CanvasCard("Mic.effect."+card.Definition.Format+"."+card.Definition.EnablePid,card.Definition.Title,card,i%2+1,visible);
            }).ToArray();
            micBoard.ReplaceCards("Mic.effect.",cards);
        }
    }
    private void SyncMonitorLayout()
    {
        if(!layoutBoards.TryGetValue("Record",out var board))return;
        bool visible=board.Value.Blocks.FirstOrDefault(b=>b.Id=="Record.capture")?.Visible??true;
        var cards=recordingCards.Select((pair,i)=>{
            if(pair.Value.Parent is Panel parent)parent.Children.Remove(pair.Value);
            string id="Record.monitor."+pair.Key;
            return new CanvasCard(id,System.Windows.Automation.AutomationProperties.GetName(pair.Value),pair.Value,i%2+1,visible);
        }).ToArray();
        board.ReplaceCards("Record.monitor.",cards.Length>0?cards:new[]{new CanvasCard("Record.monitor.empty","Мониторы",SnapBoard.Text("Подключённые мониторы не найдены."),0)});
    }
}
