using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClaudeCurtain
{
    internal static class ExitCode
    {
        public const int Ok = 0;
        public const int Error = 1;
        public const int Usage = 2;
        public const int AlreadyRunning = 3;
        public const int NoPin = 4;
        public const int WindowFailed = 5;
        public const int HookFailed = 6;
        public const int WatchdogFailed = 7;
        public const int Crash = 10;
        public const int HookFault = 11;
        public const int UiHung = 12;
        public const int HookThreadHung = 13;
    }

    internal enum Mode { None, Run, Watchdog, SetPin, SelfTest, Diag, Elevated, Summary }

    internal sealed class Options
    {
        public const int MinSeconds = 10;
        public const int MaxSeconds = 24 * 60 * 60;

        public Mode Mode = Mode.None;
        public int Seconds;
        public string LocalDir;
        public string PinFile;
        public string OutFile;
        public string Error;

        // watchdog mode
        public int ParentPid;
        public string BeatName;

        // Automated tests only. None of the scripts a person runs passes these.
        public bool TestPinFromInjected;
        public string TestFreezeTarget;
        public int TestFreezeAfter;
        public string TestKeys;
        public int TestKeysAfter;
        public int TestSecureAt;      // seconds; simulate the security screen opening at this time
        public int TestSecureOnMs;    // how long the simulated security screen stays open

        // The notify hook used for phone/desktop alerts. Empty string or "none" disables hook calls
        // (alerts are still logged). Null means "resolve the default under the user's .claude\hooks".
        public string NotifyHook;

        public string LogFile { get { return Path.Combine(LocalDir, "curtain.log"); } }
        public string StateFile { get { return Path.Combine(LocalDir, "state.json"); } }
        public string LastExitFile { get { return Path.Combine(LocalDir, "last-exit.json"); } }
        public string EventsFile { get { return Path.Combine(LocalDir, "events.log"); } }
        public string SummaryFile { get { return Path.Combine(LocalDir, "last-summary.txt"); } }
        public string AlertsFile { get { return Path.Combine(LocalDir, "alerts.log"); } }

        /// <summary>The notify hook to actually use: the explicit one, the default, or null (log only).</summary>
        public string ResolveNotifyHook()
        {
            string hook = NotifyHook;
            if (hook == null)
            {
                string home = Environment.GetEnvironmentVariable("USERPROFILE");
                if (string.IsNullOrEmpty(home)) return null;
                hook = Path.Combine(home, ".claude", "hooks", "notify.ps1");
            }
            if (hook == "" || string.Equals(hook, "none", StringComparison.OrdinalIgnoreCase)) return null;
            return hook;
        }

        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            string exeDir = Path.GetDirectoryName(ExePath);
            string baseDir = Path.GetDirectoryName(exeDir);
            o.LocalDir = Path.Combine(string.IsNullOrEmpty(baseDir) ? exeDir : baseDir, "local");
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--seconds":
                            o.Seconds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                            if (o.Mode == Mode.None) o.Mode = Mode.Run;
                            break;
                        case "--watchdog": o.Mode = Mode.Watchdog; break;
                        case "--set-pin": o.Mode = Mode.SetPin; break;
                        case "--selftest": o.Mode = Mode.SelfTest; break;
                        case "--diag": o.Mode = Mode.Diag; break;
                        case "--list-elevated": o.Mode = Mode.Elevated; break;
                        case "--show-summary": o.Mode = Mode.Summary; break;
                        case "--pid": o.ParentPid = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--beat": o.BeatName = Next(args, ref i); break;
                        case "--local-dir": o.LocalDir = Next(args, ref i); break;
                        case "--pin-file": o.PinFile = Next(args, ref i); break;
                        case "--out": o.OutFile = Next(args, ref i); break;
                        case "--test-pin-from-injected": o.TestPinFromInjected = true; break;
                        case "--test-freeze": SplitAt(Next(args, ref i), out o.TestFreezeTarget, out o.TestFreezeAfter); break;
                        case "--test-keys": SplitAt(Next(args, ref i), out o.TestKeys, out o.TestKeysAfter); break;
                        case "--test-secure": { string ms; SplitAt(Next(args, ref i), out ms, out o.TestSecureAt); o.TestSecureOnMs = int.Parse(ms, CultureInfo.InvariantCulture); break; }
                        case "--notify-hook": o.NotifyHook = Next(args, ref i); break;
                        default:
                            if (o.Error == null) o.Error = "unknown argument: " + a;
                            break;
                    }
                }
                o.LocalDir = Path.GetFullPath(o.LocalDir).TrimEnd('\\');
                if (o.PinFile == null) o.PinFile = Path.Combine(o.LocalDir, "pin.hash");
            }
            catch (Exception ex)
            {
                o.Error = ex.Message;
            }

            if (o.Error == null)
            {
                if (o.Mode == Mode.Run && (o.Seconds < MinSeconds || o.Seconds > MaxSeconds))
                    o.Error = "--seconds must be between " + MinSeconds + " and " + MaxSeconds;
                if (o.Mode == Mode.Watchdog && (o.ParentPid <= 0 || o.Seconds <= 0 || string.IsNullOrEmpty(o.BeatName)))
                    o.Error = "watchdog mode needs --pid, --seconds and --beat";
            }
            return o;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("missing value after " + args[i]);
            i++;
            return args[i];
        }

        // "value@seconds"
        private static void SplitAt(string text, out string value, out int seconds)
        {
            int at = text.LastIndexOf('@');
            if (at <= 0) throw new ArgumentException("expected value@seconds, got " + text);
            value = text.Substring(0, at);
            seconds = int.Parse(text.Substring(at + 1), CultureInfo.InvariantCulture);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Options o = Options.Parse(args);
            try
            {
                Directory.CreateDirectory(o.LocalDir);
            }
            catch { }

            if (o.Error != null || o.Mode == Mode.None)
            {
                Log.Init(o.LogFile, "main");
                Log.Write("bad arguments (" + (o.Error ?? "no mode given") + "): " + string.Join(" ", args));
                return ExitCode.Usage;
            }

            try
            {
                switch (o.Mode)
                {
                    case Mode.Run:
                        Log.Init(o.LogFile, "main");
                        return CurtainApp.Run(o);
                    case Mode.Watchdog:
                        Log.Init(o.LogFile, "watchdog");
                        return Watchdog.Run(o);
                    case Mode.SetPin:
                        Log.Init(o.LogFile, "setpin");
                        return SetPinDialog.Run(o);
                    case Mode.SelfTest:
                        return SelfTest.Run(o);
                    case Mode.Diag:
                        return Diag.Run(o);
                    case Mode.Elevated:
                        return Diag.ListElevated(o);
                    case Mode.Summary:
                        return SummaryView.Run(o);
                }
                return ExitCode.Usage;
            }
            catch (Exception ex)
            {
                Log.Init(o.LogFile, "main");
                Log.Write("fatal: " + ex);
                return ExitCode.Crash;
            }
        }
    }

    /// <summary>Small append-only log. Never contains PIN digits. Failures to write are ignored.</summary>
    internal static class Log
    {
        private const long TrimAbove = 512 * 1024;
        private const int KeepTail = 128 * 1024;

        private static readonly object gate = new object();
        private static readonly HashSet<string> once = new HashSet<string>();
        private static string path;
        private static string tag = "main";
        private static int pid;

        public static void Init(string file, string who)
        {
            lock (gate)
            {
                if (path != null) return;
                tag = who;
                try { pid = Process.GetCurrentProcess().Id; } catch { }
                try
                {
                    FileInfo info = new FileInfo(file);
                    if (info.Exists && info.Length > TrimAbove)
                    {
                        byte[] all = File.ReadAllBytes(file);
                        byte[] tail = new byte[KeepTail];
                        Array.Copy(all, all.Length - KeepTail, tail, 0, KeepTail);
                        File.WriteAllBytes(file, tail);
                    }
                }
                catch { }
                path = file;
            }
        }

        public static void Write(string message)
        {
            if (path == null) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                          " [" + tag + " " + pid + "] " + message + Environment.NewLine;
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            lock (gate)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                        }
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(15);
                    }
                    catch
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>Logs a message the first time its key is seen, so a repeating problem cannot flood the file.</summary>
        public static void WriteOnce(string key, string message)
        {
            lock (gate)
            {
                if (!once.Add(key)) return;
            }
            Write(message);
        }
    }

    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Utc(DateTime utc)
        {
            return Str(utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Windows integrity level of a process (Medium = 0x2000, High/elevated = 0x3000).</summary>
    internal static class Integrity
    {
        public const int Unknown = -1;

        /// <summary>Returns the integrity RID, or Unknown. accessDenied tells whether the lookup was refused.</summary>
        public static int OfProcess(uint processId, out bool accessDenied)
        {
            accessDenied = false;
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero)
            {
                accessDenied = Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED;
                return Unknown;
            }
            try
            {
                IntPtr token;
                if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out token))
                {
                    accessDenied = Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED;
                    return Unknown;
                }
                try
                {
                    int length;
                    Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                    if (length <= 0) return Unknown;
                    IntPtr buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, length, out length))
                            return Unknown;
                        IntPtr sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid
                        int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                        if (count < 1) return Unknown;
                        return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Native.CloseHandle(token);
                }
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }

        public static int OfCurrentProcess()
        {
            bool denied;
            return OfProcess((uint)Process.GetCurrentProcess().Id, out denied);
        }
    }

    internal static class Monitors
    {
        /// <summary>Bounds of every monitor in physical pixels, asked fresh from Windows on each call.</summary>
        public static List<Rectangle> Enumerate()
        {
            List<Rectangle> list = new List<Rectangle>();
            Native.MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr hdc, ref Native.RECT r, IntPtr data)
            {
                list.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return list;
        }

        public static string Describe(List<Rectangle> list)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Rectangle r in list)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(r.Width).Append('x').Append(r.Height).Append('+').Append(r.X).Append('+').Append(r.Y);   // e.g. 1920x1080+0+0
            }
            return sb.ToString();
        }
    }
}
