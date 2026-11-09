using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace GraphiteCompanion.Services
{
    /// <summary>What is playing right now, as Windows shows it in its own volume flyout.</summary>
    public sealed class NowPlaying
    {
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string App { get; init; } = "";
        public ImageSource? Cover { get; init; }
        public TimeSpan Position { get; init; }
        public TimeSpan Duration { get; init; }
        public bool Playing { get; init; }
        public bool CanPrevious { get; init; }
        public bool CanNext { get; init; }
        public bool CanSeek { get; init; }
        /// <summary>Changes when the track changes (used to reload the cover only then).</summary>
        public string Key => App + "\n" + Title + "\n" + Artist;
    }

    /// <summary>
    /// Reads the current media session of Windows (Spotify, Яндекс Музыка, browsers, players…) and controls it.
    /// When an app does not publish a session, the media keys of the keyboard are sent instead.
    /// </summary>
    public static class MediaService
    {
        private static MediaManager? manager;
        private static string? coverKey;
        private static ImageSource? cover;

        private static async Task<MediaSession?> Session()
        {
            try
            {
                manager ??= await MediaManager.RequestAsync();
                // Windows' "current" session can be a paused tab while music plays elsewhere: prefer what is playing
                var current = manager.GetCurrentSession();
                if (IsPlaying(current)) return current;
                foreach (var s in manager.GetSessions())
                    if (IsPlaying(s)) return s;
                return current;
            }
            catch { return null; }
        }

        private static bool IsPlaying(MediaSession? s)
        {
            try { return s?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        public static async Task<NowPlaying?> GetAsync()
        {
            var s = await Session();
            if (s == null) return null;
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var info = s.GetPlaybackInfo();
                var tl = s.GetTimelineProperties();
                bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var duration = tl.EndTime - tl.StartTime;
                var position = tl.Position - tl.StartTime;
                // apps update the position only now and then: add the time passed since the last update
                var since = DateTimeOffset.Now - tl.LastUpdatedTime;
                if (playing && since > TimeSpan.Zero && since < TimeSpan.FromHours(6))
                    position += TimeSpan.FromTicks((long)(since.Ticks * (info.PlaybackRate ?? 1.0)));
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && position > duration) position = duration;

                var np = new NowPlaying
                {
                    Title = props?.Title ?? "",
                    Artist = !string.IsNullOrWhiteSpace(props?.Artist) ? props!.Artist : props?.AlbumArtist ?? "",
                    App = AppName(s.SourceAppUserModelId),
                    Position = position,
                    Duration = duration,
                    Playing = playing,
                    CanPrevious = info.Controls.IsPreviousEnabled,
                    CanNext = info.Controls.IsNextEnabled,
                    CanSeek = info.Controls.IsPlaybackPositionEnabled && duration > TimeSpan.Zero,
                };
                if (np.Key != coverKey)
                {
                    coverKey = np.Key;
                    cover = await LoadCover(props?.Thumbnail);
                }
                else if (cover == null && props?.Thumbnail != null)
                {
                    cover = await LoadCover(props.Thumbnail); // some apps attach the picture a moment after the title
                }
                return new NowPlaying
                {
                    Title = np.Title, Artist = np.Artist, App = np.App, Position = np.Position, Duration = np.Duration,
                    Playing = np.Playing, CanPrevious = np.CanPrevious, CanNext = np.CanNext, CanSeek = np.CanSeek, Cover = cover,
                };
            }
            catch { return null; }
        }

        private static async Task<ImageSource?> LoadCover(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
        {
            if (thumb == null) return null;
            try
            {
                using var ras = await thumb.OpenReadAsync();
                using var src = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                if (ms.Length == 0) return null;
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 300;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>"Spotify.exe" → "Spotify", "chrome" → "Google Chrome", "Microsoft.ZuneMusic_…!App" → "Медиаплеер".</summary>
        public static string AppName(string? id)
        {
            string s = id ?? "";
            string l = s.ToLowerInvariant();
            if (l.Contains("spotify")) return "Spotify";
            if ((l.Contains("yandex") && l.Contains("music")) || l.Contains("яндекс")) return "Яндекс Музыка";
            if (l.Contains("yandex")) return "Яндекс Браузер";
            if (l.Contains("applemusic") || l.Contains("apple music")) return "Apple Music";
            if (l.Contains("itunes")) return "iTunes";
            if (l.Contains("appletv")) return "Apple TV";
            if (l.Contains("vk") && (l.Contains("music") || l.Contains("vkmusic"))) return "VK Музыка";
            if (l.Contains("soundcloud")) return "SoundCloud";
            if (l.Contains("deezer")) return "Deezer";
            if (l.Contains("tidal")) return "TIDAL";
            if (l.Contains("chrome")) return "Google Chrome";
            if (l.Contains("msedge")) return "Microsoft Edge";
            if (l.Contains("firefox")) return "Firefox";
            if (l.Contains("opera")) return "Opera";
            if (l.Contains("zunemusic") || l.Contains("mediaplayer")) return "Медиаплеер";
            if (l.Contains("telegram")) return "Telegram";
            if (l.Contains("aimp")) return "AIMP";
            if (l.Contains("vlc")) return "VLC";
            if (l.Contains("discord")) return "Discord";
            int bang = s.IndexOf('!');
            if (bang > 0) s = s[..bang];
            int us = s.IndexOf('_');
            if (us > 0) s = s[..us];
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : "";
        }

        // ---------------- control ----------------

        public static Task Next() => Command(s => s.TrySkipNextAsync().AsTask(), VkNext);
        public static Task Previous() => Command(s => s.TrySkipPreviousAsync().AsTask(), VkPrev);
        public static Task PlayPause() => Command(s => s.TryTogglePlayPauseAsync().AsTask(), VkPlayPause);

        public static async Task Seek(TimeSpan position)
        {
            var s = await Session();
            if (s == null) return;
            try
            {
                var tl = s.GetTimelineProperties();
                await s.TryChangePlaybackPositionAsync((tl.StartTime + position).Ticks);
            }
            catch { }
        }

        private static async Task Command(Func<MediaSession, Task<bool>> run, byte fallbackKey)
        {
            var s = await Session();
            bool done = false;
            if (s != null)
            {
                try { done = await run(s); } catch { }
            }
            if (!done) PressKey(fallbackKey);
        }

        private const byte VkNext = 0xB0, VkPrev = 0xB1, VkPlayPause = 0xB3;

        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        private static void PressKey(byte vk)
        {
            keybd_event(vk, 0, 1, UIntPtr.Zero);      // extended key down
            keybd_event(vk, 0, 1 | 2, UIntPtr.Zero);  // up
        }

        public static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
