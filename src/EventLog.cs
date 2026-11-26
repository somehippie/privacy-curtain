using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeCurtain
{
    /// <summary>
    /// The record behind the on-screen promise that attempts to use, unlock or interrupt the PC are logged.
    ///
    /// One line per event in local\events.log:  time TAB curtain-pid TAB kind TAB text
    /// Written on events only, plus one heartbeat a minute. Never holds PIN digits or which keys were
    /// pressed, only counts. Capped at 1 MB: when it grows past that, the oldest part is dropped.
    /// </summary>
    internal static class EventLog
    {
        public const long CapBytes = 1000000;
        private const int KeepBytes = 600000;

        private static readonly object gate = new object();
        private static string path;
        private static int runPid;

        // Tallies for the "While you were away" summary of this run.
        public static int WrongPin;
        public static long KeyPresses;
        public static long Clicks;
        public static int InputMinutes;
        public static bool MouseMoved;
        public static int SecurityScreen;
        public static int TaskManager;
        public static int AdminFocus;
        public static int Locks;
        public static int Unlocks;
        public static int RemoteConnects;
        public static int NetworkDown;
        public static int NetworkUp;
        public static bool EarlierUnclean;

        public static void Init(string file, int pid)
        {
            path = file;
            runPid = pid;
        }

        // This run's notable moments, for the timeline in the "While you were away" window. Kinds only, never
        // what was typed. Capped, so even a curtain left up for days stays small.
        private static readonly List<KeyValuePair<DateTime, string>> moments = new List<KeyValuePair<DateTime, string>>();

        private static bool OnTimeline(string kind, string text)
        {
            switch (kind)
            {
                case "input": case "wrong-pin": case "taskmgr": case "admin-window": case "lock": case "remote": return true;
                case "security-screen": return text.IndexOf("opened", StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }

        /// <summary>Writes this run's moments as "seconds-from-start TAB kind" lines, after a start/end header.</summary>
        public static void WriteTimeline(string file, DateTime startUtc, DateTime endUtc)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("start\t").Append(startUtc.ToString("o", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("end\t").Append(endUtc.ToString("o", CultureInfo.InvariantCulture)).Append("\r\n");
            // Which curtain this was, so "View full log" can pick this run's lines out of events.log.
            sb.Append("pid\t").Append(runPid.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            lock (gate)
            {
                foreach (KeyValuePair<DateTime, string> m in moments)
                {
                    double s = Math.Max(0, (m.Key - startUtc).TotalSeconds);
                    sb.Append(s.ToString("0", CultureInfo.InvariantCulture)).Append('\t').Append(m.Value).Append("\r\n");
                }
            }
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }

        public static void Write(string kind, string text)
        {
            if (path == null) return;
            if (OnTimeline(kind, text ?? ""))
            {
                lock (gate) { if (moments.Count < 5000) moments.Add(new KeyValuePair<DateTime, string>(DateTime.UtcNow, kind)); }
            }
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" +
                          runPid.ToString(CultureInfo.InvariantCulture) + "\t" + kind + "\t" +
                          text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            lock (gate)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Trim();
                        using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                        }
                        return;
                    }
                    catch (IOException) { Thread.Sleep(15); }
                    catch { return; }
                }
            }
        }

        private static void Trim()
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length + 512 <= CapBytes) return;
            byte[] all = File.ReadAllBytes(path);
            int from = Math.Max(0, all.Length - KeepBytes);
            while (from < all.Length && all[from] != (byte)'\n') from++;   // start on a whole line
            from = Math.Min(all.Length, from + 1);
            byte[] tail = new byte[all.Length - from];
            Array.Copy(all, from, tail, 0, tail.Length);
            File.WriteAllBytes(path, tail);
        }

        /// <summary>
        /// Looks for a curtain that logged a start but never a stop. With pid above zero, asks about that
        /// curtain only; otherwise about the most recent one.
        /// </summary>
        public static bool RunIsOpen(string file, int pid, out string started, out string lastSeen)
        {
            started = null;
            lastSeen = null;
            int open = -1;
            try
            {
                if (!File.Exists(file)) return false;
                string[] lines;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                {
                    lines = reader.ReadToEnd().Split('\n');
                }
                foreach (string raw in lines)
                {
                    string[] p = raw.TrimEnd('\r').Split('\t');
                    if (p.Length < 4) continue;
                    int linePid;
                    if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out linePid)) continue;
                    if (p[2] == "start")
                    {
                        open = linePid;
                        started = p[0];
                        lastSeen = p[0];
                    }
                    else if (linePid == open)
                    {
                        lastSeen = p[0];
                        if (p[2] == "stop" || p[2] == "killed") open = -1;
                    }
                }
            }
            catch
            {
                return false;
            }
            if (open == -1) return false;
            return pid <= 0 || open == pid;
        }

        /// <summary>A one-line tally of what happened this run, for the "all clear" alert.</summary>
        public static string CountsLine()
        {
            return "Security screen " + SecurityScreen + ", Task Manager " + TaskManager + ", admin windows " + AdminFocus +
                   ", blocked keys " + KeyPresses + ", mouse clicks " + Clicks + ", wrong PINs " + WrongPin +
                   ", locks " + Locks + ", Remote Desktop " + RemoteConnects + ".";
        }

        public static string Span(double seconds)
        {
            long s = (long)Math.Max(0, Math.Round(seconds));
            if (s >= 3600) return (s / 3600) + " h " + ((s % 3600) / 60) + " min";
            if (s >= 60) return (s / 60) + " min " + (s % 60) + " s";
            return s + " s";
        }

        public static string BuildSummary(DateTime startLocal, string closedBy)
        {
            DateTime now = DateTime.Now;
            List<string> items = new List<string>();
            if (WrongPin > 0) items.Add("Wrong PIN attempts: " + WrongPin);
            if (KeyPresses > 0) items.Add("Keyboard: " + KeyPresses + " key presses blocked");
            if (Clicks > 0 || MouseMoved)
                items.Add("Mouse: " + Clicks + " clicks blocked" + (MouseMoved ? ", mouse was moved" : ""));
            if (InputMinutes > 0) items.Add("Someone touched the keyboard or mouse in " + InputMinutes + " separate minute(s)");
            if (SecurityScreen > 0) items.Add("Windows security screen opened (Ctrl+Alt+Del, lock or admin prompt): " + SecurityScreen);
            if (TaskManager > 0) items.Add("Task Manager came to the front: " + TaskManager);
            if (AdminFocus > 0) items.Add("An administrator window took focus: " + AdminFocus);
            if (Locks > 0 || Unlocks > 0) items.Add("PC locked: " + Locks + ", unlocked: " + Unlocks);
            if (RemoteConnects > 0) items.Add("Remote Desktop connections: " + RemoteConnects);
            if (NetworkDown > 0 || NetworkUp > 0) items.Add("Network dropped: " + NetworkDown + ", came back: " + NetworkUp);
            if (EarlierUnclean) items.Add("An earlier curtain had ended without a clean close (power cut, crash or kill)");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("While you were away");
            sb.AppendLine("Curtain was up for " + Span((now - startLocal).TotalSeconds) + " (" + startLocal.ToString("t") +
                          " to " + now.ToString("t") + "). Closed by: " + closedBy + ".");
            if (items.Count == 0) sb.AppendLine("Nobody touched the PC and nothing else happened.");
            foreach (string item in items) sb.AppendLine("- " + item);
            sb.AppendLine("Full record: scripts\\curtain-log.ps1");
            return sb.ToString();
        }
    }

    /// <summary>
    /// The "While you were away" window shown after a PIN unlock, in the curtain's design language: the
    /// session length as the hero figure (in the clock's tone, dimmed overnight like the clock), a timeline of the session (short neutral ticks where someone was
    /// at the keyboard or mouse, taller accent ticks for alerts), then the counts as a table. It sizes itself
    /// to its content and to the screen, never takes focus, and closes itself after 10 seconds.
    /// </summary>
    internal sealed class SummaryView : Form
    {
        private const int EdgeGap = 24;   // keep at least this much of the screen free around the window
        private static readonly Color Background = Color.FromArgb(14, 14, 13);
        private static readonly Regex UpLine = new Regex(@"^Curtain was up for (?<dur>.+?) \((?<from>.+?) to (?<to>.+?)\)\.\s*Closed by:\s*(?<by>.+?)\.?$");

        private sealed class Model
        {
            public string Heading = "While you were away";
            public string Duration = "", From = "", To = "", ClosedBy = "";
            public readonly List<string[]> Rows = new List<string[]>();   // label, value (null: a sentence)
            public string Record = "";
            public int BodyLines;
        }

        private sealed class Item
        {
            public int Kind;   // 0 text (Y = baseline), 1 rectangle
            public string Text;
            public CurtainPainter.Face Face;
            public Color Color;
            public float X, Y, W, H, Tracking;
        }

        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Model model;
        private readonly List<KeyValuePair<double, string>> moments;   // seconds from start, kind (null: none)
        private readonly double span;
        private readonly double night;     // the clock's brightness as the window opens: 1 by day, less overnight
        private readonly Size screen;      // the screen size the text is scaled to
        private readonly Rectangle area;   // where the window may sit: the primary monitor's working area
        private readonly List<Item> items = new List<Item>();
        private readonly List<CurtainPainter.Face> faces = new List<CurtainPainter.Face>();
        private Item footer;
        private Size wanted;               // the window size the last layout asked for (before Windows clamps it)
        private float scale = 1f, trackPx;
        private int columns = 1;
        private int secondsLeft = 10;
        private Item link;                 // "VIEW FULL LOG": opens this run's events in Notepad
        private RectangleF linkHit;        // where a click counts as the link (a little larger than its text)
        private bool held;                 // the pointer is over the window: the countdown waits
        private readonly Point cursorAtOpen;

        /// <summary>What "VIEW FULL LOG" does (Run sets it; previews and tests leave it null).</summary>
        public Action ViewLog;

        public SummaryView(string text, List<KeyValuePair<double, string>> moments, double span, Size testScreen, double night)
        {
            Rectangle bounds = new Rectangle(0, 0, 1920, 1080);
            Rectangle work = bounds;
            try { bounds = Screen.PrimaryScreen.Bounds; work = Screen.PrimaryScreen.WorkingArea; } catch { }
            if (testScreen.Width > 0 && testScreen.Height > 0)
            {
                // Tests only: behave as if the primary monitor were this size (same taskbar allowance).
                work = new Rectangle(work.X, work.Y,
                                     testScreen.Width - (bounds.Width - work.Width),
                                     testScreen.Height - (bounds.Height - work.Height));
                bounds = new Rectangle(bounds.Location, testScreen);
            }
            screen = bounds.Size;
            area = work;
            model = Parse(text);
            this.moments = moments;
            this.span = span;
            this.night = night > 0 && night <= 1 ? night : 1.0;

            Text = "While you were away";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Background;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            // Lay out at full size. A long summary first flows its table into more columns, then steps its
            // text down, until it fits the screen.
            bool placed = false;
            scale = 1f;
            while (!placed && scale > 0.45f)
            {
                for (columns = 1; columns <= 3; columns++)
                {
                    if (columns > 1 && model.Rows.Count < columns * 4) break;
                    Arrange();
                    if (wanted.Height <= area.Height - 2 * EdgeGap && wanted.Width <= area.Width - 2 * EdgeGap) { placed = true; break; }
                }
                if (!placed) scale *= 0.92f;
            }
            if (!placed) { scale = 0.45f; columns = 1; Arrange(); }

            cursorAtOpen = Cursor.Position;
            KeyPreview = true;
            KeyDown += delegate { Close(); };
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                if (held) return;
                secondsLeft--;
                if (secondsLeft <= 0) { timer.Stop(); Close(); }
                else { footer.Text = FooterText(); Invalidate(); }
            };
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        private const string LinkText = "VIEW FULL LOG   →";
        private const string HeldFooter ="WAITING WHILE THE POINTER IS HERE   ·   CLICK TO CLOSE";
        private string FooterText()
        {
            return held ? HeldFooter : "CLOSES IN " + secondsLeft + " S   ·   CLICK OR PRESS A KEY TO CLOSE NOW";
        }

        private bool OverLink(Point p) { return link != null && ViewLog != null && linkHit.Contains(p); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // The countdown waits once the pointer is moved onto the window (a window that merely opens under a
            // resting pointer still closes on time), so there is time to reach "VIEW FULL LOG".
            if (!held && Cursor.Position != cursorAtOpen)
            {
                held = true;
                footer.Text = FooterText();
                Invalidate();
            }
            Cursor = OverLink(e.Location) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (held && !ClientRectangle.Contains(PointToClient(Cursor.Position)))
            {
                held = false;
                secondsLeft = 10;
                footer.Text = FooterText();
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (OverLink(e.Location))
            {
                try { ViewLog(); } catch (Exception ex) { Log.WriteOnce("summary-log", "view log failed: " + ex.Message); }
            }
            Close();
        }

        private static Model Parse(string text)
        {
            Model m = new Model();
            bool first = true;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (first) { m.Heading = line; first = false; continue; }
                m.BodyLines++;
                Match up = UpLine.Match(line);
                if (up.Success)
                {
                    m.Duration = up.Groups["dur"].Value.Trim();
                    m.From = up.Groups["from"].Value.Trim();
                    m.To = up.Groups["to"].Value.Trim();
                    m.ClosedBy = up.Groups["by"].Value.Trim();
                    continue;
                }
                if (line.StartsWith("Full record", StringComparison.OrdinalIgnoreCase)) { m.Record = line; continue; }
                if (line.StartsWith("- ")) line = line.Substring(2).Trim();
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0 && line.Length - colon - 2 <= 36) m.Rows.Add(new[] { line.Substring(0, colon), line.Substring(colon + 2) });
                else m.Rows.Add(new[] { line, null });
            }
            return m;
        }

        /// <summary>Positions everything for the current scale and sizes the window to it.</summary>
        private void Arrange()
        {
            foreach (CurtainPainter.Face f in faces) f.Font.Dispose();
            faces.Clear();
            items.Clear();
            CurtainPainter.PickFamilies();

            float su = Math.Max(48f, screen.Height / 9f) * scale;   // the curtain's unit (160 px on a 1440p screen)
            float c = su * 2.15f;
            Func<double, float> st = k => (float)(c / Math.Pow(CurtainPainter.Phi, k));
            Func<string, float, CurtainPainter.Face> face = (fam, px) => { CurtainPainter.Face x = CurtainPainter.MakeFace(fam, px); faces.Add(x); return x; };
            CurtainPainter.Face fHero = face(CurtainPainter.famLight, st(2.5));
            CurtainPainter.Face fLabel = face(CurtainPainter.famRegular, st(6));
            CurtainPainter.Face fBody = face(CurtainPainter.famSemi, st(5.5));
            CurtainPainter.Face fValue = face(CurtainPainter.famRegular, st(5.5));
            float track = fLabel.Font.Size * 0.14f;
            float pad = (float)Math.Round(st(3.5));
            float minW = Math.Max(su * 3.4f, screen.Width * 0.24f * scale);

            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics m = Graphics.FromImage(probe))
            {
                Func<string, CurtainPainter.Face, float, float> width = (t, f, tr) => CurtainPainter.Measure(m, t, f.Font, tr);
                float contentW = minW;
                float y = pad;
                float x = pad;

                y += fLabel.CapHeight;
                string heading = model.Heading.ToUpperInvariant();
                Add(heading, fLabel, CurtainPainter.Tertiary, x, y, track);
                contentW = Math.Max(contentW, width(heading, fLabel, track));

                if (model.Duration.Length > 0)
                {
                    y += st(4.5) + fHero.CapHeight;
                    Add(model.Duration, fHero, CurtainPainter.Scale(CurtainPainter.Time, night), x, y, 0);
                    contentW = Math.Max(contentW, width(model.Duration, fHero, 0));
                    y += st(5) + fLabel.CapHeight;
                    string range = (model.From + "   →   " + model.To).ToUpperInvariant();
                    string by = "CLOSED BY " + model.ClosedBy.ToUpperInvariant();
                    string dot = "   ·   ";
                    float rx = x + Add(range, fLabel, CurtainPainter.Secondary, x, y, track);
                    rx += Add(dot, fLabel, CurtainPainter.Tertiary, rx, y, track);
                    rx += Add(by, fLabel, CurtainPainter.Secondary, rx, y, track);
                    contentW = Math.Max(contentW, rx - x);
                }

                y += st(4);
                float ruleW = (float)Math.Round(c / (CurtainPainter.Phi * CurtainPainter.Phi * CurtainPainter.Phi));
                Rect(x, (float)Math.Round(y), ruleW, Math.Max(1f, (float)Math.Round(su / 70f)), CurtainPainter.Accent);
                y += st(4);

                // Rows are measured first so the timeline can span the same width as the table.
                float labelW = 0, valueW = 0, sentenceW = 0;
                foreach (string[] r in model.Rows)
                {
                    if (r[1] == null) sentenceW = Math.Max(sentenceW, width(r[0], fBody, 0));
                    else { labelW = Math.Max(labelW, width(r[0], fBody, 0)); valueW = Math.Max(valueW, width(r[1], fValue, 0)); }
                }
                float gx = st(4), colGap = st(3);
                float colW = Math.Max(sentenceW, labelW + gx + valueW);
                contentW = Math.Max(contentW, columns * colW + (columns - 1) * colGap);
                string foot = "CLOSES IN 10 S   ·   CLICK OR PRESS A KEY TO CLOSE NOW";
                contentW = Math.Max(contentW, width(foot, fLabel, track));
                contentW = Math.Max(contentW, width(HeldFooter, fLabel, track));
                if (model.Record.Length > 0) contentW = Math.Max(contentW, width(LinkText, fBody, track));

                // The session timeline.
                if (moments != null && span > 0)
                {
                    float tickLow = (float)Math.Round(st(6)), tickHigh = (float)Math.Round(st(5));
                    trackPx = Math.Max(1f, (float)Math.Round(su / 80f));
                    float mid = y + tickHigh / 2f;
                    Rect(x, (float)Math.Round(mid - trackPx / 2f), contentW, trackPx, CurtainPainter.Track);
                    foreach (KeyValuePair<double, string> mo in moments)
                    {
                        bool alert = mo.Value != "input";
                        float tx = x + (float)(contentW * Math.Max(0, Math.Min(1, mo.Key / span)));
                        float th = alert ? tickHigh : tickLow, tw = alert ? Math.Max(2f, trackPx * 2) : Math.Max(1f, trackPx);
                        Rect((float)Math.Round(tx - tw / 2f), (float)Math.Round(mid - th / 2f), tw, th, alert ? CurtainPainter.Accent : CurtainPainter.Secondary);
                    }
                    y = mid + tickHigh / 2f + st(5.5) + fLabel.CapHeight;
                    string a = model.From.ToUpperInvariant(), b = model.To.ToUpperInvariant();
                    Add(a, fLabel, CurtainPainter.Tertiary, x, y, track);
                    Add(b, fLabel, CurtainPainter.Tertiary, x + contentW - width(b, fLabel, track), y, track);
                    // Legend: two kinds, so it is always shown, and each kind differs in height as well as colour.
                    string k1 = "KEYBOARD OR MOUSE", k2 = "ALERT";
                    float lw = tickLow * 0.5f + st(6.5) + width(k1, fLabel, track) + st(4) + tickHigh * 0.2f + st(6.5) + width(k2, fLabel, track);
                    float lx = x + (contentW - lw) / 2f;
                    Rect(lx, y - tickLow * 0.75f, Math.Max(1f, trackPx), tickLow * 0.8f, CurtainPainter.Secondary);
                    lx += tickLow * 0.5f + st(6.5);
                    lx += Add(k1, fLabel, CurtainPainter.Tertiary, lx, y, track) + st(4);
                    Rect(lx, y - tickLow, Math.Max(2f, trackPx * 2), tickLow * 1.05f, CurtainPainter.Accent);
                    lx += tickHigh * 0.2f + st(6.5);
                    Add(k2, fLabel, CurtainPainter.Tertiary, lx, y, track);
                    y += st(4);
                }

                // The counts, as a table with hairlines between rows.
                float pitch = fBody.Font.Size * (columns > 1 ? 1.75f : 2.0f);
                if (model.Rows.Count > 0)
                {
                    int perColumn = (int)Math.Ceiling(model.Rows.Count / (double)columns);
                    float cw = (contentW - (columns - 1) * colGap) / columns;
                    float top = y;
                    for (int k = 0; k < columns; k++) Rect(x + k * (cw + colGap), (float)Math.Round(top), cw, 1, CurtainPainter.RuleColor);
                    for (int i = 0; i < model.Rows.Count; i++)
                    {
                        string[] r = model.Rows[i];
                        float cx = x + (i / perColumn) * (cw + colGap), ry = top + (i % perColumn) * pitch;
                        float baseline = ry + pitch / 2f + fBody.CapHeight / 2f;
                        Add(r[0], fBody, CurtainPainter.Secondary, cx, baseline, 0);
                        if (r[1] != null) Add(r[1], fValue, CurtainPainter.Primary, cx + cw - width(r[1], fValue, 0), baseline, 0);
                        Rect(cx, (float)Math.Round(ry + pitch), cw, 1, CurtainPainter.RuleColor);
                    }
                    y = top + perColumn * pitch;
                }

                link = null;
                if (model.Record.Length > 0)
                {
                    // The summary's "Full record: scripts\curtain-log.ps1" line becomes a link to this run's log.
                    y += st(4) + fBody.CapHeight;
                    link = AddItem(LinkText, fBody, CurtainPainter.Accent, x, y, track);
                    float lw = width(LinkText, fBody, track), slop = st(6);
                    linkHit = new RectangleF(x - slop, y - fBody.CapHeight - slop, lw + 2 * slop, fBody.CapHeight + 2 * slop);
                    y += st(4.5) + fLabel.CapHeight;
                }
                else y += st(4.5) + fLabel.CapHeight;
                footer = AddItem(FooterText(), fLabel, CurtainPainter.Tertiary, x, y, track);
                y += fLabel.Font.Size * 0.4f + pad;

                int w = (int)Math.Ceiling(contentW + 2 * pad), h = (int)Math.Ceiling(y);
                wanted = new Size(w, h);
                ClientSize = wanted;
                Location = new Point(Math.Max(area.Left, area.Left + (area.Width - w) / 2),
                                     Math.Max(area.Top, area.Top + (area.Height - h) / 2));
            }
        }

        private float Add(string text, CurtainPainter.Face f, Color color, float x, float baseline, float tracking)
        {
            AddItem(text, f, color, x, baseline, tracking);
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics m = Graphics.FromImage(probe))
                return CurtainPainter.Measure(m, text, f.Font, tracking);
        }

        private Item AddItem(string text, CurtainPainter.Face f, Color color, float x, float baseline, float tracking)
        {
            Item it = new Item { Kind = 0, Text = text, Face = f, Color = color, X = x, Y = baseline, Tracking = tracking };
            items.Add(it);
            return it;
        }

        private void Rect(float x, float y, float w, float h, Color color)
        {
            items.Add(new Item { Kind = 1, X = x, Y = y, W = w, H = h, Color = color });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintContent(e.Graphics); }
            catch (Exception ex) { Log.WriteOnce("summary-paint", "summary paint failed: " + ex.Message); }
        }

        /// <summary>Draws the window's content (also used to render it into an image for the README).</summary>
        internal void PaintContent(Graphics g)
        {
            g.Clear(Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Pen border = new Pen(CurtainPainter.RuleColor, 1f))
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            foreach (Item it in items)
            {
                if (it.Kind == 1)
                {
                    using (SolidBrush b = new SolidBrush(it.Color)) g.FillRectangle(b, it.X, it.Y, it.W, it.H);
                    continue;
                }
                CurtainPainter.DrawText(g, it.Text, it.Face.Font, it.Color, (float)Math.Round(it.X),
                                        (float)Math.Round(it.Y - it.Face.Ascent), it.Tracking, 0);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                foreach (CurtainPainter.Face f in faces) f.Font.Dispose();
                faces.Clear();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Tests only: where everything ended up once the window is on screen. "fits" is true when the window
        /// is inside the working area at the size its layout asked for, so nothing is clipped.
        /// </summary>
        private string DescribeLayout()
        {
            bool fits = area.Contains(Bounds) && ClientSize.Width >= wanted.Width && ClientSize.Height >= wanted.Height;
            int shown = model.BodyLines;   // every body line has a place in the layout; none is dropped
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"screen\":").Append(Json.Str(screen.Width + "x" + screen.Height));
            sb.Append(",\"area\":").Append(Json.Str(area.Width + "x" + area.Height + "+" + area.X + "+" + area.Y));
            sb.Append(",\"window\":").Append(Json.Str(Width + "x" + Height + "+" + Left + "+" + Top));
            sb.Append(",\"client\":").Append(Json.Str(ClientSize.Width + "x" + ClientSize.Height));
            sb.Append(",\"wanted\":").Append(Json.Str(wanted.Width + "x" + wanted.Height));
            sb.Append(",\"scale\":").Append(scale.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"bodyTextLines\":").Append(model.BodyLines);
            sb.Append(",\"bodyShownLines\":").Append(fits ? shown : 0);
            sb.Append(",\"fits\":").Append(fits ? "true" : "false").Append("}");
            return sb.ToString();
        }

        /// <summary>Reads the timeline the curtain wrote with this summary (null if missing or from another run).</summary>
        internal static List<KeyValuePair<double, string>> ReadTimeline(string file, string summaryFile, out double span)
        {
            span = 0;
            try
            {
                if (!File.Exists(file) || !File.Exists(summaryFile)) return null;
                if (Math.Abs((File.GetLastWriteTimeUtc(file) - File.GetLastWriteTimeUtc(summaryFile)).TotalSeconds) > 120) return null;
                DateTime start = DateTime.MinValue, end = DateTime.MinValue;
                List<KeyValuePair<double, string>> list = new List<KeyValuePair<double, string>>();
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string[] p = raw.Split('\t');
                    if (p.Length < 2) continue;
                    if (p[0] == "start") { DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start); continue; }
                    if (p[0] == "end") { DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end); continue; }
                    double s;
                    if (double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) list.Add(new KeyValuePair<double, string>(s, p[1]));
                }
                if (start == DateTime.MinValue || end <= start) return null;
                span = (end - start).TotalSeconds;
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// The run behind the last summary, as text to read: the summary, then every event that curtain logged
        /// (oldest first, the once-a-minute "nothing happened" heartbeats counted rather than listed), then the
        /// alerts sent in that time. Counts and kinds only, as in events.log: never which keys were pressed.
        /// </summary>
        internal static string BuildSessionLog(string summary, string eventsFile, string alertsFile, string timelineFile)
        {
            DateTime start = DateTime.MinValue, end = DateTime.MaxValue;
            string pid = null;
            try
            {
                foreach (string raw in File.ReadAllLines(timelineFile, Encoding.UTF8))
                {
                    string[] p = raw.Split('\t');
                    if (p.Length < 2) continue;
                    if (p[0] == "start") DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start);
                    else if (p[0] == "end") DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end);
                    else if (p[0] == "pid") pid = p[1].Trim();
                }
            }
            catch { }
            // events.log and alerts.log are in local time to the second; allow a little either side.
            DateTime from = start == DateTime.MinValue ? DateTime.MinValue : start.ToLocalTime().AddSeconds(-2);
            DateTime to = end == DateTime.MaxValue ? DateTime.MaxValue : end.ToLocalTime().AddSeconds(2);

            StringBuilder sb = new StringBuilder();
            foreach (string line in (summary ?? "").Replace("\r", "").TrimEnd().Split('\n'))
                if (!line.StartsWith("Full record", StringComparison.OrdinalIgnoreCase)) sb.Append(line).Append("\r\n");
            sb.Append("\r\n");

            List<string> events = new List<string>();
            int heartbeats = 0;
            foreach (string[] p in ReadTabbed(eventsFile, 4))
            {
                DateTime t;
                if (!TryLocal(p[0], out t)) continue;
                if (pid != null ? p[1] != pid : (t < from || t > to)) continue;
                if (p[2] == "heartbeat") { heartbeats++; continue; }
                events.Add(Stamp(t) + "   " + p[2].PadRight(16) + p[3]);
            }
            sb.Append("EVERYTHING THIS CURTAIN LOGGED, OLDEST FIRST\r\n");
            if (heartbeats > 0) sb.Append("(" + heartbeats + " once-a-minute \"curtain up, no physical input\" lines left out)\r\n");
            sb.Append("\r\n");
            if (events.Count == 0) sb.Append("Nothing was found for this curtain in events.log (it may have been trimmed).\r\n");
            foreach (string line in events) sb.Append(line).Append("\r\n");

            List<string> alerts = new List<string>();
            foreach (string[] p in ReadTabbed(alertsFile, 4))
            {
                DateTime t;
                if (!TryLocal(p[0], out t) || t < from || t > to) continue;
                alerts.Add(Stamp(t) + "   " + p[1].PadRight(16) + p[3]);
            }
            if (alerts.Count > 0)
            {
                sb.Append("\r\nALERTS IN THAT TIME (alert: sent to the phone; collapsed: held back as a repeat)\r\n\r\n");
                foreach (string line in alerts) sb.Append(line).Append("\r\n");
            }
            sb.Append("\r\nOlder curtains: scripts\\curtain-log.ps1. This file is rewritten each time VIEW FULL LOG is clicked.\r\n");
            return sb.ToString();
        }

        private static IEnumerable<string[]> ReadTabbed(string file, int fields)
        {
            string[] lines;
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    lines = reader.ReadToEnd().Split('\n');
            }
            catch { yield break; }
            foreach (string raw in lines)
            {
                string[] p = raw.TrimEnd('\r').Split(new[] { '\t' }, fields);
                if (p.Length == fields) yield return p;
            }
        }

        private static bool TryLocal(string text, out DateTime t)
        {
            return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t);
        }

        private static string Stamp(DateTime t)
        {
            return t.ToString("ddd MMM d", CultureInfo.CurrentCulture).PadRight(10) + " " +
                   t.ToString("h:mm:ss tt", CultureInfo.CurrentCulture).PadLeft(11);
        }

        internal static Size ParseScreen(string text)
        {
            if (string.IsNullOrEmpty(text)) return Size.Empty;
            string[] parts = text.ToLowerInvariant().Split('x');
            int w, h;
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out w) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out h))
                return new Size(-1, -1);
            return new Size(w, h);
        }

        public static int Run(Options o)
        {
            string text;
            try { text = File.ReadAllText(o.SummaryFile); }
            catch { return ExitCode.Error; }

            Size testScreen = ParseScreen(o.TestScreen);
            if (testScreen.Width < 0) return ExitCode.Usage;
            double span;
            List<KeyValuePair<double, string>> moments = ReadTimeline(o.TimelineFile, o.SummaryFile, out span);

            Application.EnableVisualStyles();
            using (SummaryView view = new SummaryView(text, moments, span, testScreen, CurtainPainter.NightFactor(DateTime.Now)))
            {
                string summaryText = text;
                view.ViewLog = delegate
                {
                    string file = Path.Combine(o.LocalDir, "last-session-log.txt");
                    File.WriteAllText(file, BuildSessionLog(summaryText, o.EventsFile, o.AlertsFile, o.TimelineFile), Encoding.UTF8);
                    ProcessStartInfo psi = new ProcessStartInfo("notepad.exe", "\"" + file + "\"");
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                };
                if (!string.IsNullOrEmpty(o.OutFile))
                {
                    SummaryView shown = view;
                    view.Shown += delegate
                    {
                        try { File.WriteAllText(o.OutFile, shown.DescribeLayout()); } catch { }
                    };
                }
                Application.Run(view);
            }
            return ExitCode.Ok;
        }
    }
}
