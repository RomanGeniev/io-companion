using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    /// <summary>One action that can be started with a global hotkey.</summary>
    public sealed class HotkeyDef
    {
        public string Id { get; init; } = "";
        public string Group { get; init; } = "";
        public string Label { get; init; } = "";
        public (uint mods, uint key)[] Defaults { get; init; } = Array.Empty<(uint, uint)>();
        public Action Run { get; init; } = () => { };
        /// <summary>The combination in force right now (null = nothing free was found).</summary>
        public (uint mods, uint key)? Current { get; set; }
        public bool IsCustom { get; set; }
    }

    // Configurable global hotkeys.
    public partial class MainWindow
    {
        private List<HotkeyDef>? hotkeyDefs;

        public IReadOnlyList<HotkeyDef> HotkeyDefs => hotkeyDefs ??= BuildHotkeyDefs();

        private List<HotkeyDef> BuildHotkeyDefs()
        {
            const uint A = HotkeyService.Alt, C = HotkeyService.Ctrl, S = HotkeyService.Shift, CA = C | A, CSA = C | A | S;
            HotkeyDef Mode(ListeningMode m, int n) => new()
            {
                Id = "mode." + m, Group = "Режимы звука", Label = "Режим «" + FactoryPresets.Name(m) + "»",
                Defaults = new[] { (CA, (uint)(0x30 + n)), (CA, (uint)(0x6F + n)), (CSA, (uint)(0x30 + n)) },
                Run = () => SelectMode(m, notify: true),
            };
            return new List<HotkeyDef>
            {
                Mode(ListeningMode.Games, 1), Mode(ListeningMode.Music, 2), Mode(ListeningMode.Movies, 3),
                new() { Id = "pad", Group = "Режимы звука", Label = "Сменить амбушюры",
                        Defaults = new[] { (CA, 0x50u), (CSA, 0x50u) },
                        Run = () => SelectEarpad((Earpad)(((int)settings.Earpad + 1) % 3), notify: true) },
                new() { Id = "eq", Group = "Звук", Label = "Включить / выключить эквалайзер",
                        Defaults = new[] { (CA, 0x45u), (CSA, 0x45u) }, Run = () => ToggleEq(notify: true) },
                new() { Id = "compare", Group = "Звук", Label = "Сравнить с оригинальным звуком",
                        Defaults = new[] { (CA, 0x43u), (CSA, 0x43u) }, Run = ToggleCompare },
                new() { Id = "mic", Group = "Звук", Label = "Выключить / включить микрофон",
                        Defaults = new[] { (CA, 0x4Du), (CSA, 0x4Du), (CA, 0x7Bu) }, Run = () => ToggleMicMute(fromHotkey: true) },
                new() { Id = "replay", Group = "Запись экрана", Label = "Сохранить повтор (последние секунды)",
                        Defaults = new[] { (A, 0x79u), (CA, 0x79u) }, Run = () => _ = SaveReplay() },
                new() { Id = "record", Group = "Запись экрана", Label = "Начать / остановить запись",
                        Defaults = new[] { (A, 0x78u), (CA, 0x78u) }, Run = () => _ = ToggleRecording() },
                new() { Id = "shot", Group = "Запись экрана", Label = "Сделать скриншот",
                        Defaults = new[] { (A, 0x77u), (CA, 0x77u) }, Run = () => _ = TakeScreenshot() },
                new() { Id = "overlay", Group = "Панель", Label = "Открыть панель быстрых настроек",
                        Defaults = new[] { (CA, 0x47u), (CSA, 0x47u) }, Run = ToggleOverlay },
                new() { Id = "media.play", Group = "Музыка", Label = "Пауза / продолжить",
                        Defaults = new[] { (CA, 0x20u), (CSA, 0x20u), (CA, 0x26u) }, Run = () => _ = MediaCommand(MediaService.PlayPause, playPause: true) },
                new() { Id = "media.next", Group = "Музыка", Label = "Следующий трек",
                        Defaults = new[] { (CA, 0x27u), (CSA, 0x27u), (CA, 0x22u) }, Run = () => _ = MediaCommand(MediaService.Next, playPause: false) },
                new() { Id = "media.prev", Group = "Музыка", Label = "Предыдущий трек",
                        Defaults = new[] { (CA, 0x25u), (CSA, 0x25u), (CA, 0x21u) }, Run = () => _ = MediaCommand(MediaService.Previous, playPause: false) },
            };
        }

        // ---------------- text <-> combination ----------------

        public static string FormatHotkey(uint mods, uint vk)
        {
            var parts = new List<string>();
            if ((mods & HotkeyService.Ctrl) != 0) parts.Add("Ctrl");
            if ((mods & HotkeyService.Shift) != 0) parts.Add("Shift");
            if ((mods & HotkeyService.Alt) != 0) parts.Add("Alt");
            if ((mods & HotkeyService.Win) != 0) parts.Add("Win");
            string key = KeyInterop.KeyFromVirtualKey((int)vk).ToString();
            if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key[1].ToString();
            parts.Add(key switch { "Return" => "Enter", "Prior" => "PageUp", "Next" => "PageDown", "Oem3" => "~", _ => key });
            return string.Join(" + ", parts);
        }

        private static bool ParseHotkey(string? s, out (uint mods, uint key) combo)
        {
            combo = default;
            var p = (s ?? "").Split(':');
            if (p.Length != 2 || !uint.TryParse(p[0], out var m) || !uint.TryParse(p[1], out var k) || k == 0) return false;
            combo = (m, k);
            return true;
        }

        /// <summary>"Win + G" when the keyboard hook owns it, otherwise the configured combination.</summary>
        public string OverlayKeyText() => winG?.Active == true ? "Win + G" : HotkeyLabel("overlay");

        public string HotkeyLabel(string id)
        {
            var d = HotkeyDefs.FirstOrDefault(x => x.Id == id);
            return d?.Current is { } c ? FormatHotkey(c.mods, c.key) : "не назначено";
        }

        // ---------------- registering ----------------

        /// <summary>Registers every hotkey (user's own combination, else the first free default) and refreshes all hint texts.</summary>
        private void ApplyHotkeys()
        {
            hotkeys.Clear();
            Resolve(register: true);
            UpdateHotkeyTexts();
        }

        /// <summary>
        /// Works out the combination of every action. With register=false nothing is registered
        /// (the settings window keeps hotkeys off while a key is being recorded) - availability is only probed.
        /// </summary>
        private void Resolve(bool register)
        {
            var taken = new HashSet<(uint, uint)>();
            bool Take((uint mods, uint key) c, HotkeyDef d)
            {
                if (register) return hotkeys.Register(c.mods, c.key, d.Run);
                if (taken.Contains(c) || !hotkeys.IsFree(c.mods, c.key)) return false;
                taken.Add(c);
                return true;
            }

            foreach (var d in HotkeyDefs)
            {
                d.Current = null;
                d.IsCustom = false;
                if (settings.Hotkeys.TryGetValue(d.Id, out var s) && ParseHotkey(s, out var own) && Take(own, d))
                {
                    d.Current = own;
                    d.IsCustom = true;
                    continue;
                }
                foreach (var def in d.Defaults)
                {
                    if (!Take(def, d)) continue;
                    d.Current = def;
                    break;
                }
            }
        }

        private void UpdateHotkeyTexts()
        {
            string L(string id) => HotkeyLabel(id);
            HkGames.Text = L("mode.Games");
            HkMusic.Text = L("mode.Music");
            HkMovies.Text = L("mode.Movies");
            EqSwitch.ToolTip = L("eq");
            MicHotkeyText.Text = $"{L("mic")} — выключить / включить микрофон из любой программы, даже в игре.";
            CompareButton.ToolTip = $"Держите — звук без эквалайзера и эффектов, отпустите — снова ваш. {L("compare")} — переключить";
            replayKey = L("replay"); recordKey = L("record"); shotKey = L("shot");
            RecHotkeys.Text = $"{replayKey} — сохранить повтор  ·  {recordKey} — начать / остановить запись  ·  {shotKey} — скриншот. " +
                              "Работают и из игры. Файлы лежат по мониторам: «Монитор N\\Видео\\Игра» и «Монитор N\\Фото\\Игра».";

            hotkeyLine = $"{L("pad")} — амбушюры  ·  {L("eq")} — эквалайзер  ·  {L("mic")} — микрофон  ·  {L("compare")} — сравнить";
            string over = L("overlay");
            if (winG?.Active == true) over = "Win + G  (или " + over + ")";
            SetOverlayHotkeyLabel(over);
        }

        // ---------------- settings window ----------------

        public void OpenHotkeysWindow()
        {
            ShowFromTray();
            hotkeys.Clear();          // otherwise a pressed combination would trigger its action instead of being recorded
            Resolve(register: false);
            try { new HotkeysWindow(this).ShowDialog(); }
            finally { ApplyHotkeys(); }
        }

        /// <summary>Assigns a new combination. Returns an error text for the user, or null on success.</summary>
        public string? TrySetHotkey(HotkeyDef def, uint mods, uint key)
        {
            bool fKey = key >= 0x70 && key <= 0x87;
            if (mods == 0 && !fKey) return "Добавьте Ctrl, Alt или Shift — иначе клавиша перестанет работать в обычном вводе.";
            var other = HotkeyDefs.FirstOrDefault(d => d != def && d.Current is { } c && c.mods == mods && c.key == key);
            if (other != null) return $"Это сочетание уже занято: «{other.Label}».";
            if (!hotkeys.IsFree(mods, key)) return "Это сочетание занято Windows или другой программой. Выберите другое.";
            settings.Hotkeys[def.Id] = $"{mods}:{key}";
            ScheduleSave();
            Resolve(register: false);
            return null;
        }

        public void ResetHotkey(HotkeyDef def)
        {
            settings.Hotkeys.Remove(def.Id);
            ScheduleSave();
            Resolve(register: false);
        }

        public void ResetAllHotkeys()
        {
            settings.Hotkeys.Clear();
            ScheduleSave();
            Resolve(register: false);
        }

        private void Hotkeys_Click(object sender, System.Windows.RoutedEventArgs e) => OpenHotkeysWindow();
    }
}
