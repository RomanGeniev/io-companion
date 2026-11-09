using System.Collections.Generic;

namespace GraphiteCompanion.Models
{
    public enum Earpad { Fabric, Leatherette, Velour }

    public enum ListeningMode { Games, Music, Movies }

    public enum DeviceKind { Headphones, Speakers }

    /// <summary>Starting presets for speakers / monitor speakers / home theatre (no earpads).</summary>
    public static class SpeakerPresets
    {
        private static readonly System.Collections.Generic.Dictionary<ListeningMode, int[]> Map = new()
        {
            // Speakers lack deep bass and are heard from a distance: a bit of low end, clearer voices.
            [ListeningMode.Games]  = new[] { 0, 1, 1, 0, -1, 0, 2, 3, 2, 1 },
            [ListeningMode.Music]  = new[] { 3, 3, 2, 0, -1, 0, 1, 2, 2, 2 },
            [ListeningMode.Movies] = new[] { 5, 4, 2, 0, 0, 2, 3, 2, 1, 0 },
        };

        public static int[] Get(ListeningMode mode) => (int[])Map[mode].Clone();

        public static string Key(ListeningMode mode) => $"Speakers.{mode}";

        public const string Hint = "Пресеты под колонки: немного баса и разборчивые голоса. Амбушюры для колонок не нужны.";

        /// <summary>Guess for a device the user hasn't classified yet.</summary>
        public static DeviceKind Guess(string deviceName, bool hasCMediaEq)
        {
            if (hasCMediaEq) return DeviceKind.Headphones;
            string n = deviceName.ToLowerInvariant();
            string[] headphones = { "наушник", "headphone", "headset", "гарнитур", "graphite", "airpods", "buds", "hands-free" };
            return System.Array.Exists(headphones, h => n.Contains(h)) ? DeviceKind.Headphones : DeviceKind.Speakers;
        }
    }

    public static class EqBands
    {
        public const int Count = 10;
        public const int Min = -20;
        public const int Max = 20;
        public static readonly string[] Labels = { "30", "60", "120", "250", "500", "1K", "2K", "4K", "8K", "16K" };
    }

    /// <summary>
    /// Starting presets tuned by ear for each earpad type. These are not lab measurements -
    /// they are sensible starting points that the user can freely tweak.
    /// Bands: 30, 60, 120, 250, 500, 1K, 2K, 4K, 8K, 16K (dB).
    /// </summary>
    public static class FactoryPresets
    {
        private static readonly Dictionary<(Earpad, ListeningMode), int[]> Map = new()
        {
            // Fabric: breathable, leaks some bass, a bit brighter -> add lows back.
            [(Earpad.Fabric, ListeningMode.Games)]  = new[] { 0, 1, 0, -1, -1, 0, 2, 4, 3, 1 },
            [(Earpad.Fabric, ListeningMode.Music)]  = new[] { 4, 3, 2, 0, 0, 0, 1, 1, 0, 0 },
            [(Earpad.Fabric, ListeningMode.Movies)] = new[] { 6, 5, 3, 1, 0, 1, 2, 1, 0, -1 },

            // Leatherette: tight seal, strong bass, slightly darker -> tame lows, add detail.
            [(Earpad.Leatherette, ListeningMode.Games)]  = new[] { -3, -2, -2, -1, 0, 1, 3, 4, 3, 2 },
            [(Earpad.Leatherette, ListeningMode.Music)]  = new[] { 1, 1, 0, -1, 0, 0, 1, 2, 2, 1 },
            [(Earpad.Leatherette, ListeningMode.Movies)] = new[] { 3, 3, 2, 0, 0, 1, 2, 2, 1, 0 },

            // Velour: soft, warm, slightly muffled highs -> bring back air.
            [(Earpad.Velour, ListeningMode.Games)]  = new[] { -1, -1, -1, -1, 0, 1, 3, 5, 4, 2 },
            [(Earpad.Velour, ListeningMode.Music)]  = new[] { 2, 2, 1, 0, 0, 0, 1, 2, 3, 2 },
            [(Earpad.Velour, ListeningMode.Movies)] = new[] { 4, 4, 2, 0, 0, 1, 2, 2, 2, 1 },
        };

        public static int[] Get(Earpad earpad, ListeningMode mode) => (int[])Map[(earpad, mode)].Clone();

        public static string Key(Earpad earpad, ListeningMode mode) => $"{earpad}.{mode}";

        public static string Name(Earpad e) => e switch
        {
            Earpad.Fabric => "Ткань",
            Earpad.Leatherette => "Кожзам",
            _ => "Велюр",
        };

        public static string Name(ListeningMode m) => m switch
        {
            ListeningMode.Games => "Игры",
            ListeningMode.Music => "Музыка",
            _ => "Фильмы",
        };

        public static string Hint(Earpad e) => e switch
        {
            Earpad.Fabric => "Ткань дышит и пропускает часть баса — пресеты немного добавляют низы.",
            Earpad.Leatherette => "Кожзам плотно прилегает и усиливает бас — пресеты его сдерживают и добавляют деталей.",
            _ => "Велюр мягкий и чуть приглушает верха — пресеты возвращают воздух и детальность.",
        };
    }
}
