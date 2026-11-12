using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GraphiteCompanion.Models;

namespace GraphiteCompanion.Controls
{
    /// <summary>A card with an on/off switch and parameter controls built from an <see cref="EffectDef"/>.</summary>
    public sealed class EffectCard : Border
    {
        public event Action<Guid, uint, int>? Changed;

        public EffectDef Definition { get; }

        private readonly CheckBox enableSwitch;
        private readonly TextBlock effectIcon;
        private readonly StackPanel body = new();
        private readonly Dictionary<uint, Slider> sliders = new();
        private readonly Dictionary<uint, TextBlock> valueTexts = new();
        private readonly Dictionary<uint, RadioButton[]> optionButtons = new();
        private bool loading;

        private static object Res(string key) => Application.Current.FindResource(key);

        public EffectCard(EffectDef def)
        {
            Definition = def;
            Style = (Style)Res("Card");
            Margin = new Thickness(6);
            Padding = new Thickness(12);

            var root = new StackPanel();
            Child = root;

            // Header: icon, title, description, switch
            var header = new Grid();
            System.Windows.Automation.AutomationProperties.SetName(header,"Включение эффекта");
            System.Windows.Automation.AutomationProperties.SetName(body,"Параметры эффекта");
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            effectIcon = new TextBlock {
                Text = def.Icon, FontFamily = (FontFamily)Res("IconFont"), FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            effectIcon.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush");
            var iconBox = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(10),
                Background = (Brush)Res("InsetBrush"), VerticalAlignment = VerticalAlignment.Top,
                Child = effectIcon,
            };
            header.Children.Add(iconBox);

            var titles = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
            titles.Children.Add(new TextBlock { Text = def.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            HelpTips.Set(titles, def.Description + " Включите эффект и сравните звук на знакомой записи.");
            Grid.SetColumn(titles, 1);
            header.Children.Add(titles);

            enableSwitch = new CheckBox { Style = (Style)Res("Switch"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            HelpTips.Set(enableSwitch, def.Title + ". " + def.Description + " Включите и выключите на одном и том же фрагменте, чтобы сравнить результат. Положение регуляторов сохраняется; при выключенном эффекте они не влияют на звук.");
            void OnToggle(object s, RoutedEventArgs e)
            {
                UpdateLook();
                if (!loading) Changed?.Invoke(def.Format, def.EnablePid, enableSwitch.IsChecked == true ? 1 : 0);
            }
            enableSwitch.Checked += OnToggle;
            enableSwitch.Unchecked += OnToggle;
            Grid.SetColumn(enableSwitch, 2);
            header.Children.Add(enableSwitch);
            root.Children.Add(header);

            if (def.Params.Length > 0)
            {
                body.Margin = new Thickness(0, 6, 0, 0);
                root.Children.Add(body);
                foreach (var p in def.Params) AddParam(p);
            }
            UpdateLook();
        }

        private void AddParam(EffectParam p)
        {
            string explanation = p.Label switch
            {
                "Частота среза" => "Задаёт границу частот, с которыми работает басовый эффект. Меняйте понемногу и проверяйте, не появляется ли гул.",
                "Подавление шума" => "Меняет степень ослабления фонового шума. Сильная обработка может изменить и полезный звук — сравните с выключенным эффектом.",
                "Размер комнаты" => "Меняет ощущение пространства у виртуального объёмного звука. Выберите вариант, в котором направление звуков воспринимается понятнее.",
                "Уровень баса" => "Интенсивность дополнительного баса. Большее значение сильнее подчёркивает низкие звуки; уменьшите его, если бас заглушает речь.",
                "Голос" => "Вариант изменения голоса. Для проверки включите прослушивание микрофона в наушниках.",
                "Режим" => "Вариант работы этого эффекта. Выберите подходящий тип материала и сравните на знакомом фрагменте.",
                _ => "Интенсивность этого эффекта. Меняйте небольшими шагами и сравнивайте результат на одном и том же фрагменте.",
            };
            explanation = Definition.Title + ": " + p.Label + ". " + explanation + " Применяется, только когда эффект включён.";
            var unit = new StackPanel(); body.Children.Add(unit);
            var labelRow = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            labelRow.Children.Add(new TextBlock { Text = p.Label, FontSize = 12, Foreground = (Brush)Res("SubTextBrush") });
            unit.Children.Add(labelRow);

            if (p.Options != null)
            {
                var grid = new UniformGrid { Columns = p.Options.Length };
                var group = $"{Definition.Format}.{p.Pid}.{Guid.NewGuid():N}";
                var buttons = p.Options.Select(o =>
                {
                    var rb = new RadioButton { Style = (Style)Res("Segment"), Content = o.Label, Tag = o.Value, GroupName = group };
                    HelpTips.Set(rb, o.Label + ". " + explanation);
                    rb.Checked += (_, _) => { if (!loading) Changed?.Invoke(Definition.Format, p.Pid, o.Value); };
                    grid.Children.Add(rb);
                    return rb;
                }).ToArray();
                optionButtons[p.Pid] = buttons;
                unit.Children.Add(new Border
                {
                    Background = (Brush)Res("InsetBrush"), CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(4), Child = grid,
                });
                return;
            }

            var value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            labelRow.Children.Add(value);
            valueTexts[p.Pid] = value;

            var slider = new Slider
            {
                Style = (Style)Res("VolumeSlider"),
                Minimum = p.Min, Maximum = p.Max,
                SmallChange = p.Step, LargeChange = p.Step,
                TickFrequency = p.Step, IsSnapToTickEnabled = true,
            };
            HelpTips.Set(slider, explanation);
            slider.ValueChanged += (_, e) =>
            {
                int v = (int)Math.Round(e.NewValue);
                value.Text = v + p.Unit;
                if (!loading) Changed?.Invoke(Definition.Format, p.Pid, v);
            };
            slider.PreviewMouseWheel += (s, e) =>
            {
                var sl = (Slider)s;
                sl.Value = Math.Clamp(sl.Value + (e.Delta > 0 ? p.Step : -p.Step), sl.Minimum, sl.Maximum);
                e.Handled = true;
            };
            sliders[p.Pid] = slider;
            unit.Children.Add(slider);
        }

        /// <summary>Shows the values currently stored in the driver (or defaults when missing).</summary>
        public void Load(Func<Guid, uint, int?> read)
        {
            loading = true;
            try
            {
                enableSwitch.IsChecked = (read(Definition.Format, Definition.EnablePid) ?? (Definition.DefaultEnabled ? 1 : 0)) != 0;
                foreach (var p in Definition.Params) SetParam(p, read(Definition.Format, p.Pid) ?? p.Default);
            }
            finally
            {
                loading = false;
            }
            UpdateLook();
        }

        /// <summary>Puts factory defaults into the UI and reports every value through <see cref="Changed"/>.</summary>
        public void ResetToDefaults()
        {
            Load((_, _) => null);
            Changed?.Invoke(Definition.Format, Definition.EnablePid, Definition.DefaultEnabled ? 1 : 0);
            foreach (var p in Definition.Params) Changed?.Invoke(Definition.Format, p.Pid, p.Default);
        }

        private void SetParam(EffectParam p, int v)
        {
            if (optionButtons.TryGetValue(p.Pid, out var buttons))
            {
                foreach (var b in buttons) b.IsChecked = (int)b.Tag == v;
            }
            else if (sliders.TryGetValue(p.Pid, out var s))
            {
                s.Value = Math.Clamp(v, p.Min, p.Max);
                valueTexts[p.Pid].Text = (int)s.Value + p.Unit;
            }
        }

        public void SetAvailable(bool available)
        {
            IsEnabled = available;
            Opacity = available ? 1 : 0.45;
        }

        private void UpdateLook()
        {
            // Parameters remain editable while bypassed: keep their names and stored
            // values readable. The switch and icon carry the enabled state instead.
            body.Opacity = 1;
            effectIcon.SetResourceReference(TextBlock.ForegroundProperty,enableSwitch.IsChecked == true ? "AccentBrush" : "SubTextBrush");
        }
    }
}
