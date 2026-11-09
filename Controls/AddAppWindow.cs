using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GraphiteCompanion.Controls
{
    /// <summary>Two plain steps: which program (pick its .exe or one that is running), and which sound it gets.</summary>
    public sealed class AddAppWindow : Window
    {
        public const string OwnSound = "own";

        private readonly TextBlock chosen;
        private readonly ComboBox running;
        private readonly ListBox presets;
        private readonly Button ok;

        /// <summary>Process name without .exe.</summary>
        public string ProcessName { get; private set; } = "";
        /// <summary>"mode:Games", "custom:Name" or <see cref="OwnSound"/>.</summary>
        public string Target { get; private set; } = "";

        public AddAppWindow(Window owner, IReadOnlyList<(string label, string value)> targets, string currentSoundName)
        {
            Owner = owner;
            Title = "Звук для программы";
            Width = 560;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BgBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = "Звук для программы", FontSize = 19, FontWeight = FontWeights.SemiBold });
            root.Children.Add(Sub("Когда эта программа будет на экране, звук сам переключится на выбранный пресет. " +
                                  "Вышли из неё — вернётся ваш обычный звук."));

            // ---- step 1
            root.Children.Add(Header("1. КАКАЯ ПРОГРАММА"));
            var pick = new Button { Content = "Выбрать файл программы (.exe)…", HorizontalAlignment = HorizontalAlignment.Left };
            pick.SetResourceReference(StyleProperty, "AccentButton");
            pick.Click += (_, _) => PickExe();
            root.Children.Add(pick);

            root.Children.Add(Sub("или выберите из запущенных сейчас:", new Thickness(0, 10, 0, 4)));
            running = new ComboBox { DisplayMemberPath = "Label" };
            running.ItemsSource = RunningPrograms();
            running.SelectionChanged += (_, _) =>
            {
                if (running.SelectedItem is Running r) SetProgram(r.Process);
            };
            root.Children.Add(running);

            chosen = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold, Text = "Программа не выбрана" };
            chosen.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            root.Children.Add(chosen);

            // ---- step 2
            root.Children.Add(Header("2. КАКОЙ ЗВУК ДЛЯ НЕЁ"));
            presets = new ListBox { BorderThickness = new Thickness(0), Height = 200 };
            presets.SetResourceReference(BackgroundProperty, "CardBrush");
            presets.SetResourceReference(ForegroundProperty, "TextBrush");
            var items = new List<(string label, string value)>
            {
                ($"Отдельный звук только для неё — копия текущих настроек ({currentSoundName})", OwnSound),
            };
            items.AddRange(targets);
            foreach (var t in items) presets.Items.Add(new PresetItem(t.label, t.value));
            presets.DisplayMemberPath = nameof(PresetItem.Label);
            presets.SelectedIndex = targets.Count > 0 ? 1 : 0;
            presets.SelectionChanged += (_, _) => UpdateOk();
            root.Children.Add(presets);
            root.Children.Add(Sub("«Отдельный звук» создаёт новый пресет «Игра: …»: его можно подстроить под эту программу, остальное не затронется.",
                                  new Thickness(0, 8, 0, 0)));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
            cancel.SetResourceReference(StyleProperty, "GhostButton");
            cancel.Click += (_, _) => DialogResult = false;
            ok = new Button { Content = "Добавить", MinWidth = 130, IsEnabled = false };
            ok.SetResourceReference(StyleProperty, "AccentButton");
            ok.Click += (_, _) =>
            {
                Target = ((PresetItem)presets.SelectedItem).Value;
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            Content = root;
        }

        private sealed record PresetItem(string Label, string Value);
        private sealed record Running(string Label, string Process);

        private TextBlock Sub(string text, Thickness? margin = null)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 6, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            return t;
        }

        private TextBlock Header(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            return t;
        }

        private static List<Running> RunningPrograms()
        {
            var list = new List<Running>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    if (p.Id == Environment.ProcessId) continue;
                    if (list.Any(r => r.Process.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
                    string title = p.MainWindowTitle.Length > 40 ? p.MainWindowTitle[..40] + "…" : p.MainWindowTitle;
                    list.Add(new Running($"{p.ProcessName}  —  {title}", p.ProcessName));
                }
                catch { }
            }
            return list.OrderBy(r => r.Process, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void PickExe()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите файл программы или игры",
                Filter = "Программы (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) == true) SetProgram(Path.GetFileNameWithoutExtension(dlg.FileName));
        }

        private void SetProgram(string process)
        {
            ProcessName = process;
            chosen.Text = "Выбрано: " + process + ".exe";
            chosen.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            UpdateOk();
        }

        private void UpdateOk() => ok.IsEnabled = ProcessName.Length > 0 && presets.SelectedItem != null;
    }
}
