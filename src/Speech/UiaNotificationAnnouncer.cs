using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NoImNotAHumanAccess.Speech
{
    /// <summary>
    /// DEAD END — KEPT FOR REFERENCE, NOT WIRED IN. Confirmed in-game 2026-06-04: notifications raised this way
    /// reach NO screen reader (NVDA silent, JAWS unchanged). Cause: this app's window UIA provider belongs to
    /// Unity's native engine; the host provider we get from UiaHostProviderFromHwnd is NOT the provider a reader
    /// connects to, so events raised from it are delivered to nobody (UiaRaiseNotificationEvent still returns 0).
    /// Unity exposes no priority-aware announcement and no borrowable provider, so there is no cheap interrupt
    /// path. The real interrupt route is the native AccessibilityHierarchy (focus-change through the connected
    /// hierarchy) — see memory project-nimnah-native-accessibility-hierarchy. Do NOT re-wire this expecting it to
    /// work without first solving the provider-connection problem (WM_GETOBJECT subclassing).
    ///
    /// Speech channel that raises a UI Automation notification ourselves, instead of going through Unity's
    /// <see cref="NativeAnnouncer"/> (<c>AccessibilityManager.SendAnnouncementNotification</c>).
    ///
    /// WHY THIS EXISTS — the interrupt problem. Unity's announcement raises a UIA notification with a HARDCODED
    /// processing mode (its interop exposes no <c>NotificationProcessing</c> field), so each line is QUEUED. Under
    /// JAWS that means a new announcement can't cut off the prior one: fast arrow-stepping backs up a queue and
    /// the user can't skip a long line. JAWS DOES honor <c>NotificationProcessing_MostRecent</c> (=3) — it cancels
    /// the current utterance and replaces anything pending — so if WE raise the notification with that mode, the
    /// "next arrow interrupts the prior item" behavior is restored. That is the whole point of this class.
    ///
    /// SCOPE — this channel is only made live alongside the F11 JAWS arrow relay (the relay is what creates the
    /// fast-stepping backlog in the first place; NVDA/Narrator users keep the proven NativeAnnouncer). See
    /// <c>AccessMod</c> for the swap and <c>jaws/README.md</c>.
    ///
    /// MECHANISM — <c>UiaRaiseNotificationEvent</c> (UIAutomationCore.dll, Win8.1+) needs an
    /// <c>IRawElementProviderSimple</c> as the event source. The provider does NOT have to expose a real UIA tree:
    /// for a notification event a minimal "host" provider tied to the game's top-level window is sufficient — the
    /// screen reader only consumes the notification's text + processing hint. We build that minimal provider here,
    /// resolve the game window once, and raise the event.
    /// </summary>
    public sealed class UiaNotificationAnnouncer : ISpeechOutput
    {
        // ---- UIA notification enums (mirror UIAutomationCore) ----
        // AutomationNotificationKind: ItemAdded=0, ItemRemoved=1, ActionCompleted=2, ActionAborted=3, Other=4.
        private const int NotificationKind_Other = 4;
        // AutomationNotificationProcessing: ImportantAll=0, ImportantMostRecent=1, All=2, MostRecent=3,
        // CurrentThenMostRecent=4. MostRecent (3) is the one JAWS honors as "cancel current + replace pending".
        private const int NotificationProcessing_MostRecent = 3;

        // UiaRaiseNotificationEvent(IRawElementProviderSimple, NotificationKind, NotificationProcessing,
        //                           BSTR displayString, BSTR activityId) -> HRESULT
        [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
        private static extern int UiaRaiseNotificationEvent(
            [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider,
            int notificationKind,
            int notificationProcessing,
            [MarshalAs(UnmanagedType.BStr)] string displayString,
            [MarshalAs(UnmanagedType.BStr)] string activityId);

        // Cheap presence probe: if the export resolves, the OS supports the notification API.
        [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode, EntryPoint = "UiaHostProviderFromHwnd")]
        private static extern int UiaHostProviderFromHwnd(IntPtr hwnd,
            [MarshalAs(UnmanagedType.Interface)] out IRawElementProviderSimple provider);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        private IRawElementProviderSimple? _provider;
        private bool _resolved;
        private string _activityId = "nimnah-a11y";

        public string Name => "UIA notification (MostRecent)";

        public UiaNotificationAnnouncer() => TryResolveProvider();

        public bool IsAvailable => _resolved && _provider != null;

        public void Speak(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // The game window can come up after us, or we can be constructed before it's foreground; resolve lazily.
            if (_provider == null) TryResolveProvider();
            if (_provider == null)
            {
                Log("No UIA provider (game window not found); cannot speak.");
                return;
            }

            try
            {
                // We always use MostRecent — interrupt is the entire reason this channel exists; the queued
                // behavior is what NativeAnnouncer already gives. The `interrupt` arg is honored implicitly.
                int hr = UiaRaiseNotificationEvent(
                    _provider, NotificationKind_Other, NotificationProcessing_MostRecent, text, _activityId);
                if (hr != 0)
                    Log($"UiaRaiseNotificationEvent returned 0x{hr:X8}.");
            }
            catch (Exception e)
            {
                Log($"Speak threw: {e.Message}");
                // A stale provider (window recreated) — drop it so the next Speak re-resolves.
                _provider = null;
            }
        }

        /// <summary>
        /// Build the minimal host provider for the game's top-level window. We use the OS-supplied host provider
        /// (UiaHostProviderFromHwnd) as the notification source: it is a real IRawElementProviderSimple bound to the
        /// HWND, which is all the notification API requires — no custom UIA tree needed.
        /// </summary>
        private void TryResolveProvider()
        {
            try
            {
                IntPtr hwnd = ResolveGameWindow();
                if (hwnd == IntPtr.Zero)
                {
                    // Not fatal: try again on the next Speak when the window is up.
                    _resolved = true; // the API itself is present; only the window is pending
                    return;
                }

                int hr = UiaHostProviderFromHwnd(hwnd, out IRawElementProviderSimple provider);
                if (hr != 0 || provider == null)
                {
                    Log($"UiaHostProviderFromHwnd failed (hr=0x{hr:X8}).");
                    _resolved = true;
                    return;
                }

                _provider = provider;
                _resolved = true;
                MelonLogger.Msg($"[UiaNotificationAnnouncer] host provider bound to hwnd 0x{hwnd.ToInt64():X}.");
            }
            catch (DllNotFoundException)
            {
                Log("UIAutomationCore.dll not available on this OS; channel unavailable.");
                _resolved = false;
            }
            catch (Exception e)
            {
                Log($"TryResolveProvider threw: {e.Message}");
                _resolved = false;
            }
        }

        /// <summary>
        /// Find this process's top-level window. Prefer the foreground window when it belongs to us (the common
        /// case while playing), else fall back to GetActiveWindow (this thread's active window).
        /// </summary>
        private static IntPtr ResolveGameWindow()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == GetCurrentProcessId()) return fg;
            }
            return GetActiveWindow();
        }

        private static void Log(string msg) => MelonLogger.Warning($"[UiaNotificationAnnouncer] {msg}");
    }

    /// <summary>
    /// Minimal managed declaration of the UIA <c>IRawElementProviderSimple</c> COM interface — just enough for the
    /// runtime to marshal the OS host provider we receive from <c>UiaHostProviderFromHwnd</c> and hand back to
    /// <c>UiaRaiseNotificationEvent</c>. We never implement it ourselves; we only hold the OS-supplied instance.
    /// </summary>
    [ComImport]
    [Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRawElementProviderSimple
    {
        // Order matters: this is the COM vtable layout. We don't call these, but the signatures must be present
        // so the CLR builds the correct interop stub.
        int ProviderOptions { get; }
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetPatternProvider(int patternId);
        object? GetPropertyValue(int propertyId);
        IRawElementProviderSimple? HostRawElementProvider { get; }
    }
}
