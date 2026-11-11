using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;
using DrawingPoint = System.Drawing.Point;
using Forms = System.Windows.Forms;

namespace GraphiteCompanion.Controls
{
    /// <summary>
    /// Panel opened by Win+G over desktop-composed games: shows two columns - sound on the left
    /// (mode, equalizer, volume, microphone, output device), recording and per-program volume on the right.
    /// Drag the header to move it; Esc, the hotkey or a click outside closes it.
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        private readonly MainWindow main;
        private readonly StackPanel apps = new();
        private readonly Button outputButton = new();
        private readonly Popup outputPopup = new() { StaysOpen = true, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
        private readonly StackPanel outputItems = new();
        private readonly Button replayButton = new(), recordButton = new(), shotButton = new();
        private readonly TextBlock title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock subtitle = new() { FontSize = 12 };
        private readonly RadioButton[] modes = new RadioButton[3];
        private readonly CheckBox eqSwitch = new() { Content = "Эквалайзер" };
        private readonly Slider volume = new();
        private readonly TextBlock volumeText = new() { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        private readonly ToggleButton mute = new();
        private readonly Button micButton = new();
        private readonly Button compare = new();
        private readonly TextBlock footer = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        private readonly Border dragArea;
        private readonly NowPlayingCard nowPlaying;
        private IntPtr hwnd;
        private System.Drawing.Rectangle screenBounds;
        private int fullscreenState;
        private DrawingPoint? dragOffset;
        private DrawingPoint lastDragPointer;
        private DrawingPoint pendingDragPointer;
        private bool dragMovePosted, endingDrag;
        private int dragReleaseMessage;
        private const int DragMoveMessage = 0x8000 + 42;
        internal bool IsDragging => dragOffset != null;
        private bool movedByUser;
        private MouseHookProc? mouseHookProc;
        private IntPtr mouseHook;
        private bool loading;
        private readonly WrapPanel customPanel = new();
        private readonly Border customBox = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 6, 0, 0) };
        private readonly TextBlock customTitle = new() { Text = "МОИ ПРЕСЕТЫ", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) };

        private static T R<T>(string key) => (T)Application.Current.FindResource(key);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPhysicalPoint(DrawingPoint point);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        private delegate IntPtr MouseHookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int id, MouseHookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        public OverlayWindow(MainWindow main)
        {
            this.main = main;
            HelpTips.Set(eqSwitch, "Включает или отключает эквалайзер выбранного устройства. Настройки полос сохраняются; остальные эффекты управляются отдельно.");
            HelpTips.Set(volume, "Общая громкость выбранного устройства вывода. Изменение применяется сразу и также отражается в Windows.");
            HelpTips.Set(compare, "Удерживайте, чтобы временно услышать звук без эквалайзера и эффектов. Отпустите, чтобы вернуть обработку.");
            HelpTips.Set(replayButton, "Сохранить уже накопленные последние минуты. Длина ролика зависит от настройки и того, сколько времени успел работать буфер.");
            HelpTips.Set(recordButton, "Начать запись с текущего момента или остановить и сохранить начатую запись. Это не сохранение прошлого из буфера.");
            HelpTips.Set(shotButton, "Сохранить один кадр выбранного монитора как PNG в папку записей.");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;          // never steal the foreground: a fullscreen game stays up
            // Use a normal redirected HWND. A transparent, screen-sized layered window can
            // keep receiving mouse input while its pixels are absent above a fullscreen swap chain.
            SetResourceReference(BackgroundProperty, "CardBrush");
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "TextBrush");
            SourceInitialized += (_, _) =>
            {
                hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                // Apply before the first Show; even the first mouse click must leave the game active.
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000); // WS_EX_NOACTIVATE
                var source = HwndSource.FromHwnd(hwnd);
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                source.AddHook(WindowProc);
                int roundCorners = 2;
                DwmSetWindowAttribute(hwnd, 33, ref roundCorners, sizeof(int));
            };
            ContentRendered += (_, _) => { if (!movedByUser) PositionOverlay(); LogPresentation("rendered"); };

            // ================= header: what is playing now + keyboard / close
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(title);
            titles.Children.Add(subtitle);
            var openApp = new Button
            {
                ToolTip = "Открыть SoundShot", Content = Glyph("\uE8A7", 15),
                Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
            };
            openApp.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(openApp, "Открыть SoundShot");
            openApp.Click += (_, _) => { Close(); main.ShowFromTray(); };
            var keys = new Button { ToolTip = "Изменить горячие клавиши", Content = Glyph("", 15),
                Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand };
            keys.SetResourceReference(StyleProperty, "GhostButton");
            keys.Click += (_, _) => { Close(); main.OpenHotkeysWindow(); };
            var close = new Button { ToolTip = "Закрыть (Esc)", Content = Glyph("", 15), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
            close.SetResourceReference(StyleProperty, "GhostButton");
            close.Click += (_, _) => Close();
            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerButtons.Children.Add(openApp);
            headerButtons.Children.Add(keys);
            headerButtons.Children.Add(close);
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(titles);
            Grid.SetColumn(headerButtons, 1);
            header.Children.Add(headerButtons);
            dragArea = new Border
            {
                Child = header, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
                Padding = new Thickness(30, 24, 30, 0), ToolTip = "Потяните, чтобы переместить оверлей",
            };
            dragArea.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (openApp.IsMouseOver || keys.IsMouseOver || close.IsMouseOver) return;
                e.Handled = StartDragging(Forms.Cursor.Position);
            };
            dragArea.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (dragOffset == null) return;
                QueueDragMove(Forms.Cursor.Position, released: true);
                e.Handled = true;
            };
            dragArea.PreviewMouseMove += (_, e) =>
            {
                if (dragOffset == null) return;
                QueueDragMove(Forms.Cursor.Position);
                e.Handled = true;
            };
            dragArea.LostMouseCapture += (_, _) =>
            {
                if (dragOffset != null) MoveToPointer(endingDrag ? pendingDragPointer : Forms.Cursor.Position);
                StopDragging();
            };

            // ================= left column: sound
            // ---- mode
            var modeGrid = new UniformGrid { Columns = 3 };
            string[] glyphs = { "", "", "" };
            foreach (ListeningMode m in Enum.GetValues<ListeningMode>())
            {
                var mode = m;
                var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock { Text = glyphs[(int)m], FontFamily = R<FontFamily>("IconFont"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = FactoryPresets.Name(m), Margin = new Thickness(8, 0, 0, 0), FontSize = 14 });
                var rb = new RadioButton { GroupName = "OverlayMode", Content = content, Padding = new Thickness(0, 8, 0, 8) };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Click += (_, _) => { main.SelectModeFromOutside(mode); Refresh(); };
                modes[(int)m] = rb;
                modeGrid.Children.Add(rb);
            }
            var modeBox = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Child = modeGrid };
            modeBox.SetResourceReference(Border.BackgroundProperty, "InsetBrush");

            // ---- equalizer + compare
            eqSwitch.SetResourceReference(StyleProperty, "Switch");
            eqSwitch.Click += (_, _) => { main.ToggleEqFromOutside(); Refresh(); };
            eqSwitch.VerticalAlignment = VerticalAlignment.Center;
            compare.SetResourceReference(StyleProperty, "GhostButton");
            compare.PreviewMouseLeftButtonDown += (_, _) => { main.CompareOn(); compare.CaptureMouse(); };
            compare.PreviewMouseLeftButtonUp += (_, _) => { compare.ReleaseMouseCapture(); main.CompareOff(); };
            var eqRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            eqRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            eqRow.ColumnDefinitions.Add(new ColumnDefinition());
            eqRow.Children.Add(eqSwitch);
            compare.Padding = new Thickness(12, 6, 12, 6);
            compare.Margin = new Thickness(16, 0, 0, 0);
            compare.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(compare, 1);
            eqRow.Children.Add(compare);

            // ---- volume + microphone
            var volRow = new Grid();
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            volRow.ColumnDefinitions.Add(new ColumnDefinition());
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mute.SetResourceReference(StyleProperty, "IconToggle");
            mute.ToolTip = "Выключить / включить звук";
            mute.Content = new TextBlock { FontFamily = R<FontFamily>("IconFont"), FontSize = 15 };
            mute.Click += (_, _) => { main.ToggleMuteFromOutside(); Refresh(); };
            volRow.Children.Add(mute);
            volume.SetResourceReference(StyleProperty, "VolumeSlider");
            volume.Margin = new Thickness(12, 0, 12, 0);
            volume.VerticalAlignment = VerticalAlignment.Center;
            volume.ValueChanged += (_, e) =>
            {
                volumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
                if (!loading) main.SetVolumeFromOutside((int)Math.Round(e.NewValue));
            };
            Grid.SetColumn(volume, 1);
            volRow.Children.Add(volume);
            Grid.SetColumn(volumeText, 2);
            volRow.Children.Add(volumeText);

            micButton.SetResourceReference(StyleProperty, "GhostButton");
            micButton.Margin = new Thickness(0, 10, 0, 0);
            micButton.Padding = new Thickness(12, 6, 12, 6);
            micButton.HorizontalAlignment = HorizontalAlignment.Left;
            micButton.Click += (_, _) => { main.ToggleMicMute(fromHotkey: false); Refresh(); };

            customTitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            customBox.Child = customPanel;
            customBox.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
            var left = new StackPanel();
            left.Children.Add(Section("ЗВУК", first: true));
            left.Children.Add(modeBox);
            left.Children.Add(customTitle);
            left.Children.Add(customBox);
            left.Children.Add(eqRow);
            left.Children.Add(Section("ГРОМКОСТЬ"));
            left.Children.Add(volRow);
            left.Children.Add(micButton);
            left.Children.Add(Section("КУДА ИДЁТ ЗВУК"));
            outputButton.SetResourceReference(StyleProperty, "GhostButton");
            outputButton.Padding = new Thickness(14, 10, 14, 10);
            outputButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            outputButton.Click += (_, _) =>
            {
                if (outputPopup.IsOpen) { outputPopup.IsOpen = false; return; }
                ((Border)outputPopup.Child).Width = outputButton.ActualWidth;
                outputPopup.IsOpen = true;
            };
            var popupBox = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 6, 6, 2), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 12), Child = outputItems,
                                        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.5 } };
            popupBox.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            popupBox.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            outputPopup.Child = popupBox;
            outputPopup.PlacementTarget = outputButton;
            left.Children.Add(outputButton);
            left.Children.Add(outputPopup);

            // ================= right column: recording + programs
            var recRow = new UniformGrid { Columns = 3 };
            foreach (var b in new[] { replayButton, recordButton, shotButton })
            {
                b.SetResourceReference(StyleProperty, "GhostButton");
                b.Padding = new Thickness(6, 10, 6, 10);
                b.Margin = new Thickness(b == replayButton ? 0 : 3, 0, b == shotButton ? 0 : 3, 0);
                recRow.Children.Add(b);
            }
            replayButton.Click += async (_, _) => { Close(); await main.SaveReplay(); };
            recordButton.Click += async (_, _) => { Close(); await main.ToggleRecording(); };
            shotButton.Click += async (_, _) => { Close(); await main.TakeScreenshot(); };

            var appsScroll = new ScrollViewer { Content = apps, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var right = new StackPanel();
            right.Children.Add(Section("ЗАПИСЬ ЭКРАНА", first: true));
            right.Children.Add(recRow);
            right.Children.Add(Section("ГРОМКОСТЬ ПРОГРАММ"));
            right.Children.Add(appsScroll);

            // ================= assemble
            var columns = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(left);
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);

            footer.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");

            var body = new StackPanel { Margin = new Thickness(30, 0, 30, 18) };
            nowPlaying = new NowPlayingCard(main);
            body.Children.Add(nowPlaying);
            body.Children.Add(columns);
            body.Children.Add(footer);
            var content2 = new StackPanel();
            content2.Children.Add(dragArea);
            content2.Children.Add(body);

            var panel = new Border
            {
                CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
                Child = content2,
            };
            panel.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            panel.MouseLeftButtonDown += (_, _) => outputPopup.IsOpen = false;
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
            // The global keyboard hook handles Escape while the game keeps keyboard focus.
            Closed += (_, _) =>
            {
                StopDragging();
                nowPlaying.Dispose();
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
                mouseHookProc = null;
                outputPopup.IsOpen = false;
                main.CompareOff();
            };
        }

        private TextBlock Section(string text, bool first = false)
        {
            var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 14 : 22, 0, 8) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            return t;
        }

        private static TextBlock Glyph(string glyph, double size = 14) =>
            new() { Text = glyph, FontFamily = R<FontFamily>("IconFont"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Icon + title, with the hotkey as a small line under it.</summary>
        private StackPanel Labeled(string glyph, string text, string? hotkey, bool vertical)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            title.Children.Add(Glyph(glyph));
            title.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (hotkey == null) return title;
            var sp = new StackPanel();
            sp.Children.Add(title);
            var hint = new TextBlock { Text = hotkey, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            sp.Children.Add(hint);
            return sp;
        }

        /// <summary>Shows a non-layered panel on the monitor under the mouse cursor,
        /// without taking the foreground. Clicks still reach the panel
        /// (WM_MOUSEACTIVATE → MA_NOACTIVATE); the global keyboard hook handles Escape.</summary>
        public void Open()
        {
            SHQueryUserNotificationState(out fullscreenState);
            Refresh();
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            screenBounds = screen.Bounds;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // Create the HWND on the target monitor before measuring the panel at that monitor's DPI.
            new WindowInteropHelper(this).EnsureHandle();
            SetWindowPos(hwnd, new IntPtr(-1), screenBounds.Left + 16, screenBounds.Top + 16, 200, 200, 0x0010);
            double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
            Width = Math.Min(960, Math.Max(1, (screenBounds.Width - 32) / scale));
            MaxHeight = Math.Max(1, (screenBounds.Height - 32) / scale);
            Opacity = 1;
            Show();
            UpdateLayout();
            PositionOverlay();
            mouseHookProc = OnMouseHook;
            mouseHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, mouseHookProc, GetModuleHandle(null), 0);
            if (mouseHook == IntPtr.Zero)
                SettingsStore.LogError(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Overlay mouse listener"));
            LogPresentation("opened");
        }

        private bool StartDragging(DrawingPoint pointer)
        {
            if (!IsVisible || !GetWindowRect(hwnd, out var rect)) return false;
            dragOffset = new DrawingPoint(pointer.X - rect.Left, pointer.Y - rect.Top);
            lastDragPointer = pointer;
            pendingDragPointer = pointer;
            dragReleaseMessage = Forms.SystemInformation.MouseButtonsSwapped ? 0x0205 : 0x0202;
            endingDrag = false;
            movedByUser = true;
            outputPopup.IsOpen = false;
            nowPlaying.SetPanelMoving(true);
            if (!dragArea.CaptureMouse() && mouseHook == IntPtr.Zero)
            {
                StopDragging();
                return false;
            }
            return true;
        }

        private void QueueDragMove(DrawingPoint pointer, bool released = false)
        {
            if (dragOffset == null || endingDrag) return;
            pendingDragPointer = pointer;
            endingDrag = released;
            if (dragMovePosted) return;
            // One native message consumes the latest pointer position. Moving the HWND
            // never waits for the next WPF render, even with software rendering.
            dragMovePosted = PostMessageW(hwnd, DragMoveMessage, IntPtr.Zero, IntPtr.Zero);
            if (!dragMovePosted)
            {
                MoveToPointer(pointer);
                if (endingDrag) StopDragging();
            }
        }

        private void MoveToPointer(DrawingPoint pointer)
        {
            if (dragOffset is not { } offset || !IsVisible || pointer == lastDragPointer) return;
            if (SetWindowPos(hwnd, IntPtr.Zero, pointer.X - offset.X, pointer.Y - offset.Y, 0, 0,
                0x0001 | 0x0004 | 0x0010 | 0x0400)) // NOSIZE | NOZORDER | NOACTIVATE | NOSENDCHANGING
                lastDragPointer = pointer;
        }

        private void StopDragging()
        {
            if (dragOffset == null) return;
            dragOffset = null;
            dragMovePosted = endingDrag = false;
            if (dragArea.IsMouseCaptured) dragArea.ReleaseMouseCapture();
            nowPlaying.SetPanelMoving(false);
        }

        private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
        {
            if (code == 0)
            {
                // Read screen pixels directly so drag speed is independent of monitor DPI.
                int kind = message.ToInt32();
                if (dragOffset != null && (kind == 0x0200 || kind == dragReleaseMessage))
                    QueueDragMove(new DrawingPoint(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4)),
                        released: kind == dragReleaseMessage);
                else if (kind is 0x0201 or 0x0204 or 0x0207 or 0x020B)
                    OnDesktopPress(new DrawingPoint(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4)));
            }
            return CallNextHookEx(mouseHook, code, message, data);
        }

        private void OnDesktopPress(DrawingPoint pointer)
        {
            if (!IsVisible || dragOffset != null) return;
            IntPtr hit = GetAncestor(WindowFromPhysicalPoint(pointer), 2 /* GA_ROOT */);
            if (hit == hwnd) return;
            if (outputPopup.IsOpen && PresentationSource.FromVisual(outputItems) is HwndSource popup && hit == popup.Handle) return;
            // Close after returning from the hook so the input queue is never blocked by teardown.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (IsVisible && dragOffset == null) Close(); }));
        }

        private void LogPresentation(string stage)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                File.AppendAllText(Path.Combine(SettingsStore.Folder, "overlay.log"),
                    $"[{DateTime.Now:O}] {stage} shellState={fullscreenState} " +
                    $"exclusiveD3D={fullscreenState == 3} exStyle=0x{GetWindowLong(hwnd, -20):X8} " +
                    $"size={ActualWidth:0}x{ActualHeight:0} opacity={Opacity:0.0}\n");
            }
            catch { }
        }

        private IntPtr WindowProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == DragMoveMessage)
            {
                handled = true;
                dragMovePosted = false;
                if (dragOffset != null)
                {
                    MoveToPointer(pendingDragPointer);
                    if (endingDrag) StopDragging();
                }
                return IntPtr.Zero;
            }
            if (msg == 0x0021) // WM_MOUSEACTIVATE
            {
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE
            }
            return IntPtr.Zero;
        }

        private void PositionOverlay()
        {
            if (!IsVisible || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            SetWindowPos(hwnd, new IntPtr(-1), screenBounds.Left + (screenBounds.Width - width) / 2,
                screenBounds.Top + (screenBounds.Height - height) / 2, 0, 0,
                0x0001 | 0x0010 | 0x0040); // NOSIZE | NOACTIVATE | SHOWWINDOW
        }

        public void Refresh()
        {
            loading = true;
            var s = main.GetQuickState();
            title.Text = s.Preset;
            subtitle.Text = s.Device;
            for (int i = 0; i < modes.Length; i++)
            {
                modes[i].IsChecked = s.Mode.HasValue && (int)s.Mode.Value == i;
                modes[i].ToolTip = main.HotkeyLabel("mode." + (ListeningMode)i);
            }
            eqSwitch.IsChecked = s.EqEnabled;
            eqSwitch.Content = "Эквалайзер";
            eqSwitch.ToolTip = main.HotkeyLabel("eq");
            compare.Content = Labeled("", "Сравнить", null, vertical: false);
            compare.ToolTip = "Зажмите и держите — услышите звук без эквалайзера и эффектов. " + main.HotkeyLabel("compare") + " — переключить";
            volume.Value = s.Volume;
            volumeText.Text = $"{s.Volume}%";
            mute.IsChecked = s.Muted;
            ((TextBlock)mute.Content).Text = s.Muted ? "" : "";
            micButton.Visibility = s.HasMic ? Visibility.Visible : Visibility.Collapsed;
            micButton.Content = Labeled(s.MicMuted ? "" : "", s.MicMuted ? "Микрофон выключен" : "Микрофон включён", null, vertical: false);
            micButton.ToolTip = (s.MicMuted ? "Нажмите, чтобы включить микрофон" : "Нажмите, чтобы выключить микрофон") + " · " + main.HotkeyLabel("mic");
            micButton.SetResourceReference(ForegroundProperty, s.MicMuted ? "MutedFgBrush" : "TextBrush");

            replayButton.Content = Labeled("", main.ReplayActive ? "Повтор" : "Повтор выкл.", main.HotkeyLabel("replay"), vertical: true);
            replayButton.IsEnabled = main.ReplayActive;
            recordButton.Content = Labeled(main.RecordingActive ? "" : "", main.RecordingActive ? "Остановить" : "Запись", main.HotkeyLabel("record"), vertical: true);
            shotButton.Content = Labeled("", "Скриншот", main.HotkeyLabel("shot"), vertical: true);
            footer.Text = $"Esc, {main.OverlayKeyText()} или клик снаружи — закрыть";
            RefreshCustom();
            loading = false;
            RefreshOutputs();
            RefreshApps();
        }

        /// <summary>"Динамики (Graphite 2 Pro Max WL)" → "Graphite 2 Pro Max WL"; "LT1080P (NVIDIA …)" → "LT1080P".</summary>
        private static string ShortName(string name)
        {
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            if (open <= 0 || close < open) return name;
            string before = name[..open].Trim(), inside = name.Substring(open + 1, close - open - 1);
            string[] generic = { "динамики", "speakers", "наушники", "headphones", "headset", "цифровой выход", "digital output", "realtek" };
            return Array.Exists(generic, g => before.StartsWith(g, StringComparison.OrdinalIgnoreCase)) ? inside : before;
        }

        private void RefreshCustom()
        {
            customPanel.Children.Clear();
            var names = main.CustomPresetNames;
            customTitle.Visibility = customBox.Visibility = names.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var n in names)
            {
                string name = n;
                var rb = new RadioButton
                {
                    GroupName = "OverlayCustom", Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 },
                    IsChecked = name == main.ActiveCustomName, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(14, 7, 14, 7), ToolTip = name,
                };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Click += (_, _) => { main.SelectCustomFromOutside(name); Refresh(); };
                customPanel.Children.Add(rb);
            }
        }

        private void RefreshOutputs()
        {
            outputItems.Children.Clear();
            List<AudioDeviceInfo> list;
            try { list = AudioDevices.ListActiveOutputs(); } catch { return; }
            string? current = DefaultDevice.Get(0);
            var now = list.Find(d => d.Id == current);

            // the button shows only where the sound goes now; the others are in the drop-down
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Glyph(IsHeadphones(now) ? "\uE7F6" : "\uE7F5", 16));
            var name = new TextBlock { Text = now != null ? ShortName(now.Name) : "Устройство не выбрано", Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var chevron = Glyph("\uE70D", 11);
            chevron.Margin = new Thickness(10, 0, 0, 0);
            chevron.Opacity = list.Count > 1 ? 1 : 0.35;
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
            outputButton.Content = row;
            outputButton.ToolTip = list.Count > 1 ? "Нажмите, чтобы выбрать другое устройство" : "Других устройств вывода нет";
            outputButton.IsEnabled = list.Count > 1;

            foreach (var d in list)
            {
                var dev = d;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(Glyph(IsHeadphones(dev) ? "\uE7F6" : "\uE7F5"));
                content.Children.Add(new TextBlock { Text = ShortName(dev.Name), Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340, VerticalAlignment = VerticalAlignment.Center });
                var rb = new RadioButton
                {
                    GroupName = "OverlayOut", Content = content, IsChecked = dev.Id == current,
                    Margin = new Thickness(0, 0, 0, 4), ToolTip = dev.Name, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 9, 12, 9),
                };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Click += (_, _) =>
                {
                    outputPopup.IsOpen = false;
                    if (dev.Id != current && DefaultDevice.Set(dev.Id)) main.SelectDeviceFromOutside(dev.Id);
                    Refresh();
                };
                outputItems.Children.Add(rb);
            }
        }

        private static bool IsHeadphones(AudioDeviceInfo? d) =>
            d != null && (d.HasCmEq || d.Name.Contains("Наушники", StringComparison.OrdinalIgnoreCase));

        private void RefreshApps()
        {
            apps.Children.Clear();
            string? deviceId = main.CurrentDeviceId;
            List<AppVolumeInfo> list = new();
            if (deviceId != null)
            {
                try { list = AppVolumeMixer.List(deviceId); } catch { }
            }
            if (list.Count == 0)
            {
                var empty = new TextBlock { Text = "Сейчас ни одна программа не играет звук. Запустите музыку, игру или видео — она появится здесь.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                apps.Children.Add(empty);
                return;
            }
            foreach (var app in list)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                g.Children.Add(AppIcons.Tile(app.IconPath, app.IsSystemSounds));
                var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = app.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = app.Title });
                Grid.SetColumn(names, 1);
                g.Children.Add(names);
                var sl = new Slider { Value = app.Volume, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
                sl.SetResourceReference(StyleProperty, "VolumeSlider");
                Grid.SetColumn(sl, 2);
                g.Children.Add(sl);
                var val = new TextBlock { Text = $"{app.Volume}%", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(val, 3);
                g.Children.Add(val);
                string process = app.Process;
                bool system = app.IsSystemSounds;
                sl.ValueChanged += (_, e) =>
                {
                    int v = (int)Math.Round(e.NewValue);
                    val.Text = $"{v}%";
                    main.SetAppVolume(process, v, remember: !system);
                };
                apps.Children.Add(g);
            }
        }
    }
}
