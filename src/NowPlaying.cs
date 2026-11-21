using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace ClaudeCurtain
{
    /// <summary>One moment of what a media app is playing. Immutable, so it can be handed between threads.</summary>
    internal sealed class MediaInfo
    {
        public string App = "", Title = "", Artist = "", Album = "";
        public bool Playing;
        public double Position;      // seconds, as the app last reported it
        public double Length;        // seconds; 0 = unknown (no progress line then)
        public DateTime PositionAt;  // UTC, when Position was true

        /// <summary>Everything that changes the still part of the display (not the position, which is drawn live).</summary>
        public string Key()
        {
            return App + "¦" + Title + "¦" + Artist + "¦" + Album + "¦" + (Playing ? "1" : "0") + "¦" +
                   Math.Round(Length).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Where the track is now. Apps report the position only now and then (Spotify: on play, pause and
        /// seek), so while playing it runs on from the last report, never past the end.
        /// </summary>
        public double PositionNow(DateTime utcNow)
        {
            double p = Position;
            if (Playing && PositionAt != DateTime.MinValue) p += (utcNow - PositionAt).TotalSeconds;
            if (Length > 0) p = Math.Min(p, Length);
            return Math.Max(0, p);
        }

        /// <summary>m:ss, or h:mm:ss from an hour.</summary>
        public static string Time(double seconds)
        {
            long s = (long)Math.Max(0, Math.Floor(seconds));
            if (s >= 3600) return (s / 3600) + ":" + (s / 60 % 60).ToString("00") + ":" + (s % 60).ToString("00");
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        /// <summary>A readable app name from Windows' app id: "Spotify.exe" → Spotify, "...!Spotify" → Spotify.</summary>
        public static string AppName(string appId)
        {
            string a = (appId ?? "").Trim();
            int bang = a.LastIndexOf('!');
            if (bang >= 0) a = a.Substring(bang + 1);
            int slash = Math.Max(a.LastIndexOf('\\'), a.LastIndexOf('/'));
            if (slash >= 0) a = a.Substring(slash + 1);
            if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 4);
            switch (a.ToLowerInvariant())
            {
                case "msedge": case "microsoftedge": return "Edge";
                case "chrome": return "Chrome";
                case "firefox": case "308046b0af4a39cb": return "Firefox";
                case "app": case "microsoft.zunemusic": case "microsoft.zunevideo": return "Media Player";
            }
            if (a.Length > 0) a = char.ToUpperInvariant(a[0]) + a.Substring(1);
            return a;
        }
    }

    /// <summary>
    /// What is playing, read from Windows' own media controls (the panel by the volume flyout), which
    /// Spotify, browsers and most players report to. Read once a second on a thread of its own, so a slow
    /// answer never holds up the PIN keys or the screen. Shows a track while it plays, and while it is
    /// paused for up to PausedForgetMinutes; anything stopped, closed or long paused shows nothing.
    /// Needs Windows 10 1809 or later; on anything older it quietly shows nothing.
    /// </summary>
    internal sealed class NowPlaying
    {
        public const int PausedForgetMinutes = 15;
        private const int PollMs = 1000;
        private const int AnswerMs = 3000;

        private volatile MediaInfo current;
        private Thread thread;
        private string pausedKey;
        private DateTime pausedSince;
        private bool loggedFailure;

        /// <summary>The latest track, or null when nothing is playing.</summary>
        public MediaInfo Current { get { return current; } }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "now playing";
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Start();   // a new thread is MTA, which the media controls are happy with
        }

        /// <summary>One reading on the caller's thread (previews). Null when nothing plays or the API is missing.</summary>
        public static MediaInfo ReadOnce()
        {
            string error;
            return ReadOnce(out error);
        }

        /// <summary>The same, saying why it failed (null when Windows answered, whatever is playing).</summary>
        public static MediaInfo ReadOnce(out string error)
        {
            error = null;
            try { return ReadOnceCore(); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MediaInfo ReadOnceCore()
        {
            GlobalSystemMediaTransportControlsSessionManager mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            if (mgr == null) throw new TimeoutException("no answer in " + AnswerMs + " ms");
            return Read(mgr);
        }

        private void Run()
        {
            try { Loop(); }
            catch (Exception ex)
            {
                // Typically a Windows without the media controls API (before 1809): nothing to show, ever.
                current = null;
                Log.Write("now playing: not available (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Loop()
        {
            GlobalSystemMediaTransportControlsSessionManager mgr = null;
            while (true)
            {
                try
                {
                    if (mgr == null) mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    current = mgr == null ? null : ForgetLongPause(Read(mgr));
                    loggedFailure = false;
                }
                catch (Exception ex)
                {
                    if (ex is TypeLoadException || ex is MissingMethodException) throw;
                    current = null;
                    mgr = null;   // ask for a fresh manager next time
                    if (!loggedFailure) Log.Write("now playing: reading failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                    loggedFailure = true;
                }
                Thread.Sleep(PollMs);
            }
        }

        private MediaInfo ForgetLongPause(MediaInfo m)
        {
            if (m == null || m.Playing) { pausedKey = null; return m; }
            string key = m.Key();
            if (key != pausedKey) { pausedKey = key; pausedSince = DateTime.UtcNow; }
            return (DateTime.UtcNow - pausedSince).TotalMinutes < PausedForgetMinutes ? m : null;
        }

        /// <summary>The session that is playing (Windows' current one first), else the current one if paused.</summary>
        private static MediaInfo Read(GlobalSystemMediaTransportControlsSessionManager mgr)
        {
            GlobalSystemMediaTransportControlsSession pick = null, cur = mgr.GetCurrentSession();
            if (cur != null && Status(cur) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) pick = cur;
            if (pick == null)
            {
                foreach (GlobalSystemMediaTransportControlsSession s in mgr.GetSessions())
                {
                    if (Status(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { pick = s; break; }
                }
            }
            if (pick == null && cur != null && Status(cur) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) pick = cur;
            if (pick == null) return null;

            GlobalSystemMediaTransportControlsSessionMediaProperties p = Wait(pick.TryGetMediaPropertiesAsync());
            if (p == null) return null;
            MediaInfo m = new MediaInfo();
            m.Title = Clean(p.Title);
            m.Artist = Clean(p.Artist);
            if (m.Artist.Length == 0) m.Artist = Clean(p.AlbumArtist);
            m.Album = Clean(p.AlbumTitle);
            if (m.Title.Length == 0) return null;   // between tracks, or an app with nothing to say
            m.App = AppNameOrEmpty(pick.SourceAppUserModelId);
            m.Playing = Status(pick) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            GlobalSystemMediaTransportControlsSessionTimelineProperties t = pick.GetTimelineProperties();
            if (t != null)
            {
                double start = t.StartTime.TotalSeconds, end = t.EndTime.TotalSeconds;
                if (end > start + 1 && end < 24 * 3600)
                {
                    m.Length = end - start;
                    m.Position = Math.Max(0, t.Position.TotalSeconds - start);
                    DateTime at = t.LastUpdatedTime.UtcDateTime;
                    // A report from the future, or one long before the track could have started, is not trusted.
                    DateTime now = DateTime.UtcNow;
                    m.PositionAt = at > now || at < now.AddSeconds(-(m.Length + 60)) ? now : at;
                }
            }
            return m;
        }

        private static string AppNameOrEmpty(string id)
        {
            try { return MediaInfo.AppName(id); } catch { return ""; }
        }

        private static GlobalSystemMediaTransportControlsSessionPlaybackStatus Status(GlobalSystemMediaTransportControlsSession s)
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo info = s.GetPlaybackInfo();
            return info == null ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed : info.PlaybackStatus;
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }

        /// <summary>
        /// Waits for a Windows async call by polling it: the usual helper (AsTask) needs the Windows SDK's
        /// combined metadata, which the compiler that ships with Windows does not have. Gives up after
        /// AnswerMs and returns null.
        /// </summary>
        private static T Wait<T>(IAsyncOperation<T> op) where T : class
        {
            int start = Environment.TickCount;
            while (op.Status == AsyncStatus.Started)
            {
                if (Environment.TickCount - start > AnswerMs) { try { op.Cancel(); } catch { } return null; }
                Thread.Sleep(10);
            }
            if (op.Status != AsyncStatus.Completed) return null;
            return op.GetResults();
        }
    }
}
