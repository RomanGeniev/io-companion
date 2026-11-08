using System;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // Hidden prank: when the flag file sits next to the exe, a phone-style alarm starts ringing
    // 3-10 minutes after launch. Triple-click on the logo stops it (it also stops by itself after
    // 2 minutes and when the app exits). Triple-click while it is silent turns the prank mode on/off.
    public partial class MainWindow
    {
        private static readonly string PrankFlagFile = Path.Combine(AppContext.BaseDirectory, "ultra3d.cfg");
        private readonly AlarmPlayer alarm = new();
        private DispatcherTimer? prankTimer;
        private DispatcherTimer? alarmCutoff;

        private static bool PrankEnabled => File.Exists(PrankFlagFile);

        private void InitPrank()
        {
            if (PrankEnabled) SchedulePrank(TimeSpan.FromSeconds(new Random().Next(180, 600)));
        }

        private void SchedulePrank(TimeSpan delay)
        {
            prankTimer?.Stop();
            prankTimer = new DispatcherTimer { Interval = delay };
            prankTimer.Tick += (_, _) => { prankTimer.Stop(); StartAlarm(); };
            prankTimer.Start();
        }

        private void StartAlarm()
        {
            try
            {
                float volume = session?.HasVolume == true ? session.GetVolume() : 0.5f;
                alarm.Start(session?.Info.Id, volume);
                alarmCutoff?.Stop();
                alarmCutoff = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                alarmCutoff.Tick += (_, _) => StopAlarm();
                alarmCutoff.Start();
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
            }
        }

        private void StopAlarm()
        {
            alarmCutoff?.Stop();
            alarm.Stop();
        }

        private void StopPrank()
        {
            prankTimer?.Stop();
            StopAlarm();
        }

        private void BrandLogo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 3) return;

            if (alarm.Playing)
            {
                StopAlarm(); // the "off switch" - quietly, no hints
                return;
            }

            try
            {
                if (PrankEnabled) File.Delete(PrankFlagFile);
                else File.WriteAllText(PrankFlagFile, "1");
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                return;
            }

            osd ??= new Controls.OsdWindow();
            if (PrankEnabled)
            {
                SchedulePrank(TimeSpan.FromSeconds(10)); // quick check for the owner
                osd.ShowMessage("", "Розыгрыш включён", "Будильник через 10 секунд · выключение — 3 клика по логотипу");
            }
            else
            {
                StopPrank();
                osd.ShowMessage("", "Розыгрыш выключен");
            }
        }
    }
}
