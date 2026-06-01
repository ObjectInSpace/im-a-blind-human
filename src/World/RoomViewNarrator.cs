using System;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks the name of the object currently highlighted in the room-photo close-up (the still image a door
    /// interaction opens, presented by <c>RoomDisplayer</c>). The game is a hybrid: you walk/aim in first person to
    /// reach a door, then a photo of the room opens where you move a highlight over selectable objects. A blind
    /// player needs to hear which object is highlighted as they move between them; the richer description comes from
    /// interacting (Yarn dialogue), which the dialogue hook already narrates — so this is name-only by design.
    ///
    /// Mechanism (polled each tick, mirroring <c>MenuNarrator.Tick</c>'s EventSystem polling — no fragile per-button
    /// hook): find the live <c>RoomDisplayer</c> (a MonoBehaviour) and read its <c>_selectedButton</c>. When that
    /// changes to a non-null button, resolve the button's owning room-object view by walking up the transform parents
    /// (each <c>CharacterRoomObjectView</c>/<c>ObjectRoomObjectView</c> owns a <c>_button</c>; <c>NarrativeRoomObject</c>
    /// owns a <c>_uiButton</c> — all parented to or on the view's GameObject), read its identity, and speak a humanized
    /// name. Falls back to the humanized GameObject name if no known view type resolves, so something useful is always
    /// spoken; a diagnostic log records what resolved so the hierarchy assumption can be verified against real play.
    /// Never throws.
    /// </summary>
    public sealed class RoomViewNarrator
    {
        private const string RoomsInfraNs = "_Code.Infrastructure.Rooms";
        private const string RoomsNs = "_Code.Rooms";
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _roomDisplayerClass; // RoomDisplayer (we read its _selectedButton field by name)
        private IntPtr _narrativeClass;     // NarrativeRoomObject (for its _objectType enum identity)

        private IntPtr _lastButton = IntPtr.Zero; // de-dupe: only speak when the highlight changes

        // Edge-triggered diagnostics (so the log shows the FAILURE path, not only successful highlights):
        private bool _displayerWasPresent;        // log once on found / lost transitions
        private bool _loggedNullButtonWhilePresent; // log the "found displayer but null button" case once per presence

        public RoomViewNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Poll the current room-photo highlight; speak it when it changes. Call every frame.</summary>
        public void Tick()
        {
            try
            {
                EnsureResolved();
                if (_roomDisplayerClass == IntPtr.Zero) return;

                IntPtr displayer = Il2CppRaw.FindObjectOfType(_roomDisplayerClass);
                if (displayer == IntPtr.Zero)
                {
                    if (_displayerWasPresent)
                    {
                        MelonLogger.Msg("[RoomViewNarrator] RoomDisplayer gone (left room photo).");
                        _displayerWasPresent = false;
                    }
                    _lastButton = IntPtr.Zero;
                    return; // not in a room photo
                }
                if (!_displayerWasPresent)
                {
                    MelonLogger.Msg("[RoomViewNarrator] RoomDisplayer found (entered room photo).");
                    _displayerWasPresent = true;
                    _loggedNullButtonWhilePresent = false;
                }

                IntPtr button = Il2CppRaw.ReadObjectField(displayer, _roomDisplayerClass, "_selectedButton");
                if (button == _lastButton) return; // no change (includes both-null while idle)
                _lastButton = button;
                if (button == IntPtr.Zero)
                {
                    // Diagnostic: displayer present but the selection field reads null. Log once per presence so we
                    // can tell "wrong field / selection model" from "displayer never found". Not spammed per frame.
                    if (!_loggedNullButtonWhilePresent)
                    {
                        MelonLogger.Msg("[RoomViewNarrator] _selectedButton read as null while in room photo " +
                                        "(field name wrong, or highlight uses a different field/model).");
                        _loggedNullButtonWhilePresent = true;
                    }
                    return; // highlight cleared — say nothing
                }

                Speak(button);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[RoomViewNarrator] Tick threw: {e.Message}");
            }
        }

        private void Speak(IntPtr button)
        {
            IntPtr buttonGo = Il2CppRaw.GetComponentGameObject(button);
            string? resolved = ResolveName(buttonGo, out string how);

            // Always fall back to the GameObject name so the player hears something even if the view lookup misses.
            string spoken = !string.IsNullOrWhiteSpace(resolved)
                ? resolved!
                : Humanize(Il2CppRaw.GetUnityObjectName(button));

            MelonLogger.Msg($"[RoomViewNarrator] highlight: '{spoken}' (via {how}, " +
                            $"buttonGo='{(buttonGo != IntPtr.Zero ? Il2CppRaw.GetUnityObjectName(buttonGo) : "?")}')");

            if (!string.IsNullOrWhiteSpace(spoken)) _speech.Speak(spoken, interrupt: true);
        }

        /// <summary>
        /// Resolve the highlighted object's name by walking up from the button's GameObject to a known room-object
        /// view. NarrativeRoomObject carries an <c>ENarrativeObject _objectType</c> we map to a label; the others
        /// (characters, TV/bed/fridge) use the view's humanized GameObject name. Null if no known view is found.
        /// </summary>
        private string? ResolveName(IntPtr buttonGo, out string how)
        {
            how = "gameobject";
            if (buttonGo == IntPtr.Zero) return null;

            // We need a managed GameObject to use GetComponentInParent; reads are raw, so wrap the parent search by
            // class. Narrative object first (it has a clean enum identity), then the generic views by GameObject name.
            if (_narrativeClass != IntPtr.Zero)
            {
                IntPtr narrative = GetComponentInParentRaw(buttonGo, _narrativeClass);
                if (narrative != IntPtr.Zero)
                {
                    int objType = Il2CppRaw.ReadInt32Field(narrative, _narrativeClass, "_objectType", fallback: -1);
                    if (objType >= 0) { how = "NarrativeRoomObject._objectType"; return NarrativeName(objType); }
                }
            }

            // For characters and TV/bed/fridge, the view's own GameObject name is the most reliable label without
            // resolving CharacterSOData / state enums. Walk to the nearest named ancestor that isn't the raw button.
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
                _roomDisplayerClass = Il2CppRaw.GetClass(GameAsm, RoomsInfraNs, "RoomDisplayer");
                _narrativeClass = Il2CppRaw.GetClass(GameAsm, RoomsNs, "NarrativeRoomObject");
                MelonLogger.Msg($"[RoomViewNarrator] resolved: roomDisplayer={_roomDisplayerClass != IntPtr.Zero} " +
                                $"narrative={_narrativeClass != IntPtr.Zero}");
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
