using System;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Services;
using Drawing = System.Drawing;

namespace GraphiteCompanion
{
    public partial class MainWindow
    {
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
        private RegionSelection? regionSelection;
        private void RegionScreenshot_Click(object sender, RoutedEventArgs e) => _ = TakeRegionScreenshot();
        public async Task TakeRegionScreenshot()
        {
            if (shotBusy || recordClosed || shutdownStarted || regionSelection != null || hotkeysEditor is { IsVisible: true } || IsAutoClickerKeyRecording) return;
            shotBusy = true;
            bool restore = IsVisible && WindowState != WindowState.Minimized;
            var selector = new RegionSelection(); regionSelection = selector;
            StopAutoClicker(); hotkeys.Clear();
            overlay?.Close(); flyout?.Close(); clickerIndicator?.Hide();
            if (restore) Hide();
            try
            {
                var monitors = ReadMonitors();
                var selected = await selector.Show(monitors?.Select(m => m.Bounds) ?? Array.Empty<Drawing.Rectangle>());
                if (selected is not { } area || shutdownStarted) return;
                if ((long)area.Width * area.Height > 64_000_000) throw new InvalidOperationException("Область слишком велика. Выделите меньшую часть экрана.");
                await Task.Delay(80); // Let the compositor remove the selection surfaces.
                if (shutdownStarted) return;
                DwmFlush();
                using var bitmap = new Drawing.Bitmap(area.Width, area.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
                var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
                try
                {
                    using var graphics = Drawing.Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen(area.Location, Drawing.Point.Empty, area.Size, Drawing.CopyPixelOperation.SourceCopy);
                }
                finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
                var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, area.Width, area.Height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
                BitmapSource source;
                try { source = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * data.Height, data.Stride); source.Freeze(); }
                finally { bitmap.UnlockBits(data); }
                string? savedFile = null;
                string? saveError = null;
                try
                {
                    string monitorFolder = monitors?.FirstOrDefault(m => m.Bounds.Contains(area))?.Label ?? "Несколько мониторов";
                    string saveFolder = Path.Combine(RecFolder, monitorFolder, "Рабочий стол");
                    savedFile = await TrackRecordingSave(Task.Run(() => GalleryService.SaveRegionPng(source, saveFolder, DateTime.Now)));
                }
                catch (Exception ex) { SettingsStore.LogError(ex); saveError = ex.Message; }
                if (shutdownStarted) return;
                bool copied = false;
                for (int attempt = 0; ; attempt++)
                {
                    try { Clipboard.SetImage(source); copied = true; break; }
                    catch (COMException) when (attempt < 4) { await Task.Delay(80); }
                    catch (COMException ex) { SettingsStore.LogError(ex); break; }
                }
                if (savedFile != null) NotifyChange(copied ? "Снимок сохранён и скопирован" : "Снимок сохранён", "\uE7A7", copied ? $"{area.Width} × {area.Height} · доступен в галерее и по Ctrl + V" : "Доступен в галерее. Буфер обмена занят другой программой.");
                else if (copied) NotifyChange("Снимок только в буфере", "\uE7A7", "Не удалось сохранить файл: " + saveError);
                else NotifyChange("Не удалось сохранить снимок", "\uE7A7", saveError ?? "Буфер обмена недоступен");
            }
            catch (Exception ex) { SettingsStore.LogError(ex); NotifyChange("Не удалось скопировать снимок", "", ex.Message); }
            finally
            {
                selector.Cancel(); regionSelection = null; shotBusy = false;
                if (!shutdownStarted) { ApplyHotkeys(); if (restore) Show(); UpdateRecStatus(); }
            }
        }
    }
}
