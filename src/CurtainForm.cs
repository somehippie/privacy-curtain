using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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
        public string[] Weather = NoWeather;

        public string Key()
        {
            return Clock + "|" + Date + "|" + PinState + "|" + Digits + "|" + CooldownLeft + "|" +
                   (Bright ? "1" : "0") + "|" + (AdminNotice ? "1" : "0") + "|" + Drift + "|" +
                   Stats + "|" + SysInfo + "|" + (Weather == null ? "" : string.Join("¦", Weather));
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
        private static readonly Color ColorClock = Color.FromArgb(205, 205, 205);
        private static readonly Color ColorTitle = Color.FromArgb(165, 165, 165);
        private static readonly Color ColorDim = Color.FromArgb(110, 110, 110);
        private static readonly Color ColorFaint = Color.FromArgb(88, 88, 88);
        private static readonly Color ColorBright = Color.FromArgb(190, 190, 190);
        private static readonly Color ColorWrong = Color.FromArgb(215, 95, 95);
        private static readonly Color ColorWarn = Color.FromArgb(205, 160, 70);

        private readonly CurtainUi owner;
        private Rectangle target;
        private bool affinityOk;
        private int affinityError;
        private bool retiring;
        private int fontUnit;
        private Font fontClock;
        private Font fontDate;
        private Font fontTitle;
        private Font fontHint;
        private Font fontSmall;
        private Font fontInfo;

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
                Draw(g, owner.Current);
            }
            catch (Exception ex)
            {
                // A drawing problem must never take the curtain down; it just stays black.
                Log.WriteOnce("paint", "paint failed: " + ex.Message);
            }
        }

        private void Draw(Graphics g, DisplayState s)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (s == null || w < 80 || h < 80) return;

            int unit = Math.Max(24, Math.Min(w / 16, h / 9));
            EnsureFonts(unit);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Everything drifts slowly, together, so that over the hours the display stays lit nothing
            // is burned into the panel. s.Drift advances every tick (~200 ms) and the window repaints each
            // step, so these slow, mismatched rates trace a wide Lissajous wander (x-cycle ~4 min,
            // y-cycle ~6 min) within +/-6% of centre as a smooth sub-pixel creep rather than visible hops.
            float dx = (float)(Math.Sin(s.Drift * 0.0050) * w * 0.06);
            float dy = (float)(Math.Cos(s.Drift * 0.0037) * h * 0.06);
            float cx = w / 2f + dx;
            float clockY = h * 0.36f + dy;
            float titleY = h * 0.60f + dy;
            float pinY = titleY + unit * 1.30f;

            using (StringFormat center = new StringFormat())
            {
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;
                center.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

                // A quiet cluster of glanceable info across the top: machine stats, then a short forecast.
                // Deliberately dim, so it never competes with the message below; it drifts with everything else.
                float infoY = h * 0.05f + dy;
                float lineH = unit * 0.32f;
                if (!string.IsNullOrEmpty(s.Stats)) { DrawText(g, s.Stats, fontInfo, ColorDim, cx, infoY, w, center); infoY += lineH; }
                if (!string.IsNullOrEmpty(s.SysInfo)) { DrawText(g, s.SysInfo, fontInfo, ColorDim, cx, infoY, w, center); infoY += lineH; }
                if (s.Weather != null && s.Weather.Length > 0)
                {
                    infoY += lineH * 0.4f;
                    for (int i = 0; i < s.Weather.Length; i++)
                    {
                        DrawText(g, s.Weather[i], fontInfo, i == 0 ? ColorFaint : ColorDim, cx, infoY, w, center);
                        infoY += lineH;
                    }
                }

                DrawText(g, s.Clock, fontClock, ColorClock, cx, clockY, w, center);
                DrawText(g, s.Date, fontDate, ColorDim, cx, clockY + unit * 0.98f, w, center);
                DrawText(g, "PC in use by Claude", fontTitle, ColorTitle, cx, titleY, w, center);
                DrawText(g, "Locked while Claude is working.", fontHint, ColorTitle, cx, titleY + unit * 0.52f, w, center);
                DrawText(g, "Any attempt to use, unlock or interrupt this PC is logged.", fontHint, ColorTitle, cx, titleY + unit * 0.78f, w, center);

                switch (s.PinState)
                {
                    case PinGate.StateTyping:
                        DrawDots(g, s.Digits, cx, pinY, unit);
                        DrawText(g, "Enter to unlock, Esc to clear", fontSmall, ColorDim, cx, pinY + unit * 0.34f, w, center);
                        break;
                    case PinGate.StateWrong:
                        DrawText(g, "Wrong PIN", fontHint, ColorWrong, cx, pinY, w, center);
                        break;
                    case PinGate.StateCooldown:
                        DrawText(g, "Too many wrong tries. Wait " + s.CooldownLeft + " s.", fontHint, ColorWarn, cx, pinY, w, center);
                        break;
                    default:
                        DrawText(g, "Keyboard and mouse are blocked.",
                                 fontHint, s.Bright ? ColorBright : ColorDim, cx, pinY, w, center);
                        break;
                }

                float bottom = h * 0.90f + dy / 2f;
                if (s.AdminNotice)
                {
                    DrawText(g, "An administrator window has focus. If the PIN does not respond, press the Windows key first.",
                             fontSmall, ColorWarn, cx, bottom - unit * 0.30f, w, center);
                }
                // Deliberately not shown here: the closing time (curtain-on prints it) and the emergency
                // exits (Ctrl+Alt+Del and the power button still always work; see the README).
            }
        }

        private static void DrawText(Graphics g, string text, Font font, Color color, float cx, float cy, int width, StringFormat format)
        {
            if (string.IsNullOrEmpty(text) || font == null) return;
            using (SolidBrush brush = new SolidBrush(color))
            {
                float half = font.GetHeight(g);
                RectangleF box = new RectangleF(cx - width, cy - half, width * 2f, half * 2f);
                g.DrawString(text, font, brush, box, format);
            }
        }

        private static void DrawDots(Graphics g, int count, float cx, float cy, int unit)
        {
            int n = Math.Min(Math.Max(count, 0), PinGate.MaxBuffer);
            if (n == 0) return;
            float size = unit * 0.14f;
            float gap = unit * 0.30f;
            float left = cx - ((n - 1) * gap) / 2f;
            using (SolidBrush brush = new SolidBrush(ColorClock))
            {
                for (int i = 0; i < n; i++)
                {
                    g.FillEllipse(brush, left + i * gap - size / 2f, cy - size / 2f, size, size);
                }
            }
        }

        private void EnsureFonts(int unit)
        {
            if (fontClock != null && unit == fontUnit) return;
            DisposeFonts();
            fontUnit = unit;
            fontClock = MakeFont("Segoe UI Light", unit * 1.30f);
            fontDate = MakeFont("Segoe UI", unit * 0.20f);
            fontTitle = MakeFont("Segoe UI Semilight", unit * 0.42f);
            fontHint = MakeFont("Segoe UI", unit * 0.17f);
            fontSmall = MakeFont("Segoe UI", unit * 0.125f);
            fontInfo = MakeFont("Segoe UI", unit * 0.16f);
        }

        private static Font MakeFont(string family, float pixels)
        {
            return new Font(family, Math.Max(9f, pixels), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            Font[] all = { fontClock, fontDate, fontTitle, fontHint, fontSmall, fontInfo };
            foreach (Font f in all)
            {
                if (f != null) f.Dispose();
            }
            fontClock = fontDate = fontTitle = fontHint = fontSmall = fontInfo = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
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

        private void UpdateDisplay()
        {
            DateTime now = DateTime.Now;
            DisplayState s = new DisplayState();
            s.Clock = now.ToString("t");
            s.Date = now.ToString("dddd, MMMM d");
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
                s.Weather = stats.Weather;
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
