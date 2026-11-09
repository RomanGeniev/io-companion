using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Models;

namespace GraphiteCompanion.Controls
{
    /// <summary>
    /// Panel opened by Win+G over any window: dims the screen and shows two columns - sound on the left
    /// (mode, equalizer, volume, microphone, output device), recording and per-program volume on the right.
    /// Esc, the hotkey or a click outside closes it. Works over windowed / borderless games (not exclusive fullscreen).
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        private readonly MainWindow main;
        private readonly StackPanel apps = new();
        private readonly UniformGrid outputs = new() { Columns = 1 };
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
        private readonly Border panel;
        private bool loading;

        private static T R<T>(string key) => (T)Application.Current.FindResource(key);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public OverlayWindow(MainWindow main)
        {
            this.main = main;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "TextBrush");

            // ================= header: what is playing now + keyboard / close
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(title);
            titles.Children.Add(subtitle);

            var keys = new Button { ToolTip = "Изменить горячие клавиши", Content = Glyph("", 15), Padding = new Thickness(12, 8, 12, 8) };
            keys.SetResourceReference(StyleProperty, "GhostButton");
            keys.Click += (_, _) => { Close(); main.OpenHotkeysWindow(); };
            var close = new Button { ToolTip = "Закрыть (Esc)", Content = Glyph("", 15), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 0, 0, 0) };
            close.SetResourceReference(StyleProperty, "GhostButton");
            close.Click += (_, _) => Close();
            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerButtons.Children.Add(keys);
            headerButtons.Children.Add(close);
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(titles);
            Grid.SetColumn(headerButtons, 1);
            header.Children.Add(headerButtons);

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
            eqRow.ColumnDefinitions.Add(new ColumnDefinition());
            eqRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            eqRow.Children.Add(eqSwitch);
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
            micButton.Margin = new Thickness(0, 12, 0, 0);
            micButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            micButton.Click += (_, _) => { main.ToggleMicMute(fromHotkey: false); Refresh(); };

            var left = new StackPanel();
            left.Children.Add(Section("ЗВУК", first: true));
            left.Children.Add(modeBox);
            left.Children.Add(eqRow);
            left.Children.Add(Section("ГРОМКОСТЬ"));
            left.Children.Add(volRow);
            left.Children.Add(micButton);
            left.Children.Add(Section("КУДА ИДЁТ ЗВУК"));
            left.Children.Add(outputs);

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

            var content2 = new StackPanel();
            content2.Children.Add(header);
            content2.Children.Add(columns);
            content2.Children.Add(footer);

            panel = new Border
            {
                Width = 960, CornerRadius = new CornerRadius(20), Padding = new Thickness(30, 24, 30, 18), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.6 },
                Child = content2,
                RenderTransform = new ScaleTransform(1, 1),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            panel.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            panel.MouseLeftButtonDown += (_, e) => e.Handled = true; // clicks inside don't close
            Content = new Grid { Children = { panel } };

            MouseLeftButtonDown += (_, _) => Close();             // click on the dimmed area
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Deactivated += (_, _) => { if (IsVisible) Close(); }; // alt-tab / click into another window
            Closed += (_, _) => main.CompareOff();
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

        /// <summary>Shows the overlay on the monitor under the mouse cursor.</summary>
        public void Open()
        {
            Refresh();
            Opacity = 0;
            // Put a small window on the monitor under the cursor, then let Windows maximize it: a maximized
            // borderless window covers exactly that monitor, whatever the display scaling (150% etc.) is.
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Height = 200;
            Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, screen.Bounds.Left + 50, screen.Bounds.Top + 50, 200, 200, 0x0004 | 0x0010);
            WindowState = WindowState.Maximized;
            Activate();
            Focus();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var st = (ScaleTransform)panel.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
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
            eqSwitch.Content = s.EqEnabled ? "Эквалайзер включён" : "Эквалайзер выключен";
            eqSwitch.ToolTip = main.HotkeyLabel("eq");
            compare.Content = Labeled("", "Сравнить", "зажмите, чтобы услышать без обработки", vertical: false);
            volume.Value = s.Volume;
            volumeText.Text = $"{s.Volume}%";
            mute.IsChecked = s.Muted;
            ((TextBlock)mute.Content).Text = s.Muted ? "" : "";
            micButton.Visibility = s.HasMic ? Visibility.Visible : Visibility.Collapsed;
            micButton.Content = Labeled(s.MicMuted ? "" : "", s.MicMuted ? "Микрофон выключен — нажмите, чтобы включить" : "Микрофон включён",
                                        main.HotkeyLabel("mic"), vertical: false);
            micButton.SetResourceReference(ForegroundProperty, s.MicMuted ? "MutedFgBrush" : "TextBrush");

            replayButton.Content = Labeled("", main.ReplayActive ? "Повтор" : "Повтор выкл.", main.HotkeyLabel("replay"), vertical: true);
            replayButton.IsEnabled = main.ReplayActive;
            recordButton.Content = Labeled(main.RecordingActive ? "" : "", main.RecordingActive ? "Остановить" : "Запись", main.HotkeyLabel("record"), vertical: true);
            shotButton.Content = Labeled("", "Скриншот", main.HotkeyLabel("shot"), vertical: true);
            footer.Text = $"Esc или {main.OverlayKeyText()} — закрыть  ·  значок клавиатуры вверху — изменить горячие клавиши";
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

        private void RefreshOutputs()
        {
            outputs.Children.Clear();
            List<AudioDeviceInfo> list;
            try { list = AudioDevices.ListActiveOutputs(); } catch { return; }
            string? current = DefaultDevice.Get(0);
            foreach (var d in list)
            {
                var dev = d;
                var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
                content.Children.Add(Glyph(dev.HasCmEq || dev.Name.Contains("Наушники", StringComparison.OrdinalIgnoreCase) ? "" : ""));
                content.Children.Add(new TextBlock { Text = ShortName(dev.Name), Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 });
                var rb = new RadioButton
                {
                    GroupName = "OverlayOut", Content = content, IsChecked = dev.Id == current,
                    Margin = new Thickness(0, 0, 0, 6), ToolTip = dev.Name, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 9, 12, 9),
                };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Click += (_, _) =>
                {
                    if (DefaultDevice.Set(dev.Id)) main.SelectDeviceFromOutside(dev.Id);
                    Refresh();
                };
                outputs.Children.Add(rb);
            }
        }

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
                g.Children.Add(AppIcons.Tile(app.ExePath, app.IsSystemSounds));
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
