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

        /// <summary>Everything that changes what is drawn, apart from where it sits (the drift).</summary>
        public string ContentKey()
        {
            return Clock + "|" + Date + "|" + Zone + "|" + PinState + "|" + Digits + "|" + CooldownLeft + "|" +
                   (Bright ? "1" : "0") + "|" + (AdminNotice ? "1" : "0") + "|" +
                   Cpu + "|" + Ram + "|" + Net + "|" + Uptime + "|" + Disk + "|" +
                   (Sys == null ? "-" : Sys.Version.ToString(CultureInfo.InvariantCulture)) + "|" +
                   NightDim.ToString("0.00", CultureInfo.InvariantCulture) + "|" + HourlyKey + "|" +
                   (Map == null ? "" : Map.Key) + "|" + MapMode + "|" +
                   (Weather == null ? "" : string.Join("¦", Weather));
        }

        public string Key()
        {
            return ContentKey() + "|" + Drift;
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
            g.Clear(Color.Black);
            try
            {
                PaintTo(g, owner.Current, ClientSize.Width, ClientSize.Height);
            }
            catch (Exception ex)
            {
                // A drawing problem must never take the curtain down; it just stays black.
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
        // the screen repaints at ~60 frames a second then and at the 200 ms tick the rest of the time.
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
        private volatile bool freezeForTest;

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
                foreach (CurtainForm f in forms)
                {
                    if (f.Animating) { any = true; f.Invalidate(); }
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
            DateTime now = DateTime.Now;
            DisplayState s = new DisplayState();
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
            }

            string key = s.Key();
            if (key == lastKey) return;
            lastKey = key;
            current = s;
            foreach (CurtainForm f in forms) f.Invalidate();
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
                }
            }

            if (foregroundPid != lastForegroundPid)
            {
                lastForegroundPid = foregroundPid;
                string name = "";
                try { if (foregroundPid != 0) name = Process.GetProcessById((int)foregroundPid).ProcessName; } catch { }
                if (string.Equals(name, "Taskmgr", StringComparison.OrdinalIgnoreCase))
                {
                    EventLog.TaskManager++;
                    EventLog.Write("taskmgr", "Task Manager came to the front");
                    CurtainApp.RaiseAlert("taskmgr", "Task Manager came to the front; it runs above the curtain, so physical input reaches it");
                }
            }

            if (admin == adminForeground) return;
            adminForeground = admin;
            if (admin)
            {
                EventLog.AdminFocus++;
                EventLog.Write("admin-window", "an administrator window took keyboard focus; physical keys can reach it");
                CurtainApp.RaiseAlert("admin-window", "an administrator window took focus; physical keys can reach it behind the curtain");
                int count = hotkeys.Register();
                Log.Write("an elevated window has focus (pid " + foregroundPid + ", integrity 0x" + level.ToString("X") +
                          "): hooks cannot see its input, PIN hotkey fallback on (" + count + " keys)");
            }
            else
            {
                hotkeys.Unregister();
                Log.Write("focus left the elevated window: PIN hotkey fallback off");
            }
        }
    }
}
