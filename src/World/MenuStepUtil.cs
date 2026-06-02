using System;
using NoImNotAHumanAccess.Menus;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Shared, side-effect-free helpers for the menu steppers (3D <see cref="ActionMenu"/>, 2D <see cref="TwoDProbe"/>,
    /// <see cref="MainMenuStepper"/>). Consolidates logic that was duplicated across all three. The bigger
    /// template-method base for the two pointer-tracked self-announcing steppers (3D + 2D) is deferred until the
    /// steppers are verified in-game; these pure utilities are the zero-risk shared parts.
    /// </summary>
    public static class MenuStepUtil
    {
        /// <summary>
        /// Wrap a selection index one step forward/back over <paramref name="count"/> items. <paramref name="current"/>
        /// of -1 (nothing selected / not in list) lands on the first item going forward, or the last going back.
        /// </summary>
        public static int NextIndex(int current, int count, bool backwards)
        {
            if (count <= 0) return -1;
            if (current < 0) return backwards ? count - 1 : 0;
            int step = backwards ? -1 : 1;
            return ((current + step) % count + count) % count;
        }

        /// <summary>
        /// Turn a raw interactable / button GameObject name into a speakable label: drop a "(Clone)"/"(1)" suffix and
        /// the interaction-trigger boilerplate ("…LookAtObjectTrigger"), split PascalCase ("DoorBedroom" → "Door
        /// Bedroom"), clean stray markup/whitespace, and reorder a leading "Door X" to "X door" ("Bedroom door").
        /// Shared by all three steppers so names read identically everywhere.
        /// </summary>
        public static string Humanize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "object";
            string s = raw;

            // Drop a trailing "(Clone)" / "(1)" parenthetical.
            int paren = s.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0) s = s.Substring(0, paren);
            int clone = s.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0) s = s.Substring(0, clone);

            // Drop trigger-type boilerplate (longest first so "LookAtObjectTrigger" wins over "Trigger").
            foreach (string suffix in new[] { "LookAtObjectTrigger", "ObjectTrigger", "LookAtTrigger", "Trigger" })
            {
                int at = s.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) { s = s.Remove(at, suffix.Length); break; }
            }

            s = SplitPascalCase(s.Replace('_', ' ')).Trim();
            string cleaned = ControlDescriber.Clean(s);
            if (string.IsNullOrWhiteSpace(cleaned)) return "object";

            // "Door Bedroom" → "Bedroom door"; "Door Living Room" → "Living Room door".
            const string door = "Door ";
            if (cleaned.StartsWith(door, StringComparison.OrdinalIgnoreCase) && cleaned.Length > door.Length)
                cleaned = cleaned.Substring(door.Length).Trim() + " door";

            return cleaned;
        }

        /// <summary>Insert spaces at lowercase/digit→UPPERCASE boundaries so "DoorBedroom" reads "Door Bedroom".
        /// Existing spaces and runs of capitals (e.g. "TV") are left alone.</summary>
        private static string SplitPascalCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1])))
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
