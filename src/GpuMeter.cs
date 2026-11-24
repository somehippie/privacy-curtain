using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace ClaudeCurtain
{
    /// <summary>
    /// How busy the graphics card is, and how much of its own memory is in use, read once a second on a thread
    /// of its own (2026-11-24, the user: "can you fit GPU usage into the system stats on the curtain?").
    ///
    /// The busy figure is Task Manager's: Windows' "GPU Engine" counters give each process's share of each
    /// engine (3D, copy, video decode and so on); the shares are added up per engine, and the busiest engine is
    /// the GPU's figure. Its memory comes from the "GPU Adapter Memory" counters (dedicated memory in use), and
    /// its size from the display driver's entry in the registry. None of it needs administrator rights.
    ///
    /// Reading the engine counters can take tens of milliseconds on a busy machine (one instance per process
    /// and engine), which is why it never runs on the control thread that carries the PIN keys. The query is
    /// rebuilt every minute so processes that start later are counted. Never throws; a reading that fails is
    /// -1 and shows as "--".
    /// </summary>
    internal sealed class GpuMeter
    {
        private volatile float busy = -1;      // percent, -1 = not known
        private volatile float vramUsed = -1;  // GB
        private readonly double vramTotal;     // GB, 0 = not known
        private Thread thread;

        public GpuMeter()
        {
            vramTotal = ReadVramTotalGb();
        }

        public double Busy { get { return busy; } }
        public double VramUsedGb { get { return vramUsed; } }
        public double VramTotalGb { get { return vramTotal; } }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Name = "gpu";
            thread.Start();
        }

        private void Run()
        {
            IntPtr query = IntPtr.Zero, engines = IntPtr.Zero, memory = IntPtr.Zero;
            DateTime opened = DateTime.MinValue;
            int failures = 0;
            while (true)
            {
                try
                {
                    if (query == IntPtr.Zero || DateTime.UtcNow - opened > TimeSpan.FromMinutes(1))
                    {
                        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
                        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; throw new InvalidOperationException(); }
                        if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engines) != 0) engines = IntPtr.Zero;
                        if (PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out memory) != 0) memory = IntPtr.Zero;
                        if (engines == IntPtr.Zero && memory == IntPtr.Zero) throw new InvalidOperationException();
                        PdhCollectQueryData(query);   // the first collection only sets the start of the interval
                        opened = DateTime.UtcNow;
                        Thread.Sleep(1000);
                    }
                    if (PdhCollectQueryData(query) != 0) throw new InvalidOperationException();
                    if (engines != IntPtr.Zero)
                    {
                        // Per engine (adapter, physical GPU, engine number): add up every process's share.
                        Dictionary<string, double> perEngine = new Dictionary<string, double>();
                        foreach (KeyValuePair<string, double> v in Read(engines))
                        {
                            int at = v.Key.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                            string key = at >= 0 ? v.Key.Substring(at) : v.Key;
                            double sum;
                            perEngine.TryGetValue(key, out sum);
                            perEngine[key] = sum + v.Value;
                        }
                        double top = 0;
                        foreach (double v in perEngine.Values) top = Math.Max(top, v);
                        busy = (float)Math.Max(0, Math.Min(100, top));
                    }
                    if (memory != IntPtr.Zero)
                    {
                        double most = -1;
                        foreach (KeyValuePair<string, double> v in Read(memory)) most = Math.Max(most, v.Value);
                        vramUsed = most >= 0 ? (float)(most / 1073741824.0) : -1;
                    }
                    failures = 0;
                }
                catch
                {
                    busy = -1; vramUsed = -1;
                    if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } query = IntPtr.Zero; }
                    // No counters here (an old Windows, a remote session without a GPU): try again now and then.
                    if (++failures > 3) { Thread.Sleep(60000); continue; }
                }
                Thread.Sleep(1000);
            }
        }

        /// <summary>Every instance of a wildcard counter with a valid value, by instance name.</summary>
        private static List<KeyValuePair<string, double>> Read(IntPtr counter)
        {
            List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
            uint size = 0, count;
            int rc = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return list;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf);
                if (rc != 0) return list;
                int step = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM));
                for (int i = 0; i < count; i++)
                {
                    PDH_FMT_COUNTERVALUE_ITEM item = (PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + (long)i * step), typeof(PDH_FMT_COUNTERVALUE_ITEM));
                    if (item.CStatus != 0 && item.CStatus != PDH_CSTATUS_NEW_DATA) continue;   // a process that just started
                    if (double.IsNaN(item.Value) || item.Value < 0) continue;
                    list.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        /// <summary>The largest display adapter's own memory, from its driver's registry entry (GB; 0 when unknown).</summary>
        private static double ReadVramTotalGb()
        {
            double most = 0;
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (string name in cls.GetSubKeyNames())
                    {
                        if (name.Length != 4) continue;   // 0000, 0001, ... (not "Properties")
                        try
                        {
                            using (RegistryKey k = cls.OpenSubKey(name))
                            {
                                object v = k == null ? null : k.GetValue("HardwareInformation.qwMemorySize");
                                if (v is long) most = Math.Max(most, (long)v / 1073741824.0);
                                else if (v is byte[] && ((byte[])v).Length >= 8) most = Math.Max(most, BitConverter.ToInt64((byte[])v, 0) / 1073741824.0);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return most;
        }

        // ------------------------------------------------------------------ PDH

        private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

        // Nested as in the C headers: the value struct is aligned to 8 inside the item, in 32- and 64-bit processes.
        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double Value;   // the union's double
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr Name;
            public PDH_FMT_COUNTERVALUE Fmt;
            public uint CStatus { get { return Fmt.CStatus; } }
            public double Value { get { return Fmt.Value; } }
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern int PdhCloseQuery(IntPtr query);
    }
}
