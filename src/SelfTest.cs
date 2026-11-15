using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace ClaudeCurtain
{
    /// <summary>Logic tests that need no screen, no hooks and no input. Run with --selftest.</summary>
    internal static class SelfTest
    {
        private static readonly List<string> lines = new List<string>();
        private static int failures;

        public static int Run(Options o)
        {
            try
            {
                TestPinStore(o);
                TestPinGate();
                TestKeyQueue();
                TestAlerter(o);
                TestSysStats(o);
                TestPainter(o);
                TestEnvironment();
            }
            catch (Exception ex)
            {
                Check("no unexpected exception (" + ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            lines.Add(failures == 0 ? "RESULT: all self-tests passed" : "RESULT: " + failures + " self-test(s) FAILED");
            return Diag.Emit(o, lines, failures == 0 ? ExitCode.Ok : ExitCode.Error);
        }

        private static void Check(string name, bool ok)
        {
            lines.Add((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failures++;
        }

        private static void TestPinStore(Options o)
        {
            string file = Path.Combine(o.LocalDir, "selftest-pin-" + Process.GetCurrentProcess().Id + ".hash");
            try
            {
                PinStore.Save(file, "482159");
                string text = File.ReadAllText(file);
                Check("pin file has the v1 pbkdf2 format", text.StartsWith("v1$pbkdf2-sha256$") && text.Trim().Split('$').Length == 5);
                Check("pin file does not contain the PIN", text.IndexOf("482159", StringComparison.Ordinal) < 0);

                PinStore store = PinStore.Load(file);
                Stopwatch sw = Stopwatch.StartNew();
                bool right = store.Verify("482159");
                sw.Stop();
                Check("correct PIN verifies", right);
                Check("wrong PIN is rejected", !store.Verify("482158"));
                Check("PIN with extra digit is rejected", !store.Verify("4821590"));
                Check("empty PIN is rejected", !store.Verify(""));
                lines.Add("INFO  one PIN check takes " + sw.ElapsedMilliseconds + " ms");
                Check("a PIN check is fast enough to feel instant (under 400 ms)", sw.ElapsedMilliseconds < 400);

                PinStore.Save(file, "482159");
                string second = File.ReadAllText(file);
                Check("saving the same PIN twice gives a different salt", second != text);
                Check("overwritten file still verifies", PinStore.Load(file).Verify("482159"));

                File.WriteAllText(file, "not a pin file");
                bool threw = false;
                try { PinStore.Load(file); } catch (FormatException) { threw = true; }
                Check("garbage PIN file is refused", threw);
            }
            finally
            {
                try { File.Delete(file); } catch { }
                try { File.Delete(file + ".tmp"); } catch { }
            }

            string why;
            Check("3 digits is too short", !PinStore.IsValidPin("123", out why));
            Check("4 digits is accepted", PinStore.IsValidPin("1234", out why));
            Check("12 digits is accepted", PinStore.IsValidPin("123456789012", out why));
            Check("13 digits is too long", !PinStore.IsValidPin("1234567890123", out why));
            Check("letters are refused", !PinStore.IsValidPin("12a4", out why));
            Check("null is refused", !PinStore.IsValidPin(null, out why));
        }

        /// <summary>
        /// Drives a PinGate with a fake clock. Script characters:
        ///   0-9 top-row digit   n0-n9 numpad digit   E Enter down+up   d Enter down only   u Enter up only
        ///   B Backspace   X Esc   S hotkey-style submit
        /// </summary>
        private sealed class Driver
        {
            public readonly PinGate Gate;
            public long Now = 1000;
            public bool Unlocked;
            public int VerifyCalls;
            public bool Counted;   // feed key-downs as if the hooks had counted them (physical keys)

            public Driver(string pin)
            {
                string expected = pin;
                Gate = new PinGate(delegate(string candidate) { VerifyCalls++; return candidate == expected; });
            }

            public Driver Type(string script)
            {
                for (int i = 0; i < script.Length; i++)
                {
                    char c = script[i];
                    Now += 120;
                    if (c >= '0' && c <= '9') { Key(0x30 + (c - '0'), false, false); Key(0x30 + (c - '0'), true, false); }
                    else if (c == 'n') { i++; int vk = 0x60 + (script[i] - '0'); Key(vk, false, false); Key(vk, true, false); }
                    else if (c == 'E') { Key(Native.VK_RETURN, false, false); Now += 60; Key(Native.VK_RETURN, true, false); }
                    else if (c == 'd') Key(Native.VK_RETURN, false, false);
                    else if (c == 'u') Key(Native.VK_RETURN, true, false);
                    else if (c == 'B') Key(Native.VK_BACK, false, false);
                    else if (c == 'X') Key(Native.VK_ESCAPE, false, false);
                    else if (c == 'S') Key(Native.VK_RETURN, false, true);
                }
                return this;
            }

            public Driver Wait(long ms)
            {
                Now += ms;
                Gate.Tick(Now);
                return this;
            }

            private void Key(int vk, bool up, bool submit)
            {
                if (Gate.OnKey(vk, up, submit, Counted && !up && !submit, Now)) Unlocked = true;
            }
        }

        private static void TestPinGate()
        {
            Check("gate: correct PIN then Enter unlocks", new Driver("4821").Type("4821E").Unlocked);
            Check("gate: correct PIN without Enter does not unlock", !new Driver("4821").Type("4821").Unlocked);
            Check("gate: unlock happens on Enter release, not press", !new Driver("4821").Type("4821d").Unlocked);
            Check("gate: Enter press then release unlocks", new Driver("4821").Type("4821du").Unlocked);
            Check("gate: a stray Enter release does nothing", !new Driver("4821").Type("4821u").Unlocked);
            Check("gate: numpad digits work", new Driver("4821").Type("n4n8n2n1E").Unlocked);
            Check("gate: mixed numpad and top row work", new Driver("4821").Type("4n82n1E").Unlocked);
            Check("gate: wrong PIN does not unlock", !new Driver("4821").Type("4822E").Unlocked);
            Check("gate: wrong then right unlocks", new Driver("4821").Type("1111E4821E").Unlocked);
            Check("gate: Backspace corrects a typo", new Driver("4821").Type("4822B1E").Unlocked);
            Check("gate: Esc clears what was typed", new Driver("4821").Type("99X4821E").Unlocked);
            Check("gate: a prefix of the PIN does not unlock", !new Driver("4821").Type("482E").Unlocked);
            Check("gate: the PIN followed by extra digits does not unlock", !new Driver("4821").Type("48215E").Unlocked);
            Check("gate: hotkey-style submit unlocks", new Driver("4821").Type("4821S").Unlocked);

            Driver empty = new Driver("4821").Type("EEE");
            Check("gate: Enter with nothing typed is not an attempt", empty.Gate.WrongTotal == 0 && empty.VerifyCalls == 0);

            Driver shortTry = new Driver("4821").Type("12E");
            Check("gate: a too-short entry counts as wrong without hashing", shortTry.Gate.WrongTotal == 1 && shortTry.VerifyCalls == 0);

            Driver idle = new Driver("4821").Type("48").Wait(PinGate.IdleClearMs + 500).Type("21E");
            Check("gate: a half-typed PIN is forgotten after a pause", !idle.Unlocked && idle.Gate.WrongTotal == 1);
            Check("gate: typing resumes normally after the pause", idle.Type("4821E").Unlocked);

            Driver states = new Driver("4821");
            Check("gate: starts idle", states.Gate.State == PinGate.StateIdle && states.Gate.Digits == 0);
            states.Type("48");
            Check("gate: shows typing with a digit count", states.Gate.State == PinGate.StateTyping && states.Gate.Digits == 2);
            states.Type("B");
            Check("gate: Backspace lowers the digit count", states.Gate.Digits == 1);
            states.Type("B");
            Check("gate: deleting everything returns to idle", states.Gate.State == PinGate.StateIdle && states.Gate.Digits == 0);
            states.Type("1111E");
            Check("gate: a wrong PIN shows the wrong-PIN note", states.Gate.State == PinGate.StateWrong && states.Gate.Digits == 0);
            states.Wait(PinGate.WrongMessageMs + 100);
            Check("gate: the wrong-PIN note clears by itself", states.Gate.State == PinGate.StateIdle);

            Driver cool = new Driver("4821").Type("1111E2222E3333E4444E");
            Check("gate: four wrong tries do not start a cooldown", cool.Gate.State != PinGate.StateCooldown);
            cool.Type("5555E");
            Check("gate: the fifth wrong try starts a cooldown", cool.Gate.State == PinGate.StateCooldown && cool.Gate.CooldownSecondsLeft > 0);
            cool.Type("4821E");
            Check("gate: the right PIN is ignored during the cooldown", !cool.Unlocked);
            cool.Wait(PinGate.CooldownMs + 100);
            Check("gate: the cooldown ends by itself after 30 s", cool.Gate.State == PinGate.StateIdle);
            Check("gate: the right PIN works after the cooldown", cool.Type("4821E").Unlocked);

            Driver flood = new Driver("4821").Type("123456789012345678901234567890E");
            Check("gate: an over-long entry is rejected safely", !flood.Unlocked && flood.Gate.WrongTotal == 1 && flood.VerifyCalls == 0);
            Check("gate: normal entry works after an over-long one", flood.Type("4821E").Unlocked);

            Driver overflowThenClear = new Driver("4821").Type("123456789012345678X4821E");
            Check("gate: Esc recovers from an over-long entry", overflowThenClear.Unlocked);

            Driver jump = new Driver("4821").Type("48").Wait(10L * 60 * 60 * 1000).Type("4821E");
            Check("gate: a long sleep mid-entry leaves the gate usable", jump.Unlocked);

            // Which key presses belong to the unlocking entry (left out of the blocked-key tally).
            Driver own = new Driver("4821") { Counted = true }.Type("4821E");
            Check("gate: the unlocking entry is digits plus Enter", own.Unlocked && own.Gate.UnlockKeys == 5);
            Driver typo = new Driver("4821") { Counted = true }.Type("4822B1E");
            Check("gate: a corrected typo belongs to the unlocking entry", typo.Unlocked && typo.Gate.UnlockKeys == 7);
            Driver afterWrong = new Driver("4821") { Counted = true }.Type("1111E99X4821E");
            Check("gate: wrong tries and cleared entries are not part of the unlock",
                  afterWrong.Unlocked && afterWrong.Gate.UnlockKeys == 5);
            Driver held = new Driver("4821") { Counted = true }.Type("4821ddddu");
            Check("gate: a held Enter's repeats belong to the unlocking entry", held.Unlocked && held.Gate.UnlockKeys == 8);
            Driver notCounted = new Driver("4821").Type("4821E");
            Check("gate: keys the hooks did not count are not taken off", notCounted.Unlocked && notCounted.Gate.UnlockKeys == 0);
            Driver noUnlock = new Driver("4821") { Counted = true }.Type("4821");
            Check("gate: nothing is taken off without an unlock", noUnlock.Gate.UnlockKeys == 0);
        }

        private static void TestKeyQueue()
        {
            KeyQueue q = new KeyQueue();
            int code;
            Check("queue: starts empty", !q.TryPop(out code));
            q.Push(1); q.Push(2); q.Push(3);
            bool order = q.TryPop(out code) && code == 1 && q.TryPop(out code) && code == 2 && q.TryPop(out code) && code == 3;
            Check("queue: keeps order", order && !q.TryPop(out code));
            for (int i = 0; i < 1000; i++) q.Push(i);
            int count = 0;
            while (q.TryPop(out code)) count++;
            Check("queue: overflow drops keys instead of failing", count == 255);
            q.Push(7);
            Check("queue: usable after overflow", q.TryPop(out code) && code == 7);
        }

        private static void TestAlerter(Options o)
        {
            string log = Path.Combine(o.LocalDir, "selftest-alerts-" + Process.GetCurrentProcess().Id + ".log");
            try
            {
                // hookPath null = log only, which keeps this test from launching any process or firing a toast.
                Alerter a = new Alerter(null, log, "curtain");

                a.Raise("security-screen", "first");
                a.Raise("security-screen", "second, right after");   // within 30 s -> collapsed
                a.Raise("taskmgr", "a different kind");              // different kind -> sent
                a.AllClear("closed by PIN. " + "Security screen 1.");

                string[] lines = File.ReadAllText(log).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                int sent = 0, collapsed = 0, allclear = 0;
                foreach (string line in lines)
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 2) continue;
                    if (p[1] == "alert") sent++;
                    else if (p[1] == "collapsed") collapsed++;
                    else if (p[1] == "all-clear") allclear++;
                }
                Check("alerter: the first alert of a kind is sent", sent == 2);
                Check("alerter: a repeat of the same kind within 30 s is collapsed, not re-sent", collapsed == 1);
                Check("alerter: a different kind is still sent", sent == 2);   // security + taskmgr
                Check("alerter: one all-clear is written", allclear == 1);
                Check("alerter: with no hook it still logs (never throws)", lines.Length == 4);
                Check("alerter: hook disabled when none is given", !new Alerter(null, log, "x").HookEnabled);
                Check("alerter: hook enabled when a path is given", new Alerter("some.ps1", log, "x").HookEnabled);
            }
            finally
            {
                try { File.Delete(log); } catch { }
            }
        }

        private static void TestSysStats(Options o)
        {
            string dir = Path.Combine(o.LocalDir, "selftest-sys-" + Process.GetCurrentProcess().Id);
            string wx = Path.Combine(dir, "weather.txt");
            try
            {
                Directory.CreateDirectory(dir);
                SysStats s = new SysStats(dir);
                s.Sample();
                System.Threading.Thread.Sleep(120);
                s.Sample();   // a second reading, so CPU and network have an interval to measure
                lines.Add("INFO  stats: " + s.StatsLine);
                lines.Add("INFO  system: " + s.SysLine);
                Check("sysstats: reports CPU and RAM", s.StatsLine.StartsWith("CPU ") && s.StatsLine.Contains("RAM"));
                Check("sysstats: reports uptime", s.SysLine.StartsWith("Up "));
                Check("sysstats: no file means no weather lines", s.Weather.Length == 0);

                File.WriteAllText(wx,
                    "Anytown forecast  (as of 9:05 AM)\r\nToday: Hi 70 / Lo 50  Sunny\r\nTuesday: Hi 72 / Lo 52  Clear\r\n",
                    Encoding.UTF8);
                s.Sample();
                Check("sysstats: reads the weather file", s.Weather.Length == 3 && s.Weather[0].Contains("forecast"));

                File.SetLastWriteTimeUtc(wx, DateTime.UtcNow.AddDays(-2));
                s.Sample();
                Check("sysstats: ignores a weather file older than a day", s.Weather.Length == 0);
            }
            finally
            {
                try { File.Delete(wx); } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The curtain's drawing, without a window: every state renders and fits several screen sizes, the
        /// drift keeps the composition fully on screen for hours, and a re-render is quick (it happens
        /// whenever a reading changes, about once a second).
        /// </summary>
        private static void TestPainter(Options o)
        {
            Size[] screens = { new Size(1280, 720), new Size(1920, 1080), new Size(1920, 1200), new Size(2560, 1440), new Size(3440, 1440), new Size(1080, 1920) };
            string[] states = { "idle", "typing", "wrong", "cooldown", "admin", "bright", "night" };
            bool allFit = true, allOnScreen = true, columnsGolden = true;
            string worst = "";
            long slowestMs = 0;
            foreach (Size scr in screens)
            {
                foreach (string state in states)
                {
                    Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir"), TestState = state };
                    DisplayState s = CurtainPreview.Sample(po);
                    Stopwatch sw = Stopwatch.StartNew();
                    CurtainPainter.Layout l = CurtainPainter.Compose(s, scr.Width, scr.Height);
                    sw.Stop();
                    try
                    {
                        slowestMs = Math.Max(slowestMs, sw.ElapsedMilliseconds);
                        Bitmap b = l == null ? null : l.Block;
                        if (b == null || b.Width > scr.Width * 0.9 + 1 || b.Height > scr.Height * 0.9 + 1)
                        {
                            allFit = false;
                            worst = state + " at " + scr.Width + "x" + scr.Height;
                            continue;
                        }
                        if (l.Landscape && l.HasRight && Math.Abs(l.LeftCol.Width / l.RightCol.Width - CurtainPainter.Phi) > 0.01)
                        {
                            columnsGolden = false;
                            worst = state + " at " + scr.Width + "x" + scr.Height + ": " + l.LeftCol.Width + " : " + l.RightCol.Width;
                        }
                        // Eight hours of drift, one sample every 7 s: the composition never leaves the screen.
                        for (double t = 0; t < 8 * 3600; t += 7)
                        {
                            Point p = CurtainPainter.Place(b.Width, b.Height, scr.Width, scr.Height, t);
                            if (p.X < 0 || p.Y < 0 || p.X + b.Width > scr.Width || p.Y + b.Height > scr.Height)
                            {
                                allOnScreen = false;
                                worst = state + " at " + scr.Width + "x" + scr.Height + ", t=" + t + " s";
                                break;
                            }
                        }
                    }
                    finally
                    {
                        if (l != null) l.Dispose();
                    }
                }
            }
            lines.Add("INFO  slowest curtain render: " + slowestMs + " ms");
            Check("painter: every state fits 720p, 1080p, 16:10, 1440p, ultrawide and portrait screens" + (allFit ? "" : " (" + worst + ")"), allFit);
            Check("painter: on landscape screens the two columns are exactly phi : 1" + (columnsGolden ? "" : " (" + worst + ")"), columnsGolden);
            Check("painter: the drift keeps everything on screen for 8 hours" + (allOnScreen ? "" : " (" + worst + ")"), allOnScreen);
            Check("painter: a re-render takes under 150 ms", slowestMs < 150);

            // Colour: every text tone readable on black (WCAG AA 4.5:1); body text and values AAA (7:1).
            bool aa = true;
            string weakest = "";
            double weakestRatio = 99;
            foreach (KeyValuePair<string, Color> t in CurtainPainter.TextTones)
            {
                double r = CurtainPainter.ContrastOnBlack(t.Value);
                if (r < weakestRatio) { weakestRatio = r; weakest = t.Key; }
                if (r < 4.5) aa = false;
            }
            lines.Add("INFO  weakest text tone: " + weakest + " at " + weakestRatio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ":1");
            Check("colour: every text tone meets WCAG AA on black (4.5:1)", aa);
            Check("colour: body text and values meet WCAG AAA on black (7:1)", CurtainPainter.ContrastOnBlack(CurtainPainter.Secondary) >= 7.0);

            // Motion: the animations run with a fixed clock, start fast frames, and settle again.
            using (Bitmap canvas = new Bitmap(1280, 720))
            using (Graphics g = Graphics.FromImage(canvas))
            using (CurtainPainter p = new CurtainPainter())
            {
                Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir") };
                p.FixedNowMs = 0;
                p.Paint(g, CurtainPreview.Sample(po, "idle"), 1280, 720);
                bool fadeFast = p.NeedsFastFrames;
                p.FixedNowMs = 1500;
                p.Paint(g, CurtainPreview.Sample(po, "idle"), 1280, 720);
                bool settled = !p.NeedsFastFrames;
                p.FixedNowMs = 2000;
                p.Paint(g, CurtainPreview.Sample(po, "typing"), 1280, 720);
                bool dotsFast = p.NeedsFastFrames;
                p.FixedNowMs = 3000;
                p.Paint(g, CurtainPreview.Sample(po, "wrong"), 1280, 720);
                bool shakeFast = p.NeedsFastFrames;
                p.FixedNowMs = 4000;
                p.Paint(g, CurtainPreview.Sample(po, "wrong"), 1280, 720);
                bool shakeDone = !p.NeedsFastFrames;
                Check("motion: the fade-in runs at frame rate, then the screen goes back to its slow tick", fadeFast && settled);
                Check("motion: typed PIN dots and a wrong-PIN shake animate, then settle", dotsFast && shakeFast && shakeDone);
            }

            Point a = CurtainPainter.Place(1000, 800, 2560, 1440, 0);
            Point later = CurtainPainter.Place(1000, 800, 2560, 1440, 0.2);
            Check("painter: the drift moves at most a pixel per 200 ms frame",
                  Math.Abs(a.X - later.X) <= 1 && Math.Abs(a.Y - later.Y) <= 1);
            Check("painter: it rests on the golden section (space above : below = 1 : phi)",
                  Math.Abs((1440 - 800) / (1 + CurtainPainter.Phi) - a.Y) <= 1);
        }

        private static void TestEnvironment()
        {
            int level = Integrity.OfCurrentProcess();
            lines.Add("INFO  own integrity level 0x" + level.ToString("X"));
            Check("can read own integrity level", level != Integrity.Unknown);
            lines.Add("INFO  " + (level >= 0x3000 ? "running elevated (not needed)" : "running without administrator rights"));

            List<Rectangle> monitors = Monitors.Enumerate();
            lines.Add("INFO  monitors: " + Monitors.Describe(monitors));
            Check("at least one monitor is reported", monitors.Count >= 1);

            string zone = CurtainUi.ZoneText(DateTime.Now);
            lines.Add("INFO  time zone on the curtain: " + zone);
            Check("the curtain shows the time zone and its GMT offset", zone.Contains("GMT"));
            if (TimeZoneInfo.Local.Id == "Pacific Standard Time")
            {
                Check("Pacific time reads PST, GMT−8 in winter", CurtainUi.ZoneText(new DateTime(2026, 1, 15, 12, 0, 0)) == "PST, GMT−8");
                Check("Pacific time reads PDT, GMT−7 in summer", CurtainUi.ZoneText(new DateTime(2026, 7, 15, 12, 0, 0)) == "PDT, GMT−7");
            }

            Version os = Environment.OSVersion.Version;
            lines.Add("INFO  Windows build " + os.Build);
        }
    }

    /// <summary>Read-only facts that help explain a problem. Run with --diag.</summary>
    internal static class Diag
    {
        public static int Run(Options o)
        {
            List<string> lines = new List<string>();
            int own = Integrity.OfCurrentProcess();
            lines.Add("own integrity: 0x" + own.ToString("X"));
            lines.Add("monitors: " + Monitors.Describe(Monitors.Enumerate()));

            IntPtr fg = Native.GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out pid);
            string name = "?";
            try { if (pid != 0) name = Process.GetProcessById((int)pid).ProcessName; } catch { }
            bool denied = false;
            int level = pid != 0 ? Integrity.OfProcess(pid, out denied) : Integrity.Unknown;
            lines.Add("foreground: hwnd 0x" + fg.ToInt64().ToString("X") + " pid " + pid + " (" + name + ") integrity 0x" +
                      level.ToString("X") + (denied ? " (access denied)" : ""));
            bool elevated = level == Integrity.Unknown ? denied : (own != Integrity.Unknown && level > own);
            lines.Add("foreground is elevated above this process: " + elevated);
            lines.Add("pin file present: " + File.Exists(o.PinFile));
            lines.Add("exe: " + Options.ExePath);
            return Emit(o, lines, ExitCode.Ok);
        }

        /// <summary>
        /// Lists visible windows owned by processes running above this one's integrity level (elevated).
        /// The hooks cannot see input aimed at those, so curtain-on warns about them. One line each:
        /// pid, tab, process name, tab, window title. Read-only.
        /// </summary>
        public static int ListElevated(Options o)
        {
            List<string> lines = new List<string>();
            int own = Integrity.OfCurrentProcess();
            Dictionary<uint, bool> cache = new Dictionary<uint, bool>();
            Native.EnumWindowsProc callback = delegate(IntPtr hwnd, IntPtr l)
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                StringBuilder title = new StringBuilder(200);
                Native.GetWindowText(hwnd, title, title.Capacity);
                if (title.Length == 0) return true;
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return true;
                bool elevated;
                if (!cache.TryGetValue(pid, out elevated))
                {
                    bool denied;
                    int level = Integrity.OfProcess(pid, out denied);
                    elevated = level == Integrity.Unknown ? denied : (own != Integrity.Unknown && level > own);
                    cache[pid] = elevated;
                }
                if (!elevated) return true;
                string name = "?";
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
                lines.Add(pid + "\t" + name + "\t" + title.ToString().Replace('\t', ' '));
                return true;
            };
            Native.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return Emit(o, lines, ExitCode.Ok);
        }

        public static int Emit(Options o, List<string> lines, int code)
        {
            string text = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
            try
            {
                if (o.OutFile != null) File.WriteAllText(o.OutFile, text, new UTF8Encoding(false));
            }
            catch { }
            try { Console.Out.Write(text); } catch { }
            return code;
        }
    }
}
