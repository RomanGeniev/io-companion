using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GraphiteCompanion.Controls
{
    /// <summary>Program icons for the mixer rows (taken from the .exe, cached), with a glyph as fallback.</summary>
    public static class AppIcons
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

        private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static ImageSource? Load(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (Cache.TryGetValue(exePath, out var cached)) return cached;
            ImageSource? img = null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    bmp.Freeze();
                    img = bmp;
                }
            }
            catch { }
            Cache[exePath] = img;
            return img;
        }

        /// <summary>A 28×28 rounded tile with the program's icon (or a speaker / app glyph).</summary>
        public static FrameworkElement Tile(string? exePath, bool systemSounds)
        {
            var box = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
            var img = systemSounds ? null : Load(exePath);
            if (img != null)
            {
                box.Child = new Image { Source = img, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(box.Child, BitmapScalingMode.HighQuality);
            }
            else
            {
                var glyph = new TextBlock
                {
                    Text = systemSounds ? "" : "",
                    FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                    FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                box.Child = glyph;
            }
            return box;
        }
    }
}
