using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
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
                if (src.Length == 0) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 300;
                bmp.StreamSource = src;
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

        /// <summary>True while the seek fallback is talking to the player's own window; it may briefly
        /// steal the focus, and the overlay must not treat that as "the user went elsewhere".</summary>
        public static bool SeekInProgress { get; private set; }
        private static readonly SemaphoreSlim seekGate = new(1, 1);

        public static async Task<bool> Seek(TimeSpan position, CancellationToken cancellationToken = default)
        {
            await seekGate.WaitAsync(cancellationToken);
            try { return await SeekCore(position, cancellationToken); }
            finally { seekGate.Release(); }
        }

        private static async Task<bool> SeekCore(TimeSpan position, CancellationToken cancellationToken)
        {
            var s = await Session();
            if (s == null) return false;
            cancellationToken.ThrowIfCancellationRequested();

            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;
            bool preferSlider;
            bool appleMusic;
            try
            {
                timeline = s.GetTimelineProperties();
                appleMusic = AppName(s.SourceAppUserModelId) == "Apple Music";
                preferSlider = appleMusic || !s.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled;
            }
            catch { return false; }

            if (!preferSlider && await SeekWithSystem(s, timeline.StartTime + position, cancellationToken)) return true;

            var duration = timeline.EndTime - timeline.StartTime;
            var before = timeline.Position - timeline.StartTime;
            if (await SeekWithSlider(position, duration, before, appleMusic, cancellationToken)) return true;

            // A player may leave its controls disabled or have its window closed while still accepting system seeks.
            return preferSlider && await SeekWithSystem(s, timeline.StartTime + position, cancellationToken);
        }

        private static async Task<bool> SeekWithSystem(MediaSession session, TimeSpan position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void CheckPosition()
            {
                try
                {
                    if (Math.Abs((session.GetTimelineProperties().Position - position).TotalSeconds) < 1.25)
                        confirmed.TrySetResult(true);
                }
                catch { }
            }

            void TimelineChanged(MediaSession sender, TimelinePropertiesChangedEventArgs args) => CheckPosition();
            try
            {
                session.TimelinePropertiesChanged += TimelineChanged;
                if (!await session.TryChangePlaybackPositionAsync(position.Ticks)) return false;
                CheckPosition();
                // React to the first confirmed update. Only a player that reports success without moving waits.
                var result = await Task.WhenAny(confirmed.Task, Task.Delay(220, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                return result == confirmed.Task;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
            finally { try { session.TimelinePropertiesChanged -= TimelineChanged; } catch { } }
        }

        private static async Task<bool> SeekWithSlider(TimeSpan position, TimeSpan duration, TimeSpan before, bool appleMusic,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SeekInProgress = true;
            IntPtr front = GetForegroundWindow();
            try
            {
                return await Task.Run(() => SeekViaUiAutomation(position, duration, before, appleMusic, front, cancellationToken), cancellationToken);
            }
            finally { SeekInProgress = false; }
        }

        /// <summary>
        /// Last resort for apps that publish a timeline but ignore the system seek command:
        /// find the player's own seek slider in its window (Apple Music exposes "LCDScrubber"
        /// with a RangeValue pattern) and set its value directly. The window must be open,
        /// not minimized to the tray. <paramref name="restoreForeground"/> is the window that
        /// was in front before (SetValue raises the player's window) — the focus goes back to it
        /// and the player's window is pushed back below, so a borderless game never sees it.
        /// </summary>
        private static bool SeekViaUiAutomation(TimeSpan target, TimeSpan duration, TimeSpan before, bool appleMusic,
            IntPtr restoreForeground, CancellationToken cancellationToken)
        {
            if (duration <= TimeSpan.Zero) return false;
            try
            {
                var root = AutomationElement.RootElement;
                var windows = root.FindAll(TreeScope.Children, PlayerWindowCondition(appleMusic));
                var sliderCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider);
                var scrubberCond = new PropertyCondition(AutomationElement.AutomationIdProperty, "LCDScrubber");

                foreach (AutomationElement win in windows)
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    try
                    {
                        if (win.Current.ProcessId == Environment.ProcessId) continue;
                        var sliders = win.FindAll(TreeScope.Descendants, appleMusic ? scrubberCond : sliderCond);
                        bool exactScrubber = appleMusic && sliders.Count > 0;
                        if (appleMusic && !exactScrubber) sliders = win.FindAll(TreeScope.Descendants, sliderCond);
                        foreach (AutomationElement sl in sliders)
                        {
                            if (cancellationToken.IsCancellationRequested) return false;
                            if (!sl.TryGetCurrentPattern(RangeValuePattern.Pattern, out var p)) continue;
                            var rv = (RangeValuePattern)p;
                            var cur = rv.Current;
                            if (cur.IsReadOnly) continue;
                            // match the session's track: length ≈ duration, value ≈ position before the seek
                            if (Math.Abs(cur.Maximum - duration.TotalSeconds) > Math.Max(5, duration.TotalSeconds * 0.08)) continue;
                            if (!exactScrubber && Math.Abs(cur.Value - before.TotalSeconds) > 10) continue;
                            if (cancellationToken.IsCancellationRequested) return false;
                            rv.SetValue(Math.Clamp(target.TotalSeconds, cur.Minimum, cur.Maximum));
                            // SetValue raised the player's window (focus and z-order): undo both.
                            IntPtr player = win.Current.NativeWindowHandle;
                            if (restoreForeground != IntPtr.Zero && restoreForeground != player)
                                RestoreForeground(restoreForeground);
                            // then the z-order: a visible player window would otherwise sit above the game
                            if (restoreForeground != IntPtr.Zero && restoreForeground != player && IsWindowVisible(player))
                                SetWindowPos(player, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                                    0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x0010 /* SWP_NOACTIVATE */);
                            return true;
                        }
                    }
                    catch { }
                }
                if (restoreForeground != IntPtr.Zero) RestoreForeground(restoreForeground);
            }
            catch { }
            return false;
        }

        private static Condition PlayerWindowCondition(bool appleMusic)
        {
            if (!appleMusic) return Condition.TrueCondition;
            var conditions = new List<Condition>();
            foreach (var process in Process.GetProcessesByName("AppleMusic"))
            {
                using (process) conditions.Add(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
            }
            return conditions.Count switch
            {
                0 => Condition.FalseCondition,
                1 => conditions[0],
                _ => new OrCondition(conditions.ToArray()),
            };
        }

        /// <summary>
        /// After the player's window stole the foreground, a plain SetForegroundWindow is refused
        /// (Windows moved the "right to set foreground" to the player's process). Attaching our
        /// thread to the current foreground thread for a moment gives that right back.
        /// </summary>
        private static void RestoreForeground(IntPtr window)
        {
            try
            {
                if (!SetForegroundWindow(window))
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != window)
                    {
                        uint fgThread = GetWindowThreadProcessId(fg, out _);
                        uint myThread = GetCurrentThreadId();
                        if (fgThread != 0 && fgThread != myThread)
                        {
                            AttachThreadInput(myThread, fgThread, true);
                            SetForegroundWindow(window);
                            AttachThreadInput(myThread, fgThread, false);
                        }
                    }
                }
            }
            catch { }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

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
