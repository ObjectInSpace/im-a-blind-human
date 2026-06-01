using System;
using MelonLoader;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks the moment-to-moment interaction prompt — the "press [action] to [subject]" hint the game shows when
    /// the first-person raycast lands on an interactable. Fed by <see cref="WorldPatches"/>, which postfixes the
    /// game's single prompt sink <c>HUDPresenter.ShowHint(subject, action, target, icon)</c>. By the time that runs,
    /// <c>subject</c>/<c>action</c> are already resolved display strings (the LocalizedStrings on RaycastTargetHint
    /// are evaluated upstream), so this is a pure text sink — no localization work, mirroring the dialogue path.
    ///
    /// Two guards, both inherent to that sink:
    /// 1. Re-entry: the prompt is (re)shown whenever the targeting/conditions state changes and may re-fire for the
    ///    same object. We dedupe on the composed phrase so a hint is spoken once while it stays current; a different
    ///    target, action, or a HideHint() (which resets the dedupe) lets the next one through.
    /// 2. Rich text: the strings can carry TMP markup; we strip it with the same cleaner the menu/dialogue paths use.
    ///
    /// Phrasing is "{action} {subject}" (e.g. "take cigarettes", "open door") — the game authors action as the verb
    /// and subject as the noun, which reads naturally concatenated and matches how a sighted player parses the HUD.
    /// </summary>
    public sealed class HudNarrator
    {
        private readonly ISpeechOutput _speech;
        private string _lastSpoken = string.Empty;

        public HudNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>
        /// Handle one interaction prompt. Cleans markup, composes "{action} {subject}" plus an energy-cost suffix
        /// when the prompt carries one, drops empties and the consecutive duplicate that re-firing produces, then
        /// speaks (interrupting, so a fresh prompt cuts off the tail of a stale one rather than queueing). Never
        /// throws into the game.
        /// </summary>
        /// <param name="subject">The thing being acted on (noun), e.g. "cigarettes", "door".</param>
        /// <param name="action">The verb prompt, e.g. "take", "open".</param>
        /// <param name="icon">The <c>ERaycastHintIcon</c> as its underlying int — the action's energy cost shown on
        /// the HUD (None=0, Energy=1, EnergyX2=2, AllEnergy=3, Save=4). Mapped to a spoken suffix.</param>
        public void OnHint(string? subject, string? action, int icon = 0)
        {
            try
            {
                string subj = Clean(subject);
                string act = Clean(action);

                // Compose verb + noun, tolerating either side being absent (some prompts may carry only one).
                string text;
                if (act.Length > 0 && subj.Length > 0) text = $"{act} {subj}";
                else if (act.Length > 0) text = act;
                else text = subj;

                if (text.Length == 0) return;

                // Append the energy cost the HUD shows for this action, so a blind player hears what a sighted one
                // sees on the prompt icon. Part of the deduped phrase, so a cost change on the same target re-speaks.
                string cost = EnergyCostSuffix(icon);
                if (cost.Length > 0) text = $"{text}, {cost}";

                if (text == _lastSpoken) return;
                _lastSpoken = text;

                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[HudNarrator] OnHint threw: {e.Message}");
            }
        }

        /// <summary>
        /// Map <c>ERaycastHintIcon</c> to a spoken energy-cost suffix. Save (4) is not an energy cost, so it gets no
        /// suffix; None (0) and any unknown value also yield nothing.
        /// </summary>
        private static string EnergyCostSuffix(int icon) => icon switch
        {
            1 => "1 energy",     // Energy
            2 => "2 energy",     // EnergyX2
            3 => "all energy",   // AllEnergy
            _ => string.Empty,   // None / Save / unknown
        };

        /// <summary>
        /// The prompt was hidden (player looked away / conditions lapsed). We don't announce the disappearance, but
        /// we clear the dedupe so re-targeting the SAME object speaks its prompt again rather than being swallowed.
        /// </summary>
        public void OnHintHidden()
        {
            _lastSpoken = string.Empty;
        }

        private static string Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : ControlDescriber.Clean(s!).Trim();
    }
}
