using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphiteCompanion.Models;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace GraphiteCompanion.Services
{
    public sealed class TrayService : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private readonly WF.NotifyIcon icon;
        private SD.Icon appIcon;
        private SD.Icon mutedIcon;
        private SD.Color accentA = SD.Color.FromArgb(91, 140, 255), accentB = SD.Color.FromArgb(155, 107, 255);
        private bool micMuted;
        private readonly WF.ToolStripMenuItem micItem;
        private readonly WF.ToolStripMenuItem[] modeItems = new WF.ToolStripMenuItem[3];
        private readonly WF.ToolStripMenuItem[] padItems = new WF.ToolStripMenuItem[3];
        private readonly WF.ToolStripMenuItem eqItem;

        public event Action? OpenRequested;
        public event Action? MicMuteRequested;
        public event Action? ExitRequested;
        public event Action? ToggleEqRequested;
        public event Action<ListeningMode>? ModeRequested;
        public event Action<Earpad>? EarpadRequested;

        public TrayService()
        {
            appIcon = CreateIcon(false, accentA, accentB);
            mutedIcon = CreateIcon(true, accentA, accentB);
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => OpenRequested?.Invoke());
            menu.Items.Add(new WF.ToolStripSeparator());

            foreach (ListeningMode m in Enum.GetValues<ListeningMode>())
            {
                var mode = m;
                var item = new WF.ToolStripMenuItem(FactoryPresets.Name(mode), null, (_, _) => ModeRequested?.Invoke(mode));
                modeItems[(int)mode] = item;
                menu.Items.Add(item);
            }

            var pads = new WF.ToolStripMenuItem("Амбушюры");
            foreach (Earpad p in Enum.GetValues<Earpad>())
            {
                var pad = p;
                var item = new WF.ToolStripMenuItem(FactoryPresets.Name(pad), null, (_, _) => EarpadRequested?.Invoke(pad));
                padItems[(int)pad] = item;
                pads.DropDownItems.Add(item);
            }
            menu.Items.Add(pads);

            eqItem = new WF.ToolStripMenuItem("Эквалайзер включён", null, (_, _) => ToggleEqRequested?.Invoke());
            menu.Items.Add(eqItem);
            micItem = new WF.ToolStripMenuItem("Микрофон выключен", null, (_, _) => MicMuteRequested?.Invoke());
            menu.Items.Add(micItem);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

            icon = new WF.NotifyIcon
            {
                Icon = appIcon,
                Text = "SoundShot",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) OpenRequested?.Invoke(); };
        }

        public void UpdateState(Earpad pad, ListeningMode? mode, bool eqEnabled, string presetTitle)
        {
            for (int i = 0; i < modeItems.Length; i++) modeItems[i].Checked = mode.HasValue && (int)mode.Value == i;
            for (int i = 0; i < padItems.Length; i++) padItems[i].Checked = (int)pad == i;
            eqItem.Checked = eqEnabled;
            string text = $"SoundShot\n{presetTitle}" + (eqEnabled ? "" : " (EQ выкл.)");
            icon.Text = text.Length > 127 ? text[..127] : text;
        }

        public void Notify(string title, string text) =>
            icon.ShowBalloonTip(1500, title, text, WF.ToolTipIcon.None);

        public ImageSource CreateWindowIcon() =>
            Imaging.CreateBitmapSourceFromHIcon(appIcon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        public void SetMicMuted(bool muted)
        {
            micItem.Checked = muted;
            micMuted = muted;
            icon.Icon = muted ? mutedIcon : appIcon;
        }

        /// <summary>Recolours the tray / window icon to the selected accent.</summary>
        public void SetAccent(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            accentA = SD.Color.FromArgb(a.R, a.G, a.B);
            accentB = SD.Color.FromArgb(b.R, b.G, b.B);
            var oldApp = appIcon; var oldMuted = mutedIcon;
            appIcon = CreateIcon(false, accentA, accentB);
            mutedIcon = CreateIcon(true, accentA, accentB);
            icon.Icon = micMuted ? mutedIcon : appIcon;
            oldApp.Dispose(); oldMuted.Dispose();
        }

        private static SD.Icon CreateIcon(bool micMuted, SD.Color colorA, SD.Color colorB)
        {
            using var bmp = new SD.Bitmap(32, 32);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                using var bg = new SD.Drawing2D.LinearGradientBrush(
                    new SD.Rectangle(0, 0, 32, 32), colorA, colorB, 45f);
                using var path = RoundedRect(new SD.RectangleF(1, 1, 30, 30), 8);
                g.FillPath(bg, path);
                using var pen = new SD.Pen(SD.Color.White, 2.6f);
                g.DrawArc(pen, 8f, 7f, 16f, 16f, 180, 180);
                using var white = new SD.SolidBrush(SD.Color.White);
                using var left = RoundedRect(new SD.RectangleF(6.2f, 14.5f, 5.2f, 9.5f), 2.2f);
                using var right = RoundedRect(new SD.RectangleF(20.6f, 14.5f, 5.2f, 9.5f), 2.2f);
                g.FillPath(white, left);
                g.FillPath(white, right);
                if (micMuted)
                {
                    using var red = new SD.SolidBrush(SD.Color.FromArgb(255, 72, 96));
                    using var ring = new SD.Pen(SD.Color.FromArgb(18, 23, 34), 2f);
                    g.FillEllipse(red, 19f, 19f, 12f, 12f);
                    g.DrawEllipse(ring, 19f, 19f, 12f, 12f);
                }
            }
            IntPtr h = bmp.GetHicon();
            try { return (SD.Icon)SD.Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }

        private static SD.Drawing2D.GraphicsPath RoundedRect(SD.RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new SD.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            appIcon.Dispose();
            mutedIcon.Dispose();
        }
    }
}
