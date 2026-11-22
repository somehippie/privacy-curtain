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
                TestMedia();
                TestWeatherMap(o);
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

                // The map is decoded on a worker thread, so it appears a moment after map.txt does.
                Check("sysstats: no map.txt means no map", s.Map == null);
                SaveSolid(Path.Combine(dir, "base.png"), 40, 20, (x, y) => Color.FromArgb(72, 72, 72));
                File.WriteAllLines(Path.Combine(dir, WeatherMap.IndexName), new[] { "box 44.4 -123.5 45.6 -120.5", "base base.png" }, Encoding.UTF8);
                Stopwatch wait = Stopwatch.StartNew();
                s.Sample();
                while (s.Map == null && wait.ElapsedMilliseconds < 3000) { System.Threading.Thread.Sleep(20); s.Sample(); }
                Check("sysstats: reads the weather map when map.txt appears", s.Map != null && s.Map.Radar.Length == 0 && s.Map.BaseW == 40);
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
            string[] states = { "idle", "typing", "wrong", "cooldown", "admin", "bright", "night", "clouds" };
            bool allFit = true, allOnScreen = true, columnsGolden = true, mapPlaced = true, headsShared = true;
            string worst = "", mapWorst = "", headsWorst = "";
            long slowestMs = 0, firstMs = 0;   // re-renders; the first at each size, which also draws the map's ground
            foreach (Size scr in screens)
            {
                bool firstAtSize = true;
                foreach (string state in states)
                {
                    Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir"), TestState = state };
                    DisplayState s = CurtainPreview.Sample(po);
                    Stopwatch sw = Stopwatch.StartNew();
                    CurtainPainter.Layout l = CurtainPainter.Compose(s, scr.Width, scr.Height);
                    sw.Stop();
                    try
                    {
                        if (firstAtSize) firstMs = Math.Max(firstMs, sw.ElapsedMilliseconds);
                        else slowestMs = Math.Max(slowestMs, sw.ElapsedMilliseconds);
                        firstAtSize = false;
                        Bitmap b = l == null ? null : l.Block;
                        if (b == null || b.Width > scr.Width * CurtainPainter.MaxWidthShare + 1 || b.Height > scr.Height * CurtainPainter.MaxHeightShare + 1)
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
                        // The map: in the left column, under its own header (which is under the date), clear of the
                        // message (the PIN row may share its lowest band), and big enough to read.
                        RectangleF mp = l.Map;
                        if (mp.IsEmpty || mp.Left < -0.5f || mp.Right > l.LeftCol.Right + 0.5f || mp.Top <= l.MapRuleY ||
                            l.MapRuleY <= l.MapHeadBaseline || mp.Bottom > l.Rule.Top - l.Unit * 0.3f || mp.Height < l.Unit * 1.1f ||
                            mp.Bottom < l.PinHintBaseline - 0.5f)
                        {
                            mapPlaced = false;
                            mapWorst = state + " at " + scr.Width + "x" + scr.Height + ": map " + mp + ", rule at " + l.Rule.Top;
                        }
                        // Beside the map, the forecast's header shares its line: one hairline row runs under both.
                        if (l.Landscape && l.HasRight && !mp.IsEmpty)
                        {
                            int ry = (int)Math.Round(l.OriginY + l.MapRuleY);
                            Color under = b.GetPixel((int)Math.Round(l.OriginX + mp.Width / 2f), ry);
                            Color beside = b.GetPixel((int)Math.Round(l.OriginX + l.RightCol.X + l.RightCol.Width / 2f), ry);
                            Func<Color, bool> hairline = c => Math.Abs(c.R - CurtainPainter.RuleColor.R) <= 2 &&
                                                              Math.Abs(c.G - CurtainPainter.RuleColor.G) <= 2 && Math.Abs(c.B - CurtainPainter.RuleColor.B) <= 2;
                            if (!hairline(under) || !hairline(beside))
                            {
                                headsShared = false;
                                headsWorst = state + " at " + scr.Width + "x" + scr.Height + ": " + under + " / " + beside;
                            }
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
            lines.Add("INFO  slowest curtain render: " + slowestMs + " ms (the first at a new size: " + firstMs + " ms)");
            Check("painter: every state fits 720p, 1080p, 16:10, 1440p, ultrawide and portrait screens" + (allFit ? "" : " (" + worst + ")"), allFit);
            Check("painter: on landscape screens the two columns are exactly phi : 1" + (columnsGolden ? "" : " (" + worst + ")"), columnsGolden);
            Check("painter: the drift keeps everything on screen for 8 hours" + (allOnScreen ? "" : " (" + worst + ")"), allOnScreen);
            Check("painter: a re-render takes under 150 ms (the first at a new screen size, which draws the map's ground, under 400 ms)",
                  slowestMs < 150 && firstMs < 400);
            Check("map: sits in the left column under its header, clear of the message, with the PIN row in its lowest band" +
                  (mapPlaced ? "" : " (" + mapWorst + ")"), mapPlaced);
            Check("painter: beside the map, the forecast's header and rule share the map's line" +
                  (headsShared ? "" : " (" + headsWorst + ")"), headsShared);

            // Stable geometry: wildly different readings, times and forecasts must give the very same frame.
            {
                Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir") };
                List<DisplayState> variants = new List<DisplayState>();
                variants.Add(CurtainPreview.Sample(po, "idle"));
                DisplayState big = CurtainPreview.Sample(po, "idle");
                big.Clock = "12:59 PM";
                big.Date = "Wednesday, September 30";
                big.Zone = "PDT, GMT−7";
                SysSnapshot x = CurtainPreview.SampleSys();
                x.Version = 2; x.Cpu = 100; x.MemPercent = 100; x.MemUsedGb = 99.9; x.MemTotalGb = 999;
                x.NetDown = 99.9 * 1048576; x.NetUp = 99.9 * 1048576; x.DiskUsedPercent = 100; x.DiskFreeGb = 9999;
                x.UptimeSeconds = 99L * 86400 + 23 * 3600;
                big.Sys = x;
                foreach (HourPoint hp in big.Hourly) { hp.Temp = hp.Temp > 57 ? 105 : -9; hp.Pop = 100; }
                big.Weather = new[]
                {
                    "Anytown forecast  (as of 12:59 PM)",
                    "Wednesday:  Hi 105° / Lo -9°   Chance Showers And Thunderstorms Then Patchy Blowing Dust  (100% precip)  [wind 199 mph at 12 PM, gusts 299]",
                    "Thursday:  Hi 99° / Lo 88°   Sunny  (100% precip)",
                    "Saturday:  Hi 99° / Lo 88°   Sunny",
                };
                variants.Add(big);
                DisplayState small = CurtainPreview.Sample(po, "idle");
                small.Clock = "1:01 AM";
                small.Sys = new SysSnapshot();   // nothing measured yet
                small.Hourly = new HourPoint[0];  // no hourly forecast yet
                small.Weather = new[] { "Anytown forecast  (as of 1:01 AM)", "Tonight:  Lo 4°   Clear" };
                variants.Add(small);
                variants.Add(CurtainPreview.Sample(po, "typing"));
                bool stable = true;
                string first = null, diff = "";
                foreach (Size scr in new[] { new Size(2560, 1440), new Size(1920, 1080), new Size(1080, 1920) })
                {
                    first = null;
                    foreach (DisplayState v in variants)
                    {
                        CurtainPainter.Layout l = CurtainPainter.Compose(v, scr.Width, scr.Height);
                        string g = l.Block.Width + "x" + l.Block.Height + " " + l.LeftCol + " " + l.RightCol + " " + l.Map;
                        l.Dispose();
                        if (first == null) first = g;
                        else if (g != first) { stable = false; diff = scr.Width + "x" + scr.Height + ": " + first + " vs " + g; }
                        if (!stable) lines.Add("INFO  geometry " + scr.Width + "x" + scr.Height + " variant " + variants.IndexOf(v) + ": " + g);
                    }
                }
                Check("painter: changing readings, times and forecasts never resize or move the composition" + (stable ? "" : " (" + diff + ")"), stable);
            }

            // The days' peak wind: read from the line, shown in the row, amber when it is windy, and a line
            // without it still reads as before.
            {
                Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir") };
                DisplayState calm = CurtainPreview.Sample(po, "idle"), windy = CurtainPreview.Sample(po, "idle"), none = CurtainPreview.Sample(po, "idle");
                string head = "Anytown forecast  (as of 9:05 AM)";
                calm.Weather = new[] { head, "Today:  Hi 70° / Lo 50°   Partly Sunny  (2% precip)  [wind 8 mph at 3 PM, gusts 14]" };
                windy.Weather = new[] { head, "Today:  Hi 70° / Lo 50°   Partly Sunny  (2% precip)  [wind 8 mph at 3 PM, gusts 34]" };
                none.Weather = new[] { head, "Today:  Hi 70° / Lo 50°   Partly Sunny  (2% precip)" };
                Func<DisplayState, List<string>> texts = st =>
                {
                    List<string> t = new List<string>();
                    foreach (KeyValuePair<string, Color> op in CurtainPainter.TextOpsForTest(st, 2560, 1440)) t.Add(op.Key + "|" + op.Value.ToArgb());
                    return t;
                };
                List<string> tc = texts(calm), tw = texts(windy), tn = texts(none);
                bool calmShown = tc.Contains("8 mph|" + CurtainPainter.Secondary.ToArgb()) && tc.Exists(t => t.StartsWith("peak 3 PM  \u00B7  gusts 14|"));
                bool windyAmber = tw.Contains("8 mph|" + CurtainPainter.Warn.ToArgb());
                bool plain = !tn.Exists(t => t.Contains(" mph|")) && tn.Contains("Partly sunny|" + CurtainPainter.Tertiary.ToArgb()) &&
                             tn.Contains("2%|" + CurtainPainter.Tertiary.ToArgb());
                Check("forecast: each day shows its peak wind, when it peaks and the strongest gust (" + calmShown + "), amber from gusts of " +
                      CurtainPainter.WindyGustMph + " (" + windyAmber + "), and a day without wind reads as before (" + plain + ")",
                      calmShown && windyAmber && plain);
            }

            // The hourly forecast file.
            {
                HourPoint[] h = SysStats.ParseHourly(new[]
                {
                    "# hourly forecast for Anytown, as of 1:07 PM",
                    "2026-11-15T13:00:00-08:00\t64\t9\t1\tMostly Cloudy",
                    "2026-11-15T19:00:00-08:00\t61\t\t0\tLight Rain",
                    "not a forecast line",
                });
                Check("hourly: the forecast file is read (comment and bad lines skipped, a missing rain chance kept as unknown)",
                      h.Length == 2 && h[0].Temp == 64 && h[0].Pop == 9 && h[0].IsDay && h[1].Pop == -1 && !h[1].IsDay && h[1].Text == "Light Rain");
                DateTime now = new DateTime(2026, 11, 15, 10, 42, 0);
                HourPoint[] next = CurtainPainter.NextHours(CurtainPreview.SampleHourly(now), now, 6);
                Check("hourly: the strip starts at the current hour and shows six", next.Length == 6 && next[0].Start.Hour == 10 && next[5].Start.Hour == 15);
                HourPoint[] full = SysStats.ParseHourly(new[] { "2026-11-15T13:00:00-08:00\t64\t9\t1\tMostly Cloudy\t79\t5–10 mph S" });
                double la, lo;
                bool located = SysStats.ParseLocation(new[] { "# hourly forecast", "# location 45.0000 -122.0000" }, out la, out lo);
                Check("hourly: humidity, wind and the grid's location are read",
                      full.Length == 1 && full[0].Humidity == 79 && full[0].Wind == "5–10 mph S" && located && Math.Abs(la - 45.0) < 1e-6 && Math.Abs(lo + 122.0) < 1e-6);
                // Sunrise and sunset against published values: London on 21 June 2026 (03:43 and 20:21 UTC)
                // and New York on 15 November 2026 (11:42 and 21:38 UTC, cross-checked with the Julian-date sunrise equation).
                DateTime r1, s1, r2, s2;
                bool london = CurtainPainter.SunTimesUtc(new DateTime(2026, 6, 21), 51.5074, -0.1278, out r1, out s1);
                bool newYork = CurtainPainter.SunTimesUtc(new DateTime(2026, 11, 15), 40.7128, -74.0060, out r2, out s2);
                Func<DateTime, DateTime, bool> near = (got, want) => Math.Abs((got - want).TotalMinutes) <= 3;
                Check("sun: sunrise and sunset match published times within 3 minutes",
                      london && newYork &&
                      near(r1, new DateTime(2026, 6, 21, 3, 43, 0, DateTimeKind.Utc)) && near(s1, new DateTime(2026, 6, 21, 20, 21, 0, DateTimeKind.Utc)) &&
                      near(r2, new DateTime(2026, 11, 15, 11, 42, 0, DateTimeKind.Utc)) && near(s2, new DateTime(2026, 11, 15, 21, 38, 0, DateTimeKind.Utc)));
                DateTime lr, ls;
                Check("sun: no sunrise or sunset is shown during the polar night", !CurtainPainter.SunTimesUtc(new DateTime(2026, 12, 21), 78.22, 15.65, out lr, out ls));
            }

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

            // The clock has its own tone, a step under hero (at its size hero glares, above all at night), and
            // dims overnight: the composed frame really paints it so.
            bool clockTone = CurtainPainter.Time.R < CurtainPainter.Hero.R;
            string clockSeen = "";
            foreach (string clockState in new[] { "idle", "night" })
            {
                DisplayState clockSample = CurtainPreview.Sample(new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir"), TestState = clockState });
                Color clockWant = CurtainPainter.Scale(CurtainPainter.Time, clockState == "night" ? CurtainPainter.NightLevel : 1.0);
                using (CurtainPainter.Layout clockLayout = CurtainPainter.Compose(clockSample, 2560, 1440))
                {
                    int[] clockPx = MapArt.GetPixels(clockLayout.Block);
                    int clockW = clockLayout.Block.Width, brightest = 0;
                    int cx0 = Math.Max(0, (int)(clockLayout.OriginX + clockLayout.LeftCol.Left));
                    int cx1 = Math.Min(clockW, (int)(clockLayout.OriginX + clockLayout.LeftCol.Right));
                    float clockBase = 0;
                    foreach (KeyValuePair<string, float> b in clockLayout.Baselines) if (b.Key == CurtainPainter.ClockLabel) { clockBase = b.Value; break; }
                    int cy0 = Math.Max(0, (int)clockLayout.OriginY);
                    int cy1 = Math.Min(clockLayout.Block.Height, (int)(clockLayout.OriginY + clockBase));
                    for (int cy = cy0; cy < cy1; cy++)       // the band of the digits (and AM/PM): nothing else is there
                        for (int cx = cx0; cx < cx1; cx++)
                        {
                            int p = clockPx[cy * clockW + cx];
                            if (((p >> 24) & 0xFF) >= 250) brightest = Math.Max(brightest, (p >> 16) & 0xFF);
                        }
                    clockSeen += (clockSeen.Length > 0 ? ", " : "") + clockState + " " + brightest;
                    if (Math.Abs(brightest - clockWant.R) > 2) clockTone = false;
                }
            }
            lines.Add("INFO  the clock's brightest red: " + clockSeen + " (the time " + CurtainPainter.Time.R + ", hero " + CurtainPainter.Hero.R + ")");
            Check("colour: the clock is painted in its own tone, a step under hero, and dims overnight", clockTone);

            // Beside the map the block takes all the height it may, so the map gets it; the clock is half a step down.
            using (CurtainPainter.Layout fill = CurtainPainter.Compose(CurtainPreview.Sample(new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir"), TestState = "idle" }), 2560, 1440))
            {
                float allowed = 1440 * CurtainPainter.MaxHeightShare;
                lines.Add("INFO  at 1440p: block " + fill.Block.Width + "x" + fill.Block.Height + ", map " + fill.Map.Width + "x" + fill.Map.Height);
                Check("painter: beside the map the block takes the height it may (the map gets it), with the clock half a step down the scale",
                      !fill.Map.IsEmpty && fill.Block.Height <= allowed + 1 && fill.Block.Height >= allowed - 8 && CurtainPainter.ClockStep == 0.5);
            }

            // Motion: the animations run with a fixed clock, start fast frames, and settle again.
            using (Bitmap canvas = new Bitmap(1280, 720))
            using (Graphics g = Graphics.FromImage(canvas))
            using (CurtainPainter p = new CurtainPainter())
            {
                Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir") };
                p.FixedNowMs = 0;
                p.Paint(g, CurtainPreview.Sample(po, "idle"), 1280, 720);
                bool fadeFast = p.NeedsFastFrames;
                p.FixedNowMs = WeatherMap.HoldMs - 100;   // after the fade-in, while the map's loop holds its latest picture
                p.Paint(g, CurtainPreview.Sample(po, "idle"), 1280, 720);
                bool settled = !p.NeedsFastFrames && CurtainPainter.FadeInMs < WeatherMap.HoldMs - 100;
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

            // The launcher's dialog: it fits, its parts never overlap, and the hours stay a whole number from 1 to 24.
            {
                bool fits = true, apart = true;
                string dialogWorst = "";
                string longNote = "Claude Code is open here, in a folder with a rather long name (since Sat 11:44 PM), but without Remote Control. You can type /remote-control in it and turn this off instead.";
                foreach (Size scr in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160), new Size(1080, 1920) })
                    using (LaunchView v = new LaunchView(24, "on", longNote, scr))
                    {
                        Size cs = v.ClientSize;
                        if (cs.Width > scr.Width * 0.9 || cs.Height > scr.Height * 0.9) { fits = false; dialogWorst = scr.Width + "x" + scr.Height + ": " + cs.Width + "x" + cs.Height; }
                        List<RectangleF> parts = new List<RectangleF>(v.Parts());
                        for (int pa = 0; pa < parts.Count; pa++)
                        {
                            RectangleF r = parts[pa];
                            if (r.Left < 0 || r.Top < 0 || r.Right > cs.Width || r.Bottom > cs.Height) { apart = false; dialogWorst = scr.Width + "x" + scr.Height + ": part " + pa + " outside"; }
                            for (int pb = pa + 1; pb < parts.Count; pb++)
                                if (r.IntersectsWith(parts[pb])) { apart = false; dialogWorst = scr.Width + "x" + scr.Height + ": parts " + pa + " and " + pb; }
                        }
                    }
                Check("launcher: the dialog fits screens from 720p to 4K and portrait, and none of its parts overlap" + (fits && apart ? "" : " (" + dialogWorst + ")"), fits && apart);

                bool typing, offline;
                using (LaunchView v = new LaunchView(24, "off", "", new Size(2560, 1440)))
                {
                    v.TypeDigit(1); v.TypeDigit(2); bool twelve = v.Hours == 12;
                    v.TypeDigit(3); bool restarts = v.Hours == 3;    // 123 is too many: a new figure starts
                    v.TypeDigit(0); bool refused = v.Hours == 0;     // nor is 30: the figure waits for a valid one
                    v.TypeDigit(8); bool eight = v.Hours == 8;
                    v.Backspace(); bool empty = v.Hours == 0;
                    v.Step(-1); bool floor = v.Hours == 1;
                    for (int k = 0; k < 30; k++) v.Step(1);
                    bool ceiling = v.Hours == LaunchView.MaxHours;
                    v.Flip();
                    typing = twelve && restarts && refused && eight && empty && floor && ceiling && v.Remote;
                }
                using (LaunchView v = new LaunchView(8, "unavailable", "", new Size(2560, 1440)))
                {
                    v.Flip();
                    offline = !v.Remote && !v.RemoteAvailable && v.Hours == 8;
                }
                Check("launcher: digits build the hours (1 then 2 is 12; a third starts again), the steps stay within 1 to 24, and the switch is off for good without Claude Code",
                      typing && offline);
            }

            Point a = CurtainPainter.Place(1000, 800, 2560, 1440, 0);
            Point later = CurtainPainter.Place(1000, 800, 2560, 1440, 0.2);
            Check("painter: the drift moves at most a pixel per 200 ms frame",
                  Math.Abs(a.X - later.X) <= 1 && Math.Abs(a.Y - later.Y) <= 1);
            Check("painter: it rests on the golden section (space above : below = 1 : phi)",
                  Math.Abs((1440 - 800) / (1 + CurtainPainter.Phi) - a.Y) <= 1);
        }

        /// <summary>
        /// What is playing: times, app names, the position running on between the app's reports, the layout
        /// beside the clock on every screen, shortened titles, and a live read of Windows' media controls.
        /// </summary>
        private static void TestMedia()
        {
            Check("media: times read m:ss, and h:mm:ss from an hour",
                  MediaInfo.Time(0) == "0:00" && MediaInfo.Time(217.9) == "3:37" && MediaInfo.Time(600) == "10:00" &&
                  MediaInfo.Time(3725) == "1:02:05" && MediaInfo.Time(-4) == "0:00");
            Check("media: app names come out readable",
                  MediaInfo.AppName("Spotify.exe") == "Spotify" && MediaInfo.AppName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "Spotify" &&
                  MediaInfo.AppName("MSEdge") == "Edge" && MediaInfo.AppName("chrome.exe") == "Chrome" && MediaInfo.AppName(null) == "");

            DateTime t0 = new DateTime(2026, 11, 21, 22, 0, 0, DateTimeKind.Utc);
            MediaInfo playing = new MediaInfo { Title = "A", Artist = "B", Playing = true, Position = 10, Length = 200, PositionAt = t0 };
            MediaInfo paused = new MediaInfo { Title = "A", Artist = "B", Playing = false, Position = 10, Length = 200, PositionAt = t0 };
            Check("media: while playing the position runs on from the last report, and stops at the end",
                  Math.Abs(playing.PositionNow(t0.AddSeconds(5)) - 15) < 0.01 && playing.PositionNow(t0.AddSeconds(1000)) == 200);
            Check("media: while paused it stays put", Math.Abs(paused.PositionNow(t0.AddSeconds(5)) - 10) < 0.01);
            MediaInfo later = new MediaInfo { Title = "A", Artist = "B", Playing = true, Position = 90, Length = 200, PositionAt = t0.AddSeconds(80) };
            Check("media: the position does not change what is composed (it is drawn live); playing or paused does",
                  playing.Key() == later.Key() && playing.Key() != paused.Key());

            Size[] screens = { new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440), new Size(3440, 1440), new Size(1080, 1920) };
            bool fits = true, landscapeShows = true, placed = true, quietWithout = true;
            string worst = "";
            foreach (Size scr in screens)
            {
                foreach (string state in new[] { "music", "paused", "music-long", "idle" })
                {
                    Options po = new Options { LocalDir = Path.Combine(Path.GetTempPath(), "no-such-dir"), TestState = state };
                    DisplayState s = CurtainPreview.Sample(po);
                    using (CurtainPainter.Layout l = CurtainPainter.Compose(s, scr.Width, scr.Height))
                    {
                        string at = state + " at " + scr.Width + "x" + scr.Height;
                        if (l == null || l.Block == null || l.Block.Width > scr.Width * CurtainPainter.MaxWidthShare + 1 ||
                            l.Block.Height > scr.Height * CurtainPainter.MaxHeightShare + 1) { fits = false; worst = at; continue; }
                        if (state == "idle") { if (l.HasMedia) quietWithout = false; continue; }
                        if (l.Landscape && !l.HasMedia) { landscapeShows = false; worst = at; }
                        if (!l.HasMedia) continue;
                        // beside the clock, inside the left column, the line on the date's baseline
                        if (l.BarLeft < l.MediaLeft || l.BarRight > l.LeftCol.Right + 0.5f || l.BarY > l.MediaBaseline ||
                            l.MediaLength != 217)
                        {
                            placed = false;
                            worst = at + ": bar " + l.BarLeft + ".." + l.BarRight + ", room from " + l.MediaLeft;
                        }
                        // ink in the media's corner of the block: there is some, and none left of where it may start
                        // (that is the clock's reserved width)
                        int x0 = (int)Math.Round(l.OriginX + l.MediaLeft), x1 = (int)Math.Round(l.OriginX + l.LeftCol.Right);
                        int y1 = (int)Math.Round(l.OriginY + l.MediaBaseline - l.Unit * 0.5f);
                        bool ink = false;
                        for (int y = 0; y < y1 && !ink; y += 2)
                            for (int x = x0; x < x1 && !ink; x += 2)
                                ink = l.Block.GetPixel(x, y).G > 40;
                        if (!ink) { placed = false; worst = at + ": no ink beside the clock"; }
                    }
                }
            }
            Check("media: with a track the composition still fits every screen" + (fits ? "" : " (" + worst + ")"), fits);
            Check("media: on every landscape screen it shows beside the clock" + (landscapeShows ? "" : " (" + worst + ")"), landscapeShows);
            Check("media: it sits right of the clock's room, inside the left column, its line on the date's baseline" +
                  (placed ? "" : " (" + worst + ")"), placed);
            Check("media: with nothing playing nothing is shown", quietWithout);

            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics m = Graphics.FromImage(probe))
            using (Font f = new Font("Segoe UI", 20f, GraphicsUnit.Pixel))
            {
                float w = CurtainPainter.Measure(m, "Night Swim", f, 0) + 4;
                string a = CurtainPainter.Shorten(m, "Night Swim (feat. Somebody)", f, 0, w);
                string b = CurtainPainter.Shorten(m, "Wish You Were Here - Remastered 2011", f, 0, CurtainPainter.Measure(m, "Wish You Were Here", f, 0) + 4);
                string c = CurtainPainter.Shorten(m, "Supercalifragilisticexpialidocious", f, 0, w);
                string d = CurtainPainter.Shorten(m, "Short", f, 0, w);
                Check("media: a long title drops its extras first ('" + a + "', '" + b + "'), then ends in an ellipsis ('" + c + "')",
                      a == "Night Swim" && b == "Wish You Were Here" && c.EndsWith("\u2026") && CurtainPainter.Measure(m, c, f, 0) <= w && d == "Short");
            }

            // Windows' media controls answer (whatever is or is not playing), within a few seconds.
            Stopwatch sw = Stopwatch.StartNew();
            string error;
            MediaInfo now = NowPlaying.ReadOnce(out error);
            sw.Stop();
            Check("media: Windows' media controls answer in " + sw.ElapsedMilliseconds + " ms (" +
                  (error ?? (now == null ? "nothing playing" : (now.Playing ? "a track playing" : "a track paused") + " in " + now.App)) + ")",
                  error == null && sw.ElapsedMilliseconds < 5000);
        }

        /// <summary>
        /// The weather map: reading map.txt and its pictures (radar colours back to dBZ, land and water, stale
        /// and missing pictures), choosing radar or clouds, which part of the area shows, the replay's timing,
        /// the sun's height, and that the replay and the PIN veil animate and then let the screen rest.
        /// </summary>
        private static void TestWeatherMap(Options o)
        {
            // Radar colours: every whole dBZ comes back as itself; known points of NOAA's ramp; not-radar is nothing.
            bool roundTrip = true;
            for (int d = 1; d <= 80; d++) if (WeatherMap.RadarDbz(unchecked((int)0xFF000000) | WeatherMap.RadarRgb(d)) != d) roundTrip = false;
            Check("map: every radar colour reads back as its dBZ", roundTrip);
            Check("map: NOAA's ramp: green is 25 dBZ, yellow 40, blue 20, an in-between colour 7, transparent nothing",
                  WeatherMap.RadarDbz(unchecked((int)0xFF00FF00)) == 25 && WeatherMap.RadarDbz(unchecked((int)0xFFFFFF00)) == 40 &&
                  WeatherMap.RadarDbz(unchecked((int)0xFF0000F6)) == 20 && WeatherMap.RadarDbz(unchecked((int)0xFF4AA3A3)) == 7 &&
                  WeatherMap.RadarDbz(0x0000FF00) == 0 && WeatherMap.RadarDbz(unchecked((int)0xFFFFFFFF)) == 0);

            // The index file.
            WeatherMap.Index ix = WeatherMap.Index.Parse(new[]
            {
                "# weather map for the curtain",
                "box 44.4 -123.5 45.6 -120.5",
                "base map-base-a.png",
                "radar 2026-11-15T18:40:00Z radar-b.png",
                "radar 2026-11-15T18:32:00Z radar-a.png",
                "radar 2026-11-15T18:24:00Z ..\\..\\secret.png",
                "sat vis 2026-11-15T18:40:00Z sat-vis-a.jpg",
                "sat ir 2026-11-15T18:30:00Z sat-ir-a.jpg",
                "credit NOAA  ·  © OpenStreetMap",
                "nonsense line",
            });
            Check("map: map.txt is read (box, base, pictures oldest first, one satellite kind, credit; a path is refused)",
                  ix.HasBox && Math.Abs(ix.Box.North - 45.6) < 1e-9 && ix.Base == "map-base-a.png" && ix.Radar.Count == 2 &&
                  ix.Radar[0].Value == "radar-a.png" && ix.Sat.Count == 1 && ix.SatKind == "vis" && ix.Credit == "NOAA  ·  © OpenStreetMap");
            Check("map: a box that is not a box is refused", !WeatherMap.Index.Parse(new[] { "box 45 -120 44 -121" }).HasBox);

            // Loading real files: land and water, radar values, satellite grey, a stale and a missing picture skipped.
            string dir = Path.Combine(o.LocalDir, "selftest-map-" + Process.GetCurrentProcess().Id);
            try
            {
                Directory.CreateDirectory(dir);
                DateTime now = new DateTime(2026, 11, 15, 18, 45, 0, DateTimeKind.Utc);
                SaveSolid(Path.Combine(dir, "base.png"), 40, 20, (x, y) => x < 20 ? Color.FromArgb(128, 128, 128) : Color.FromArgb(72, 72, 72));
                SaveSolid(Path.Combine(dir, "radar-new.png"), 30, 12, (x, y) => x >= 10 && x < 13 && y >= 4 && y < 7 ? Color.FromArgb(0, 255, 0) : Color.Transparent);
                SaveSolid(Path.Combine(dir, "radar-old.png"), 30, 12, (x, y) => Color.Transparent);
                SaveSolid(Path.Combine(dir, "sat.png"), 30, 12, (x, y) => Color.FromArgb(180, 180, 180));
                File.WriteAllLines(Path.Combine(dir, WeatherMap.IndexName), new[]
                {
                    "box 44.4 -123.5 45.6 -120.5", "base base.png",
                    "radar 2026-11-15T14:00:00Z radar-old.png",
                    "radar 2026-11-15T18:40:00Z radar-new.png",
                    "radar 2026-11-15T18:44:00Z radar-missing.png",
                    "sat vis 2026-11-15T18:40:00Z sat.png",
                    "credit NOAA",
                }, Encoding.UTF8);
                WeatherMap m = WeatherMap.Load(dir, now);
                Check("map: the pictures load: water and land, radar dBZ, satellite grey; stale and missing ones are skipped",
                      m != null && m.Water[0] == 255 && m.Water[39] == 0 && m.Radar.Length == 1 && m.Radar[0].Values[5 * 30 + 11] == 25 &&
                      m.Radar[0].Values[0] == 0 && m.Sat.Length == 1 && m.Sat[0].Kind == "vis" && m.Sat[0].Values[0] == 180);
                Check("map: pictures that grow too old drop out without a reload",
                      m != null && m.Fresh(now) == m && m.Fresh(now.AddHours(4)).Radar.Length == 0 && m.Fresh(now.AddHours(4)).Key != m.Key);
                File.Delete(Path.Combine(dir, "base.png"));
                Check("map: no land and water map means no map", WeatherMap.Load(dir, now) == null);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            // Radar or clouds.
            {
                GeoBox box = new GeoBox(44.4, -123.5, 45.6, -120.5);
                Func<double, double, MapFrame> rain = (lat, lon) =>
                {
                    MapFrame f = new MapFrame { Kind = "radar", W = 300, H = 120, Values = new byte[300 * 120], Utc = DateTime.UtcNow };
                    if (!double.IsNaN(lat))
                    {
                        int cx = (int)((lon - box.West) / (box.East - box.West) * 300), cy = (int)((box.North - lat) / (box.North - box.South) * 120);
                        for (int y = cy - 2; y <= cy + 2; y++) for (int x = cx - 2; x <= cx + 2; x++) f.Values[y * 300 + x] = 30;
                    }
                    return f;
                };
                Func<MapFrame, WeatherMap> make = r => new WeatherMap
                {
                    Box = box, Radar = new[] { r },
                    Sat = new[] { new MapFrame { Kind = "vis", W = 4, H = 4, Values = new byte[16], Utc = DateTime.UtcNow } },
                };
                DateTime now = new DateTime(2026, 11, 15, 10, 42, 0);
                Func<int, string, HourPoint[]> hours = (pop, text) =>
                {
                    HourPoint[] h = new HourPoint[6];
                    for (int i = 0; i < 6; i++) h[i] = new HourPoint { Start = new DateTime(2026, 11, 15, 10 + i, 0, 0), Pop = i == 2 ? pop : 5, Text = i == 2 ? text : "Sunny" };
                    return h;
                };
                double hLat = 45.0, hLon = -122.0;
                bool dry = !WeatherMap.ShowRadar(make(rain(double.NaN, 0)), hours(10, "Partly Sunny"), now, hLat, hLon);
                bool chance = WeatherMap.ShowRadar(make(rain(double.NaN, 0)), hours(40, "Mostly Cloudy"), now, hLat, hLon);
                bool words = WeatherMap.ShowRadar(make(rain(double.NaN, 0)), hours(15, "Slight Chance Light Rain"), now, hLat, hLon);
                bool later = !WeatherMap.ShowRadar(make(rain(double.NaN, 0)), hours(5, "Sunny"), now, hLat, hLon) &&
                             !WeatherMap.ShowRadar(make(rain(double.NaN, 0)), new[] { new HourPoint { Start = now.AddHours(4), Pop = 90, Text = "Rain" } }, now, hLat, hLon);
                bool near = WeatherMap.ShowRadar(make(rain(45.2, -122.1)), hours(5, "Sunny"), now, hLat, hLon);
                bool far = !WeatherMap.ShowRadar(make(rain(45.0, -120.9)), hours(5, "Sunny"), now, hLat, hLon);   // ~87 km east
                Check("map: dry hours ahead and no rain near show the clouds", dry && later);
                Check("map: a 30% chance in the next three hours, or rain in the forecast's words, shows the radar", chance && words);
                Check("map: rain on the radar within 50 km shows the radar; farther away it does not", near && far);
            }

            // Which part of the area shows.
            {
                GeoBox box = new GeoBox(44.4, -123.5, 45.6, -120.5), roomy = new GeoBox(44.2, -124.2, 46.2, -119.8);
                GeoBox c = WeatherMap.Crop(roomy, 45.0, -122.0, 969, 599);
                double homeX = (-122.0 - c.West) / (c.East - c.West) * 969, homeY = (c.North - 45.0) / (c.North - c.South) * 599;
                Check("map: it shows 135 km top to bottom, keeps the map's shape, puts home on the golden section across (the larger part west) and 7/10 of the way down",
                      WeatherMap.ViewKm == 135 && Math.Abs(c.KmTall - WeatherMap.ViewKm) < 0.5 && Math.Abs(c.KmWide / c.KmTall - 969 / 599.0) < 0.02 &&
                      Math.Abs(homeX - 969 / CurtainPainter.Phi) < 1.5 && Math.Abs(homeY - 599 * (1 - WeatherMap.SouthShare)) < 1.5);
                GeoBox wide = WeatherMap.Crop(box, 45.0, -122.0, 3000, 100);
                GeoBox edge = WeatherMap.Crop(box, 44.5, -123.45, 873, 390);
                Check("map: it never reaches outside the pictures, even for a very wide map or a home near their edge",
                      wide.West >= box.West - 1e-9 && wide.East <= box.East + 1e-9 && Math.Abs(wide.KmWide / wide.KmTall - 30) < 0.5 &&
                      edge.West >= box.West - 1e-9 && edge.South >= box.South - 1e-9 && edge.East <= box.East && edge.North <= box.North);
            }

            // The replay: a loop that holds the latest picture briefly, plays the hour in order, ends on the latest.
            {
                int i, j;
                float t, progress;
                bool playing;
                long cycle = WeatherMap.CycleFor(8), start = WeatherMap.HoldMs;
                WeatherMap.LoopAt(8, WeatherMap.HoldMs / 2, out i, out j, out t, out progress, out playing);
                bool rests = i == 7 && j == 7 && !playing;
                WeatherMap.LoopAt(8, start + WeatherMap.FadeMs / 2, out i, out j, out t, out progress, out playing);
                bool wraps = i == 7 && j == 0 && t > 0.3f && t < 0.7f && playing;
                bool forward = true;
                int lastI = 0;
                for (long ms = start + WeatherMap.FadeMs; ms < cycle; ms += 50)
                {
                    WeatherMap.LoopAt(8, ms, out i, out j, out t, out progress, out playing);
                    if (i < lastI || j != i + 1 || !playing) forward = false;
                    lastI = i;
                }
                WeatherMap.LoopAt(8, cycle + 10, out i, out j, out t, out progress, out playing);
                bool ends = i == 7 && !playing;
                WeatherMap.LoopAt(1, start + 100, out i, out j, out t, out progress, out playing);
                Check("map: the replay loops: it plays the past hour in order, ends on the latest, holds it two steps and plays again",
                      rests && wraps && forward && ends && i == 0 && !playing && WeatherMap.HoldMs <= 2 * WeatherMap.StepMs && cycle < 8000);
            }

            // The sun's height, for evening out the visible picture.
            {
                double ny = CurtainPainter.SunElevation(new DateTime(2026, 11, 15, 16, 40, 0, DateTimeKind.Utc), 40.7128, -74.0060);
                double london = CurtainPainter.SunElevation(new DateTime(2026, 6, 21, 12, 2, 0, DateTimeKind.Utc), 51.5074, -0.1278);
                double night = CurtainPainter.SunElevation(new DateTime(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc), 40.7128, -74.0060);
                Check("sun: its height at noon matches (New York in November about 31 degrees, London at midsummer about 62); below the horizon at night",
                      Math.Abs(ny - 30.8) < 1.0 && Math.Abs(london - 61.9) < 1.0 && night < -30);
            }

            // Motion: the replay's cross-fades and the veil over the map while a PIN is typed run fast, then rest.
            using (Bitmap canvas = new Bitmap(1280, 720))
            using (Graphics g = Graphics.FromImage(canvas))
            using (CurtainPainter p = new CurtainPainter())
            {
                Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir") };
                DisplayState idle = CurtainPreview.Sample(po, "idle");
                long cycle = WeatherMap.CycleFor(8), turn = 3 * cycle;   // a few turns in, so the picture count settles
                for (int k = 0; k <= 12; k++) { p.FixedNowMs = turn + k * 100; p.Paint(g, idle, 1280, 720); }   // in the hold; also warms every picture
                bool rest = !p.NeedsFastFrames;
                p.FixedNowMs = turn + WeatherMap.HoldMs + WeatherMap.StepMs + WeatherMap.FadeMs + (WeatherMap.StepMs - WeatherMap.FadeMs) + WeatherMap.FadeMs / 2;
                p.Paint(g, idle, 1280, 720);
                bool fading = p.NeedsFastFrames;
                p.FixedNowMs = turn + cycle + WeatherMap.HoldMs / 2;
                p.Paint(g, idle, 1280, 720);
                bool restsAgain = !p.NeedsFastFrames;
                p.FixedNowMs = turn + cycle + WeatherMap.HoldMs / 2 + 100;
                p.Paint(g, CurtainPreview.Sample(po, "typing"), 1280, 720);
                bool veilMoves = p.NeedsFastFrames;
                p.FixedNowMs = turn + cycle + WeatherMap.HoldMs / 2 + 100 + CurtainPainter.VeilMs + 400;   // the loop plays on, unseen
                p.Paint(g, CurtainPreview.Sample(po, "typing"), 1280, 720);
                bool veilDone = !p.NeedsFastFrames;
                Check("motion: the map's replay cross-fades at frame rate and the screen rests in the hold on the latest picture", rest && fading && restsAgain);
                Check("motion: the map steps back while a PIN is typed, then the screen rests", veilMoves && veilDone);
            }

            // Station reports, the elevation tiles and the tide in map.txt.
            {
                WeatherMap.Index sx = WeatherMap.Index.Parse(new[]
                {
                    "box 44.4 -123.5 45.6 -120.5", "base b.png", "dem 9 80 197 84 199",
                    "station KABC 45.1 -122.2 57 300 14 22 2026-11-15T18:35:00Z",
                    "station KDEF 44.9 -121.8 - - - - 2026-11-15T18:35:00Z",
                    "station BAD! 44.9 -121.8 50 0 0 - 2026-11-15T18:35:00Z",
                    "station KGHI 44.9 -121.8 50 0 0",
                    "tidestation 9999999 45.0 -123.0",
                    "tide 2026-11-15T12:00:00Z L 0.5", "tide 2026-11-15T18:00:00Z H 6.5", "tide 2026-11-16T00:00:00Z L 1.5",
                });
                Check("map: station reports, the elevation tiles and the tide are read from map.txt (malformed stations refused)",
                      sx.Stations.Count == 2 && sx.Stations[0].TempF == 57 && sx.Stations[0].WindFrom == 300 && sx.Stations[0].WindMph == 14 &&
                      sx.Stations[0].GustMph == 22 && sx.Stations[1].TempF == int.MinValue && sx.Stations[1].WindMph == -1 &&
                      sx.DemZoom == 9 && sx.DemX1 == 84 && sx.Tide != null && sx.Tide.Times.Length == 3 && sx.Tide.High[1]);
                MapTide tide = sx.Tide;
                DateTime t0 = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
                Check("tide: the curve passes through each high and low, halfway between at mid-time, rising before a high",
                      tide != null && Math.Abs(tide.HeightAt(t0) - 0.5) < 1e-6 && Math.Abs(tide.HeightAt(t0.AddHours(6)) - 6.5) < 1e-6 &&
                      Math.Abs(tide.HeightAt(t0.AddHours(3)) - 3.5) < 1e-6 && tide.HeightAt(t0.AddHours(2)) < tide.HeightAt(t0.AddHours(4)) &&
                      tide.NextEvent(t0.AddHours(1)) == 1 && double.IsNaN(tide.HeightAt(t0.AddHours(-1))) && tide.NextEvent(t0.AddHours(13)) == -1);
                Check("map: wind reads as the forecast writes it, gusts as a range, and is labelled from 10 mph",
                      WeatherMap.WindText(sx.Stations[0]) == "14–22 mph" && WeatherMap.WindText(new MapStation { WindMph = 12, GustMph = 15 }) == "12 mph" &&
                      WeatherMap.Windy(sx.Stations[0]) && !WeatherMap.Windy(new MapStation { WindMph = 9 }));
            }

            // Rivers and lakes: map.txt names the hydrography mask; zoom 10 elevation spans up to 16 tiles a side; the
            // mask only ever adds water (a channel the land and water map lacks), never takes any away.
            {
                WeatherMap.Index wx = WeatherMap.Index.Parse(new[] { "box 44.4 -123.5 45.6 -120.5", "base b.png", "water w.png", "dem 10 160 393 170 398" });
                WeatherMap.Index bad = WeatherMap.Index.Parse(new[] { "box 44.4 -123.5 45.6 -120.5", "base b.png", "water ..\\w.png", "dem 10 160 393 180 398" });
                byte[] water = { 0, 0, 255, 255, 0, 0 };   // 3 x 2: the land and water map's water on the right
                int clear = 0, blue = unchecked((int)0xFF0000FF), half = unchecked((int)0x800000FF), red = unchecked((int)0xFFFF0000);
                WeatherMap.MergeWater(water, 3, 2, new[] { clear, blue, clear, half, half, red }, 3, 2);
                Check("map: the rivers and lakes mask is named in map.txt and only adds water (a channel, a lake's soft edge), never removes it",
                      wx.Water == "w.png" && wx.DemZoom == 10 && wx.DemX1 == 170 && bad.Water == null && bad.DemZoom == -1 &&
                      water[0] == 0 && water[1] == 255 && water[2] == 255 && water[3] == 255 && water[4] == 128 && water[5] == 0);
            }

            // Marsh and creeks: named in map.txt, read as masks (creeks scaled by their size), drawn into the ground.
            {
                WeatherMap.Index hx = WeatherMap.Index.Parse(new[] { "box 44.4 -123.5 45.6 -120.5", "base b.png", "waterfine f.png", "marsh m.png", "streams s.png" });
                WeatherMap.Index hbad = WeatherMap.Index.Parse(new[] { "box 44.4 -123.5 45.6 -120.5", "base b.png", "marsh ..\\m.png", "streams a b" });
                int clear = 0, green = unchecked((int)0xFF00FF00), big = unchecked((int)0xFFFF00FF), small = unchecked((int)0xFF5500FF), soft = unchecked((int)0x80FF00FF);
                byte[] mm = WeatherMap.MaskFrom(new[] { clear, green, clear, green }, 2, 2, 2, 2, false);
                byte[] sm = WeatherMap.MaskFrom(new[] { clear, big, small, soft }, 2, 2, 2, 2, true);
                Check("map: the high-resolution water, marsh and creeks are named in map.txt and read as masks, creeks weighed by their size (river, small creek, soft edge)",
                      hx.WaterFine == "f.png" && hx.Marsh == "m.png" && hx.Streams == "s.png" && hbad.Marsh == null && hbad.Streams == null &&
                      mm[0] == 0 && mm[1] == 255 && mm[3] == 255 && sm[0] == 0 && sm[1] == 255 && sm[2] == 85 && sm[3] == 128);
                int yr = unchecked((int)0xFFFFFFFF), seasonal = unchecked((int)0xFFAA00FF);   // a year-round river; a seasonal creek
                byte[] ym = WeatherMap.YearRoundFrom(new[] { yr, seasonal }, 2, 1, 2, 1);
                Check("map: creeks that flow all year are told from seasonal ones (the green channel)", ym[0] == 255 && ym[1] == 0);
                Check("map: the marsh pattern is rows of short staggered dashes",
                      MapArt.MarshDash(0, 2) && !MapArt.MarshDash(0, 3) && !MapArt.MarshDash(4, 2) && MapArt.MarshDash(4, 6) && !MapArt.MarshDash(0, 6));
            }

            // Elevation: terrarium pixels to metres, tiles found and stitched in the right places.
            {
                float[] e = WeatherMap.Terrarium(new[] { unchecked((int)0xFF800000), unchecked((int)0xFF812C00), unchecked((int)0xFF7FFF80) });
                double gx, gy;
                WeatherMap.Mercator(51.5074, -0.1278, 9, out gx, out gy);
                float[] a = new float[65536], b = new float[65536];
                for (int i = 0; i < 65536; i++) { a[i] = 100; b[i] = 200; }
                int sw, sh;
                float[] grid = WeatherMap.StitchDem(new[] { a, b }, 9, 255, 170, 2, 1, new GeoBox(51.3, -0.6, 51.5, 0.6), out sw, out sh);
                Check("relief: elevation pixels decode to metres (sea level, 300 m, half a metre below) and London falls in tile 255, 170 at zoom 9",
                      Math.Abs(e[0]) < 1e-3 && Math.Abs(e[1] - 300) < 1e-3 && Math.Abs(e[2] + 0.5) < 1e-3 && (int)(gx / 256) == 255 && (int)(gy / 256) == 170);
                Check("relief: the tiles are stitched into the right places",
                      sw == 480 && sh == 80 && Math.Abs(grid[(sh / 2) * sw + 2] - 100) < 0.01 && Math.Abs(grid[(sh / 2) * sw + sw - 3] - 200) < 0.01);
            }

            // Which stations get a label.
            {
                GeoBox crop = new GeoBox(44.6, -123.3, 45.4, -121.5);   // 500 pixels a degree on a 900 x 400 map
                Func<double, double, int, MapStation> st = (la, lo, t) => new MapStation { Id = "K" + t, Lat = la, Lon = lo, TempF = t, Utc = DateTime.UtcNow };
                MapStation[] all =
                {
                    st(45.0, -122.4, 60), st(45.01, -122.38, 61),                  // nearest home, and one right beside it
                    st(45.3, -123.2, 55), st(44.8, -121.6, 70), st(45.3, -121.6, 66), st(44.8, -123.2, 54),   // the four corners
                    st(44.62, -122.4, 58), st(46.0, -122.0, 50),                   // in the legend's band; off the map
                };
                Func<MapStation, RectangleF> box = s => new RectangleF(-20, -8, 40, 16);
                List<PlacedStation> picked = WeatherMap.PickStations(all, crop, 900, 400, 45.0, -122.45, box, 120, 10, 60, new List<RectangleF>(), 7);
                bool spaced = true;
                for (int i = 0; i < picked.Count; i++)
                    for (int j = i + 1; j < picked.Count; j++)
                        if (Math.Sqrt(Math.Pow(picked[i].X - picked[j].X, 2) + Math.Pow(picked[i].Y - picked[j].Y, 2)) < 120) spaced = false;
                bool kept = picked.TrueForAll(p => p.Station.TempF != 58 && p.Station.TempF != 50 && p.Station.TempF != 61);
                List<PlacedStation> two = WeatherMap.PickStations(all, crop, 900, 400, 45.0, -122.45, box, 120, 10, 60, new List<RectangleF>(), 2);
                List<PlacedStation> around = WeatherMap.PickStations(all, crop, 900, 400, 45.0, -122.45, box, 120, 10, 60,
                                                                     new List<RectangleF> { new RectangleF(820, 30, 60, 40) }, 7);
                Check("map: temperatures start nearest home, spread out, keep apart, and stay off the edges, the legend and other labels",
                      picked.Count == 5 && picked[0].Station.TempF == 60 && spaced && kept && two.Count == 2 &&
                      around.Count == 4 && around.TrueForAll(p => p.Station.TempF != 66));
            }

            // The sample: labels placed without overlapping, and the relief drawn into the ground.
            {
                Options po = new Options { LocalDir = Path.Combine(o.LocalDir, "no-such-dir") };
                DisplayState s = CurtainPreview.Sample(po, "idle");
                CurtainPainter.Layout l = CurtainPainter.Compose(s, 2560, 1440);
                bool apart = true;
                CurtainPainter.TideChart tc = l.MapTide;
                for (int i = 0; i < l.MapStations.Count; i++)
                {
                    if (tc != null && l.MapStations[i].Box.IntersectsWith(tc.Panel)) apart = false;
                    for (int j = i + 1; j < l.MapStations.Count; j++) if (l.MapStations[i].Box.IntersectsWith(l.MapStations[j].Box)) apart = false;
                }
                Check("map: the sample shows station temperatures (wind where it blows) and the tide, with no label over another",
                      l.MapStations.Count >= 4 && l.MapStations.Exists(p => WeatherMap.Windy(p.Station)) && tc != null && apart);
                if (tc != null)
                {
                    // The sample's tide: a low of 0.6 ft 4.2 hours ago, a high of 6.3 ft in 2.1 hours, a low of 2.1 ft after it.
                    float nowX = tc.Spark.Left + tc.Spark.Width * CurtainPainter.TidePastHours / CurtainPainter.TideHours;
                    // Neither top corner hides a temperature, so it takes the one over more water: the sea, to the west.
                    int smw = (int)Math.Round(l.Map.Width), smh = (int)Math.Round(l.Map.Height);
                    RectangleF other = new RectangleF(l.Map.Width - tc.Panel.Right, tc.Panel.Top, tc.Panel.Width, tc.Panel.Height);
                    bool corner = tc.Panel.Top > 0 && tc.Panel.Left > 0 && tc.Panel.Right < l.Map.Width / 2f && tc.Panel.Bottom < l.Map.Height / 2f &&
                                  WeatherMap.WaterShare(s.Map, l.MapCrop, smw, smh, tc.Panel) > WeatherMap.WaterShare(s.Map, l.MapCrop, smw, smh, other) + 0.5;
                    bool rows = tc.Rows.Count == 3 &&
                                tc.Rows[0].Label == "TIDE" && tc.Rows[0].Height == "4.9 ft" && tc.Rows[0].Change == "\u2191" && tc.Rows[0].Time == "" &&
                                tc.Rows[1].Label == "HIGH" && tc.Rows[1].Height == "6.3 ft" && tc.Rows[1].Change == "+5.7" && tc.Rows[1].Time.Length > 0 &&
                                tc.Rows[2].Label == "LOW" && tc.Rows[2].Height == "2.1 ft" && tc.Rows[2].Change == "\u22124.2";
                    bool inside = tc.LabelX > tc.Panel.Left && tc.ChangeRight < tc.Panel.Right && tc.Spark.Top > tc.Panel.Top &&
                                  tc.Rows[2].Baseline < tc.Panel.Bottom && tc.Spark.Right <= tc.HeightRight - 10;
                    Check("tide: the sample's table sits in the top left corner, over the sea: now (4.9 ft, rising, the day's curve), then the next high and low with times, heights and changes (+5.7, \u22124.2)",
                          corner && rows && inside && Math.Abs(tc.Curve[tc.Now].X - nowX) < 0.5f &&
                          CurtainPainter.Feet(-0.04, false) == "0.0" && CurtainPainter.Feet(-0.2, false) == "\u22120.2");

                    // Two more temperatures under that corner: now the east one hides fewer, so the inset moves there.
                    DisplayState busy = CurtainPreview.Sample(po, "idle");
                    GeoBox bc = l.MapCrop;
                    List<MapStation> more = new List<MapStation>(busy.Map.Stations);
                    for (int i = 0; i < 2; i++)
                    {
                        float x = tc.Panel.Left + tc.Panel.Width * (0.2f + 0.6f * i), y = tc.Panel.Top + tc.Panel.Height * 0.5f;
                        more.Add(new MapStation { Id = "S9" + i, TempF = 70 + i, Utc = busy.Now.ToUniversalTime(),
                                                  Lat = bc.North - y / l.Map.Height * (bc.North - bc.South), Lon = bc.West + x / l.Map.Width * (bc.East - bc.West) });
                    }
                    busy.Map.Stations = more.ToArray();
                    CurtainPainter.Layout bl = CurtainPainter.Compose(busy, 2560, 1440);
                    Check("tide: with more temperatures under the west corner, the inset takes the east one, clear of every label",
                          bl.MapTide != null && bl.MapTide.Panel.Left > bl.Map.Width / 2f && !bl.MapStations.Exists(p => p.Box.IntersectsWith(bl.MapTide.Panel)));
                    bl.Dispose();
                }
                l.Dispose();

                // A picture to share: no home on the map.
                DisplayState shared = CurtainPreview.Sample(new Options { LocalDir = po.LocalDir, PreviewHideHome = true }, "idle");
                CurtainPainter.Layout hl = CurtainPainter.Compose(shared, 2560, 1440);
                Check("preview --hide-home: home is left off the map", shared.HideHome && float.IsNaN(hl.MapHome.X));
                hl.Dispose();

                WeatherMap relief = s.Map;
                WeatherMap flat = new WeatherMap { Box = relief.Box, BaseW = relief.BaseW, BaseH = relief.BaseH, Water = relief.Water, BaseId = "selftest-flat" };
                GeoBox c = WeatherMap.Crop(relief.Box, s.Lat, s.Lon, 600, 300);
                int[] withRelief = MapArt.GetPixels(MapArt.Base(relief, c, 600, 300)), without = MapArt.GetPixels(MapArt.Base(flat, c, 600, 300));
                int differ = 0;
                for (int i = 0; i < withRelief.Length; i++) if (withRelief[i] != without[i]) differ++;
                lines.Add("INFO  relief changes " + (100 * differ / withRelief.Length) + "% of the sample map's pixels");
                Check("relief: hills and contours are drawn into the map's ground", differ > withRelief.Length / 10);

                WeatherMap bare = new WeatherMap { Box = relief.Box, BaseW = relief.BaseW, BaseH = relief.BaseH, Water = relief.Water,
                                                   Elevation = relief.Elevation, DemW = relief.DemW, DemH = relief.DemH, DemId = relief.DemId, BaseId = "selftest-bare" };
                int[] bareGround = MapArt.GetPixels(MapArt.Base(bare, c, 600, 300));
                int marshPx = 0, creekPx = 0;
                for (int i = 0; i < withRelief.Length; i++) if (withRelief[i] != bareGround[i]) { if (MapArt.MarshDash(i % 600, i / 600)) marshPx++; else creekPx++; }
                lines.Add("INFO  marsh dashes on " + marshPx + " pixels, creeks and marsh ground on " + creekPx);
                Check("map: the sample's marsh and creeks are drawn into the ground", relief.Marsh.Length > 0 && relief.Streams.Length > 0 && marshPx > 50 && creekPx > 500);
                // The same map with every creek seasonal: the year-round ones must be what makes it bluer.
                WeatherMap allSeasonal = new WeatherMap { Box = relief.Box, BaseW = relief.BaseW, BaseH = relief.BaseH, Water = relief.Water,
                                                          Marsh = relief.Marsh, Streams = relief.Streams, Elevation = relief.Elevation,
                                                          DemW = relief.DemW, DemH = relief.DemH, DemId = relief.DemId, BaseId = "selftest-seasonal" };
                int[] seasonalGround = MapArt.GetPixels(MapArt.Base(allSeasonal, c, 600, 300));
                int bluer = 0, seasonalBlue = 0;
                for (int i = 0; i < withRelief.Length; i++)
                {
                    int p = withRelief[i], q = seasonalGround[i], z = bareGround[i];
                    if ((p & 255) - ((p >> 16) & 255) > (q & 255) - ((q >> 16) & 255) + 6) bluer++;
                    if (q != z && (q & 255) - ((q >> 16) & 255) > 4) seasonalBlue++;
                }
                lines.Add("INFO  year-round creeks make " + bluer + " pixels bluer; seasonal creeks and marsh tint " + seasonalBlue);
                Check("map: year-round creeks are drawn a clearer blue than seasonal ones, which are a dim grey-blue", bluer > 100 && seasonalBlue > 500);
                Check("map: open water is a deep navy, darker and greyer than the faintest rain",
                      MapArt.WaterTone.B > MapArt.WaterTone.R && MapArt.WaterTone.B < MapArt.RainQuiet.B / 2 &&
                      MapArt.YearRoundTone.B - MapArt.YearRoundTone.R > MapArt.SeasonalTone.B - MapArt.SeasonalTone.R);
            }
        }

        private static void SaveSolid(string path, int w, int h, Func<int, int, Color> colour)
        {
            using (Bitmap b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) b.SetPixel(x, y, colour(x, y));
                b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
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
