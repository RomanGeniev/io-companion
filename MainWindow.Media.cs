using System;
using System.Threading.Tasks;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // Music hotkeys: next / previous / pause for whatever is playing, with a hint showing the track.
    public partial class MainWindow
    {
        private async Task MediaCommand(Func<Task> command, bool playPause)
        {
            await command();
            if (overlay is { IsVisible: true }) return;   // the mini player there already shows the change
            await Task.Delay(playPause ? 350 : 900);      // let the app switch the track first
            var now = await MediaService.GetAsync();
            if (now == null || (now.Title.Length == 0 && now.Artist.Length == 0)) return;
            string title = now.Title.Length > 0 ? now.Title : "Без названия";
            if (playPause)
                NotifyChange(now.Playing ? "Играет" : "Пауза", now.Playing ? "" : "", title);
            else
                NotifyChange(title, "", now.Artist.Length > 0 ? now.Artist : now.App);
        }
    }
}
