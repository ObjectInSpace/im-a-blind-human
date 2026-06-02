using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks the name of the object highlighted in the room-photo close-up (the still image a door opens). The game
    /// is a hybrid: walk/aim in first person to a door, then a photo of the room opens where you move a highlight over
    /// selectable objects. A blind player needs to hear which object is highlighted as they move between them; the
    /// richer description comes from interacting (Yarn dialogue), which the dialogue hook already narrates — so this
    /// is name-only by design.
    ///
    /// Driven by a Harmony postfix on <c>UIButton.OnHover()</c> (see <see cref="WorldPatches"/>), which fires on the
    /// specific button being highlighted. The earlier poll-the-RoomDisplayer approach was abandoned: in-game,
    /// <c>FindObjectOfType(RoomDisplayer)</c> never returned an instance (it was never even logged as found), and the
    /// highlight is a custom <c>UIButton.OnHover</c> rather than EventSystem selection (so the menu narrator's
    /// <c>currentSelectedGameObject</c> path doesn't see it either). Hooking OnHover gets the hovered button directly.
    ///
    /// The spoken name is the hovered button's own humanized GameObject name. The live log proved this is accurate
    /// across all room-object kinds ("Nightstand", "Bed", "Window"), whereas an earlier parent-walk + ENarrativeObject
    /// enum mislabeled things (the bed's parent is "BG"; the window's _objectType resolved to "curtain"). So we read
    /// the button GameObject name directly. De-dupes consecutive hovers of the same button. Never throws.
    /// </summary>
    public sealed class RoomViewNarrator
    {
        private readonly ISpeechOutput _speech;
        private IntPtr _lastButton = IntPtr.Zero; // de-dupe consecutive hovers of the same button

        public RoomViewNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Called from the UIButton.OnHover postfix with the hovered button's pointer. Resolves and speaks
        /// the object's name; de-dupes repeat hovers of the same button.</summary>
        public void OnButtonHovered(IntPtr button)
        {
            try
            {
                if (button == IntPtr.Zero || button == _lastButton) return;
                _lastButton = button;

                // The button's own GameObject name is the reliable label across all room-object kinds — the live log
                // showed "Nightstand", "Bed", "Window" all correct on the button GO, while the earlier parent-walk +
                // ENarrativeObject enum mislabeled them ("BG" for the bed's parent; "curtain" for the window's
                // _objectType). So speak the humanized button GameObject name directly.
                IntPtr buttonGo = Il2CppRaw.GetComponentGameObject(button);
                string spoken = Humanize(Il2CppRaw.GetUnityObjectName(buttonGo != IntPtr.Zero ? buttonGo : button));

                if (string.IsNullOrWhiteSpace(spoken)) return;
                _speech.Speak(spoken, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RoomViewNarrator] OnButtonHovered threw: {e.Message}");
            }
        }

        private static string Humanize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = raw!;
            int clone = s.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0) s = s.Substring(0, clone);
            s = s.Replace('_', ' ').Trim();
            return Menus.ControlDescriber.Clean(s);
        }
    }
}
