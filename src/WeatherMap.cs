using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCurtain
{
    /// <summary>A latitude / longitude box, in degrees.</summary>
    internal struct GeoBox
    {
        public double South, West, North, East;

        public GeoBox(double south, double west, double north, double east)
        {
            South = south; West = west; North = north; East = east;
        }

        public bool IsEmpty { get { return !(North > South) || !(East > West); } }
        public double KmTall { get { return (North - South) * WeatherMap.KmPerDegree; } }
        public double KmWide { get { return (East - West) * WeatherMap.KmPerDegree * Math.Cos((North + South) * Math.PI / 360); } }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#####} {1:0.#####} {2:0.#####} {3:0.#####}", South, West, North, East);
        }
    }

    /// <summary>One picture of the weather over the map's box, as numbers rather than colours.</summary>
    internal sealed class MapFrame
    {
        public DateTime Utc;
        public string Kind = "";   // "radar": dBZ (0 = nothing); "vis": visible brightness 0-255; "ir": infrared, colder = higher, 0-255
        public int W, H;
        public byte[] Values;      // row by row, north at the top

        public string Id { get { return Kind + "@" + Utc.Ticks.ToString(CultureInfo.InvariantCulture); } }
    }

    /// <summary>
    /// The live weather map: NOAA's radar mosaic (MRMS, every radar that sees the area blended and cleaned of
    /// ground clutter) and NOAA's GOES satellite pictures, over a box around home, plus where land and water
    /// are. curtain-weather.ps1 downloads them into local\ every few minutes and lists them in map.txt; the
    /// curtain only reads files and never touches the network. Built on the control thread each time map.txt
    /// changes and never changed afterwards, so the UI thread can use it freely.
    ///
    /// The map shows the radar when rain is near: a chance of rain of 30% or more (or rain in the words of the
    /// forecast) in the next three hours, or rain on the radar within 50 km. Otherwise it shows the clouds,
    /// fog included, from the satellite: the visible picture by day, infrared at night.
    /// </summary>
    internal sealed class WeatherMap
    {
        public const string IndexName = "map.txt";
        public const double KmPerDegree = 111.2;
        public const double ViewKm = 80;          // how much of the area the map shows, top to bottom
        public const double StaleHours = 3;       // older pictures are never shown
        public const int RainDbz = 20;            // light rain on the radar
        public const int RainChance = 30;         // % chance in the next three hours that makes the map show radar
        public const double NearKm = 50;          // rain on the radar this close to home does too

        public GeoBox Box;
        public int BaseW, BaseH;
        public byte[] Water = new byte[0];        // 0 = land ... 255 = water, over Box
        public string BaseId = "";
        public MapFrame[] Radar = new MapFrame[0];   // oldest first
        public MapFrame[] Sat = new MapFrame[0];     // oldest first, all one kind
        public string Credit = "";
        public string Key = "";                   // changes whenever anything shown changes

        // ------------------------------------------------------------------ the index file

        /// <summary>What map.txt lists. Lines: box S W N E | base FILE | radar UTC FILE | sat vis|ir UTC FILE | credit TEXT.</summary>
        internal sealed class Index
        {
            public GeoBox Box;
            public bool HasBox;
            public string Base;
            public string Credit = "";
            public readonly List<KeyValuePair<DateTime, string>> Radar = new List<KeyValuePair<DateTime, string>>();
            public readonly List<KeyValuePair<DateTime, string>> Sat = new List<KeyValuePair<DateTime, string>>();
            public string SatKind = "";

            public static Index Parse(string[] lines)
            {
                Index ix = new Index();
                foreach (string raw in lines ?? new string[0])
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    DateTime t;
                    switch (p[0])
                    {
                        case "box":
                            double s, w, n, e;
                            if (p.Length == 5 && Num(p[1], out s) && Num(p[2], out w) && Num(p[3], out n) && Num(p[4], out e) &&
                                n > s && e > w && Math.Abs(s) <= 90 && Math.Abs(n) <= 90 && Math.Abs(w) <= 180 && Math.Abs(e) <= 180 &&
                                n - s <= 10 && e - w <= 20)
                            {
                                ix.Box = new GeoBox(s, w, n, e);
                                ix.HasBox = true;
                            }
                            break;
                        case "base":
                            if (p.Length == 2 && PlainName(p[1])) ix.Base = p[1];
                            break;
                        case "radar":
                            if (p.Length == 3 && Utc(p[1], out t) && PlainName(p[2])) ix.Radar.Add(new KeyValuePair<DateTime, string>(t, p[2]));
                            break;
                        case "sat":
                            if (p.Length == 4 && (p[1] == "vis" || p[1] == "ir") && Utc(p[2], out t) && PlainName(p[3]))
                            {
                                if (ix.SatKind.Length > 0 && ix.SatKind != p[1]) break;   // one kind per loop
                                ix.SatKind = p[1];
                                ix.Sat.Add(new KeyValuePair<DateTime, string>(t, p[3]));
                            }
                            break;
                        case "credit":
                            ix.Credit = line.Substring(6).Trim();
                            break;
                    }
                }
                ix.Radar.Sort((a, b) => a.Key.CompareTo(b.Key));
                ix.Sat.Sort((a, b) => a.Key.CompareTo(b.Key));
                return ix;
            }

            private static bool Num(string s, out double v)
            {
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
            }

            private static bool Utc(string s, out DateTime t)
            {
                DateTimeOffset o;
                t = DateTime.MinValue;
                if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out o)) return false;
                t = o.UtcDateTime;
                return true;
            }

            /// <summary>A bare file name in local\: never a path, so map.txt can only point at its own folder.</summary>
            internal static bool PlainName(string name)
            {
                return !string.IsNullOrEmpty(name) && name.Length <= 120 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                       name.IndexOf("..", StringComparison.Ordinal) < 0 && name == Path.GetFileName(name);
            }
        }

        // ------------------------------------------------------------------ loading

        /// <summary>Reads map.txt and the pictures it lists. Null when there is no usable map. Never throws.</summary>
        internal static WeatherMap Load(string dir, DateTime utcNow)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return null;
                string index = Path.Combine(dir, IndexName);
                if (!File.Exists(index)) return null;
                Index ix = Index.Parse(File.ReadAllLines(index, Encoding.UTF8));
                if (!ix.HasBox || ix.Base == null) return null;

                WeatherMap m = new WeatherMap();
                m.Box = ix.Box;
                m.Credit = ix.Credit;
                int[] px;
                int w, h;
                if (!ReadPixels(Path.Combine(dir, ix.Base), out px, out w, out h)) return null;
                m.BaseW = w; m.BaseH = h;
                m.Water = WaterFrom(px);
                m.BaseId = ix.Base + "@" + File.GetLastWriteTimeUtc(Path.Combine(dir, ix.Base)).Ticks.ToString(CultureInfo.InvariantCulture);

                List<MapFrame> radar = new List<MapFrame>(), sat = new List<MapFrame>();
                foreach (KeyValuePair<DateTime, string> e in ix.Radar)
                {
                    if ((utcNow - e.Key).TotalHours > StaleHours) continue;
                    if (!ReadPixels(Path.Combine(dir, e.Value), out px, out w, out h)) continue;
                    radar.Add(new MapFrame { Utc = e.Key, Kind = "radar", W = w, H = h, Values = RadarFrom(px) });
                }
                foreach (KeyValuePair<DateTime, string> e in ix.Sat)
                {
                    if ((utcNow - e.Key).TotalHours > StaleHours) continue;
                    if (!ReadPixels(Path.Combine(dir, e.Value), out px, out w, out h)) continue;
                    sat.Add(new MapFrame { Utc = e.Key, Kind = ix.SatKind, W = w, H = h, Values = GreyFrom(px) });
                }
                m.Radar = radar.ToArray();
                m.Sat = sat.ToArray();
                m.Key = m.MakeKey();
                return m;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The same map without pictures that have grown too old (itself when none have).</summary>
        internal WeatherMap Fresh(DateTime utcNow)
        {
            Func<MapFrame[], MapFrame[]> keep = all => Array.FindAll(all, f => (utcNow - f.Utc).TotalHours <= StaleHours);
            MapFrame[] r = keep(Radar), s = keep(Sat);
            if (r.Length == Radar.Length && s.Length == Sat.Length) return this;
            WeatherMap m = (WeatherMap)MemberwiseClone();
            m.Radar = r;
            m.Sat = s;
            m.Key = m.MakeKey();
            return m;
        }

        private string MakeKey()
        {
            StringBuilder b = new StringBuilder();
            b.Append(Box).Append('|').Append(BaseId);
            foreach (MapFrame f in Radar) b.Append('|').Append(f.Id);
            foreach (MapFrame f in Sat) b.Append('|').Append(f.Id);
            return b.ToString();
        }

        private static bool ReadPixels(string path, out int[] px, out int w, out int h)
        {
            px = null; w = h = 0;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length == 0 || fi.Length > 8 * 1024 * 1024) return false;
                byte[] bytes = File.ReadAllBytes(path);   // read first, so the file is never held open
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image img = Image.FromStream(ms, false, true))
                {
                    if (img.Width < 2 || img.Height < 2 || img.Width > 4096 || img.Height > 4096) return false;
                    using (Bitmap b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                            g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
                        }
                        w = b.Width; h = b.Height;
                        px = MapArt.GetPixels(b);
                    }
                }
                return true;
            }
            catch
            {
                px = null; w = h = 0;
                return false;
            }
        }

        // ------------------------------------------------------------------ decoding the pictures

        /// <summary>The land and water map (NASA GIBS, from OpenStreetMap): land is dark grey, water light grey.</summary>
        internal static byte[] WaterFrom(int[] px)
        {
            byte[] w = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                int g = (px[i] >> 8) & 255;
                int v = (g - 76) * 255 / 50;
                w[i] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
            return w;
        }

        /// <summary>A satellite picture: grey levels (both kinds come with bright = cloud).</summary>
        internal static byte[] GreyFrom(int[] px)
        {
            byte[] v = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) v[i] = (byte)((px[i] >> 8) & 255);
            return v;
        }

        internal static byte[] RadarFrom(int[] px)
        {
            byte[] v = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) v[i] = RadarDbz(px[i]);
            return v;
        }

        // nowCOAST's radar colours ("weather_radar_base_reflectivity", a ramp between these points), dBZ -> RGB.
        // The server colours whole-dBZ values along the ramp, so each colour maps back to exactly one value.
        private static readonly int[] RampDbz =
        {
            1, 2, 3, 5, 6, 8, 9, 10, 11, 13, 14, 15, 16, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 30, 31, 33, 34, 35, 36, 38, 39, 40,
            41, 43, 44, 45, 46, 48, 49, 50, 51, 53, 54, 55, 56, 58, 59, 60, 61, 63, 64, 65, 66, 68, 69, 70, 71, 73, 74, 75, 76, 78, 79, 80,
        };
        private static readonly int[] RampRgb =
        {
            0x9C9B7B, 0x908F7A, 0x838378, 0x777777, 0x599494, 0x3BB1B1, 0x1DCECE, 0x00ECEC, 0x00D9EE, 0x00C6F1, 0x00B3F3, 0x01A0F6,
            0x0078F6, 0x0050F6, 0x0028F6, 0x0000F6, 0x003FB8, 0x007F7B, 0x00BF3D, 0x00FF00, 0x00F100, 0x00E300, 0x00D500, 0x00C800,
            0x00BA00, 0x00AC00, 0x009E00, 0x009000, 0x3FAB00, 0x7FC700, 0xBFE300, 0xFFFF00, 0xF9EF00, 0xF3DF00, 0xEDCF00, 0xE7C000,
            0xEDB400, 0xF3A800, 0xF99C00, 0xFF9000, 0xFF6C00, 0xFF4800, 0xFF2400, 0xFF0000, 0xF60000, 0xED0000, 0xE40000, 0xDC0000,
            0xD50000, 0xCE0000, 0xC70000, 0xC00000, 0xCF003F, 0xDF007F, 0xEF00BF, 0xFF00FF, 0xE515F1, 0xCC2AE4, 0xB23FD6, 0x9955C9,
            0x747BCE, 0x4FA1D4, 0x2AC7DA, 0x05EDE0,
        };
        private static int[] dbzColour;                        // whole dBZ 1..80 -> RGB on the ramp
        private static Dictionary<int, byte> colourDbz;        // RGB -> dBZ, filled lazily for in-between colours
        private static readonly object rampGate = new object();

        /// <summary>The colour nowCOAST gives a whole-dBZ value.</summary>
        internal static int RadarRgb(int dbz)
        {
            BuildRamp();
            return dbzColour[Math.Max(1, Math.Min(80, dbz))];
        }

        /// <summary>The dBZ a radar pixel stands for (0 = no echo).</summary>
        internal static byte RadarDbz(int argb)
        {
            if (((argb >> 24) & 255) == 0) return 0;
            int rgb = argb & 0xFFFFFF;
            lock (rampGate)
            {
                BuildRamp();
                byte v;
                if (colourDbz.TryGetValue(rgb, out v)) return v;
                int best = 0, bestD = int.MaxValue;
                for (int d = 1; d <= 80; d++)
                {
                    int c = dbzColour[d];
                    int dr = ((c >> 16) & 255) - ((rgb >> 16) & 255), dg = ((c >> 8) & 255) - ((rgb >> 8) & 255), db = (c & 255) - (rgb & 255);
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < bestD) { bestD = dist; best = d; }
                }
                v = bestD <= 3 * 24 * 24 ? (byte)best : (byte)0;   // not a radar colour at all: treat as no echo
                if (colourDbz.Count < 4096) colourDbz[rgb] = v;
                return v;
            }
        }

        private static void BuildRamp()
        {
            lock (rampGate)
            {
                if (dbzColour != null) return;
                int[] colours = new int[81];
                Dictionary<int, byte> back = new Dictionary<int, byte>();
                for (int d = 1; d <= 80; d++)
                {
                    int k = 0;
                    while (k < RampDbz.Length - 1 && RampDbz[k + 1] < d) k++;
                    int c;
                    if (RampDbz[k] >= d || k == RampDbz.Length - 1) c = RampRgb[k];
                    else
                    {
                        int a = RampRgb[k], b = RampRgb[k + 1];
                        double t = (d - RampDbz[k]) / (double)(RampDbz[k + 1] - RampDbz[k]);
                        Func<int, int> ch = shift => (int)Math.Round(((a >> shift) & 255) + (((b >> shift) & 255) - ((a >> shift) & 255)) * t, MidpointRounding.AwayFromZero);
                        c = (ch(16) << 16) | (ch(8) << 8) | ch(0);
                    }
                    colours[d] = c;
                    if (!back.ContainsKey(c)) back[c] = (byte)d;
                }
                colourDbz = back;
                dbzColour = colours;
            }
        }

        // ------------------------------------------------------------------ what to show, and where

        /// <summary>
        /// True to show the radar, false for the clouds. Radar when rain is in the next three hours of the
        /// forecast (a 30% chance, or rain, showers, storms or snow in its words) or on the radar within 50 km.
        /// </summary>
        internal static bool ShowRadar(WeatherMap m, HourPoint[] hourly, DateTime now, double lat, double lon)
        {
            if (m == null) return false;
            if (m.Sat.Length == 0) return m.Radar.Length > 0;
            if (m.Radar.Length == 0) return false;
            foreach (HourPoint h in CurtainPainter.NextHours(hourly, now, 3))
            {
                if (h.Start >= now.AddHours(3)) break;   // a gap in the forecast: only the next three hours count
                if (h.Pop >= RainChance) return true;
                string sym = CurtainPainter.Symbol(h.Text, false);
                if (sym == "rain" || sym == "storm" || sym == "snow") return true;
            }
            return EchoesNear(m.Radar[m.Radar.Length - 1], m.Box, lat, lon, NearKm, RainDbz);
        }

        /// <summary>Whether the radar shows at least light rain within km of a place.</summary>
        internal static bool EchoesNear(MapFrame f, GeoBox box, double lat, double lon, double km, int dbz)
        {
            if (f == null || f.Values == null) return false;
            if (double.IsNaN(lat) || double.IsNaN(lon)) { lat = (box.North + box.South) / 2; lon = (box.West + box.East) / 2; }
            double kmLon = KmPerDegree * Math.Cos(lat * Math.PI / 180);
            int hits = 0;
            for (int y = 0; y < f.H; y++)
            {
                double dy = (box.North - (y + 0.5) * (box.North - box.South) / f.H - lat) * KmPerDegree;
                if (Math.Abs(dy) > km) continue;
                for (int x = 0; x < f.W; x++)
                {
                    if (f.Values[y * f.W + x] < dbz) continue;
                    double dx = (box.West + (x + 0.5) * (box.East - box.West) / f.W - lon) * kmLon;
                    if (dx * dx + dy * dy <= km * km && ++hits >= 3) return true;   // a lone pixel is not rain
                }
            }
            return false;
        }

        /// <summary>
        /// The part of the box a w x h map shows: ViewKm tall (less if the box is too small for the shape),
        /// home halfway down and on the golden section across, so φ of the width lies to the west, where
        /// the weather mostly comes from. Slid inward where home is too near the box's edge.
        /// </summary>
        internal static GeoBox Crop(GeoBox box, double lat, double lon, int w, int h)
        {
            double midLat = (box.North + box.South) / 2;
            double kmLon = KmPerDegree * Math.Cos(midLat * Math.PI / 180);
            double aspect = Math.Max(1, w) / (double)Math.Max(1, h);
            double hk = ViewKm, wk = hk * aspect;
            double maxW = box.KmWide * 0.999, maxH = box.KmTall * 0.999;
            if (wk > maxW) { wk = maxW; hk = wk / aspect; }
            if (hk > maxH) { hk = maxH; wk = hk * aspect; }
            double dLat = hk / KmPerDegree, dLon = wk / kmLon;
            double cLat = double.IsNaN(lat) ? midLat : lat, cLon = double.IsNaN(lon) ? (box.West + box.East) / 2 : lon;
            double westShare = double.IsNaN(lon) ? 0.5 : 1 / CurtainPainter.Phi;
            double s = Math.Max(box.South, Math.Min(box.North - dLat, cLat - dLat / 2));
            double we = Math.Max(box.West, Math.Min(box.East - dLon, cLon - dLon * westShare));
            return new GeoBox(s, we, s + dLat, we + dLon);
        }

        // ------------------------------------------------------------------ the replay

        public const int CycleMs = 30000;   // the past hour replays once every 30 s...
        public const int StepMs = 700;      // ...a picture every 0.7 s...
        public const int FadeMs = 250;      // ...each cross-fading into the next; the rest of the time it rests on the latest

        /// <summary>
        /// Which pictures show at a moment: picture i fading into j by fade (0..1). The replay runs at the end of
        /// each cycle and finishes on the latest picture, which then rests until the next cycle. progress runs
        /// 0..1 through the replay.
        /// </summary>
        internal static void LoopAt(int count, long ms, out int i, out int j, out float fade, out float progress, out bool playing)
        {
            i = j = Math.Max(0, count - 1);
            fade = 0; progress = 1; playing = false;
            if (count < 2) return;
            long play = FadeMs + (long)(count - 1) * StepMs;
            long p = ((ms % CycleMs) + CycleMs) % CycleMs;
            long start = CycleMs - play;
            if (p < start) return;
            playing = true;
            long q = p - start;
            if (q < FadeMs)
            {
                i = count - 1; j = 0; fade = q / (float)FadeMs; progress = 0;
                return;
            }
            q -= FadeMs;
            int k = (int)(q / StepMs);
            if (k >= count - 1) { playing = false; return; }
            long r = q - (long)k * StepMs, hold = StepMs - FadeMs;
            i = k; j = k + 1;
            fade = r < hold ? 0 : (r - hold) / (float)FadeMs;
            progress = (k + fade) / (count - 1);
        }

        // ------------------------------------------------------------------ the sample (previews and tests)

        private static WeatherMap sample;
        private static readonly object sampleGate = new object();

        /// <summary>
        /// A made-up map around a made-up place for previews and the public screenshots: a coast, a river and
        /// a lake, a line of showers moving in from the west, a fog bank on the coast. Not anywhere real.
        /// </summary>
        internal static WeatherMap Sample(double lat, double lon, DateTime nowUtc)
        {
            lock (sampleGate)
            {
                if (sample != null && sample.Radar.Length > 0 && sample.Radar[sample.Radar.Length - 1].Utc == Floor(nowUtc, 4)) return sample;
                sample = MakeSample(lat, lon, nowUtc);
                return sample;
            }
        }

        private static DateTime Floor(DateTime t, int minutes)
        {
            long step = TimeSpan.FromMinutes(minutes).Ticks;
            return new DateTime(t.Ticks - t.Ticks % step, DateTimeKind.Utc);
        }

        private static WeatherMap MakeSample(double lat, double lon, DateTime nowUtc)
        {
            double cLat = Math.Round(lat, 1), cLon = Math.Round(lon, 1);
            WeatherMap m = new WeatherMap();
            m.Box = new GeoBox(cLat - 0.6, cLon - 1.5, cLat + 0.6, cLon + 1.5);
            m.Credit = "SAMPLE DATA";
            m.BaseId = "sample";
            double kmLon = KmPerDegree * Math.Cos(cLat * Math.PI / 180);
            double boxKmW = 3.0 * kmLon, boxKmH = 1.2 * KmPerDegree;
            Func<double, double, double> homeX = (xn, yn) => (xn - (lon - m.Box.West) / 3.0) * boxKmW;   // km east of home
            Func<double, double, double> homeY = (xn, yn) => ((m.Box.North - lat) / 1.2 - yn) * boxKmH;  // km north of home

            // Land and water.
            int bw = 900, bh = 360;
            m.BaseW = bw; m.BaseH = bh;
            m.Water = new byte[bw * bh];
            for (int y = 0; y < bh; y++)
            {
                double yn = (y + 0.5) / bh;
                double coast = 0.23 + 0.022 * Math.Sin(yn * 5.3 + 0.4) + 0.011 * Math.Sin(yn * 13.7 + 1.1);
                for (int x = 0; x < bw; x++)
                {
                    double xn = (x + 0.5) / bw;
                    double d = (coast - xn) * boxKmW;   // km inside the sea (negative on land)
                    if (xn > coast - 0.01 && xn < 0.84)
                    {
                        double ry = 0.585 - 0.12 * (xn - 0.23) + 0.03 * Math.Sin(xn * 9.0 + 0.3);
                        double half = 0.9 + 4.5 * Math.Exp(-(xn - 0.23) * 10);   // km: a wide mouth narrowing upstream
                        d = Math.Max(d, half - Math.Abs(yn - ry) * boxKmH);
                    }
                    double lx = (xn - 0.67) * boxKmW / 9.5, ly = (yn - 0.25) * boxKmH / 6.5;
                    double wobble = 0.22 * (Noise(xn * 40, yn * 16) - 0.5) + 0.12 * Math.Sin(Math.Atan2(ly, lx) * 3 + 0.8);
                    d = Math.Max(d, (1 + wobble - Math.Sqrt(lx * lx + ly * ly)) * 6.5);   // a lake to the north-east
                    double cov = 0.5 + d / (boxKmW / bw);
                    m.Water[y * bw + x] = (byte)(255 * Math.Max(0, Math.Min(1, cov)));
                }
            }

            // Radar: a band of rain 20-60 km west of home, a broken line of heavier showers on its leading edge
            // and lighter rain behind, moving east-north-east at about 30 km/h, with a few showers ahead of it.
            int rw = 300, rh = 120;
            List<MapFrame> radar = new List<MapFrame>();
            DateTime last = Floor(nowUtc, 4);
            for (int f = 7; f >= 0; f--)
            {
                double ago = f * 8;   // minutes
                MapFrame fr = new MapFrame { Utc = last.AddMinutes(-ago), Kind = "radar", W = rw, H = rh, Values = new byte[rw * rh] };
                double sx = -0.5 * ago, sy = -0.13 * ago;   // where the rain was then, relative to now
                for (int y = 0; y < rh; y++)
                {
                    double yn = (y + 0.5) / rh;
                    for (int x = 0; x < rw; x++)
                    {
                        double xn = (x + 0.5) / rw;
                        double ex = homeX(xn, yn) - sx, ey = homeY(xn, yn) - sy;   // km from home, in the rain's own frame
                        double edge = -24 + 0.28 * ey + 7 * Math.Sin(ey / 17.0);   // the leading edge, curving
                        double u = ex - edge;                                          // km ahead of (+) or behind (-) it
                        double n = 0.55 * Noise(ex / 7.0, ey / 7.0) + 0.3 * Noise(ex / 3.1 + 17, ey / 3.1) + 0.15 * Noise(ex / 1.4, ey / 1.4 + 5);
                        double line = 35 * Math.Exp(-((u + 4) / 5.5) * ((u + 4) / 5.5));
                        double trail = u < 0 ? 24 * Smooth01((u + 55) / 25.0) * (1 - Smooth01((u + 3) / 6.0)) : 0;
                        double v = Math.Max(line, trail) * (0.45 + 1.05 * n) - 6;
                        double ahead = u > 6 && u < 40 ? 22 * Math.Max(0, Noise(ex / 4.0 + 40, ey / 4.0) - 0.74) / 0.26 : 0;   // stray showers
                        v = Math.Max(v, ahead > 0 ? 14 + ahead : 0);
                        fr.Values[y * rw + x] = (byte)(v >= 12 ? Math.Min(80, v) : 0);
                    }
                }
                radar.Add(fr);
            }
            m.Radar = radar.ToArray();

            // Satellite: fog over the sea and up the river mouth, scattered cloud inland, drifting east.
            int sw = 750, sh = 300;
            List<MapFrame> sat = new List<MapFrame>();
            for (int f = 6; f >= 0; f--)
            {
                double ago = f * 10;
                MapFrame fr = new MapFrame { Utc = Floor(nowUtc, 10).AddMinutes(-ago), Kind = "vis", W = sw, H = sh, Values = new byte[sw * sh] };
                for (int y = 0; y < sh; y++)
                {
                    double yn = (y + 0.5) / sh;
                    double coast = 0.23 + 0.022 * Math.Sin(yn * 5.3 + 0.4) + 0.011 * Math.Sin(yn * 13.7 + 1.1);
                    for (int x = 0; x < sw; x++)
                    {
                        double xn = (x + 0.5) / sw;
                        double kmE = (xn - coast) * boxKmW;   // km inland
                        double reach = 6 + 3 * Math.Sin(yn * 7 + 1) - 0.08 * ago + 8 * (Noise(xn * 30, yn * 12 + 3) - 0.5);
                        double ry = 0.585 - 0.12 * (xn - 0.23) + 0.03 * Math.Sin(xn * 9.0 + 0.3);
                        if (Math.Abs(yn - ry) * boxKmH < 9) reach += 16 * Math.Exp(-Math.Abs(yn - ry) * boxKmH / 4);
                        double seaward = 1 / (1 + Math.Exp(-(kmE + 34 + 12 * (Noise(xn * 9, yn * 5) - 0.5)) / 5.0));   // the bank's outer edge
                        double fog = seaward / (1 + Math.Exp((kmE - reach) / 3.5));
                        fog *= 0.62 + 0.28 * Noise(xn * 22 + 0.006 * ago, yn * 9) + 0.10 * Noise(xn * 70, yn * 28);
                        double puffs = Noise(xn * 48 - 0.02 * ago, yn * 27 + 0.006 * ago) * 0.6 + Noise(xn * 115, yn * 65) * 0.4;   // round, not stretched
                        double cloud = Math.Max(0, (puffs - 0.66) / 0.22) * Smooth01((xn - 0.52) / 0.12);   // fair-weather cumulus over the hills inland
                        double ground = xn < coast ? 30 : 46 + 10 * Noise(xn * 40, yn * 16);
                        double v = ground + (185 - ground) * Math.Min(1, Math.Max(fog, cloud));
                        fr.Values[y * sw + x] = (byte)Math.Max(0, Math.Min(255, v));
                    }
                }
                sat.Add(fr);
            }
            m.Sat = sat.ToArray();
            m.Key = m.MakeKey();
            return m;
        }

        private static double Smooth01(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        /// <summary>Smooth value noise in 0..1 (a fixed hash, so the sample is the same every time).</summary>
        private static double Noise(double x, double y)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            double xf = x - xi, yf = y - yi;
            double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
            double a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
            return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
        }

        private static double Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (double)0xFFFFFF;
            }
        }
    }

    /// <summary>
    /// Turns the map's numbers into pictures the size of the map on screen, in the curtain's palette, and
    /// keeps them: the water and shoreline once per size and place, each weather picture once.
    ///   Water      a warm near-black, a shade lighter than the land; the shore a fine line drawn over the weather
    ///   Rain       the one data hue, from quiet to bright as it gets heavier; amber when heavy, coral when severe
    ///   Clouds     warm white, more opaque as they get thicker (fog included)
    /// </summary>
    internal static class MapArt
    {
        internal static readonly Color WaterTone = Color.FromArgb(22, 21, 20);
        internal static readonly Color ShoreTone = Color.FromArgb(92, 89, 84);
        internal static readonly Color CloudTone = Color.FromArgb(200, 196, 188);   // at most ~Secondary on screen: the clock stays the brightest thing
        internal static readonly Color RainQuiet = Color.FromArgb(74, 104, 130);
        internal static readonly Color RainBright = Color.FromArgb(146, 178, 204);

        private sealed class Entry : IDisposable
        {
            public string Key;
            public long Used;
            public Bitmap Water, Shore, Overlay;
            public string OverlayKey;
            public readonly Dictionary<string, Bitmap> Frames = new Dictionary<string, Bitmap>();

            public void Dispose()
            {
                if (Water != null) Water.Dispose();
                if (Shore != null) Shore.Dispose();
                if (Overlay != null) Overlay.Dispose();
                foreach (Bitmap b in Frames.Values) b.Dispose();
                Frames.Clear();
                Water = Shore = Overlay = null;
            }
        }

        private static readonly object gate = new object();
        private static readonly List<Entry> entries = new List<Entry>();
        private static long clock;

        private static Entry Get(WeatherMap m, GeoBox crop, int w, int h)
        {
            string key = m.BaseId + "|" + m.Box + "|" + crop + "|" + w + "x" + h;
            foreach (Entry e in entries)
            {
                if (e.Key == key) { e.Used = ++clock; return e; }
            }
            Entry n = new Entry { Key = key, Used = ++clock };
            RenderBase(m, crop, w, h, out n.Water, out n.Shore);
            entries.Add(n);
            while (entries.Count > 4)
            {
                Entry oldest = entries[0];
                foreach (Entry e in entries) if (e.Used < oldest.Used) oldest = e;
                entries.Remove(oldest);
                oldest.Dispose();
            }
            return n;
        }

        internal static Bitmap Water(WeatherMap m, GeoBox crop, int w, int h)
        {
            lock (gate) { return Get(m, crop, w, h).Water; }
        }

        internal static Bitmap Shore(WeatherMap m, GeoBox crop, int w, int h)
        {
            lock (gate) { return Get(m, crop, w, h).Shore; }
        }

        /// <summary>The map's overlay (home, legend, credit), drawn by draw when its key changes.</summary>
        internal static Bitmap Overlay(WeatherMap m, GeoBox crop, int w, int h, string key, Action<Graphics> draw)
        {
            lock (gate)
            {
                Entry e = Get(m, crop, w, h);
                if (e.Overlay != null && e.OverlayKey == key) return e.Overlay;
                if (draw == null) return null;
                Bitmap b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    draw(g);
                }
                if (e.Overlay != null) e.Overlay.Dispose();
                e.Overlay = b;
                e.OverlayKey = key;
                return b;
            }
        }

        /// <summary>A weather picture at map size, rendered now if it is not ready and render is true (else null).</summary>
        internal static Bitmap Frame(WeatherMap m, GeoBox crop, int w, int h, MapFrame f, bool render)
        {
            lock (gate)
            {
                Entry e = Get(m, crop, w, h);
                Bitmap b;
                if (e.Frames.TryGetValue(f.Id, out b)) return b;
                if (!render) return null;
                b = RenderFrame(m, crop, w, h, f);
                e.Frames[f.Id] = b;
                Prune(e, m);
                return b;
            }
        }

        /// <summary>Renders one picture that is not ready yet, so a replay never has to wait. True if it did.</summary>
        internal static bool WarmOne(WeatherMap m, GeoBox crop, int w, int h, MapFrame[] frames)
        {
            lock (gate)
            {
                Entry e = Get(m, crop, w, h);
                for (int i = frames.Length - 1; i >= 0; i--)
                {
                    if (e.Frames.ContainsKey(frames[i].Id)) continue;
                    e.Frames[frames[i].Id] = RenderFrame(m, crop, w, h, frames[i]);
                    return true;
                }
                return false;
            }
        }

        private static void Prune(Entry e, WeatherMap m)
        {
            if (e.Frames.Count <= m.Radar.Length + m.Sat.Length) return;
            HashSet<string> live = new HashSet<string>();
            foreach (MapFrame f in m.Radar) live.Add(f.Id);
            foreach (MapFrame f in m.Sat) live.Add(f.Id);
            List<string> gone = new List<string>();
            foreach (string id in e.Frames.Keys) if (!live.Contains(id)) gone.Add(id);
            foreach (string id in gone) { e.Frames[id].Dispose(); e.Frames.Remove(id); }
        }

        /// <summary>Where each column and row of the map falls in a grid that covers the whole box.</summary>
        private static void Mapping(GeoBox box, GeoBox crop, int w, int h, int gw, int gh, float sub, out float[] fx, out float[] fy)
        {
            fx = new float[w];
            fy = new float[h];
            for (int x = 0; x < w; x++)
            {
                double lon = crop.West + (x + sub) / w * (crop.East - crop.West);
                fx[x] = (float)((lon - box.West) / (box.East - box.West) * gw - 0.5);
            }
            for (int y = 0; y < h; y++)
            {
                double lat = crop.North - (y + sub) / h * (crop.North - crop.South);
                fy[y] = (float)((box.North - lat) / (box.North - box.South) * gh - 0.5);
            }
        }

        private static float Bilinear(byte[] v, int gw, int gh, float x, float y)
        {
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x > gw - 1) x = gw - 1;
            if (y > gh - 1) y = gh - 1;
            int x0 = (int)x, y0 = (int)y;
            int x1 = x0 < gw - 1 ? x0 + 1 : x0, y1 = y0 < gh - 1 ? y0 + 1 : y0;
            float tx = x - x0, ty = y - y0;
            float a = v[y0 * gw + x0], b = v[y0 * gw + x1], c = v[y1 * gw + x0], d = v[y1 * gw + x1];
            return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
        }

        private static void RenderBase(WeatherMap m, GeoBox crop, int w, int h, out Bitmap water, out Bitmap shore)
        {
            // Coverage of water per screen pixel, from four samples (the source is finer than the screen).
            float[] cov = new float[w * h];
            float[] ax, ay, bx, by;
            Mapping(m.Box, crop, w, h, m.BaseW, m.BaseH, 0.25f, out ax, out ay);
            Mapping(m.Box, crop, w, h, m.BaseW, m.BaseH, 0.75f, out bx, out by);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float s = Bilinear(m.Water, m.BaseW, m.BaseH, ax[x], ay[y]) + Bilinear(m.Water, m.BaseW, m.BaseH, bx[x], ay[y]) +
                              Bilinear(m.Water, m.BaseW, m.BaseH, ax[x], by[y]) + Bilinear(m.Water, m.BaseW, m.BaseH, bx[x], by[y]);
                    cov[y * w + x] = s / (4 * 255f);
                }
            }
            int[] wp = new int[w * h], sp = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    wp[i] = Premul(WaterTone, cov[i]);
                    float gx = cov[y * w + Math.Min(w - 1, x + 1)] - cov[y * w + Math.Max(0, x - 1)];
                    float gy = cov[Math.Min(h - 1, y + 1) * w + x] - cov[Math.Max(0, y - 1) * w + x];
                    float edge = (float)Math.Sqrt(gx * gx + gy * gy) * 0.62f;
                    sp[i] = Premul(ShoreTone, Math.Min(1f, edge));
                }
            }
            water = FromPixels(wp, w, h);
            shore = FromPixels(sp, w, h);
        }

        private static Bitmap RenderFrame(WeatherMap m, GeoBox crop, int w, int h, MapFrame f)
        {
            float[] fx, fy;
            Mapping(m.Box, crop, w, h, f.W, f.H, 0.5f, out fx, out fy);
            int[] px = new int[w * h];
            bool radar = f.Kind == "radar";
            float gain = 1f;
            if (f.Kind == "vis")
            {
                double elev = CurtainPainter.SunElevation(f.Utc, (crop.North + crop.South) / 2, (crop.West + crop.East) / 2);
                gain = VisibleGain(elev);
            }
            int[] lut = new int[256];
            for (int v = 0; v < 256; v++) lut[v] = radar ? RadarArgb(v) : CloudArgb(f.Kind, v * gain);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = Bilinear(f.Values, f.W, f.H, fx[x], fy[y]);
                    px[y * w + x] = radar ? RadarArgb(v) : lut[(int)(v + 0.5f)];
                }
            }
            return FromPixels(px, w, h);
        }

        /// <summary>Evens out the visible picture through the day: a low sun lights clouds dimly.</summary>
        internal static float VisibleGain(double sunElevation)
        {
            double s = Math.Sin(Math.Max(1, sunElevation) * Math.PI / 180);
            return (float)Math.Max(0.75, Math.Min(1.8, Math.Sqrt(0.5 / Math.Max(0.17, s))));
        }

        /// <summary>A radar value (dBZ) as a premultiplied colour: nothing below drizzle, soft edges, then the rain ramp.</summary>
        internal static int RadarArgb(float dbz)
        {
            if (dbz < 14f) return 0;
            float a = Smooth((dbz - 14f) / 6f);
            Color c;
            if (dbz < 40f)
            {
                float t = Clamp01((dbz - 18f) / 22f);
                c = Lerp(RainQuiet, RainBright, t);
                a *= 0.72f + 0.28f * t;
            }
            else if (dbz < 50f) c = Lerp(RainBright, CurtainPainter.Warn, Smooth((dbz - 40f) / 6f));
            else c = Lerp(CurtainPainter.Warn, CurtainPainter.Wrong, Smooth((dbz - 50f) / 6f));
            return Premul(c, a);
        }

        /// <summary>A satellite value as a premultiplied colour: clear ground transparent, cloud and fog warm white.</summary>
        internal static int CloudArgb(string kind, float v)
        {
            float a = kind == "ir" ? Smooth((v - 122f) / 66f) : Smooth((v - 62f) / 118f);
            return Premul(CloudTone, a * 0.84f);
        }

        // ------------------------------------------------------------------ pixels

        internal static int[] GetPixels(Bitmap b)
        {
            Rectangle r = new Rectangle(0, 0, b.Width, b.Height);
            BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] px = new int[b.Width * b.Height];
                for (int y = 0; y < b.Height; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), px, y * b.Width, b.Width);
                return px;
            }
            finally
            {
                b.UnlockBits(d);
            }
        }

        internal static Bitmap FromPixels(int[] px, int w, int h)
        {
            Bitmap b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, IntPtr.Add(d.Scan0, y * d.Stride), w);
            }
            finally
            {
                b.UnlockBits(d);
            }
            return b;
        }

        private static int Premul(Color c, float a)
        {
            a = Clamp01(a);
            if (a <= 0.002f) return 0;
            int A = (int)(a * 255 + 0.5f);
            int R = (int)(c.R * a + 0.5f), G = (int)(c.G * a + 0.5f), B = (int)(c.B * a + 0.5f);
            return (A << 24) | (R << 16) | (G << 8) | B;
        }

        private static Color Lerp(Color a, Color b, float t)
        {
            t = Clamp01(t);
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t + 0.5f), (int)(a.G + (b.G - a.G) * t + 0.5f), (int)(a.B + (b.B - a.B) * t + 0.5f));
        }

        private static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        private static float Smooth(float t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
    }
}
