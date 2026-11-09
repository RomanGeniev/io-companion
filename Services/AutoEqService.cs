using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GraphiteCompanion.Models;

namespace GraphiteCompanion.Services
{
    public sealed record AutoEqEntry(string Name, string Path, string Source)
    {
        public string Display => $"{Name}  ·  {Source}";
    }

    /// <summary>
    /// Headphone correction presets from the open AutoEq project (MIT, github.com/jaakkopasanen/AutoEq).
    /// Its "FixedBandEQ" profiles use the same 10 bands (31 Hz … 16 kHz) as our equalizer.
    /// The index is cached for a week in %APPDATA%\GraphiteCompanion.
    /// </summary>
    public static class AutoEqService
    {
        private const string Raw = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";
        private static readonly string CacheFile = System.IO.Path.Combine(SettingsStore.Folder, "autoeq-index.md");
        private static readonly Regex Line = new(@"^- \[(?<name>.+?)\]\(\./(?<path>[^)]+)\) by (?<src>.+?)\s*$", RegexOptions.Compiled);

        private static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("IO-Companion");
            return c;
        }

        public static async Task<List<AutoEqEntry>> LoadIndexAsync()
        {
            string? text = null;
            try
            {
                if (File.Exists(CacheFile) && DateTime.Now - File.GetLastWriteTime(CacheFile) < TimeSpan.FromDays(7))
                    text = await File.ReadAllTextAsync(CacheFile);
            }
            catch { }
            if (text == null)
            {
                try
                {
                    using var http = Client();
                    text = await http.GetStringAsync(Raw + "INDEX.md");
                    Directory.CreateDirectory(SettingsStore.Folder);
                    await File.WriteAllTextAsync(CacheFile, text);
                }
                catch when (File.Exists(CacheFile))
                {
                    text = await File.ReadAllTextAsync(CacheFile); // offline: use the old copy
                }
            }
            var list = new List<AutoEqEntry>();
            foreach (var raw in text.Split('\n'))
            {
                var m = Line.Match(raw.TrimEnd('\r'));
                if (m.Success) list.Add(new AutoEqEntry(m.Groups["name"].Value, m.Groups["path"].Value, m.Groups["src"].Value));
            }
            return list;
        }

        /// <summary>Downloads the 10-band profile and converts it to our bands (rounded to 1 dB).</summary>
        public static async Task<int[]> LoadBandsAsync(AutoEqEntry entry)
        {
            string fileName = Uri.UnescapeDataString(entry.Path.Split('/').Last()) + " FixedBandEQ.txt";
            string url = Raw + entry.Path + "/" + Uri.EscapeDataString(fileName);
            using var http = Client();
            string text = await http.GetStringAsync(url);

            var gains = new List<double>();
            foreach (Match m in Regex.Matches(text, @"Gain\s+(-?[0-9.]+)\s*dB"))
                gains.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (gains.Count != EqBands.Count) throw new InvalidDataException("Неожиданный формат профиля AutoEq");
            return gains.Select(g => (int)Math.Clamp(Math.Round(g), EqBands.Min, EqBands.Max)).ToArray();
        }

        /// <summary>Simple search: every word of the query must appear in the name.</summary>
        public static IEnumerable<AutoEqEntry> Search(IEnumerable<AutoEqEntry> all, string query)
        {
            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return Enumerable.Empty<AutoEqEntry>();
            return all.Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                      .OrderBy(e => e.Name.Length).ThenBy(e => e.Name).Take(200);
        }
    }
}
