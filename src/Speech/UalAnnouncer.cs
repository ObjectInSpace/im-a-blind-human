using System;
using MelonLoader;
using UnityAccessibilityLib;

namespace NoImNotAHumanAccess.Speech
{
    /// <summary>
    /// Speech channel backed by UnityAccessibilityLib (UniversalSpeech) — talks to the player's NVDA/JAWS/etc.
    /// DIRECTLY via P/Invoke, with a Windows SAPI fallback. This REPLACES the old Unity-AssistiveSupport channel to
    /// fix the multi-second latency: that channel routed through Unity's <c>SendAnnouncementNotification</c>, which delivers a
    /// low-priority UIA *notification* the reader services on a slow poll (measured: our call was 0 ms, but the line
    /// reached the reader seconds later). UniversalSpeech speaks immediately and supports real interrupt. The proper
    /// in-engine fix (focus through an AccessibilityHierarchy) is impossible on this build — the game's IL2CPP compile
    /// STRIPPED the AccessibilityNode/Hierarchy types out of GameAssembly.dll entirely (see project memory).
    ///
    /// Requires <c>UniversalSpeech.dll</c> (64-bit, from https://github.com/qtnc/UniversalSpeech) in the game root;
    /// the build copies it there. <see cref="SpeechManager"/> adds dedup, repeat-buffer, and rich-text cleaning over
    /// the raw wrapper, so we go through it rather than the low-level <c>UniversalSpeechWrapper</c>.
    /// </summary>
    public sealed class UalAnnouncer : ISpeechOutput
    {
        // The ISpeechOutput seam is text-only (no per-line category), so everything announces as System. Narrators
        // could later pass a richer TextType, but the interface is type-agnostic and this keeps the swap minimal.
        private const int DefaultTextType = (int)TextType.System;

        private bool _initialized;

        public string Name => "UnityAccessibilityLib (UniversalSpeech)";

        public UalAnnouncer()
        {
            try
            {
                // Initialize returns false if neither a screen reader nor SAPI could be brought up (e.g. the native
                // DLL is missing from the game root). We surface that via IsAvailable so AccessMod can log it.
                _initialized = SpeechManager.Initialize();
                MelonLogger.Msg($"[UalAnnouncer] SpeechManager.Initialize() = {_initialized}.");
            }
            catch (Exception e)
            {
                // Most likely DllNotFoundException for UniversalSpeech.dll — report it plainly; speech just won't work.
                MelonLogger.Warning($"[UalAnnouncer] Initialize threw (UniversalSpeech.dll missing from game root?): {e.Message}");
                _initialized = false;
            }
        }

        public bool IsAvailable => _initialized;

        public void Speak(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!_initialized) { Log("Not initialized; cannot speak."); return; }
            try
            {
                // UniversalSpeech has no per-call interrupt flag at the SpeechManager layer — Stop() then Announce()
                // gives "cut current speech and say this now", which is what every narrator means by interrupt:true.
                if (interrupt) SpeechManager.Stop();
                SpeechManager.Announce(text, DefaultTextType);
            }
            catch (Exception e)
            {
                Log($"Speak threw: {e.Message}");
            }
        }

        private static void Log(string msg) => MelonLogger.Warning($"[UalAnnouncer] {msg}");
    }
}
