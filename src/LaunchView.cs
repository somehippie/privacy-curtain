using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ClaudeCurtain
{
    /// <summary>
    /// The launcher's dialog, in the curtain's design language (the ground, type scale and tones of the curtain
    /// and of "While you were away"): how long the curtain stays up, as a large figure with steps, presets and
    /// the time it will close; a switch for opening Claude Code with Remote Control, with the line that says why
    /// it starts on or off; and the two buttons. curtain-launch.ps1 writes what it knows into a small file and
    /// runs "ClaudeCurtain.exe --ask-launch &lt;file&gt;"; the answer comes back in the same file, with exit code
    /// 0 to raise the curtain and Cancelled otherwise. Everything works from the keyboard: digits and the arrow
    /// keys set the hours, Tab moves, Space flips the switch, Enter raises, Esc cancels. The focused part is
    /// always ringed. The window has no frame; it can be dragged by its ground.
    /// </summary>
    internal sealed class LaunchView : Form
    {
        public const int Cancelled = 20;   // the exit code when the dialog is closed without raising the curtain
        public const int MaxHours = 24;
        internal static readonly Color Ground = Color.FromArgb(14, 14, 13);
        private static readonly int[] Presets = { 1, 2, 4, 8, 12, 24 };

        internal int Hours;               // 0 while the typed figure is not a valid number of hours
        internal bool Remote;             // the switch
        internal readonly bool RemoteAvailable;
        private readonly string note;
        private bool fresh = true;        // the next digit typed replaces the figure

        private enum Part { None, Hours, Minus, Plus, Chip, Switch, Cancel, Raise }
        private Part focus = Part.Hours, hover = Part.None, pressed = Part.None;
        private int hoverChip = -1, pressedChip = -1;

        // The layout, in window pixels (Arrange).
        private float st4, st6, st65;
        private CurtainPainter.Face fLabel, fHero, fBody, fNote, fButton, fChip;
        private readonly List<CurtainPainter.Face> faces = new List<CurtainPainter.Face>();
        private float track, pad, contentW, labelBase, heroBase, closesBase, toggleBase, noteBase, notePitch, noteX;
        private RectangleF heroBox, minusBox, plusBox, switchBox, toggleBox, cancelBox, raiseBox;
        private readonly RectangleF[] chipBoxes = new RectangleF[Presets.Length];
        private RectangleF rule;
        private readonly List<string> noteLines = new List<string>();
        private string closes = "";
        private readonly Timer minute = new Timer();

        public LaunchView(int hours, string remote, string note, Size testScreen)
        {
            Hours = Math.Max(1, Math.Min(MaxHours, hours));
            RemoteAvailable = !string.Equals(remote, "unavailable", StringComparison.OrdinalIgnoreCase);
            Remote = RemoteAvailable && string.Equals(remote, "on", StringComparison.OrdinalIgnoreCase);
            this.note = (note ?? "").Trim();

            Rectangle bounds = new Rectangle(0, 0, 1920, 1080), work = bounds;
            try { bounds = Screen.PrimaryScreen.Bounds; work = Screen.PrimaryScreen.WorkingArea; } catch { }
            if (testScreen.Width > 0 && testScreen.Height > 0) { bounds = new Rectangle(Point.Empty, testScreen); work = bounds; }

            Text = "Privacy Curtain";
            AccessibleName = "Raise the privacy curtain";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = true;
            KeyPreview = true;
            BackColor = Ground;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            try { Icon = Icon.ExtractAssociatedIcon(Options.ExePath); } catch { }

            Arrange(bounds.Height);
            // Across the middle; down, on the golden section (a little above the middle), like the curtain.
            Location = new Point(work.X + (work.Width - Width) / 2,
                                 work.Y + (int)Math.Round((work.Height - Height) / (1 + CurtainPainter.Phi)));
            UpdateCloses();
            Describe();

            minute.Interval = 1000;
            minute.Tick += delegate { if (UpdateCloses()) Invalidate(); };
            minute.Start();
            MouseWheel += (s, e) => { if (heroBox.Contains(e.Location) || focus == Part.Hours) Step(e.Delta > 0 ? 1 : -1); };
        }

        private bool quiet;   // tests only: shown without taking the focus
        protected override bool ShowWithoutActivation { get { return quiet; } }

        /// <summary>Tests only: one key as the dialog would take it (a digit, + or -, s for the switch, r to raise, x to cancel).</summary>
        internal void Feed(char ch)
        {
            if (ch >= '0' && ch <= '9') TypeDigit(ch - '0');
            else if (ch == '+') Step(1);
            else if (ch == '-') Step(-1);
            else if (ch == 's') Flip();
            else if (ch == 'r') Raise();
            else if (ch == 'x') CancelDialog();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ClassStyle |= 0x00020000;   // CS_DROPSHADOW: a soft shadow, as a frameless window has no frame to show its edge
                return p;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                minute.Dispose();
                foreach (CurtainPainter.Face f in faces) f.Font.Dispose();
                faces.Clear();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ what the launcher reads back

        /// <summary>Reads the launcher's request, shows the dialog (or draws it into --render-preview's file), answers.</summary>
        public static int Run(Options o)
        {
            int hours = 24;
            string remote = "off", text = "";
            try
            {
                foreach (string raw in File.ReadAllLines(o.LaunchFile, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim().ToLowerInvariant(), value = raw.Substring(eq + 1).Trim();
                    if (key == "hours")
                    {
                        double h;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) hours = (int)Math.Round(h);
                    }
                    else if (key == "remote") remote = value;
                    else if (key == "note") text = value;
                }
            }
            catch { return ExitCode.Error; }
            Size testScreen = SummaryView.ParseScreen(o.TestScreen);
            if (testScreen.Width < 0) return ExitCode.Usage;

            Application.EnableVisualStyles();
            using (LaunchView view = new LaunchView(hours, remote, text, testScreen))
            {
                if (!string.IsNullOrEmpty(o.PreviewFile)) { view.RenderTo(o.PreviewFile); return ExitCode.Ok; }
                if (!string.IsNullOrEmpty(o.TestKeys))
                {
                    // Tests only: off screen and without the focus, the given keys are fed to the dialog itself (never
                    // typed into whatever window is in front), and it gives up after 10 s rather than wait forever.
                    view.quiet = true;
                    view.Location = new Point(-32000, -32000);
                    Timer feed = new Timer { Interval = Math.Max(1, o.TestKeysAfter * 1000) };
                    feed.Tick += delegate { feed.Stop(); foreach (char ch in o.TestKeys) view.Feed(ch); };
                    Timer limit = new Timer { Interval = 10000 };
                    limit.Tick += delegate { limit.Stop(); view.CancelDialog(); };
                    view.Shown += delegate { feed.Start(); limit.Start(); };
                }
                else view.Shown += delegate { view.Activate(); };
                Application.Run(view);
                if (view.DialogResult != DialogResult.OK) return Cancelled;
                int chosen = view.Hours;
                File.WriteAllLines(o.LaunchFile, new[]
                {
                    "hours=" + chosen.ToString(CultureInfo.InvariantCulture),
                    "remote=" + (view.Remote ? "on" : "off"),
                }, new UTF8Encoding(false));
                return ExitCode.Ok;
            }
        }

        /// <summary>The dialog for a sample launch, as an image (the README's picture).</summary>
        internal static void RenderSample(string file, int w, int h)
        {
            using (LaunchView view = new LaunchView(8, "on", "No Claude Code session on this PC has Remote Control on.", new Size(w, h)))
                view.RenderTo(file);
        }

        internal void RenderTo(string file)
        {
            using (Bitmap b = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) PaintContent(g);
                b.Save(file, ImageFormat.Png);
            }
        }

        // ------------------------------------------------------------------ layout

        private CurtainPainter.Face Face(string family, float px)
        {
            CurtainPainter.Face f = CurtainPainter.MakeFace(family, px);
            faces.Add(f);
            return f;
        }

        /// <summary>Positions everything from the screen's height, on the curtain's type scale, and sizes the window.</summary>
        private void Arrange(int screenHeight)
        {
            CurtainPainter.PickFamilies();
            float su = Math.Max(48f, screenHeight / 9f) * 0.8f;   // the curtain's unit, a step down: this is read up close
            float c = su * CurtainPainter.ClockPerUnit;
            Func<double, float> st = k => (float)(c / Math.Pow(CurtainPainter.Phi, k));
            st4 = st(4); st6 = st(6); st65 = st(6.5);
            fLabel = Face(CurtainPainter.famRegular, st(6));
            fHero = Face(CurtainPainter.famLight, st(2.5));
            fBody = Face(CurtainPainter.famRegular, st(5.5));
            fNote = Face(CurtainPainter.famRegular, st(6));
            fButton = Face(CurtainPainter.famSemi, st(5.5));
            fChip = Face(CurtainPainter.famRegular, st(6));
            track = fLabel.Font.Size * 0.14f;
            pad = (float)Math.Round(st(3.5));
            contentW = (float)Math.Round(su * 3.4f);
            float x = pad;

            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics m = Graphics.FromImage(probe))
            {
                m.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                labelBase = pad + fLabel.CapHeight;

                // The figure, its steps at the right, and when the curtain will close.
                heroBase = (float)Math.Round(labelBase + st(4.5) + fHero.CapHeight);
                float figureW = CurtainPainter.Measure(m, "24", fHero.Font, 0);
                heroBox = new RectangleF(x, heroBase - fHero.CapHeight, figureW + st(6) + CurtainPainter.Measure(m, "HOURS", fLabel.Font, track), fHero.CapHeight);
                float sb = (float)Math.Round(st(4));
                plusBox = new RectangleF(x + contentW - sb, (float)Math.Round(heroBase - fHero.CapHeight / 2f - sb / 2f), sb, sb);
                minusBox = new RectangleF(plusBox.X - (float)Math.Round(st(6.5)) - sb, plusBox.Y, sb, sb);
                closesBase = (float)Math.Round(heroBase + st(4.5) + fBody.CapHeight);

                // Presets, one row across.
                float chipTop = (float)Math.Round(closesBase + st(4.5));
                float chipH = (float)Math.Round(st(4.4));
                float gap = (float)Math.Round(st(6.5));
                float chipW = (float)Math.Floor((contentW - gap * (Presets.Length - 1)) / Presets.Length);
                for (int i = 0; i < Presets.Length; i++) chipBoxes[i] = new RectangleF(x + i * (chipW + gap), chipTop, chipW, chipH);

                // The accent rule, as on the curtain, between the time and the rest.
                float ruleY = (float)Math.Round(chipTop + chipH + st(4));
                rule = new RectangleF(x, ruleY, (float)Math.Round(c / (CurtainPainter.Phi * CurtainPainter.Phi * CurtainPainter.Phi)), Math.Max(1f, (float)Math.Round(su / 70f)));

                // The switch, its label, and the line under it.
                float rowTop = (float)Math.Round(ruleY + rule.Height + st(4));
                float sh = (float)Math.Round(st(5)), sw = (float)Math.Round(sh * 1.9f);
                switchBox = new RectangleF(x, rowTop, sw, sh);
                noteX = (float)Math.Round(x + sw + st(5.5));
                toggleBase = (float)Math.Round(rowTop + sh / 2f + fBody.CapHeight / 2f);
                float labelW = CurtainPainter.Measure(m, ToggleText, fBody.Font, 0);
                toggleBox = RectangleF.FromLTRB(x - 4, rowTop - 4, noteX + labelW + 4, rowTop + sh + 4);
                noteLines.Clear();
                foreach (string line in Wrap(m, note, fNote.Font, x + contentW - noteX)) noteLines.Add(line);
                if (noteLines.Count > 3) { noteLines.RemoveRange(3, noteLines.Count - 3); noteLines[2] = noteLines[2].TrimEnd('.', ' ') + "…"; }
                notePitch = (float)Math.Round(fNote.Font.Size * 1.45f);
                noteBase = (float)Math.Round(toggleBase + st(6) + fNote.CapHeight);
                float below = noteLines.Count > 0 ? noteBase + (noteLines.Count - 1) * notePitch + fNote.Font.Size * 0.3f : rowTop + sh;

                // The buttons, at the right.
                float bh = (float)Math.Round(st(4));
                float bpad = (float)Math.Round(st(4.5));
                float raiseW = (float)Math.Round(CurtainPainter.Measure(m, "Raise curtain", fButton.Font, 0) + 2 * bpad);
                float cancelW = (float)Math.Round(CurtainPainter.Measure(m, "Cancel", fButton.Font, 0) + 2 * bpad);
                float btop = (float)Math.Round(below + st(3.5));
                raiseBox = new RectangleF(x + contentW - raiseW, btop, raiseW, bh);
                cancelBox = new RectangleF(raiseBox.X - (float)Math.Round(st(6)) - cancelW, btop, cancelW, bh);

                ClientSize = new Size((int)Math.Ceiling(contentW + 2 * pad), (int)Math.Ceiling(btop + bh + pad));
            }
        }

        private const string ToggleText = "Open Claude Code with Remote Control";

        /// <summary>Words into lines no wider than width.</summary>
        private static IEnumerable<string> Wrap(Graphics m, string text, Font font, float width)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            string line = "";
            foreach (string word in text.Split(' '))
            {
                if (word.Length == 0) continue;
                string tryLine = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && CurtainPainter.Measure(m, tryLine, font, 0) > width) { yield return line; line = word; }
                else line = tryLine;
            }
            if (line.Length > 0) yield return line;
        }

        // ------------------------------------------------------------------ state

        /// <summary>Refreshes the "Closes at" line; true when it changed.</summary>
        private bool UpdateCloses()
        {
            string text;
            if (Hours < 1) text = "Choose 1 to 24 hours";
            else
            {
                DateTime now = DateTime.Now, at = now.AddHours(Hours);
                string day = at.Date == now.Date ? "today" : at.Date == now.Date.AddDays(1) ? "tomorrow" : at.ToString("dddd", CultureInfo.CurrentCulture);
                text = "Closes at " + at.ToString("t", CultureInfo.CurrentCulture) + " " + day;
            }
            bool changed = text != closes;
            closes = text;
            return changed;
        }

        private void Describe()
        {
            AccessibleDescription = (Hours >= 1 ? Hours + (Hours == 1 ? " hour. " : " hours. ") + closes + ". " : closes + ". ") +
                                    (RemoteAvailable ? "Remote Control " + (Remote ? "on" : "off") + ". " : "") + note;
        }

        private void Changed()
        {
            UpdateCloses();
            Describe();
            Invalidate();
        }

        /// <summary>A digit typed: it starts a new figure, or extends it while that stays within 24.</summary>
        internal void TypeDigit(int d)
        {
            if (fresh || Hours == 0) Hours = d;
            else Hours = Hours * 10 + d <= MaxHours ? Hours * 10 + d : d;
            fresh = false;
            focus = Part.Hours;
            Changed();
        }

        internal void Backspace()
        {
            Hours /= 10;
            fresh = false;
            Changed();
        }

        /// <summary>One hour more or less, kept within 1 to 24.</summary>
        internal void Step(int by)
        {
            Hours = Math.Max(1, Math.Min(MaxHours, (Hours < 1 ? 0 : Hours) + by));
            fresh = true;
            focus = Part.Hours;
            Changed();
        }

        internal void Pick(int hours)
        {
            Hours = hours;
            fresh = true;
            Changed();
        }

        internal void Flip()
        {
            if (!RemoteAvailable) return;
            Remote = !Remote;
            Changed();
        }

        private void Raise()
        {
            if (Hours < 1) { focus = Part.Hours; Invalidate(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        internal void CancelDialog()
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        // ------------------------------------------------------------------ keyboard

        private Part[] Order()
        {
            return RemoteAvailable ? new[] { Part.Hours, Part.Switch, Part.Cancel, Part.Raise } : new[] { Part.Hours, Part.Cancel, Part.Raise };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0;
            switch (key)
            {
                case Keys.Tab:
                {
                    Part[] order = Order();
                    int at = Math.Max(0, Array.IndexOf(order, focus));
                    focus = order[(at + (shift ? order.Length - 1 : 1)) % order.Length];
                    Invalidate();
                    return true;
                }
                case Keys.Enter:
                    if (focus == Part.Cancel) CancelDialog(); else Raise();
                    return true;
                case Keys.Escape:
                    CancelDialog();
                    return true;
                case Keys.Up: case Keys.Right: Step(1); return true;
                case Keys.Down: case Keys.Left: Step(-1); return true;
                case Keys.PageUp: Pick(NextPreset(1)); return true;
                case Keys.PageDown: Pick(NextPreset(-1)); return true;
                case Keys.Home: Pick(1); return true;
                case Keys.End: Pick(MaxHours); return true;
                case Keys.Back: focus = Part.Hours; Backspace(); return true;
                case Keys.Space:
                    if (focus == Part.Switch) Flip();
                    else if (focus == Part.Cancel) CancelDialog();
                    else if (focus == Part.Raise) Raise();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar >= '0' && e.KeyChar <= '9') { TypeDigit(e.KeyChar - '0'); e.Handled = true; }
            base.OnKeyPress(e);
        }

        private int NextPreset(int dir)
        {
            if (dir > 0) { foreach (int p in Presets) if (p > Hours) return p; return MaxHours; }
            for (int i = Presets.Length - 1; i >= 0; i--) if (Presets[i] < Hours) return Presets[i];
            return 1;
        }

        // ------------------------------------------------------------------ mouse

        private Part HitTest(PointF p, out int chip)
        {
            chip = -1;
            if (minusBox.Contains(p)) return Part.Minus;
            if (plusBox.Contains(p)) return Part.Plus;
            for (int i = 0; i < chipBoxes.Length; i++) if (chipBoxes[i].Contains(p)) { chip = i; return Part.Chip; }
            if (RemoteAvailable && toggleBox.Contains(p)) return Part.Switch;
            if (cancelBox.Contains(p)) return Part.Cancel;
            if (raiseBox.Contains(p)) return Part.Raise;
            if (heroBox.Contains(p)) return Part.Hours;
            return Part.None;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
            {
                // The ground drags the window; everything that does something stays a click.
                long lp = m.LParam.ToInt64();
                Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                int chip;
                if (HitTest(PointToClient(screen), out chip) == Part.None) m.Result = (IntPtr)HTCAPTION;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int chip;
            Part p = HitTest(e.Location, out chip);
            if (p != hover || chip != hoverChip) { hover = p; hoverChip = chip; Invalidate(); }
            Cursor = p == Part.None || p == Part.Hours ? Cursors.Default : Cursors.Hand;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = Part.None; hoverChip = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = HitTest(e.Location, out pressedChip); Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int chip;
            Part p = HitTest(e.Location, out chip);
            Part was = pressed;
            int wasChip = pressedChip;
            pressed = Part.None; pressedChip = -1;
            Invalidate();
            if (e.Button == MouseButtons.Left && p == was && chip == wasChip)
            {
                switch (p)
                {
                    case Part.Minus: Step(-1); break;
                    case Part.Plus: Step(1); break;
                    case Part.Chip: Pick(Presets[chip]); break;
                    case Part.Switch: focus = Part.Switch; Flip(); break;
                    case Part.Cancel: CancelDialog(); break;
                    case Part.Raise: Raise(); break;
                    case Part.Hours: focus = Part.Hours; Invalidate(); break;
                }
            }
            base.OnMouseUp(e);
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintContent(e.Graphics); }
            catch (Exception ex) { Log.WriteOnce("launch-paint", "launch dialog paint failed: " + ex.Message); }
        }

        internal void PaintContent(Graphics g)
        {
            g.Clear(Ground);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Pen border = new Pen(CurtainPainter.RuleColor, 1f))
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            Put(g, "RAISE THE CURTAIN", fLabel, CurtainPainter.Tertiary, pad, labelBase, track);

            // The figure: the hours in the clock's tone, its unit after it, a line under it while it has the focus.
            string figure = Hours.ToString(CultureInfo.InvariantCulture);
            float figureW = Wide(g, figure, fHero, 0);
            float ink = InkLeft(figure, fHero);
            Put(g, figure, fHero, Hours >= 1 ? CurtainPainter.Time : CurtainPainter.Tertiary, pad - ink, heroBase, 0);
            Put(g, Hours == 1 ? "HOUR" : "HOURS", fLabel, CurtainPainter.Tertiary, pad - ink + figureW + st6, heroBase, track);
            if (focus == Part.Hours)
                Fill(g, CurtainPainter.Secondary, new RectangleF(pad, (float)Math.Round(heroBase + st65), Math.Max(figureW - ink, st4), 2));

            StepButton(g, minusBox, false, Hours > 1, Part.Minus);
            StepButton(g, plusBox, true, Hours < MaxHours, Part.Plus);
            Put(g, closes, fBody, Hours >= 1 ? CurtainPainter.Secondary : CurtainPainter.Warn, pad, closesBase, 0);

            for (int i = 0; i < Presets.Length; i++)
            {
                RectangleF r = chipBoxes[i];
                bool on = Hours == Presets[i];
                bool lit = hover == Part.Chip && hoverChip == i;
                using (GraphicsPath path = Round(r, r.Height / 2f))
                {
                    if (on) using (SolidBrush b = new SolidBrush(CurtainPainter.Primary)) g.FillPath(b, path);
                    else using (Pen p = new Pen(lit ? CurtainPainter.Tertiary : CurtainPainter.RuleColor, 1f)) g.DrawPath(p, path);
                }
                string t = Presets[i] + " h";
                float tw = Wide(g, t, fChip, 0);
                Put(g, t, fChip, on ? Ground : lit ? CurtainPainter.Primary : CurtainPainter.Secondary,
                     r.X + (r.Width - tw) / 2f, r.Y + r.Height / 2f + fChip.CapHeight / 2f, 0);
            }

            Fill(g, CurtainPainter.Accent, rule);

            // The switch: light when on, a dark track when off, greyed out without Claude Code.
            {
                RectangleF r = switchBox;
                bool lit = hover == Part.Switch;
                Color trackColor = !RemoteAvailable ? CurtainPainter.Track : Remote ? (lit ? CurtainPainter.Hero : CurtainPainter.Primary) : CurtainPainter.Track;
                using (GraphicsPath path = Round(r, r.Height / 2f))
                {
                    using (SolidBrush b = new SolidBrush(trackColor)) g.FillPath(b, path);
                    if (!Remote || !RemoteAvailable)
                        using (Pen p = new Pen(lit && RemoteAvailable ? CurtainPainter.Tertiary : CurtainPainter.RuleColor, 1f)) g.DrawPath(p, path);
                }
                float k = r.Height - 6;
                float kx = Remote && RemoteAvailable ? r.Right - 3 - k : r.X + 3;
                Color knob = !RemoteAvailable ? CurtainPainter.RuleColor : Remote ? Ground : CurtainPainter.Tertiary;
                using (SolidBrush b = new SolidBrush(knob)) g.FillEllipse(b, kx, r.Y + 3, k, k);
                if (focus == Part.Switch) Ring(g, r, r.Height / 2f);
                Put(g, ToggleText, fBody, RemoteAvailable ? CurtainPainter.Primary : CurtainPainter.Tertiary, noteX, toggleBase, 0);
                for (int i = 0; i < noteLines.Count; i++)
                    Put(g, noteLines[i], fNote, CurtainPainter.Tertiary, noteX, noteBase + i * notePitch, 0);
            }

            // The buttons: Cancel outlined, Raise filled light (dimmed while the figure is not valid).
            using (GraphicsPath path = Round(cancelBox, 6))
            using (Pen p = new Pen(hover == Part.Cancel ? CurtainPainter.Tertiary : CurtainPainter.RuleColor, 1f))
                g.DrawPath(p, path);
            Centered(g, "Cancel", fButton, hover == Part.Cancel ? CurtainPainter.Primary : CurtainPainter.Secondary, cancelBox);
            if (focus == Part.Cancel) Ring(g, cancelBox, 6);
            bool ready = Hours >= 1;
            using (GraphicsPath path = Round(raiseBox, 6))
            using (SolidBrush b = new SolidBrush(!ready ? CurtainPainter.Track : hover == Part.Raise ? CurtainPainter.Hero : CurtainPainter.Primary))
                g.FillPath(b, path);
            Centered(g, "Raise curtain", fButton, ready ? Ground : CurtainPainter.Tertiary, raiseBox);
            if (focus == Part.Raise) Ring(g, raiseBox, 6);
        }

        private void StepButton(Graphics g, RectangleF r, bool plus, bool enabled, Part part)
        {
            bool lit = enabled && hover == part;
            using (GraphicsPath path = Round(r, 6))
            using (Pen p = new Pen(lit ? CurtainPainter.Tertiary : CurtainPainter.RuleColor, 1f))
                g.DrawPath(p, path);
            Color ink = !enabled ? CurtainPainter.RuleColor : lit ? CurtainPainter.Primary : CurtainPainter.Secondary;
            float len = (float)Math.Round(r.Width * 0.36f), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (Pen p = new Pen(ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(p, cx - len / 2f, cy, cx + len / 2f, cy);
                if (plus) g.DrawLine(p, cx, cy - len / 2f, cx, cy + len / 2f);
            }
        }

        /// <summary>The focus ring: a 2-pixel line around the part, a few pixels out.</summary>
        private static void Ring(Graphics g, RectangleF r, float radius)
        {
            RectangleF o = RectangleF.Inflate(r, 4, 4);
            using (GraphicsPath path = Round(o, radius + 4))
            using (Pen p = new Pen(CurtainPainter.Secondary, 2f))
                g.DrawPath(p, path);
        }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static void Fill(Graphics g, Color color, RectangleF r)
        {
            using (SolidBrush b = new SolidBrush(color)) g.FillRectangle(b, r);
        }

        private static void Put(Graphics g, string text, CurtainPainter.Face f, Color color, float x, float baseline, float tracking)
        {
            CurtainPainter.DrawText(g, text, f.Font, color, (float)Math.Round(x), (float)Math.Round(baseline - f.Ascent), tracking, 0);
        }

        private static float Wide(Graphics g, string text, CurtainPainter.Face f, float tracking)
        {
            return CurtainPainter.Measure(g, text, f.Font, tracking);
        }

        private static float InkLeft(string text, CurtainPainter.Face f)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddString(text, f.Font.FontFamily, (int)f.Font.Style, f.Font.Size, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = p.GetBounds();
                return b.Width > 0 ? b.Left : 0;
            }
        }

        private static void Centered(Graphics g, string text, CurtainPainter.Face f, Color color, RectangleF r)
        {
            float tw = Wide(g, text, f, 0);
            Put(g, text, f, color, r.X + (r.Width - tw) / 2f, r.Y + r.Height / 2f + f.CapHeight / 2f, 0);
        }

        // ------------------------------------------------------------------ checks (self-test)

        /// <summary>The parts' boxes, for the self-test to check that nothing overlaps and all fits.</summary>
        internal IEnumerable<RectangleF> Parts()
        {
            yield return heroBox; yield return minusBox; yield return plusBox;
            foreach (RectangleF r in chipBoxes) yield return r;
            yield return switchBox; yield return cancelBox; yield return raiseBox;
        }
    }
}
