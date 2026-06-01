using System;
using MelonLoader;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// On-demand status readout (bound to a key in <see cref="AccessMod"/>). A blind player can't see the persistent
    /// HUD resource state — day, time of day, and energy (= remaining day-actions) — nor their held items, so this
    /// speaks them on request. Reads live state through <see cref="GameStateAccess"/>; speaks a short fallback if the
    /// state isn't readable yet (e.g. pressed at the main menu before a gameplay scene exists). Never throws.
    /// </summary>
    public sealed class StatusNarrator
    {
        private readonly ISpeechOutput _speech;
        private readonly GameStateAccess _state = new();

        public StatusNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Speak the current status, interrupting so a repeat press re-reads from the top.</summary>
        public void Announce()
        {
            try
            {
                string? status = _state.Describe();
                _speech.Speak(status ?? "Status not available right now.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StatusNarrator] Announce threw: {e.Message}");
            }
        }
    }
}
