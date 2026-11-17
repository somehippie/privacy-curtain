using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeCurtain
{
    /// <summary>
    /// Paints the curtain into a PNG instead of showing it (--render-preview). The real curtain is excluded
    /// from screen capture by design, so this is how its design is reviewed and documented. Sample data is
    /// fixed so renders are comparable; the forecast is the real local\weather.txt when there is one, and
    /// with a real map (local\map.txt) the time and the next hours are real too.
    ///
    ///   --test-state idle|typing|wrong|shake|cooldown|admin|bright|night   which moment to show;
    ///                       radar|clouds   the map in that mode whatever the weather says;
    ///                       summary|launch   instead, the "While you were away" window or the launcher's dialog
    ///   --hide-home         leave home off the map, for a picture to share
    ///   --overlay           draw the construction on top: the φ : 1 columns, the golden-section margins
    ///                       and the type scale
    ///   --render-drift PNG  instead, plot two hours of the drift path
    /// </summary>
    internal static class CurtainPreview
    {
        public static int Run(Options o)
        {
            int w = 2560, h = 1440;
            if (!string.IsNullOrEmpty(o.TestScreen))
            {
                string[] parts = o.TestScreen.ToLowerInvariant().Split('x');
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out w) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out h))
                    return ExitCode.Usage;
            }
            if (string.Equals(o.TestState, "summary", StringComparison.OrdinalIgnoreCase))
            {
                RenderSummary(o.PreviewFile, w, h);
                return ExitCode.Ok;
            }
            if (string.Equals(o.TestState, "launch", StringComparison.OrdinalIgnoreCase))
            {
                LaunchView.RenderSample(o.PreviewFile, w, h);   // the launcher's dialog, for a sample launch
                return ExitCode.Ok;
            }
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (CurtainForm form = new CurtainForm(null, new Rectangle(0, 0, w, h)))
            {
                g.Clear(Color.Black);
                if (o.PreviewDrift) DrawDrift(g, Sample(o, "idle"), w, h);
                else
                {
                    Render(form.Painter, g, o, w, h);
                    if (o.PreviewOverlay) DrawConstruction(g, form.Painter, w, h);
                }
                bmp.Save(o.PreviewFile, ImageFormat.Png);
            }
            return ExitCode.Ok;
        }

        /// <summary>The "While you were away" window for a sample overnight session, as an image.</summary>
        private static void RenderSummary(string file, int w, int h)
        {
            string text = string.Join("\n", new[]
            {
                "While you were away",
                "Curtain was up for 12 h 4 min (11:44 PM to 11:49 AM). Closed by: PIN.",
                "- Wrong PIN attempts: 2",
                "- Keyboard: 23 key presses blocked",
                "- Mouse: 4 clicks blocked, mouse was moved",
                "- Someone touched the keyboard or mouse in 3 separate minute(s)",
                "- Windows security screen opened (Ctrl+Alt+Del, lock or admin prompt): 1",
                "Full record: scripts\\curtain-log.ps1",
            });
            List<KeyValuePair<double, string>> moments = new List<KeyValuePair<double, string>>();
            foreach (double m in new double[] { 7140, 7200, 7260, 31500 }) moments.Add(new KeyValuePair<double, string>(m, "input"));
            moments.Add(new KeyValuePair<double, string>(7230, "wrong-pin"));
            moments.Add(new KeyValuePair<double, string>(7290, "wrong-pin"));
            moments.Add(new KeyValuePair<double, string>(31530, "security-screen"));
            using (SummaryView v = new SummaryView(text, moments, 12 * 3600 + 4 * 60, new Size(w, h), 1.0))
            using (Bitmap b = new Bitmap(v.ClientSize.Width, v.ClientSize.Height, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                v.PaintContent(g);
                b.Save(file, ImageFormat.Png);
            }
        }

        /// <summary>Plays a short scripted timeline so animated states are shown at a real moment.</summary>
        internal static void Render(CurtainPainter p, Graphics g, Options o, int w, int h)
        {
            string state = (o.TestState ?? "idle").ToLowerInvariant();
            p.FixedNowMs = 0;
            p.Paint(g, Sample(o, "idle"), w, h);   // the first frame starts the fade-in
            long final = 4000;
            if (state == "typing" || state == "wrong" || state == "shake")
            {
                p.FixedNowMs = 2000;
                p.Paint(g, Sample(o, "typing"), w, h);
            }
            if (state == "wrong" || state == "shake")
            {
                p.FixedNowMs = 3000;
                p.Paint(g, Sample(o, "wrong"), w, h);
                if (state == "shake") final = 3070;
            }
            if (state == "cooldown")
            {
                p.FixedNowMs = 3000;
                p.Paint(g, Sample(o, "cooldown"), w, h);
            }
            p.FixedNowMs = final;
            p.Paint(g, Sample(o, state == "shake" ? "wrong" : state), w, h);
        }

        internal static DisplayState Sample(Options o)
        {
            return Sample(o, o.TestState);
        }

        internal static DisplayState Sample(Options o, string state)
        {
            // With a real map (local\ has one) the picture shows the real time and the real hourly forecast, so
            // it agrees with itself; otherwise everything is fixed sample data, so renders are comparable.
            WeatherMap real = o.LocalDir != null ? WeatherMap.Load(o.LocalDir, DateTime.UtcNow) : null;
            DateTime now = new DateTime(2026, 11, 15, 10, 42, 0);
            if (real != null) { DateTime c = DateTime.Now; now = new DateTime(c.Year, c.Month, c.Day, c.Hour, c.Minute, 0); }
            DisplayState s = new DisplayState();
            s.Clock = now.ToString("t", CultureInfo.GetCultureInfo("en-US"));
            s.Date = now.ToString("dddd, MMMM d", CultureInfo.GetCultureInfo("en-US"));
            s.Zone = real != null ? CurtainUi.ZoneText(now) : "PST, GMT−8";
            s.Drift = Math.Max(0, o.PreviewSeconds) * 5;   // Drift counts 200 ms ticks
            s.Cpu = "12%";
            s.Ram = "45% · 14.2/32 GB";
            s.Net = "↓ 1.2 MB/s  ↑ 38 KB/s";
            s.Uptime = "3d 4h 12m";
            s.Disk = "C: 212 GB free of 465";
            s.Stats = "CPU " + s.Cpu + "     RAM " + s.Ram + "     Net " + s.Net;
            s.SysInfo = "Up " + s.Uptime + "     " + s.Disk;
            s.Sys = SampleSys();
            s.Now = now;
            s.Hourly = SampleHourly(now);
            s.HourlyKey = "sample";
            s.Lat = 45.0;     // a neutral sample place in the Pacific time zone, not anyone's home
            s.Lon = -122.0;

            // The map: the real one when local\ has it (and then the real place, for home on it, and the real
            // hours), else a made-up sample.
            if (real != null)
            {
                string hourly = Path.Combine(o.LocalDir, "weather-hourly.txt");
                try
                {
                    if (File.Exists(hourly))
                    {
                        string[] all = File.ReadAllLines(hourly, Encoding.UTF8);
                        double la, lo;
                        if (SysStats.ParseLocation(all, out la, out lo)) { s.Lat = la; s.Lon = lo; }
                        HourPoint[] hours = SysStats.ParseHourly(all);
                        if (hours.Length > 0) { s.Hourly = hours; s.HourlyKey = "local"; }
                    }
                }
                catch { }
                s.Map = real;
            }
            else
            {
                s.Map = WeatherMap.Sample(s.Lat, s.Lon, now.ToUniversalTime());
            }
            if (o.PreviewHideHome)
            {
                // A picture to share: no home on the map, which centres on the place rounded to 0.1° (as the
                // downloads are) instead of on the place itself.
                s.HideHome = true;
                s.Lat = Math.Round(s.Lat, 1);
                s.Lon = Math.Round(s.Lon, 1);
            }

            string wx = Path.Combine(o.LocalDir ?? "", "weather.txt");
            string[] lines = null;
            try { if (o.LocalDir != null && File.Exists(wx)) lines = File.ReadAllLines(wx, Encoding.UTF8); } catch { }
            if (lines == null || lines.Length == 0)
            {
                lines = new[]
                {
                    "Anytown forecast  (as of 9:05 AM)",
                    "Today:  Hi 70° / Lo 50°   Partly Sunny  (2% precip)",
                    "Tonight:  Lo 48°   Mostly Clear",
                    "Monday:  Hi 64° / Lo 47°   Chance Light Rain then Patchy Fog  (53% precip)",
                };
            }
            s.Weather = lines;

            switch ((state ?? "idle").ToLowerInvariant())
            {
                case "typing": s.PinState = PinGate.StateTyping; s.Digits = 4; break;
                case "wrong": case "shake": s.PinState = PinGate.StateWrong; break;
                case "cooldown": s.PinState = PinGate.StateCooldown; s.CooldownLeft = 27; break;
                case "admin": s.AdminNotice = true; break;
                case "bright": s.Bright = true; break;
                case "night": s.NightDim = CurtainPainter.NightLevel; break;
                case "radar": s.MapMode = 1; break;
                case "clouds": s.MapMode = 2; break;
            }
            return s;
        }

        /// <summary>Twelve hours from the top of the current hour: rain moving in over the afternoon.</summary>
        internal static HourPoint[] SampleHourly(DateTime now)
        {
            int[] temp = { 58, 60, 62, 63, 63, 62, 60, 58, 57, 56, 55, 55 };
            int[] pop = { 5, 9, 18, 34, 58, 76, 84, 70, 45, 25, 12, 8 };
            string[] text =
            {
                "Mostly Cloudy", "Mostly Cloudy", "Slight Chance Light Rain", "Chance Light Rain", "Light Rain Likely", "Light Rain",
                "Light Rain", "Light Rain Likely", "Chance Light Rain", "Slight Chance Light Rain", "Mostly Cloudy", "Partly Cloudy",
            };
            DateTime top = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            HourPoint[] h = new HourPoint[temp.Length];
            for (int i = 0; i < h.Length; i++)
            {
                h[i] = new HourPoint { Start = top.AddHours(i), Temp = temp[i], Pop = pop[i], Text = text[i],
                                       Humidity = 62 + 2 * i, Wind = (6 + i / 2) + " mph SSW" };
                h[i].IsDay = h[i].Start.Hour >= 7 && h[i].Start.Hour < 17;
            }
            return h;
        }

        /// <summary>Ten plausible minutes: a quiet machine with a few bursts of work and one download.</summary>
        internal static SysSnapshot SampleSys()
        {
            Random r = new Random(7);
            int n = SysStats.HistoryLength;
            float[] cpu = new float[n], net = new float[n];
            for (int i = 0; i < n; i++)
            {
                double c = 6 + 4 * r.NextDouble();
                c += 58 * Bump(i, 95, 14) + 74 * Bump(i, 300, 22) + 36 * Bump(i, 455, 10) + 22 * Bump(i, 560, 30);
                cpu[i] = (float)Math.Min(100, c + 3 * (r.NextDouble() - 0.5));
                double k = 18 * 1024 + 12 * 1024 * r.NextDouble();
                k += 3.1 * 1048576 * Bump(i, 300, 26) + 0.9 * 1048576 * Bump(i, 520, 12) + 1.2 * 1048576 * Bump(i, 590, 9);
                net[i] = (float)k;
            }
            SysSnapshot s = new SysSnapshot();
            s.Version = 1;
            s.Cpu = 12; s.MemPercent = 45; s.MemUsedGb = 14.2; s.MemTotalGb = 32;
            s.NetDown = net[n - 1]; s.NetUp = 38 * 1024;
            s.DiskUsedPercent = 54; s.DiskFreeGb = 212; s.DiskTotalGb = 465; s.DiskName = "C:";
            s.UptimeSeconds = 3 * 86400 + 4 * 3600 + 12 * 60;
            s.CpuHistory = cpu;
            s.NetHistory = net;
            return s;
        }

        private static double Bump(int i, double at, double width)
        {
            double z = (i - at) / width;
            return Math.Exp(-z * z);
        }

        // ------------------------------------------------------------------ construction overlay

        private static readonly Color Ink = Color.FromArgb(225, 217, 119, 87);
        private static readonly Color Faint = Color.FromArgb(120, 217, 119, 87);

        private static void DrawConstruction(Graphics g, CurtainPainter p, int w, int h)
        {
            CurtainPainter.Layout l = p.Current;
            if (l == null) return;
            Point at = p.LastPlacement;
            float ox = at.X + l.OriginX, oy = at.Y + l.OriginY, u = l.Unit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font f = new Font("Bahnschrift", Math.Max(11f, u * 0.13f), GraphicsUnit.Pixel))
            using (Font big = new Font("Bahnschrift SemiLight", Math.Max(14f, u * 0.2f), GraphicsUnit.Pixel))
            using (Pen line = new Pen(Ink, 1f))
            using (Pen faint = new Pen(Faint, 1f) { DashStyle = DashStyle.Dash })
            using (SolidBrush text = new SolidBrush(Ink))
            {
                // the columns: φ : 1
                RectangleF left = new RectangleF(ox + l.LeftCol.X, oy + l.LeftCol.Y, l.LeftCol.Width, l.LeftCol.Height);
                float air = u * 0.14f;
                g.DrawRectangle(faint, left.X - air, left.Y - air, left.Width + 2 * air, left.Height + 2 * air);
                float dimY = oy - u * 0.55f;
                Dimension(g, line, text, big, left.Left, left.Right, dimY, "φ");
                if (l.HasRight)
                {
                    RectangleF right = new RectangleF(ox + l.RightCol.X, oy + l.RightCol.Y, l.RightCol.Width, l.RightCol.Height);
                    g.DrawRectangle(faint, right.X - air, right.Y - air, right.Width + 2 * air, right.Height + 2 * air);
                    if (l.Landscape) Dimension(g, line, text, big, right.Left, right.Right, dimY, "1");
                }

                // the margins: the composition rests on the golden section (space above : below = 1 : φ)
                Size bs = l.Block.Size;
                float top = at.Y, bottom = at.Y + bs.Height, mx = Math.Max(u * 0.4f, at.X * 0.45f);
                g.DrawLine(faint, 0, top, w, top);
                g.DrawLine(faint, 0, bottom, w, bottom);
                VDimension(g, line, text, big, mx, 0, top, "1");
                VDimension(g, line, text, big, mx, bottom, h, "φ");

                // the type scale: every size is the clock size over a power of φ
                foreach (KeyValuePair<string, float> b in l.Baselines)
                {
                    float y = oy + b.Value;
                    float x1 = ox - u * 0.15f;
                    g.DrawLine(line, x1 - u * 0.35f, y, x1, y);
                    SizeF sz = g.MeasureString(b.Key, f);
                    g.DrawString(b.Key, f, text, x1 - u * 0.45f - sz.Width, y - sz.Height * 0.62f);
                }

                string note = "CONSTRUCTION   ·   COLUMNS φ : 1   ·   MARGINS 1 : φ   ·   TYPE SCALE C / φⁿ   ·   φ = 1.618";
                SizeF ns = g.MeasureString(note, f);
                g.DrawString(note, f, text, w - u * 0.4f - ns.Width, h - u * 0.45f);
            }
        }

        private static void Dimension(Graphics g, Pen pen, Brush b, Font f, float x0, float x1, float y, string label)
        {
            g.DrawLine(pen, x0, y, x1, y);
            g.DrawLine(pen, x0, y - 8, x0, y + 8);
            g.DrawLine(pen, x1, y - 8, x1, y + 8);
            SizeF sz = g.MeasureString(label, f);
            float mid = (x0 + x1) / 2f;
            using (SolidBrush black = new SolidBrush(Color.Black)) g.FillRectangle(black, mid - sz.Width / 2f - 6, y - sz.Height / 2f, sz.Width + 12, sz.Height);
            g.DrawString(label, f, b, mid - sz.Width / 2f, y - sz.Height / 2f);
        }

        private static void VDimension(Graphics g, Pen pen, Brush b, Font f, float x, float y0, float y1, string label)
        {
            g.DrawLine(pen, x, y0, x, y1);
            g.DrawLine(pen, x - 8, y0, x + 8, y0);
            g.DrawLine(pen, x - 8, y1, x + 8, y1);
            SizeF sz = g.MeasureString(label, f);
            float mid = (y0 + y1) / 2f;
            using (SolidBrush black = new SolidBrush(Color.Black)) g.FillRectangle(black, x - sz.Width / 2f - 4, mid - sz.Height / 2f, sz.Width + 8, sz.Height);
            g.DrawString(label, f, b, x - sz.Width / 2f, mid - sz.Height / 2f);
        }

        // ------------------------------------------------------------------ drift path

        /// <summary>Two hours of the composition's drift, magnified so the path is visible.</summary>
        private static void DrawDrift(Graphics g, DisplayState s, int w, int h)
        {
            CurtainPainter.Layout l = CurtainPainter.Compose(s, w, h);
            if (l == null) return;
            try
            {
                int bw = l.Block.Width, bh = l.Block.Height;
                Point rest = CurtainPainter.Place(bw, bh, w, h, 0);
                double ax = Math.Min(w * 0.05, (w - bw) / 2.0);
                double freeY = h - bh, restY = freeY / (1 + CurtainPainter.Phi);
                double ay = Math.Min(h * 0.05, Math.Min(restY, freeY - restY));
                float u = l.Unit;
                // magnify the drift envelope to fill most of the frame
                float zoom = (float)Math.Min(w * 0.78 / (2 * ax), h * 0.62 / (2 * ay));
                float cx = w / 2f, cy = h * 0.53f;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (Pen frame = new Pen(CurtainPainter.RuleColor, 1f))
                    g.DrawRectangle(frame, cx - (float)ax * zoom, cy - (float)ay * zoom, (float)(2 * ax) * zoom, (float)(2 * ay) * zoom);
                // The continuous path that the whole-pixel steps follow.
                List<PointF> pts = new List<PointF>();
                for (double t = 0; t <= 2 * 3600; t += 1.5)
                {
                    double tx = 2 * Math.PI * t / CurtainPainter.DriftPeriodSeconds, ty = tx / CurtainPainter.Phi;
                    pts.Add(new PointF(cx + (float)(ax * Math.Sin(tx)) * zoom, cy + (float)(ay * Math.Sin(ty)) * zoom));
                }
                using (Pen path = new Pen(Color.FromArgb(150, CurtainPainter.Secondary), Math.Max(1.5f, u / 90f)))
                {
                    path.LineJoin = LineJoin.Round;
                    g.DrawLines(path, pts.ToArray());
                }
                float d = u * 0.09f;
                using (SolidBrush accent = new SolidBrush(CurtainPainter.Accent))
                    g.FillEllipse(accent, cx - d / 2f, cy - d / 2f, d, d);
                using (Font label = new Font("Bahnschrift", Math.Max(12f, u * 0.14f), GraphicsUnit.Pixel))
                using (Font title = new Font("Bahnschrift SemiLight", Math.Max(16f, u * 0.3f), GraphicsUnit.Pixel))
                using (SolidBrush ink = new SolidBrush(CurtainPainter.Secondary))
                using (SolidBrush dim = new SolidBrush(CurtainPainter.Tertiary))
                {
                    g.DrawString("Two hours of drift", title, ink, u * 0.6f, u * 0.5f);
                    g.DrawString("ONE SWEEP ACROSS EVERY 5 MIN   ·   ONE SWEEP DOWN EVERY 5 × φ ≈ 8.1 MIN   ·   THE PATH NEVER REPEATS",
                                 label, dim, u * 0.62f, u * 0.5f + u * 0.42f);
                    string scale = "±" + Math.Round(ax) + " × ±" + Math.Round(ay) + " PX ON A " + w + " × " + h + " SCREEN, SHOWN ×" + Math.Round(zoom, 1).ToString(CultureInfo.InvariantCulture);
                    g.DrawString(scale, label, dim, u * 0.62f, h - u * 0.6f);
                }
            }
            finally
            {
                l.Dispose();
            }
        }
    }
}
