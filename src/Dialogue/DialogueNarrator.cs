using System;
using MelonLoader;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.Dialogue
{
    /// <summary>
    /// Speaks dialogue/subtitle lines. Fed by <see cref="DialoguePatches"/>, which postfixes the game's single
    /// central text sink <c>SubtitlesView.UpdateText(string)</c> — every rendered subtitle/dialogue line (already
    /// resolved through Unity Localization to the player's language) passes through it. This is the bulk of the
    /// game's content (a narrative VN-style horror game), so getting this one choke point right covers most of it
    /// without per-frame TMP scraping.
    ///
    /// Two things to guard against, both inherent to that sink:
    /// 1. Typewriter re-entry: the view can call UpdateText many times for one line as the typewriter reveals it
    ///    (and again on show/hide). We dedupe on the cleaned text so a line is spoken once, not per character.
    /// 2. Rich text: lines carry TMP markup (<c>&lt;color&gt;</c>, <c>&lt;b&gt;</c>, sprite tags). We strip it with
    ///    the same cleaner the menu path uses so the screen reader hears words, not tags.
    /// </summary>
    public sealed class DialogueNarrator
    {
        private readonly ISpeechOutput _speech;
        private string _lastSpoken = string.Empty;
        private float _lastSpokenAt = float.NegativeInfinity;

        // De-dupe window: the typewriter reveal re-calls the sink with the same final string many times within a
        // fraction of a second, so suppress an identical line that repeats inside this window. But a line that recurs
        // LATER (e.g. the same info popup — "no more radio today" — triggered again minutes on) is a real new event
        // and must be spoken again, so the de-dupe is time-bounded, not permanent.
        private const float DedupeWindowSeconds = 1.5f;

        // Matches a numbered substitution slot like {0} or {12} (Yarn / string.Format style). Anchored to a digit run
        // inside the braces so ordinary prose braces (rare, but e.g. "{}" or "{note}") don't trip the leak warning.
        private static readonly System.Text.RegularExpressions.Regex PlaceholderRegex =
            new(@"\{\d+\}", System.Text.RegularExpressions.RegexOptions.Compiled);

        public DialogueNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>True if the text still contains a numbered substitution slot (e.g. <c>{0}</c>) — i.e. it was read
        /// before its runtime values were spliced in.</summary>
        private static bool HasUnsubstitutedPlaceholder(string text) => PlaceholderRegex.IsMatch(text);

        /// <summary>
        /// Handle one dialogue/subtitle line from the game. Cleans rich-text markup, optionally prefixes the
        /// speaker, drops empties and consecutive duplicates (typewriter re-entry), then speaks it. Dialogue
        /// interrupts so a new line cuts off the tail of the previous one rather than queueing behind it.
        /// Never throws into the game.
        /// </summary>
        /// <param name="raw">The line text (subtitle string, or Yarn <c>LocalizedLine.RawText</c>).</param>
        /// <param name="speaker">Optional speaker name (Yarn <c>CharacterName</c>); null/empty for narration.</param>
        public void OnLine(string? raw, string? speaker = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return;

                string text = ControlDescriber.Clean(raw!);
                if (string.IsNullOrWhiteSpace(text)) return;

                // Leak detector: an unsubstituted "{0}"/"{1}"/… means a line reached us BEFORE its runtime values
                // (phone number, day count, name) were spliced in — the Yarn RawText bug we fixed for RunLine. Other
                // sinks (intro/ending narration, the TV "other game" text, string popups) read text we don't substitute,
                // so if any still carries a placeholder it shows up here. We still SPEAK the line (better than silence),
                // but log it loudly so the offending sink is identifiable in testing. Not an error into the game.
                if (HasUnsubstitutedPlaceholder(text))
                    MelonLogger.Warning($"[DialogueNarrator] line still has a substitution placeholder (speaking anyway): \"{text}\"");

                // Speaker attribution. Yarn RawText often already leads with "Name: ..."; only prefix when the
                // text doesn't already start with the speaker, so we don't say the name twice.
                if (!string.IsNullOrWhiteSpace(speaker))
                {
                    string s = ControlDescriber.Clean(speaker!).Trim();
                    if (s.Length > 0 && !text.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                        text = $"{s}: {text}";
                }

                // Typewriter reveal re-calls the sink with the same final string repeatedly within a moment; suppress
                // an identical repeat only inside the short de-dupe window. The same text recurring later (e.g. an
                // info popup re-triggered) falls outside the window and is spoken again.
                float now = Time.realtimeSinceStartup;
                if (text == _lastSpoken && now - _lastSpokenAt < DedupeWindowSeconds) { _lastSpokenAt = now; return; }
                _lastSpoken = text;
                _lastSpokenAt = now;

                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[DialogueNarrator] OnLine threw: {e.Message}");
            }
        }
    }
}
