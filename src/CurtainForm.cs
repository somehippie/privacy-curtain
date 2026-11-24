using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeCurtain
{
    /// <summary>What the curtain shows. Rebuilt by the UI timer; the windows repaint only when it changes.</summary>
    internal sealed class DisplayState
    {
        private static readonly string[] NoWeather = new string[0];

        public string Clock = "";
        public string Date = "";
        public string Zone = "";       // e.g. "PST, GMT−8"
        public string AutoExit = "";
        public int PinState;
        public int Digits;
        public int CooldownLeft;
        public bool Bright;
        public bool AdminNotice;
        public int Drift;
        public bool Resting;             // no key for a while (RestClock): the map holds still, the accent rule rests

        // Glanceable info (see SysStats). Empty strings / arrays mean "nothing to show".
        public string Stats = "";
        public string SysInfo = "";
        public string Cpu = "", Ram = "", Net = "", Uptime = "", Disk = "";
        public string[] Weather = NoWeather;
        public SysSnapshot Sys;          // numbers and ten-minute history for the charts (null = strings only)
        public double NightDim = 1.0;    // the clock's brightness: 1 by day, CurtainPainter.NightLevel overnight
        public HourPoint[] Hourly = new HourPoint[0];   // the coming hours of the forecast
        public string HourlyKey = "";    // changes when a new hourly forecast is read
        public DateTime Now = DateTime.Now;              // local time of this state (which hour is "now")
        public double Lat = double.NaN, Lon = double.NaN;   // where the forecast is for (sunrise and sunset, home on the map)
        public WeatherMap Map;           // radar and satellite pictures (null = no map)
        public int MapMode;              // 0 = radar or clouds as the weather decides; 1 = radar; 2 = clouds (previews)
        public bool HideHome;            // previews to share: no home on the map (and Lat, Lon rounded to 0.1°)
        public MediaInfo Media;          // what is playing (null = nothing); its position is drawn live

        /// <summary>Everything that changes what is drawn, apart from where it sits (the drift).</summary>
        public string ContentKey()
        {
            return Clock + "|" + Date + "|" + Zone + "|" + PinState + "|" + Digits + "|" + CooldownLeft + "|" +
                   (Bright ? "1" : "0") + "|" + (AdminNotice ? "1" : "0") + "|" +
                   Cpu + "|" + Ram + "|" + Net + "|" + Uptime + "|" + Disk + "|" +
                   (Sys == null ? "-" : Sys.Version.ToString(CultureInfo.InvariantCulture)) + "|" +
                   NightDim.ToString("0.00", CultureInfo.InvariantCulture) + "|" + HourlyKey + "|" +
                   (Map == null ? "" : Map.Key) + "|" + MapMode + (HideHome ? "h" : "") + "|" +
                   (Media == null ? "" : Media.Key()) + "|" +
                   (Weather == null ? "" : string.Join("¦", Weather));
        }

        public string Key()
        {
            return ContentKey() + "|" + Drift + (Resting ? "|rest" : "");
        }
    }

    /// <summary>
    /// When the curtain rests (2026-11-23; the user: "after 45min or so a lower PC/GPU mode helps save energy. If I
    /// touch a key it can spin back up in 20min intervals", then: the monitor off too, and the keyboard only, not the
    /// mouse). It rests once no key has been pressed for RestMinutes since it went up, or for WakeMinutes since the
    /// last key. Only physical key presses count: the mouse and Claude's own input never wake it. Resting, the map
    /// holds its latest picture, the screen changes every few seconds, the weather script fetches less often and
    /// the monitor is switched off (and off again a minute after anything else turns it on).
    /// </summary>
    internal sealed class RestClock
    {
        public const double DefaultRestMinutes = 45, DefaultWakeMinutes = 20;
        public const int MonitorAgainSeconds = 60;
        private readonly double restMinutes, wakeMinutes;
        private DateTime lastKeyUtc;
        private bool anyKey;

        public RestClock(DateTime startUtc, double restMinutes, double wakeMinutes)
        {
            lastKeyUtc = startUtc;
            this.restMinutes = restMinutes;
            this.wakeMinutes = wakeMinutes;
        }

        public double WakeMinutes { get { return wakeMinutes; } }

        public void Key(DateTime utc)
        {
            lastKeyUtc = utc;
            anyKey = true;
        }

        public bool Resting(DateTime utc)
        {
            if (restMinutes <= 0) return false;
            return (utc - lastKeyUtc).TotalMinutes >= (anyKey ? wakeMinutes : restMinutes);
        }
    }

    /// <summary>
    /// One black, always-on-top window covering one monitor.
    ///
    ///  - WS_EX_LAYERED + WS_EX_TRANSPARENT: mouse hit-testing skips it, so injected clicks reach the apps below.
    ///  - WS_EX_NOACTIVATE + shown without activation: it never takes keyboard focus.
    ///  - WDA_EXCLUDEFROMCAPTURE: screen captures see the desktop underneath instead of this window.
    ///  - It answers WM_QUERYENDSESSION by closing the whole curtain, so it can never hold up a shutdown.
    /// </summary>
    internal sealed class CurtainForm : Form
    {
        private readonly CurtainUi owner;
        private readonly CurtainPainter painter = new CurtainPainter();
        private Rectangle target;
        private bool affinityOk;
        private int affinityError;
        private bool retiring;

        public CurtainForm(CurtainUi owner, Rectangle bounds)
        {
            this.owner = owner;
            target = bounds;
            Text = "Claude privacy curtain";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = true;
            BackColor = Color.Black;
            Bounds = bounds;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque |
                     ControlStyles.ResizeRedraw, true);
        }

        public Rectangle Target { get { return target; } }
        public bool AffinityOk { get { return affinityOk; } }
        public int AffinityError { get { return affinityError; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);   // fully opaque
            affinityOk = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            if (!affinityOk)
            {
                affinityError = Marshal.GetLastWin32Error();
            }
            else
            {
                uint actual;
                if (Native.GetWindowDisplayAffinity(Handle, out actual) && actual != Native.WDA_EXCLUDEFROMCAPTURE)
                {
                    affinityOk = false;
                    affinityError = -1;
                }
            }
        }

        /// <summary>Puts the window over its monitor, on top, visible, without activating it.</summary>
        public void ApplyPosition()
        {
            if (!IsHandleCreated) return;
            if (Native.IsIconic(Handle)) Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, target.X, target.Y, target.Width, target.Height,
                                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
        }

        public void SetTarget(Rectangle bounds)
        {
            target = bounds;
            ApplyPosition();
            Invalidate();
        }

        /// <summary>Removes this window because its monitor went away. Does not close the curtain.</summary>
        public void Retire()
        {
            retiring = true;
            Dispose();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_QUERYENDSESSION:
                    // Windows is shutting down, restarting or signing out (for example the power button).
                    // Agree at once and get out of the way.
                    m.Result = (IntPtr)1;
                    CurtainApp.SessionEnding((m.LParam.ToInt64() & 0x80000000L) != 0);
                    return;
                case Native.WM_ENDSESSION:
                    if (m.WParam != IntPtr.Zero) CurtainApp.SessionEnding((m.LParam.ToInt64() & 0x80000000L) != 0);
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_CLOSE:
                    if (!retiring)
                    {
                        // Something asked the window to close (for example taskkill without /F). Treat it as "off".
                        CurtainApp.ExitNow("window-closed", ExitCode.Ok);
                        return;
                    }
                    break;
                case Native.WM_DISPLAYCHANGE:
                    owner.DisplayChanged();
                    break;
                case Native.WM_POWERBROADCAST:
                    owner.PowerEvent((int)m.WParam.ToInt64());
                    break;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            try
            {
                PaintTo(g, owner.Current, ClientSize.Width, ClientSize.Height);   // the painter clears to black first
            }
            catch (Exception ex)
            {
                // A drawing problem must never take the curtain down; it just stays black.
                try { g.Clear(Color.Black); } catch { }
                Log.WriteOnce("paint", "paint failed: " + ex.Message);
            }
        }

        internal void PaintTo(Graphics g, DisplayState s, int w, int h)
        {
            painter.Paint(g, s, w, h);
            if (painter.NeedsFastFrames && owner != null) owner.RequestFrames();
        }

        /// <summary>True while this window's painter is in the middle of an animation.</summary>
        public bool Animating { get { return painter.NeedsFastFrames; } }

        /// <summary>
        /// Switches the monitors off while the curtain rests. Windows' default handling of the system command does
        /// it; the keep-awake request stays held, so sleep and the screen saver stay off. Any input turns them on.
        /// </summary>
        public void MonitorOff()
        {
            try
            {
                if (IsHandleCreated) Native.PostMessage(Handle, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MONITORPOWER, (IntPtr)2);
            }
            catch { }
        }

        /// <summary>While animating: the least time between repaints, and the part that changes (empty: all).</summary>
        public int FrameIntervalMs { get { return painter.FrameIntervalMs; } }
        public Rectangle AnimatedArea { get { return painter.AnimatedArea; } }
        internal int LastFrameAt;   // Environment.TickCount of the last fast-frame repaint (CurtainUi's frame timer)

        /// <summary>Previews: the painter itself, to drive it with a fixed clock.</summary>
        internal CurtainPainter Painter { get { return painter; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) painter.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Fallback PIN entry for the one case the hooks cannot cover: an elevated (administrator) window has
    /// keyboard focus. Windows does not show that input to a normal process's hooks, but it still delivers
    /// registered hotkeys, so while such a window is in front the PIN keys are registered as hotkeys.
    /// They are released again the moment focus leaves the elevated window, because a registered hotkey
    /// would otherwise swallow the digits Claude types.
    /// </summary>
    internal sealed class HotkeyWindow : NativeWindow
    {
        private static readonly int[] KeyList = BuildKeys();
        private readonly KeyQueue queue;
        private bool registered;
        private int caught;

        public HotkeyWindow(KeyQueue queue)
        {
            this.queue = queue;
            // A hidden ordinary window (never shown), which also receives lock / unlock / Remote Desktop notices.
            CreateParams cp = new CreateParams();
            cp.Caption = "ClaudeCurtainHotkeys";
            CreateHandle(cp);
            if (!Native.WTSRegisterSessionNotification(Handle, 0))
                Log.Write("warning: could not register for lock/unlock notifications");
        }

        public bool Registered { get { return registered; } }

        /// <summary>
        /// How many keys arrived through the hotkeys since they were last registered. Only ever logged as
        /// "none" or "some": a count would give away the PIN's length.
        /// </summary>
        public int Caught { get { return caught; } }

        private static int[] BuildKeys()
        {
            List<int> keys = new List<int>();
            for (int vk = 0x30; vk <= 0x39; vk++) keys.Add(vk);
            for (int vk = 0x60; vk <= 0x69; vk++) keys.Add(vk);
            keys.Add(Native.VK_RETURN);
            keys.Add(Native.VK_BACK);
            keys.Add(Native.VK_ESCAPE);
            return keys.ToArray();
        }

        public int Register()
        {
            if (registered) return 0;
            caught = 0;
            int ok = 0;
            for (int i = 0; i < KeyList.Length; i++)
            {
                if (Native.RegisterHotKey(Handle, i + 1, Native.MOD_NOREPEAT, (uint)KeyList[i])) ok++;
            }
            registered = true;
            return ok;
        }

        public void Unregister()
        {
            if (!registered) return;
            for (int i = 0; i < KeyList.Length; i++)
            {
                Native.UnregisterHotKey(Handle, i + 1);
            }
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int index = (int)m.WParam.ToInt64() - 1;
                if (index >= 0 && index < KeyList.Length)
                {
                    int vk = KeyList[index];
                    // Logged once per stretch, so the log shows the fallback works without showing the PIN's length.
                    if (caught++ == 0) Log.Write("hotkey fallback: the first key arrived");
                    queue.Push(vk == Native.VK_RETURN ? (vk | KeyQueue.FlagSubmit) : vk);
                }
                return;
            }
            if (m.Msg == Native.WM_WTSSESSION_CHANGE)
            {
                switch ((int)m.WParam.ToInt64())
                {
                    case 7: EventLog.Locks++; EventLog.Write("lock", "the PC was locked"); break;
                    case 8: EventLog.Unlocks++; EventLog.Write("unlock", "the PC was unlocked (Windows sign-in)"); break;
                    case 3: EventLog.RemoteConnects++; EventLog.Write("remote", "a Remote Desktop connection took over the session"); break;
                    case 4: EventLog.Write("remote", "the Remote Desktop connection ended"); break;
                    case 2: EventLog.Write("session", "the session was disconnected from the screen (user switch or Remote Desktop)"); break;
                    case 1: EventLog.Write("session", "the session was reconnected to the screen"); break;
                    case 6: EventLog.Write("signout", "the user is signing out"); break;
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Everything that lives on the UI thread: the windows, the clock timer, the upkeep.</summary>
    internal sealed class CurtainUi
    {
        private const int TickMs = 200;

        private readonly List<CurtainForm> forms = new List<CurtainForm>();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        // Runs only while a short animation plays (fade-in, minute cross-fade, PIN dots, wrong-PIN shake), so
        // the screen repaints at ~60 frames a second then and at the 200 ms tick the rest of the time; and while
        // the map's replay fades (most of each turn of its loop), when that is all that moves, at ~28 a second
        // and only the map's part of the window.
        private readonly System.Windows.Forms.Timer frames = new System.Windows.Forms.Timer();
        private readonly PinGate gate;
        private readonly InputHooks hooks;
        private readonly KeyQueue queue;
        private readonly string autoExitText;
        private readonly SysStats stats;

        private HotkeyWindow hotkeys;
        private DisplayState current = new DisplayState();
        private string lastKey = "";
        private int tick;
        private int lastActivity;
        private int brightUntilTick;
        private bool adminForeground;
        private bool pendingReconcile;
        private volatile bool reassertRequested;
        private uint ownPid;
        private uint lastForegroundPid;
        private int ownIntegrity;
        private IntPtr lastUserWindow;   // the last window in front that was not elevated
        private int reclaimTries;
        private int reclaimWait;
        private int reclaimStarted;
        private IntPtr reclaimedFrom;
        private int reclaimedAt;
        private IntPtr giveUpOn;

        /// <summary>Test only: a window with exactly this title counts as elevated.</summary>
        public string TestElevatedTitle { get; set; }
        private volatile bool freezeForTest;

        /// <summary>When to rest (null: never); and a file that exists while resting, for the weather script.</summary>
        public RestClock Rest { get; set; }
        public string RestFlagFile { get; set; }
        private bool resting, monitorOff;
        private long lastKeys = -1, lastInput = -1;
        private DateTime lastInputUtc = DateTime.UtcNow;
        private const int RestRepaintTicks = 25;   // resting, the screen changes every 5 s (TickMs 200)

        public CurtainUi(PinGate gate, InputHooks hooks, KeyQueue queue, string autoExitText, SysStats stats)
        {
            this.gate = gate;
            this.hooks = hooks;
            this.queue = queue;
            this.autoExitText = autoExitText;
            this.stats = stats;
        }

        public DisplayState Current { get { return current; } }
        public int MonitorCount { get { return forms.Count; } }

        /// <summary>Creates one window per monitor. Returns null on success, otherwise why it could not.</summary>
        public string Create()
        {
            ownPid = (uint)Process.GetCurrentProcess().Id;
            ownIntegrity = Integrity.OfCurrentProcess();
            hotkeys = new HotkeyWindow(queue);
            UpdateDisplay();

            string error = Reconcile(true);
            if (error != null) return error;
            foreach (CurtainForm f in forms)
            {
                if (!f.AffinityOk)
                {
                    return "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed, error " + f.AffinityError +
                           " (needs Windows 10 version 2004 or later)";
                }
            }
            return null;
        }

        public void Start()
        {
            timer.Interval = TickMs;
            timer.Tick += OnTick;
            timer.Start();
            frames.Interval = 16;
            frames.Tick += delegate
            {
                bool any = false;
                int now = Environment.TickCount;
                foreach (CurtainForm f in forms)
                {
                    if (!f.Animating) continue;
                    any = true;
                    // the map's replay, when it is all that moves, repaints less often and only where it is
                    if (unchecked(now - f.LastFrameAt) < f.FrameIntervalMs) continue;
                    f.LastFrameAt = now;
                    Rectangle area = f.AnimatedArea;
                    if (area.IsEmpty) f.Invalidate(); else f.Invalidate(area);
                }
                if (!any) frames.Stop();
            };
        }

        /// <summary>A window's painter started an animation: repaint at frame rate until it ends.</summary>
        public void RequestFrames()
        {
            if (!frames.Enabled && timer.Enabled) frames.Start();
        }

        public void DisplayChanged() { pendingReconcile = true; }

        public void PowerEvent(int what)
        {
            if (what == Native.PBT_APMSUSPEND) Log.Write("system is going to sleep");
            if (what == Native.PBT_APMRESUMEAUTOMATIC)
            {
                Log.Write("system resumed from sleep");
                pendingReconcile = true;
                hooks.RequestRehook();
            }
        }

        public void FreezeForTest() { freezeForTest = true; }

        /// <summary>Asked from the control thread when the security screen closes: bring the black screen back on top.</summary>
        public void RequestReassert() { reassertRequested = true; }

        public string Describe()
        {
            List<Rectangle> rects = new List<Rectangle>();
            foreach (CurtainForm f in forms) rects.Add(f.Target);
            return Monitors.Describe(rects);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (freezeForTest) Thread.Sleep(Timeout.Infinite);
            CurtainApp.UiBeat = CurtainApp.UiBeat + 1;
            tick++;
            try
            {
                if (pendingReconcile || reassertRequested || tick % 5 == 0)
                {
                    bool reassert = reassertRequested;
                    pendingReconcile = false;
                    reassertRequested = false;
                    Reconcile(false);
                    foreach (CurtainForm f in forms) f.ApplyPosition();   // stay on top of other topmost windows
                    if (reassert) EventLog.Write("reassert", "curtain brought back to the foreground after the security screen closed");
                }
                if (tick % 2 == 0) CheckForeground();
                UpdateDisplay();
            }
            catch (Exception ex)
            {
                Log.WriteOnce("tick:" + ex.GetType().Name, "ui upkeep failed: " + ex);
            }
        }

        /// <summary>
        /// The local time zone and its distance from GMT, for example "PST, GMT−8" or "PDT, GMT−7". Windows
        /// only gives long names ("Pacific Daylight Time"), so the short form is their initials.
        /// </summary>
        public static string ZoneText(DateTime local)
        {
            try
            {
                TimeZoneInfo tz = TimeZoneInfo.Local;
                string name = tz.IsDaylightSavingTime(local) ? tz.DaylightName : tz.StandardName;
                StringBuilder abbr = new StringBuilder();
                foreach (string word in (name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (char.IsLetter(word[0])) abbr.Append(char.ToUpperInvariant(word[0]));
                }
                TimeSpan off = tz.GetUtcOffset(local);
                string gmt = "GMT";
                if (off != TimeSpan.Zero)
                {
                    TimeSpan a = off.Duration();
                    gmt += (off < TimeSpan.Zero ? "−" : "+") + (int)a.TotalHours + (a.Minutes != 0 ? ":" + a.Minutes.ToString("00") : "");
                }
                return abbr.Length >= 2 && abbr.Length <= 5 ? abbr + ", " + gmt : gmt;
            }
            catch
            {
                return "";
            }
        }

        private void UpdateDisplay()
        {
            // Rest (RestClock): a physical key wakes it at once; resting, the monitor goes off once a minute has
            // passed without any input at all (the mouse or Claude's own input turn it on without waking the
            // curtain), and the screen changes only every few seconds.
            if (Rest != null && hooks != null)
            {
                DateTime utc = DateTime.UtcNow;
                long keys = hooks.KeyPresses;
                if (keys != lastKeys)
                {
                    if (lastKeys >= 0) Rest.Key(utc);
                    lastKeys = keys;
                }
                long input = keys + hooks.ButtonPresses + hooks.Moves + hooks.InjectedKeys + hooks.InjectedMouse;
                if (input != lastInput)
                {
                    lastInput = input;
                    lastInputUtc = utc;
                    monitorOff = false;
                }
                bool r = Rest.Resting(utc);
                if (r != resting) SetResting(r);
                if (resting && !monitorOff && (utc - lastInputUtc).TotalSeconds >= RestClock.MonitorAgainSeconds && forms.Count > 0)
                {
                    monitorOff = true;
                    forms[0].MonitorOff();
                }
                if (resting && tick % RestRepaintTicks != 0) return;
            }

            DateTime now = DateTime.Now;
            DisplayState s = new DisplayState();
            s.Resting = resting;
            s.Clock = now.ToString("t");
            s.Date = now.ToString("dddd, MMMM d");
            s.Zone = ZoneText(now);
            s.NightDim = CurtainPainter.NightFactor(now);
            s.Now = now;
            s.AutoExit = autoExitText;
            s.PinState = gate.State;
            s.Digits = gate.Digits;
            s.CooldownLeft = gate.CooldownSecondsLeft;

            int activity = hooks.Activity;
            if (activity != lastActivity)
            {
                lastActivity = activity;
                brightUntilTick = tick + (4000 / TickMs);
            }
            s.Bright = tick < brightUntilTick;
            s.AdminNotice = adminForeground;
            s.Drift = tick;   // advances every tick (TickMs = 200 ms); the window repaints each step, so the drift creeps smoothly
            if (stats != null)
            {
                s.Stats = stats.StatsLine;
                s.SysInfo = stats.SysLine;
                s.Cpu = stats.Cpu; s.Ram = stats.Ram; s.Net = stats.Net;
                s.Uptime = stats.UpFor; s.Disk = stats.Disk;
                s.Weather = stats.Weather;
                s.Sys = stats.Snapshot;
                s.Hourly = stats.Hourly;
                s.HourlyKey = stats.HourlyKey;
                s.Lat = stats.Latitude;
                s.Lon = stats.Longitude;
                s.Map = stats.Map;
                s.Media = stats.Media;
            }

            string key = s.Key();
            if (key == lastKey) return;
            lastKey = key;
            current = s;
            foreach (CurtainForm f in forms) f.Invalidate();
        }

        private void SetResting(bool r)
        {
            resting = r;
            monitorOff = false;
            Log.Write(r ? "resting: no key for a while, so the map holds still, the screen changes every few seconds and the monitor goes off"
                        : "awake: a key was pressed; fully on for " + Rest.WakeMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min");
            try
            {
                if (!string.IsNullOrEmpty(RestFlagFile))
                {
                    if (r) System.IO.File.WriteAllText(RestFlagFile, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    else System.IO.File.Delete(RestFlagFile);
                }
            }
            catch { }
        }

        /// <summary>Makes the set of windows match the set of monitors (hot-plug, resolution change, wake).</summary>
        private string Reconcile(bool initial)
        {
            List<Rectangle> monitors = Monitors.Enumerate();
            if (monitors.Count == 0) return initial ? "Windows reported no monitors" : null;

            bool same = monitors.Count == forms.Count;
            for (int i = 0; same && i < monitors.Count; i++)
            {
                if (forms[i].Target != monitors[i]) same = false;
            }
            if (same) return null;

            if (!initial) Log.Write("display layout changed: " + Monitors.Describe(monitors));
            for (int i = 0; i < monitors.Count; i++)
            {
                if (i < forms.Count)
                {
                    forms[i].SetTarget(monitors[i]);
                }
                else
                {
                    CurtainForm f = new CurtainForm(this, monitors[i]);
                    f.Show();
                    f.ApplyPosition();
                    forms.Add(f);
                    if (!initial && !f.AffinityOk)
                        Log.Write("warning: a new curtain window could not be excluded from capture, error " + f.AffinityError);
                }
            }
            for (int i = forms.Count - 1; i >= monitors.Count; i--)
            {
                forms[i].Retire();
                forms.RemoveAt(i);
            }
            return null;
        }

        /// <summary>
        /// Windows hides input aimed at an elevated window from a normal process's hooks. Notice when such
        /// a window is in front, say so on the curtain, and switch the PIN keys over to hotkeys meanwhile.
        /// If that window is one nobody can see (a background service's helper, as a game launcher's
        /// update once left behind), hand focus back to the window that had it before.
        /// </summary>
        private void CheckForeground()
        {
            bool admin = false;
            uint foregroundPid = 0;
            int level = Integrity.Unknown;
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(foreground, out foregroundPid);
                if (foregroundPid != 0 && foregroundPid != ownPid)
                {
                    bool denied;
                    level = Integrity.OfProcess(foregroundPid, out denied);
                    if (level == Integrity.Unknown) admin = denied;
                    else admin = ownIntegrity != Integrity.Unknown && level > ownIntegrity;
                    if (!admin && TestElevatedTitle != null && TitleOf(foreground) == TestElevatedTitle) admin = true;
                    if (!admin) lastUserWindow = foreground;   // where focus goes back to
                }
            }

            if (foregroundPid != lastForegroundPid)
            {
                lastForegroundPid = foregroundPid;
                string name = ProcessNameOf(foregroundPid);
                if (string.Equals(name, "Taskmgr", StringComparison.OrdinalIgnoreCase))
                {
                    EventLog.TaskManager++;
                    EventLog.Write("taskmgr", "Task Manager came to the front");
                    CurtainApp.RaiseAlert("taskmgr", "Task Manager came to the front; it runs above the curtain, so physical input reaches it");
                }
            }

            if (admin != adminForeground)
            {
                adminForeground = admin;
                if (admin)
                {
                    EventLog.AdminFocus++;
                    EventLog.Write("admin-window", "an administrator window took keyboard focus; physical keys can reach it");
                    CurtainApp.RaiseAlert("admin-window", "an administrator window took focus; physical keys can reach it behind the curtain");
                    int count = hotkeys.Register();
                    Log.Write("an elevated window has focus (pid " + foregroundPid + ", integrity 0x" + level.ToString("X") +
                              ", " + Describe(foreground) + "): hooks cannot see its input, PIN hotkey fallback on (" + count + " keys)");
                    reclaimTries = 0;
                    reclaimWait = 0;
                }
                else
                {
                    bool caught = hotkeys.Caught > 0;
                    hotkeys.Unregister();
                    Log.Write("focus left the elevated window: PIN hotkey fallback off (" +
                              (caught ? "keys arrived through it" : "no keys arrived through it") + ")");
                }
            }

            if (admin) ReclaimFromHidden(foreground);
        }

        /// <summary>
        /// An elevated window nobody can see has focus: give focus back to the window that had it, or else
        /// to the taskbar (what pressing the Windows key does). A window the user can see is left alone;
        /// it is theirs to use. Windows refuses a background process's focus change until 200 s pass with
        /// no input (ForegroundLockTimeout), unless that process sent the latest input itself, so the
        /// first tries come right after a zero-pixel mouse move from the curtain (AttachThreadInput did
        /// not help in testing). Later tries, every 2 s while the window keeps focus, skip the nudge so
        /// they cannot keep resetting that 200 s. If the same window takes focus straight back, the
        /// curtain stops fighting it and the on-screen hint (press the Windows key) stays.
        /// </summary>
        private void ReclaimFromHidden(IntPtr foreground)
        {
            if (foreground == giveUpOn) return;
            string why = HiddenReason(foreground);
            if (why == null) return;
            if (reclaimWait > 0) { reclaimWait--; return; }
            reclaimWait = 4;   // CheckForeground runs every 400 ms

            if (foreground == reclaimedFrom && Environment.TickCount - reclaimedAt < 10000)
            {
                giveUpOn = foreground;
                Log.Write("the hidden elevated window took focus straight back; leaving it be (press the Windows key)");
                return;
            }

            IntPtr target = lastUserWindow;
            if (target == IntPtr.Zero || !Native.IsWindow(target) || !Native.IsWindowVisible(target) || Native.IsIconic(target))
                target = Native.FindWindow("Shell_TrayWnd", null);
            if (target == IntPtr.Zero) return;

            reclaimTries++;
            if (reclaimTries == 1) reclaimStarted = Environment.TickCount;
            Native.SetForegroundWindow(target);
            string how = "directly";
            if (Native.GetForegroundWindow() != target && reclaimTries <= 3)
            {
                if (Native.NudgeMouse())
                {
                    Native.SetForegroundWindow(target);
                    how = "after a zero-pixel mouse nudge";
                }
                else how = "the nudge was refused, error " + Marshal.GetLastWin32Error();
            }
            if (Native.GetForegroundWindow() == target)
            {
                reclaimedFrom = foreground;
                reclaimedAt = Environment.TickCount;
                Log.Write("took focus back from a hidden elevated window (" + why + ") " + how + " and gave it to " + Describe(target) +
                          (reclaimTries > 1 ? ", " + ((Environment.TickCount - reclaimStarted) / 1000) + " s after the first try" : ""));
                EventLog.Write("focus-back", "the curtain took focus back from a hidden administrator window");
            }
            else if (reclaimTries == 1)
            {
                Log.Write("could not take focus back from a hidden elevated window (" + why + ") yet (" + how + "; target " +
                          Describe(target) + "); Windows allows it once 200 s pass with no input, so trying every 2 s");
            }
        }

        /// <summary>Why a window cannot be seen, or null when it can.</summary>
        internal static string HiddenReason(IntPtr hwnd)
        {
            if (!Native.IsWindowVisible(hwnd)) return "not visible";
            if (Native.IsIconic(hwnd)) return "minimised";
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return "cloaked";
            Native.RECT r;
            if (Native.GetWindowRect(hwnd, out r))
            {
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 4 || h <= 4) return "only " + w + " x " + h + " px";
            }
            if (Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONULL) == IntPtr.Zero) return "off every screen";
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_LAYERED) != 0)
            {
                uint key, flags;
                byte alpha;
                if (Native.GetLayeredWindowAttributes(hwnd, out key, out alpha, out flags) &&
                    (flags & Native.LWA_ALPHA) != 0 && alpha == 0) return "fully transparent";
            }
            return null;
        }

        /// <summary>Process, window class, place and visibility: enough to recognise a window later, without its title.</summary>
        private static string Describe(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            StringBuilder cls = new StringBuilder(128);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            Native.RECT r;
            string place = Native.GetWindowRect(hwnd, out r)
                ? r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) : "?";
            string hidden = HiddenReason(hwnd);
            return ProcessNameOf(pid) + ", class " + cls + ", " + place + ", " + (hidden ?? "visible");
        }

        private static string ProcessNameOf(uint pid)
        {
            try { if (pid != 0) return Process.GetProcessById((int)pid).ProcessName; } catch { }
            return "?";
        }

        private static string TitleOf(IntPtr hwnd)
        {
            StringBuilder title = new StringBuilder(256);
            Native.GetWindowText(hwnd, title, title.Capacity);
            return title.ToString();
        }
    }
}
