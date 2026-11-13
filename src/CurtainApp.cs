using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace ClaudeCurtain
{
    /// <summary>
    /// The curtain itself. Three threads:
    ///   UI thread       the windows, the clock, topmost upkeep
    ///   hook thread     blocks physical input (InputHooks)
    ///   control thread  PIN checks, the timer, the remote stop signal, and health checks on the other two
    /// plus a separate watchdog process that kills this one if it overruns or stops responding.
    ///
    /// The rule everywhere: when anything looks wrong, close the curtain. Being open by mistake costs
    /// some privacy; being stuck closed costs the use of the PC.
    /// </summary>
    internal static class CurtainApp
    {
        public const string MutexName = "Local\\ClaudeCurtain.Instance";
        public const string StopEventName = "Local\\ClaudeCurtain.Stop";
        public const string BeatEventPrefix = "Local\\ClaudeCurtain.Beat.";

        /// <summary>The watchdog's own deadline is this much later than the curtain's, as a backstop.</summary>
        public const int WatchdogGraceSeconds = 10;

        private const int UiStaleLimitSeconds = 8;
        private const int HookStaleLimitSeconds = 6;
        private const int RehookEveryMs = 20000;

        /// <summary>Bumped by the UI timer; the control thread watches it to notice a stuck UI thread.</summary>
        public static volatile int UiBeat;

        /// <summary>Alert the user (phone + log) about something behind the curtain. Safe before setup and from any thread.</summary>
        public static void RaiseAlert(string kind, string message)
        {
            Alerter a = alerter;
            if (a != null) a.Raise(kind, message);
        }

        private static Options options;
        private static Mutex instanceMutex;
        private static EventWaitHandle stopEvent;
        private static EventWaitHandle beatEvent;
        private static Process watchdog;
        private static PinStore pinStore;
        private static PinGate gate;
        private static KeyQueue queue;
        private static InputHooks hooks;
        private static CurtainUi ui;
        private static Alerter alerter;
        private static SysStats sysStats;
        private static DateTime startUtc;
        private static DateTime deadlineUtc;
        private static ulong startTick;
        private static long durationMs;
        private static int exiting;

        public static int Run(Options o)
        {
            options = o;
            Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS | Native.SEM_NOGPFAULTERRORBOX | Native.SEM_NOOPENFILEERRORBOX);
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;

            bool createdNew;
            instanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                Log.Write("a curtain is already running; not starting a second one");
                return ExitCode.AlreadyRunning;
            }

            try
            {
                pinStore = PinStore.Load(o.PinFile);
            }
            catch (Exception ex)
            {
                Log.Write("no usable PIN file at " + o.PinFile + " (" + ex.Message + "); refusing to start without a local unlock");
                return ExitCode.NoPin;
            }

            int pid = Process.GetCurrentProcess().Id;
            string beatName = BeatEventPrefix + pid.ToString(CultureInfo.InvariantCulture);
            bool created;
            stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName, out created);
            stopEvent.Reset();
            beatEvent = new EventWaitHandle(false, EventResetMode.AutoReset, beatName);

            // The event log. First, notice a previous curtain that never logged a close (power cut, crash, kill).
            string openStarted, openLastSeen;
            bool earlierOpen = EventLog.RunIsOpen(o.EventsFile, 0, out openStarted, out openLastSeen);
            EventLog.Init(o.EventsFile, pid);
            if (earlierOpen)
            {
                EventLog.EarlierUnclean = true;
                EventLog.Write("unclean", "an earlier curtain (started " + openStarted + ", last heard from " + openLastSeen +
                               ") ended without a clean close: power cut, crash or kill");
            }

            startUtc = DateTime.UtcNow;
            startTick = Native.GetTickCount64();
            durationMs = o.Seconds * 1000L;
            deadlineUtc = startUtc.AddSeconds(o.Seconds);

            // Listen for shutdown / sign-out on a thread of its own, set up before any window exists, so that
            // the answer to "may Windows shut down?" never depends on the UI thread being healthy.
            Thread subscribe = new Thread(delegate() { SystemEvents.SessionEnding += OnSessionEnding; });
            subscribe.IsBackground = true;
            subscribe.SetApartmentState(ApartmentState.MTA);
            subscribe.Start();
            subscribe.Join(5000);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Options.ExePath,
                    "--watchdog --pid " + pid.ToString(CultureInfo.InvariantCulture) +
                    " --seconds " + (o.Seconds + WatchdogGraceSeconds).ToString(CultureInfo.InvariantCulture) +
                    " --beat \"" + beatName + "\" --local-dir \"" + o.LocalDir + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                watchdog = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Write("could not start the watchdog (" + ex.Message + "); refusing to run without it");
                return ExitCode.WatchdogFailed;
            }

            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

            string hookPath = o.ResolveNotifyHook();
            if (hookPath != null && !File.Exists(hookPath))
            {
                Log.Write("notify hook not found at " + hookPath + "; curtain alerts will be logged only, not sent");
                hookPath = null;
            }
            alerter = new Alerter(hookPath, o.AlertsFile, "curtain");

            queue = new KeyQueue();
            gate = new PinGate(new Func<string, bool>(pinStore.Verify));
            hooks = new InputHooks(queue, o.TestPinFromInjected);

            sysStats = new SysStats(o.LocalDir);
            try { sysStats.Sample(); } catch { }   // seed RAM / uptime / weather for the very first paint

            Application.EnableVisualStyles();
            ui = new CurtainUi(gate, hooks, queue, AutoExitText(), sysStats);
            string error = ui.Create();
            if (error != null)
            {
                Log.Write("window setup failed: " + error);
                StopWatchdog();
                return ExitCode.WindowFailed;
            }

            error = hooks.Start(5000);
            if (error != null)
            {
                Log.Write("input hooks failed: " + error);
                hooks.Stop(300);
                StopWatchdog();
                return ExitCode.HookFailed;
            }

            WriteState(pid);
            Log.Write("ready: " + o.Seconds + " s, monitors " + ui.Describe() +
                      ", closes at " + deadlineUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                      ", watchdog pid " + watchdog.Id +
                      (o.TestPinFromInjected ? ", TEST: PIN accepted from injected keys" : "") +
                      (o.TestFreezeTarget != null ? ", TEST: freeze " + o.TestFreezeTarget + "@" + o.TestFreezeAfter : "") +
                      (o.TestKeys != null ? ", TEST: scripted keys@" + o.TestKeysAfter : ""));

            EventLog.Write("start", "curtain up on " + ui.MonitorCount + " monitor(s), timer " + EventLog.Span(o.Seconds) +
                           ", closes by itself at " + deadlineUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            try
            {
                NetworkChange.NetworkAvailabilityChanged += delegate(object s, NetworkAvailabilityEventArgs e)
                {
                    if (e.IsAvailable) { EventLog.NetworkUp++; EventLog.Write("network", "network reconnected"); }
                    else { EventLog.NetworkDown++; EventLog.Write("network", "network disconnected"); }
                };
            }
            catch (Exception ex)
            {
                Log.Write("could not watch the network: " + ex.Message);
            }

            Thread control = new Thread(ControlLoop);
            control.IsBackground = true;
            control.Name = "curtain-control";
            control.Start();

            ui.Start();
            Application.Run();

            ExitNow("message-loop-ended", ExitCode.Crash);
            return ExitCode.Crash;
        }

        /// <summary>
        /// Closes the curtain. Safe to call from any thread, any number of times. Input is released first,
        /// then the process is terminated outright; a second timer thread guarantees that happens even if
        /// a file write or the unhook call stalls.
        /// </summary>
        public static void ExitNow(string reason, int code)
        {
            if (Interlocked.CompareExchange(ref exiting, 1, 0) != 0) return;

            try
            {
                Thread failsafe = new Thread(delegate()
                {
                    Thread.Sleep(2500);
                    Native.TerminateProcess(Native.GetCurrentProcess(), (uint)code);
                });
                failsafe.IsBackground = true;
                failsafe.Priority = ThreadPriority.Highest;
                failsafe.Start();
            }
            catch { }

            try { if (hooks != null) hooks.Stop(300); } catch { }
            try { Log.Write("exit: " + reason + " | " + Stats()); } catch { }
            try
            {
                FlushInputCounts(false);
                string how = HowClosed(reason);
                EventLog.Write("stop", how);
                File.WriteAllText(options.SummaryFile, EventLog.BuildSummary(startUtc.ToLocalTime(), how));
                // One "all clear" on a clean close, with the totals. Faults and kills skip it (and usually
                // cannot send anyway); the next start still logs an unclean earlier ending.
                if (alerter != null && IsCleanClose(reason))
                {
                    alerter.AllClear("curtain closed by " + how + " after " +
                                     EventLog.Span((DateTime.UtcNow - startUtc).TotalSeconds) + ". " + EventLog.CountsLine());
                }
                if (reason == "pin")
                {
                    // A separate short-lived process shows the summary; this one is about to end.
                    ProcessStartInfo psi = new ProcessStartInfo(Options.ExePath, "--show-summary --local-dir \"" + options.LocalDir + "\"");
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
            }
            catch { }
            try { WriteLastExit(reason, code); } catch { }
            try { if (options != null) File.Delete(options.StateFile); } catch { }

            Native.TerminateProcess(Native.GetCurrentProcess(), (uint)code);
            Thread.Sleep(Timeout.Infinite);
        }

        private static void ControlLoop()
        {
            try
            {
                // Keep-awake: while the curtain is up the PC must not sleep, hibernate, blank the display or
                // start a screen saver, because any of those can end in the Windows lock screen, where Claude
                // cannot work. This is a request held by this thread, not a settings change: it disappears
                // the instant the process ends, and the normal lock and sleep settings apply again.
                uint previous = Native.SetThreadExecutionState(
                    Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
                Log.Write(previous != 0 ? "keep-awake request is held (sleep, display-off and screen saver are on hold)"
                                        : "warning: the keep-awake request was refused");

                WaitHandle[] handles = new WaitHandle[] { stopEvent, queue.Signal };
                Stopwatch clock = Stopwatch.StartNew();
                long lastSecond = -1;
                long lastRehookMs = 0;
                int lastUiBeat = UiBeat;
                int uiStale = 0;
                int lastAck = hooks.Ack;
                int hookStale = 0;
                bool freezeDone = false;
                bool keysDone = false;
                int loggedWrong = 0;
                bool onSecureDesktop = false;
                long lastMinute = 0;
                bool testSecureFired = false;
                long testSecureUntil = 0;

                while (true)
                {
                    int signalled = WaitHandle.WaitAny(handles, 250);
                    if (signalled == 0)
                    {
                        ExitNow("remote-stop", ExitCode.Ok);
                        return;
                    }

                    long now = clock.ElapsedMilliseconds;

                    int code;
                    while (queue.TryPop(out code))
                    {
                        bool submit = (code & KeyQueue.FlagSubmit) != 0;
                        if (gate.OnKey(code & 0xFF, (code & KeyQueue.FlagUp) != 0, submit, now))
                        {
                            // The hotkey fallback fires on key-down: let Enter come back up before leaving.
                            if (submit) WaitForRelease(Native.VK_RETURN, 1500);
                            ExitNow("pin", ExitCode.Ok);
                            return;
                        }
                    }
                    gate.Tick(now);

                    try { beatEvent.Set(); } catch { }

                    // The timer. Two independent clocks, either one ends the curtain: wall-clock time (so time
                    // spent asleep counts) and the system tick count (so a changed clock cannot extend it).
                    if (DateTime.UtcNow >= deadlineUtc || (long)(Native.GetTickCount64() - startTick) >= durationMs)
                    {
                        ExitNow("timer", ExitCode.Ok);
                        return;
                    }

                    long second = now / 1000;
                    if (second == lastSecond) continue;
                    lastSecond = second;

                    try { if (sysStats != null) sysStats.Sample(); } catch { }   // refresh the on-screen stats

                    if (watchdog.HasExited)
                    {
                        ExitNow("watchdog-lost", ExitCode.Ok);
                        return;
                    }

                    // ---- event log upkeep ----
                    int wrong = gate.WrongTotal;
                    if (wrong != loggedWrong)
                    {
                        loggedWrong = wrong;
                        EventLog.WrongPin = wrong;
                        EventLog.Write("wrong-pin", "wrong PIN entered (attempt " + wrong + " of this curtain)");
                    }
                    IntPtr desk = Native.OpenInputDesktop(0, false, 0x0100);
                    bool secure = desk == IntPtr.Zero;
                    if (!secure) Native.CloseDesktop(desk);
                    // Test seam: pretend the security screen opens at a set time, for the chosen span, so the
                    // open-alert and close-reassert paths run without a real Ctrl+Alt+Del.
                    if (options.TestSecureAt > 0 && !testSecureFired && now >= options.TestSecureAt * 1000L)
                    {
                        testSecureFired = true;
                        testSecureUntil = now + options.TestSecureOnMs;
                        Log.Write("TEST: simulating the security screen for " + options.TestSecureOnMs + " ms");
                    }
                    if (now < testSecureUntil) secure = true;
                    if (secure != onSecureDesktop)
                    {
                        onSecureDesktop = secure;
                        if (secure)
                        {
                            EventLog.SecurityScreen++;
                            EventLog.Write("security-screen", "Windows security screen opened (Ctrl+Alt+Del, lock screen or an administrator prompt)");
                            RaiseAlert("security-screen", "the Windows security screen opened (Ctrl+Alt+Del, lock screen or an admin prompt)");
                        }
                        else
                        {
                            EventLog.Write("security-screen", "Windows security screen closed, back to the curtain");
                            ui.RequestReassert();   // bring the black screen back on top, without stealing focus
                        }
                    }
                    long minute = now / 60000;
                    if (minute != lastMinute)
                    {
                        lastMinute = minute;
                        FlushInputCounts(true);
                    }
                    if (hooks.Faulted)
                    {
                        ExitNow("hook-fault", ExitCode.HookFault);
                        return;
                    }
                    if (hooks.RehookFailures >= 3)
                    {
                        ExitNow("hook-reinstall-failed", ExitCode.HookFault);
                        return;
                    }

                    // Staleness is counted in loop passes, not elapsed time, so waking from sleep is not a "hang".
                    int beat = UiBeat;
                    if (beat == lastUiBeat) uiStale++;
                    else { uiStale = 0; lastUiBeat = beat; }
                    if (uiStale >= UiStaleLimitSeconds)
                    {
                        ExitNow("ui-thread-hung", ExitCode.UiHung);
                        return;
                    }

                    int ack = hooks.Ack;
                    if (ack == lastAck) hookStale++;
                    else { hookStale = 0; lastAck = ack; }
                    if (hookStale >= HookStaleLimitSeconds)
                    {
                        ExitNow("hook-thread-hung", ExitCode.HookThreadHung);
                        return;
                    }
                    hooks.Ping();

                    if (now - lastRehookMs >= RehookEveryMs)
                    {
                        lastRehookMs = now;
                        hooks.RequestRehook();
                    }

                    // ---- automated tests only ----
                    if (options.TestKeys != null && !keysDone && now >= options.TestKeysAfter * 1000L)
                    {
                        keysDone = true;
                        Log.Write("TEST: feeding scripted keys");
                        foreach (string part in options.TestKeys.Split(','))
                        {
                            int vk = int.Parse(part.Trim(), CultureInfo.InvariantCulture);
                            queue.Push(vk);
                            if (vk == Native.VK_RETURN) queue.Push(vk | KeyQueue.FlagUp);
                        }
                    }
                    if (options.TestFreezeTarget != null && !freezeDone && now >= options.TestFreezeAfter * 1000L)
                    {
                        freezeDone = true;
                        Log.Write("TEST: freezing " + options.TestFreezeTarget);
                        if (options.TestFreezeTarget == "ui") ui.FreezeForTest();
                        else if (options.TestFreezeTarget == "hook") hooks.FreezeForTest();
                        else if (options.TestFreezeTarget == "control") Thread.Sleep(Timeout.Infinite);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("control thread failed: " + ex);
                ExitNow("control-thread-failed", ExitCode.Crash);
            }
        }

        private static void WaitForRelease(int vk, int maxMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxMs && (Native.GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                Thread.Sleep(15);
            }
        }

        private static void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            SessionEnding(e.Reason == SessionEndReasons.Logoff);
        }

        /// <summary>Windows asked apps to close for a sign-out, or for a shutdown or restart.</summary>
        public static void SessionEnding(bool signOut)
        {
            if (Interlocked.CompareExchange(ref sessionEndLogged, 1, 0) == 0)
            {
                if (signOut) EventLog.Write("signout", "sign-out requested");
                else EventLog.Write("shutdown", "shutdown or restart requested (for example the power button)");
                sessionEndSignOut = signOut;
            }
            ExitNow("windows-session-ending", ExitCode.Ok);
        }

        private static int sessionEndLogged;
        private static bool sessionEndSignOut;
        private static long countedKeys;
        private static long countedClicks;
        private static long countedMoves;

        private static bool IsCleanClose(string reason)
        {
            return reason == "pin" || reason == "remote-stop" || reason == "timer" || reason == "windows-session-ending";
        }

        private static string HowClosed(string reason)
        {
            switch (reason)
            {
                case "pin": return "PIN";
                case "remote-stop": return "curtain-off";
                case "timer": return "timer";
                case "windows-session-ending": return sessionEndSignOut ? "Windows sign-out" : "Windows shutdown or restart";
                case "window-closed": return "close request (taskkill)";
                case "watchdog-lost": return "fault: its watchdog process disappeared";
                default: return "fault: " + reason;
            }
        }

        /// <summary>Records physical keyboard and mouse use since the last call, as counts only.</summary>
        private static void FlushInputCounts(bool heartbeat)
        {
            if (hooks == null) return;
            long keys = hooks.KeyPresses, clicks = hooks.ButtonPresses, moves = hooks.Moves;
            long dk = keys - countedKeys, dc = clicks - countedClicks, dm = moves - countedMoves;
            countedKeys = keys; countedClicks = clicks; countedMoves = moves;
            if (dk > 0 || dc > 0 || dm > 0)
            {
                EventLog.KeyPresses += dk;
                EventLog.Clicks += dc;
                if (dm > 0) EventLog.MouseMoved = true;
                EventLog.InputMinutes++;
                EventLog.Write("input", "physical input blocked in the last minute: " + dk + " key presses, " + dc +
                               " mouse clicks" + (dm > 0 ? ", mouse moved" : ""));
            }
            else if (heartbeat)
            {
                EventLog.Write("heartbeat", "curtain up, no physical input");
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try { Log.Write("unhandled exception: " + e.ExceptionObject); } catch { }
            ExitNow("crash", ExitCode.Crash);
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            try { Log.Write("unhandled UI exception: " + e.Exception); } catch { }
            ExitNow("crash", ExitCode.Crash);
        }

        private static void StopWatchdog()
        {
            try { if (watchdog != null && !watchdog.HasExited) watchdog.Kill(); } catch { }
        }

        private static string AutoExitText()
        {
            DateTime local = deadlineUtc.ToLocalTime();
            string when = local.ToString("t");
            if (local.Date != DateTime.Now.Date) when += " (" + local.ToString("ddd") + ")";
            return "This curtain closes by itself at " + when + ".";
        }

        private static string Stats()
        {
            long elapsed = (long)(Native.GetTickCount64() - startTick) / 1000;
            return "elapsed=" + elapsed + "s " + (hooks != null ? hooks.Stats() : "") +
                   " wrongPin=" + (gate != null ? gate.WrongTotal : 0);
        }

        private static void WriteState(int pid)
        {
            string json = "{" +
                "\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + "," +
                "\"watchdogPid\":" + watchdog.Id.ToString(CultureInfo.InvariantCulture) + "," +
                "\"startedUtc\":" + Json.Utc(startUtc) + "," +
                "\"deadlineUtc\":" + Json.Utc(deadlineUtc) + "," +
                "\"seconds\":" + options.Seconds.ToString(CultureInfo.InvariantCulture) + "," +
                "\"monitors\":" + ui.MonitorCount.ToString(CultureInfo.InvariantCulture) + "," +
                "\"layout\":" + Json.Str(ui.Describe()) +
                "}";
            try { File.WriteAllText(options.StateFile, json); }
            catch (Exception ex) { Log.Write("could not write the state file: " + ex.Message); }
        }

        private static void WriteLastExit(string reason, int code)
        {
            string json = "{" +
                "\"reason\":" + Json.Str(reason) + "," +
                "\"code\":" + code.ToString(CultureInfo.InvariantCulture) + "," +
                "\"atUtc\":" + Json.Utc(DateTime.UtcNow) + "," +
                "\"startedUtc\":" + Json.Utc(startUtc) + "," +
                "\"wrongPinAttempts\":" + (gate != null ? gate.WrongTotal : 0).ToString(CultureInfo.InvariantCulture) +
                "}";
            File.WriteAllText(options.LastExitFile, json);
        }
    }

    /// <summary>
    /// A second, tiny process whose only job is to make sure the curtain ends. It kills the curtain if the
    /// curtain outlives its timer (by a few seconds' grace) or stops sending heartbeats, and it leaves as
    /// soon as the curtain is gone. The curtain in turn closes if this process disappears.
    /// </summary>
    internal static class Watchdog
    {
        private const int MissLimit = 20;   // seconds without a heartbeat

        /// <summary>The curtain is gone. If it never logged a close, it was killed or crashed: record that.</summary>
        private static int ParentGone(Options o)
        {
            try
            {
                Thread.Sleep(250);
                string started, lastSeen;
                if (EventLog.RunIsOpen(o.EventsFile, o.ParentPid, out started, out lastSeen))
                {
                    EventLog.Write("killed", "the curtain process was killed or crashed without a clean close");
                }
            }
            catch { }
            return ExitCode.Ok;
        }

        public static int Run(Options o)
        {
            Process parent;
            try
            {
                parent = Process.GetProcessById(o.ParentPid);
            }
            catch
            {
                return ExitCode.Ok;
            }

            try
            {
                if (!string.Equals(parent.MainModule.FileName, Options.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write("pid " + o.ParentPid + " is not a curtain; leaving");
                    return ExitCode.Ok;
                }
            }
            catch { }

            EventWaitHandle beat = null;
            try
            {
                beat = EventWaitHandle.OpenExisting(o.BeatName);
            }
            catch (Exception ex)
            {
                Log.Write("no heartbeat event (" + ex.Message + "); watching the deadline only");
            }

            ulong startTick = Native.GetTickCount64();
            long limitMs = o.Seconds * 1000L;
            DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(o.Seconds);
            int misses = 0;
            Log.Write("watching curtain pid " + o.ParentPid + ", hard limit " + o.Seconds + " s");
            EventLog.Init(o.EventsFile, o.ParentPid);

            while (true)
            {
                bool beaten = true;
                try
                {
                    // One second per pass, but notice the curtain's end within a quarter of that.
                    beaten = false;
                    for (int i = 0; i < 4; i++)
                    {
                        if (parent.HasExited) return ParentGone(o);
                        if (beat != null) { if (beat.WaitOne(250)) beaten = true; }
                        else { Thread.Sleep(250); beaten = true; }
                    }
                    if (parent.HasExited) return ParentGone(o);
                }
                catch (Exception ex)
                {
                    Log.WriteOnce("watch", "watchdog check failed: " + ex.Message);
                    Thread.Sleep(1000);
                }

                misses = beaten ? 0 : misses + 1;

                string why = null;
                if (misses >= MissLimit) why = "heartbeat-lost";
                else if (DateTime.UtcNow >= deadlineUtc || (long)(Native.GetTickCount64() - startTick) >= limitMs) why = "deadline";
                if (why == null) continue;

                Log.Write("killing curtain pid " + o.ParentPid + ": " + why);
                EventLog.Write("stop", why == "deadline" ? "watchdog (the curtain overran its timer)"
                                                         : "watchdog (the curtain stopped responding)");
                try
                {
                    parent.Kill();
                    parent.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Write("kill failed: " + ex.Message);
                }
                try
                {
                    File.WriteAllText(o.LastExitFile, "{\"reason\":" + Json.Str("watchdog-kill:" + why) +
                                      ",\"code\":-1,\"atUtc\":" + Json.Utc(DateTime.UtcNow) + "}");
                }
                catch { }
                try { File.Delete(o.StateFile); } catch { }
                return ExitCode.Ok;
            }
        }
    }
}
