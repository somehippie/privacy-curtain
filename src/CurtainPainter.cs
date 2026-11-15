using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeCurtain
{
    /// <summary>
    /// Draws the curtain. Everything follows one proportional system built on the golden ratio φ ≈ 1.618.
    ///
    /// Grid    On a landscape screen the composition is two columns whose widths are φ : 1: the time and the
    ///         message on the left, live data on the right, a hairline between them. On a portrait screen the
    ///         data moves under the message. The composition rests on the screen's golden section (the space
    ///         above it and the space below it are 1 : φ).
    /// Type    One family (Bahnschrift, a DIN design that ships with Windows). Every size is the clock size
    ///         divided by a power of φ; every gap is a step on the same scale. Large type is aligned by its
    ///         ink rather than its side bearing, the colon sits on the optical centre of the digits, and
    ///         small capitals are letter-spaced.
    /// Data    Ten-minute sparklines for CPU and network, proportion bars for memory and disk, and a
    ///         forecast table with weather symbols drawn in code. Values are always printed as text beside
    ///         their graphic, so nothing depends on reading a chart.
    /// Colour  Warm neutrals in four text tones plus one accent. Contrast on black is checked by the
    ///         self-tests: every text tone meets WCAG AA, body text meets AAA.
    /// Motion  The composition is rendered once into an image (again only when something on it changes)
    ///         and moved as one rigid piece, a whole pixel at a time, so nothing shimmers. It drifts against
    ///         burn-in with horizontal and vertical periods in the ratio 1 : φ, a path that never repeats.
    ///         It fades in when the curtain rises and cross-fades when the minute turns. The accent rule
    ///         breathes slowly while Claude works. PIN dots ease in as they are typed, and a wrong PIN shakes
    ///         them before they fade.
    /// </summary>
    internal sealed class CurtainPainter : IDisposable
    {
        public const double Phi = 1.6180339887498949;
        public const double DriftPeriodSeconds = 300;   // one horizontal sweep; the vertical one is φ times longer
        public const double NightLevel = 0.70;          // the clock's brightness overnight
        public const int FadeInMs = 900;
        public const int CrossFadeMs = 600;
        public const int DotPopMs = 180;
        public const int ShakeMs = 480;
        public const int SwapMs = 200;
        public const double BreathSeconds = 7.0;

        // ---- Colour tokens. Text tones are checked against WCAG contrast on black by the self-tests.
        internal static readonly Color Hero = Color.FromArgb(226, 222, 214);       // the time
        internal static readonly Color Primary = Color.FromArgb(206, 202, 194);    // title, highs
        internal static readonly Color Secondary = Color.FromArgb(160, 156, 149);  // body, values, chart marks (AAA)
        internal static readonly Color Tertiary = Color.FromArgb(126, 122, 116);   // labels, notes (AA)
        internal static readonly Color RuleColor = Color.FromArgb(54, 52, 49);     // hairlines (decorative)
        internal static readonly Color Track = Color.FromArgb(40, 39, 37);         // chart tracks and baselines
        internal static readonly Color Accent = Color.FromArgb(217, 119, 87);      // the one accent: the rule
        internal static readonly Color Wrong = Color.FromArgb(234, 116, 104);
        internal static readonly Color Warn = Color.FromArgb(224, 178, 98);

        /// <summary>Every colour used for text, with the contrast on black it must reach.</summary>
        internal static readonly KeyValuePair<string, Color>[] TextTones =
        {
            new KeyValuePair<string, Color>("hero", Hero),
            new KeyValuePair<string, Color>("hero at night", Scale(Hero, NightLevel)),
            new KeyValuePair<string, Color>("primary", Primary),
            new KeyValuePair<string, Color>("secondary", Secondary),
            new KeyValuePair<string, Color>("tertiary", Tertiary),
            new KeyValuePair<string, Color>("wrong", Wrong),
            new KeyValuePair<string, Color>("warn", Warn),
        };

        internal const string AdminText = "An administrator window has focus. If the PIN does not respond, press the Windows key first.";
        private const string HintText = "ENTER TO UNLOCK   ·   ESC TO CLEAR";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>Previews and tests drive the animations with this instead of the real clock.</summary>
        public long? FixedNowMs;

        private Layout cur, prev;
        private string curKey;
        private long firstPaint = -1, swapStart;
        private int lastState, shownDigits, wrongDots;
        private readonly long[] dotBorn = new long[PinGate.MaxBuffer];
        private long wrongStart = -100000, wrongEnd = -100000, stateSince = -100000;
        private bool fast;
        private Point lastAt;

        /// <summary>True while a short animation is running; the window then repaints at ~60 frames a second.</summary>
        public bool NeedsFastFrames { get { return fast; } }

        /// <summary>The layout and where it was last drawn (for the construction overlay in previews).</summary>
        internal Layout Current { get { return cur; } }
        internal Point LastPlacement { get { return lastAt; } }

        public void Dispose()
        {
            if (cur != null) cur.Dispose();
            if (prev != null) prev.Dispose();
            cur = prev = null;
        }

        private long Now() { return FixedNowMs.HasValue ? FixedNowMs.Value : Clock.ElapsedMilliseconds; }

        // ================================================================== frame

        public void Paint(Graphics g, DisplayState s, int w, int h)
        {
            long now = Now();
            g.Clear(Color.Black);
            fast = false;
            if (s == null || w < 80 || h < 80) return;
            if (firstPaint < 0) firstPaint = now;

            string key = w + "x" + h + "|" + s.ContentKey();
            if (cur == null || key != curKey)
            {
                Layout fresh = Compose(s, w, h);
                if (fresh != null)
                {
                    bool minuteTurned = cur != null && cur.ClockText != fresh.ClockText && now - firstPaint > FadeInMs;
                    if (minuteTurned)
                    {
                        if (prev != null) prev.Dispose();
                        prev = cur;
                        swapStart = now;
                    }
                    else if (cur != null) cur.Dispose();
                    cur = fresh;
                    curKey = key;
                }
            }
            if (cur == null) return;
            TrackPin(s, now);

            float fadeIn = OutCubic(Clamp01((now - firstPaint) / (float)FadeInMs));
            float swap = 1f;
            if (prev != null)
            {
                swap = Clamp01((now - swapStart) / (float)CrossFadeMs);
                if (swap >= 1f) { prev.Dispose(); prev = null; }
            }

            double secs = s.Drift * 0.2;   // Drift counts 200 ms ticks
            g.CompositingMode = CompositingMode.SourceOver;
            if (prev != null) DrawBlock(g, prev, w, h, secs, fadeIn);
            lastAt = DrawBlock(g, cur, w, h, secs, fadeIn * (prev != null ? InOutSine(swap) : 1f));
            DrawLive(g, cur, s, lastAt, now, fadeIn);

            bool pinAnim = now - wrongStart < ShakeMs + SwapMs + 60 || now - wrongEnd < SwapMs + 60 || now - stateSince < SwapMs + 60;
            for (int i = 0; i < shownDigits && !pinAnim; i++) pinAnim = now - dotBorn[i] < DotPopMs + 40;
            fast = now - firstPaint < FadeInMs + 60 || prev != null || pinAnim;
        }

        private void TrackPin(DisplayState s, long now)
        {
            if (s.PinState != lastState)
            {
                if (s.PinState == PinGate.StateWrong) { wrongStart = now; wrongDots = Math.Max(1, shownDigits); }
                if (lastState == PinGate.StateWrong) wrongEnd = now;
                stateSince = now;
                lastState = s.PinState;
            }
            int d = Math.Min(Math.Max(s.Digits, 0), PinGate.MaxBuffer);
            for (int i = shownDigits; i < d; i++) dotBorn[i] = now;
            shownDigits = d;
        }

        private static Point DrawBlock(Graphics g, Layout l, int w, int h, double secs, float alpha)
        {
            Point at = Place(l.Block.Width, l.Block.Height, w, h, secs);
            if (alpha <= 0.004f) return at;
            if (alpha >= 0.996f) { g.DrawImageUnscaled(l.Block, at); return at; }
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = alpha;
                ia.SetColorMatrix(cm);
                g.DrawImage(l.Block, new Rectangle(at.X, at.Y, l.Block.Width, l.Block.Height),
                            0, 0, l.Block.Width, l.Block.Height, GraphicsUnit.Pixel, ia);
            }
            return at;
        }

        /// <summary>The parts that move on their own: the breathing rule and the PIN row.</summary>
        private void DrawLive(Graphics g, Layout l, DisplayState s, Point at, long now, float fadeIn)
        {
            float ox = at.X + l.OriginX, oy = at.Y + l.OriginY;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            double breath = 0.62 + 0.38 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (now / 1000.0) / BreathSeconds));
            using (SolidBrush b = new SolidBrush(Fade(Accent, (float)breath * fadeIn)))
                g.FillRectangle(b, ox + l.Rule.X, oy + l.Rule.Y, l.Rule.Width, l.Rule.Height);

            float u = l.Unit, dot = l.DotSize, pitch = dot * 2.1f;
            float x0 = ox + l.PinX + dot / 2f, cy = oy + l.PinDotsY;
            Face body = l.Body, label = l.Label;

            // Dots: each eases in as it is typed. A wrong PIN shakes them, then they fade and the note fades in.
            int count = shownDigits;
            float dotAlpha = 1f, shake = 0f;
            long sinceWrong = now - wrongStart;
            bool wrongShowing = s.PinState == PinGate.StateWrong;
            if (wrongShowing && sinceWrong < ShakeMs + SwapMs)
            {
                count = wrongDots;
                double t = sinceWrong / 1000.0;
                shake = (float)(u * 0.09 * Math.Exp(-6.0 * t) * Math.Sin(2 * Math.PI * 7.0 * t));
                if (sinceWrong > ShakeMs) dotAlpha = 1f - Clamp01((sinceWrong - ShakeMs) / (float)SwapMs);
                if (sinceWrong >= ShakeMs + SwapMs) count = 0;
            }
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    float pop = wrongShowing ? 1f : OutBack(Clamp01((now - dotBorn[i]) / (float)DotPopMs));
                    float a = (wrongShowing ? dotAlpha : Clamp01((now - dotBorn[i]) / (DotPopMs * 0.6f))) * fadeIn;
                    float d = dot * (0.35f + 0.65f * pop);
                    using (SolidBrush b = new SolidBrush(Fade(Hero, a)))
                        g.FillEllipse(b, x0 + i * pitch + shake - d / 2f, cy - d / 2f, d, d);
                }
            }

            float noteBase = cy + body.CapHeight / 2f;
            if (wrongShowing || now - wrongEnd < SwapMs)
            {
                float a = wrongShowing ? Clamp01((sinceWrong - ShakeMs) / (float)SwapMs) : 1f - Clamp01((now - wrongEnd) / (float)SwapMs);
                DrawLiveText(g, "Wrong PIN", body, Fade(Wrong, a * fadeIn), ox + l.PinX, noteBase, 0);
            }
            if (s.PinState == PinGate.StateCooldown)
            {
                float a = Clamp01((now - stateSince) / (float)SwapMs) * fadeIn;
                DrawLiveText(g, "Too many wrong tries. Wait " + s.CooldownLeft + " s.", body, Fade(Warn, a), ox + l.PinX, noteBase, 0);
            }
            if (s.AdminNotice)
            {
                DrawLiveText(g, AdminText, l.Small, Fade(Warn, fadeIn), ox + l.PinX, oy + l.PinHintBaseline, 0);
            }
            else if (s.PinState == PinGate.StateTyping)
            {
                float a = Clamp01((now - stateSince) / (float)SwapMs) * fadeIn;
                DrawLiveText(g, HintText, label, Fade(Tertiary, a), ox + l.PinX, oy + l.PinHintBaseline, l.Track);
            }
        }

        private static void DrawLiveText(Graphics g, string text, Face f, Color c, float x, float baseline, float tracking)
        {
            if (c.A < 3) return;
            DrawText(g, text, f.Font, c, (float)Math.Round(x), (float)Math.Round(baseline - f.Ascent), tracking, 0);
        }

        /// <summary>
        /// Where the composition sits at a given time: centred across, on the golden section down, plus the
        /// drift. Whole pixels only, so it moves rigidly and never resamples.
        /// </summary>
        public static Point Place(int bw, int bh, int w, int h, double seconds)
        {
            double freeX = Math.Max(0, w - bw);
            double freeY = Math.Max(0, h - bh);
            double restX = freeX / 2.0;
            double restY = freeY / (1.0 + Phi);
            double ax = Math.Min(w * 0.05, restX);
            double ay = Math.Min(h * 0.05, Math.Min(restY, freeY - restY));
            double tx = 2 * Math.PI * seconds / DriftPeriodSeconds;
            double ty = tx / Phi;
            return new Point((int)Math.Round(restX + ax * Math.Sin(tx)), (int)Math.Round(restY + ay * Math.Sin(ty)));
        }

        /// <summary>The clock's brightness for a local time: full by day, NightLevel overnight, hour-long ramps.</summary>
        public static double NightFactor(DateTime local)
        {
            double hour = local.TimeOfDay.TotalHours, dim;
            if (hour >= 22.5 || hour < 6.5) dim = 1;
            else if (hour >= 21.5) dim = Smooth(hour - 21.5);
            else if (hour < 7.5) dim = 1 - Smooth(hour - 6.5);
            else dim = 0;
            return 1 - (1 - NightLevel) * dim;
        }

        // ================================================================== composition

        internal sealed class Face
        {
            public Font Font;
            public float Ascent;      // line top to baseline
            public float CapHeight;   // baseline to the top of a capital
        }

        internal sealed class Layout : IDisposable
        {
            public Bitmap Block;
            public float OriginX, OriginY;            // layout coordinates -> bitmap coordinates
            public float Unit, DotSize, PinX, PinDotsY, PinHintBaseline, Track;
            public RectangleF Rule, LeftCol, RightCol;
            public bool Landscape, HasRight;
            public string ClockText = "";
            public Face Body, Label, Small;
            public readonly List<KeyValuePair<string, float>> Baselines = new List<KeyValuePair<string, float>>();
            private readonly List<Face> faces = new List<Face>();

            public Face Own(Face f) { faces.Add(f); return f; }

            public void Dispose()
            {
                if (Block != null) Block.Dispose();
                Block = null;
                foreach (Face f in faces) f.Font.Dispose();
                faces.Clear();
            }
        }

        private sealed class Op
        {
            public int Kind;            // 0 text, 1 rect, 2 dot, 3 weather symbol, 4 sparkline, 5 bar, 6 droplet
            public string Text;
            public Font Font;
            public float Ascent;
            public Color Color;
            public float X, Y, W, H;    // text: left, baseline, max width (0 = none); others: box
            public float Tracking;
            public float[] Data;
            public double Value, Max;
        }

        internal static string famLight, famSemi, famRegular;

        internal static void PickFamilies()
        {
            if (famRegular != null) return;
            famLight = Pick("Bahnschrift Light", "Segoe UI Light");
            famSemi = Pick("Bahnschrift SemiLight", "Segoe UI Semilight");
            famRegular = Pick("Bahnschrift", "Segoe UI");
        }

        private static string Pick(string preferred, string fallback)
        {
            try { using (FontFamily f = new FontFamily(preferred)) { return preferred; } }
            catch { return fallback; }
        }

        internal static readonly StringFormat Typo = MakeTypo();

        private static StringFormat MakeTypo()
        {
            StringFormat f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            return f;
        }

        internal static Face MakeFace(string family, float px)
        {
            Face face = new Face();
            face.Font = new Font(family, Math.Max(8f, px), FontStyle.Regular, GraphicsUnit.Pixel);
            FontFamily ff = face.Font.FontFamily;
            float em = ff.GetEmHeight(FontStyle.Regular);
            face.Ascent = face.Font.Size * ff.GetCellAscent(FontStyle.Regular) / em;
            RectangleF b = Ink("H", face.Font);
            face.CapHeight = b.Height > 0 ? face.Ascent - b.Top : face.Font.Size * 0.7f;
            return face;
        }

        /// <summary>The ink of a string drawn at (0,0), in the same coordinates DrawString uses.</summary>
        private static RectangleF Ink(string text, Font font)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddString(text, font.FontFamily, (int)font.Style, font.Size, PointF.Empty, Typo);
                return p.GetBounds();
            }
        }

        private static readonly Regex WeatherHead = new Regex(@"^(?<place>.+?)\s+forecast\s*\(as of (?<asof>[^)]+)\)\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex WeatherRow = new Regex(
            @"^(?<day>[^:]+):\s+(?:Hi\s+(?<hi>-?\d+\S?))?\s*/?\s*(?:Lo\s+(?<lo>-?\d+\S?))?\s+(?<desc>.+?)(?:\s+\((?<pop>\d+)% precip\))?\s*$",
            RegexOptions.IgnoreCase);

        /// <summary>Lays the composition out at the largest scale that leaves room for the drift.</summary>
        internal static Layout Compose(DisplayState s, int w, int h)
        {
            PickFamilies();
            bool landscape = w >= h * 1.2;
            float unit = landscape ? Math.Max(16f, Math.Min(w, h) / 9f) : Math.Max(16f, Math.Min(w / 6.6f, h / 11.5f));
            for (int attempt = 0; attempt < 12; attempt++)
            {
                Layout l = ComposeAt(s, unit, landscape);
                if (l == null) return null;
                if ((l.Block.Width <= w * 0.90f && l.Block.Height <= h * 0.90f) || attempt == 11) return l;
                l.Dispose();
                unit *= 0.92f;
            }
            return null;
        }

        private static Layout ComposeAt(DisplayState s, float unit, bool landscape)
        {
            float c = unit * 2.15f;
            Func<double, float> st = k => (float)(c / Math.Pow(Phi, k));
            Layout L = new Layout();
            L.Unit = unit;
            L.Landscape = landscape;
            Face fClock = L.Own(MakeFace(famLight, c));
            Face fSuffix = L.Own(MakeFace(famSemi, st(4)));
            Face fTitle = L.Own(MakeFace(famSemi, st(4)));
            Face fBody = L.Own(MakeFace(famSemi, st(5)));
            Face fData = L.Own(MakeFace(famRegular, st(5.5)));
            Face fLabel = L.Own(MakeFace(famRegular, st(6)));
            Face fSmall = L.Own(MakeFace(famRegular, st(6)));
            float track = fLabel.Font.Size * 0.14f;
            L.Body = fBody; L.Label = fLabel; L.Small = fSmall; L.Track = track;
            Color hero = Scale(Hero, s.NightDim > 0 ? s.NightDim : 1.0);

            List<Op> ops = new List<Op>();
            try
            {
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics m = Graphics.FromImage(probe))
                {
                    m.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    // ---------------- left column: x = 0 is its edge, y = 0 the top of the digits
                    float y = fClock.CapHeight;
                    L.Baselines.Add(new KeyValuePair<string, float>("C", y));
                    float leftW = AddClock(ops, m, s.Clock ?? "", fClock, fSuffix, hero, track, y, st(6), L);

                    string date = (s.Date ?? "").ToUpperInvariant();
                    string zone = (s.Zone ?? "").Replace(",", "").ToUpperInvariant();
                    y += st(4) + fLabel.CapHeight;
                    L.Baselines.Add(new KeyValuePair<string, float>("C / φ⁶", y));
                    float x = AddText(ops, m, date, fLabel, Secondary, 0, y, track);
                    if (zone.Length > 0)
                    {
                        x += AddText(ops, m, "   ·   ", fLabel, Tertiary, x, y, track);
                        x += AddText(ops, m, zone, fLabel, Tertiary, x, y, track);
                    }
                    leftW = Math.Max(leftW, x);

                    y += st(3);
                    float ruleW = (float)Math.Round(c / (Phi * Phi)), ruleH = Math.Max(1f, (float)Math.Round(unit / 70f));
                    L.Rule = new RectangleF(0, (float)Math.Round(y), ruleW, ruleH);
                    y += ruleH + st(3) + fTitle.CapHeight;
                    string title = "PC in use by Claude";
                    L.Baselines.Add(new KeyValuePair<string, float>("C / φ⁴", y));
                    leftW = Math.Max(leftW, AddText(ops, m, title, fTitle, Primary, -InkLeft(title, fTitle), y, 0));

                    y += st(5) + fBody.CapHeight;
                    L.Baselines.Add(new KeyValuePair<string, float>("C / φ⁵", y));
                    string l1 = "Keyboard and mouse are locked while Claude works.";
                    string l2 = "Any attempt to use, unlock or interrupt this PC is logged.";
                    leftW = Math.Max(leftW, AddText(ops, m, l1, fBody, s.Bright ? hero : Secondary, 0, y, 0));
                    y += fBody.Font.Size * 1.5f;
                    leftW = Math.Max(leftW, AddText(ops, m, l2, fBody, Secondary, 0, y, 0));
                    float textBottom = y + fBody.Font.Size * 0.3f;

                    // The PIN row is drawn live (it animates); here it only gets its reserved space.
                    L.DotSize = Math.Max(6f, (float)Math.Round(st(6.5)));
                    y += st(3);
                    L.PinX = 0;
                    L.PinDotsY = y + L.DotSize / 2f;
                    y += Math.Max(L.DotSize, fBody.CapHeight) + st(5) + fLabel.CapHeight;
                    L.PinHintBaseline = y;
                    leftW = Math.Max(leftW, Measure(m, HintText, fLabel.Font, track));
                    leftW = Math.Max(leftW, Measure(m, AdminText, fSmall.Font, 0));
                    float leftBottom = y + fLabel.Font.Size * 0.35f;

                    // ---------------- right column: measured first, then placed on the grid
                    float need = RightColumn(null, m, s, fData, fLabel, fSmall, track, unit, st, 0, 0, 0);
                    L.HasRight = need > 0;
                    float leftColW = leftW, rightW = 0, colX = 0, colY = 0;
                    if (L.HasRight)
                    {
                        if (landscape)
                        {
                            rightW = Math.Max(need, leftW / (float)Phi);
                            leftColW = rightW * (float)Phi;
                            colX = leftColW + st(2);
                        }
                        else
                        {
                            rightW = Math.Max(need, leftW);
                            leftColW = rightW;
                            colY = leftBottom + st(2);
                        }
                    }
                    float rightBottom = L.HasRight
                        ? RightColumn(ops, m, s, fData, fLabel, fSmall, track, unit, st, rightW, colX, colY)
                        : 0;
                    if (L.HasRight && landscape)
                    {
                        float top = -fClock.CapHeight * 0.02f;
                        float bottom = Math.Max(textBottom, rightBottom);
                        ops.Add(new Op { Kind = 1, X = (float)Math.Round(leftColW + st(2) / 2f), Y = top, W = 1, H = bottom - top, Color = RuleColor });
                    }
                    L.LeftCol = new RectangleF(0, 0, leftColW, leftBottom);
                    L.RightCol = L.HasRight ? new RectangleF(colX, colY, rightW, rightBottom - colY) : RectangleF.Empty;
                }

                // ---------------- render everything into one bitmap
                float minX = 0, minY = 0, maxX = 0, maxY = 0;
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics m = Graphics.FromImage(probe))
                {
                    foreach (Op op in ops)
                    {
                        RectangleF r = Bounds(m, op);
                        minX = Math.Min(minX, r.Left); minY = Math.Min(minY, r.Top);
                        maxX = Math.Max(maxX, r.Right); maxY = Math.Max(maxY, r.Bottom);
                    }
                }
                maxX = Math.Max(maxX, Math.Max(L.LeftCol.Right, L.RightCol.Right));
                maxY = Math.Max(maxY, L.PinHintBaseline + fLabel.Font.Size * 0.35f);
                int pad = (int)Math.Ceiling(unit * 0.2f);
                L.OriginX = (float)Math.Round(-minX + pad);
                L.OriginY = (float)Math.Round(-minY + pad);
                int bw = (int)Math.Ceiling(maxX - minX) + 2 * pad;
                int bh = (int)Math.Ceiling(maxY - minY) + 2 * pad;
                L.Block = new Bitmap(Math.Max(1, bw), Math.Max(1, bh), PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(L.Block))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    foreach (Op op in ops) Render(g, op, L.OriginX, L.OriginY, unit);
                }
                return L;
            }
            catch
            {
                L.Dispose();
                throw;
            }
        }

        /// <summary>Hours and minutes in display weight, the colon on the digits' optical centre, AM/PM hung small.</summary>
        private static float AddClock(List<Op> ops, Graphics m, string clock, Face f, Face fSuffix, Color color, float track, float baseline, float gap, Layout L)
        {
            L.ClockText = clock;
            string digits = clock.Trim(), suffix = "";
            int sp = digits.LastIndexOf(' ');
            if (sp > 0 && digits.Length - sp <= 4) { suffix = digits.Substring(sp + 1).ToUpperInvariant(); digits = digits.Substring(0, sp); }
            int colon = digits.IndexOf(':');
            if (colon < 0) colon = digits.IndexOf('.');
            float x = -InkLeft(digits, f);
            if (colon > 0)
            {
                string a = digits.Substring(0, colon), sep = digits.Substring(colon, 1), b = digits.Substring(colon + 1);
                float air = f.Font.Size * 0.03f;
                RectangleF digitInk = Ink("0", f.Font), colonInk = Ink(sep, f.Font);
                float lift = (colonInk.Top + colonInk.Height / 2f) - (digitInk.Top + digitInk.Height / 2f);
                x += AddText(ops, m, a, f, color, x, baseline, 0) + air;
                x += AddText(ops, m, sep, f, color, x, baseline - lift, 0) + air;
                x += AddText(ops, m, b, f, color, x, baseline, 0);
            }
            else
            {
                x += AddText(ops, m, digits, f, color, x, baseline, 0);
            }
            float right = x;
            if (suffix.Length > 0)
            {
                float sx = right + gap;
                right = sx + AddText(ops, m, suffix, fSuffix, Secondary, sx, baseline - f.CapHeight + fSuffix.CapHeight, track * 1.2f);
            }
            return right;
        }

        /// <summary>
        /// The data column: a SYSTEM table (value, ten-minute chart or bar, note) and a FORECAST table. With
        /// ops null it only measures and returns the width it needs; otherwise it lays the tables out at the
        /// given width and position and returns the bottom edge.
        /// </summary>
        private static float RightColumn(List<Op> ops, Graphics m, DisplayState s, Face fData, Face fLabel, Face fSmall,
                                         float track, float unit, Func<double, float> st, float width, float ox, float oy)
        {
            bool measure = ops == null;
            if (measure) ops = new List<Op>();
            float gx = st(5);
            float y = oy;
            float needed = 0;

            // ---------------- system
            List<object[]> rows = SystemRows(s);
            if (rows.Count > 0)
            {
                float labelW = 0, valueW = 0, noteW = 0, graphW = (float)Math.Round(unit * 1.3f);
                bool anyGraph = false;
                foreach (object[] r in rows)
                {
                    labelW = Math.Max(labelW, Measure(m, (string)r[0], fLabel.Font, track));
                    valueW = Math.Max(valueW, Measure(m, (string)r[1], fData.Font, 0));
                    noteW = Math.Max(noteW, Measure(m, (string)r[4], fSmall.Font, 0));
                    anyGraph |= r[2] != null;
                }
                if (!anyGraph) graphW = 0;
                float tableW = labelW + gx + valueW + (anyGraph ? gx + graphW : 0) + (noteW > 0 ? gx + noteW : 0);
                bool hasHistory = s.Sys != null && s.Sys.CpuHistory.Length > 1;
                string side = hasHistory ? "LAST " + Math.Max(1, (int)Math.Round(Math.Min(s.Sys.CpuHistory.Length, SysStats.HistoryLength) / 60.0)) + " MIN" : "";
                tableW = Math.Max(tableW, Measure(m, "SYSTEM", fLabel.Font, track) + gx + Measure(m, side, fLabel.Font, track));
                needed = Math.Max(needed, tableW);
                float w = Math.Max(width, tableW);

                y += fLabel.CapHeight;
                AddText(ops, m, "SYSTEM", fLabel, Tertiary, ox, y, track);
                if (side.Length > 0) AddText(ops, m, side, fLabel, Tertiary, ox + w - Measure(m, side, fLabel.Font, track), y, track);
                y += st(6) * 0.8f;
                ops.Add(new Op { Kind = 1, X = ox, Y = (float)Math.Round(y), W = w, H = 1, Color = RuleColor });
                y += st(5) + fData.CapHeight;
                float pitch = fData.Font.Size * 1.8f;
                float vx = ox + labelW + gx, gx0 = vx + valueW + gx, nx = gx0 + (anyGraph ? graphW + gx : 0);
                foreach (object[] r in rows)
                {
                    AddText(ops, m, (string)r[0], fLabel, Tertiary, ox, y, track);
                    string value = (string)r[1];
                    AddText(ops, m, value, fData, Secondary, vx + valueW - Measure(m, value, fData.Font, 0), y, 0);
                    if (r[2] is float[])
                    {
                        float gh = (float)Math.Round(fData.CapHeight * 1.3f);
                        ops.Add(new Op { Kind = 4, X = gx0, Y = y - gh, W = graphW, H = gh, Data = (float[])r[2], Max = (double)r[3], Color = Secondary });
                    }
                    else if (r[2] is double)
                    {
                        float bh = Math.Max(4f, (float)Math.Round(unit / 24f));
                        ops.Add(new Op { Kind = 5, X = gx0, Y = (float)Math.Round(y - fData.CapHeight / 2f - bh / 2f), W = graphW, H = bh, Value = (double)r[2], Color = Secondary });
                    }
                    if (((string)r[4]).Length > 0) AddText(ops, m, (string)r[4], fSmall, Tertiary, nx, y, 0);
                    y += pitch;
                }
                y += -pitch + fData.Font.Size * 0.3f;
            }

            // ---------------- forecast
            string place = null, asOf = null;
            List<string[]> wx = new List<string[]>();
            List<string> raw = new List<string>();
            bool parsed = true;
            string[] weather = s.Weather ?? new string[0];
            for (int i = 0; i < weather.Length; i++)
            {
                string line = (weather[i] ?? "").Trim();
                if (line.Length == 0) continue;
                Match hm = WeatherHead.Match(line);
                if (i == 0 && hm.Success) { place = hm.Groups["place"].Value.Trim(); asOf = hm.Groups["asof"].Value.Trim(); continue; }
                raw.Add(line);
                Match rm = WeatherRow.Match(line);
                if (!rm.Success) { parsed = false; continue; }
                wx.Add(new[]
                {
                    rm.Groups["day"].Value.Trim(),
                    rm.Groups["hi"].Success ? rm.Groups["hi"].Value : "",
                    rm.Groups["lo"].Success ? rm.Groups["lo"].Value : "",
                    rm.Groups["desc"].Value.Trim(),
                    rm.Groups["pop"].Success ? rm.Groups["pop"].Value + "%" : "",
                });
            }
            if (raw.Count > 0)
            {
                if (rows.Count > 0) y += st(3);
                string head = "FORECAST" + (place != null ? "   ·   " + place.ToUpperInvariant() : "");
                string side = asOf != null ? "AS OF " + asOf.ToUpperInvariant() : "";
                float headW = Measure(m, head, fLabel.Font, track) + (side.Length > 0 ? gx + Measure(m, side, fLabel.Font, track) : 0);
                float line2 = st(5) * 0.95f;
                float icon = (float)Math.Round(fData.CapHeight + line2);
                float tx = icon + gx * 0.75f;
                float dayW = 0, hiW = 0, loW = 0, popW = 0;
                float drop = (float)Math.Round(fData.CapHeight * 0.95f);
                if (parsed)
                {
                    foreach (string[] r in wx)
                    {
                        dayW = Math.Max(dayW, Measure(m, r[0].ToUpperInvariant(), fLabel.Font, track));
                        hiW = Math.Max(hiW, Measure(m, r[1], fData.Font, 0));
                        loW = Math.Max(loW, Measure(m, r[2], fData.Font, 0));
                        if (r[4].Length > 0) popW = Math.Max(popW, drop * 0.62f + gx * 0.25f + Measure(m, r[4], fData.Font, 0));
                    }
                }
                float rowW = tx + dayW + gx + hiW + gx * 0.6f + loW + (popW > 0 ? gx + popW : 0);
                if (!parsed) { rowW = 0; foreach (string r in raw) rowW = Math.Max(rowW, Measure(m, r, fSmall.Font, 0)); }
                float tableW = Math.Max(rowW, headW);
                needed = Math.Max(needed, tableW);
                float w = Math.Max(width, tableW);

                y += fLabel.CapHeight;
                AddText(ops, m, head, fLabel, Tertiary, ox, y, track);
                if (side.Length > 0) AddText(ops, m, side, fLabel, Tertiary, ox + w - Measure(m, side, fLabel.Font, track), y, track);
                y += st(6) * 0.8f;
                ops.Add(new Op { Kind = 1, X = ox, Y = (float)Math.Round(y), W = w, H = 1, Color = RuleColor });
                y += st(5) + fData.CapHeight;
                if (parsed)
                {
                    foreach (string[] r in wx)
                    {
                        bool night = r[0].IndexOf("night", StringComparison.OrdinalIgnoreCase) >= 0;
                        ops.Add(new Op { Kind = 3, Text = Symbol(r[3], night), X = ox, Y = (float)Math.Round(y - fData.CapHeight), W = icon, H = icon, Color = Secondary });
                        AddText(ops, m, r[0].ToUpperInvariant(), fLabel, Tertiary, ox + tx, y, track);
                        float hx = ox + tx + dayW + gx;
                        if (r[1].Length > 0) AddText(ops, m, r[1], fData, Primary, hx + hiW - Measure(m, r[1], fData.Font, 0), y, 0);
                        float lx = hx + hiW + gx * 0.6f;
                        if (r[2].Length > 0) AddText(ops, m, r[2], fData, Secondary, lx + loW - Measure(m, r[2], fData.Font, 0), y, 0);
                        if (r[4].Length > 0)
                        {
                            float pw = Measure(m, r[4], fData.Font, 0);
                            float px = ox + w - pw;
                            AddText(ops, m, r[4], fData, Tertiary, px, y, 0);
                            float dw = drop * 0.62f;
                            ops.Add(new Op { Kind = 6, X = px - gx * 0.25f - dw, Y = y - drop, W = dw, H = drop, Color = Tertiary });
                        }
                        Op d = AddTextOp(ops, SentenceCase(r[3]), fSmall, Tertiary, ox + tx, y + line2, 0);
                        d.W = w - tx;
                        y += line2 + st(4) * 0.95f + fData.CapHeight;
                    }
                    y -= st(4) * 0.95f + fData.CapHeight;
                    y += fSmall.Font.Size * 0.35f;
                }
                else
                {
                    foreach (string r in raw)
                    {
                        Op d = AddTextOp(ops, r, fSmall, Secondary, ox, y, 0);
                        d.W = w;
                        y += fSmall.Font.Size * 1.7f;
                    }
                }
            }
            return measure ? needed : y;
        }

        /// <summary>Rows: label, value, graphic (float[] history, or double percent, or null), chart max, note.</summary>
        private static List<object[]> SystemRows(DisplayState s)
        {
            List<object[]> rows = new List<object[]>();
            SysSnapshot x = s.Sys;
            if (x != null && x.Version > 0)
            {
                rows.Add(new object[] { "CPU", x.Cpu >= 0 ? Math.Round(x.Cpu).ToString(CultureInfo.InvariantCulture) + "%" : "--", x.CpuHistory, 100.0, "" });
                if (x.MemPercent >= 0)
                    rows.Add(new object[] { "MEMORY", Math.Round(x.MemPercent).ToString(CultureInfo.InvariantCulture) + "%", x.MemPercent, 0.0,
                                            x.MemUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + x.MemTotalGb.ToString("0", CultureInfo.InvariantCulture) + " GB" });
                double peak = 0;
                foreach (float v in x.NetHistory) if (v > peak) peak = v;
                rows.Add(new object[] { "NETWORK", x.NetDown >= 0 ? "↓ " + Rate(x.NetDown) : "--", x.NetHistory, Math.Max(128 * 1024.0, peak * 1.15),
                                        x.NetUp >= 0 ? "↑ " + Rate(x.NetUp) : "" });
                if (x.DiskUsedPercent >= 0)
                    rows.Add(new object[] { "DISK", Math.Round(x.DiskUsedPercent).ToString(CultureInfo.InvariantCulture) + "%", x.DiskUsedPercent, 0.0,
                                            x.DiskFreeGb.ToString("0", CultureInfo.InvariantCulture) + " GB free" });
                if (x.UptimeSeconds >= 0) rows.Add(new object[] { "UPTIME", Uptime(x.UptimeSeconds), null, 0.0, "" });
                return rows;
            }
            // No numeric readings (yet): the plain strings, without graphics.
            string[,] plain = { { "CPU", s.Cpu }, { "MEMORY", s.Ram }, { "NETWORK", s.Net }, { "DISK", s.Disk }, { "UPTIME", s.Uptime } };
            for (int i = 0; i < plain.GetLength(0); i++)
                if (!string.IsNullOrEmpty(plain[i, 1])) rows.Add(new object[] { plain[i, 0], plain[i, 1], null, 0.0, "" });
            return rows;
        }

        internal static string Rate(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 1000.0) return kb.ToString(kb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB/s";
            double mb = kb / 1024.0;
            return mb.ToString(mb < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB/s";
        }

        internal static string Uptime(long secs)
        {
            long d = secs / 86400, h = (secs % 86400) / 3600, mnt = (secs % 3600) / 60;
            if (d > 0) return d + "d " + h + "h";
            if (h > 0) return h + "h " + mnt + "m";
            return mnt + "m";
        }

        internal static string SentenceCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string lower = text.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }

        /// <summary>Which symbol a forecast phrase gets. The strongest weather wins ("cloudy then rain" is rain).</summary>
        internal static string Symbol(string desc, bool night)
        {
            string d = (desc ?? "").ToLowerInvariant();
            if (d.Contains("thunder") || d.Contains("t-storm")) return "storm";
            if (d.Contains("snow") || d.Contains("flurr") || d.Contains("sleet") || d.Contains("blizzard") || d.Contains("ice")) return "snow";
            if (d.Contains("rain") || d.Contains("shower") || d.Contains("drizzle")) return "rain";
            if (d.Contains("fog") || d.Contains("haze") || d.Contains("smoke") || d.Contains("mist")) return "fog";
            if (d.Contains("wind") || d.Contains("breez") || d.Contains("blustery")) return "wind";
            if (d.Contains("partly") || d.Contains("mostly sunny") || d.Contains("mostly clear") || d.Contains("few clouds") || d.Contains("scattered"))
                return night ? "partly-night" : "partly";
            if (d.Contains("cloud") || d.Contains("overcast")) return "cloud";
            if (d.Contains("sunny") || d.Contains("clear") || d.Contains("fair") || d.Contains("hot")) return night ? "moon" : "sun";
            return "cloud";
        }

        // ================================================================== ops and rendering

        private static float AddText(List<Op> ops, Graphics m, string text, Face f, Color color, float x, float baseline, float tracking)
        {
            AddTextOp(ops, text, f, color, x, baseline, tracking);
            return Measure(m, text, f.Font, tracking);
        }

        private static Op AddTextOp(List<Op> ops, string text, Face f, Color color, float x, float baseline, float tracking)
        {
            Op op = new Op { Kind = 0, Text = text ?? "", Font = f.Font, Ascent = f.Ascent, Color = color, X = x, Y = baseline, Tracking = tracking };
            ops.Add(op);
            return op;
        }

        private static float InkLeft(string text, Face f)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            RectangleF b = Ink(text.Substring(0, 1), f.Font);
            return b.Width > 0 ? b.Left : 0;
        }

        internal static float Measure(Graphics m, string text, Font font, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (tracking <= 0) return m.MeasureString(text, font, PointF.Empty, Typo).Width;
            float total = 0;
            foreach (char ch in text) total += m.MeasureString(ch.ToString(), font, PointF.Empty, Typo).Width + tracking;
            return total - tracking;
        }

        private static RectangleF Bounds(Graphics m, Op op)
        {
            if (op.Kind == 0)
            {
                float tw = op.W > 0 ? op.W : Measure(m, op.Text, op.Font, op.Tracking);
                return RectangleF.FromLTRB(op.X, op.Y - op.Font.Size * 0.8f, op.X + tw, op.Y + op.Font.Size * 0.3f);
            }
            if (op.Kind == 2) return new RectangleF(op.X - op.W / 2f, op.Y - op.W / 2f, op.W, op.W);
            return new RectangleF(op.X, op.Y, op.W, op.H);
        }

        private static void Render(Graphics g, Op op, float ox, float oy, float unit)
        {
            switch (op.Kind)
            {
                case 0:
                    DrawText(g, op.Text, op.Font, op.Color, (float)Math.Round(ox + op.X), (float)Math.Round(oy + op.Y - op.Ascent), op.Tracking, op.W);
                    break;
                case 1:
                    using (SolidBrush b = new SolidBrush(op.Color))
                        g.FillRectangle(b, (float)Math.Round(ox + op.X), (float)Math.Round(oy + op.Y), (float)Math.Round(op.W), op.H);
                    break;
                case 2:
                    using (SolidBrush b = new SolidBrush(op.Color))
                        g.FillEllipse(b, ox + op.X - op.W / 2f, oy + op.Y - op.W / 2f, op.W, op.W);
                    break;
                case 3:
                    DrawSymbol(g, op.Text, new RectangleF(ox + op.X, oy + op.Y, op.W, op.H), op.Color);
                    break;
                case 4:
                    DrawSparkline(g, op, ox, oy, unit);
                    break;
                case 5:
                    DrawBar(g, op, ox, oy);
                    break;
                case 6:
                    DrawDroplet(g, new RectangleF(ox + op.X, oy + op.Y, op.W, op.H), op.Color);
                    break;
            }
        }

        internal static void DrawText(Graphics g, string text, Font font, Color color, float x, float top, float tracking, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (SolidBrush brush = new SolidBrush(color))
            {
                if (maxWidth > 0)
                {
                    using (StringFormat f = (StringFormat)Typo.Clone())
                    {
                        f.Trimming = StringTrimming.EllipsisWord;
                        f.FormatFlags &= ~StringFormatFlags.NoClip;
                        g.DrawString(text, font, brush, new RectangleF(x, top, maxWidth + 1, font.Size * 1.6f), f);
                    }
                    return;
                }
                if (tracking <= 0) { g.DrawString(text, font, brush, x, top, Typo); return; }
                foreach (char ch in text)
                {
                    string one = ch.ToString();
                    g.DrawString(one, font, brush, x, top, Typo);
                    x += g.MeasureString(one, font, PointF.Empty, Typo).Width + tracking;
                }
            }
        }

        /// <summary>
        /// A ten-minute sparkline: the newest reading at the right edge, a fixed or honestly padded scale, a
        /// faint area under a 2-pixel line, a recessive baseline, and a dot on the latest value.
        /// </summary>
        private static void DrawSparkline(Graphics g, Op op, float ox, float oy, float unit)
        {
            float x0 = ox + op.X, y0 = oy + op.Y, gw = op.W, gh = op.H;
            using (Pen baseline = new Pen(Track, 1f))
                g.DrawLine(baseline, x0, (float)Math.Round(y0 + gh) + 0.5f, x0 + gw, (float)Math.Round(y0 + gh) + 0.5f);
            float[] d = op.Data;
            if (d == null || d.Length < 2) return;
            int cap = SysStats.HistoryLength, n = Math.Min(d.Length, cap), skip = d.Length - n, offset = cap - n;
            double max = op.Max > 0 ? op.Max : 1;
            int buckets = Math.Max(8, (int)(gw / 2.5f));
            List<PointF> pts = new List<PointF>();
            for (int b = 0; b < buckets; b++)
            {
                int i0 = (int)((long)b * cap / buckets) - offset, i1 = (int)((long)(b + 1) * cap / buckets) - offset;
                if (i1 <= 0) continue;
                if (i0 < 0) i0 = 0;
                if (i1 > n) i1 = n;
                double sum = 0; int cnt = 0;
                for (int i = i0; i < i1; i++) { float v = d[skip + i]; if (v >= 0) { sum += v; cnt++; } }
                if (cnt == 0) continue;
                double val = Math.Min(max, sum / cnt);
                pts.Add(new PointF(x0 + gw * (b + 0.5f) / buckets, y0 + gh - (float)(val / max) * (gh - 1)));
            }
            if (pts.Count < 2) return;
            PointF[] area = new PointF[pts.Count + 2];
            pts.CopyTo(area, 0);
            area[pts.Count] = new PointF(pts[pts.Count - 1].X, y0 + gh);
            area[pts.Count + 1] = new PointF(pts[0].X, y0 + gh);
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(46, op.Color)))
                g.FillPolygon(fill, area);
            float lw = Math.Max(1.5f, unit / 80f);
            using (Pen p = new Pen(op.Color, lw))
            {
                p.LineJoin = LineJoin.Round;
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLines(p, pts.ToArray());
            }
            PointF last = pts[pts.Count - 1];
            float dot = Math.Max(5f, unit / 20f);
            using (SolidBrush halo = new SolidBrush(Color.Black))
                g.FillEllipse(halo, last.X - dot / 2f - lw, last.Y - dot / 2f - lw, dot + 2 * lw, dot + 2 * lw);
            using (SolidBrush b = new SolidBrush(Hero))
                g.FillEllipse(b, last.X - dot / 2f, last.Y - dot / 2f, dot, dot);
        }

        /// <summary>A proportion bar: a dark track, the filled share anchored left with a rounded data end.</summary>
        private static void DrawBar(Graphics g, Op op, float ox, float oy)
        {
            float x = (float)Math.Round(ox + op.X), y = (float)Math.Round(oy + op.Y), w = op.W, h = op.H;
            using (GraphicsPath track = RoundedRight(x, y, w, h))
            using (SolidBrush tb = new SolidBrush(Track))
                g.FillPath(tb, track);
            float fw = (float)(w * Math.Max(0, Math.Min(100, op.Value)) / 100.0);
            if (fw < h / 2f) return;
            using (GraphicsPath fill = RoundedRight(x, y, fw, h))
            using (SolidBrush fb = new SolidBrush(op.Color))
                g.FillPath(fb, fill);
        }

        private static GraphicsPath RoundedRight(float x, float y, float w, float h)
        {
            GraphicsPath p = new GraphicsPath();
            float r = h / 2f;
            p.AddLine(x, y, x + w - r, y);
            p.AddArc(x + w - h, y, h, h, 270, 180);
            p.AddLine(x + w - r, y + h, x, y + h);
            p.CloseFigure();
            return p;
        }

        private static void DrawDroplet(Graphics g, RectangleF r, Color c)
        {
            PointF[] pts = new PointF[48];
            for (int i = 0; i < pts.Length; i++)
            {
                double t = 2 * Math.PI * i / pts.Length;
                pts[i] = new PointF(r.X + r.Width * (float)(0.5 + 0.5 * Math.Sin(t) * Math.Sin(t / 2)),
                                    r.Y + r.Height * (float)(0.5 - 0.5 * Math.Cos(t)));
            }
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        // ---- weather symbols, drawn as filled shapes on a 1 x 1 design grid

        private static void DrawSymbol(Graphics g, string kind, RectangleF box, Color c)
        {
            float s = Math.Min(box.Width, box.Height);
            float bx = box.X + (box.Width - s) / 2f, by = box.Y + (box.Height - s) / 2f;
            Func<float, float> X = v => bx + v * s;
            Func<float, float> Y = v => by + v * s;
            using (SolidBrush ink = new SolidBrush(c))
            using (SolidBrush cut = new SolidBrush(Color.Black))
            using (Pen stroke = new Pen(c, Math.Max(1.5f, s * 0.075f)))
            {
                stroke.StartCap = LineCap.Round;
                stroke.EndCap = LineCap.Round;
                switch (kind)
                {
                    case "sun":
                        Sun(g, ink, stroke, X(0.5f), Y(0.5f), s * 0.2f, s * 0.31f, s * 0.44f);
                        break;
                    case "moon":
                        Moon(g, ink, cut, X(0.5f), Y(0.5f), s * 0.33f);
                        break;
                    case "partly":
                    case "partly-night":
                        if (kind == "partly") Sun(g, ink, stroke, X(0.36f), Y(0.36f), s * 0.15f, s * 0.22f, s * 0.31f);
                        else Moon(g, ink, cut, X(0.38f), Y(0.36f), s * 0.22f);
                        Cloud(g, cut, X(0.08f), Y(0.36f), s * 0.94f, s * 0.08f);
                        Cloud(g, ink, X(0.08f), Y(0.36f), s * 0.94f, 0);
                        break;
                    case "rain":
                        Cloud(g, ink, X(0.02f), Y(0.05f), s * 0.96f, 0);
                        for (int i = 0; i < 3; i++)
                            g.DrawLine(stroke, X(0.32f + i * 0.18f), Y(0.72f), X(0.26f + i * 0.18f), Y(0.90f));
                        break;
                    case "storm":
                        Cloud(g, ink, X(0.02f), Y(0.05f), s * 0.96f, 0);
                        g.FillPolygon(cut, Pts(X, Y, 0.56f, 0.52f, 0.36f, 0.80f, 0.50f, 0.80f, 0.42f, 1.0f, 0.72f, 0.66f, 0.57f, 0.66f, 0.66f, 0.52f));
                        g.FillPolygon(ink, Pts(X, Y, 0.56f, 0.58f, 0.42f, 0.78f, 0.53f, 0.78f, 0.47f, 0.94f, 0.66f, 0.70f, 0.55f, 0.70f, 0.62f, 0.58f));
                        break;
                    case "snow":
                        Cloud(g, ink, X(0.02f), Y(0.05f), s * 0.96f, 0);
                        float r = s * 0.045f;
                        float[] sx = { 0.30f, 0.50f, 0.70f, 0.40f, 0.60f };
                        float[] sy = { 0.76f, 0.76f, 0.76f, 0.92f, 0.92f };
                        for (int i = 0; i < sx.Length; i++) g.FillEllipse(ink, X(sx[i]) - r, Y(sy[i]) - r, 2 * r, 2 * r);
                        break;
                    case "fog":
                        Cloud(g, ink, X(0.02f), Y(0.02f), s * 0.96f, 0);
                        g.DrawLine(stroke, X(0.18f), Y(0.74f), X(0.82f), Y(0.74f));
                        g.DrawLine(stroke, X(0.30f), Y(0.90f), X(0.92f), Y(0.90f));
                        break;
                    case "wind":
                        using (GraphicsPath p = new GraphicsPath())
                        {
                            p.AddLine(X(0.08f), Y(0.36f), X(0.62f), Y(0.36f));
                            p.AddArc(X(0.56f), Y(0.18f), s * 0.18f, s * 0.18f, 90, -270);
                            g.DrawPath(stroke, p);
                        }
                        g.DrawLine(stroke, X(0.08f), Y(0.56f), X(0.86f), Y(0.56f));
                        using (GraphicsPath p = new GraphicsPath())
                        {
                            p.AddLine(X(0.08f), Y(0.76f), X(0.56f), Y(0.76f));
                            p.AddArc(X(0.50f), Y(0.76f), s * 0.16f, s * 0.16f, 270, 270);
                            g.DrawPath(stroke, p);
                        }
                        break;
                    default:
                        Cloud(g, ink, X(0.02f), Y(0.16f), s * 0.96f, 0);
                        break;
                }
            }
        }

        private static PointF[] Pts(Func<float, float> X, Func<float, float> Y, params float[] v)
        {
            PointF[] p = new PointF[v.Length / 2];
            for (int i = 0; i < p.Length; i++) p[i] = new PointF(X(v[2 * i]), Y(v[2 * i + 1]));
            return p;
        }

        private static void Sun(Graphics g, Brush ink, Pen stroke, float cx, float cy, float r, float r0, float r1)
        {
            g.FillEllipse(ink, cx - r, cy - r, 2 * r, 2 * r);
            for (int i = 0; i < 8; i++)
            {
                double a = Math.PI * i / 4;
                g.DrawLine(stroke, cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
            }
        }

        private static void Moon(Graphics g, Brush ink, Brush cut, float cx, float cy, float r)
        {
            g.FillEllipse(ink, cx - r, cy - r, 2 * r, 2 * r);
            float k = r * 0.86f;
            g.FillEllipse(cut, cx - k + r * 0.42f, cy - k - r * 0.30f, 2 * k, 2 * k);
        }

        /// <summary>A cloud: three bumps on a rounded base, w wide, its top-left at (x, y). grow pads every part.</summary>
        private static void Cloud(Graphics g, Brush b, float x, float y, float w, float grow)
        {
            float u = w;
            Action<float, float, float> circle = (cx, cy, r) =>
                g.FillEllipse(b, x + cx * u - r * u - grow, y + cy * u - r * u - grow, 2 * r * u + 2 * grow, 2 * r * u + 2 * grow);
            circle(0.30f, 0.42f, 0.17f);
            circle(0.52f, 0.33f, 0.22f);
            circle(0.74f, 0.45f, 0.15f);
            using (GraphicsPath p = new GraphicsPath())
            {
                float left = x + 0.10f * u - grow, top = y + 0.38f * u - grow, right = x + 0.92f * u + grow, bottom = y + 0.60f * u + grow;
                float d = bottom - top;
                p.AddArc(left, top, d, d, 90, 180);
                p.AddArc(right - d, top, d, d, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        // ================================================================== small helpers

        internal static Color Scale(Color c, double f)
        {
            return Color.FromArgb(c.A, (int)Math.Round(c.R * f), (int)Math.Round(c.G * f), (int)Math.Round(c.B * f));
        }

        private static Color Fade(Color c, float alpha)
        {
            return Color.FromArgb((int)Math.Round(255 * Clamp01(alpha)), c.R, c.G, c.B);
        }

        private static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        private static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
        private static float OutCubic(float t) { float u = 1 - t; return 1 - u * u * u; }
        private static float InOutSine(float t) { return (float)(0.5 - 0.5 * Math.Cos(Math.PI * t)); }
        private static float OutBack(float t) { const float k = 1.70158f; float u = t - 1; return 1 + (k + 1) * u * u * u + k * u * u; }

        /// <summary>WCAG 2.x contrast ratio of a colour on black.</summary>
        internal static double ContrastOnBlack(Color c)
        {
            Func<int, double> lin = v => { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            double l = 0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B);
            return (l + 0.05) / 0.05;
        }
    }
}
