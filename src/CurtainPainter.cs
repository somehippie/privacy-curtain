using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeCurtain
{
    /// <summary>
    /// Draws the curtain. Everything follows one proportional system built on the golden ratio φ ≈ 1.618.
    ///
    /// Grid    On a landscape screen the composition is two columns whose widths are φ : 1: the time, the
    ///         weather map and the message on the left, live data on the right, a hairline between them. On a
    ///         portrait screen the data moves under the message. The composition rests on the screen's golden
    ///         section (the space above it and the space below it are 1 : φ).
    /// Type    One family (Bahnschrift, a DIN design that ships with Windows). Every size is the clock size
    ///         divided by a power of φ; every gap is a step on the same scale. Large type is aligned by its
    ///         ink rather than its side bearing, the colon sits on the optical centre of the digits, and
    ///         small capitals are letter-spaced.
    /// Data    Ten-minute sparklines for CPU and network, proportion bars for memory and disk, the next six hours (temperature curve, rain bars), and a
    ///         forecast table with weather symbols drawn in code. Values are always printed as text beside
    ///         their graphic, so nothing depends on reading a chart. A weather map (radar when rain is near,
    ///         clouds and fog otherwise; see WeatherMap) drawn in the same palette.
    /// Colour  Warm neutrals in four text tones plus one accent. Contrast on black is checked by the
    ///         self-tests: every text tone meets WCAG AA, body text meets AAA.
    /// Motion  The composition is rendered once into an image (again only when something on it changes)
    ///         and moved as one rigid piece, a whole pixel at a time, so nothing shimmers. It drifts against
    ///         burn-in with horizontal and vertical periods in the ratio 1 : φ, a path that never repeats.
    ///         It fades in when the curtain rises and cross-fades when the minute turns. The accent rule
    ///         breathes slowly while Claude works. PIN dots ease in as they are typed, and a wrong PIN shakes
    ///         them before they fade. Every 30 seconds the map replays its past hour in about five.
    /// </summary>
    internal sealed class CurtainPainter : IDisposable
    {
        public const double Phi = 1.6180339887498949;
        public const float ClockPerUnit = 2.15f;        // the clock's size in units; every other size is it over a power of φ
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
        internal static readonly Color Rain = Color.FromArgb(112, 148, 178);      // the one data hue: chance of rain

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
            fast = now - firstPaint < FadeInMs + 60 || prev != null || pinAnim || mapFading;
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
            DrawMap(g, l, s, ox, oy, now, fadeIn);

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

            // The weather map (Map is empty when there is none): its picture's place, its header, what it plays.
            public RectangleF Map;
            public float MapHeadBaseline, MapRuleY;
            public WeatherMap MapData;
            public MapFrame[] MapFrames = new MapFrame[0];
            public bool MapRadar;
            public GeoBox MapCrop;
            public PointF MapHome = new PointF(float.NaN, float.NaN);   // in map pixels
            public string MapOverlayKey = "";
            public Face Data;
            public List<PlacedStation> MapStations = new List<PlacedStation>();
            public TideChart MapTide;             // the tide inset (null = none)
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

        /// <summary>
        /// The tide inset, in map pixels: a small table of the nearest station's tide. Its first row is now
        /// (the day's curve with now on it, the height now, rising or falling), then the next low and high.
        /// </summary>
        internal sealed class TideChart
        {
            public RectangleF Panel;                         // its dark ground
            public RectangleF Spark;                         // the day's curve, in the first row; its bottom is the baseline
            public PointF[] Curve = new PointF[0];           // TidePastHours ago to TideHours later, oldest first
            public int Now;                                  // Curve[Now] is now
            public readonly List<TideRow> Rows = new List<TideRow>();
            public float LabelX, TimeRight, HeightRight, ChangeRight;   // the columns: labels from the left, the rest to the right
        }

        /// <summary>
        /// One row of the tide inset: "LOW", "11:57 PM", "1.3 ft", "−6.1" (the change since the high or low
        /// before). The first row is "TIDE", the curve instead of a time, the height now, and ↑ or ↓.
        /// </summary>
        internal sealed class TideRow
        {
            public string Label = "", Time = "", Height = "", Change = "";
            public float Baseline;
        }

        public const int TidePastHours = 4;   // the tide inset's curve starts this long ago...
        public const int TideHours = 24;      // ...and shows a day

        private sealed class Op
        {
            public int Kind;            // 0 text, 1 rect, 2 dot, 3 weather symbol, 4 sparkline, 5 bar, 6 droplet, 7 rain bar, 8 curve, 9 picture
            public string Text;
            public Func<Bitmap> Picture;   // kind 9: drawn at (X, Y) unscaled; fetched only when the block is drawn, owned by MapArt
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
                Layout l = ComposeAt(s, unit, landscape, w * 0.90f, h * 0.90f, attempt == 11);
                if (l == null) return null;
                if (l.Block != null) return l;
                l.Dispose();
                unit *= 0.92f;
            }
            return null;
        }

        /// <summary>Lays out at one scale. When the result would not fit in maxW x maxH it is not drawn (Block stays null).</summary>
        private static Layout ComposeAt(DisplayState s, float unit, bool landscape, float maxW, float maxH, bool final)
        {
            float c = unit * ClockPerUnit;
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
            L.Body = fBody; L.Label = fLabel; L.Small = fSmall; L.Data = fData; L.Track = track;
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
                    // ...but sized for the widest time it can ever show, so the minute turning moves nothing.
                    string clockText = L.ClockText;
                    bool twelve = (s.Clock ?? "").Trim().IndexOf(' ') > 0;
                    // Bahnschrift's figures are proportional, so the widest time is built from the widest
                    // digit each position allows (hour tens 0-2, minute tens 0-5), measured, not assumed.
                    string any = WidestDigit(m, fClock.Font, "0123456789"), tens = WidestDigit(m, fClock.Font, "012345");
                    string widest = (twelve ? "1" + WidestDigit(m, fClock.Font, "012") : WidestDigit(m, fClock.Font, "012") + any) + ":" + tens + any;
                    foreach (string suffix in twelve ? new[] { " AM", " PM" } : new[] { "" })
                        leftW = Math.Max(leftW, AddClock(new List<Op>(), m, widest + suffix, fClock, fSuffix, hero, track, y, st(6), L));
                    L.ClockText = clockText;

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
                    leftW = Math.Max(leftW, Measure(m, "WEDNESDAY, SEPTEMBER 30   \u00B7   PDT GMT\u22127", fLabel.Font, track));

                    // The message (rule, title, two lines) and the PIN row are measured now and placed after the
                    // data column: on a landscape screen the message's last line sits level with the data
                    // column's last line, so both columns share a top and a bottom edge, with the clock at the
                    // top, the message at the foot, and the PIN row just above the message.
                    float yDate = y;
                    float ruleW = (float)Math.Round(c / (Phi * Phi)), ruleH = Math.Max(1f, (float)Math.Round(unit / 70f));
                    string title = "PC in use by Claude";
                    string l1 = "Keyboard and mouse are locked while Claude works.";
                    string l2 = "Any attempt to use, unlock or interrupt this PC is logged.";
                    float msgH = ruleH + st(3) + fTitle.CapHeight + st(5) + fBody.CapHeight + fBody.Font.Size * 1.5f;
                    L.DotSize = Math.Max(6f, (float)Math.Round(st(6.5)));
                    float dotRow = Math.Max(L.DotSize, fBody.CapHeight);
                    float pinH = dotRow + st(5) + fLabel.CapHeight;
                    leftW = Math.Max(leftW, Measure(m, title, fTitle.Font, 0) - InkLeft(title, fTitle));
                    leftW = Math.Max(leftW, Measure(m, l1, fBody.Font, 0));
                    leftW = Math.Max(leftW, Measure(m, l2, fBody.Font, 0));
                    leftW = Math.Max(leftW, Measure(m, HintText, fLabel.Font, track));
                    leftW = Math.Max(leftW, Measure(m, AdminText, fSmall.Font, 0));

                    // ---------------- the weather map: under the date, as wide as the column's text, its header
                    // ruled like the data column's sections. On a landscape screen it takes the room between the
                    // date and the message (which the data column decides), so it never moves anything else. The
                    // PIN row shares the map's lowest band: while a PIN is typed the map steps back (see DrawMap).
                    bool hasMap = s.Map != null;
                    float mapHead = 0, mapRule = 0, mapTop = 0, mapMinH = 0;
                    if (hasMap)
                    {
                        mapHead = yDate + st(3) + fLabel.CapHeight * 0.5f;
                        mapRule = (float)Math.Round(mapHead + st(6) * 0.8f);
                        mapTop = (float)Math.Round(mapRule + st(5.5));
                        mapMinH = (float)Math.Round(Math.Max(leftW / (Phi * Phi * Phi), pinH + st(3)));
                    }

                    // ---------------- right column: measured, then placed on the grid
                    float need = RightColumn(null, m, s, fData, fLabel, fSmall, track, unit, st, 0, 0, 0);
                    L.HasRight = need > 0;
                    // the message's last baseline, laid out naturally
                    float lastLine = hasMap ? mapTop + mapMinH + st(3) + msgH : yDate + st(3) + pinH + st(3) + msgH;
                    if (L.HasRight && landscape)
                    {
                        float rightH = RightColumn(new List<Op>(), m, s, fData, fLabel, fSmall, track, unit, st, need, 0, 0);
                        lastLine = Math.Max(lastLine, rightH - fSmall.Font.Size * 0.35f);
                    }
                    float ruleTop = (float)Math.Round(lastLine - msgH);
                    L.Rule = new RectangleF(0, ruleTop, ruleW, ruleH);
                    L.PinX = 0;
                    L.PinHintBaseline = ruleTop - st(3);
                    L.PinDotsY = L.PinHintBaseline - fLabel.CapHeight - st(5) - dotRow / 2f;
                    if (hasMap)
                    {
                        float mapBottom = (float)Math.Round(ruleTop - st(3));   // level with the PIN hint's baseline
                        L.Map = new RectangleF(0, mapTop, (float)Math.Round(leftW), Math.Max(8f, mapBottom - mapTop));
                        L.MapHeadBaseline = mapHead;
                        L.MapRuleY = mapRule;
                        AddMap(ops, m, s, L, fLabel, track, unit);
                    }
                    y = ruleTop + ruleH + st(3) + fTitle.CapHeight;
                    L.Baselines.Add(new KeyValuePair<string, float>("C / \u03C6\u2074", y));
                    AddText(ops, m, title, fTitle, Primary, -InkLeft(title, fTitle), y, 0);
                    y += st(5) + fBody.CapHeight;
                    L.Baselines.Add(new KeyValuePair<string, float>("C / \u03C6\u2075", y));
                    AddText(ops, m, l1, fBody, s.Bright ? hero : Secondary, 0, y, 0);
                    y += fBody.Font.Size * 1.5f;
                    AddText(ops, m, l2, fBody, Secondary, 0, y, 0);
                    float leftBottom = y + fBody.Font.Size * 0.3f;

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
                        float bottom = Math.Max(leftBottom, rightBottom);
                        ops.Add(new Op { Kind = 1, X = (float)Math.Round(leftColW + st(2) / 2f), Y = top, W = 1, H = bottom - top, Color = RuleColor });
                    }
                    L.LeftCol = new RectangleF(0, 0, leftColW, leftBottom);
                    L.RightCol = L.HasRight ? new RectangleF(colX, colY, rightW, rightBottom - colY) : RectangleF.Empty;
                }

                // ---------------- render everything into one bitmap. Its size comes from the grid (the
                // columns), never from the ink, so changing content can never resize or move it.
                float minX = 0, minY = 0;
                float maxX = Math.Max(L.LeftCol.Right, L.RightCol.Right);
                float maxY = Math.Max(L.LeftCol.Bottom, L.RightCol.Bottom);
                int pad = (int)Math.Ceiling(unit * 0.25f);
                L.OriginX = (float)Math.Round(-minX + pad);
                L.OriginY = (float)Math.Round(-minY + pad);
                int bw = (int)Math.Ceiling(maxX - minX) + 2 * pad;
                int bh = (int)Math.Ceiling(maxY - minY) + 2 * pad;
                if (!final && (Math.Max(1, bw) > maxW || Math.Max(1, bh) > maxH)) return L;   // too big: a smaller scale comes next
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

        // ================================================================== the weather map

        /// <summary>
        /// The map's still parts, drawn into the block: the header (what it shows; the picture's time is drawn
        /// live), its rule, the water, and a note while there are no pictures yet. Decides radar or clouds and
        /// which part of the area shows. The weather pictures, the shoreline over them and home are live.
        /// </summary>
        private static void AddMap(List<Op> ops, Graphics m, DisplayState s, Layout L, Face fLabel, float track, float unit)
        {
            WeatherMap map = s.Map;
            int mw = (int)Math.Round(L.Map.Width), mh = (int)Math.Round(L.Map.Height);
            bool radar = WeatherMap.ShowRadar(map, s.Hourly, s.Now, s.Lat, s.Lon);
            if (s.MapMode == 1 && map.Radar.Length > 0) radar = true;
            if (s.MapMode == 2 && map.Sat.Length > 0) radar = false;
            L.MapData = map;
            L.MapRadar = radar;
            L.MapFrames = radar ? map.Radar : map.Sat;
            GeoBox crop = WeatherMap.Crop(map.Box, s.Lat, s.Lon, mw, mh);
            L.MapCrop = crop;

            string what = radar ? "RADAR" : "CLOUDS";
            if (!radar && map.Sat.Length > 0 && map.Sat[0].Kind == "ir") what += "   ·   INFRARED";
            if (L.MapFrames.Length > 1) what += "   ·   PAST HOUR";
            AddText(ops, m, what, fLabel, Tertiary, 0, L.MapHeadBaseline, track);
            ops.Add(new Op { Kind = 1, X = 0, Y = L.MapRuleY, W = L.Map.Width, H = 1, Color = RuleColor });
            ops.Add(new Op { Kind = 9, X = L.Map.X, Y = L.Map.Y, Picture = () => MapArt.Base(map, crop, mw, mh) });
            if (L.MapFrames.Length == 0)
            {
                AddCentered(ops, m, "THE MAP WILL APPEAR WITH THE NEXT REFRESH", fLabel, Tertiary,
                            L.Map.X + L.Map.Width / 2f, L.Map.Y + L.Map.Height / 2f + fLabel.CapHeight / 2f, track);
            }

            if (!s.HideHome && !double.IsNaN(s.Lat) && !double.IsNaN(s.Lon))
            {
                float hx = (float)((s.Lon - crop.West) / (crop.East - crop.West) * mw);
                float hy = (float)((crop.North - s.Lat) / (crop.North - crop.South) * mh);
                if (hx >= 0 && hx <= mw && hy >= 0 && hy <= mh) L.MapHome = new PointF(hx, hy);
            }
            // On the map: the tide inset first (in one of the top corners), then temperatures spread out around
            // it, all clear of home, the edges and the legend's band at the bottom.
            float pad = (float)Math.Round(unit * 0.14f);
            float band = (float)Math.Round(pad * 2.6f + fLabel.CapHeight);
            List<RectangleF> taken = new List<RectangleF>();
            if (!float.IsNaN(L.MapHome.X))
            {
                float hr = unit * 0.11f;
                taken.Add(new RectangleF(L.MapHome.X - hr, L.MapHome.Y - hr, 2 * hr, 2 * hr));
            }
            Face fData = L.Data, fSmall = L.Small;
            float lineGap = (float)Math.Round(unit * 0.07f);
            Func<MapStation, RectangleF> labelBox = st =>
            {
                float bw = Measure(m, st.TempF.ToString(CultureInfo.InvariantCulture) + "°", fData.Font, 0);
                float bh = fData.CapHeight;
                if (WeatherMap.Windy(st))
                {
                    bw = Math.Max(bw, fSmall.CapHeight * 1.5f + Measure(m, WeatherMap.WindText(st), fSmall.Font, 0));
                    bh += lineGap + fSmall.CapHeight;
                }
                return new RectangleF(-bw / 2f, -fData.CapHeight / 2f, bw, bh);
            };
            float spacing = Math.Max(unit * 0.8f, Measure(m, "100°", fData.Font, 0) * 2.4f);
            Func<List<PlacedStation>> pick = () => WeatherMap.PickStations(map.Stations, crop, mw, mh, s.Lat, s.Lon, labelBox, spacing, pad,
                                                                         band + pad * 0.5f, taken, WeatherMap.MaxStations);
            string tideKey = PlaceTide(m, L, map.Tide, mw, mh, s.Now, pad, band, taken, pick());
            L.MapStations = pick();
            StringBuilder labels = new StringBuilder();
            foreach (PlacedStation p in L.MapStations)
                labels.Append(p.Station.Id).Append(' ').Append(p.Station.TempF).Append(' ').Append(WeatherMap.Windy(p.Station) ? WeatherMap.WindText(p.Station) + " " + p.Station.WindFrom : "").Append(';');

            L.MapOverlayKey = (radar ? "radar" : "clouds") + "|" + L.MapFrames.Length.ToString(CultureInfo.InvariantCulture) + "|" + map.Credit + "|" +
                              L.MapHome.X.ToString("0.0", CultureInfo.InvariantCulture) + "," + L.MapHome.Y.ToString("0.0", CultureInfo.InvariantCulture) + "|" +
                              fLabel.Font.Size.ToString("0.00", CultureInfo.InvariantCulture) + "|" + unit.ToString("0.00", CultureInfo.InvariantCulture) + "|" +
                              labels + "|" + tideKey;
        }

        /// <summary>
        /// The tide inset: a small table for a quick look, as a fisherman or surfer would want it. "TIDE", the
        /// day's curve from four hours ago with now on it, the height now and an arrow, rising or falling; then
        /// the next low and high in the order they come, each with its time, its height and the change from the
        /// high or low before ("−6.1": the water drops 6.1 ft). It takes the top corner that hides fewer
        /// temperatures, on a tie the east one (where the weather has already gone by), and never covers home.
        /// Returns a key for what it shows ("-" when there is no tide, or no room).
        /// </summary>
        private static string PlaceTide(Graphics m, Layout L, MapTide tide, int mw, int mh, DateTime nowLocal,
                                        float pad, float band, List<RectangleF> taken, List<PlacedStation> shown)
        {
            L.MapTide = null;
            if (tide == null) return "-";
            DateTime now = nowLocal.ToUniversalTime();
            int next = tide.NextEvent(now);
            double heightNow = tide.HeightAt(now);
            if (double.IsNaN(heightNow) || next < 0) return "-";

            TideChart t = new TideChart();
            t.Rows.Add(new TideRow { Label = "TIDE", Height = Feet(heightNow, false) + " ft", Change = tide.High[next] ? "\u2191" : "\u2193" });
            for (int i = next; i < tide.Times.Length && i < next + 2; i++)
            {
                t.Rows.Add(new TideRow { Label = tide.High[i] ? "HIGH" : "LOW", Height = Feet(tide.Feet[i], false) + " ft",
                                         Time = tide.Times[i].ToLocalTime().ToString("t", CultureInfo.CurrentCulture).ToUpperInvariant(),
                                         Change = i > 0 ? Feet(tide.Feet[i] - tide.Feet[i - 1], true) : "" });
            }

            // Columns sized to what they hold; the curve takes the time column of the first row.
            Face fl = L.Label, fs = L.Small;
            float track = L.Track;
            Func<double, float> st = k => (float)Math.Round(L.Unit * ClockPerUnit / Math.Pow(Phi, k));
            float labelW = 0, timeW = st(3), heightW = 0, changeW = 0;
            foreach (TideRow r in t.Rows)
            {
                labelW = Math.Max(labelW, Measure(m, r.Label, fl.Font, track));
                timeW = Math.Max(timeW, Measure(m, r.Time, fs.Font, 0));
                heightW = Math.Max(heightW, Measure(m, r.Height, fs.Font, 0));
                changeW = Math.Max(changeW, Measure(m, r.Change, fs.Font, 0));
            }
            float padX = st(6.5), padY = st(7), gap = st(7), sparkH = (float)Math.Round(fs.CapHeight * 1.3f), pitch = fs.CapHeight + st(7);
            float panelW = (float)Math.Ceiling(padX + labelW + st(6.5) + timeW + gap + heightW + gap + changeW + padX);
            float panelH = (float)Math.Ceiling(padY + sparkH + pitch * (t.Rows.Count - 1) + padY);
            if (panelW + 2 * pad > mw || panelH + pad + band > mh) return "-";

            RectangleF east = new RectangleF(mw - pad - panelW, pad, panelW, panelH), west = new RectangleF(pad, pad, panelW, panelH);
            Func<RectangleF, int> hides = r =>   // of the temperatures that would show without the inset
            {
                if (taken.Exists(o => o.IntersectsWith(r))) return int.MaxValue;   // home
                return shown.FindAll(p => p.Box.IntersectsWith(r)).Count;
            };
            int hideEast = hides(east), hideWest = hides(west);
            if (hideEast == int.MaxValue && hideWest == int.MaxValue) return "-";
            t.Panel = hideWest < hideEast ? west : east;

            float left = t.Panel.Left, top = t.Panel.Top;
            t.LabelX = left + padX;
            t.TimeRight = (float)Math.Round(t.LabelX + labelW + st(6.5) + timeW);
            t.HeightRight = (float)Math.Round(t.TimeRight + gap + heightW);
            t.ChangeRight = (float)Math.Round(t.HeightRight + gap + changeW);
            for (int i = 0; i < t.Rows.Count; i++) t.Rows[i].Baseline = (float)Math.Round(top + padY + sparkH + pitch * i);
            t.Spark = new RectangleF(t.TimeRight - timeW, t.Rows[0].Baseline - sparkH, timeW, sparkH);

            // The curve, every ten minutes, scaled to the day's range like the system graphs.
            DateTime start = now.AddHours(-TidePastHours);
            const int stepMin = 10;
            int n = TideHours * 60 / stepMin + 1;
            double[] h = new double[n];
            for (int k = 0; k < n; k++) h[k] = tide.HeightAt(start.AddMinutes(stepMin * k));   // h[Now] is known
            for (int k = 1; k < n; k++) if (double.IsNaN(h[k])) h[k] = h[k - 1];       // past the predictions: level
            for (int k = n - 2; k >= 0; k--) if (double.IsNaN(h[k])) h[k] = h[k + 1];  // and before them
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in h) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            double range = Math.Max(1.0, hi - lo);
            t.Curve = new PointF[n];
            for (int k = 0; k < n; k++)
                t.Curve[k] = new PointF(t.Spark.Left + t.Spark.Width * k / (n - 1), t.Spark.Bottom - (float)((h[k] - lo) / range) * (sparkH - 1));
            t.Now = TidePastHours * 60 / stepMin;

            L.MapTide = t;
            taken.Add(t.Panel);
            StringBuilder key = new StringBuilder(tide.Id);
            key.Append(' ').Append((now.Ticks / TimeSpan.TicksPerMinute / stepMin).ToString(CultureInfo.InvariantCulture));   // the curve moves on
            foreach (TideRow r in t.Rows) key.Append('|').Append(r.Label).Append(' ').Append(r.Time).Append(' ').Append(r.Height).Append(' ').Append(r.Change);
            return key.Append('|').Append(left.ToString("0", CultureInfo.InvariantCulture)).ToString();
        }

        /// <summary>Feet to one decimal with a real minus sign, and a plus sign when asked: "6.3", "−0.2", "+5.7".</summary>
        internal static string Feet(double feet, bool signed)
        {
            double r = Math.Round(feet, 1);
            string s = Math.Abs(r).ToString("0.0", CultureInfo.InvariantCulture);
            return r < 0 ? "\u2212" + s : signed ? "+" + s : s;
        }

        public const int VeilMs = 300;
        private bool mapFading;
        private float mapVeil;
        private long mapVeilAt = -1;

        /// <summary>
        /// The live part of the map: the weather picture (replaying the past hour at the end of every cycle),
        /// the shoreline drawn over it so the coast stays readable under fog, the labels, the legend and
        /// credit, the picture's time in the header, and during the replay a mark running along the header's
        /// rule. While a PIN is typed (or a notice shows in the PIN row, which shares the map's lowest band)
        /// the map fades to black, and comes back when the row clears.
        /// </summary>
        private void DrawMap(Graphics g, Layout l, DisplayState s, float ox, float oy, long now, float fadeIn)
        {
            mapFading = false;
            if (l.MapData == null || l.Map.Width < 8 || l.Map.Height < 8) return;
            WeatherMap map = l.MapData;
            int mx = (int)Math.Round(ox + l.Map.X), my = (int)Math.Round(oy + l.Map.Y);
            int mw = (int)Math.Round(l.Map.Width), mh = (int)Math.Round(l.Map.Height);

            bool pinUp = s.PinState != PinGate.StateIdle || s.AdminNotice;
            if (mapVeilAt < 0) mapVeil = pinUp ? 1f : 0f;
            else
            {
                float step = Math.Max(0, now - mapVeilAt) / (float)VeilMs;
                mapVeil = pinUp ? Math.Min(1f, mapVeil + step) : Math.Max(0f, mapVeil - step);
            }
            mapVeilAt = now;
            float veil = InOutSine(mapVeil);
            if (mapVeil > 0 && mapVeil < 1) mapFading = true;
            if (veil > 0.004f)
            {
                using (SolidBrush black = new SolidBrush(Color.FromArgb((int)Math.Round(255 * veil), 0, 0, 0)))
                    g.FillRectangle(black, mx, my, mw, mh);
            }
            float show = (1 - veil) * fadeIn;
            float dim = (float)(s.NightDim > 0 && s.NightDim < 1 ? s.NightDim : 1.0) * show;
            MapFrame[] fr = l.MapFrames;
            if (fr.Length > 0)
            {
                int i, j;
                float t, progress;
                bool playing;
                WeatherMap.LoopAt(fr.Length, now, out i, out j, out t, out progress, out playing);
                Bitmap a = MapArt.Frame(map, l.MapCrop, mw, mh, fr[i], true);
                Bitmap b = t > 0 && j != i ? MapArt.Frame(map, l.MapCrop, mw, mh, fr[j], false) : null;
                if (b == null) t = 0;   // not ready yet: stay on this picture (it is rendered below)
                DrawPicture(g, a, mx, my, dim * (1 - t));
                if (b != null) DrawPicture(g, b, mx, my, dim * t);
                if (t > 0 && t < 1) mapFading = true;

                MapFrame shown = t < 0.5f ? fr[i] : fr[j];
                string when = shown.Utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture).ToUpperInvariant();
                float tw = Measure(g, when, l.Label.Font, l.Track);
                DrawLiveText(g, when, l.Label, Fade(Tertiary, fadeIn), ox + l.Map.Right - tw, oy + l.MapHeadBaseline, l.Track);
                if (playing)
                {
                    float segW = (float)Math.Round(Math.Max(l.Unit * 0.12f, l.Map.Width / fr.Length));
                    float sx = (float)Math.Round(ox + l.Map.X + progress * (l.Map.Width - segW));
                    using (SolidBrush mark = new SolidBrush(Fade(Secondary, fadeIn)))
                        g.FillRectangle(mark, sx, (float)Math.Round(oy + l.MapRuleY) - 1, segW, 2);
                }
            }
            DrawPicture(g, MapArt.Shore(map, l.MapCrop, mw, mh), mx, my, show);
            Bitmap overlay = MapArt.Overlay(map, l.MapCrop, mw, mh, l.MapOverlayKey, og => DrawMapOverlay(og, l, mw, mh));
            DrawPicture(g, overlay, mx, my, show);
            if (fr.Length > 0) MapArt.WarmOne(map, l.MapCrop, mw, mh, fr);   // at most one picture a frame, so a replay never waits
        }

        /// <summary>
        /// The tide inset, the temperatures and wind, home, and along the bottom edge (darkened a little, as under
        /// a photo caption, so the words read over cloud and rain) the radar's legend at the left and the credit
        /// at the right.
        /// </summary>
        private static void DrawMapOverlay(Graphics og, Layout l, int mw, int mh)
        {
            og.SmoothingMode = SmoothingMode.AntiAlias;
            og.PixelOffsetMode = PixelOffsetMode.HighQuality;
            og.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Face f = l.Label;
            float pad = (float)Math.Round(l.Unit * 0.14f);
            float baseline = (float)Math.Round(mh - pad);
            bool legend = l.MapRadar && l.MapFrames.Length > 0;
            if (legend || l.MapData.Credit.Length > 0)
            {
                float band = (float)Math.Round(pad * 2.6f + f.CapHeight);
                RectangleF r = new RectangleF(0, mh - band, mw, band);
                using (LinearGradientBrush shade = new LinearGradientBrush(new PointF(0, r.Top - 1), new PointF(0, r.Bottom),
                                                                         Color.FromArgb(0, 0, 0, 0), Color.FromArgb(170, 0, 0, 0)))
                    og.FillRectangle(shade, r);
            }
            if (l.MapData.Credit.Length > 0)
            {
                string credit = l.MapData.Credit.ToUpperInvariant();
                DrawText(og, credit, f.Font, Tertiary, (float)Math.Round(mw - pad - Measure(og, credit, f.Font, l.Track)), (float)Math.Round(baseline - f.Ascent), l.Track, 0);
            }
            if (legend)
            {
                float x = pad, sw = (float)Math.Round(f.CapHeight), gap = Math.Max(2f, (float)Math.Round(sw * 0.3f));
                DrawText(og, "LIGHT", f.Font, Tertiary, x, (float)Math.Round(baseline - f.Ascent), l.Track, 0);
                x += Measure(og, "LIGHT", f.Font, l.Track) + sw * 0.6f;
                foreach (int dbz in new[] { 22, 30, 38, 46, 56 })
                {
                    int c = MapArt.RadarArgb(dbz);   // premultiplied: over black it shows as its RGB
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(255, (c >> 16) & 255, (c >> 8) & 255, c & 255)))
                        og.FillRectangle(sb, (float)Math.Round(x), baseline - sw, sw, sw);
                    x += sw + gap;
                }
                DrawText(og, "HEAVY", f.Font, Tertiary, (float)Math.Round(x - gap + sw * 0.6f), (float)Math.Round(baseline - f.Ascent), l.Track, 0);
            }
            float haloW = Math.Max(1.5f, l.Unit / 75f);
            float lineGap = (float)Math.Round(l.Unit * 0.07f);
            if (l.MapTide != null) DrawTide(og, l, l.MapTide);
            // Temperatures, and wind where it blows 10 mph or more: an arrow the way it blows, then its speed.
            foreach (PlacedStation ps in l.MapStations)
            {
                Face df = l.Data, sf = l.Small;
                string t = ps.Station.TempF.ToString(CultureInfo.InvariantCulture) + "°";
                HaloString(og, t, df, Primary, ps.X - Measure(og, t, df.Font, 0) / 2f, ps.Y + df.CapHeight / 2f, haloW);
                if (!WeatherMap.Windy(ps.Station)) continue;
                string wt = WeatherMap.WindText(ps.Station);
                float aw = sf.CapHeight * 1.5f, ww = aw + Measure(og, wt, sf.Font, 0);
                float baseY = ps.Y + df.CapHeight / 2f + lineGap + sf.CapHeight, x0 = ps.X - ww / 2f;
                if (ps.Station.WindFrom >= 0)
                    WindArrow(og, x0 + sf.CapHeight * 0.55f, baseY - sf.CapHeight / 2f, sf.CapHeight * 1.1f, (ps.Station.WindFrom + 180) % 360, haloW);
                HaloString(og, wt, sf, Secondary, x0 + aw, baseY, haloW);
            }
            if (!float.IsNaN(l.MapHome.X))
            {
                float d = Math.Max(6f, (float)Math.Round(l.Unit / 14f)), ring = Math.Max(1.5f, d * 0.24f);
                PointF p = l.MapHome;
                using (SolidBrush halo = new SolidBrush(Color.Black))
                    og.FillEllipse(halo, p.X - d / 2f - ring, p.Y - d / 2f - ring, d + 2 * ring, d + 2 * ring);
                using (SolidBrush dot = new SolidBrush(Accent))
                    og.FillEllipse(dot, p.X - d / 2f, p.Y - d / 2f, d, d);
            }
        }

        /// <summary>
        /// The tide inset: a dark ground, and the table: labels in the label tone, times and changes in the
        /// body tone, heights brightest; in the first row the day's curve, drawn like the system graphs (the
        /// hours gone fainter than the coming ones, now a bright dot).
        /// </summary>
        private static void DrawTide(Graphics og, Layout l, TideChart t)
        {
            Face fl = l.Label, fs = l.Small;
            using (SolidBrush ground = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                og.FillRectangle(ground, t.Panel);
            foreach (TideRow r in t.Rows)
            {
                float top = (float)Math.Round(r.Baseline - fs.Ascent);
                DrawText(og, r.Label, fl.Font, Tertiary, (float)Math.Round(t.LabelX), (float)Math.Round(r.Baseline - fl.Ascent), l.Track, 0);
                DrawText(og, r.Time, fs.Font, Secondary, (float)Math.Round(t.TimeRight - Measure(og, r.Time, fs.Font, 0)), top, 0, 0);
                DrawText(og, r.Height, fs.Font, Primary, (float)Math.Round(t.HeightRight - Measure(og, r.Height, fs.Font, 0)), top, 0, 0);
                DrawText(og, r.Change, fs.Font, Secondary, (float)Math.Round(t.ChangeRight - Measure(og, r.Change, fs.Font, 0)), top, 0, 0);
            }

            RectangleF sp = t.Spark;
            using (Pen axis = new Pen(Track, 1f)) og.DrawLine(axis, sp.Left, (float)Math.Round(sp.Bottom) + 0.5f, sp.Right, (float)Math.Round(sp.Bottom) + 0.5f);
            PointF[] past = new PointF[t.Now + 1], coming = new PointF[t.Curve.Length - t.Now];
            Array.Copy(t.Curve, 0, past, 0, past.Length);
            Array.Copy(t.Curve, t.Now, coming, 0, coming.Length);
            FillUnder(og, past, sp.Bottom, Color.FromArgb(23, Secondary));
            FillUnder(og, coming, sp.Bottom, Color.FromArgb(46, Secondary));
            float lw = Math.Max(1.5f, l.Unit / 80f);
            using (Pen line = new Pen(Tertiary, lw) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                og.DrawLines(line, past);
            using (Pen line = new Pen(Secondary, lw) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                og.DrawLines(line, coming);
            PointF p = t.Curve[t.Now];
            float d = Math.Max(5f, l.Unit / 20f);
            using (SolidBrush ring = new SolidBrush(Color.Black))
                og.FillEllipse(ring, p.X - d / 2f - lw, p.Y - d / 2f - lw, d + 2 * lw, d + 2 * lw);
            using (SolidBrush dot = new SolidBrush(Hero))
                og.FillEllipse(dot, p.X - d / 2f, p.Y - d / 2f, d, d);
        }

        /// <summary>The area under a curve down to a baseline.</summary>
        private static void FillUnder(Graphics g, PointF[] pts, float baseY, Color c)
        {
            if (pts.Length < 2) return;
            PointF[] poly = new PointF[pts.Length + 2];
            Array.Copy(pts, poly, pts.Length);
            poly[pts.Length] = new PointF(pts[pts.Length - 1].X, baseY);
            poly[pts.Length + 1] = new PointF(pts[0].X, baseY);
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, poly);
        }

        /// <summary>Text as a shape with a dark outline under it, so it reads over cloud and rain.</summary>
        private static void HaloString(Graphics g, string text, Face f, Color c, float x, float baseline, float halo)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddString(text, f.Font.FontFamily, (int)f.Font.Style, f.Font.Size,
                            new PointF((float)Math.Round(x), (float)Math.Round(baseline - f.Ascent)), Typo);
                using (Pen pen = new Pen(Color.FromArgb(150, 0, 0, 0), halo * 2) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, p);
                using (SolidBrush b = new SolidBrush(c))
                    g.FillPath(b, p);
            }
        }

        /// <summary>A small arrow centred on (cx, cy) pointing the way the wind blows (degrees clockwise from north).</summary>
        private static void WindArrow(Graphics g, float cx, float cy, float len, int toward, float halo)
        {
            double a = toward * Math.PI / 180;
            float dx = (float)Math.Sin(a), dy = (float)-Math.Cos(a);
            PointF tail = new PointF(cx - dx * len / 2f, cy - dy * len / 2f), head = new PointF(cx + dx * len / 2f, cy + dy * len / 2f);
            float hw = len * 0.34f;
            PointF[] wings =
            {
                new PointF(head.X - dx * hw - dy * hw * 0.8f, head.Y - dy * hw + dx * hw * 0.8f), head,
                new PointF(head.X - dx * hw + dy * hw * 0.8f, head.Y - dy * hw - dx * hw * 0.8f),
            };
            float lw = Math.Max(1.5f, len / 9f);
            using (Pen shade = new Pen(Color.FromArgb(150, 0, 0, 0), lw + 2 * halo) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(shade, tail, head);
                g.DrawLines(shade, wings);
            }
            using (Pen pen = new Pen(Secondary, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, tail, head);
                g.DrawLines(pen, wings);
            }
        }

        private static void DrawPicture(Graphics g, Bitmap b, int x, int y, float alpha)
        {
            if (b == null || alpha <= 0.004f) return;
            if (alpha >= 0.996f) { g.DrawImageUnscaled(b, x, y); return; }
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = alpha;
                ia.SetColorMatrix(cm);
                g.DrawImage(b, new Rectangle(x, y, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
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
        /// The data column: a SYSTEM table (value, ten-minute chart or bar, note), then the FORECAST: the next
        /// six hours as a strip (temperature curve, rain-chance bars) and the next three days. Every width and
        /// height comes from templates of the widest content a slot can hold, never from the live values, so
        /// changing numbers can never resize or move the composition. With ops null it only measures and
        /// returns the width it needs; otherwise it lays everything out at the given width and position and
        /// returns the bottom edge.
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
                bool graphs = s.Sys != null;
                float labelW = 0, valueW = 0, noteW = 0, graphW = graphs ? (float)Math.Round(unit * 1.3f) : 0;
                foreach (string t in new[] { "CPU", "MEMORY", "NETWORK", "DISK", "UPTIME" }) labelW = Math.Max(labelW, Measure(m, t, fLabel.Font, track));
                foreach (string t in new[] { "100%", "↓ 999.9 KB/s", "↓ 99.9 MB/s", "99d 23h", "--" }) valueW = Math.Max(valueW, Measure(m, t, fData.Font, 0));
                foreach (string t in new[] { "99.9 / 999 GB", "↑ 999.9 KB/s", "↑ 99.9 MB/s", "9999 GB free" }) noteW = Math.Max(noteW, Measure(m, t, fSmall.Font, 0));
                if (!graphs)
                {
                    // Plain strings only (no numbers): size for what is there.
                    foreach (object[] r in rows) valueW = Math.Max(valueW, Measure(m, (string)r[1], fData.Font, 0));
                    noteW = 0;
                }
                float tableW = labelW + gx + valueW + (graphs ? gx + graphW + gx + noteW : 0);
                const string side = "LAST 10 MIN";
                tableW = Math.Max(tableW, Measure(m, "SYSTEM", fLabel.Font, track) + gx + Measure(m, side, fLabel.Font, track));
                needed = Math.Max(needed, tableW);
                float w = Math.Max(width, tableW);

                y += fLabel.CapHeight;
                AddText(ops, m, "SYSTEM", fLabel, Tertiary, ox, y, track);
                if (graphs) AddText(ops, m, side, fLabel, Tertiary, ox + w - Measure(m, side, fLabel.Font, track), y, track);
                y += st(6) * 0.8f;
                ops.Add(new Op { Kind = 1, X = ox, Y = (float)Math.Round(y), W = w, H = 1, Color = RuleColor });
                y += st(5) + fData.CapHeight;
                float pitch = fData.Font.Size * 1.8f;
                float vx = ox + labelW + gx, gx0 = vx + valueW + gx, nx = gx0 + graphW + gx;
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
                    if (graphs && ((string)r[4]).Length > 0) AddText(ops, m, (string)r[4], fSmall, Tertiary, nx, y, 0);
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
            HourPoint[] hours = NextHours(s.Hourly, s.Now, 6);
            bool haveDays = raw.Count > 0, haveHours = s.Hourly != null && s.Hourly.Length > 0;
            if (haveDays || haveHours)
            {
                if (rows.Count > 0) y += st(3);
                string head = "FORECAST" + (place != null ? "   ·   " + place.ToUpperInvariant() : "");
                string side = asOf != null ? "AS OF " + asOf.ToUpperInvariant() : "";
                float headW = Measure(m, head, fLabel.Font, track) + gx + Measure(m, "AS OF 12:59 PM", fLabel.Font, track);
                float line2 = st(5) * 0.95f;
                float icon = (float)Math.Round(fData.CapHeight + line2);
                float tx = icon + gx * 0.75f;
                float drop = (float)Math.Round(fData.CapHeight * 0.95f);
                float dayW = Measure(m, "WEDNESDAY", fLabel.Font, track);
                float tempW = Measure(m, "-99°", fData.Font, 0);
                float popW = drop * 0.62f + gx * 0.25f + Measure(m, "100%", fData.Font, 0);
                float rowW = tx + dayW + gx + tempW + gx * 0.6f + tempW + gx + popW;
                float slotMin = Math.Max(Measure(m, "12 PM", fLabel.Font, track), Measure(m, "100%", fData.Font, 0)) + gx * 0.6f;
                float tableW = Math.Max(Math.Max(rowW, headW), 6 * slotMin);
                for (int i = 0; i < 4; i++)
                    tableW = Math.Max(tableW, (Measure(m, TileTemplates[i], fData.Font, 0) + gx * 0.4f) / TileShare[i]);
                needed = Math.Max(needed, tableW);
                float w = Math.Max(width, tableW);

                y += fLabel.CapHeight;
                AddText(ops, m, head, fLabel, Tertiary, ox, y, track);
                if (side.Length > 0) AddText(ops, m, side, fLabel, Tertiary, ox + w - Measure(m, side, fLabel.Font, track), y, track);
                y += st(6) * 0.8f;
                ops.Add(new Op { Kind = 1, X = ox, Y = (float)Math.Round(y), W = w, H = 1, Color = RuleColor });

                // ---- right now: humidity and wind for this hour, the next sunrise and sunset (four quiet tiles)
                {
                    HourPoint nowHour = hours.Length > 0 ? hours[0] : null;
                    DateTime rise, set;
                    bool sun = NextSun(s.Now, s.Lat, s.Lon, out rise, out set);
                    string[] values =
                    {
                        nowHour != null && nowHour.Humidity >= 0 ? nowHour.Humidity.ToString(CultureInfo.InvariantCulture) + "%" : "--",
                        nowHour != null && nowHour.Wind.Length > 0 ? nowHour.Wind : "--",
                        sun ? rise.ToString("t", CultureInfo.CurrentCulture) : "--",
                        sun ? set.ToString("t", CultureInfo.CurrentCulture) : "--",
                    };
                    y += st(5) + fLabel.CapHeight;
                    float vy = y + st(6) + fData.CapHeight;
                    float tx0 = ox;
                    for (int i = 0; i < 4; i++)
                    {
                        float tw = w * TileShare[i];
                        AddText(ops, m, TileLabels[i], fLabel, Tertiary, tx0, y, track);
                        Op v = AddTextOp(ops, values[i], fData, Secondary, tx0, vy, 0);
                        if (Measure(m, values[i], fData.Font, 0) > tw - gx * 0.4f) v.W = tw - gx * 0.4f;   // never into the next tile
                        tx0 += tw;
                    }
                    y = vy + fData.Font.Size * 0.3f;
                }

                // ---- the next six hours: hour, symbol, temperature on a curve, chance of rain
                y += st(4.5) + fLabel.CapHeight;
                AddText(ops, m, "NEXT 6 HOURS", fLabel, Tertiary, ox, y, track);
                float slot = w / 6f;
                float hourBase = y + st(5) + fLabel.CapHeight;
                float iconSz = (float)Math.Round(fData.CapHeight * 2.0f);
                float iconTop = hourBase + st(6);
                float curveTop = iconTop + iconSz + st(6) + fData.CapHeight + st(6.5);
                float curveH = (float)Math.Round(st(4) * 0.85f);
                float curveBottom = curveTop + curveH;
                float barMax = (float)Math.Round(st(4.4));
                float barBase = curveBottom + st(5.5) + barMax;
                float popBase = barBase + st(6.5) + fLabel.CapHeight;
                if (hours.Length > 0)
                {
                    int tmin = int.MaxValue, tmax = int.MinValue;
                    foreach (HourPoint hp in hours) { tmin = Math.Min(tmin, hp.Temp); tmax = Math.Max(tmax, hp.Temp); }
                    double lo = tmin, hi = tmax;
                    if (hi - lo < 6) { double mid = (hi + lo) / 2; lo = mid - 3; hi = mid + 3; }   // a flat day should look flat
                    float[] curve = new float[hours.Length * 2];
                    float bw = Math.Max(4f, (float)Math.Round(slot * 0.14f));
                    for (int i = 0; i < hours.Length; i++)
                    {
                        HourPoint hp = hours[i];
                        float cx = ox + slot * (i + 0.5f);
                        string when = i == 0 ? "NOW" : HourLabel(hp.Start);
                        AddCentered(ops, m, when, fLabel, i == 0 ? Secondary : Tertiary, cx, hourBase, track);
                        ops.Add(new Op { Kind = 3, Text = Symbol(hp.Text, !hp.IsDay), X = cx - iconSz / 2f, Y = iconTop, W = iconSz, H = iconSz, Color = Secondary });
                        float py = curveBottom - (float)((hp.Temp - lo) / (hi - lo)) * curveH;
                        curve[2 * i] = cx;
                        curve[2 * i + 1] = py;
                        AddCentered(ops, m, hp.Temp.ToString(CultureInfo.InvariantCulture) + "°", fData, i == 0 ? Hero : Primary, cx, py - st(6.5), 0);
                        if (hp.Pop >= 0)
                        {
                            float bh = Math.Max(1f, (float)Math.Round(barMax * hp.Pop / 100f));
                            ops.Add(new Op { Kind = 7, X = (float)Math.Round(cx - bw / 2f), Y = barBase - bh, W = bw, H = bh, Color = Rain });
                        }
                        AddCentered(ops, m, hp.Pop >= 0 ? hp.Pop.ToString(CultureInfo.InvariantCulture) + "%" : "–", fLabel, Tertiary, cx, popBase, 0);
                    }
                    ops.Add(new Op { Kind = 8, Data = curve, Color = Secondary });
                    ops.Add(new Op { Kind = 1, X = ox, Y = (float)Math.Round(barBase), W = w, H = 1, Color = Track });
                }
                else
                {
                    AddText(ops, m, "THE HOURLY FORECAST WILL APPEAR WITH THE NEXT REFRESH", fLabel, Tertiary, ox, (curveTop + curveBottom) / 2f, track);
                }
                y = popBase + fLabel.Font.Size * 0.35f;

                // ---- the next three days (three rows are always reserved)
                y += st(4.5) + fLabel.CapHeight;
                AddText(ops, m, "NEXT 3 DAYS", fLabel, Tertiary, ox, y, track);
                y += st(5) + fData.CapHeight;
                float rowPitch = line2 + st(4) * 0.95f + fData.CapHeight;
                for (int i = 0; i < 3; i++)
                {
                    if (parsed && i < wx.Count)
                    {
                        string[] r = wx[i];
                        bool night = r[0].IndexOf("night", StringComparison.OrdinalIgnoreCase) >= 0;
                        ops.Add(new Op { Kind = 3, Text = Symbol(r[3], night), X = ox, Y = (float)Math.Round(y - fData.CapHeight), W = icon, H = icon, Color = Secondary });
                        string day = r[0].ToUpperInvariant();
                        Op dayOp = AddTextOp(ops, day, fLabel, Tertiary, ox + tx, y, track);
                        if (Measure(m, day, fLabel.Font, track) > dayW) dayOp.W = dayW;   // an unexpected long name is cut, not allowed to push
                        float hx = ox + tx + dayW + gx;
                        if (r[1].Length > 0) AddText(ops, m, r[1], fData, Primary, hx + tempW - Measure(m, r[1], fData.Font, 0), y, 0);
                        float lx = hx + tempW + gx * 0.6f;
                        if (r[2].Length > 0) AddText(ops, m, r[2], fData, Secondary, lx + tempW - Measure(m, r[2], fData.Font, 0), y, 0);
                        if (r[4].Length > 0)
                        {
                            float pw = Measure(m, r[4], fData.Font, 0);
                            float px = ox + w - pw;
                            AddText(ops, m, r[4], fData, Tertiary, px, y, 0);
                            float dw = drop * 0.62f;
                            ops.Add(new Op { Kind = 6, X = px - gx * 0.25f - dw, Y = y - drop, W = dw, H = drop, Color = Rain });
                        }
                        Op d = AddTextOp(ops, SentenceCase(r[3]), fSmall, Tertiary, ox + tx, y + line2, 0);
                        d.W = w - tx;
                    }
                    else if (!parsed && i < raw.Count)
                    {
                        Op d = AddTextOp(ops, raw[i], fSmall, Secondary, ox, y, 0);
                        d.W = w;
                    }
                    y += rowPitch;
                }
                y -= st(4) * 0.95f + fData.CapHeight;
                y += fSmall.Font.Size * 0.35f;
            }
            return measure ? needed : y;
        }

        private static readonly string[] TileLabels = { "HUMIDITY", "WIND", "SUNRISE", "SUNSET" };
        private static readonly string[] TileTemplates = { "100%", "15–25 mph WNW", "12:59 AM", "12:59 PM" };
        private static readonly float[] TileShare = { 0.24f, 0.32f, 0.22f, 0.22f };

        /// <summary>The next sunrise and the next sunset after "now", in local time (false if unknown or polar).</summary>
        internal static bool NextSun(DateTime now, double lat, double lon, out DateTime rise, out DateTime set)
        {
            rise = set = DateTime.MinValue;
            if (double.IsNaN(lat) || double.IsNaN(lon)) return false;
            DateTime r, st;
            if (!SunTimesUtc(now.Date, lat, lon, out r, out st)) return false;
            rise = r.ToLocalTime();
            set = st.ToLocalTime();
            if (rise <= now && SunTimesUtc(now.Date.AddDays(1), lat, lon, out r, out st)) rise = r.ToLocalTime();
            if (set <= now && SunTimesUtc(now.Date.AddDays(1), lat, lon, out r, out st)) set = st.ToLocalTime();
            return true;
        }

        /// <summary>
        /// Sunrise and sunset (UTC) for a calendar day at a place, from NOAA's solar equations (equation of
        /// time and declination as Fourier series, the sun's centre 0.833 degrees below the horizon for
        /// refraction and its radius). Accurate to about a minute; false where the sun does not rise or set.
        /// </summary>
        internal static bool SunTimesUtc(DateTime day, double lat, double lon, out DateTime riseUtc, out DateTime setUtc)
        {
            riseUtc = setUtc = DateTime.MinValue;
            double g = 2 * Math.PI / (DateTime.IsLeapYear(day.Year) ? 366 : 365) * (day.DayOfYear - 1);
            double eq = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g)
                                  - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
            double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g)
                          + 0.000907 * Math.Sin(2 * g) - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
            double phi = lat * Math.PI / 180;
            double cosHa = Math.Cos(90.833 * Math.PI / 180) / (Math.Cos(phi) * Math.Cos(decl)) - Math.Tan(phi) * Math.Tan(decl);
            if (cosHa < -1 || cosHa > 1) return false;
            double ha = Math.Acos(cosHa) * 180 / Math.PI;
            DateTime midnight = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc);
            riseUtc = midnight.AddMinutes(720 - 4 * (lon + ha) - eq);
            setUtc = midnight.AddMinutes(720 - 4 * (lon - ha) - eq);
            return true;
        }

        /// <summary>The sun's height above the horizon in degrees at a moment and place (same NOAA equations; refraction ignored).</summary>
        internal static double SunElevation(DateTime utc, double lat, double lon)
        {
            double hours = utc.TimeOfDay.TotalHours;
            double g = 2 * Math.PI / (DateTime.IsLeapYear(utc.Year) ? 366 : 365) * (utc.DayOfYear - 1 + (hours - 12) / 24);
            double eq = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g)
                                  - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
            double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g)
                          + 0.000907 * Math.Sin(2 * g) - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
            double hourAngle = ((hours * 60 + eq + 4 * lon) / 4 - 180) * Math.PI / 180;   // from true solar time
            double phi = lat * Math.PI / 180;
            double cosZ = Math.Sin(phi) * Math.Sin(decl) + Math.Cos(phi) * Math.Cos(decl) * Math.Cos(hourAngle);
            return 90 - Math.Acos(Math.Max(-1, Math.Min(1, cosZ))) * 180 / Math.PI;
        }

        /// <summary>The current hour and the ones after it, oldest first.</summary>
        internal static HourPoint[] NextHours(HourPoint[] all, DateTime now, int count)
        {
            List<HourPoint> list = new List<HourPoint>();
            if (all != null)
            {
                foreach (HourPoint h in all) if (h.Start.AddHours(1) > now) list.Add(h);
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list.ToArray();
        }

        private static string HourLabel(DateTime t)
        {
            DateTimeFormatInfo f = CultureInfo.CurrentCulture.DateTimeFormat;
            bool twelve = f.ShortTimePattern.IndexOf('t') >= 0;
            return (twelve ? t.ToString("h tt", CultureInfo.CurrentCulture) : t.ToString("HH:00", CultureInfo.CurrentCulture)).ToUpperInvariant();
        }

        private static void AddCentered(List<Op> ops, Graphics m, string text, Face f, Color c, float cx, float baseline, float tracking)
        {
            AddTextOp(ops, text, f, c, cx - Measure(m, text, f.Font, tracking) / 2f, baseline, tracking);
        }

        /// <summary>
        /// Always the same five rows once readings exist: label, value, graphic (float[] history, double
        /// percent, or null), chart max, note. A reading that is not available yet shows "--".
        /// </summary>
        private static List<object[]> SystemRows(DisplayState s)
        {
            List<object[]> rows = new List<object[]>();
            SysSnapshot x = s.Sys;
            if (x != null)
            {
                bool any = x.Version > 0;
                rows.Add(new object[] { "CPU", any && x.Cpu >= 0 ? Math.Round(x.Cpu).ToString(CultureInfo.InvariantCulture) + "%" : "--", x.CpuHistory, 100.0, "" });
                rows.Add(new object[] { "MEMORY", any && x.MemPercent >= 0 ? Math.Round(x.MemPercent).ToString(CultureInfo.InvariantCulture) + "%" : "--",
                                        any ? x.MemPercent : -1.0, 0.0,
                                        any && x.MemPercent >= 0 ? x.MemUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + x.MemTotalGb.ToString("0", CultureInfo.InvariantCulture) + " GB" : "" });
                double peak = 0;
                foreach (float v in x.NetHistory) if (v > peak) peak = v;
                rows.Add(new object[] { "NETWORK", any && x.NetDown >= 0 ? "↓ " + Rate(x.NetDown) : "--", x.NetHistory, Math.Max(128 * 1024.0, peak * 1.15),
                                        any && x.NetUp >= 0 ? "↑ " + Rate(x.NetUp) : "" });
                rows.Add(new object[] { "DISK", any && x.DiskUsedPercent >= 0 ? Math.Round(x.DiskUsedPercent).ToString(CultureInfo.InvariantCulture) + "%" : "--",
                                        any ? x.DiskUsedPercent : -1.0, 0.0,
                                        any && x.DiskUsedPercent >= 0 ? x.DiskFreeGb.ToString("0", CultureInfo.InvariantCulture) + " GB free" : "" });
                rows.Add(new object[] { "UPTIME", any && x.UptimeSeconds >= 0 ? Uptime(x.UptimeSeconds) : "--", null, 0.0, "" });
                return rows;
            }
            // No numeric readings at all (only in previews of old states): the plain strings, without graphics.
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

        private static string WidestDigit(Graphics m, Font font, string digits)
        {
            string best = digits.Substring(0, 1);
            float bestW = -1;
            foreach (char d in digits)
            {
                float w = Measure(m, d.ToString(), font, 0);
                if (w > bestW) { bestW = w; best = d.ToString(); }
            }
            return best;
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
                case 7:
                    using (GraphicsPath p = RoundedTop(ox + op.X, oy + op.Y, op.W, op.H))
                    using (SolidBrush b = new SolidBrush(op.Color))
                        g.FillPath(b, p);
                    break;
                case 8:
                    DrawTemperatureCurve(g, op, ox, oy, unit);
                    break;
                case 9:
                    Bitmap pic = op.Picture != null ? op.Picture() : null;
                    if (pic != null) g.DrawImageUnscaled(pic, (int)Math.Round(ox + op.X), (int)Math.Round(oy + op.Y));
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

        /// <summary>A rain-chance bar: anchored on its baseline, a rounded data end on top.</summary>
        private static GraphicsPath RoundedTop(float x, float y, float w, float h)
        {
            GraphicsPath p = new GraphicsPath();
            float r = Math.Min(w / 2f, h);
            if (h <= r) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
            p.AddArc(x, y, w, 2 * r, 180, 180);
            p.AddLine(x + w, y + r, x + w, y + h);
            p.AddLine(x + w, y + h, x, y + h);
            p.CloseFigure();
            return p;
        }

        /// <summary>The hourly temperatures as one smooth line, a dot on each hour, the current hour brighter.</summary>
        private static void DrawTemperatureCurve(Graphics g, Op op, float ox, float oy, float unit)
        {
            float[] d = op.Data;
            if (d == null || d.Length < 2) return;
            PointF[] pts = new PointF[d.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(ox + d[2 * i], oy + d[2 * i + 1]);
            float lw = Math.Max(1.5f, unit / 80f);
            if (pts.Length > 1)
            {
                using (Pen p = new Pen(op.Color, lw))
                {
                    p.LineJoin = LineJoin.Round;
                    g.DrawCurve(p, pts, 0.45f);
                }
            }
            float dot = Math.Max(5f, unit / 20f);
            for (int i = 0; i < pts.Length; i++)
            {
                using (SolidBrush halo = new SolidBrush(Color.Black))
                    g.FillEllipse(halo, pts[i].X - dot / 2f - lw, pts[i].Y - dot / 2f - lw, dot + 2 * lw, dot + 2 * lw);
                using (SolidBrush b = new SolidBrush(i == 0 ? Hero : op.Color))
                    g.FillEllipse(b, pts[i].X - dot / 2f, pts[i].Y - dot / 2f, dot, dot);
            }
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
