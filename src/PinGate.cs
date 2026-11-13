using System;
using System.Text;

namespace ClaudeCurtain
{
    /// <summary>
    /// Hands PIN-relevant key presses from the hook thread (and the hotkey fallback) to the control thread.
    /// The hook callback must return within a few milliseconds, so it only ever does this tiny push.
    /// </summary>
    internal sealed class KeyQueue
    {
        public const int FlagUp = 0x100;      // key release (only sent for Enter)
        public const int FlagSubmit = 0x200;  // "Enter" seen through the hotkey fallback: submit right away

        private readonly int[] buffer = new int[256];
        private readonly object gate = new object();
        private int head;
        private int tail;

        public readonly System.Threading.AutoResetEvent Signal = new System.Threading.AutoResetEvent(false);

        public void Push(int code)
        {
            lock (gate)
            {
                int next = (head + 1) & 255;
                if (next != tail)
                {
                    buffer[head] = code;
                    head = next;
                }
            }
            Signal.Set();
        }

        public bool TryPop(out int code)
        {
            lock (gate)
            {
                if (tail == head)
                {
                    code = 0;
                    return false;
                }
                code = buffer[tail];
                tail = (tail + 1) & 255;
                return true;
            }
        }
    }

    /// <summary>
    /// PIN entry state machine. Pure logic, no Win32, driven with an explicit clock so it can be tested.
    ///
    /// Digits (top row or numpad) build the PIN, Backspace removes one, Esc clears, Enter submits.
    /// Submission happens when Enter is RELEASED, so no key is still held down when the curtain closes.
    /// Five wrong tries in a row cost a 30 second pause. Nothing here can lock the PIN out for longer.
    /// </summary>
    internal sealed class PinGate
    {
        public const int StateIdle = 0;
        public const int StateTyping = 1;
        public const int StateWrong = 2;
        public const int StateCooldown = 3;

        public const int MaxBuffer = 16;
        public const int IdleClearMs = 8000;
        public const int WrongMessageMs = 2500;
        public const int CooldownEvery = 5;
        public const int CooldownMs = 30000;

        private readonly Func<string, bool> verify;
        private readonly StringBuilder buffer = new StringBuilder(MaxBuffer);
        private bool overflow;
        private bool enterArmed;
        private long lastKeyMs;
        private long messageUntilMs;
        private long cooldownUntilMs;
        private int wrongStreak;

        // Read by the UI thread for display only.
        private volatile int state;
        private volatile int digits;
        private volatile int cooldownLeft;
        private volatile int wrongTotal;

        public PinGate(Func<string, bool> verify)
        {
            this.verify = verify;
        }

        public int State { get { return state; } }
        public int Digits { get { return digits; } }
        public int CooldownSecondsLeft { get { return cooldownLeft; } }
        public int WrongTotal { get { return wrongTotal; } }

        /// <summary>Feeds one key event. Returns true when the correct PIN has just been submitted.</summary>
        public bool OnKey(int vk, bool up, bool submitNow, long nowMs)
        {
            Tick(nowMs);
            if (state == StateCooldown) return false;

            int digit = -1;
            if (vk >= 0x30 && vk <= 0x39) digit = vk - 0x30;        // top row 0-9
            else if (vk >= 0x60 && vk <= 0x69) digit = vk - 0x60;   // numpad 0-9

            if (digit >= 0)
            {
                if (up) return false;
                if (buffer.Length < MaxBuffer) buffer.Append((char)('0' + digit));
                else overflow = true;
                lastKeyMs = nowMs;
                enterArmed = false;
                digits = buffer.Length;
                state = StateTyping;
                return false;
            }

            if (vk == Native.VK_BACK)
            {
                if (up) return false;
                if (overflow)
                {
                    ClearBuffer();
                }
                else if (buffer.Length > 0)
                {
                    buffer.Length = buffer.Length - 1;
                    digits = buffer.Length;
                    lastKeyMs = nowMs;
                    if (buffer.Length == 0 && state == StateTyping) state = StateIdle;
                }
                enterArmed = false;
                return false;
            }

            if (vk == Native.VK_ESCAPE)
            {
                if (!up) ClearBuffer();
                return false;
            }

            if (vk == Native.VK_RETURN)
            {
                if (submitNow) return Submit(nowMs);
                if (!up)
                {
                    enterArmed = buffer.Length > 0 || overflow;
                    return false;
                }
                if (!enterArmed) return false;
                enterArmed = false;
                return Submit(nowMs);
            }

            return false;
        }

        /// <summary>Advances timers: ends the cooldown, hides the "wrong PIN" note, forgets a half-typed PIN.</summary>
        public void Tick(long nowMs)
        {
            if (state == StateCooldown)
            {
                long left = cooldownUntilMs - nowMs;
                if (left <= 0)
                {
                    cooldownLeft = 0;
                    state = StateIdle;
                }
                else
                {
                    cooldownLeft = (int)((left + 999) / 1000);
                }
                return;
            }
            if (state == StateWrong && nowMs >= messageUntilMs)
            {
                state = buffer.Length > 0 ? StateTyping : StateIdle;
            }
            if ((buffer.Length > 0 || overflow) && nowMs - lastKeyMs >= IdleClearMs)
            {
                ClearBuffer();
            }
        }

        private bool Submit(long nowMs)
        {
            if (buffer.Length == 0 && !overflow) return false;

            string candidate = buffer.ToString();
            bool tooLong = overflow;
            ClearBuffer();

            bool ok = false;
            if (!tooLong && candidate.Length >= PinStore.MinLength)
            {
                try { ok = verify(candidate); }
                catch { ok = false; }
            }
            if (ok)
            {
                wrongStreak = 0;
                return true;
            }

            wrongStreak++;
            wrongTotal = wrongTotal + 1;
            if (wrongStreak % CooldownEvery == 0)
            {
                cooldownUntilMs = nowMs + CooldownMs;
                cooldownLeft = CooldownMs / 1000;
                state = StateCooldown;
            }
            else
            {
                messageUntilMs = nowMs + WrongMessageMs;
                state = StateWrong;
            }
            return false;
        }

        private void ClearBuffer()
        {
            buffer.Length = 0;
            overflow = false;
            enterArmed = false;
            digits = 0;
            if (state == StateTyping) state = StateIdle;
        }
    }
}
