using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls;

public sealed partial class OverlayWindow
{
    private OverlayLayout layout = new();
    private FrameworkElement layoutContent = null!;
    private SnapBoard overlayBoard = null!;
    private OverlayLayout? beforeEditing;
    private bool IsLayoutEditing => overlayBoard?.Editing == true;
    private readonly Dictionary<string, FrameworkElement> blocks = new();
    private readonly TextBlock noteText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly Image customImage = new() { MaxHeight = 240, Stretch = Stretch.Uniform };
    private readonly TextBlock imageError = new() { Text = "Выберите блок, затем нажмите «Картинка»", TextWrapping = TextWrapping.Wrap };
    private string? loadedImage;
    private CanvasLayout Composition() => layout.Composition?.Copy() ?? new() { Blocks = layout.Blocks.Select(b => new CanvasBlock { Id = b.Id, Zone = b.Zone, Visible = b.Visible }).ToList() };
    private void RememberComposition()
    {
        layout.Composition = overlayBoard.Value;
        layout.Blocks = layout.Composition.Blocks.Select(b => new OverlayBlock { Id = b.Id, Zone = b.Zone, Visible = b.Visible }).ToList();
    }

    private void InitializeBlocks(StackPanel oldLeft, StackPanel oldRight, Border modeBox, Grid eqRow, Grid volRow, System.Windows.Controls.Primitives.UniformGrid recRow, ScrollViewer appsScroll)
    {
        oldLeft.Children.Clear(); oldRight.Children.Clear();
        FrameworkElement Group(string name, params UIElement[] children)
        {
            var group = new StackPanel(); if (name.Length > 0) group.Children.Add(Section(name, first: name is "ЗВУК" or "ЗАПИСЬ ЭКРАНА"));
            foreach (var child in children) group.Children.Add(child);
            return group;
        }
        blocks["music"] = nowPlaying;
        blocks["sound"] = Group("ЗВУК", modeBox, customTitle, customBox, eqRow);
        blocks["volume"] = Group("ГРОМКОСТЬ", volRow);
        blocks["voice"] = Group("", voiceCard);
        blocks["output"] = Group("КУДА ИДЁТ ЗВУК", outputButton, outputPopup);
        blocks["recording"] = Group("ЗАПИСЬ ЭКРАНА", recRow);
        blocks["timer"] = timerCard;
        blocks["apps"] = Group("ГРОМКОСТЬ ПРОГРАММ", appsScroll);
        noteText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        blocks["note"] = Group("МОЯ ЗАМЕТКА", noteText);
        var imageSurface = new Grid(); imageSurface.Children.Add(customImage); imageSurface.Children.Add(imageError);
        imageError.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        blocks["image"] = Group("МОЯ КАРТИНКА", imageSurface);
        var cards = OverlayLayout.Catalog.Select(c => new CanvasCard(c.Id, c.Name, blocks[c.Id], c.Zone, c.Visible)).ToDictionary(c => c.Id);
        cards["music"].Inside.Add(nowPlaying.LayoutButtons);
        cards["sound"].Inside.Add(new("Overlay.modes", "Режимы", (Panel)modeBox.Child, "Игры", "Музыка", "Фильмы"));
        cards["sound"].Inside.Add(new("Overlay.eq", "Эквалайзер", eqRow, "Включить эквалайзер", "Сравнить"));
        // Volume and its numeric readout stay one indivisible element.
        volRow.Children.Remove(volume); volRow.Children.Remove(volumeText);
        var volumeUnit = new Grid(); volumeUnit.ColumnDefinitions.Add(new()); volumeUnit.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(volume, 0); Grid.SetColumn(volumeText, 1); volumeUnit.Children.Add(volume); volumeUnit.Children.Add(volumeText);
        Grid.SetColumn(volumeUnit, 1); volRow.Children.Add(volumeUnit); volRow.ColumnDefinitions.RemoveAt(2); Grid.SetColumn(micButton, 2);
        cards["volume"].Inside.Add(new("Overlay.volume", "Громкость", volRow, "Звук вкл. / выкл.", "Уровень громкости", "Микрофон"));
        var voices = (Panel)voiceCard.Children[1]; voices.Width = double.NaN;
        cards["voice"].Inside.Add(new("Overlay.voices", "Голоса", voices, "Обычный", "Монстр", "Мульт", "Мужской", "Женский"));
        cards["recording"].Inside.Add(new("Overlay.capture", "Кнопки записи", recRow, "Повтор", "Запись", "Снимок", "Область"));
        var timerBody = (StackPanel)timerCard.Child;
        var timerRow = (StackPanel)timerBody.Children[1]; timerRow.Children.RemoveAt(1);
        cards["timer"].Inside.Add(new("Overlay.timer", "Таймер", CanvasParts.Wrap(timerRow), "Минуты", "Секунды", "Пуск / пауза", "Сбросить"));
        cards["note"].Options = NoteOptions(); cards["note"].InsideLabel="Текст заметки"; cards["image"].Options = ImageOptions(); cards["image"].InsideLabel="Картинка";
        overlayBoard = new SnapBoard(cards.Values, Composition());
        overlayBoard.ModeChanged += () => { RefreshTimerVisibility(); };
        overlayBoard.Finished += save => {
            if (save) { RememberComposition(); main.SaveOverlayLayout(layout); }
            else if (beforeEditing != null) ApplyLayout(beforeEditing);
            beforeEditing = null; RefreshTimerVisibility();
            if (hwnd != IntPtr.Zero) SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000);
        };
    }

    private void ApplyLayout(OverlayLayout value)
    {
        layout = value.Copy().Normalize();
        overlayBoard.Apply(Composition());
        ApplyAppearance();
    }
    private void ApplyAppearance()
    {
        footer.Visibility = layout.Footer ? Visibility.Visible : Visibility.Collapsed;
        noteText.Text = string.IsNullOrWhiteSpace(layout.Note) ? "Ваша заметка — выберите этот блок и нажмите «Текст заметки»" : layout.Note;
        if (loadedImage != layout.ImagePath)
        {
            loadedImage = layout.ImagePath; customImage.Source = null;
            try
            {
                if (!string.IsNullOrEmpty(loadedImage))
                {
                    using var stream = File.OpenRead(loadedImage);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1024; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); customImage.Source = bitmap;
                }
            }
            catch (Exception) { imageError.Text = "Картинка недоступна. Выберите другую в настройках оверлея."; }
            imageError.Visibility = customImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }
        layoutContent.LayoutTransform = new ScaleTransform(layout.Scale, layout.Scale);
        RefreshTimerVisibility(); ApplyNativeOpacity();
        if (IsVisible && hwnd != IntPtr.Zero)
        {
            double dpi = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
            Width = Math.Min(layout.Width * layout.Scale, Math.Max(1, (screenBounds.Width - 32) / dpi));
            UpdateLayout();
            if (!IsLayoutEditing) PositionOverlay();
            else if (GetWindowRect(hwnd, out var bounds))
            {
                // Keep the canvas still while editing, only constrain it when it outgrows the screen.
                int x = Math.Clamp(bounds.Left, screenBounds.Left, Math.Max(screenBounds.Left, screenBounds.Right - (bounds.Right - bounds.Left)));
                int y = Math.Clamp(bounds.Top, screenBounds.Top, Math.Max(screenBounds.Top, screenBounds.Bottom - (bounds.Bottom - bounds.Top)));
                SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint color, out byte alpha, out uint flags);
    private bool opacityQueued;
    private bool applyingOpacity;
    private void ApplyNativeOpacity()
    {
        ApplyNativeOpacityNow();
        QueueNativeOpacity();
    }
    private void QueueNativeOpacity()
    {
        if (opacityQueued) return; opacityQueued = true;
        // Reconcile after WPF layout, before input. ContextIdle can starve behind
        // background work, so it must never be the only path for a slider change.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
            opacityQueued = false;
            ApplyNativeOpacityNow();
        }));
    }
    private void ApplyNativeOpacityNow()
    {
        if (applyingOpacity || hwnd == IntPtr.Zero || (!IsVisible && !firstPresentationPending)) return;
        applyingOpacity = true;
        try {
            int style = GetWindowLong(hwnd, -20);
            if (firstPresentationPending || layout.Opacity < .999)
            {
                if((style & 0x00080000) == 0) SetWindowLong(hwnd, -20, style | 0x00080000);
                byte wanted=firstPresentationPending?(byte)0:(byte)Math.Round(layout.Opacity * 255);
                if(!GetLayeredWindowAttributes(hwnd,out _,out byte actual,out uint flags) || actual!=wanted || flags!=2)
                    SetLayeredWindowAttributes(hwnd, 0, wanted, 2);
            }
            else if ((style & 0x00080000) != 0) SetWindowLong(hwnd, -20, style & ~0x00080000);
        }
        finally { applyingOpacity = false; }
    }
    private void EditLayout()
    {
        if (IsLayoutEditing) { overlayBoard.Begin(); return; }
        outputPopup.IsOpen = clickerPopup.IsOpen = false;
        beforeEditing = layout.Copy();
        overlayBoard.Appearance = AppearanceOptions();
        // Recreate text options with the current values after a cancelled session.
        RefreshCustomOptions();
        if (hwnd != IntPtr.Zero) SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) & ~0x08000000);
        overlayBoard.Begin(); Activate();
    }
    internal bool HandleLayoutEscape()
    {
        if (!IsLayoutEditing || DragPreview.Active || OwnedWindows.Cast<Window>().Any(w => w.IsVisible && !overlayBoard.IsInspector(w))) return false;
        Dispatcher.BeginInvoke(new Action(() => { if (!IsLayoutEditing || overlayBoard.CloseSelectionList() || overlayBoard.CloseFlyouts()) return; if (overlayBoard.ActiveBlock != null) overlayBoard.Back(); else overlayBoard.Finish(false); }));
        return true;
    }
    private void ToggleLayoutTimer()
    {
        var block = layout.Blocks.First(x => x.Id == "timer");
        if (!block.Visible) { block.Visible = true; if (layout.Composition != null) layout.Composition.Blocks.First(x => x.Id == "timer").Visible = true; ApplyLayout(layout); main.SaveOverlayLayout(layout); if (!main.CountdownTimer.IsShown) main.CountdownTimer.ToggleVisibility(); }
        else main.CountdownTimer.ToggleVisibility();
    }
    private void RememberLayoutPosition()
    {
        if (!layout.RememberPosition || !GetWindowRect(hwnd, out var rect)) return;
        screenBounds = PointerScreen();
        layout.X = Math.Clamp((rect.Left - screenBounds.Left) / (double)Math.Max(1, screenBounds.Width - (rect.Right - rect.Left)), 0, 1);
        layout.Y = Math.Clamp((rect.Top - screenBounds.Top) / (double)Math.Max(1, screenBounds.Height - (rect.Bottom - rect.Top)), 0, 1);
        if (!IsLayoutEditing) main.SaveOverlayLayout(layout);
    }
}
