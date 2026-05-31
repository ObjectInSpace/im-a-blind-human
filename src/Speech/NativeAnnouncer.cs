using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine.Accessibility;

namespace NoImNotAHumanAccess.Speech
{
    /// <summary>
    /// Speech channel backed by Unity 6.3's native accessibility API. On Windows this raises UI Automation
    /// notifications, so output reaches whatever UIA-aware screen reader the player runs (NVDA, JAWS, Narrator)
    /// in that reader's own voice/rate settings — it is NOT Narrator-only. Verified in-game: spoke through NVDA
    /// with Narrator off, and without forcing any screen-reader status override (Unity detects the reader).
    ///
    /// Implementation note: the announcement entry point is
    /// <c>UnityEngine.Accessibility.AccessibilityManager.SendAnnouncementNotification(string)</c>. The interop
    /// assembly's public <c>string</c> wrapper is unusable in this Il2CppInterop build — it marshals through
    /// <c>Il2CppSystem.ReadOnlySpan&lt;char&gt;.GetPinnableReference()</c>, which is missing (confirmed at runtime:
    /// "Method not found: ...ReadOnlySpan`1.GetPinnableReference()"). So we bypass it: resolve the underlying
    /// internal call <c>AccessibilityManager::SendAnnouncementNotification_Injected</c> ourselves and invoke it with
    /// a span wrapper built over a pinned managed string. The _Injected method takes a pointer to a
    /// {char* begin; int length} struct — a stable native ABI, independent of the broken span helper.
    /// Phase 3 (navigable menus) will additionally build an AccessibilityHierarchy; announcements do not need it.
    /// </summary>
    public sealed class NativeAnnouncer : ISpeechOutput
    {
        // Mirrors Il2CppInterop's ManagedSpanWrapper { void* begin; int length } and the _Injected delegate
        // shape (a single pointer to that struct).
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct SpanWrapper { public void* Begin; public int Length; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SendAnnouncementInjected(IntPtr spanWrapperPtr);

        private SendAnnouncementInjected? _sendAnnouncement;

        public string Name => "Native (Unity AssistiveSupport)";

        public NativeAnnouncer()
        {
            ResolveSendAnnouncement();
        }

        public bool IsAvailable => _sendAnnouncement != null;

        public unsafe void Speak(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (_sendAnnouncement == null)
            {
                Log("SendAnnouncementNotification not resolved; cannot speak.");
                return;
            }
            try
            {
                fixed (char* begin = text)
                {
                    SpanWrapper wrapper = new SpanWrapper { Begin = begin, Length = text.Length };
                    _sendAnnouncement((IntPtr)(&wrapper));
                }
            }
            catch (Exception e)
            {
                Log($"Speak threw: {e.InnerException?.Message ?? e.Message}");
            }
        }

        private void ResolveSendAnnouncement()
        {
            try
            {
                // Resolve the ICall directly, exactly as the interop binding does internally, bypassing the
                // broken public string wrapper.
                _sendAnnouncement = IL2CPP.ResolveICall<SendAnnouncementInjected>(
                    "UnityEngine.Accessibility.AccessibilityManager::SendAnnouncementNotification_Injected");

                if (_sendAnnouncement == null)
                    Log("Could not resolve SendAnnouncementNotification_Injected ICall.");
                else
                    MelonLogger.Msg("[NativeAnnouncer] Resolved SendAnnouncementNotification_Injected ICall.");
            }
            catch (Exception e)
            {
                Log($"ResolveSendAnnouncement threw: {e.Message}");
            }
        }

        private static void Log(string msg) => MelonLogger.Warning($"[NativeAnnouncer] {msg}");
    }
}
