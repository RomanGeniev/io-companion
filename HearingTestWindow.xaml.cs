using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Models;

namespace GraphiteCompanion
{
    /// <summary>
    /// Simple ascending-threshold hearing test. For each EQ band a tone gets louder in 3 dB steps
    /// until the user presses "Слышу". Thresholds relative to 1 kHz are compared with the typical
    /// human hearing threshold curve and turned into gentle EQ corrections.
    /// </summary>
    public partial class HearingTestWindow : Window
    {
        // EQ bands 2..10 are tested (30 Hz copies the 60 Hz result - too low to test reliably on headphones)
        private static readonly double[] Freqs = { 60, 120, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        private const double StartDb = -78, MaxDb = -12, StepDb = 3;

        private readonly TonePlayer player = new();
        private readonly string deviceId;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(650) };
        private readonly double?[] thresholds = new double?[Freqs.Length];
        private int index = -1;
        private double level;
        private bool pausing;

        /// <summary>Resulting EQ gains for the 10 bands (null if the test was cancelled).</summary>
        public int[]? Result { get; private set; }

        public HearingTestWindow(string outputDeviceId)
        {
            InitializeComponent();
            deviceId = outputDeviceId;
            timer.Tick += (_, _) => Tick();
            Dots.ItemsSource = Freqs.Select(f => MakeDot(f)).ToList();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Space && index >= 0) { Heard(); e.Handled = true; }
            };
            Closed += (_, _) => { timer.Stop(); player.Dispose(); };
        }

        private static Border MakeDot(double f) => new()
        {
            Margin = new Thickness(3, 0, 3, 0), Height = 30, CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.FindResource("InsetBrush"),
            Child = new TextBlock
            {
                Text = f >= 1000 ? $"{f / 1000:0}K" : $"{f:0}", FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("SubTextBrush"),
            },
        };

        private void Main_Click(object sender, RoutedEventArgs e)
        {
            if (index < 0) Begin();
            else if (index >= Freqs.Length) { DialogResult = true; Close(); }
            else Heard();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
            Close();
        }

        private void Begin()
        {
            try { player.Start(deviceId); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось воспроизвести звук: " + ex.Message, Title);
                return;
            }
            InfoText.Text = "Слушайте внимательно. Нажмите «Слышу» (или пробел), как только появится звук.";
            MainButtonText.Text = "Слышу";
            NextFrequency();
            timer.Start();
            MainButton.Focus();
        }

        private void NextFrequency()
        {
            index++;
            if (index >= Freqs.Length) { Finish(); return; }
            level = StartDb;
            pausing = false;
            StepText.Text = $"Тон {index + 1} из {Freqs.Length} · {Freqs[index]:0} Гц";
            HighlightDot(index, "AccentBrush");
            player.Set(Freqs[index], level);
            UpdateLevelBar();
        }

        private void Tick()
        {
            if (index < 0 || index >= Freqs.Length) return;
            if (pausing) { NextFrequency(); return; }
            level += StepDb;
            if (level > MaxDb)
            {
                thresholds[index] = MaxDb + StepDb; // not heard at all
                MarkDone();
                return;
            }
            player.Set(Freqs[index], level);
            UpdateLevelBar();
        }

        private void Heard()
        {
            if (index < 0 || index >= Freqs.Length || pausing) return;
            thresholds[index] = level;
            MarkDone();
        }

        private void MarkDone()
        {
            player.Set(Freqs[index], null);
            HighlightDot(index, "GoodBrush");
            pausing = true; // short silence, then the next tone
        }

        private void HighlightDot(int i, string brushKey)
        {
            if (Dots.Items[i] is Border b)
            {
                b.Background = (Brush)FindResource(brushKey);
                ((TextBlock)b.Child).Foreground = Brushes.White;
            }
        }

        private void UpdateLevelBar()
        {
            double frac = (level - StartDb) / (MaxDb - StartDb);
            LevelFill.Width = Math.Clamp(frac, 0, 1) * (LevelFill.Parent as FrameworkElement)!.ActualWidth;
        }

        private void Finish()
        {
            timer.Stop();
            player.Stop();
            Result = ComputeGains();
            StepText.Text = "Готово";
            InfoText.Text = "Пресет «Мой слух» создан и включён. Его можно подправить вручную на вкладке «Эквалайзер».\n\n" +
                            "Поправки: " + string.Join("  ", EqBands.Labels.Select((l, i) => $"{l}: {(Result[i] > 0 ? "+" : "")}{Result[i]}"));
            MainButtonText.Text = "Применить";
            CancelButton.Visibility = Visibility.Collapsed;
            LevelFill.Width = 0;
        }

        /// <summary>Typical absolute threshold of hearing (Terhardt), dB SPL.</summary>
        private static double Ath(double hz)
        {
            double f = hz / 1000.0;
            return 3.64 * Math.Pow(f, -0.8) - 6.5 * Math.Exp(-0.6 * Math.Pow(f - 3.3, 2)) + 1e-3 * Math.Pow(f, 4);
        }

        private int[] ComputeGains()
        {
            int ref1k = Array.IndexOf(Freqs, 1000.0);
            double t1k = thresholds[ref1k] ?? -50;
            var gains = new int[EqBands.Count];
            for (int i = 0; i < Freqs.Length; i++)
            {
                double t = thresholds[i] ?? t1k;
                // how much less sensitive this ear is here compared with a typical ear (relative to 1 kHz)
                double deviation = (t - t1k) - (Ath(Freqs[i]) - Ath(1000));
                double gain = deviation * 0.5;                         // correct only half - sounds natural
                double maxBoost = Freqs[i] >= 16000 ? 6 : 9;
                gains[i + 1] = (int)Math.Round(Math.Clamp(gain, -6, maxBoost));
            }
            gains[0] = gains[1]; // 30 Hz follows 60 Hz
            return gains;
        }
    }
}
