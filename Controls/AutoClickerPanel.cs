using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls
{
    /// <summary>The main page and overlay edit the same saved settings and the same worker.</summary>
    public sealed class AutoClickerPanel : UserControl
    {
        private readonly MainWindow main;
        private readonly bool compact;
        private readonly TextBox interval = NumberBox(), count = NumberBox(), delay = NumberBox(), x = NumberBox(), y = NumberBox();
        private readonly ComboBox button = Choice("Левая", "Правая", "Средняя");
        private readonly ComboBox clickType = Choice("Одиночный", "Двойной");
        private readonly ComboBox repeat = Choice("До остановки", "Число повторов");
        private readonly ComboBox mode = Choice("Нажатие — вкл. / выкл.", "Пока держу клавишу"), timeLimit = Choice("Без ограничения", "Остановить через");
        private readonly TextBox duration = NumberBox();
        private readonly CheckBox indicator = new() { Content = "Индикатор вне оверлея", Margin = new Thickness(0, 4, 0, 0) };
        private readonly ComboBox position = Choice("За курсором", "В заданной точке");
        private readonly Button start = ActionButton("Запустить", true), key = ActionButton("F6"), pick = ActionButton("Запомнить точку через 3 с");
        private readonly TextBlock status = Text("Готов к запуску", 18), detail = Text("", 12, true), error = Text("", 12), speed = Text("", 11, true), pointSummary = Text("", 12, true);
        private readonly StackPanel editable = new();
        private readonly DispatcherTimer pickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private AutoClickerSettings? shown;
        private bool syncing, recording, subscribed;
        private int pickSeconds;
        public event Action? SettingsRequested;

        public AutoClickerPanel(MainWindow main, bool compact)
        {
            this.main = main;
            this.compact = compact;
            FontSize = 13;
            SetResourceReference(ForegroundProperty, "TextBrush");
            var root = new StackPanel();
            if (compact)
            {
                Width = 336;
                status.FontSize = 14;
                var title = Text("Автокликер", 17);
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 0, 0, 8);
                root.Children.Add(title);
                root.Children.Add(status);
                detail.Margin = new Thickness(0, 3, 0, 12);
                root.Children.Add(detail);
            }
            else
            {
                var texts = new StackPanel();
                status.FontWeight = FontWeights.SemiBold;
                detail.Margin = new Thickness(0, 5, 0, 0);
                texts.Children.Add(status); texts.Children.Add(detail);
                start.MinWidth = 150;
                var banner = Pair(texts, start, autoRight: true);
                root.Children.Add(Box(banner));
            }

            var intervalField = Field("Интервал между повторами", WithUnit(interval, "мс"));
            HelpTips.Set(interval, "От 10 до 3 600 000 мс. 100 мс = 10 повторов в секунду. В двойном режиме один повтор — два клика.");
            speed.Margin = new Thickness(0, -2, 0, 12);
            if (compact) speed.Visibility = Visibility.Collapsed;
            var clicks = new StackPanel();
            clicks.Children.Add(compact ? intervalField : Pair(intervalField, Field("Задержка старта", WithUnit(delay, "мс")))); clicks.Children.Add(speed);
            clicks.Children.Add(Pair(Field("Кнопка мыши", button), Field("Тип клика", clickType)));
            var repeats = new StackPanel();
            repeats.Children.Add(Pair(Field("Повторять", repeat), Field("Количество", count)));
            repeats.Children.Add(Pair(Field("По времени", timeLimit), Field("Секунды", duration)));
            HelpTips.Set(duration, "От 1 до 86 400 секунд. Отсчёт после задержки старта; пауза над SoundShot тоже учитывается. Остановка по первому достигнутому ограничению.");
            HelpTips.Set(count, "От 1 до 1 000 000 повторов. Двойной клик считается одним повтором.");
            if (compact)
            {
                editable.Children.Add(Field("Как запускать", mode));
                editable.Children.Add(clicks);
                editable.Children.Add(repeats);
                pointSummary.Margin = new Thickness(0, 0, 0, 10);
                editable.Children.Add(pointSummary);
            }
            else
            {
                var location = new StackPanel();
                location.Children.Add(Field("Где нажимать", position));
                location.Children.Add(Pair(Field("Координата X", x), Field("Координата Y", y)));
                pick.HorizontalAlignment = HorizontalAlignment.Stretch;
                location.Children.Add(pick);
                HelpTips.Set(pick, "Нажмите и наведите курсор на нужное место. Через 3 секунды его положение запомнится без клика. Поддерживаются все мониторы.");
                var launch = new StackPanel();
                launch.Children.Add(Field("Как запускать", mode));
                HelpTips.Set(delay, "От 0 до 60 000 мс. Задержка даёт время перенести курсор после запуска кнопкой.");
                launch.Children.Add(Field("Запуск / остановка", key));
                var stopHint = Text("Esc — остановить", 12, true);
                stopHint.Margin = new Thickness(0, 0, 0, 2);
                launch.Children.Add(stopHint);
                indicator.SetResourceReference(StyleProperty, "Switch");
                launch.Children.Add(indicator);
                var left = new StackPanel(); left.Children.Add(Box(clicks, "КЛИКИ")); left.Children.Add(Box(launch, "УПРАВЛЕНИЕ"));
                var right = new StackPanel(); right.Children.Add(Box(repeats, "ПОВТОРЫ")); right.Children.Add(Box(location, "ПОЗИЦИЯ"));
                editable.Children.Add(Pair(left, right));
            }
            root.Children.Add(editable);
            if (compact)
            {
                root.Children.Add(Field("Запуск / остановка · нажмите, чтобы изменить", key));
                start.HorizontalAlignment = HorizontalAlignment.Stretch;
                root.Children.Add(start);
                var more = ActionButton("Все настройки →");
                more.Margin = new Thickness(0, 8, 0, 0);
                more.Click += (_, _) => { SettingsRequested?.Invoke(); main.ShowAutoClickerPage(); };
                root.Children.Add(more);
                HelpTips.Set(start, "Повторное нажатие клавиши или Esc останавливает клики. По окнам SoundShot автокликер не нажимает.");
            }
            error.Margin = new Thickness(0, 8, 0, 0);
            error.SetResourceReference(TextBlock.ForegroundProperty, "MutedFgBrush");
            error.Visibility = Visibility.Collapsed;
            root.Children.Add(error);
            if (compact) Compress(root);
            Content = root;
            foreach (var box in new[] { interval, count, delay, x, y, duration }) box.TextChanged += (_, _) => { if (!syncing) Commit(); };
            foreach (var combo in new[] { button, clickType, repeat, position, mode, timeLimit }) combo.SelectionChanged += (_, _) => { if (!syncing) Commit(); };
            indicator.Click += (_, _) => { if (!syncing) Commit(); };
            start.Click += (_, _) => { if (main.AutoClickerState.Running || Commit()) main.ToggleAutoClicker(); Refresh(); };
            key.Click += (_, _) =>
            {
                if (recording) { EndRecording(); return; }
                if (!main.BeginAutoClickerKeyRecording(this)) { ShowError("Сначала закройте другой редактор горячих клавиш."); return; }
                recording = true;
                key.Content = "Нажмите клавишу…";
                key.Focus();
                HelpTips.Set(key, "Нажмите F-клавишу или сочетание с Ctrl, Alt, Shift. Esc — отмена.");
            };
            key.PreviewKeyDown += RecordKey;
            key.LostKeyboardFocus += (_, _) => { if (recording) EndRecording(); };
            pick.Click += (_, _) =>
            {
                if (pickSeconds > 0) { CancelPick(); return; }
                main.StopAutoClicker();
                pickSeconds = 3; pick.Content = "Наведите курсор · 3 с";
                main.SetAutoClickerEditorValid(this, false);
                pickTimer.Start();
            };
            pickTimer.Tick += (_, _) =>
            {
                if (--pickSeconds > 0) { pick.Content = $"Наведите курсор · {pickSeconds} с"; return; }
                pickTimer.Stop();
                var point = WindowsAutoClickInput.Cursor();
                syncing = true;
                if (point is { } p) { x.Text = p.X.ToString(CultureInfo.InvariantCulture); y.Text = p.Y.ToString(CultureInfo.InvariantCulture); position.SelectedIndex = 1; }
                syncing = false;
                pick.Content = "Запомнить точку через 3 с";
                if (point == null) ShowError("Не удалось определить положение курсора.");
                Commit();
            };
            Loaded += (_, _) => { if (!subscribed) { main.AutoClickerChanged += Refresh; subscribed = true; } shown = null; Refresh(); };
            Unloaded += (_, _) =>
            {
                if (subscribed) { main.AutoClickerChanged -= Refresh; subscribed = false; }
                EndRecording(); CancelPick(); main.SetAutoClickerEditorValid(this, true);
            };
            IsVisibleChanged += (_, _) => { if (!IsVisible) { EndRecording(); CancelPick(); main.SetAutoClickerEditorValid(this, true); shown = null; } else Refresh(); };
            Refresh();
        }

        private void CancelPick()
        {
            pickTimer.Stop(); pickSeconds = 0; pick.Content = "Запомнить точку через 3 с";
            main.SetAutoClickerEditorValid(this, true);
        }
        private void EndRecording()
        {
            if (!recording) return;
            recording = false; main.EndAutoClickerKeyRecording(this); Refresh();
        }
        private void RecordKey(object sender, KeyEventArgs e)
        {
            if (!recording) return;
            e.Handled = true;
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            if (pressed == Key.Escape) { EndRecording(); return; }
            if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return;
            string? message = main.SaveAutoClickerKey(this, (uint)Keyboard.Modifiers, (uint)KeyInterop.VirtualKeyFromKey(pressed));
            if (message != null) { ShowError(message); return; }
            recording = false; ShowError(null); Refresh();
        }

        private bool Commit()
        {
            var current = main.AutoClickerOptions;
            bool valid = Read(interval, 10, 3_600_000, out int ms);
            valid &= Read(count, 1, 1_000_000, out int repeats, repeat.SelectedIndex == 1);
            valid &= Read(duration, 1, 86400, out int seconds, timeLimit.SelectedIndex == 1);
            int wait = current.StartDelayMs, px = current.X, py = current.Y;
            if (!compact)
            {
                valid &= Read(delay, 0, 60_000, out wait);
                valid &= Read(x, -100_000, 100_000, out px, position.SelectedIndex == 1);
                valid &= Read(y, -100_000, 100_000, out py, position.SelectedIndex == 1);
            }
            main.SetAutoClickerEditorValid(this, valid && pickSeconds == 0);
            duration.IsEnabled = timeLimit.SelectedIndex == 1;
            count.IsEnabled = repeat.SelectedIndex == 1;
            x.IsEnabled = y.IsEnabled = position.SelectedIndex == 1;
            if (!valid) { ShowError("Проверьте выделенное значение. Допустимый диапазон указан в подсказке."); return false; }
            ShowError(null);
            var next = current with { HoldToRun = mode.SelectedIndex == 1, TimeLimited = timeLimit.SelectedIndex == 1,
                RunSeconds = timeLimit.SelectedIndex == 1 ? seconds : current.RunSeconds, ShowIndicator = compact ? current.ShowIndicator : indicator.IsChecked == true, IntervalMs = ms, Button = (AutoClickButton)Math.Max(0, button.SelectedIndex), DoubleClick = clickType.SelectedIndex == 1,
                Limited = repeat.SelectedIndex == 1, RepeatCount = repeat.SelectedIndex == 1 ? repeats : current.RepeatCount,
                FixedPosition = compact ? current.FixedPosition : position.SelectedIndex == 1, X = px, Y = py, StartDelayMs = wait };
            shown = next;
            main.SetAutoClickerOptions(next);
            Refresh();
            return true;
        }
        private static bool Read(TextBox box, int min, int max, out int value, bool required = true)
        {
            bool valid = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
            box.SetResourceReference(BorderBrushProperty, valid || !required ? "CardBorderBrush" : "MutedFgBrush");
            return valid || !required;
        }
        private void ShowError(string? message) { error.Text = message ?? ""; error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }

        private void Refresh()
        {
            if (IsLoaded && !IsVisible) return;
            if (recording && !main.IsAutoClickerKeyRecording) recording = false;
            var options = main.AutoClickerOptions;
            if (shown != options)
            {
                syncing = true;
                mode.SelectedIndex = options.HoldToRun ? 1 : 0; timeLimit.SelectedIndex = options.TimeLimited ? 1 : 0;
                duration.Text = options.RunSeconds.ToString(CultureInfo.InvariantCulture); duration.IsEnabled = options.TimeLimited; indicator.IsChecked = options.ShowIndicator;
                interval.Text = options.IntervalMs.ToString(CultureInfo.InvariantCulture); count.Text = options.RepeatCount.ToString(CultureInfo.InvariantCulture);
                delay.Text = options.StartDelayMs.ToString(CultureInfo.InvariantCulture); x.Text = options.X.ToString(CultureInfo.InvariantCulture); y.Text = options.Y.ToString(CultureInfo.InvariantCulture);
                button.SelectedIndex = (int)options.Button; clickType.SelectedIndex = options.DoubleClick ? 1 : 0;
                repeat.SelectedIndex = options.Limited ? 1 : 0; position.SelectedIndex = options.FixedPosition ? 1 : 0;
                count.IsEnabled = options.Limited; x.IsEnabled = y.IsEnabled = options.FixedPosition;
                shown = options; syncing = false;
                main.SetAutoClickerEditorValid(this, pickSeconds == 0);
                ShowError(null);
            }
            var state = main.AutoClickerState;
            double seconds = Math.Max(0, (state.StartsAt - DateTimeOffset.UtcNow).TotalSeconds);
            status.Text = !state.Running ? (state.Completed > 0 ? "Остановлен" : "Готов к запуску") : seconds > 0 ? $"Старт через {Math.Ceiling(seconds):0} с" : state.WaitingForTarget ? (options.FixedPosition ? "Точка находится в окне SoundShot" : "Наведите курсор вне SoundShot") : "Автокликер работает";
            status.SetResourceReference(TextBlock.ForegroundProperty, state.Running ? "AccentBrush" : "TextBrush");
            detail.Text = state.Error ?? (state.Running || state.Completed > 0 ? $"Повторов: {state.Completed:N0}" + (options.Limited ? $" / {options.RepeatCount:N0}" : "") + "  ·  Esc — остановить" : "По окнам SoundShot клики не выполняются");
            if (state.Running && state.RemainingSeconds is double left) detail.Text += $"  ·  ещё {Math.Ceiling(left):0} с";
            detail.Visibility = compact && !state.Running && state.Completed == 0 && state.Error == null ? Visibility.Collapsed : Visibility.Visible;
            if (compact) status.Margin = new Thickness(0, 0, 0, detail.Visibility == Visibility.Collapsed ? 12 : 0);
            if (!recording)
            {
                key.Content = main.HotkeyLabel("autoclicker");
                HelpTips.Set(key, "Нажмите и задайте клавишу запуска / остановки. По умолчанию F6. Esc отменяет ввод.");
            }
            editable.IsEnabled = !state.Running;
            start.Content = state.Running ? "Остановить" : options.HoldToRun ? "Удерживайте " + main.HotkeyLabel("autoclicker") : "Запустить";
            start.IsEnabled = state.Running || !options.HoldToRun;
            AutomationProperties.SetName(start, state.Running ? "Остановить автокликер" : "Запустить автокликер");
            speed.Text = options.IntervalMs < 1000 ? $"{1000d / options.IntervalMs:0.#} повторов в секунду" : $"Один повтор каждые {options.IntervalMs / 1000d:0.###} с";
            pointSummary.Text = options.FixedPosition ? $"Точка: {options.X}, {options.Y}  ·  задержка {options.StartDelayMs / 1000d:0.###} с" : $"За курсором  ·  задержка {options.StartDelayMs / 1000d:0.###} с";
        }

        private static void Compress(DependencyObject root)
        {
            if (root is StackPanel stack && stack.Margin.Bottom == 12) stack.Margin = new Thickness(0, 0, 0, 7);
            if (root is Panel panel) foreach (UIElement child in panel.Children) Compress(child);
        }

        private static TextBlock Text(string text, double size, bool secondary = false)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "SubTextBrush" : "TextBrush"); return block;
        }
        private static TextBox NumberBox()
        {
            var box = new TextBox { Height = 34, Padding = new Thickness(10, 5, 10, 5), VerticalContentAlignment = VerticalAlignment.Center };
            box.SetResourceReference(BackgroundProperty, "InsetBrush"); box.SetResourceReference(ForegroundProperty, "TextBrush"); box.SetResourceReference(BorderBrushProperty, "CardBorderBrush"); return box;
        }
        private static ComboBox Choice(params string[] items) => new() { ItemsSource = items, SelectedIndex = 0, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private static Button ActionButton(string text, bool accent = false)
        {
            var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(12, 7, 12, 7) };
            button.SetResourceReference(StyleProperty, accent ? "AccentButton" : "GhostButton"); return button;
        }
        private static FrameworkElement Field(string label, FrameworkElement control)
        {
            AutomationProperties.SetName(control, label);
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var caption = Text(label, 12, true); caption.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(caption); stack.Children.Add(control); return stack;
        }
        private static Grid Pair(FrameworkElement left, FrameworkElement right, bool autoRight = false)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = autoRight ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right); return grid;
        }
        private static FrameworkElement WithUnit(FrameworkElement editor, string unit) => Pair(editor, Text(unit, 12, true), autoRight: true);
        private static Border Box(FrameworkElement child, string? title = null)
        {
            var stack = new StackPanel();
            if (title != null) { var heading = Text(title, 11, true); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 10); stack.Children.Add(heading); }
            stack.Children.Add(child);
            var border = new Border { Child = stack, CornerRadius = new CornerRadius(14), Padding = new Thickness(16), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
            border.SetResourceReference(Border.BackgroundProperty, "CardBrush"); border.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush"); return border;
        }
    }
}
