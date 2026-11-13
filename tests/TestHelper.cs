using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeCurtain.Tests
{
    /// <summary>
    /// Command-line helper for test-curtain.ps1. Not part of the curtain and never started by it.
    /// Everything it sends is ordinary injected input (SendInput), the same kind Claude's own clicks
    /// and typing are, which is exactly what the curtain has to let through.
    /// </summary>
    internal static class TestHelper
    {
        [STAThread]
        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            if (args.Length == 0)
            {
                Console.WriteLine("commands: makepin inspect sendmsg locked fg idle cursor move setpos click text keys capture harness");
                return 2;
            }
            try
            {
                switch (args[0])
                {
                    case "makepin": return MakePin(args);
                    case "checkpin":
                        Console.WriteLine(ClaudeCurtain.PinStore.Load(args[1]).Verify(args[2]) ? "match" : "nomatch");
                        return 0;
                    case "inspect": return Inspect(args);
                    case "sendmsg": return SendMsg(args);
                    case "locked": return Locked();
                    case "execstate":
                        // What Windows is currently being asked to keep awake, summed over every app:
                        // bit 1 = system (no sleep), bit 2 = display (no blanking, no screen saver).
                        uint es;
                        uint status = CallNtPowerInformation(16 /* SystemExecutionState */, IntPtr.Zero, 0, out es, 4);
                        Console.WriteLine(status == 0 ? (es & 3).ToString() : "error " + status);
                        return 0;
                    case "displaywatch": return DisplayWatch(args);
                    case "fg": return ForegroundInfo();
                    case "idle": return Idle();
                    case "cursor": return CursorPos();
                    case "move": return Move(args, false);
                    case "setpos": return Move(args, true);
                    case "click": return Click(args);
                    case "text": return TypeText(args);
                    case "keys": return KeyTaps(args);
                    case "capture": return Capture(args);
                    case "harness": return Harness(args);
                }
                Console.WriteLine("unknown command " + args[0]);
                return 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + ex.Message);
                return 1;
            }
        }

        private static int Int(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }

        private static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ---------------------------------------------------------------- makepin
        // Writes a throwaway PIN hash with the curtain's own PinStore code and prints the PIN for the test run.
        private static int MakePin(string[] args)
        {
            byte[] raw = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            StringBuilder pin = new StringBuilder();
            foreach (byte b in raw) pin.Append((char)('0' + (b % 10)));
            ClaudeCurtain.PinStore.Save(args[1], pin.ToString());
            Console.WriteLine(pin.ToString());
            return 0;
        }

        // ---------------------------------------------------------------- inspect
        private static int Inspect(string[] args)
        {
            uint wanted = (uint)Int(args[1]);
            bool children = args.Length > 2 && args[2] == "--children";
            List<string> items = new List<string>();
            EnumWindowsProc cb = delegate(IntPtr hwnd, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != wanted) return true;

                RECT r;
                GetWindowRect(hwnd, out r);
                uint affinity;
                bool gotAffinity = GetWindowDisplayAffinity(hwnd, out affinity);
                uint key; byte alpha; uint lwaFlags;
                bool gotLayered = GetLayeredWindowAttributes(hwnd, out key, out alpha, out lwaFlags);

                StringBuilder sb = new StringBuilder("{");
                sb.Append("\"hwnd\":").Append(hwnd.ToInt64());
                sb.Append(",\"class\":").Append(Q(ClassOf(hwnd)));
                sb.Append(",\"title\":").Append(Q(TitleOf(hwnd)));
                sb.Append(",\"visible\":").Append(IsWindowVisible(hwnd) ? "true" : "false");
                sb.Append(",\"iconic\":").Append(IsIconic(hwnd) ? "true" : "false");
                sb.Append(",\"x\":").Append(r.Left).Append(",\"y\":").Append(r.Top);
                sb.Append(",\"w\":").Append(r.Right - r.Left).Append(",\"h\":").Append(r.Bottom - r.Top);
                sb.Append(",\"exstyle\":").Append((uint)GetWindowLong(hwnd, -20));
                sb.Append(",\"affinity\":").Append(gotAffinity ? affinity.ToString() : "-1");
                sb.Append(",\"alpha\":").Append(gotLayered ? alpha.ToString() : "-1");
                if (children)
                {
                    List<string> kids = new List<string>();
                    EnumWindowsProc childCb = delegate(IntPtr child, IntPtr l2)
                    {
                        RECT cr;
                        GetWindowRect(child, out cr);
                        kids.Add("{\"hwnd\":" + child.ToInt64() + ",\"class\":" + Q(ClassOf(child)) + ",\"text\":" + Q(TitleOf(child)) +
                                 ",\"x\":" + cr.Left + ",\"y\":" + cr.Top + ",\"w\":" + (cr.Right - cr.Left) + ",\"h\":" + (cr.Bottom - cr.Top) + "}");
                        return true;
                    };
                    EnumChildWindows(hwnd, childCb, IntPtr.Zero);
                    GC.KeepAlive(childCb);
                    sb.Append(",\"children\":[").Append(string.Join(",", kids.ToArray())).Append("]");
                }
                sb.Append("}");
                items.Add(sb.ToString());
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            Console.WriteLine("[" + string.Join(",", items.ToArray()) + "]");
            return 0;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TitleOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- sendmsg
        // sendmsg <hwnd> <msg> <wparam> <lparam> <timeoutMs>
        private static int SendMsg(string[] args)
        {
            IntPtr hwnd = new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture));
            uint msg = (uint)Int(args[2]);
            IntPtr wp = new IntPtr(long.Parse(args[3], CultureInfo.InvariantCulture));
            IntPtr lp = new IntPtr(long.Parse(args[4], CultureInfo.InvariantCulture));
            uint timeout = (uint)Int(args[5]);
            Stopwatch sw = Stopwatch.StartNew();
            IntPtr result;
            IntPtr ok = SendMessageTimeout(hwnd, msg, wp, lp, 0x0002 /* SMTO_ABORTIFHUNG */, timeout, out result);
            int error = Marshal.GetLastWin32Error();
            sw.Stop();
            Console.WriteLine("{\"ok\":" + (ok != IntPtr.Zero ? "true" : "false") + ",\"result\":" + result.ToInt64() +
                              ",\"error\":" + error + ",\"elapsedMs\":" + sw.ElapsedMilliseconds + "}");
            return 0;
        }

        // ---------------------------------------------------------------- locked
        // Prints "locked" when the interactive desktop is not the one receiving input (lock screen, sign-in, UAC).
        private static int Locked()
        {
            IntPtr desk = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            if (desk == IntPtr.Zero)
            {
                Console.WriteLine("locked");
                return 0;
            }
            StringBuilder name = new StringBuilder(256);
            int needed;
            bool got = GetUserObjectInformation(desk, 2 /* UOI_NAME */, name, name.Capacity * 2, out needed);
            CloseDesktop(desk);
            Console.WriteLine(got && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase) ? "unlocked" : "locked");
            return 0;
        }

        private static int ForegroundInfo()
        {
            IntPtr fg = GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
            string name = "";
            try { if (pid != 0) name = Process.GetProcessById((int)pid).ProcessName; } catch { }
            Console.WriteLine("{\"hwnd\":" + fg.ToInt64() + ",\"pid\":" + pid + ",\"process\":" + Q(name) + ",\"class\":" + Q(fg != IntPtr.Zero ? ClassOf(fg) : "") + "}");
            return 0;
        }

        private static int Idle()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            GetLastInputInfo(ref info);
            Console.WriteLine(((GetTickCount() - info.dwTime) / 1000).ToString());
            return 0;
        }

        private static int CursorPos()
        {
            POINT p;
            GetCursorPos(out p);
            Console.WriteLine(p.X + "," + p.Y);
            return 0;
        }

        // ---------------------------------------------------------------- injected input
        private static void MoveBySendInput(int x, int y)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            INPUT[] input = new INPUT[1];
            input[0].type = 0;
            input[0].u.mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
            input[0].u.mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
            input[0].u.mi.dwFlags = 0x0001 | 0x8000 | 0x4000;   // MOVE | ABSOLUTE | VIRTUALDESK
            Send(input);
        }

        private static int Move(string[] args, bool viaSetCursorPos)
        {
            int x = Int(args[1]), y = Int(args[2]);
            if (viaSetCursorPos) SetCursorPos(x, y);
            else MoveBySendInput(x, y);
            Thread.Sleep(60);
            return CursorPos();
        }

        private static int Click(string[] args)
        {
            int x = Int(args[1]), y = Int(args[2]);
            MoveBySendInput(x, y);
            Thread.Sleep(80);
            INPUT[] down = new INPUT[1];
            down[0].type = 0;
            down[0].u.mi.dwFlags = 0x0002;   // LEFTDOWN
            Send(down);
            Thread.Sleep(50);
            INPUT[] up = new INPUT[1];
            up[0].type = 0;
            up[0].u.mi.dwFlags = 0x0004;     // LEFTUP
            Send(up);
            Thread.Sleep(60);
            return CursorPos();
        }

        private static int TypeText(string[] args)
        {
            foreach (char c in args[1])
            {
                INPUT[] input = new INPUT[2];
                input[0].type = 1;
                input[0].u.ki.wScan = c;
                input[0].u.ki.dwFlags = 0x0004;            // UNICODE
                input[1].type = 1;
                input[1].u.ki.wScan = c;
                input[1].u.ki.dwFlags = 0x0004 | 0x0002;   // UNICODE | KEYUP
                Send(input);
                Thread.Sleep(15);
            }
            Console.WriteLine("ok");
            return 0;
        }

        // keys <vk,vk,...> [holdMs]   each key is pressed and released
        private static int KeyTaps(string[] args)
        {
            int hold = args.Length > 2 ? Int(args[2]) : 40;
            foreach (string part in args[1].Split(','))
            {
                ushort vk = (ushort)Int(part.Trim());
                INPUT[] down = new INPUT[1];
                down[0].type = 1;
                down[0].u.ki.wVk = vk;
                Send(down);
                Thread.Sleep(hold);
                INPUT[] up = new INPUT[1];
                up[0].type = 1;
                up[0].u.ki.wVk = vk;
                up[0].u.ki.dwFlags = 0x0002;
                Send(up);
                Thread.Sleep(40);
            }
            Console.WriteLine("ok");
            return 0;
        }

        private static void Send(INPUT[] input)
        {
            uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(INPUT)));
            if (sent != input.Length) throw new InvalidOperationException("SendInput sent " + sent + " of " + input.Length + " (error " + Marshal.GetLastWin32Error() + ")");
        }

        // ---------------------------------------------------------------- capture
        // capture <out.png | -> <x> <y> <w> <h>   the same GDI screen grab ordinary screenshot tools use
        private static int Capture(string[] args)
        {
            int x = Int(args[2]), y = Int(args[3]), w = Int(args[4]), h = Int(args[5]);
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
                long nonBlack = 0, total = 0;
                int step = Math.Max(1, Math.Min(w, h) / 200);
                for (int py = 0; py < h; py += step)
                {
                    for (int px = 0; px < w; px += step)
                    {
                        Color c = bmp.GetPixel(px, py);
                        total++;
                        if (c.R > 12 || c.G > 12 || c.B > 12) nonBlack++;
                    }
                }
                Color mid = bmp.GetPixel(w / 2, h / 2);
                if (args[1] != "-") bmp.Save(args[1], ImageFormat.Png);
                Console.WriteLine("{\"nonBlack\":" + (total == 0 ? 0 : (double)nonBlack / total).ToString("0.0000", CultureInfo.InvariantCulture) +
                                  ",\"center\":\"" + mid.R.ToString("X2") + mid.G.ToString("X2") + mid.B.ToString("X2") + "\"}");
            }
            return 0;
        }

        // ---------------------------------------------------------------- harness
        // harness <statusFile> <seconds> <x> <y>
        // A small always-on-top target window: a magenta square to find in a screenshot, a text box to type
        // into and a button to click. It reports what reached it by rewriting a small JSON status file.
        private static int Harness(string[] args)
        {
            string statusFile = args[1];
            int seconds = Int(args[2]);
            int x = Int(args[3]), y = Int(args[4]);
            Application.EnableVisualStyles();
            using (HarnessForm form = new HarnessForm(statusFile, seconds, x, y))
            {
                Application.Run(form);
            }
            return 0;
        }

        private sealed class HarnessForm : Form
        {
            private readonly string statusFile;
            private readonly DateTime closeAt;
            private readonly Panel panel = new Panel();
            private readonly TextBox box = new TextBox();
            private readonly Button button = new Button();
            private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            private int clicks;

            public HarnessForm(string statusFile, int seconds, int x, int y)
            {
                this.statusFile = statusFile;
                closeAt = DateTime.UtcNow.AddSeconds(seconds);
                Text = "Curtain test target";
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(x, y);
                ClientSize = new Size(460, 190);
                TopMost = true;
                ShowInTaskbar = false;

                panel.BackColor = Color.FromArgb(255, 0, 255);
                panel.SetBounds(16, 16, 150, 150);
                box.Multiline = true;
                box.SetBounds(186, 16, 256, 70);
                button.Text = "Click me";
                button.SetBounds(186, 104, 256, 62);
                button.Click += delegate { clicks++; };
                Controls.AddRange(new Control[] { panel, box, button });

                timer.Interval = 120;
                timer.Tick += delegate { Report(); };
                timer.Start();
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Report();
            }

            private static Point Center(Control c)
            {
                return c.PointToScreen(new Point(c.Width / 2, c.Height / 2));
            }

            private void Report()
            {
                if (DateTime.UtcNow >= closeAt || File.Exists(statusFile + ".stop"))
                {
                    timer.Stop();
                    Close();
                    return;
                }
                Point p = Center(panel), b = Center(box), k = Center(button);
                string json = "{\"hwnd\":" + Handle.ToInt64() +
                              ",\"foreground\":" + (GetForegroundWindow() == Handle ? "true" : "false") +
                              ",\"focused\":" + (box.Focused ? "true" : "false") +
                              ",\"panelX\":" + p.X + ",\"panelY\":" + p.Y +
                              ",\"boxX\":" + b.X + ",\"boxY\":" + b.Y +
                              ",\"buttonX\":" + k.X + ",\"buttonY\":" + k.Y +
                              ",\"clicks\":" + clicks +
                              ",\"text\":" + Q(box.Text) + "}";
                try
                {
                    string tmp = statusFile + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(statusFile)) File.Replace(tmp, statusFile, null);
                    else File.Move(tmp, statusFile);
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- displaywatch
        // displaywatch <seconds>   prints "off" if Windows turned the display off at any point, else "on".
        // If the display is off at the end it is woken again with a one-pixel injected mouse move.
        private static int DisplayWatch(string[] args)
        {
            using (DisplayWatchForm form = new DisplayWatchForm(Int(args[1])))
            {
                Application.Run(form);
                Console.WriteLine(form.WentOff ? "off" : "on");
            }
            return 0;
        }

        private sealed class DisplayWatchForm : Form
        {
            private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            private bool isOff;
            public bool WentOff;

            public DisplayWatchForm(int seconds)
            {
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                Opacity = 0;
                Size = new Size(1, 1);
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-2000, -2000);
                timer.Interval = seconds * 1000;
                timer.Tick += delegate
                {
                    timer.Stop();
                    if (isOff)
                    {
                        INPUT[] nudge = new INPUT[1];
                        nudge[0].type = 0;
                        nudge[0].u.mi.dx = 1;
                        nudge[0].u.mi.dwFlags = 0x0001;
                        try { Send(nudge); } catch { }
                    }
                    Close();
                };
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Guid consoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
                RegisterPowerSettingNotification(Handle, ref consoleDisplayState, 0);
                timer.Start();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0218 && m.WParam.ToInt64() == 0x8013 && m.LParam != IntPtr.Zero)
                {
                    // POWERBROADCAST_SETTING: GUID (16 bytes), DataLength (4), Data. 0 = off, 1 = on, 2 = dimmed.
                    int state = Marshal.ReadInt32(m.LParam, 20);
                    isOff = state == 0;
                    if (isOff) WentOff = true;
                }
                base.WndProc(ref m);
            }
        }

        // ---------------------------------------------------------------- Win32
        [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, out uint output, int outputLength);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, int length, out int needed);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("kernel32.dll")] private static extern uint GetTickCount();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
    }
}
