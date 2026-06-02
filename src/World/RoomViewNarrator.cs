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
    /// From the button we resolve a humanized name by walking up the transform parents to a known room-object view
    /// (<c>NarrativeRoomObject</c> carries an <c>ENarrativeObject _objectType</c> we map to a label; characters and
    /// TV/bed/fridge use the parent GameObject name), falling back to the button's own GameObject name so something is
    /// always spoken. A diagnostic log records what resolved, to verify the button→view hierarchy against real play.
    /// De-dupes consecutive hovers of the same button. Never throws.
    /// </summary>
    public sealed class RoomViewNarrator
    {
        private const string RoomsNs = "_Code.Rooms";
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _narrativeClass; // NarrativeRoomObject (for its _objectType enum identity)

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
                EnsureResolved();

                IntPtr buttonGo = Il2CppRaw.GetComponentGameObject(button);
                string? resolved = ResolveName(buttonGo, out string how);
                string spoken = !string.IsNullOrWhiteSpace(resolved)
                    ? resolved!
                    : Humanize(Il2CppRaw.GetUnityObjectName(button));

                MelonLogger.Msg($"[RoomViewNarrator] hover: '{spoken}' (via {how}, " +
                                $"buttonGo='{(buttonGo != IntPtr.Zero ? Il2CppRaw.GetUnityObjectName(buttonGo) : "?")}')");

                if (!string.IsNullOrWhiteSpace(spoken)) _speech.Speak(spoken, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RoomViewNarrator] OnButtonHovered threw: {e.Message}");
            }
        }

        /// <summary>
        /// Resolve the highlighted object's name by walking up from the button's GameObject to a known room-object
        /// view. NarrativeRoomObject carries an <c>ENarrativeObject _objectType</c> we map to a label; the others
        /// (characters, TV/bed/fridge) use the parent's humanized GameObject name. Null if nothing resolves.
        /// </summary>
        private string? ResolveName(IntPtr buttonGo, out string how)
        {
            how = "gameobject";
            if (buttonGo == IntPtr.Zero) return null;

            if (_narrativeClass != IntPtr.Zero)
            {
                IntPtr narrative = GetComponentInParentRaw(buttonGo, _narrativeClass);
                if (narrative != IntPtr.Zero)
                {
                    int objType = Il2CppRaw.ReadInt32Field(narrative, _narrativeClass, "_objectType", fallback: -1);
                    if (objType >= 0) { how = "NarrativeRoomObject._objectType"; return NarrativeName(objType); }
                }
            }

            IntPtr parentGo = Il2CppRaw.GetParentGameObject(buttonGo);
            if (parentGo != IntPtr.Zero)
            {
                string? n = Humanize(Il2CppRaw.GetUnityObjectName(parentGo));
                if (!string.IsNullOrWhiteSpace(n)) { how = "parent gameobject"; return n; }
            }
            return null;
        }

        /// <summary>GetComponentInParent(Type) for a raw GameObject pointer (walks parents via raw Component reads).</summary>
        private static IntPtr GetComponentInParentRaw(IntPtr goPtr, IntPtr componentClass)
        {
            IntPtr cur = goPtr;
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 16)
            {
                IntPtr c = Il2CppRaw.GetComponentRaw(cur, componentClass);
                if (c != IntPtr.Zero) return c;
                cur = Il2CppRaw.GetParentGameObject(cur);
            }
            return IntPtr.Zero;
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _narrativeClass = Il2CppRaw.GetClass(GameAsm, RoomsNs, "NarrativeRoomObject");
                MelonLogger.Msg($"[RoomViewNarrator] resolved: narrative={_narrativeClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RoomViewNarrator] EnsureResolved threw: {e.Message}");
            }
        }

        // ENarrativeObject: Bedroom_Curtain, Bedroom_Nightstand, Bathroom_Window, BigRoom_Cross, BigRoom_Toy,
        // Kitchen_Cupboards, Office_Pictures, Office_Magazine, Pantry_Box.
        private static string NarrativeName(int e) => e switch
        {
            0 => "curtain",
            1 => "nightstand",
            2 => "window",
            3 => "cross",
            4 => "toy",
            5 => "cupboards",
            6 => "pictures",
            7 => "magazine",
            8 => "box",
            _ => "object",
        };

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
