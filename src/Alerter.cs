using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ClaudeCurtain
{
    /// <summary>
    /// Awareness, not lockout. Sends a fire-and-forget alert through the user's existing notify hook
    /// (which reaches their phone) and writes a line to local\alerts.log for the record and the tests.
    ///
    /// Nothing here blocks, disables or changes anything on the PC, and a failure to alert never touches
    /// the curtain: every path is wrapped and offloaded so it cannot slow or stall the control or UI thread.
    ///
    /// Rate limit: repeat alerts of the same kind within 30 s collapse into one. The repeats are still
    /// counted in the event log, so the "all clear" totals stay accurate; only the phone pings are throttled.
    /// </summary>
    internal sealed class Alerter
    {
        public const int RepeatWindowMs = 30000;
        private const string Event = "curtain_alert";
        private const long TrimAbove = 256 * 1024;
        private const int KeepTail = 160 * 1024;

        private readonly string hookPath;    // notify.ps1, or a test stub; null = log only, still safe
        private readonly string alertsLog;
        private readonly string label;       // becomes the notification's project label
        private readonly object gate = new object();
        private readonly Dictionary<string, long> lastSentMs = new Dictionary<string, long>();
        private readonly Dictionary<string, int> collapsed = new Dictionary<string, int>();
        private readonly Stopwatch clock = Stopwatch.StartNew();

        public Alerter(string hookPath, string alertsLog, string label)
        {
            this.hookPath = string.IsNullOrEmpty(hookPath) ? null : hookPath;
            this.alertsLog = alertsLog;
            this.label = string.IsNullOrEmpty(label) ? "curtain" : label;
        }

        public bool HookEnabled { get { return hookPath != null; } }

        /// <summary>Alert the user about something happening behind the curtain. Never blocks the caller.</summary>
        public void Raise(string kind, string message)
        {
            bool send;
            lock (gate)
            {
                long now = clock.ElapsedMilliseconds;
                long last;
                if (lastSentMs.TryGetValue(kind, out last) && now - last < RepeatWindowMs)
                {
                    send = false;
                    int c;
                    collapsed[kind] = (collapsed.TryGetValue(kind, out c) ? c : 0) + 1;
                }
                else
                {
                    send = true;
                    lastSentMs[kind] = now;
                }
            }

            if (send)
            {
                LogLine("alert", kind, message);
                FireAsync("Curtain: " + message);
            }
            else
            {
                LogLine("collapsed", kind, message + " (within " + (RepeatWindowMs / 1000) + " s of the last, not re-sent)");
            }
        }

        /// <summary>
        /// The single "all clear" sent when the curtain closes cleanly. Runs on the caller's thread because
        /// the process is about to end, but it only starts the hook and returns; it never waits on it.
        /// </summary>
        public void AllClear(string message)
        {
            LogLine("all-clear", "all-clear", message);
            FireNow("Curtain all clear: " + message);
        }

        private void FireAsync(string body)
        {
            try { ThreadPool.QueueUserWorkItem(delegate { FireNow(body); }); }
            catch { FireNow(body); }
        }

        private void FireNow(string body)
        {
            if (hookPath == null) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + hookPath + "\" -Event " + Event);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                Process p = Process.Start(psi);
                // The notify hook reads a Claude Code hook JSON payload from stdin: .message is the body,
                // the leaf of .cwd is the label. session_id keeps it off the shared per-session latch state.
                string payload = "{\"message\":" + Json.Str(body) + ",\"cwd\":" + Json.Str(label) +
                                 ",\"session_id\":\"curtain\"}";
                p.StandardInput.Write(payload);
                p.StandardInput.Close();
                // Deliberately not waited on: the hook runs independently, even as the curtain exits.
            }
            catch (Exception ex)
            {
                LogLine("hook-failed", "hook", ex.Message);
            }
        }

        private void LogLine(string tag, string kind, string message)
        {
            if (alertsLog == null) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" +
                          tag + "\t" + kind + "\t" +
                          (message ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\r\n";
            lock (gate)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Trim();
                        using (FileStream fs = new FileStream(alertsLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(line);
                            fs.Write(bytes, 0, bytes.Length);
                        }
                        return;
                    }
                    catch (IOException) { Thread.Sleep(10); }
                    catch { return; }
                }
            }
        }

        private void Trim()
        {
            FileInfo info = new FileInfo(alertsLog);
            if (!info.Exists || info.Length + 512 <= TrimAbove) return;
            byte[] all = File.ReadAllBytes(alertsLog);
            int from = Math.Max(0, all.Length - KeepTail);
            while (from < all.Length && all[from] != (byte)'\n') from++;
            from = Math.Min(all.Length, from + 1);
            byte[] tail = new byte[all.Length - from];
            Array.Copy(all, from, tail, 0, tail.Length);
            File.WriteAllBytes(alertsLog, tail);
        }
    }
}
