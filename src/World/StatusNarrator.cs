using System;
using MelonLoader;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// On-demand status readout (bound to a key in <see cref="AccessMod"/>). A blind player can't see the persistent
    /// HUD resource state — day, time of day, and energy (= remaining day-actions) — nor their held items, so this
    /// speaks them on request. Reads live state through <see cref="GameStateAccess"/>; speaks a short fallback if the
    /// state isn't readable yet (e.g. pressed at the main menu before a gameplay scene exists).
    ///
    /// Also appends any CORPSES present in the scene (via <see cref="CorpseNarrator"/>): dead characters are de-buttoned,
    /// so the hover/stepper narration never speaks them, yet a blind player still wants to know a body is in the room and
    /// whether it was a human or a visitor. (Folded in here after the old F10 "where things are" readout was removed —
    /// the corpse info was the part worth keeping.) Never throws.
    /// </summary>
    public sealed class StatusNarrator
    {
        private readonly ISpeechOutput _speech;
        private readonly CorpseNarrator _corpses;
        private readonly GameStateAccess _state = new();

        public StatusNarrator(ISpeechOutput speech, CorpseNarrator corpses)
        {
            _speech = speech;
            _corpses = corpses;
        }

        /// <summary>Speak the current status plus any corpses present, interrupting so a repeat press re-reads from the
        /// top.</summary>
        public void Announce()
        {
            try
            {
                string? status = _state.Describe();
                string? corpses = _corpses.Describe();

                string text =
                    (status, corpses) switch
                    {
                        (not null, not null) => $"{status} {corpses}",
                        (not null, null) => status!,
                        (null, not null) => corpses!,
                        _ => "Status not available right now.",
                    };
                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[StatusNarrator] Announce threw: {e.Message}");
            }
        }
    }
}
