using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GraphiteCompanion.Controls
{
    /// <summary>
    /// "Which monitor?" card shown over the game when several monitors are recorded.
    /// Keys: 1–9 the monitor with that number, 0 all of them, Enter the highlighted one, Esc cancel.
    /// </summary>
    public sealed class MonitorPickWindow : Window
    {
        public sealed record Choice(string Device, int Number, string Game);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly List<Choice> items;
        private readonly List<Button> buttons = new();
        private readonly List<TextBlock> subs = new();
        private readonly System.Drawing.Rectangle placeOn;
        private readonly TaskCompletionSource<List<string>?> result = new();
        private int highlighted;
        private bool wasActive;

        /// <summary>Returns the chosen monitor devices, or null when cancelled.</summary>
        public static Task<List<string>?> Pick(string title, string hint, IList<Choice> items, string defaultDevice, System.Drawing.Rectangle placeOn)
        {
            var w = new MonitorPickWindow(title, hint, items, defaultDevice, placeOn);
            w.Show();
            return w.result.Task;
        }

        private MonitorPickWindow(string titleText, string hintText, IList<Choice> choices, string defaultDevice, System.Drawing.Rectangle placeOn)
        {
            items = choices.ToList();
            this.placeOn = placeOn;
            highlighted = Math.Max(0, items.FindIndex(c => c.Device == defaultDevice));

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "TextBrush");

            var stack = new StackPanel { Width = 360 };
            stack.Children.Add(new TextBlock { Text = titleText, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var hint = new TextBlock { Text = hintText, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            stack.Children.Add(hint);

            foreach (var c in items)
                stack.Children.Add(MakeButton(c.Number.ToString(), $"Монитор {c.Number}", c.Game, () => Finish(new() { c.Device })));
            if (items.Count > 1)
                stack.Children.Add(MakeButton("0", "Все мониторы", $"{items.Count} {(items.Count < 5 ? "файла" : "файлов")}, по одному на монитор", () => Finish(items.Select(i => i.Device).ToList())));

            var keys = new TextBlock { Text = "Цифра — выбрать · Enter — выделенный · Esc — отмена", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            stack.Children.Add(keys);

            var card = new Border
            {
                Margin = new Thickness(24), Padding = new Thickness(20, 18, 20, 16),
                CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 },
                Child = stack,
            };
            card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "AccentBorderBrush");
            Content = card;

            Highlight(highlighted);
            SourceInitialized += (_, _) => Place();
            Loaded += (_, _) => { Place(); SetForegroundWindow(new WindowInteropHelper(this).Handle); Activate(); buttons[highlighted].Focus(); };
            Activated += (_, _) => wasActive = true;
            Deactivated += (_, _) => { if (wasActive) Finish(null); };   // clicked back into the game = cancel
            PreviewKeyDown += OnKey;
            Loaded += (_, _) => GrabKeys();
            Closed += (_, _) => ReleaseKeys();
        }

        // ---- keys work even when Windows keeps focus in the game (it often refuses to hand it to a popup)
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
        private readonly Dictionary<int, Key> grabbed = new();

        private void GrabKeys()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var keys = new List<(uint vk, Key key)> { (0x1B, Key.Escape), (0x0D, Key.Enter), (0x26, Key.Up), (0x28, Key.Down) };
            foreach (var c in items.Where(c => c.Number is > 0 and < 10)) { keys.Add((0x30u + (uint)c.Number, Key.D0 + c.Number)); keys.Add((0x60u + (uint)c.Number, Key.NumPad0 + c.Number)); }
            if (items.Count > 1) { keys.Add((0x30, Key.D0)); keys.Add((0x60, Key.NumPad0)); }
            int id = 0xB100;
            foreach (var (vk, key) in keys)
                if (RegisterHotKey(hwnd, ++id, 0x4000 /* NOREPEAT */, vk)) grabbed[id] = key;
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x0312 && grabbed.TryGetValue(w.ToInt32(), out var k)) { handled = true; Press(k); }
                return IntPtr.Zero;
            });
        }

        private void ReleaseKeys()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var id in grabbed.Keys) UnregisterHotKey(hwnd, id);
            grabbed.Clear();
        }

        private Button MakeButton(string key, string title, string sub, Action click)
        {
            var keyBox = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0),
                                      Child = new TextBlock { Text = key, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            keyBox.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            var s = new TextBlock { Text = sub, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 270 };
            texts.Children.Add(s);
            subs.Add(s);
            // fixed width: the button styles centre their content, this keeps rows left-aligned
            var row = new StackPanel { Orientation = Orientation.Horizontal, Width = 326 };
            row.Children.Add(keyBox);
            row.Children.Add(texts);

            var b = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6) };
            b.Click += (_, _) => click();
            int index = buttons.Count;
            b.MouseEnter += (_, _) => Highlight(index);
            buttons.Add(b);
            return b;
        }

        private void Highlight(int i)
        {
            highlighted = i;
            for (int k = 0; k < buttons.Count; k++)
            {
                buttons[k].SetResourceReference(StyleProperty, k == i ? "AccentButton" : "GhostButton");
                if (k == i) subs[k].Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));  // readable on the accent
                else subs[k].SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (Press(e.Key == Key.System ? e.SystemKey : e.Key)) e.Handled = true;
        }

        private bool Press(Key key)
        {
            int digit = key >= Key.D0 && key <= Key.D9 ? key - Key.D0 : key >= Key.NumPad0 && key <= Key.NumPad9 ? key - Key.NumPad0 : -1;
            if (key == Key.Escape) Finish(null);
            else if (key == Key.Enter) buttons[highlighted].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (key == Key.Down || key == Key.Up) Highlight((highlighted + (key == Key.Down ? 1 : buttons.Count - 1)) % buttons.Count);
            else if (digit == 0 && items.Count > 1) Finish(items.Select(c => c.Device).ToList());
            else if (digit > 0 && items.FirstOrDefault(c => c.Number == digit) is { } c) Finish(new() { c.Device });
            else return false;
            return true;
        }

        /// <summary>Centre on the given monitor (pixel rectangle).</summary>
        private void Place()
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            var m = src.CompositionTarget.TransformToDevice;
            int w = (int)(ActualWidth * m.M11), h = (int)(ActualHeight * m.M22);
            if (w == 0 || h == 0) return;
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
                         placeOn.Left + (placeOn.Width - w) / 2, placeOn.Top + (placeOn.Height - h) / 2, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE|NOZORDER|NOACTIVATE
        }

        private void Finish(List<string>? chosen)
        {
            if (result.Task.IsCompleted) return;
            result.TrySetResult(chosen);
            Close();
        }
    }
}
