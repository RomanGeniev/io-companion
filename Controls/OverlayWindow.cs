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
    /// Game-Bar-style overlay opened by a hotkey over any window: dims the screen and shows a panel with
    /// modes, equalizer, compare, device / microphone volume and per-program volume. Esc, the hotkey or
    /// a click outside closes it. Works over windowed / borderless games (not exclusive fullscreen).
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        private readonly MainWindow main;
        private readonly StackPanel apps = new();
        private readonly UniformGrid outputs = new();
        private readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock subtitle = new() { FontSize = 12 };
        private readonly RadioButton[] modes = new RadioButton[3];
        private readonly CheckBox eqSwitch = new() { Content = "Эквалайзер" };
        private readonly Slider volume = new();
        private readonly TextBlock volumeText = new() { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        private readonly ToggleButton mute = new();
        private readonly Button micButton = new();
        private readonly TextBlock micText = new();
        private readonly Button compare = new();
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

            // ---- header
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var header = new StackPanel();
            header.Children.Add(title);
            header.Children.Add(subtitle);

            // ---- modes
            var modeGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 18, 0, 0) };
            string[] glyphs = { "", "", "" };
            foreach (ListeningMode m in Enum.GetValues<ListeningMode>())
            {
                var mode = m;
                var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock { Text = glyphs[(int)m], FontFamily = R<FontFamily>("IconFont"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = FactoryPresets.Name(m), Margin = new Thickness(8, 0, 0, 0), FontSize = 14 });
                var rb = new RadioButton { GroupName = "OverlayMode", Content = content, Padding = new Thickness(0, 6, 0, 6) };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Click += (_, _) => { main.SelectModeFromOutside(mode); Refresh(); };
                modes[(int)m] = rb;
                modeGrid.Children.Add(rb);
            }
            var modeBox = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(4), Child = modeGrid };
            modeBox.SetResourceReference(Border.BackgroundProperty, "InsetBrush");

            // ---- EQ + compare + mic
            eqSwitch.SetResourceReference(StyleProperty, "Switch");
            eqSwitch.Click += (_, _) => { main.ToggleEqFromOutside(); Refresh(); };

            compare.SetResourceReference(StyleProperty, "GhostButton");
            compare.Content = IconText("", "Сравнить (держите)");
            compare.PreviewMouseLeftButtonDown += (_, _) => { main.CompareOn(); compare.CaptureMouse(); };
            compare.PreviewMouseLeftButtonUp += (_, _) => { compare.ReleaseMouseCapture(); main.CompareOff(); };

            micButton.SetResourceReference(StyleProperty, "GhostButton");
            micButton.Content = micText;
            micButton.Click += (_, _) => { main.ToggleMicMute(fromHotkey: false); Refresh(); };

            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            eqSwitch.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(eqSwitch);
            compare.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(compare, 1);
            row.Children.Add(compare);
            micButton.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(micButton, 2);
            row.Children.Add(micButton);

            // ---- device volume
            var volRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            volRow.ColumnDefinitions.Add(new ColumnDefinition());
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mute.SetResourceReference(StyleProperty, "IconToggle");
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

            // ---- apps
            var appsTitle = new TextBlock { Text = "ГРОМКОСТЬ ПРОГРАММ", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 8) };
            appsTitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var appsScroll = new ScrollViewer { Content = apps, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var hint = new TextBlock { Text = "Esc или та же горячая клавиша — закрыть", FontSize = 11, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");

            // ---- output device (where Windows sends the sound)
            var outTitle = new TextBlock { Text = "ВЫВОД ЗВУКА", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) };
            outTitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            outputs.Columns = 2;

            var content2 = new StackPanel();
            foreach (var el in new UIElement[] { header, outTitle, outputs, modeBox, row, volRow, appsTitle, appsScroll, hint }) content2.Children.Add(el);

            panel = new Border
            {
                Width = 560, CornerRadius = new CornerRadius(20), Padding = new Thickness(26, 22, 26, 18), BorderThickness = new Thickness(1),
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

        private static StackPanel IconText(string glyph, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = R<FontFamily>("IconFont"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0) });
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
            for (int i = 0; i < modes.Length; i++) modes[i].IsChecked = s.Mode.HasValue && (int)s.Mode.Value == i;
            eqSwitch.IsChecked = s.EqEnabled;
            volume.Value = s.Volume;
            volumeText.Text = $"{s.Volume}%";
            mute.IsChecked = s.Muted;
            ((TextBlock)mute.Content).Text = s.Muted ? "" : "";
            micButton.Visibility = s.HasMic ? Visibility.Visible : Visibility.Collapsed;
            micText.Text = s.MicMuted ? "Микрофон выкл." : "Микрофон вкл.";
            micButton.SetResourceReference(ForegroundProperty, s.MicMuted ? "MutedFgBrush" : "TextBrush");
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
                var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock
                {
                    Text = dev.HasCmEq || dev.Name.Contains("Наушники", StringComparison.OrdinalIgnoreCase) ? "" : "",
                    FontFamily = R<FontFamily>("IconFont"), VerticalAlignment = VerticalAlignment.Center,
                });
                content.Children.Add(new TextBlock { Text = ShortName(dev.Name), Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
                var rb = new RadioButton
                {
                    GroupName = "OverlayOut", Content = content, IsChecked = dev.Id == current,
                    Margin = new Thickness(0, 0, 6, 6), ToolTip = dev.Name,
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
                var empty = new TextBlock { Text = "Сейчас ни одна программа не играет звук.", FontSize = 12 };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                apps.Children.Add(empty);
                return;
            }
            foreach (var app in list)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                g.Children.Add(new TextBlock { Text = app.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var sl = new Slider { Value = app.Volume, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
                sl.SetResourceReference(StyleProperty, "VolumeSlider");
                Grid.SetColumn(sl, 1);
                g.Children.Add(sl);
                var val = new TextBlock { Text = $"{app.Volume}%", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(val, 2);
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
