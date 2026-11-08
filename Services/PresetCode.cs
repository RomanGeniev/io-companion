using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphiteCompanion.Models;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// Packs an EQ curve (+ optional effect set) into a short shareable code like
    /// "IO-1A2B-3C4D-…". Crockford Base32, version byte and CRC-8, so typos are detected.
    /// </summary>
    public static class PresetCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const byte Version = 1;

        public static string Encode(int[] bands, Dictionary<string, int>? effects)
        {
            var data = new List<byte> { Version, (byte)(effects != null ? 1 : 0) };
            foreach (var b in bands) data.Add((byte)(Math.Clamp(b, EqBands.Min, EqBands.Max) - EqBands.Min));
            if (effects != null)
            {
                foreach (var def in Effects.Output)
                {
                    data.Add((byte)(Get(effects, def.Format, def.EnablePid, def.DefaultEnabled ? 1 : 0) != 0 ? 1 : 0));
                    foreach (var p in def.Params)
                    {
                        int v = Get(effects, def.Format, p.Pid, p.Default);
                        data.Add((byte)(p.Options != null ? v : (Math.Clamp(v, p.Min, p.Max) - p.Min) / p.Step));
                    }
                }
            }
            data.Add(Crc8(data));
            string s = ToBase32(data.ToArray());
            var sb = new StringBuilder("IO");
            for (int i = 0; i < s.Length; i += 4) sb.Append('-').Append(s.AsSpan(i, Math.Min(4, s.Length - i)));
            return sb.ToString();
        }

        /// <summary>Returns null when the code is not valid (typo, wrong version, damaged).</summary>
        public static (int[] bands, Dictionary<string, int>? effects)? Decode(string code)
        {
            try
            {
                string s = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
                if (s.StartsWith("IO")) s = s[2..];
                s = s.Replace('O', '0').Replace('I', '1').Replace('L', '1');
                var data = FromBase32(s);
                if (data.Length < 2 + EqBands.Count + 1 || data[0] != Version) return null;
                if (Crc8(data.Take(data.Length - 1)) != data[^1]) return null;

                int pos = 2;
                var bands = new int[EqBands.Count];
                for (int i = 0; i < bands.Length; i++) bands[i] = data[pos++] + EqBands.Min;
                if (bands.Any(b => b > EqBands.Max)) return null;

                Dictionary<string, int>? effects = null;
                if ((data[1] & 1) != 0)
                {
                    effects = new Dictionary<string, int>();
                    foreach (var def in Effects.Output)
                    {
                        effects[Effects.Key(def.Format, def.EnablePid)] = data[pos++] != 0 ? 1 : 0;
                        foreach (var p in def.Params)
                        {
                            int raw = data[pos++];
                            int v = p.Options != null ? raw : Math.Clamp(p.Min + raw * p.Step, p.Min, p.Max);
                            if (p.Options != null && !p.Options.Any(o => o.Value == v)) v = p.Default;
                            effects[Effects.Key(def.Format, p.Pid)] = v;
                        }
                    }
                }
                return (bands, effects);
            }
            catch
            {
                return null;
            }
        }

        private static int Get(Dictionary<string, int> d, Guid f, uint pid, int fallback) =>
            d.TryGetValue(Effects.Key(f, pid), out var v) ? v : fallback;

        private static byte Crc8(IEnumerable<byte> data)
        {
            byte crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
            return crc;
        }

        private static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        private static byte[] FromBase32(string s)
        {
            var result = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (char c in s)
            {
                int v = Alphabet.IndexOf(c);
                if (v < 0) throw new FormatException();
                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            return result.ToArray();
        }
    }
}
