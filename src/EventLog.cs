using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
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

        public static void Write(string kind, string text)
        {
            if (path == null) return;
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

    /// <summary>The "While you were away" window shown after a PIN unlock. Closes itself after 10 seconds.</summary>
    internal sealed class SummaryView : Form
    {
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Label footer = new Label();
        private int secondsLeft = 10;

        public SummaryView(string text)
        {
            // Size everything to the screen (in pixels, so a high-DPI display does not shrink it). The old
            // fixed point sizes came out tiny on a scaled 1440p panel.
            int sh = 1080, sw = 1920;
            try { sh = Screen.PrimaryScreen.Bounds.Height; sw = Screen.PrimaryScreen.Bounds.Width; } catch { }
            float bodyPx = Math.Max(16f, sh / 62f);
            float headerPx = Math.Max(24f, sh / 38f);
            float footerPx = Math.Max(13f, sh / 92f);

            Text = "While you were away";
            Font = new Font("Segoe UI", bodyPx, GraphicsUnit.Pixel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 24);
            ForeColor = Color.Gainsboro;
            Padding = new Padding(30, 24, 30, 16);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // The first line is the heading ("While you were away"); the rest is the body.
            string all = (text ?? "").Trim();
            string heading = all, rest = "";
            int nl = all.IndexOf('\n');
            if (nl >= 0) { heading = all.Substring(0, nl).TrimEnd('\r'); rest = all.Substring(nl + 1).Trim(); }

            Label body = new Label();
            body.AutoSize = true;
            body.MaximumSize = new Size((int)Math.Min(1200, sw * 0.7), 0);
            body.Text = rest;
            body.Dock = DockStyle.Top;

            Label head = new Label();
            head.AutoSize = true;
            head.Font = new Font("Segoe UI Semibold", headerPx, GraphicsUnit.Pixel);
            head.Text = heading;
            head.Padding = new Padding(0, 0, 0, (int)(headerPx * 0.5f));
            head.Dock = DockStyle.Top;

            footer.AutoSize = true;
            footer.Font = new Font("Segoe UI", footerPx, GraphicsUnit.Pixel);
            footer.ForeColor = Color.Gray;
            footer.Dock = DockStyle.Bottom;
            footer.Padding = new Padding(0, 14, 0, 0);

            // Add body first, then the heading, so the heading docks above the body; footer at the bottom.
            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(footer);
            UpdateFooter();

            EventHandler close = delegate { Close(); };
            Click += close;
            body.Click += close;
            footer.Click += close;
            KeyPreview = true;
            KeyDown += delegate { Close(); };

            timer.Interval = 1000;
            timer.Tick += delegate
            {
                secondsLeft--;
                if (secondsLeft <= 0) { timer.Stop(); Close(); }
                else UpdateFooter();
            };
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        private void UpdateFooter()
        {
            footer.Text = "Closes in " + secondsLeft + " s. Click or press a key to close now.";
        }

        public static int Run(Options o)
        {
            string text;
            try { text = File.ReadAllText(o.SummaryFile); }
            catch { return ExitCode.Error; }
            Application.EnableVisualStyles();
            using (SummaryView view = new SummaryView(text))
            {
                Application.Run(view);
            }
            return ExitCode.Ok;
        }
    }
}
