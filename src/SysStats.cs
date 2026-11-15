using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;

namespace ClaudeCurtain
{
    /// <summary>
    /// Cheap, glanceable machine stats for the curtain screen, plus a short forecast read from a file that
    /// a script refreshes. Sampled about once a second from the control thread; the UI thread only reads the
    /// finished strings. Nothing here needs administrator rights, touches the network, or throws to a caller:
    /// a failing reading just shows "--" and the curtain is never affected.
    ///
    ///   CPU   from GetSystemTimes (idle vs. busy over the interval), not a performance counter
    ///   RAM   from GlobalMemoryStatusEx
    ///   Net   from the network interfaces' byte counters, as a rate over the interval
    ///   Up    system uptime; Disk free on the system drive
    ///   Weather   whatever curtain-weather.ps1 last wrote to local\weather.txt (already formatted)
    /// </summary>
    internal sealed class SysStats
    {
        private static readonly string[] None = new string[0];

        private readonly string weatherFile;
        private readonly string hourlyFile;
        private DateTime hourlyStamp;
        private volatile HourPoint[] hourly = new HourPoint[0];
        private volatile string hourlyKey = "";

        /// <summary>The coming hours from weather-hourly.txt (empty when missing or more than a day old).</summary>
        public HourPoint[] Hourly { get { return hourly; } }
        public string HourlyKey { get { return hourlyKey; } }
        private double lat = double.NaN, lon = double.NaN;
        /// <summary>The forecast grid's centre (NaN until known), for sunrise and sunset.</summary>
        public double Latitude { get { return lat; } }
        public double Longitude { get { return lon; } }
        private readonly string systemDrive;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        // CPU sampling state (FILETIME ticks, 100 ns units).
        private long lastIdle, lastKernel, lastUser;
        private bool haveCpu;

        // Network sampling state.
        private long lastRx, lastTx, lastNetMs;
        private bool haveNet;

        // Weather file state: re-read only when the file changes.
        private DateTime weatherStamp;

        // Published to the UI thread. Reference/word assignments are atomic; marked volatile for visibility.
        private volatile string statsLine = "";
        private volatile string sysLine = "";
        private volatile string[] weather = None;

        public SysStats(string localDir)
        {
            weatherFile = string.IsNullOrEmpty(localDir) ? null : Path.Combine(localDir, "weather.txt");
            hourlyFile = string.IsNullOrEmpty(localDir) ? null : Path.Combine(localDir, "weather-hourly.txt");
            string root = "C:\\";
            try { root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)); }
            catch { }
            systemDrive = string.IsNullOrEmpty(root) ? "C:\\" : root;
        }

        public string StatsLine { get { return statsLine; } }
        public string SysLine { get { return sysLine; } }

        // The same readings one by one, for the curtain's table layout.
        private volatile string cpuText = "--", ramText = "--", netText = "--", uptimeText = "--", diskText = "";
        public string Cpu { get { return cpuText; } }
        public string Ram { get { return ramText; } }
        public string Net { get { return netText; } }
        public string UpFor { get { return uptimeText; } }
        public string Disk { get { return diskText; } }
        public string[] Weather { get { return weather; } }

        // Numbers behind the strings, and ten minutes of history, for the curtain's charts.
        public const int HistoryLength = 600;   // one reading a second
        private readonly float[] cpuRing = new float[HistoryLength];
        private readonly float[] netRing = new float[HistoryLength];
        private int ringCount, ringNext, version;
        private double cpuNow = -1, memPct = -1, memUsed, memTotal, netDown = -1, netUp = -1, diskPct = -1, diskFree, diskTotal;
        private long upSecs = -1;
        private string diskName = "";
        private volatile SysSnapshot snapshot = new SysSnapshot();

        /// <summary>The latest readings as one immutable object (safe to hand to another thread).</summary>
        public SysSnapshot Snapshot { get { return snapshot; } }

        /// <summary>Takes one reading. Call on a timer (about once a second). Never throws.</summary>
        public void Sample()
        {
            try { statsLine = BuildStats(); } catch { }
            try { sysLine = BuildSystem(); } catch { }
            try { ReadWeather(); } catch { }
            try { ReadHourly(); } catch { }
            try { Publish(); } catch { }
        }

        private void Publish()
        {
            cpuRing[ringNext] = (float)cpuNow;
            netRing[ringNext] = (float)netDown;
            ringNext = (ringNext + 1) % HistoryLength;
            if (ringCount < HistoryLength) ringCount++;

            SysSnapshot s = new SysSnapshot();
            s.Version = ++version;
            s.Cpu = cpuNow; s.MemPercent = memPct; s.MemUsedGb = memUsed; s.MemTotalGb = memTotal;
            s.NetDown = netDown; s.NetUp = netUp;
            s.DiskUsedPercent = diskPct; s.DiskFreeGb = diskFree; s.DiskTotalGb = diskTotal; s.DiskName = diskName;
            s.UptimeSeconds = upSecs;
            s.CpuHistory = Unroll(cpuRing);
            s.NetHistory = Unroll(netRing);
            snapshot = s;
        }

        private float[] Unroll(float[] ring)
        {
            float[] a = new float[ringCount];
            int start = (ringNext - ringCount + HistoryLength) % HistoryLength;
            for (int i = 0; i < ringCount; i++) a[i] = ring[(start + i) % HistoryLength];
            return a;
        }

        private string BuildStats()
        {
            string cpu = "--";
            long idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                if (haveCpu)
                {
                    long dIdle = idle - lastIdle;
                    long dTotal = (kernel - lastKernel) + (user - lastUser);   // kernel time already includes idle
                    if (dTotal > 0)
                    {
                        double busy = 100.0 * (dTotal - dIdle) / dTotal;
                        if (busy < 0) busy = 0;
                        if (busy > 100) busy = 100;
                        cpu = ((int)Math.Round(busy)) + "%";
                        cpuNow = busy;
                    }
                }
                lastIdle = idle; lastKernel = kernel; lastUser = user; haveCpu = true;
            }

            string ram = "--";
            Native.MEMORYSTATUSEX mem = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(mem) && mem.ullTotalPhys > 0)
            {
                double totalGb = mem.ullTotalPhys / 1073741824.0;
                double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                ram = mem.dwMemoryLoad + "% \u00b7 " + usedGb.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                      totalGb.ToString("0", CultureInfo.InvariantCulture) + " GB";
                memPct = mem.dwMemoryLoad; memUsed = usedGb; memTotal = totalGb;
            }

            string net = "--";
            long rx, tx;
            if (ReadNet(out rx, out tx))
            {
                long nowMs = clock.ElapsedMilliseconds;
                if (haveNet && nowMs > lastNetMs)
                {
                    double secs = (nowMs - lastNetMs) / 1000.0;
                    double down = (rx - lastRx) / secs;
                    double up = (tx - lastTx) / secs;
                    if (down < 0) down = 0;   // a counter reset or an interface going away
                    if (up < 0) up = 0;
                    net = "\u2193 " + Rate(down) + "  \u2191 " + Rate(up);
                    netDown = down; netUp = up;
                }
                lastRx = rx; lastTx = tx; lastNetMs = nowMs; haveNet = true;
            }

            cpuText = cpu; ramText = ram; netText = net;
            return "CPU " + cpu + "     RAM " + ram + "     Net " + net;
        }

        private static bool ReadNet(out long rx, out long tx)
        {
            rx = 0; tx = 0;
            bool any = false;
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in all)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    IPv4InterfaceStatistics s = ni.GetIPv4Statistics();
                    rx += s.BytesReceived;
                    tx += s.BytesSent;
                    any = true;
                }
                catch { }
            }
            return any;
        }

        private static string Rate(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 1000.0) return kb.ToString(kb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB/s";
            double mb = kb / 1024.0;
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
        }

        private string BuildSystem()
        {
            long secs = (long)(Native.GetTickCount64() / 1000UL);
            uptimeText = Uptime(secs);
            upSecs = secs;
            string line = "Up " + uptimeText;
            try
            {
                DriveInfo d = new DriveInfo(systemDrive);
                if (d.IsReady)
                {
                    double freeGb = d.TotalFreeSpace / 1073741824.0;
                    double totalGb = d.TotalSize / 1073741824.0;
                    diskText = d.Name.TrimEnd('\\') + " " + freeGb.ToString("0", CultureInfo.InvariantCulture) +
                               " GB free of " + totalGb.ToString("0", CultureInfo.InvariantCulture);
                    diskName = d.Name.TrimEnd('\\'); diskFree = freeGb; diskTotal = totalGb;
                    diskPct = totalGb > 0 ? 100.0 * (totalGb - freeGb) / totalGb : -1;
                    line += "     " + diskText;
                }
            }
            catch { }
            return line;
        }

        private static string Uptime(long secs)
        {
            if (secs < 0) secs = 0;
            long d = secs / 86400; secs %= 86400;
            long h = secs / 3600; secs %= 3600;
            long m = secs / 60;
            if (d > 0) return d + "d " + h + "h " + m + "m";
            if (h > 0) return h + "h " + m + "m";
            return m + "m";
        }

        private void ReadHourly()
        {
            if (hourlyFile == null || !File.Exists(hourlyFile)) { hourly = new HourPoint[0]; hourlyKey = ""; return; }
            DateTime stamp = File.GetLastWriteTimeUtc(hourlyFile);
            if (stamp == hourlyStamp) return;
            hourlyStamp = stamp;
            if ((DateTime.UtcNow - stamp).TotalHours > 24) { hourly = new HourPoint[0]; hourlyKey = ""; return; }
            string[] all = File.ReadAllLines(hourlyFile, Encoding.UTF8);
            hourly = ParseHourly(all);
            double la, lo;
            ParseLocation(all, out la, out lo);
            lat = la;
            lon = lo;
            hourlyKey = stamp.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses weather-hourly.txt: "start TAB temperature TAB precipitation % (may be empty) TAB 1|0 (day) TAB
        /// short forecast" per line; '#' lines are comments. Lines that do not parse are skipped.
        /// </summary>
        internal static HourPoint[] ParseHourly(string[] lines)
        {
            List<HourPoint> list = new List<HourPoint>();
            foreach (string raw in lines ?? new string[0])
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split('\t');
                if (p.Length < 5) continue;
                DateTimeOffset start;
                int temp;
                if (!DateTimeOffset.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) continue;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out temp)) continue;
                HourPoint h = new HourPoint();
                h.Start = start.LocalDateTime;
                h.Temp = temp;
                int pop;
                h.Pop = int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pop) ? Math.Max(0, Math.Min(100, pop)) : -1;
                h.IsDay = p[3].Trim() != "0";
                h.Text = p[4].Trim();
                int hum;
                if (p.Length > 5 && int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out hum)) h.Humidity = Math.Max(0, Math.Min(100, hum));
                if (p.Length > 6) h.Wind = p[6].Trim();
                list.Add(h);
            }
            return list.ToArray();
        }

        /// <summary>The "# location lat lon" line of weather-hourly.txt: the forecast grid's centre.</summary>
        internal static bool ParseLocation(string[] lines, out double lat, out double lon)
        {
            lat = lon = double.NaN;
            foreach (string raw in lines ?? new string[0])
            {
                string line = (raw ?? "").Trim();
                if (!line.StartsWith("# location ", StringComparison.Ordinal)) continue;
                string[] p = line.Substring(11).Split(' ');
                if (p.Length == 2 &&
                    double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon) &&
                    Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180) return true;
                lat = lon = double.NaN;
            }
            return false;
        }

        private void ReadWeather()
        {
            if (weatherFile == null || !File.Exists(weatherFile)) { weather = None; return; }
            DateTime stamp = File.GetLastWriteTimeUtc(weatherFile);
            if (stamp == weatherStamp) return;   // unchanged since the last read
            weatherStamp = stamp;
            // Never let a stale forecast linger: drop anything more than a day old.
            if ((DateTime.UtcNow - stamp).TotalHours > 24) { weather = None; return; }
            try
            {
                string[] raw = File.ReadAllLines(weatherFile, Encoding.UTF8);
                List<string> lines = new List<string>();
                foreach (string line in raw)
                {
                    string t = (line ?? "").Trim();
                    if (t.Length > 0) lines.Add(t);
                    if (lines.Count >= 5) break;
                }
                weather = lines.Count > 0 ? lines.ToArray() : None;
            }
            catch { weather = None; }
        }
    }

    /// <summary>One second's readings plus ten minutes of history. Never changed after it is published.</summary>
    internal sealed class SysSnapshot
    {
        public int Version;
        public double Cpu = -1;                     // percent busy; -1 = not known yet
        public double MemPercent = -1, MemUsedGb, MemTotalGb;
        public double NetDown = -1, NetUp = -1;     // bytes per second
        public double DiskUsedPercent = -1, DiskFreeGb, DiskTotalGb;
        public string DiskName = "";
        public long UptimeSeconds = -1;
        public float[] CpuHistory = new float[0];   // oldest first, one a second; negative = no reading
        public float[] NetHistory = new float[0];   // download bytes per second, same spacing
    }

    /// <summary>One hour of the hourly forecast.</summary>
    internal sealed class HourPoint
    {
        public DateTime Start;     // local time
        public int Temp;           // degrees F
        public int Pop = -1;       // chance of precipitation, %; -1 = not given
        public bool IsDay = true;
        public string Text = "";   // short forecast, e.g. "Light Rain Likely"
        public int Humidity = -1;  // relative humidity, %; -1 = not given
        public string Wind = "";   // e.g. "5–10 mph WNW" or "Calm"
    }
}