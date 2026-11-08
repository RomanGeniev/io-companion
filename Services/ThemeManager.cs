using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace GraphiteCompanion.Services
{
    /// <summary>Light / dark theme and accent colour, switched live (all colours are DynamicResource).</summary>
    public static class ThemeManager
    {
        public sealed record AccentInfo(string Key, string Name, Color A, Color B, Color Solid);

        public static readonly AccentInfo[] Accents =
        {
            new("Violet",  "Фиолетовый", Hex("#5B8CFF"), Hex("#9B6BFF"), Hex("#6E97FF")),
            new("Red",     "Красный",    Hex("#FF4D6D"), Hex("#FF8A3D"), Hex("#FF5C7A")),
            new("Green",   "Зелёный",    Hex("#22C55E"), Hex("#14B8A6"), Hex("#34D399")),
            new("Cyan",    "Бирюзовый",  Hex("#06B6D4"), Hex("#3B82F6"), Hex("#22D3EE")),
            new("Orange",  "Оранжевый",  Hex("#F97316"), Hex("#F43F5E"), Hex("#FB923C")),
            new("Pink",    "Розовый",    Hex("#EC4899"), Hex("#8B5CF6"), Hex("#F472B6")),
        };

        public static AccentInfo Current { get; private set; } = Accents[0];
        public static bool IsLight { get; private set; }

        public static AccentInfo FindAccent(string? key) =>
            Array.Find(Accents, a => a.Key == key) ?? Accents[0];

        public static bool SystemUsesLight()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
            }
            catch { return false; }
        }

        /// <param name="theme">"Dark", "Light" or "System".</param>
        public static void Apply(string theme, string accentKey)
        {
            bool light = theme == "Light" || (theme == "System" && SystemUsesLight());
            IsLight = light;
            var accent = FindAccent(accentKey);
            Current = accent;
            var r = Application.Current.Resources;

            void Set(string key, string hex) => r[key] = Freeze(new SolidColorBrush(Hex(hex)));

            if (light)
            {
                Set("BgBrush", "#F3F5FA"); Set("CardBrush", "#FFFFFF"); Set("CardHoverBrush", "#F2F5FB");
                Set("CardBorderBrush", "#DDE3EE"); Set("InsetBrush", "#EEF1F7"); Set("TextBrush", "#141A26");
                Set("SubTextBrush", "#5B667A"); Set("TrackBrush", "#D7DDE8"); Set("GoodBrush", "#16A34A");
                Set("WarnBrush", "#B7791F"); Set("GhostBrush", "#EEF1F7"); Set("GhostHoverBrush", "#E3E8F2");
                Set("GhostPressedBrush", "#D6DDEB"); Set("MutedBgBrush", "#FFE3E8"); Set("MutedFgBrush", "#E0355A");
                Set("SwitchOffBrush", "#C5CDDB"); Set("PopupBrush", "#F7FFFFFF"); Set("BannerBrush", "#FFF4DD");
                Set("BannerBorderBrush", "#F2D492"); Set("ZeroLineBrush", "#C9D1DF");
                r["SelectedBrush"] = Freeze(new SolidColorBrush(Mix(Hex("#FFFFFF"), accent.Solid, 0.12)));
            }
            else
            {
                Set("BgBrush", "#0B0E14"); Set("CardBrush", "#141A26"); Set("CardHoverBrush", "#1A2132");
                Set("CardBorderBrush", "#232C3E"); Set("InsetBrush", "#0F141F"); Set("TextBrush", "#E8ECF4");
                Set("SubTextBrush", "#8A94A8"); Set("TrackBrush", "#222A3B"); Set("GoodBrush", "#3DDC97");
                Set("WarnBrush", "#F5A524"); Set("GhostBrush", "#1A2132"); Set("GhostHoverBrush", "#232C42");
                Set("GhostPressedBrush", "#2B3550"); Set("MutedBgBrush", "#3A2330"); Set("MutedFgBrush", "#FF7A90");
                Set("SwitchOffBrush", "#2A3347"); Set("PopupBrush", "#F2121722"); Set("BannerBrush", "#2A200C");
                Set("BannerBorderBrush", "#5C4512"); Set("ZeroLineBrush", "#2A3347");
                r["SelectedBrush"] = Freeze(new SolidColorBrush(Mix(Hex("#141A26"), accent.Solid, 0.16)));
            }

            r["AccentBrush"] = Freeze(new SolidColorBrush(accent.Solid));
            r["AccentBorderBrush"] = Freeze(new SolidColorBrush(WithAlpha(accent.Solid, 0x60)));
            r["AccentGradient"] = Freeze(new LinearGradientBrush(accent.A, accent.B, new Point(0, 0), new Point(1, 1)));
            r["CurveFillBrush"] = Freeze(new LinearGradientBrush(WithAlpha(accent.A, 0x55), WithAlpha(accent.B, 0x08), 90));
            r["SpectrumBrush"] = Freeze(new LinearGradientBrush(WithAlpha(accent.Solid, light ? (byte)0x55 : (byte)0x46), WithAlpha(accent.B, 0x0C), 90));

            try { Application.Current.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark; } catch { }
        }

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}
