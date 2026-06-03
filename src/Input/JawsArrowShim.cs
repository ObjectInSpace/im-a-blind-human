using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NoImNotAHumanAccess.InputShim
{
    /// <summary>
    /// Lets the GAME receive the arrow keys when JAWS is running. JAWS installs a global low-level keyboard hook
    /// and consumes the arrows for its own reading commands BEFORE they reach this Unity app, so in menus/dialogue
    /// (where the mod relies on the game's own InputSystemUIInputModule to read the arrow) navigation dies under
    /// JAWS. NVDA passes them through, so this is JAWS-specific. (3D free-roam is unaffected: there the mod polls
    /// Input.GetKeyDown directly.)
    ///
    /// We cannot make JAWS's hook "pass the key through" — Windows hooks can't veto each other, and the only per-app
    /// JAWS switch (sleep mode) that releases the arrows ALSO silences the mod's announcements. So this shim is a
    /// PURE RELAY, not a navigator:
    ///   1. Install our OWN WH_KEYBOARD_LL hook. Because LL hooks are called most-recently-installed-first and the
    ///      game (with this mod) launches after JAWS is already running, our hook runs BEFORE JAWS's.
    ///   2. For an arrow key while the game window is foreground, we SUPPRESS the real event (return 1, do not chain)
    ///      so JAWS never sees it, then immediately RE-INJECT an identical hardware-scancode keystroke via SendInput.
    ///   3. The re-injected key carries a hardware scancode (KEYEVENTF_SCANCODE), so Unity's input backend accepts
    ///      it as a genuine keypress and the game navigates itself — exactly as it would with no screen reader.
    ///   4. Our re-injected events are tagged (InjectedSignature in dwExtraInfo) and passed straight through our own
    ///      hook, so we never recurse.
    ///
    /// The mod does NOT read, interpret, or act on the arrows here — it only stops JAWS from eating them. Confirmed
    /// in-game: arrows navigate menus/dialogue under JAWS AND the mod still speaks.
    ///
    /// KNOWN TRADEOFF: because Unity reads RAW input (not window messages), the relay must re-inject via SendInput,
    /// which is visible to JAWS's own keyboard hook and desyncs its keyboard processing — so while the relay is on,
    /// JAWS can't interrupt its speech (Ctrl-to-silence and fast next-arrow interrupt stop working). This is intrinsic
    /// to co-existing with JAWS on a raw-input game; a passive (non-re-injecting) hook can't deliver the key because
    /// JAWS would still suppress it. The F11 toggle fully installs/uninstalls the hook so the user can choose between
    /// "arrows work" (on) and "JAWS interrupt works" (off). OFF BY DEFAULT — the hook is never installed until a JAWS
    /// user opts in with F11, so NVDA users (who already have working arrows + interrupt) are completely unaffected.
    /// See jaws/README.md.
    /// </summary>
    public sealed class JawsArrowShim : IDisposable
    {
        // ---- Win32 ----
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        // Tag on our own injected events so our hook passes them through instead of re-processing (anti-recursion).
        private static readonly UIntPtr InjectedSignature = unchecked((UIntPtr)0x4E494D48); // 'NIMH'

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;          // 1 == INPUT_KEYBOARD
            public KEYBDINPUT ki;
            // Pad so the struct is large enough for the union (mouse input is the largest member). On x64 the
            // KEYBDINPUT path is the largest of what we use; the explicit padding keeps SendInput's cbSize happy.
            public ulong padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        // Keep the delegate alive for the lifetime of the hook (GC must not collect it).
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private bool _enabled;

        public JawsArrowShim() => _proc = HookCallback;

        public bool IsInstalled => _hook != IntPtr.Zero;

        /// <summary>Install the low-level keyboard hook. Safe to call once; logs and no-ops on failure.</summary>
        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                // hMod = IntPtr.Zero, dwThreadId = 0 -> a global LL hook owned by this process. LL hooks don't need
                // the module handle of an injected DLL (unlike WH_KEYBOARD), so Zero is correct here.
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
                if (_hook == IntPtr.Zero)
                    MelonLogger.Warning($"[JawsArrowShim] SetWindowsHookEx failed (err={Marshal.GetLastWin32Error()}); arrows may stay JAWS-captured.");
                else
                {
                    _enabled = true;
                    MelonLogger.Msg("[JawsArrowShim] keyboard hook installed (arrow relay active).");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[JawsArrowShim] Install threw: {e.Message}");
            }
        }

        /// <summary>
        /// Fully INSTALL or UNINSTALL the hook (not just a flag flip). This makes the F11 toggle a true A/B test of
        /// the hook's PRESENCE in the LL chain — to check whether merely having our hook installed disturbs JAWS's
        /// keyboard processing (e.g. Ctrl-to-silence). "On" = hook present + arrow relay active; "off" = hook removed
        /// entirely, so JAWS sees the raw keyboard exactly as if this mod's shim didn't exist.
        /// </summary>
        public void SetEnabled(bool on)
        {
            if (on)
            {
                Install();
                _enabled = _hook != IntPtr.Zero;
            }
            else
            {
                // Remove the hook from the chain completely, so this is a real "no hook" comparison.
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                _enabled = false;
                MelonLogger.Msg("[JawsArrowShim] hook UNINSTALLED (raw keyboard; JAWS unaffected by shim).");
            }
        }

        public bool Enabled => _enabled;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode < 0 || !_enabled) return CallNextHookEx(_hook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (!isKeyDown && !isKeyUp) return CallNextHookEx(_hook, nCode, wParam, lParam);

                KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // Our own re-injected events: pass straight through (prevents recursion).
                if (data.dwExtraInfo == InjectedSignature)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                if (!IsArrow(data.vkCode)) return CallNextHookEx(_hook, nCode, wParam, lParam);

                // Only act when OUR game window is foreground — never touch keys meant for other apps.
                if (!IsGameForeground()) return CallNextHookEx(_hook, nCode, wParam, lParam);

                // Suppress the real event so JAWS (later in the LL chain) never sees it, then re-inject an identical
                // hardware-scancode keystroke for the game. Returning 1 without chaining stops delivery to JAWS AND
                // the default target — re-injection is what delivers it to the game.
                ReinjectArrow(data.vkCode, isKeyUp);
                return (IntPtr)1;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[JawsArrowShim] HookCallback threw: {e.Message}");
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }
        }

        private static bool IsArrow(uint vk) => vk == VK_LEFT || vk == VK_UP || vk == VK_RIGHT || vk == VK_DOWN;

        /// <summary>Hardware scancodes for the arrow cluster (extended keys). Set 1 make codes.</summary>
        private static ushort ScanFor(uint vk) => vk switch
        {
            VK_UP => 0x48,
            VK_DOWN => 0x50,
            VK_LEFT => 0x4B,
            VK_RIGHT => 0x4D,
            _ => 0
        };

        private static void ReinjectArrow(uint vk, bool keyUp)
        {
            ushort scan = ScanFor(vk);
            if (scan == 0) return;

            uint flags = KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY;
            if (keyUp) flags |= KEYEVENTF_KEYUP;

            var inputs = new INPUT[1];
            inputs[0].type = 1; // INPUT_KEYBOARD
            inputs[0].ki = new KEYBDINPUT
            {
                wVk = 0,            // 0 with KEYEVENTF_SCANCODE: deliver by scancode, the form Unity reads as hardware
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = InjectedSignature
            };

            uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
            if (sent != 1)
                MelonLogger.Warning($"[JawsArrowShim] SendInput sent {sent}/1 (err={Marshal.GetLastWin32Error()}).");
        }

        private static bool IsGameForeground()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == GetCurrentProcessId();
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                MelonLogger.Msg("[JawsArrowShim] keyboard hook removed.");
            }
        }
    }
}
