using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    /// <summary>One weather station's latest report (an airport METAR): where it is, its temperature and wind.</summary>
    internal sealed class MapStation
    {
        public string Id = "";
        public double Lat, Lon;
        public int TempF = int.MinValue;   // int.MinValue = not reported
        public int WindFrom = -1;          // degrees the wind blows from; -1 = variable or not reported
        public int WindMph = -1;           // -1 = not reported
        public int GustMph = -1;
        public DateTime Utc;
    }

    /// <summary>An active National Weather Service alert that reaches the map: what, how serious, until when, where.</summary>
    internal sealed class MapAlert
    {
        public string Event = "";
        public int Level;                 // 2 = warning, 1 = watch or advisory
        public DateTime EndsUtc;          // DateTime.MinValue = not given
        public readonly List<PointF[]> Rings = new List<PointF[]>();   // outlines; X = longitude, Y = latitude
    }

    /// <summary>A recent earthquake from the USGS feed.</summary>
    internal sealed class MapQuake
    {
        public DateTime Utc;
        public double Lat, Lon, Mag;
    }

    /// <summary>A station chosen for the map, where it falls in map pixels, and the box its label takes.</summary>
    internal sealed class PlacedStation
    {
        public MapStation Station;
        public float X, Y;
        public RectangleF Box;
    }

    /// <summary>The nearest NOAA tide station and its predicted highs and lows.</summary>
    internal sealed class MapTide
    {
        public string Id = "";
        public double Lat, Lon;
        public DateTime[] Times = new DateTime[0];   // UTC, oldest first
        public bool[] High = new bool[0];
        public float[] Feet = new float[0];

        /// <summary>
        /// The tide's height at a moment, by the usual cosine curve between the high and low on either side of
        /// it (good to a few inches for a picture). NaN outside the predictions.
        /// </summary>
        public double HeightAt(DateTime utc)
        {
            for (int i = 0; i + 1 < Times.Length; i++)
            {
                if (utc < Times[i] || utc > Times[i + 1]) continue;
                double span = (Times[i + 1] - Times[i]).TotalSeconds;
                double f = span > 0 ? (utc - Times[i]).TotalSeconds / span : 0;
                return (Feet[i] + Feet[i + 1]) / 2.0 + (Feet[i] - Feet[i + 1]) / 2.0 * Math.Cos(Math.PI * f);
            }
            return double.NaN;
        }

        /// <summary>The first high or low after a moment, or -1.</summary>
        public int NextEvent(DateTime utc)
        {
            for (int i = 0; i < Times.Length; i++) if (Times[i] > utc) return i;
            return -1;
        }
    }

    /// <summary>One picture of the weather over the map's box, as numbers rather than colours.</summary>
    internal sealed class MapFrame
    {
        public DateTime Utc;
        public string Kind = "";   // "radar": dBZ (0 = nothing); "vis": visible brightness 0-255; "ir": infrared, colder = higher, 0-255;
                                   // "fog": the chance of fog or low cloud, percent
        public int W, H;
        public byte[] Values;      // row by row, north at the top
        public GeoBox Box;         // the area it covers when not the map's own (the fog's web-map tiles); empty = the map's
        public bool Mercator;      // rows spaced as on a web map (the fog's tiles), not evenly by latitude

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
        public const double ViewKm = 135;         // how much of the area the map shows, top to bottom
        public const double SouthShare = 0.3;     // how much of it lies south of home: 7/10 of the map is north of it
        public const double StaleHours = 3;       // older pictures are never shown
        public const int RainDbz = 20;            // light rain on the radar
        public const int RainChance = 30;         // % chance in the next three hours that makes the map show radar
        public const double NearKm = 50;          // rain on the radar this close to home does too

        public GeoBox Box;
        public int BaseW, BaseH;
        public byte[] Water = new byte[0];        // 0 = land ... 255 = water, over Box
        public byte[] Marsh = new byte[0];        // 0 ... 255 = marsh, on the base grid (empty = none)
        public byte[] Streams = new byte[0];      // creeks, 0 ... 255 by size, on the base grid (empty = none)
        public byte[] YearRound = new byte[0];    // the part of Streams that flows all year, the same way
        public string BaseId = "";
        public MapFrame[] Radar = new MapFrame[0];   // oldest first
        public MapFrame[] Sat = new MapFrame[0];     // oldest first, all one kind
        // The chance of fog (2026-11-23, at the user's request: fog under the radar, "if it's pixelated smooth it
        // out"): NOAA's GOES-West fog and low stratus product (IFR probability) from SSEC RealEarth, oldest first.
        // Shown in either mode, as a soft veil under the radar or the clouds, at the moments the loop shows.
        public MapFrame[] Fog = new MapFrame[0];
        public const double FogStaleHours = 2;    // older fog is never shown (the product sometimes lags for hours)
        public MapStation[] Stations = new MapStation[0];
        public MapTide Tide;                      // null = no tide station near
        public MapAlert[] Alerts = new MapAlert[0];  // active alerts that reach the map, by number in map.txt
        public MapQuake[] Quakes = new MapQuake[0];  // the last QuakeDays of earthquakes, nearby
        public const double QuakeDays = 3;
        public int DemW, DemH;
        public float[] Elevation;                 // metres over Box, for the relief (null = none)
        public string DemId = "";
        public string Credit = "";
        public string Key = "";                   // changes whenever anything shown changes

        // The close-up under the map (2026-11-21, at the user's request: their own town, to see which
        // neighbourhoods the rain reaches): a map of its own ground (finer relief, water, creeks, streets) that
        // plays this map's weather pictures, so the two always show the same moment. Null when there is none.
        public WeatherMap Detail;
        public GeoBox FrameBox;                   // the box the weather pictures cover: Box, or the big map's for the close-up
        public byte[] Roads = new byte[0];        // the bigger roads (close-up only), 0 ... 255 by size, on the base grid
        public byte[] Streets = new byte[0];      // local streets, 0 ... 255, the same way
        // the close-up's height: 11.5 km at first ("too zoomed out, I'd want at least 2x or more"), then 4.6 ("might be
        // a little too zoomed in now, don't back too far out just a bit"), 5.5, and 5.75 (twice as close as the first,
        // the user's own estimate); now 7.5, its south edge where 5.75 put it and the rest to the north (the user: more
        // of the town to the north, "keep the lower bound the same")
        public const double DetailViewKm = 7.5;
        public const double DetailSouthKm = 2.875;  // how far the close-up reaches south of home; the rest of it lies north
        public const double DetailWestShare = 0.7; // home 7/10 of the way across: the larger part of the view west

        public const double StationStaleHours = 2;   // older station reports are never shown
        public const int WindyMph = 10;               // wind is labelled from this speed up
        public const int MaxStations = 7;

        // ------------------------------------------------------------------ the index file

        /// <summary>
        /// What map.txt lists. Lines: box S W N E | base FILE | dem Z X0 Y0 X1 Y1 (elevation tiles
        /// dem-Z-X-Y.png) | radar UTC FILE | sat vis|ir UTC FILE | fogtiles Z X0 Y0 X1 Y1 (the web-map tiles each
        /// fog picture is stitched from) | fog UTC FILE | station ID LAT LON TEMP_F WIND_FROM WIND_MPH
        /// GUST_MPH UTC ("-" for a value not reported) | tidestation ID LAT LON | tide UTC H|L FEET | credit TEXT.
        /// </summary>
        internal sealed class Index
        {
            public GeoBox Box;
            public bool HasBox;
            public string Base;
            public string Water;      // the rivers, canals and lakes, on the base map's grid (optional)
            public string WaterFine;  // the same water from the high-resolution hydrography, added to it (optional)
            public string Marsh;      // swamps, marshes and seasonal wetlands, the same way (optional)
            public string Streams;    // creeks, their size in the red channel, the same way (optional)
            public string Roads;      // streets in the green channel, bigger roads in the red one (the close-up's)
            public Index Detail;      // the close-up's own lines ("detail box ...", "detail base ...", ...)
            public string Credit = "";
            public readonly List<KeyValuePair<DateTime, string>> Radar = new List<KeyValuePair<DateTime, string>>();
            public readonly List<KeyValuePair<DateTime, string>> Sat = new List<KeyValuePair<DateTime, string>>();
            public readonly List<KeyValuePair<DateTime, string>> Fog = new List<KeyValuePair<DateTime, string>>();
            public readonly List<MapStation> Stations = new List<MapStation>();
            public string SatKind = "";
            public int DemZoom = -1, DemX0, DemY0, DemX1, DemY1;
            public int FogZoom = -1, FogX0, FogY0, FogX1, FogY1;
            public MapTide Tide;
            public readonly SortedDictionary<int, MapAlert> Alerts = new SortedDictionary<int, MapAlert>();
            public readonly List<MapQuake> Quakes = new List<MapQuake>();
            private readonly List<KeyValuePair<DateTime, KeyValuePair<bool, float>>> tides = new List<KeyValuePair<DateTime, KeyValuePair<bool, float>>>();

            public static Index Parse(string[] lines)
            {
                Index ix = new Index();
                foreach (string raw in lines ?? new string[0]) ix.Add(raw, true);
                ix.Radar.Sort((a, b) => a.Key.CompareTo(b.Key));
                ix.Sat.Sort((a, b) => a.Key.CompareTo(b.Key));
                ix.Fog.Sort((a, b) => a.Key.CompareTo(b.Key));
                if (ix.Tide != null)
                {
                    ix.tides.Sort((a, b) => a.Key.CompareTo(b.Key));
                    if (ix.tides.Count >= 2)
                    {
                        ix.Tide.Times = ix.tides.ConvertAll(ev => ev.Key).ToArray();
                        ix.Tide.High = ix.tides.ConvertAll(ev => ev.Value.Key).ToArray();
                        ix.Tide.Feet = ix.tides.ConvertAll(ev => ev.Value.Value).ToArray();
                    }
                    else ix.Tide = null;
                }
                return ix;
            }

            /// <summary>One line. "detail ..." lines go to the close-up's own index (one level only).</summary>
            private void Add(string raw, bool top)
            {
                Index ix = this;
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line[0] == '#') return;
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    DateTime t;
                    switch (p[0])
                    {
                        case "detail":
                            if (top && p.Length >= 3)
                            {
                                if (ix.Detail == null) ix.Detail = new Index();
                                ix.Detail.Add(line.Substring(6), false);
                            }
                            break;
                        case "roads":
                            if (p.Length == 2 && PlainName(p[1])) ix.Roads = p[1];
                            break;
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
                        case "water":
                            if (p.Length == 2 && PlainName(p[1])) ix.Water = p[1];
                            break;
                        case "waterfine":
                            if (p.Length == 2 && PlainName(p[1])) ix.WaterFine = p[1];
                            break;
                        case "marsh":
                            if (p.Length == 2 && PlainName(p[1])) ix.Marsh = p[1];
                            break;
                        case "streams":
                            if (p.Length == 2 && PlainName(p[1])) ix.Streams = p[1];
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
                        case "fog":
                            if (p.Length == 3 && Utc(p[1], out t) && PlainName(p[2]) && ix.Fog.Count < 24) ix.Fog.Add(new KeyValuePair<DateTime, string>(t, p[2]));
                            break;
                        case "fogtiles":
                        {
                            int fz, fx0, fy0, fx1, fy1;
                            if (p.Length == 6 && Whole(p[1], 1, 12, out fz) && Whole(p[2], 0, (1 << fz) - 1, out fx0) && Whole(p[3], 0, (1 << fz) - 1, out fy0) &&
                                Whole(p[4], fx0, Math.Min((1 << fz) - 1, fx0 + 3), out fx1) && Whole(p[5], fy0, Math.Min((1 << fz) - 1, fy0 + 3), out fy1))
                            {
                                ix.FogZoom = fz; ix.FogX0 = fx0; ix.FogY0 = fy0; ix.FogX1 = fx1; ix.FogY1 = fy1;
                            }
                            break;
                        }
                        case "credit":
                            ix.Credit = line.Substring(6).Trim();
                            break;
                        case "station":
                            MapStation st = ParseStation(p);
                            if (st != null && ix.Stations.Count < 200) ix.Stations.Add(st);
                            break;
                        case "dem":
                            int z, x0, y0, x1, y1;
                            if (p.Length == 6 && Whole(p[1], 1, 14, out z) && Whole(p[2], 0, 1 << 14, out x0) && Whole(p[3], 0, 1 << 14, out y0) &&
                                Whole(p[4], x0, x0 + 15, out x1) && Whole(p[5], y0, y0 + 15, out y1))
                            {
                                ix.DemZoom = z; ix.DemX0 = x0; ix.DemY0 = y0; ix.DemX1 = x1; ix.DemY1 = y1;
                            }
                            break;
                        case "alert":
                        {
                            int id, lv;
                            DateTime ends = DateTime.MinValue;
                            if (p.Length >= 5 && Whole(p[1], 1, 999, out id) && Whole(p[2], 1, 2, out lv) && (p[3] == "-" || Utc(p[3], out ends)) &&
                                !ix.Alerts.ContainsKey(id) && ix.Alerts.Count < 50)
                            {
                                string ev = string.Join(" ", p, 4, p.Length - 4);
                                ix.Alerts[id] = new MapAlert { Event = ev.Length > 60 ? ev.Substring(0, 60) : ev, Level = lv, EndsUtc = ends };
                            }
                            break;
                        }
                        case "alertring":
                        {
                            int id;
                            MapAlert a;
                            if (p.Length >= 5 && Whole(p[1], 1, 999, out id) && ix.Alerts.TryGetValue(id, out a) && a.Rings.Count < 200)
                            {
                                List<PointF> pts = new List<PointF>();
                                for (int k = 2; k < p.Length && pts.Count < 5000; k++)
                                {
                                    string[] ll = p[k].Split(',');
                                    double la, lo;
                                    if (ll.Length == 2 && Num(ll[0], out la) && Num(ll[1], out lo) && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180)
                                        pts.Add(new PointF((float)lo, (float)la));
                                }
                                if (pts.Count >= 3) a.Rings.Add(pts.ToArray());
                            }
                            break;
                        }
                        case "quake":
                        {
                            double qa, qo, mg;
                            if (p.Length == 5 && Utc(p[1], out t) && Num(p[2], out qa) && Num(p[3], out qo) && Num(p[4], out mg) &&
                                Math.Abs(qa) <= 90 && Math.Abs(qo) <= 180 && mg >= 0 && mg < 10 && ix.Quakes.Count < 500)
                                ix.Quakes.Add(new MapQuake { Utc = t, Lat = qa, Lon = qo, Mag = mg });
                            break;
                        }
                        case "tidestation":
                            double tla, tlo;
                            if (p.Length == 4 && PlainName(p[1]) && p[1].Length <= 12 && Num(p[2], out tla) && Num(p[3], out tlo) &&
                                Math.Abs(tla) <= 90 && Math.Abs(tlo) <= 180)
                                ix.Tide = new MapTide { Id = p[1], Lat = tla, Lon = tlo };
                            break;
                        case "tide":
                            double ft;
                            if (p.Length == 4 && Utc(p[1], out t) && (p[2] == "H" || p[2] == "L") && Num(p[3], out ft) && Math.Abs(ft) < 100 && ix.tides.Count < 64)
                                ix.tides.Add(new KeyValuePair<DateTime, KeyValuePair<bool, float>>(t, new KeyValuePair<bool, float>(p[2] == "H", (float)ft)));
                            break;
                    }
                }
            }

            /// <summary>station ID LAT LON TEMP_F WIND_FROM WIND_MPH GUST_MPH UTC; null when malformed.</summary>
            private static MapStation ParseStation(string[] p)
            {
                double lat, lon;
                DateTime t;
                if (p.Length != 9 || p[1].Length < 3 || p[1].Length > 6 || !Num(p[2], out lat) || !Num(p[3], out lon) ||
                    Math.Abs(lat) > 90 || Math.Abs(lon) > 180 || !Utc(p[8], out t)) return null;
                foreach (char ch in p[1]) if (!char.IsLetterOrDigit(ch)) return null;
                MapStation s = new MapStation { Id = p[1], Lat = lat, Lon = lon, Utc = t };
                int v;
                if (Whole(p[4], -80, 140, out v)) s.TempF = v;
                if (Whole(p[5], 0, 360, out v)) s.WindFrom = v % 360;
                if (Whole(p[6], 0, 250, out v)) s.WindMph = v;
                if (Whole(p[7], 0, 300, out v)) s.GustMph = v;
                return s;
            }

            private static bool Whole(string s, int min, int max, out int v)
            {
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;
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
                m.FrameBox = ix.Box;
                m.Credit = ix.Credit;
                if (!LoadGround(m, dir, ix)) return null;
                int[] px;
                int w, h;

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
                List<MapFrame> fog = new List<MapFrame>();
                if (ix.FogZoom > 0)
                {
                    GeoBox tiles = TileBox(ix.FogZoom, ix.FogX0, ix.FogY0, ix.FogX1, ix.FogY1);
                    foreach (KeyValuePair<DateTime, string> e in ix.Fog)
                    {
                        if ((utcNow - e.Key).TotalHours > FogStaleHours || e.Key > utcNow.AddMinutes(10)) continue;
                        if (!ReadPixels(Path.Combine(dir, e.Value), out px, out w, out h)) continue;
                        if (w != 256 * (ix.FogX1 - ix.FogX0 + 1) || h != 256 * (ix.FogY1 - ix.FogY0 + 1)) continue;   // not the tiles it says
                        fog.Add(new MapFrame { Utc = e.Key, Kind = "fog", W = w, H = h, Values = FogFrom(px), Box = tiles, Mercator = true });
                    }
                }
                m.Fog = fog.ToArray();
                m.Stations = ix.Stations.FindAll(st => (utcNow - st.Utc).TotalHours <= StationStaleHours).ToArray();
                m.Tide = ix.Tide;
                List<MapAlert> alerts = new List<MapAlert>();
                foreach (MapAlert a in ix.Alerts.Values)
                    if (a.Rings.Count > 0 && (a.EndsUtc == DateTime.MinValue || a.EndsUtc > utcNow)) alerts.Add(a);   // an ended one is gone
                m.Alerts = alerts.ToArray();
                m.Quakes = ix.Quakes.FindAll(q => q.Utc <= utcNow.AddMinutes(10) && (utcNow - q.Utc).TotalDays <= QuakeDays).ToArray();
                if (ix.DemZoom > 0) LoadDem(m, dir, ix);
                // The close-up: its own ground, this map's pictures. Without it (or with a broken one) the map is
                // as it was.
                Index dx = ix.Detail;
                if (dx != null && dx.HasBox && dx.Base != null && dx.Box.KmTall < 200)
                {
                    try
                    {
                        WeatherMap d = DetailGround(dir, dx);
                        if (d != null)
                        {
                            d.FrameBox = m.Box;
                            d.Radar = m.Radar;
                            d.Sat = m.Sat;
                            d.Fog = m.Fog;
                            d.Key = d.MakeKey();
                            m.Detail = d;
                        }
                    }
                    catch { }
                }
                m.Key = m.MakeKey();
                return m;
            }
            catch
            {
                return null;
            }
        }

        // The close-up's ground (about 3200 x 1600 cells and 70 elevation tiles) changes only with the place, while
        // map.txt changes every few minutes with the weather: it is kept from the last load while its files are
        // the same, so a refresh decodes only the new pictures. Never changed once built, so maps share it.
        private static readonly object groundGate = new object();
        private static string groundKey;
        private static WeatherMap ground;

        /// <summary>A fresh copy of the close-up's ground (the cached one while its files are unchanged), or null.</summary>
        private static WeatherMap DetailGround(string dir, Index dx)
        {
            StringBuilder key = new StringBuilder(Path.GetFullPath(dir)).Append('|').Append(dx.Box).Append('|');
            key.Append(dx.DemZoom).Append(' ').Append(dx.DemX0).Append(' ').Append(dx.DemY0).Append(' ').Append(dx.DemX1).Append(' ').Append(dx.DemY1);
            foreach (string name in new[] { dx.Base, dx.Water, dx.WaterFine, dx.Marsh, dx.Streams, dx.Roads })
            {
                FileInfo fi = name != null ? new FileInfo(Path.Combine(dir, name)) : null;
                key.Append('|').Append(name ?? "-");
                if (fi != null && fi.Exists) key.Append('@').Append(fi.LastWriteTimeUtc.Ticks).Append('/').Append(fi.Length);
            }
            string k = key.ToString();
            lock (groundGate)
            {
                if (ground != null && groundKey == k) return (WeatherMap)ground.MemberwiseClone();
            }
            WeatherMap d = new WeatherMap { Box = dx.Box };
            if (!LoadGround(d, dir, dx)) return null;
            if (dx.DemZoom > 0) LoadDem(d, dir, dx);
            lock (groundGate)
            {
                groundKey = k;
                ground = d;
            }
            return (WeatherMap)d.MemberwiseClone();
        }

        /// <summary>
        /// The ground of a map: the land and water map's water, the rivers, canals and lakes added to it (a river
        /// delta's channels and the reservoirs in the hills come from there), the high-resolution water added the
        /// same way (2026-11-21: reservoirs and sloughs the 1:100,000 data lacks), so either source only ever
        /// adds water; marsh and creeks; and for the close-up the streets. Each optional part left out keeps the
        /// map as it was. False when the land and water map itself does not read.
        /// </summary>
        private static bool LoadGround(WeatherMap m, string dir, Index ix)
        {
            int[] px;
            int w, h;
            if (!ReadPixels(Path.Combine(dir, ix.Base), out px, out w, out h)) return false;
            m.BaseW = w; m.BaseH = h;
            m.Water = WaterFrom(px);
            m.BaseId = ix.Base + "@" + File.GetLastWriteTimeUtc(Path.Combine(dir, ix.Base)).Ticks.ToString(CultureInfo.InvariantCulture);
            Func<string, int[]> read = name =>
            {
                int[] p;
                int pw, ph;
                if (name == null || !ReadPixels(Path.Combine(dir, name), out p, out pw, out ph)) return null;
                if (pw != w || ph != h) p = Resample(p, pw, ph, w, h);
                m.BaseId += "|" + name + "@" + File.GetLastWriteTimeUtc(Path.Combine(dir, name)).Ticks.ToString(CultureInfo.InvariantCulture);
                return p;
            };
            int[] q;
            if ((q = read(ix.Water)) != null) MergeWater(m.Water, w, h, q, w, h);
            if ((q = read(ix.WaterFine)) != null) MergeWater(m.Water, w, h, q, w, h);
            if ((q = read(ix.Marsh)) != null) m.Marsh = MaskFrom(q, w, h, w, h, false);
            if ((q = read(ix.Streams)) != null)
            {
                m.Streams = MaskFrom(q, w, h, w, h, true);
                m.YearRound = YearRoundFrom(q, w, h, w, h);
            }
            if ((q = read(ix.Roads)) != null)
            {
                m.Roads = ChannelMask(q, 16);
                m.Streets = ChannelMask(q, 8);
            }
            return true;
        }

        /// <summary>A mask picture read at another grid's cells (nearest), as MaskFrom and MergeWater read them.</summary>
        private static int[] Resample(int[] px, int pw, int ph, int w, int h)
        {
            int[] o = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(ph - 1, (int)((y + 0.5) * ph / h));
                for (int x = 0; x < w; x++) o[y * w + x] = px[sy * pw + Math.Min(pw - 1, (int)((x + 0.5) * pw / w))];
            }
            return o;
        }

        /// <summary>One colour channel of a mask (shift 16 red, 8 green), scaled by its opacity.</summary>
        internal static byte[] ChannelMask(int[] px, int shift)
        {
            byte[] v = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) v[i] = (byte)(((px[i] >> 24) & 255) * ((px[i] >> shift) & 255) / 255);
            return v;
        }

        // ------------------------------------------------------------------ elevation

        /// <summary>
        /// Stitches the elevation tiles (AWS Terrain Tiles, "terrarium" PNGs in Web Mercator) into a grid over the
        /// box at about the tiles' own resolution (0.00125 degree a cell from zoom 10, 0.0025 below). Leaves
        /// Elevation null when a tile is missing: no relief rather than a hole.
        /// </summary>
        private static void LoadDem(WeatherMap m, string dir, Index ix)
        {
            int z = ix.DemZoom, nx = ix.DemX1 - ix.DemX0 + 1, ny = ix.DemY1 - ix.DemY0 + 1;
            float[][] tiles = new float[nx * ny][];
            for (int ty = 0; ty < ny; ty++)
            {
                for (int tx = 0; tx < nx; tx++)
                {
                    int[] px;
                    int w, h;
                    string name = string.Format(CultureInfo.InvariantCulture, "dem-{0}-{1}-{2}.png", z, ix.DemX0 + tx, ix.DemY0 + ty);
                    if (!ReadPixels(Path.Combine(dir, name), out px, out w, out h) || w != 256 || h != 256) return;
                    tiles[ty * nx + tx] = Terrarium(px);
                }
            }
            int gw, gh;
            m.Elevation = StitchDem(tiles, z, ix.DemX0, ix.DemY0, nx, ny, m.Box, out gw, out gh);
            m.DemW = gw;
            m.DemH = gh;
            m.DemId = string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}/{3}/{4}", z, ix.DemX0, ix.DemY0, ix.DemX1, ix.DemY1);
        }

        /// <summary>Terrarium encoding: metres = R * 256 + G + B / 256 - 32768.</summary>
        internal static float[] Terrarium(int[] px)
        {
            float[] e = new float[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i];
                e[i] = ((c >> 16) & 255) * 256f + ((c >> 8) & 255) + (c & 255) / 256f - 32768f;
            }
            return e;
        }

        /// <summary>Where a place falls in the whole world's Web Mercator pixels at a zoom (256-pixel tiles).</summary>
        internal static void Mercator(double lat, double lon, int z, out double gx, out double gy)
        {
            double n = 256.0 * (1 << z);
            gx = (lon + 180.0) / 360.0 * n;
            double r = Math.Max(-85.0511, Math.Min(85.0511, lat)) * Math.PI / 180;
            gy = (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n;
        }

        internal static float[] StitchDem(float[][] tiles, int z, int x0, int y0, int nx, int ny, GeoBox box, out int w, out int h)
        {
            double cell = z >= 13 ? 0.00015 : z >= 12 ? 0.0003 : z >= 10 ? 0.00125 : 0.0025;   // about the tiles' own resolution (13: the close-up)
            w = Math.Max(2, Math.Min(4096, (int)Math.Round((box.East - box.West) / cell)));
            h = Math.Max(2, Math.Min(2048, (int)Math.Round((box.North - box.South) / cell)));
            double[] gxs = new double[w], gys = new double[h];
            double unused;
            for (int x = 0; x < w; x++)
            {
                Mercator(0, box.West + (x + 0.5) * (box.East - box.West) / w, z, out gxs[x], out unused);
                gxs[x] -= x0 * 256.0 + 0.5;   // tile pixel centres sit on the half
            }
            for (int y = 0; y < h; y++)
            {
                Mercator(box.North - (y + 0.5) * (box.North - box.South) / h, 0, z, out unused, out gys[y]);
                gys[y] -= y0 * 256.0 + 0.5;
            }
            int W = nx * 256, H = ny * 256;
            float[] e = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                double fy = Math.Max(0, Math.Min(H - 1, gys[y]));
                int ya = (int)fy, yb = Math.Min(H - 1, ya + 1);
                float ty = (float)(fy - ya);
                for (int x = 0; x < w; x++)
                {
                    double fx = Math.Max(0, Math.Min(W - 1, gxs[x]));
                    int xa = (int)fx, xb = Math.Min(W - 1, xa + 1);
                    float tx = (float)(fx - xa);
                    float a = Sample(tiles, nx, xa, ya), b = Sample(tiles, nx, xb, ya), c = Sample(tiles, nx, xa, yb), d = Sample(tiles, nx, xb, yb);
                    e[y * w + x] = a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
                }
            }
            return e;
        }

        private static float Sample(float[][] tiles, int nx, int gx, int gy)
        {
            return tiles[(gy >> 8) * nx + (gx >> 8)][(gy & 255) * 256 + (gx & 255)];
        }

        // ------------------------------------------------------------------ station labels

        internal static bool Windy(MapStation s) { return s.WindMph >= WindyMph; }

        /// <summary>"14 mph", or "14–20 mph" when it gusts well above that (as the forecast writes wind).</summary>
        internal static string WindText(MapStation s)
        {
            if (s.GustMph >= s.WindMph + 5) return s.WindMph.ToString(CultureInfo.InvariantCulture) + "–" + s.GustMph.ToString(CultureInfo.InvariantCulture) + " mph";
            return s.WindMph.ToString(CultureInfo.InvariantCulture) + " mph";
        }

        /// <summary>
        /// The stations to label on a w x h map of crop: inside it and clear of its edges (edge), its bottom band
        /// (bottom) and the boxes already taken. The first is the one nearest home; each next one is the station
        /// farthest from those already chosen, so the labels spread across coast, bay and inland, never closer
        /// than spacing and never overlapping. At most max. label gives a label's box around its station.
        /// </summary>
        internal static List<PlacedStation> PickStations(MapStation[] all, GeoBox crop, int w, int h, double homeLat, double homeLon,
                                                         Func<MapStation, RectangleF> label, float spacing, float edge, float bottom,
                                                         List<RectangleF> taken, int max)
        {
            List<PlacedStation> cand = new List<PlacedStation>();
            List<MapStation> sorted = new List<MapStation>(all ?? new MapStation[0]);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (MapStation s in sorted)
            {
                if (s.TempF == int.MinValue) continue;
                float x = (float)((s.Lon - crop.West) / (crop.East - crop.West) * w);
                float y = (float)((crop.North - s.Lat) / (crop.North - crop.South) * h);
                RectangleF r = label(s);
                r.Offset(x, y);
                if (r.Left < edge || r.Right > w - edge || r.Top < edge || r.Bottom > h - bottom) continue;
                bool blocked = false;
                foreach (RectangleF t in taken ?? new List<RectangleF>()) if (t.IntersectsWith(r)) { blocked = true; break; }
                if (!blocked) cand.Add(new PlacedStation { Station = s, X = x, Y = y, Box = r });
            }
            List<PlacedStation> picked = new List<PlacedStation>();
            if (cand.Count == 0 || max <= 0) return picked;

            float hx = w / 2f, hy = h / 2f;
            if (!double.IsNaN(homeLat) && !double.IsNaN(homeLon))
            {
                hx = (float)((homeLon - crop.West) / (crop.East - crop.West) * w);
                hy = (float)((crop.North - homeLat) / (crop.North - crop.South) * h);
            }
            PlacedStation first = cand[0];
            foreach (PlacedStation c in cand)
                if (Dist(c.X, c.Y, hx, hy) < Dist(first.X, first.Y, hx, hy)) first = c;
            picked.Add(first);
            cand.Remove(first);
            float gutter = spacing * 0.08f;
            while (picked.Count < max)
            {
                PlacedStation best = null;
                float bestD = -1;
                foreach (PlacedStation c in cand)
                {
                    float d = float.MaxValue;
                    bool clash = false;
                    RectangleF grown = c.Box;
                    grown.Inflate(gutter, gutter);
                    foreach (PlacedStation p in picked)
                    {
                        d = Math.Min(d, Dist(c.X, c.Y, p.X, p.Y));
                        if (grown.IntersectsWith(p.Box)) clash = true;
                    }
                    if (clash || d < spacing) continue;
                    if (d > bestD) { bestD = d; best = c; }
                }
                if (best == null) break;
                picked.Add(best);
                cand.Remove(best);
            }
            return picked;
        }

        private static float Dist(float ax, float ay, float bx, float by)
        {
            float dx = ax - bx, dy = ay - by;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>The same map without pictures that have grown too old (itself when none have).</summary>
        internal WeatherMap Fresh(DateTime utcNow)
        {
            Func<MapFrame[], MapFrame[]> keep = all => Array.FindAll(all, f => (utcNow - f.Utc).TotalHours <= StaleHours);
            MapFrame[] r = keep(Radar), s = keep(Sat);
            MapFrame[] fg = Array.FindAll(Fog, f => (utcNow - f.Utc).TotalHours <= FogStaleHours);
            MapStation[] st = Array.FindAll(Stations, x => (utcNow - x.Utc).TotalHours <= StationStaleHours);
            if (r.Length == Radar.Length && s.Length == Sat.Length && fg.Length == Fog.Length && st.Length == Stations.Length) return this;
            WeatherMap m = (WeatherMap)MemberwiseClone();
            m.Radar = r;
            m.Sat = s;
            m.Fog = fg;
            m.Stations = st;
            if (Detail != null)
            {
                WeatherMap d = (WeatherMap)Detail.MemberwiseClone();
                d.Radar = r;
                d.Sat = s;
                d.Fog = fg;
                d.Key = d.MakeKey();
                m.Detail = d;
            }
            m.Key = m.MakeKey();
            return m;
        }

        private string MakeKey()
        {
            StringBuilder b = new StringBuilder();
            b.Append(Box).Append('|').Append(BaseId).Append('|').Append(DemId);
            foreach (MapFrame f in Radar) b.Append('|').Append(f.Id);
            foreach (MapFrame f in Sat) b.Append('|').Append(f.Id);
            foreach (MapFrame f in Fog) b.Append('|').Append(f.Id);
            foreach (MapStation s in Stations)
                b.Append('|').Append(s.Id).Append(' ').Append(s.TempF).Append(' ').Append(s.WindFrom).Append(' ').Append(s.WindMph).Append(' ').Append(s.GustMph);
            if (Tide != null) b.Append("|tide ").Append(Tide.Id).Append(' ').Append(Tide.Times.Length > 0 ? Tide.Times[0].Ticks : 0);
            foreach (MapAlert a in Alerts) b.Append("|alert ").Append(a.Level).Append(' ').Append(a.EndsUtc.Ticks).Append(' ').Append(a.Rings.Count).Append(' ').Append(a.Event);
            foreach (MapQuake q in Quakes) b.Append("|quake ").Append(q.Utc.Ticks).Append(' ').Append(q.Mag.ToString("0.0", CultureInfo.InvariantCulture));
            if (Detail != null) b.Append("|detail ").Append(Detail.Box).Append(' ').Append(Detail.BaseId).Append(' ').Append(Detail.DemId);
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

        /// <summary>
        /// Adds the hydrography mask (USGS, water drawn opaque on a transparent ground; edges partly opaque) to the
        /// water: the larger of the two at each cell. The mask is read at the base grid's cells, whatever its size.
        /// </summary>
        internal static void MergeWater(byte[] water, int w, int h, int[] px, int pw, int ph)
        {
            if (water == null || px == null || w <= 0 || h <= 0 || pw <= 0 || ph <= 0 || px.Length < pw * ph) return;
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(ph - 1, (int)((y + 0.5) * ph / h));
                for (int x = 0; x < w; x++)
                {
                    int c = px[sy * pw + Math.Min(pw - 1, (int)((x + 0.5) * pw / w))];
                    int a = (c >> 24) & 255, b = c & 255, r = (c >> 16) & 255;
                    if (a > 0 && b > r && a > water[y * w + x]) water[y * w + x] = (byte)a;   // blue only: water
                }
            }
        }

        /// <summary>
        /// A mask drawn opaque on a transparent ground, read at the base grid's cells: its opacity, and with
        /// bySize the red channel scales it too (the creeks: 85 for the smallest drawn, 255 for rivers).
        /// </summary>
        internal static byte[] MaskFrom(int[] px, int pw, int ph, int w, int h, bool bySize)
        {
            byte[] mask = new byte[Math.Max(0, w * h)];
            if (px == null || w <= 0 || h <= 0 || pw <= 0 || ph <= 0 || px.Length < pw * ph) return mask;
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(ph - 1, (int)((y + 0.5) * ph / h));
                for (int x = 0; x < w; x++)
                {
                    int c = px[sy * pw + Math.Min(pw - 1, (int)((x + 0.5) * pw / w))];
                    int a = (c >> 24) & 255;
                    if (bySize) a = a * ((c >> 16) & 255) / 255;
                    mask[y * w + x] = (byte)a;
                }
            }
            return mask;
        }

        /// <summary>The creeks that flow all year: the creek mask (by size) where the green channel is full.</summary>
        internal static byte[] YearRoundFrom(int[] px, int pw, int ph, int w, int h)
        {
            byte[] all = MaskFrom(px, pw, ph, w, h, true);
            if (px == null || w <= 0 || h <= 0 || pw <= 0 || ph <= 0 || px.Length < pw * ph) return all;
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(ph - 1, (int)((y + 0.5) * ph / h));
                for (int x = 0; x < w; x++)
                {
                    int c = px[sy * pw + Math.Min(pw - 1, (int)((x + 0.5) * pw / w))];
                    all[y * w + x] = (byte)(all[y * w + x] * ((c >> 8) & 255) / 255);
                }
            }
            return all;
        }

        /// <summary>A satellite picture: grey levels (both kinds come with bright = cloud).</summary>
        internal static byte[] GreyFrom(int[] px)
        {
            byte[] v = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) v[i] = (byte)((px[i] >> 8) & 255);
            return v;
        }

        /// <summary>
        /// The area web-map tiles X0..X1, Y0..Y1 at zoom Z cover (the usual tiling: x from the date line, y from
        /// the north, rows spaced by the Mercator projection).
        /// </summary>
        internal static GeoBox TileBox(int z, int x0, int y0, int x1, int y1)
        {
            double n = Math.Pow(2, z);
            Func<int, double> lat = y => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI;
            return new GeoBox(lat(y1 + 1), x0 / n * 360 - 180, lat(y0), (x1 + 1) / n * 360 - 180);
        }

        internal static byte[] FogFrom(int[] px)
        {
            byte[] v = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) v[i] = FogPercent(px[i]);
            return v;
        }

        /// <summary>
        /// SSEC RealEarth's colours for the fog product back to the chance of fog, percent: black up to 9, a grey
        /// ramp to 25, yellow through orange to red at 75, a darkening red to 100 (read off its legend,
        /// 2026-11-23; the tiles use those colours exactly). Anything else, or a see-through pixel, is none.
        /// </summary>
        internal static byte FogPercent(int argb)
        {
            int a = (argb >> 24) & 255, r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
            if (a < 128) return 0;
            double p;
            if (r == g && g == b) p = r == 0 ? 0 : 9.0 + r * 0.0611;
            else if (r == 255 && b == 0) p = 24.2 + (255 - g) * 0.2008;
            else if (g == 0 && b == 0 && r >= 120) p = 75.4 + (255 - r) * 0.2;
            else p = 0;
            return (byte)Math.Max(0, Math.Min(100, Math.Round(p)));
        }

        /// <summary>
        /// The fog picture to show with a weather picture of the given time: the newest from up to five minutes
        /// after it, else the oldest there is (fog drifts slowly, and the product can lag the radar).
        /// </summary>
        internal static MapFrame FogAt(MapFrame[] fog, DateTime utc)
        {
            if (fog == null || fog.Length == 0) return null;
            MapFrame best = fog[0];
            foreach (MapFrame f in fog) if (f.Utc <= utc.AddMinutes(5)) best = f;
            return best;
        }

        public const int FogShowsPercent = 35;   // the header says FOG from here: the veil is plain by then (it starts at 30)

        /// <summary>Whether a fog picture has fog (FogShowsPercent or more) anywhere inside the crop.</summary>
        internal static bool FogInView(MapFrame f, GeoBox crop, GeoBox mapBox)
        {
            if (f == null || f.Values == null || f.W < 1 || f.H < 1) return false;
            GeoBox b = f.Box.IsEmpty ? mapBox : f.Box;
            if (b.IsEmpty) return false;
            Func<double, int> col = lon => (int)Math.Floor((lon - b.West) / (b.East - b.West) * f.W);
            Func<double, int> row = lat => f.Mercator
                ? (int)Math.Floor((MapArt.MercatorY(b.North) - MapArt.MercatorY(lat)) / (MapArt.MercatorY(b.North) - MapArt.MercatorY(b.South)) * f.H)
                : (int)Math.Floor((b.North - lat) / (b.North - b.South) * f.H);
            int x0 = Math.Max(0, col(crop.West)), x1 = Math.Min(f.W - 1, col(crop.East));
            int y0 = Math.Max(0, row(crop.North)), y1 = Math.Min(f.H - 1, row(crop.South));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (f.Values[y * f.W + x] >= FogShowsPercent) return true;
            return false;
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

        /// <summary>How much of a part of a w x h map (r, in map pixels, showing crop) is water: 0 (all land) to 1.</summary>
        internal static double WaterShare(WeatherMap m, GeoBox crop, int w, int h, RectangleF r)
        {
            if (m == null || m.Water.Length == 0 || m.BaseW <= 0 || m.BaseH <= 0 || w <= 0 || h <= 0) return 0;
            const int nx = 16, ny = 8;
            double sum = 0;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double lon = crop.West + (r.Left + (i + 0.5) * r.Width / nx) / w * (crop.East - crop.West);
                    double lat = crop.North - (r.Top + (j + 0.5) * r.Height / ny) / h * (crop.North - crop.South);
                    int bx = (int)((lon - m.Box.West) / (m.Box.East - m.Box.West) * m.BaseW);
                    int by = (int)((m.Box.North - lat) / (m.Box.North - m.Box.South) * m.BaseH);
                    if (bx >= 0 && by >= 0 && bx < m.BaseW && by < m.BaseH) sum += m.Water[by * m.BaseW + bx] / 255.0;
                }
            return sum / (nx * ny);
        }

        /// <summary>
        /// The part of the box a w x h map shows: ViewKm tall (less if the box is too small for the shape),
        /// home on the golden section across, so the larger part lies to the west, where the weather mostly
        /// comes from, and 7/10 of the way down, so most of the map lies to the north. Slid inward where home
        /// is too near the box's edge.
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
            double southShare = double.IsNaN(lat) ? 0.5 : SouthShare;
            double s = Math.Max(box.South, Math.Min(box.North - dLat, cLat - dLat * southShare));
            double we = Math.Max(box.West, Math.Min(box.East - dLon, cLon - dLon * westShare));
            return new GeoBox(s, we, s + dLat, we + dLon);
        }

        /// <summary>
        /// The part of the close-up's box a w x h close-up shows: DetailViewKm tall (less if the box is too small
        /// for the shape), home 7/10 of the way across, its south edge DetailSouthKm south of home (at most half
        /// the height) and the rest north; slid inward near the box's edge.
        /// </summary>
        internal static GeoBox DetailCrop(GeoBox box, double lat, double lon, int w, int h)
        {
            double midLat = (box.North + box.South) / 2;
            double kmLon = KmPerDegree * Math.Cos(midLat * Math.PI / 180);
            double aspect = Math.Max(1, w) / (double)Math.Max(1, h);
            double hk = DetailViewKm, wk = hk * aspect;
            double maxW = box.KmWide * 0.999, maxH = box.KmTall * 0.999;
            if (wk > maxW) { wk = maxW; hk = wk / aspect; }
            if (hk > maxH) { hk = maxH; wk = hk * aspect; }
            double dLat = hk / KmPerDegree, dLon = wk / kmLon;
            double cLat = double.IsNaN(lat) ? midLat : lat, cLon = double.IsNaN(lon) ? (box.West + box.East) / 2 : lon;
            double westShare = double.IsNaN(lon) ? 0.5 : DetailWestShare;
            double south = double.IsNaN(lat) ? dLat / 2 : Math.Min(dLat / 2, DetailSouthKm / KmPerDegree);
            double s = Math.Max(box.South, Math.Min(box.North - dLat, cLat - south));
            double we = Math.Max(box.West, Math.Min(box.East - dLon, cLon - dLon * westShare));
            return new GeoBox(s, we, s + dLat, we + dLon);
        }

        // ------------------------------------------------------------------ the replay

        // The past hour plays on a loop: a picture every 0.7 s, each fading gradually into the next over the whole
        // step (until 2026-11-23 each was held and then cross-faded in 250 ms; the user: "a gradual fade instead of
        // frame-by-frame animation could help"), and the latest fading back to the oldest over a step of its own as
        // each turn begins. No pause on the latest (until 2026-11-24 it was held 1.4 s and then faded back in
        // 0.45 s; the user: "the final image should have same interval as the frames"): every picture, the latest
        // too, gets one step.
        public const int StepMs = 700;

        /// <summary>One turn of the loop for count pictures: a step for each, the latest's being the fade back.</summary>
        internal static long CycleFor(int count)
        {
            return count < 2 ? 0 : (long)count * StepMs;
        }

        /// <summary>
        /// Which pictures show at a moment: picture i fading into j by fade (0..1). Each turn of the loop begins on
        /// the latest picture, fades it back to the oldest, then plays the past hour and finishes on the latest
        /// again, every step the same length (until 2026-11-17 it rested on the latest for most of a 30 s cycle,
        /// until 2026-11-24 for 1.4 s). progress runs 0..1 through the hour, and is 0 during the fade back.
        /// </summary>
        internal static void LoopAt(int count, long ms, out int i, out int j, out float fade, out float progress, out bool playing)
        {
            i = j = Math.Max(0, count - 1);
            fade = 0; progress = 1; playing = false;
            if (count < 2) return;
            long cycle = CycleFor(count);
            long p = ((ms % cycle) + cycle) % cycle;
            playing = true;
            int k = (int)(p / StepMs);
            double s = (p - (long)k * StepMs) / (double)StepMs;
            // eased, so the motion never stops yet each picture is whole for a moment as the fade passes it
            fade = (float)Smooth01(s);
            if (k == 0) { i = count - 1; j = 0; progress = 0; return; }   // the turn begins: the latest back to the oldest
            i = k - 1; j = k;
            progress = (float)((k - 1 + s) / (count - 1));
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

            // Land and water: how far inside the water a point of the box is, in km (negative on land); the
            // close-up draws its water from the same function.
            Func<double, double, double> waterKm = (xn, yn) =>
            {
                double coast = 0.23 + 0.022 * Math.Sin(yn * 5.3 + 0.4) + 0.011 * Math.Sin(yn * 13.7 + 1.1);
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
                double px = homeX(xn, yn), py = homeY(xn, yn);   // two old quarry ponds between home and the river
                d = Math.Max(d, 0.45 - Math.Sqrt((px + 1.6) * (px + 1.6) + (py + 0.7) * (py + 0.7) * 1.8));
                d = Math.Max(d, 0.3 - Math.Sqrt((px + 0.4) * (px + 0.4) * 1.5 + (py + 0.5) * (py + 0.5)));
                return d;
            };
            int bw = 900, bh = 360;
            m.BaseW = bw; m.BaseH = bh;
            m.Water = new byte[bw * bh];
            for (int y = 0; y < bh; y++)
            {
                double yn = (y + 0.5) / bh;
                for (int x = 0; x < bw; x++)
                {
                    double cov = 0.5 + waterKm((x + 0.5) / bw, yn) / (boxKmW / bw);
                    m.Water[y * bw + x] = (byte)(255 * Math.Max(0, Math.Min(1, cov)));
                }
            }

            // Marsh along the river's mouth and creeks running down to the river and the lake, drawn as the real
            // masks are, so previews show them too.
            using (Bitmap mk = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Bitmap sk = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(mk))
                using (SolidBrush green = new SolidBrush(Color.FromArgb(255, 0, 255, 0)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillClosedCurve(green, new[] { new PointF(bw * 0.235f, bh * 0.50f), new PointF(bw * 0.30f, bh * 0.49f), new PointF(bw * 0.36f, bh * 0.53f),
                                                     new PointF(bw * 0.33f, bh * 0.555f), new PointF(bw * 0.26f, bh * 0.545f) });
                    g.FillClosedCurve(green, new[] { new PointF(bw * 0.24f, bh * 0.63f), new PointF(bw * 0.31f, bh * 0.615f), new PointF(bw * 0.33f, bh * 0.66f),
                                                     new PointF(bw * 0.27f, bh * 0.69f) });
                }
                using (Graphics g = Graphics.FromImage(sk))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float[][] creeks =
                    {
                        new[] { 0.40f, 0.18f, 0.43f, 0.30f, 0.41f, 0.42f, 0.44f, 0.54f },   // down to the river from the north
                        new[] { 0.52f, 0.95f, 0.50f, 0.82f, 0.53f, 0.70f, 0.51f, 0.57f },   // from the hills in the south
                        new[] { 0.62f, 0.90f, 0.66f, 0.78f, 0.63f, 0.66f, 0.65f, 0.55f },
                        new[] { 0.80f, 0.05f, 0.76f, 0.12f, 0.73f, 0.20f, 0.70f, 0.24f },   // into the lake
                        new[] { 0.30f, 0.95f, 0.32f, 0.85f, 0.29f, 0.76f, 0.31f, 0.67f },
                        new[] { 0.72f, 0.95f, 0.75f, 0.80f, 0.72f, 0.68f, 0.74f, 0.53f },
                    };
                    for (int k = 0; k < creeks.Length; k++)
                    {
                        float[] c = creeks[k];
                        PointF[] pts = new PointF[c.Length / 2];
                        for (int j = 0; j < pts.Length; j++) pts[j] = new PointF(c[2 * j] * bw, c[2 * j + 1] * bh);
                        using (Pen pen = new Pen(Color.FromArgb(255, k % 3 == 0 ? 255 : 170, k % 3 == 0 ? 255 : 0, 255), 1f))
                            g.DrawCurve(pen, pts);
                    }
                }
                m.Marsh = MaskFrom(MapArt.GetPixels(mk), bw, bh, bw, bh, false);
                m.Streams = MaskFrom(MapArt.GetPixels(sk), bw, bh, bw, bh, true);
                m.YearRound = YearRoundFrom(MapArt.GetPixels(sk), bw, bh, bw, bh);
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
                        // and a shower that hangs over home's town all hour, as one can by the hills, waxing and
                        // waning, so the close-up has weather in every frame
                        double gx = homeX(xn, yn) + 2.2, gy = homeY(xn, yn) - 0.6;   // km from its middle, on the ground
                        double shower = (30 + 6 * Math.Sin(ago / 9.0)) * Math.Exp(-(gx * gx / 3.0 + gy * gy / 1.6));
                        if (shower > 12) v = Math.Max(v, shower);
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

            // Fog, as the real product gives it: the chance of fog in cells about 10 km across (blocks of the
            // picture's own grid), high over the sea and up the river mouth, ebbing a little through the hour.
            int gw = 240, gh = 130, block = 10;
            List<MapFrame> fogFrames = new List<MapFrame>();
            for (int f = 6; f >= 0; f--)
            {
                double ago = f * 10;
                MapFrame fr = new MapFrame { Utc = Floor(nowUtc, 10).AddMinutes(-ago), Kind = "fog", W = gw, H = gh, Values = new byte[gw * gh] };
                for (int by = 0; by < gh; by += block)
                {
                    for (int bx = 0; bx < gw; bx += block)
                    {
                        double xn = (bx + block / 2.0) / gw, yn = (by + block / 2.0) / gh;
                        double coast = 0.23 + 0.022 * Math.Sin(yn * 5.3 + 0.4) + 0.011 * Math.Sin(yn * 13.7 + 1.1);
                        double kmE = (xn - coast) * boxKmW;   // km inland
                        double ry = 0.585 - 0.12 * (xn - 0.23) + 0.03 * Math.Sin(xn * 9.0 + 0.3);
                        double reach = 8 + 0.05 * ago + 18 * Math.Exp(-Math.Abs(yn - ry) * boxKmH / 6);
                        double pct = 92 / (1 + Math.Exp((kmE - reach) / 6.0)) * (0.85 + 0.3 * (Noise(xn * 12, yn * 6 + 2) - 0.5));
                        if (kmE < -45) pct *= Math.Max(0, 1 - (-45 - kmE) / 20);   // the bank's outer edge, far out at sea
                        byte v = (byte)Math.Max(0, Math.Min(100, Math.Round(pct)));
                        for (int y = by; y < Math.Min(gh, by + block); y++)
                            for (int x = bx; x < Math.Min(gw, bx + block); x++) fr.Values[y * gw + x] = v;
                    }
                }
                fogFrames.Add(fr);
            }
            m.Fog = fogFrames.ToArray();

            // Stations: cool and windy on the coast, warmer inland, as on any summer coast.
            double[,] st =
            {
                // km east, km north (of home), F, wind from, mph, gust
                { -60, 19, 55, 300, 17, 24 }, { -61, -21, 56, 310, 21, -1 }, { -46, -7, 57, 290, 12, -1 }, { -24, 6, 59, 270, 8, -1 },
                { -6, -14, 61, 250, 6, -1 }, { 14, 18, 64, 240, 5, -1 }, { 33, -16, 68, -1, 0, -1 }, { 44, 10, 67, 230, 7, -1 },
                { 34, 30, 63, 260, 11, -1 }, { 4, -31, 65, 200, 4, -1 }, { -38, 27, 58, 280, 9, -1 },
            };
            m.Stations = new MapStation[st.GetLength(0)];
            for (int i = 0; i < m.Stations.Length; i++)
            {
                m.Stations[i] = new MapStation
                {
                    Id = "S" + (i + 1).ToString("00", CultureInfo.InvariantCulture), Lat = lat + st[i, 1] / KmPerDegree, Lon = lon + st[i, 0] / kmLon,
                    TempF = (int)st[i, 2], WindFrom = (int)st[i, 3], WindMph = (int)st[i, 4], GustMph = (int)st[i, 5], Utc = nowUtc,
                };
            }

            // The tide, at the river mouth: rising towards a high in about two hours.
            double[] hours = { -10.5, -4.2, 2.1, 8.4, 14.3, 20.7, 26.9 };
            float[] feet = { 5.9f, 0.6f, 6.3f, 2.1f, 5.7f, -0.2f, 6.6f };
            MapAlert surf = new MapAlert { Event = "High Surf Advisory", Level = 1, EndsUtc = nowUtc.AddHours(20) };
            surf.Rings.Add(new[]
            {
                new PointF((float)(cLon - 0.98), (float)(cLat + 0.55)), new PointF((float)(cLon - 0.78), (float)(cLat + 0.55)),
                new PointF((float)(cLon - 0.80), (float)(cLat + 0.05)), new PointF((float)(cLon - 0.76), (float)(cLat - 0.55)),
                new PointF((float)(cLon - 0.96), (float)(cLat - 0.55)), new PointF((float)(cLon - 0.99), (float)(cLat + 0.05)),
            });
            m.Alerts = new[] { surf };
            m.Quakes = new[]
            {
                new MapQuake { Utc = nowUtc.AddHours(-3), Lat = lat + 0.14, Lon = lon + 0.42, Mag = 3.4 },
                new MapQuake { Utc = nowUtc.AddHours(-30), Lat = lat - 1.3, Lon = lon + 0.4, Mag = 4.7 },
            };
            m.Tide = new MapTide { Id = "SAMPLE", Lat = lat - 13 / KmPerDegree, Lon = lon - 58 / kmLon };
            m.Tide.Times = Array.ConvertAll(hours, hh => nowUtc.AddHours(hh));
            m.Tide.High = Array.ConvertAll(feet, ft => ft > 3);
            m.Tide.Feet = feet;

            // Hills: a coast range cut by the river, a mountain to the south-east, rolling country between, and
            // foothills rising just east of home (the close-up's hills). The close-up uses the same function.
            Func<double, double, double> height = (xn, yn) =>
            {
                double coast = 0.23 + 0.022 * Math.Sin(yn * 5.3 + 0.4) + 0.011 * Math.Sin(yn * 13.7 + 1.1);
                double kmIn = (xn - coast) * boxKmW;
                if (kmIn < 0) return -20 + kmIn * 0.5;
                double ridge = 640 * Math.Exp(-Math.Pow((kmIn - 15) / 8.0, 2)) * (0.55 + 0.6 * Noise(xn * 14, yn * 8));
                double ex = homeX(xn, yn) - 38, ey = homeY(xn, yn) + 24;
                double mount = 1150 * Math.Exp(-(ex * ex + ey * ey) / (2 * 13.0 * 13.0)) * (0.8 + 0.4 * Noise(xn * 30, yn * 12));
                double hills = 300 * Math.Max(0, Noise(xn * 22 + 5, yn * 11) - 0.3) * Smooth01(kmIn / 20);
                double fx = homeX(xn, yn);
                double foot = 560 * Smooth01((fx - 1.5) / 4.0) * Math.Exp(-Math.Pow((fx - 8) / 7.0, 2)) * (0.65 + 0.5 * Noise(xn * 60 + 9, yn * 24));
                double ry = 0.585 - 0.12 * (xn - 0.23) + 0.03 * Math.Sin(xn * 9.0 + 0.3);
                double valley = Math.Exp(-Math.Pow((yn - ry) * boxKmH / 7.0, 2));
                return (ridge + mount + hills + foot + 20 * Smooth01(kmIn / 3)) * (1 - 0.88 * valley);
            };
            int dw = 1200, dh = 480;
            m.DemW = dw;
            m.DemH = dh;
            m.DemId = "sample";
            m.Elevation = new float[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                double yn = (y + 0.5) / dh;
                for (int x = 0; x < dw; x++) m.Elevation[y * dw + x] = (float)height((x + 0.5) / dw, yn);
            }
            m.FrameBox = m.Box;
            m.Detail = MakeSampleDetail(m, lat, lon, waterKm, height);
            m.Key = m.MakeKey();
            return m;
        }

        /// <summary>
        /// The sample's close-up: the same made-up place, 0.2 x 0.4 degrees around home (as the weather script cuts
        /// the real one: more of it west) at 0.00025 degree a cell,
        /// its river, ponds and hills from the map's own functions (with gullies only seen close in), and what
        /// only the close-up shows: a town's streets on the low ground (each neighbourhood's grid at its own
        /// angle, a few parks), two freeways (one on a bridge over the river), creeks from the
        /// foothills and marsh on the river's banks west of town. Not anywhere real.
        /// </summary>
        private static WeatherMap MakeSampleDetail(WeatherMap m, double lat, double lon, Func<double, double, double> waterKm,
                                                   Func<double, double, double> height)
        {
            double cLat = Math.Round(lat, 1), cLon = Math.Round(lon, 1);
            WeatherMap c = new WeatherMap
            {
                Box = new GeoBox(cLat - 0.1, cLon - 0.25, cLat + 0.1, cLon + 0.15), FrameBox = m.Box, Radar = m.Radar, Sat = m.Sat, Fog = m.Fog,
                BaseId = "sample-detail", DemId = "sample-detail",
            };
            int w = 1600, h = 800;
            c.BaseW = w; c.BaseH = h; c.DemW = w; c.DemH = h;
            c.Water = new byte[w * h]; c.Marsh = new byte[w * h]; c.Streams = new byte[w * h]; c.YearRound = new byte[w * h];
            c.Roads = new byte[w * h]; c.Streets = new byte[w * h]; c.Elevation = new float[w * h];
            double kmLon = KmPerDegree * Math.Cos(lat * Math.PI / 180);
            double cell = (c.Box.KmWide / w + c.Box.KmTall / h) / 2;   // km a cell, about 0.025
            double mw = m.Box.East - m.Box.West, mh = m.Box.North - m.Box.South;
            Func<double, double, double> near = (dist, width) => Math.Max(0, 1 - dist / (cell * width));   // a line's coverage
            Func<double, double, double> gridDist = (u, step) => Math.Abs(u / step - Math.Round(u / step)) * step;
            for (int y = 0; y < h; y++)
            {
                double la = c.Box.North - (y + 0.5) / h * (c.Box.North - c.Box.South);
                double ny = (la - lat) * KmPerDegree;   // km north of home
                for (int x = 0; x < w; x++)
                {
                    double lo = c.Box.West + (x + 0.5) / w * (c.Box.East - c.Box.West);
                    double ex = (lo - lon) * kmLon;   // km east of home
                    double xn = (lo - m.Box.West) / mw, yn = (m.Box.North - la) / mh;
                    int i = y * w + x;
                    double wet = waterKm(xn, yn);
                    double water = Math.Max(0, Math.Min(1, 0.5 + wet / cell));
                    c.Water[i] = (byte)(255 * water);
                    double e = height(xn, yn), hill = Smooth01((e - 40) / 120);
                    e += (45 * (Noise(ex / 0.9, ny / 0.9) - 0.5) + 18 * (Noise(ex / 0.35 + 7, ny / 0.35) - 0.5)) * hill;
                    c.Elevation[i] = (float)e;

                    // the river's middle here, in km north of home (as the map's water function has it)
                    double ryn = 0.585 - 0.12 * (xn - 0.23) + 0.03 * Math.Sin(xn * 9.0 + 0.3);
                    double riverNy = (m.Box.North - ryn * mh - lat) * KmPerDegree;

                    // Freeways: one north and south through town, crossing the river on a bridge, one along the
                    // river's north side; both drawn over water too.
                    double fw1 = near(Math.Abs(ex - (-3.5 + 1.8 * Math.Sin(ny / 5.0))), 1.1);
                    double fw2 = near(Math.Abs(ny - (riverNy + 2.4)), 1.1);
                    double road = Math.Max(fw1, fw2);
                    if (water < 0.5)
                    {
                        // the town: an oval around a point 5 km west of home, its edge ragged; streets only on low ground
                        double tx = (ex + 5) / 14.0, ty = ny / 8.5;
                        double town = 1 - Math.Sqrt(tx * tx + ty * ty) + 0.35 * (Noise(ex / 2.5 + 11, ny / 2.5) - 0.5);
                        double low = 1 - Smooth01((e - 70) / 40);
                        if (town > 0 && low > 0 && wet < -0.08)
                        {
                            double bx = ex / 1.8 + 0.35 * (Noise(ex / 3 + 1, ny / 3) - 0.5), by = ny / 1.8 + 0.35 * (Noise(ex / 3, ny / 3 + 1) - 0.5);
                            int nx0 = (int)Math.Floor(bx), ny0 = (int)Math.Floor(by);   // a neighbourhood, its edges wavy
                            if (Hash(nx0 + 57, ny0) < 0.88)                                 // ...or a park
                            {
                                // its streets bend a little and run at the neighbourhood's own angle and spacing
                                double wx = ex + 0.3 * (Noise(ex / 1.1 + 31, ny / 1.1) - 0.5), wy = ny + 0.3 * (Noise(ex / 1.1, ny / 1.1 + 31) - 0.5);
                                double ang = 0.2 + 1.3 * Hash(nx0, ny0 + 101), step = 0.2 + 0.1 * Hash(nx0 + 3, ny0 + 9);
                                double u = wx * Math.Cos(ang) + wy * Math.Sin(ang), v = -wx * Math.Sin(ang) + wy * Math.Cos(ang);
                                double st = near(Math.Min(gridDist(u, step), gridDist(v, step * 2.2)), 0.8);
                                c.Streets[i] = (byte)(255 * 0.45 * st * Math.Min(1, town * 6) * low);   // as thin as the real streets read
                            }
                        }
                        // marsh on the river's low banks west of town
                        if (wet > -0.45 && ex < -6 && Noise(ex / 1.3 + 3, ny / 1.3) > 0.42) c.Marsh[i] = 255;
                        // creeks: one that flows all year down from the foothills, a seasonal one from the north
                        if (ny > riverNy)
                        {
                            double yr = ny < 6.5 ? near(Math.Abs(ex - (2.4 + 0.7 * Math.Sin(ny / 1.2))), 1.2) : 0;
                            double se = ny < 5.5 ? near(Math.Abs(ex - (-6.2 + 0.8 * Math.Sin(ny / 1.1 + 1))), 1.2) : 0;
                            c.Streams[i] = (byte)(255 * Math.Max(yr, se * 0.67));
                            c.YearRound[i] = (byte)(255 * yr);
                        }
                    }
                    c.Roads[i] = (byte)(255 * road);
                }
            }
            c.Key = c.MakeKey();
            return c;
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
        internal static readonly Color WaterTone = Color.FromArgb(13, 21, 32);    // a deep navy (a warm 22, 21, 20 until 2026-11-21)
        internal static readonly Color ShoreTone = Color.FromArgb(78, 92, 108);   // a cool slate (a warm 92, 89, 84 until 2026-11-21)
        internal static readonly Color CloudTone = Color.FromArgb(200, 196, 188);   // at most ~Secondary on screen: the clock stays the brightest thing
        internal static readonly Color RainQuiet = Color.FromArgb(74, 104, 130);
        internal static readonly Color RainBright = Color.FromArgb(146, 178, 204);

        private sealed class Entry : IDisposable
        {
            public string Key;
            public long Used;
            public Bitmap Base, Shore, Overlay;
            public string OverlayKey;
            public readonly Dictionary<string, int[]> Frames = new Dictionary<string, int[]>();   // premultiplied, at map size
            public Bitmap Mix;      // the weather at this moment, blended from the frames on each paint
            public int[] MixPx;

            public void Dispose()
            {
                if (Base != null) Base.Dispose();
                if (Shore != null) Shore.Dispose();
                if (Overlay != null) Overlay.Dispose();
                if (Mix != null) Mix.Dispose();
                Frames.Clear();
                Base = Shore = Overlay = Mix = null;
                MixPx = null;
            }
        }

        private static readonly object gate = new object();
        private static readonly List<Entry> entries = new List<Entry>();
        private static long clock;

        private static Entry Get(WeatherMap m, GeoBox crop, int w, int h)
        {
            string key = m.BaseId + "|" + m.DemId + "|" + m.Box + "|" + crop + "|" + w + "x" + h;
            foreach (Entry e in entries)
            {
                if (e.Key == key) { e.Used = ++clock; return e; }
            }
            Entry n = new Entry { Key = key, Used = ++clock };
            RenderBase(m, crop, w, h, out n.Base, out n.Shore);
            entries.Add(n);
            while (entries.Count > 8)   // the map and the close-up, for this layout and the one fading out
            {
                Entry oldest = entries[0];
                foreach (Entry e in entries) if (e.Used < oldest.Used) oldest = e;
                entries.Remove(oldest);
                oldest.Dispose();
            }
            return n;
        }

        internal static Bitmap Base(WeatherMap m, GeoBox crop, int w, int h)
        {
            lock (gate) { return Get(m, crop, w, h).Base; }
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

        /// <summary>A weather picture at map size, as a new bitmap the caller disposes (previews and tests).</summary>
        internal static Bitmap Frame(WeatherMap m, GeoBox crop, int w, int h, MapFrame f, bool render)
        {
            int[] px = Pixels(m, crop, w, h, f, render);
            return px == null ? null : FromPixels(px, w, h);
        }

        /// <summary>A weather picture's pixels at map size, rendered now if not ready and render is true (else null).</summary>
        private static int[] Pixels(WeatherMap m, GeoBox crop, int w, int h, MapFrame f, bool render)
        {
            if (f == null) return null;
            lock (gate)
            {
                Entry e = Get(m, crop, w, h);
                int[] px;
                if (e.Frames.TryGetValue(f.Id, out px)) return px;
                if (!render) return null;
                px = RenderFrame(m, crop, w, h, f);
                e.Frames[f.Id] = px;
                Prune(e, m);
                return px;
            }
        }

        /// <summary>
        /// The weather over the map at one moment, as one picture: weather picture a fading into b by t (radar or
        /// clouds), over the fog picture fa fading into fb by ft, all at alpha. Blended here pixel by pixel and
        /// drawn once (2026-11-23): drawing two half-faded pictures over each other thins whatever the two share
        /// in the middle of every fade, which a fade lasting the whole step would show as a slow pulse; and
        /// GDI+'s faded drawing is slow besides. Pictures not rendered yet are left out, except a, which is
        /// rendered now. The bitmap is reused for the next moment: draw it before asking again.
        /// </summary>
        internal static Bitmap Weather(WeatherMap m, GeoBox crop, int w, int h, MapFrame a, MapFrame b, float t,
                                       MapFrame fa, MapFrame fb, float ft, float alpha)
        {
            if (w < 1 || h < 1 || alpha <= 0.004f) return null;
            int[] pa = Pixels(m, crop, w, h, a, true), pb = Pixels(m, crop, w, h, b, false);
            int[] qa = Pixels(m, crop, w, h, fa, true), qb = Pixels(m, crop, w, h, fb, false);
            if (pb == null) t = 0;
            if (qb == null) ft = 0;
            if (pa == null && qa == null) return null;
            lock (gate)
            {
                Entry e = Get(m, crop, w, h);
                if (e.MixPx == null || e.MixPx.Length != w * h) e.MixPx = new int[w * h];
                Compose(e.MixPx, pa, pb, t, qa, qb, ft, alpha);
                if (e.Mix == null || e.Mix.Width != w || e.Mix.Height != h)
                {
                    if (e.Mix != null) e.Mix.Dispose();
                    e.Mix = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                }
                BitmapData d = e.Mix.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    if (d.Stride == w * 4) Marshal.Copy(e.MixPx, 0, d.Scan0, w * h);
                    else for (int y = 0; y < h; y++) Marshal.Copy(e.MixPx, y * w, IntPtr.Add(d.Scan0, y * d.Stride), w);
                }
                finally
                {
                    e.Mix.UnlockBits(d);
                }
                return e.Mix;
            }
        }

        /// <summary>
        /// dst = (a fading into b by t) over (fa fading into fb by ft), times alpha; premultiplied pixels, any of
        /// them null for none. Fast where both layers are empty, which is most of a radar picture.
        /// </summary>
        internal static void Compose(int[] dst, int[] a, int[] b, float t, int[] fa, int[] fb, float ft, float alpha)
        {
            alpha = Clamp01(alpha);
            int wa = (int)(256 * alpha * (1 - Clamp01(t)) + 0.5f), wb = (int)(256 * alpha * Clamp01(t) + 0.5f);
            int fwa = (int)(256 * alpha * (1 - Clamp01(ft)) + 0.5f), fwb = (int)(256 * alpha * Clamp01(ft) + 0.5f);
            if (wa + wb > 256) wb = 256 - wa;
            if (fwa + fwb > 256) fwb = 256 - fwa;
            if (b == null) { b = a; wa += wb; wb = 0; }
            if (fb == null) { fb = fa; fwa += fwb; fwb = 0; }
            int n = dst.Length;
            for (int i = 0; i < n; i++)
            {
                int x = a != null ? a[i] : 0, y = wb > 0 ? b[i] : 0;
                int top = (x | y) == 0 ? 0 : Mix(x, y, wa, wb);
                int fx = fa != null ? fa[i] : 0, fy = fwb > 0 ? fb[i] : 0;
                if ((fx | fy) != 0) top = Over(top, Mix(fx, fy, fwa, fwb));
                dst[i] = top;
            }
        }

        // Two premultiplied pixels weighted wx and wy (out of 256 together, at most), two channels at a time.
        private static int Mix(int x, int y, int wx, int wy)
        {
            uint ux = (uint)x, uy = (uint)y;
            uint rb = (((ux & 0x00FF00FFu) * (uint)wx + (uy & 0x00FF00FFu) * (uint)wy) >> 8) & 0x00FF00FFu;
            uint ag = ((((ux >> 8) & 0x00FF00FFu) * (uint)wx + ((uy >> 8) & 0x00FF00FFu) * (uint)wy)) & 0xFF00FF00u;
            return (int)(rb | ag);
        }

        // A premultiplied pixel over another.
        private static int Over(int top, int under)
        {
            uint t = (uint)top, u = (uint)under;
            if (t == 0) return under;
            uint keep = 255 - (t >> 24);
            if (keep == 0) return top;
            uint rb = (((u & 0x00FF00FFu) * keep + 0x00800080u) >> 8) & 0x00FF00FFu;
            uint ag = ((((u >> 8) & 0x00FF00FFu) * keep + 0x00800080u)) & 0xFF00FF00u;
            return (int)(t + (rb | ag));
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
            if (e.Frames.Count <= m.Radar.Length + m.Sat.Length + m.Fog.Length) return;
            HashSet<string> live = new HashSet<string>();
            foreach (MapFrame f in m.Radar) live.Add(f.Id);
            foreach (MapFrame f in m.Sat) live.Add(f.Id);
            foreach (MapFrame f in m.Fog) live.Add(f.Id);
            List<string> gone = new List<string>();
            foreach (string id in e.Frames.Keys) if (!live.Contains(id)) gone.Add(id);
            foreach (string id in gone) e.Frames.Remove(id);
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

        /// <summary>
        /// The map's ground, drawn into the still block: the land's relief, contour lines, and the water over
        /// them; and, as a separate picture that goes over the weather, the shoreline.
        ///   Relief    hills lit from the north-west (the cartographer's convention), heights exaggerated a
        ///             little, so the ranges read at a glance; black can't be darkened, so slopes facing the
        ///             light brighten and high ground takes a faint warm tint, and valleys stay black.
        ///   Contours  a hairline every 100 m and a stronger one every 500 m, drawn per pixel from the height and
        ///             its slope, so the lines are smooth at any size without tracing them; where a slope packs
        ///             the 100 m lines too close they fade, and steep ground keeps its 500 m lines.
        /// </summary>
        private static void RenderBase(WeatherMap m, GeoBox crop, int w, int h, out Bitmap ground, out Bitmap shore)
        {
            // Coverage of water per screen pixel, from four samples (the source is finer than the screen); marsh
            // the same way, and creeks from the strongest of the four, so a line a pixel wide stays unbroken.
            float[] cov = new float[w * h];
            bool hasMarsh = m.Marsh != null && m.Marsh.Length == m.BaseW * m.BaseH && m.Marsh.Length > 0;
            bool hasStreams = m.Streams != null && m.Streams.Length == m.BaseW * m.BaseH && m.Streams.Length > 0;
            float[] marsh = hasMarsh ? new float[w * h] : null, creek = hasStreams ? new float[w * h] : null;
            bool hasYearRound = hasStreams && m.YearRound != null && m.YearRound.Length == m.Streams.Length;
            float[] yearRound = hasYearRound ? new float[w * h] : null;
            bool hasRoads = m.Roads != null && m.Roads.Length == m.BaseW * m.BaseH && m.Roads.Length > 0;
            bool hasStreets = m.Streets != null && m.Streets.Length == m.BaseW * m.BaseH && m.Streets.Length > 0;
            float[] road = hasRoads ? new float[w * h] : null, street = hasStreets ? new float[w * h] : null;
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
                    if (hasMarsh)
                        marsh[y * w + x] = (Bilinear(m.Marsh, m.BaseW, m.BaseH, ax[x], ay[y]) + Bilinear(m.Marsh, m.BaseW, m.BaseH, bx[x], ay[y]) +
                                            Bilinear(m.Marsh, m.BaseW, m.BaseH, ax[x], by[y]) + Bilinear(m.Marsh, m.BaseW, m.BaseH, bx[x], by[y])) / (4 * 255f);
                    if (hasStreams)
                        creek[y * w + x] = Math.Max(Math.Max(Bilinear(m.Streams, m.BaseW, m.BaseH, ax[x], ay[y]), Bilinear(m.Streams, m.BaseW, m.BaseH, bx[x], ay[y])),
                                                    Math.Max(Bilinear(m.Streams, m.BaseW, m.BaseH, ax[x], by[y]), Bilinear(m.Streams, m.BaseW, m.BaseH, bx[x], by[y]))) / 255f;
                    if (hasYearRound)
                        yearRound[y * w + x] = Math.Max(Math.Max(Bilinear(m.YearRound, m.BaseW, m.BaseH, ax[x], ay[y]), Bilinear(m.YearRound, m.BaseW, m.BaseH, bx[x], ay[y])),
                                                        Math.Max(Bilinear(m.YearRound, m.BaseW, m.BaseH, ax[x], by[y]), Bilinear(m.YearRound, m.BaseW, m.BaseH, bx[x], by[y]))) / 255f;
                    // the bigger roads unbroken (the strongest of the four samples); the streets as how much of the
                    // pixel they cover, so a neighbourhood reads as a fine, even weave
                    if (hasRoads)
                        road[y * w + x] = Math.Max(Math.Max(Bilinear(m.Roads, m.BaseW, m.BaseH, ax[x], ay[y]), Bilinear(m.Roads, m.BaseW, m.BaseH, bx[x], ay[y])),
                                                   Math.Max(Bilinear(m.Roads, m.BaseW, m.BaseH, ax[x], by[y]), Bilinear(m.Roads, m.BaseW, m.BaseH, bx[x], by[y]))) / 255f;
                    if (hasStreets)
                        street[y * w + x] = (Bilinear(m.Streets, m.BaseW, m.BaseH, ax[x], ay[y]) + Bilinear(m.Streets, m.BaseW, m.BaseH, bx[x], ay[y]) +
                                             Bilinear(m.Streets, m.BaseW, m.BaseH, ax[x], by[y]) + Bilinear(m.Streets, m.BaseW, m.BaseH, bx[x], by[y])) / (4 * 255f);
                }
            }
            float[] elev = null;
            if (m.Elevation != null && m.DemW > 1 && m.DemH > 1)
            {
                // A grid finer than the screen (zoom 10 tiles) is sampled four times a pixel, so it gives its detail
                // without aliasing its ridges; a coarser one once, at the pixel's centre.
                float[] dem = m.Elevation;
                int dw = m.DemW, dh = m.DemH;
                bool fine = m.Box.KmWide / dw < crop.KmWide / w * 0.8;
                float[] ex0, ey0, ex1, ey1;
                Mapping(m.Box, crop, w, h, dw, dh, fine ? 0.25f : 0.5f, out ex0, out ey0);
                Mapping(m.Box, crop, w, h, dw, dh, 0.75f, out ex1, out ey1);
                elev = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        elev[y * w + x] = fine
                            ? 0.25f * (BilinearF(dem, dw, dh, ex0[x], ey0[y]) + BilinearF(dem, dw, dh, ex1[x], ey0[y]) +
                                       BilinearF(dem, dw, dh, ex0[x], ey1[y]) + BilinearF(dem, dw, dh, ex1[x], ey1[y]))
                            : BilinearF(dem, dw, dh, ex0[x], ey0[y]);
                // Then softened with two box passes (close to a Gaussian): of a pixel for the fine grid, which keeps
                // ridges and valleys crisp and takes out only gully-sized grain; wider for a grid as coarse as the
                // screen, whose noise would otherwise make the shading grainy and the contours scratchy.
                int radius = fine ? (crop.KmWide * 1000.0 / w < 100 ? 2 : 1) : Math.Max(1, (int)Math.Round(w / 360.0));   // the close-up: 2, or gullies turn to grain
                for (int pass = 0; pass < 2; pass++) elev = BoxBlur(elev, w, h, radius);
            }
            double mx = crop.KmWide * 1000.0 / w, my = crop.KmTall * 1000.0 / h;   // metres a pixel
            // close in (the close-up, ~25 m a pixel), a contour every 50 m and a stronger one every 250 m
            bool closeIn = Math.Min(mx, my) < ContourHalfBelowMetres;
            float contour = closeIn ? ContourMetres / 2 : ContourMetres;
            // close in, seasonal creeks and channels share the ground with every street, so they draw stronger to
            // stay apart from them (still greyer than the year-round blue)
            float seasonal = closeIn ? SeasonalCloseStrength : SeasonalStrength;
            // Real hills look flat at the big map's scale, so their heights are exaggerated; closer in (the
            // close-up, ~60 m a pixel) they need less, or every gully turns to grain (2026-11-21).
            double exaggeration = ReliefExaggeration * Math.Min(1.0, Math.Pow(Math.Min(mx, my) / ReliefFullAtMetres, 0.5));
            double alt = ReliefSunDegrees * Math.PI / 180, az = 315 * Math.PI / 180;
            double lx = Math.Sin(az) * Math.Cos(alt), ly = Math.Cos(az) * Math.Cos(alt), lz = Math.Sin(alt);

            int[] gp = new int[w * h], sp = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int yn = Math.Max(0, y - 1), ys = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, xw = Math.Max(0, x - 1), xe = Math.Min(w - 1, x + 1);
                    float land = 1 - cov[i];
                    float r = 0, g = 0, b = 0;
                    if (elev != null && land > 0.01f)
                    {
                        float e0 = Math.Max(0f, elev[i]);
                        float dE = Math.Max(0f, elev[y * w + xe]) - Math.Max(0f, elev[y * w + xw]);
                        float dN = Math.Max(0f, elev[yn * w + x]) - Math.Max(0f, elev[ys * w + x]);
                        // the ground's normal, heights exaggerated, against the light: brighter only where lit
                        double nx = -exaggeration * dE / (2 * mx), ny = -exaggeration * dN / (2 * my), len = Math.Sqrt(nx * nx + ny * ny + 1);
                        double lit = Math.Max(0, (nx * lx + ny * ly + lz) / len - lz) / (1 - lz);
                        double high = SmoothD(e0 / 1300.0);
                        float level = (float)(0.13 * high + 0.55 * Math.Min(1, lit * 1.6) * (0.3 + 0.7 * Math.Sqrt(high)));
                        r = ReliefTone.R * level; g = ReliefTone.G * level; b = ReliefTone.B * level;
                        // a contour where the height crosses a 100 m level, about a pixel wide whatever the slope;
                        // every fifth (500 m) a little stronger, and where a slope packs the 100 m ones closer than
                        // about three pixels they fade out, so steep ground keeps only its 500 m lines
                        float slope = (float)Math.Sqrt(dE * dE + dN * dN) / 2f;   // metres a pixel
                        if (e0 > contour * 0.5f && slope > 0.5f)
                        {
                            float n = (float)Math.Round(e0 / contour);
                            bool index = (int)n % ContourIndexEvery == 0;
                            float off = Math.Abs(e0 - n * contour) / slope;
                            float spacing = contour * (index ? ContourIndexEvery : 1) / slope;   // pixels to the next such line
                            float room = Math.Max(0f, Math.Min(1f, (spacing - 2.5f) / 2.5f));
                            float a = (1 - off / 0.85f) * (index ? ContourIndexStrength : ContourStrength) * room * Math.Min(1f, land * 1.5f);
                            if (a > 0)
                            {
                                r += (ContourTone.R - r) * a; g += (ContourTone.G - g) * a; b += (ContourTone.B - b) * a;
                            }
                        }
                        r *= land; g *= land; b *= land;
                    }
                    // Marsh: the ground a step toward the water's tone, with rows of short dashes, as on a
                    // topographic print; creeks a fine light line, stronger for bigger streams (2026-11-21).
                    if (hasMarsh && marsh[i] > 0.02f && land > 0.01f)
                    {
                        float a = Math.Min(1f, marsh[i]) * land;
                        r += (WaterTone.R - r) * 0.6f * a; g += (WaterTone.G - g) * 0.6f * a; b += (WaterTone.B - b) * 0.6f * a;
                        if (MarshDash(x, y))
                        {
                            float d = MarshStrength * a;
                            r += (MarshTone.R - r) * d; g += (MarshTone.G - g) * d; b += (MarshTone.B - b) * d;
                        }
                    }
                    if (hasStreams && creek[i] > 0.02f && land > 0.01f)
                    {
                        // how much of the line flows all year: a clear blue, drawn stronger; seasonal creeks a dim
                        // grey-blue (2026-11-21)
                        float f = hasYearRound ? Math.Min(1f, yearRound[i] / creek[i]) : 0f;
                        float a = Math.Min(1f, creek[i]) * (seasonal + (YearRoundStrength - seasonal) * f) * land;
                        float tr = SeasonalTone.R + (YearRoundTone.R - SeasonalTone.R) * f;
                        float tg = SeasonalTone.G + (YearRoundTone.G - SeasonalTone.G) * f;
                        float tb = SeasonalTone.B + (YearRoundTone.B - SeasonalTone.B) * f;
                        r += (tr - r) * a; g += (tg - g) * a; b += (tb - b) * a;
                    }
                    // Streets a faint warm grey on the land (the close-up's, 2026-11-21)...
                    if (hasStreets && street[i] > 0.01f && land > 0.01f)
                    {
                        float a = Math.Min(1f, street[i] * StreetGain) * StreetStrength * land;
                        r += (StreetTone.R - r) * a; g += (StreetTone.G - g) * a; b += (StreetTone.B - b) * a;
                    }
                    float c = cov[i];
                    r += WaterTone.R * c; g += WaterTone.G * c; b += WaterTone.B * c;
                    // ...and the bigger roads a step lighter, over the water too, so the bridges show
                    if (hasRoads && road[i] > 0.02f)
                    {
                        float a = Math.Min(1f, road[i]) * RoadStrength;
                        r += (RoadTone.R - r) * a; g += (RoadTone.G - g) * a; b += (RoadTone.B - b) * a;
                    }
                    gp[i] = unchecked((int)0xFF000000) | (Byte(r) << 16) | (Byte(g) << 8) | Byte(b);

                    float gx = cov[y * w + xe] - cov[y * w + xw];
                    float gy = cov[ys * w + x] - cov[yn * w + x];
                    float edge = (float)Math.Sqrt(gx * gx + gy * gy) * 0.62f;
                    sp[i] = Premul(ShoreTone, Math.Min(1f, edge));
                }
            }
            ground = FromPixels(gp, w, h);
            shore = FromPixels(sp, w, h);
        }

        /// <summary>The marsh pattern: rows of dashes four pixels long, every fourth row, each row staggered.</summary>
        internal static bool MarshDash(int x, int y)
        {
            return y % 4 == 2 && (x + (y / 4 % 2) * 4) % 8 < 4;
        }

        // The water family (2026-11-21, at the user's request for blue year-round waterways): open water a deep
        // navy, the shoreline a cool slate, marsh dashes between the two, creeks that flow all year a clear blue
        // and seasonal ones a dim grey-blue. All darker or greyer than the rain, which stays the brightest blue.
        public const float MarshStrength = 0.75f;
        public const float YearRoundStrength = 0.9f;
        public const float SeasonalStrength = 0.5f;
        public const float SeasonalCloseStrength = 0.85f;   // the close-up's
        internal static readonly Color MarshTone = Color.FromArgb(76, 96, 116);
        internal static readonly Color YearRoundTone = Color.FromArgb(72, 118, 164);
        internal static readonly Color SeasonalTone = Color.FromArgb(86, 100, 112);

        // Streets and roads, the close-up's only (2026-11-21): warm greys under the relief's own tone at most, so
        // the town reads as a fine weave and the freeways as its lines, and the rain stays the brightest thing.
        public const float StreetStrength = 0.7f;
        public const float StreetGain = 1.6f;          // a pixel half covered by streets shows them nearly full
        public const float RoadStrength = 0.75f;
        internal static readonly Color StreetTone = Color.FromArgb(100, 95, 88);
        internal static readonly Color RoadTone = Color.FromArgb(138, 130, 118);

        public const double ReliefSunDegrees = 40;     // the light's height
        public const double ReliefExaggeration = 2.4;  // at this scale real hills look flat
        public const double ReliefFullAtMetres = 120;  // ...from this many metres a pixel; less below (the close-up)
        public const float ContourMetres = 100f;        // 200 m until 2026-11-17
        public const double ContourHalfBelowMetres = 50; // closer than this many metres a pixel, every 50 m (the close-up)
        public const int ContourIndexEvery = 5;         // every 500 m, a little stronger
        public const float ContourStrength = 0.32f;
        public const float ContourIndexStrength = 0.5f;
        internal static readonly Color ReliefTone = Color.FromArgb(120, 114, 106);
        internal static readonly Color ContourTone = Color.FromArgb(82, 78, 72);

        /// <summary>A separable box blur (running sums), edges clamped.</summary>
        private static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            float[] tmp = new float[w * h], dst = new float[w * h];
            float k = 1f / (2 * r + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float sum = 0;
                for (int i = -r; i <= r; i++) sum += src[row + Math.Max(0, Math.Min(w - 1, i))];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = sum * k;
                    sum += src[row + Math.Min(w - 1, x + r + 1)] - src[row + Math.Max(0, x - r)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int i = -r; i <= r; i++) sum += tmp[Math.Max(0, Math.Min(h - 1, i)) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum * k;
                    sum += tmp[Math.Min(h - 1, y + r + 1) * w + x] - tmp[Math.Max(0, y - r) * w + x];
                }
            }
            return dst;
        }

        private static int Byte(float v) { return v <= 0 ? 0 : (v >= 255 ? 255 : (int)(v + 0.5f)); }
        private static double SmoothD(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        private static float BilinearF(float[] v, int gw, int gh, float x, float y)
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

        /// <summary>
        /// The fog product's cells are about this many of its tiles' pixels across (zoom 7, RealEarth's finest,
        /// about 1 km a pixel here; the cells about 10 km), so the fog is smoothed by its cells, not its pixels.
        /// </summary>
        public const float FogCellPixels = 9.5f;

        /// <summary>
        /// One weather picture at map size, premultiplied. The picture's cells are interpolated with a cubic
        /// (Catmull-Rom) curve, which passes through every cell's own value, so smoothing never moves rain or
        /// fog; until 2026-11-23 straight-line blending left diamond-shaped steps at the edge of the rain (the
        /// user: "if it's pixelated smooth it out ... as smooth as possible without greatly obscuring
        /// accuracy"). Then a light blur, by the cell's size on screen: radar about a quarter of a cell on the
        /// map and a third in the close-up (where a 1 km cell is 80 pixels, so showers come out as round as
        /// they are), clouds only where blown up, and the fog, whose cells are about 10 km, about a third of
        /// its cell, so it reads as a soft bank rather than squares.
        /// </summary>
        private static int[] RenderFrame(WeatherMap m, GeoBox crop, int w, int h, MapFrame f)
        {
            float[] fx, fy;
            GeoBox fb = !f.Box.IsEmpty ? f.Box : (m.FrameBox.IsEmpty ? m.Box : m.FrameBox);   // the close-up plays the big map's pictures
            Mapping(fb, crop, w, h, f.W, f.H, 0.5f, out fx, out fy);
            if (f.Mercator) MercatorRows(fb, crop, h, f.H, fy);
            int[] px = new int[w * h];
            bool radar = f.Kind == "radar", fog = f.Kind == "fog";
            float gain = 1f;
            if (f.Kind == "vis")
            {
                double elev = CurtainPainter.SunElevation(f.Utc, (crop.North + crop.South) / 2, (crop.West + crop.East) / 2);
                gain = VisibleGain(elev);
            }
            float[] val = Cubic(f.Values, f.W, f.H, fx, fy, w, h);
            float cellPx = (float)(w * (fb.East - fb.West) / Math.Max(1, f.W) / Math.Max(1e-9, crop.East - crop.West));
            int radius = 0, passes = 2;
            if (radar) radius = (int)Math.Round(cellPx * (cellPx >= 8 ? 0.35f : 0.25f));
            else if (fog) { radius = (int)Math.Round(cellPx * FogCellPixels * 0.3f); passes = 3; }
            else if (cellPx >= 4) radius = (int)Math.Round(cellPx * 0.2f);
            radius = Math.Min(radius, Math.Max(w, h));
            if (radius >= 1) for (int pass = 0; pass < passes; pass++) val = BoxBlur(val, w, h, radius);
            if (radar)
            {
                for (int i = 0; i < w * h; i++) px[i] = RadarArgb(val[i]);
            }
            else
            {
                int[] lut = new int[256];
                for (int v = 0; v < 256; v++) lut[v] = fog ? FogArgb(v) : CloudArgb(f.Kind, v * gain);
                for (int i = 0; i < w * h; i++) px[i] = lut[Math.Max(0, Math.Min(255, (int)(val[i] + 0.5f)))];
            }
            return px;
        }

        /// <summary>Rows for a picture whose rows are spaced as on a web map (Mercator), over its box.</summary>
        private static void MercatorRows(GeoBox box, GeoBox crop, int h, int gh, float[] fy)
        {
            double top = MercatorY(box.North), bottom = MercatorY(box.South);
            for (int y = 0; y < h; y++)
            {
                double lat = crop.North - (y + 0.5) / h * (crop.North - crop.South);
                fy[y] = (float)((top - MercatorY(lat)) / (top - bottom) * gh - 0.5);
            }
        }

        internal static double MercatorY(double lat)
        {
            double r = Math.Max(-85, Math.Min(85, lat)) * Math.PI / 180;
            return Math.Log(Math.Tan(Math.PI / 4 + r / 2));
        }

        /// <summary>
        /// A picture's values at each screen pixel, interpolated with a cubic curve (Catmull-Rom) along rows
        /// and then columns: smooth, and exact at each cell's own value. Clamped to 0..255 (the curve overshoots
        /// a little at sharp edges).
        /// </summary>
        internal static float[] Cubic(byte[] v, int gw, int gh, float[] fx, float[] fy, int w, int h)
        {
            int[] cx = new int[w * 4], cy = new int[h * 4];
            float[] wx = new float[w * 4], wy = new float[h * 4];
            for (int x = 0; x < w; x++) CubicTaps(fx[x], gw, cx, wx, x * 4);
            for (int y = 0; y < h; y++) CubicTaps(fy[y], gh, cy, wy, y * 4);
            int lo = gh - 1, hi = 0;
            for (int k = 0; k < cy.Length; k++) { lo = Math.Min(lo, cy[k]); hi = Math.Max(hi, cy[k]); }
            if (hi < lo) hi = lo;
            // each source row the screen needs, interpolated across once
            float[] rows = new float[(hi - lo + 1) * w];
            for (int sy = lo; sy <= hi; sy++)
            {
                int src = sy * gw, dst = (sy - lo) * w;
                for (int x = 0; x < w; x++)
                {
                    int k = x * 4;
                    rows[dst + x] = v[src + cx[k]] * wx[k] + v[src + cx[k + 1]] * wx[k + 1] + v[src + cx[k + 2]] * wx[k + 2] + v[src + cx[k + 3]] * wx[k + 3];
                }
            }
            float[] val = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int k = y * 4;
                int r0 = (cy[k] - lo) * w, r1 = (cy[k + 1] - lo) * w, r2 = (cy[k + 2] - lo) * w, r3 = (cy[k + 3] - lo) * w;
                float w0 = wy[k], w1 = wy[k + 1], w2 = wy[k + 2], w3 = wy[k + 3];
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float s = rows[r0 + x] * w0 + rows[r1 + x] * w1 + rows[r2 + x] * w2 + rows[r3 + x] * w3;
                    val[row + x] = s < 0 ? 0 : (s > 255 ? 255 : s);
                }
            }
            return val;
        }

        // The four cells around position p (clamped to the picture) and their Catmull-Rom weights.
        private static void CubicTaps(float p, int n, int[] idx, float[] wt, int at)
        {
            if (p < 0) p = 0;
            if (p > n - 1) p = n - 1;
            int i = (int)Math.Floor(p);
            float t = p - i, t2 = t * t, t3 = t2 * t;
            wt[at] = 0.5f * (-t3 + 2 * t2 - t);
            wt[at + 1] = 0.5f * (3 * t3 - 5 * t2 + 2);
            wt[at + 2] = 0.5f * (-3 * t3 + 4 * t2 + t);
            wt[at + 3] = 0.5f * (t3 - t2);
            for (int k = 0; k < 4; k++) idx[at + k] = Math.Max(0, Math.Min(n - 1, i - 1 + k));
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

        /// <summary>
        /// How thick the fog's veil gets: the ground, the water and any rain stay readable through it (0.42 made
        /// a bank over the whole sea a flat grey slab).
        /// </summary>
        public const float FogMax = 0.30f;

        /// <summary>
        /// The chance of fog (percent) as a premultiplied colour, in the clouds' own warm white: nothing below
        /// 30%, then a veil thickening to FogMax at 70% and over.
        /// </summary>
        internal static int FogArgb(float percent)
        {
            return Premul(CloudTone, FogMax * Smooth((percent - 30f) / 40f));
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
