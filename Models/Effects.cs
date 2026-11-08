using System;
using GraphiteCompanion.Models;

namespace GraphiteCompanion.Models
{
    public sealed record EffectOption(string Label, int Value);

    public sealed record EffectParam(
        string Label, uint Pid, int Min, int Max, int Default,
        string Unit = "", int Step = 1, EffectOption[]? Options = null);

    public sealed record EffectDef(
        string Title, string Description, string Icon,
        Guid Format, uint EnablePid, bool DefaultEnabled, EffectParam[] Params);

    /// <summary>
    /// C-Media Xear effects available on io Graphite 2 Pro. Property keys were mapped by
    /// recording what the official Audio Center writes for every control.
    /// </summary>
    public static class Effects
    {
        private static readonly Guid Xear = new("5e96e4d9-ff2c-4d84-9bd6-fe10cf815009");
        private static readonly Guid Surround = new("c020cdbf-756f-4b6d-9031-be6e20cf1c9f");
        private static readonly Guid NoiseReduction = new("d9d2c54d-289d-4bf2-8bef-3270c977ae33");
        private static readonly Guid MagicVoice = new("3bc6329a-2397-4ff3-92f5-2cd9894bb889");

        public static readonly EffectDef[] Output =
        {
            new("Объёмный звук 7.1",
                "Виртуальный surround: лучше слышно, откуда идёт звук в играх и фильмах.",
                "", Surround, 0, false, new[]
                {
                    new EffectParam("Режим", 5, 0, 1, 1, Options: new[]
                    {
                        new EffectOption("Музыка / игры", 1),
                        new EffectOption("Фильмы", 0),
                    }),
                    new EffectParam("Размер комнаты", 2, 0, 2, 0, Options: new[]
                    {
                        new EffectOption("Малая", 2),
                        new EffectOption("Средняя", 1),
                        new EffectOption("Большая", 0),
                    }),
                }),

            new("Audio Brilliant",
                "Возвращает детальность и «воздух» сжатой музыке, стримам и видео.",
                "", Xear, 0, false, new[]
                {
                    new EffectParam("Уровень", 1, 0, 10, 2),
                }),

            new("Dynamic Bass",
                "Виртуальный бас: больше глубины низов без гула.",
                "", Xear, 5, false, new[]
                {
                    new EffectParam("Уровень баса", 7, 0, 10, 3),
                    new EffectParam("Частота среза", 8, 100, 400, 100, " Гц", 10),
                }),

            new("Smart Volume",
                "Выравнивает громкость: тихое громче, резкие пики тише.",
                "", Xear, 9, false, new[]
                {
                    new EffectParam("Уровень", 10, 0, 100, 50),
                    new EffectParam("Режим", 11, 0, 1, 0, Options: new[]
                    {
                        new EffectOption("Музыка", 0),
                        new EffectOption("Речь", 1),
                    }),
                }),

            new("Voice Clarity",
                "Делает голоса в звонках и видео разборчивее, приглушает фоновый шум.",
                "", Xear, 2, false, new[]
                {
                    new EffectParam("Уровень", 3, 0, 10, 3),
                    new EffectParam("Подавление шума", 4, 0, 20, 0, " дБ"),
                }),
        };

        public static string Key(Guid format, uint pid) => $"{format}|{pid}";

        public static (Guid format, uint pid) ParseKey(string key)
        {
            var parts = key.Split('|');
            return (Guid.Parse(parts[0]), uint.Parse(parts[1]));
        }

        /// <summary>All output effects off with standard levels.</summary>
        public static System.Collections.Generic.Dictionary<string, int> DefaultSnapshot()
        {
            var d = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var def in Output)
            {
                d[Key(def.Format, def.EnablePid)] = def.DefaultEnabled ? 1 : 0;
                foreach (var p in def.Params) d[Key(def.Format, p.Pid)] = p.Default;
            }
            return d;
        }

        /// <summary>Sensible effect combination for each listening mode (used until the user changes it).</summary>
        public static System.Collections.Generic.Dictionary<string, int> ModeDefaults(ListeningMode mode)
        {
            var d = DefaultSnapshot();
            switch (mode)
            {
                case ListeningMode.Games:
                    // No virtual surround: in competitive games (CS2, Dota 2, Valorant) it blurs
                    // positional cues; CS2 has its own HRTF. Users can enable 7.1 for single-player games.
                    d[Key(Surround, 5)] = 1;   // keep "music / gaming" as the preferred 7.1 mode
                    d[Key(Surround, 2)] = 1;   // medium room if the user turns 7.1 on
                    break;
                case ListeningMode.Movies:
                    d[Key(Surround, 0)] = 1;   // 7.1 on
                    d[Key(Surround, 5)] = 0;   // movie mode
                    d[Key(Surround, 2)] = 0;   // large room
                    d[Key(Xear, 5)] = 1;       // dynamic bass on
                    d[Key(Xear, 7)] = 4;
                    d[Key(Xear, 8)] = 100;
                    break;
            }
            return d;
        }

        public static readonly EffectDef[] Microphone =
        {
            new("AI шумоподавление",
                "Нейросеть убирает клавиатуру, вентиляторы и фоновый шум из микрофона.",
                "", NoiseReduction, 1, true, Array.Empty<EffectParam>()),

            new("Magic Voice",
                "Изменение голоса в микрофоне — для веселья в играх и звонках.",
                "", MagicVoice, 2, false, new[]
                {
                    new EffectParam("Голос", 3, 0, 3, 3, Options: new[]
                    {
                        new EffectOption("Монстр", 0),
                        new EffectOption("Мульт", 1),
                        new EffectOption("Мужской", 2),
                        new EffectOption("Женский", 3),
                    }),
                }),
        };
    }
}
