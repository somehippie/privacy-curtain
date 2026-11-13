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
            string root = "C:\\";
            try { root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)); }
            catch { }
            systemDrive = string.IsNullOrEmpty(root) ? "C:\\" : root;
        }

        public string StatsLine { get { return statsLine; } }
        public string SysLine { get { return sysLine; } }
        public string[] Weather { get { return weather; } }

        /// <summary>Takes one reading. Call on a timer (about once a second). Never throws.</summary>
        public void Sample()
        {
            try { statsLine = BuildStats(); } catch { }
            try { sysLine = BuildSystem(); } catch { }
            try { ReadWeather(); } catch { }
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
                }
                lastRx = rx; lastTx = tx; lastNetMs = nowMs; haveNet = true;
            }

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
            string line = "Up " + Uptime(secs);
            try
            {
                DriveInfo d = new DriveInfo(systemDrive);
                if (d.IsReady)
                {
                    double freeGb = d.TotalFreeSpace / 1073741824.0;
                    double totalGb = d.TotalSize / 1073741824.0;
                    line += "     " + d.Name.TrimEnd('\\') + " " + freeGb.ToString("0", CultureInfo.InvariantCulture) +
                            " GB free of " + totalGb.ToString("0", CultureInfo.InvariantCulture);
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
}
