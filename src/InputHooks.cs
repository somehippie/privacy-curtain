using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeCurtain
{
    /// <summary>
    /// Low-level keyboard and mouse hooks on their own thread.
    ///
    /// Physical input is swallowed. Injected input (SendInput and friends, which is how Claude clicks
    /// and types) carries the INJECTED flag and is passed straight through.
    ///
    /// Safety properties this relies on, all provided by Windows rather than by this code:
    ///  - Hooks belong to this process. If the process dies for any reason they are gone instantly.
    ///  - If this thread ever stops answering, Windows drops the hooks by itself (LowLevelHooksTimeout).
    ///  - Ctrl+Alt+Del is handled before hooks run, and the power button is not keyboard input at all.
    ///
    /// The callbacks do no allocation, no I/O and no locking beyond one tiny queue push.
    /// </summary>
    internal sealed class InputHooks
    {
        private const uint MsgPing = (uint)Native.WM_APP + 1;
        private const uint MsgRehook = (uint)Native.WM_APP + 2;

        private readonly KeyQueue queue;
        private readonly bool pinFromInjected;
        private readonly Native.HookProc keyboardProc;   // fields keep the delegates alive for native code
        private readonly Native.HookProc mouseProc;
        private readonly bool[] heldAtStart = new bool[256];
        private readonly ManualResetEvent started = new ManualResetEvent(false);

        private Thread thread;
        private volatile uint threadId;
        private IntPtr keyboardHook = IntPtr.Zero;
        private IntPtr mouseHook = IntPtr.Zero;
        private volatile string startError;
        private volatile int ack;
        private volatile bool faulted;
        private volatile int rehookFailures;
        private volatile int activity;
        private volatile bool freezeForTest;

        // Written only by the hook thread; read elsewhere for the log.
        private long blockedKeys;
        private long blockedButtons;
        private long blockedMoves;
        private long passedKeys;
        private long passedMouse;
        private long keyPresses;      // blocked physical key-downs (a held key repeats)
        private long buttonPresses;   // blocked physical mouse button-downs

        public long KeyPresses { get { return Interlocked.Read(ref keyPresses); } }
        public long ButtonPresses { get { return Interlocked.Read(ref buttonPresses); } }
        public long Moves { get { return Interlocked.Read(ref blockedMoves); } }

        public InputHooks(KeyQueue queue, bool pinFromInjected)
        {
            this.queue = queue;
            this.pinFromInjected = pinFromInjected;
            keyboardProc = new Native.HookProc(KeyboardProc);
            mouseProc = new Native.HookProc(MouseProc);
        }

        /// <summary>Counter the control thread watches to prove this thread is still pumping messages.</summary>
        public int Ack { get { return ack; } }
        public bool Faulted { get { return faulted; } }
        public int RehookFailures { get { return rehookFailures; } }

        /// <summary>Bumps on every blocked physical key press or mouse button press (not on mouse moves).</summary>
        public int Activity { get { return activity; } }

        /// <summary>Returns null when both hooks are installed, otherwise the reason they are not.</summary>
        public string Start(int timeoutMs)
        {
            thread = new Thread(ThreadMain);
            thread.IsBackground = true;
            thread.Name = "curtain-hooks";
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
            if (!started.WaitOne(timeoutMs)) return "the hook thread did not start in time";
            return startError;
        }

        public void Ping() { Post(MsgPing); }

        /// <summary>
        /// Re-installs both hooks. Windows can silently remove a low-level hook after timeouts; doing this
        /// on a schedule heals that, and keeps this hook first in the chain.
        /// </summary>
        public void RequestRehook() { Post(MsgRehook); }

        public void FreezeForTest() { freezeForTest = true; }

        public void Stop(int joinMs)
        {
            uint id = threadId;
            if (id != 0) Native.PostThreadMessage(id, (uint)Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            try
            {
                if (thread != null && thread != Thread.CurrentThread) thread.Join(joinMs);
            }
            catch { }
        }

        public string Stats()
        {
            return "blockedKeys=" + Interlocked.Read(ref blockedKeys) +
                   " blockedButtons=" + Interlocked.Read(ref blockedButtons) +
                   " blockedMoves=" + Interlocked.Read(ref blockedMoves) +
                   " injectedKeys=" + Interlocked.Read(ref passedKeys) +
                   " injectedMouse=" + Interlocked.Read(ref passedMouse);
        }

        private void Post(uint message)
        {
            uint id = threadId;
            if (id != 0) Native.PostThreadMessage(id, message, IntPtr.Zero, IntPtr.Zero);
        }

        private void ThreadMain()
        {
            try
            {
                threadId = Native.GetCurrentThreadId();
                Native.MSG msg;
                Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);   // makes sure this thread has a message queue

                // A key or button that is already down when the curtain starts must be allowed to come
                // back up once, or Windows would think it is stuck (a held Ctrl would turn Claude's typing
                // into shortcuts).
                for (int vk = 1; vk < 256; vk++)
                {
                    heldAtStart[vk] = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                }

                string error = Install();
                if (error != null)
                {
                    startError = error;
                    return;
                }
                started.Set();

                while (true)
                {
                    int result = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                    if (result == 0 || result == -1) break;
                    if (msg.hwnd == IntPtr.Zero && msg.message == MsgPing)
                    {
                        if (freezeForTest) Thread.Sleep(Timeout.Infinite);
                        ack = ack + 1;
                        continue;
                    }
                    if (msg.hwnd == IntPtr.Zero && msg.message == MsgRehook)
                    {
                        Reinstall();
                        continue;
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                faulted = true;
                startError = ex.Message;
                Log.Write("hook thread failed: " + ex);
            }
            finally
            {
                Uninstall();
                started.Set();
            }
        }

        private string Install()
        {
            IntPtr module = Native.GetModuleHandle(null);
            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (keyboardHook == IntPtr.Zero)
                return "SetWindowsHookEx(WH_KEYBOARD_LL) failed, error " + Marshal.GetLastWin32Error();
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            if (mouseHook == IntPtr.Zero)
                return "SetWindowsHookEx(WH_MOUSE_LL) failed, error " + Marshal.GetLastWin32Error();
            return null;
        }

        private void Reinstall()
        {
            // New hook first, then drop the old one, so there is never a moment with no hook.
            IntPtr module = Native.GetModuleHandle(null);
            bool ok = true;

            IntPtr k = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (k != IntPtr.Zero)
            {
                IntPtr old = keyboardHook;
                keyboardHook = k;
                if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
            }
            else ok = false;

            IntPtr m = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            if (m != IntPtr.Zero)
            {
                IntPtr old = mouseHook;
                mouseHook = m;
                if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
            }
            else ok = false;

            rehookFailures = ok ? 0 : rehookFailures + 1;
        }

        private void Uninstall()
        {
            if (keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
            if (mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode != 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            try
            {
                // KBDLLHOOKSTRUCT: vkCode @0, scanCode @4, flags @8
                int vk = Marshal.ReadInt32(lParam, 0) & 0xFF;
                int flags = Marshal.ReadInt32(lParam, 8);
                bool up = (flags & Native.LLKHF_UP) != 0;

                if ((flags & (Native.LLKHF_INJECTED | Native.LLKHF_LOWER_IL_INJECTED)) != 0)
                {
                    passedKeys++;
                    if (pinFromInjected) Feed(vk, up);   // automated tests only
                    return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }

                if (up && heldAtStart[vk])
                {
                    heldAtStart[vk] = false;
                    return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }

                if (!up) { activity = activity + 1; keyPresses++; }
                Feed(vk, up);
                blockedKeys++;
                return (IntPtr)1;
            }
            catch
            {
                // Never trap input because of a bug here: let the event through and flag it.
                faulted = true;
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }
        }

        private void Feed(int vk, bool up)
        {
            bool digit = (vk >= 0x30 && vk <= 0x39) || (vk >= 0x60 && vk <= 0x69);
            if (digit || vk == Native.VK_BACK || vk == Native.VK_ESCAPE)
            {
                if (!up) queue.Push(vk);
                return;
            }
            if (vk == Native.VK_RETURN)
            {
                queue.Push(up ? (vk | KeyQueue.FlagUp) : vk);
            }
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode != 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            try
            {
                // MSLLHOOKSTRUCT: pt @0 (8 bytes), mouseData @8, flags @12
                int flags = Marshal.ReadInt32(lParam, 12);
                if ((flags & (Native.LLMHF_INJECTED | Native.LLMHF_LOWER_IL_INJECTED)) != 0)
                {
                    passedMouse++;
                    return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }

                int message = (int)wParam.ToInt64();
                if (message == Native.WM_MOUSEMOVE)
                {
                    blockedMoves++;
                    return (IntPtr)1;
                }

                int releasedVk = 0;
                switch (message)
                {
                    case Native.WM_LBUTTONUP: releasedVk = Native.VK_LBUTTON; break;
                    case Native.WM_RBUTTONUP: releasedVk = Native.VK_RBUTTON; break;
                    case Native.WM_MBUTTONUP: releasedVk = Native.VK_MBUTTON; break;
                    case Native.WM_XBUTTONUP:
                        int which = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;
                        releasedVk = which == 1 ? Native.VK_XBUTTON1 : (which == 2 ? Native.VK_XBUTTON2 : 0);
                        break;
                    case Native.WM_LBUTTONDOWN:
                    case Native.WM_RBUTTONDOWN:
                    case Native.WM_MBUTTONDOWN:
                    case Native.WM_XBUTTONDOWN:
                        activity = activity + 1;
                        buttonPresses++;
                        break;
                }

                if (releasedVk != 0 && heldAtStart[releasedVk])
                {
                    heldAtStart[releasedVk] = false;
                    return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }

                blockedButtons++;   // buttons and wheel
                return (IntPtr)1;
            }
            catch
            {
                faulted = true;
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }
        }
    }
}
