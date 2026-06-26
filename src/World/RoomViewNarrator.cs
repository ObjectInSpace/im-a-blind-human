using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

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

        // While the 2D stepper drives selection it narrates names ITSELF, so the game's own hover narration must stay
        // quiet until this time. A plain "suppress the next hover of button X" wouldn't cover the occlusion case: when
        // two photo hotspots overlap, warping onto one makes the raycaster hover the OTHER, so the hover that fires is
        // for the wrong button and wouldn't match X. Suppressing by a short TIME WINDOW instead silences whatever hover
        // the warp triggers, right or wrong, leaving the stepper as the single source of truth.
        private float _suppressHoverUntil;
        private const float StepHoverSuppressSeconds = 0.30f;

        public RoomViewNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Suppress the game's own hover narration for a short window, because the 2D stepper is about to (or
        /// just did) speak the name itself. See <see cref="_suppressHoverUntil"/> for why this is time-based.</summary>
        public void SuppressHoverBriefly() => _suppressHoverUntil = Time.realtimeSinceStartup + StepHoverSuppressSeconds;

        /// <summary>Called from the UIButton.OnHover postfix with the hovered button's pointer. Resolves and speaks
        /// the object's name; de-dupes repeat hovers of the same button.</summary>
        public void OnButtonHovered(IntPtr button)
        {
            try
            {
                if (button == IntPtr.Zero || button == _lastButton) return;

                // The 2D stepper just warped the mouse and narrated the object itself; stay quiet so the warp's hover
                // (which may even be for an OVERLAPPING neighbour, not the stepped object) doesn't double- or
                // mis-announce. Don't touch _lastButton here — let the stepper own the de-dupe state during stepping.
                // Outside that window this hook is the live highlight readout as normal.
                if (Time.realtimeSinceStartup < _suppressHoverUntil) return;
                _lastButton = button;

                // The button's own GameObject name is the reliable label across all room-object kinds — the live log
                // showed "Nightstand", "Bed", "Window" all correct on the button GO, while the earlier parent-walk +
                // ENarrativeObject enum mislabeled them ("BG" for the bed's parent; "curtain" for the window's
                // _objectType). So speak the humanized button GameObject name directly.
                // Prefer the button GameObject's name (correct for furniture: "Bed", "Window", "Cupboards"). Some buttons
                // (e.g. the kitchen Fridge) have a BLANK GameObject name but a usable name on the UIButton component
                // itself — reading only the GameObject left those announced as blank (the user's "blank entry that opens
                // the fridge"). So fall back to the component's own name when the GameObject name is empty.
                IntPtr buttonGo = Il2CppRaw.GetComponentGameObject(button);
                string spoken = Humanize(Il2CppRaw.GetUnityObjectName(buttonGo));
                if (string.IsNullOrWhiteSpace(spoken))
                    spoken = Humanize(Il2CppRaw.GetUnityObjectName(button));

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
