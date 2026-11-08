using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GraphiteCompanion.Services;
using Microsoft.Win32;

namespace GraphiteCompanion
{
    // Theme (dark / light / system), accent colour and small UI animations.
    public partial class MainWindow
    {
        private void InitAppearance()
        {
            foreach (var a in ThemeManager.Accents)
            {
                var dot = new RadioButton
                {
                    GroupName = "Accent", Tag = a.Key, ToolTip = a.Name,
                    Background = new LinearGradientBrush(a.A, a.B, new Point(0, 0), new Point(1, 1)),
                    IsChecked = a.Key == ThemeManager.Current.Key,
                };
                dot.SetResourceReference(StyleProperty, "AccentDot");
                System.Windows.Automation.AutomationProperties.SetName(dot, a.Name);
                dot.Checked += (_, _) => { if (initialized) { settings.Accent = a.Key; ApplyTheme(); } };
                AccentPanel.Children.Add(dot);
            }
            ThemeDark.IsChecked = settings.Theme == "Dark";
            ThemeLight.IsChecked = settings.Theme == "Light";
            ThemeSystem.IsChecked = settings.Theme == "System";
            var accent = ThemeManager.Current;
            tray.SetAccent(accent.A, accent.B);
            Icon = tray.CreateWindowIcon();

            // follow Windows light/dark switch when "Как в Windows" is selected
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && settings.Theme == "System")
                    Dispatcher.BeginInvoke(new Action(ApplyTheme));
            };
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (!initialized) return;
            settings.Theme = (string)((FrameworkElement)sender).Tag;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            // switching the Fluent theme re-creates control templates, which resets scroll positions;
            // remember them so the window stays where the user was
            var scrollers = new[] { SidebarScroll, EffectsScroll, PageMic, PageAuto };
            var offsets = Array.ConvertAll(scrollers, s => s.VerticalOffset);
            void RestoreScroll()
            {
                for (int i = 0; i < scrollers.Length; i++) scrollers[i].ScrollToVerticalOffset(offsets[i]);
            }

            ThemeManager.Apply(settings.Theme, settings.Accent);
            var accent = ThemeManager.Current;
            tray.SetAccent(accent.A, accent.B);
            Icon = tray.CreateWindowIcon();

            // controls created in code took their colours at creation time - rebuild them
            OutputEffectsGrid.Children.Clear();
            MicEffectsGrid.Children.Clear();
            outputCards.Clear();
            micCards.Clear();
            BuildEffectCards();
            pendingEffects.Clear();
            ReloadEffects();
            RebuildRules();
            osd?.Close();
            osd = null;

            RestoreScroll();
            Dispatcher.BeginInvoke(new Action(RestoreScroll), System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(RestoreScroll), System.Windows.Threading.DispatcherPriority.ContextIdle);

            // soft cross-fade of the whole window
            if (Content is UIElement root)
                root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(260))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            ScheduleSave();
        }

        /// <summary>Slide-up + fade-in for the page that just became visible.</summary>
        private static void AnimatePage(FrameworkElement page)
        {
            var shift = new TranslateTransform(0, 14);
            page.RenderTransform = shift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(230);
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
        }
    }
}
