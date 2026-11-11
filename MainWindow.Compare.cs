using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // "Сравнить" (A/B without EQ and effects) and per-device mode / preset memory.
    public partial class MainWindow
    {
        private List<(Guid format, uint pid)>? compareRestore;

        public bool Comparing => compareRestore != null;

        /// <summary>Bypass on: EQ and all effects off until <see cref="CompareOff"/>.</summary>
        public void CompareOn()
        {
            if (Comparing || session?.Eq == null && session?.Effects == null) return;
            compareRestore = BeginNeutralSound();
            lastWrittenEnabled = null;
            ApplyToDevice();          // writes "EQ off" now that Comparing is true
            CompareText.Text = "Без обработки";
            NotifyChangeQuiet("Без эквалайзера и эффектов");
        }

        public void CompareOff()
        {
            if (!Comparing) return;
            EndNeutralSound(compareRestore!);
            compareRestore = null;
            CompareText.Text = "Сравнить";
            NotifyChangeQuiet(PresetTitleText());
        }

        public void ToggleCompare()
        {
            if (Comparing) CompareOff(); else CompareOn();
        }

        /// <summary>On-screen hint only while the window isn't the active one (the button text is enough otherwise).</summary>
        private void NotifyChangeQuiet(string text)
        {
            if (!IsActive) NotifyChange(text, "");
        }

        private void Compare_Down(object sender, MouseButtonEventArgs e)
        {
            CompareOn();
            CompareButton.CaptureMouse();
        }

        private void Compare_Up(object sender, MouseButtonEventArgs e)
        {
            CompareButton.ReleaseMouseCapture();
            CompareOff();
        }

        private void Compare_Leave(object sender, MouseEventArgs e)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) CompareOff();
        }

        // ===================== per-device profile =====================

        /// <summary>Loads the selected device's own mode / earpads / preset (if it has one yet).</summary>
        private void RestoreDeviceProfile()
        {
            if (session == null || !settings.DeviceProfiles.TryGetValue(session.Info.Id, out var p)) return;
            settings.Mode = p.Mode;
            settings.Earpad = p.Earpad;
            settings.ActiveCustom = p.ActiveCustom != null && settings.Custom.Exists(c => c.Name == p.ActiveCustom) ? p.ActiveCustom : null;
        }

        /// <summary>Remembers the current mode / earpads / preset for the selected device.</summary>
        private void StoreDeviceProfile()
        {
            // don't remember a mode that was switched on automatically for a program
            if (session == null) return;
            settings.DeviceProfiles[session.Info.Id] = new DeviceProfile
            {
                Mode = settings.Mode,
                Earpad = settings.Earpad,
                ActiveCustom = settings.ActiveCustom,
            };
        }
    }
}
