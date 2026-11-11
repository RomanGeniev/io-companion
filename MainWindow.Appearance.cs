using System;
using System.Windows;
using System.Windows.Automation;
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
            if (settings.NavigationLayoutVersion < 1)
            {
                settings.SidebarCollapsed = false;
                settings.NavigationLayoutVersion = 1;
            }
            ApplySidebarLayout();
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
            RefreshCustomAccent();
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

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            settings.SidebarCollapsed = !settings.SidebarCollapsed;
            ApplySidebarLayout(animate: true);
            ScheduleSave();
        }

        private void ApplySidebarLayout(bool animate = false)
        {
            bool collapsed = settings.SidebarCollapsed;
            var visibility = collapsed ? Visibility.Hidden : Visibility.Visible;
            ((FrameworkElement)BrandSubtitle.Parent).Visibility = visibility;
            NavigationHint.Visibility = visibility;
            foreach (var child in SidebarNavigation.Children)
            {
                if (child is TextBlock label) label.Visibility = visibility;
                if (child is RadioButton { Content: StackPanel content } && content.Children.Count > 1)
                    content.Children[1].Visibility = visibility;
            }
            string action = collapsed ? "Развернуть навигацию" : "Свернуть навигацию";
            SidebarToggle.ToolTip = action;
            SidebarToggle.Content = collapsed ? "\uE76C" : "\uE76B";
            AutomationProperties.SetName(SidebarToggle, action);

            double from = SidebarHost.Width;
            double to = collapsed ? 58 : 208;
            SidebarHost.BeginAnimation(WidthProperty, null);
            SidebarHost.Width = to;
            if (animate && SystemParameters.ClientAreaAnimation && from != to)
            {
                var duration = TimeSpan.FromMilliseconds(190);
                SidebarHost.BeginAnimation(WidthProperty, new DoubleAnimation(from, to, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            }
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (!initialized) return;
            settings.Theme = (string)((FrameworkElement)sender).Tag;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            if (shutdownStarted) return;
            // switching the Fluent theme re-creates control templates, which resets scroll positions;
            // remember them so the window stays where the user was
            var scrollers = new[] { SidebarScroll, EffectsScroll, PageMic, PageRecord, PresetScroll, PageSettings };
            var offsets = Array.ConvertAll(scrollers, s => s.VerticalOffset);
            void RestoreScroll()
            {
                for (int i = 0; i < scrollers.Length; i++) scrollers[i].ScrollToVerticalOffset(offsets[i]);
            }

            ThemeManager.Apply(settings.Theme, settings.Accent);
            RefreshCustomAccent();
            var accent = ThemeManager.Current;
            tray.SetAccent(accent.A, accent.B);
            Icon = tray.CreateWindowIcon();

            // controls created in code took their colours at creation time - rebuild them
            OutputEffectsGrid.Children.Clear();
            MicEffectsGrid.Children.Clear();
            outputCards.Clear();
            micCards.Clear();
            BuildEffectCards();
            BuildMonitorRows();
            FlushEffects();
            ReloadEffects();
            osd?.Close();
            osd = null;

            RestoreScroll();
            Dispatcher.BeginInvoke(new Action(RestoreScroll), System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(RestoreScroll), System.Windows.Threading.DispatcherPriority.ContextIdle);

            // soft cross-fade of the whole window
            if (Content is UIElement root)
                root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(260))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            ScheduleSave();
        }

        private void RefreshCustomAccent()
        {
            var color = ThemeManager.FindAccent(settings.CustomAccent).Solid;
            CustomAccentBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            CustomAccentPreview.Background = new SolidColorBrush(color);
            CustomAccentHint.Text = ThemeManager.TryCustomColor(settings.Accent, out _)
                ? "Используется ваш цвет. Он сохранится после перезапуска."
                : "Выберите цвет в палитре или введите код #RRGGBB.";
        }

        private void SetCustomAccent(string value)
        {
            if (!ThemeManager.TryCustomColor(value, out var color))
            {
                CustomAccentHint.Text = "Введите 6 шестнадцатеричных цифр после #. Например, #7C5CFC.";
                return;
            }
            settings.CustomAccent = settings.Accent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            foreach (var child in AccentPanel.Children)
                if (child is RadioButton dot) dot.IsChecked = false;
            ApplyTheme();
        }

        private void ChooseCustomAccent_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Controls.AccentColorWindow(this, ThemeManager.FindAccent(settings.CustomAccent).Solid);
            if (dialog.ShowDialog() == true) SetCustomAccent(dialog.SelectedHex);
        }

        private void ApplyCustomAccent_Click(object sender, RoutedEventArgs e) => SetCustomAccent(CustomAccentBox.Text);
        private void CustomAccentBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { SetCustomAccent(CustomAccentBox.Text); e.Handled = true; }
        }

        /// <summary>Slide-up + fade-in for the page that just became visible.</summary>
        private static void AnimatePage(FrameworkElement page)
        {
            var shift = new TranslateTransform();
            page.RenderTransform = shift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(230);
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
    }
}
