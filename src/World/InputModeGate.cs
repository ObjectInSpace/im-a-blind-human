using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// The game's OWN world-vs-UI signal, used as the load-bearing gate for the mod's destructive 3D action (the
    /// Backspace → <c>Act()</c> that opens interactions). Reads <c>InputHandling._inUiCounter</c>: the game's reference
    /// count of things that have put input into UI mode. <b>0 ⇒ world free-roam</b> (the only time it is safe to fire a
    /// 3D interaction); <b>&gt;0 ⇒ some overlay owns input</b> (dialog, close-up, phone, cutscene, pause, popup, OR a 2D
    /// photo) and the 3D action MUST stay dead.
    ///
    /// WHY this and not the mod's own <see cref="InputContext"/> classification: ground-truth F8 logging (2026-06-03)
    /// caught <c>InputContext.Classify()</c> returning ThreeD while <c>_inUiCounter==1</c> — a photo/overlay was up but
    /// the mod's per-view enumeration missed it, so Backspace would have fired <c>Act()</c> over the photo and opened a
    /// second photo on top of it = unrecoverable SOFTLOCK. The game's counter was right in every state we tested
    /// (roam=0; photo, dialog, and the misclassified-ThreeD case all =1). So we trust the game, not our enumeration.
    /// See memory project-nimnah-input-mode-gate. This is the "rely on the game" gate the user asked for.
    ///
    /// <c>InputHandling</c> is a MonoBehaviour, so it is findable Zenject-free via the standard inactive-inclusive find
    /// (the Zenject resolver hard-crashes this game). Read-only: never mutates game state.
    /// </summary>
    public sealed class InputModeGate
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private bool _resolved;
        private IntPtr _inputHandlingClass; // _Code.Player.InputHandling (MonoBehaviour)
        private IntPtr _getCurrentDevice;   // get_CurrentDevice (diagnostic only)

        // Unity InputSystem PlayerInput / InputActionMap — READ-ONLY F8 diagnostics: what control scheme + action map are
        // actually live. This is what proved (2026-06-08) that the pause menu is ALREADY on the enabled UI map, so dead
        // pause arrows were a lost-SELECTION problem, not an input-map problem. Kept as cheap insurance for the next
        // "why is native nav dead here?" question.
        private IntPtr _playerInputClass;   // UnityEngine.InputSystem.PlayerInput
        private IntPtr _getCurrentScheme;   // PlayerInput.get_currentControlScheme -> string
        private IntPtr _getCurrentMap;      // PlayerInput.get_currentActionMap -> InputActionMap
        private IntPtr _actionMapClass;     // UnityEngine.InputSystem.InputActionMap
        private IntPtr _getMapName;         // InputActionMap.get_name -> string
        private IntPtr _getMapEnabled;      // InputActionMap.get_enabled -> bool

        /// <summary>
        /// True only when the game is in world free-roam (<c>_inUiCounter == 0</c>) — the one state in which firing a
        /// 3D interaction is safe. FALSE (fail-safe) when an overlay owns input OR the signal can't be read: a missed
        /// suppression is merely an inert key, whereas a false "roam" can softlock, so any doubt resolves to "not roam".
        /// </summary>
        public bool IsWorldRoam()
        {
            try
            {
                EnsureResolved();
                if (_inputHandlingClass == IntPtr.Zero) return false;
                IntPtr ih = Il2CppRaw.FindObjectIncludingInactive(_inputHandlingClass);
                if (ih == IntPtr.Zero) return false;
                // fallback: -1 (treated as NOT roam) if the field can't be read, so we fail safe.
                return Il2CppRaw.ReadInt32Field(ih, _inputHandlingClass, "_inUiCounter", fallback: -1) == 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputModeGate] IsWorldRoam threw: {e.Message}; treating as NOT roam.");
                return false;
            }
        }

        /// <summary>F8 diagnostic: dump the live input-mode signals so the world-vs-UI state can be read from the log
        /// across contexts (roam, dialog, fridge, phone, cutscene, pause, photo).</summary>
        public void Dump()
        {
            try
            {
                EnsureResolved();
                if (_inputHandlingClass == IntPtr.Zero) { MelonLogger.Msg("[InputModeGate] InputHandling class UNRESOLVED."); return; }

                IntPtr ih = Il2CppRaw.FindObjectIncludingInactive(_inputHandlingClass);
                if (ih == IntPtr.Zero) { MelonLogger.Msg("[InputModeGate] InputHandling instance NOT FOUND this frame."); return; }

                int inUiCounter = Il2CppRaw.ReadInt32Field(ih, _inputHandlingClass, "_inUiCounter");
                int device = _getCurrentDevice != IntPtr.Zero
                    ? Il2CppRaw.InvokeInt32Getter(ih, _getCurrentDevice) : -1; // EInputDevice: 0 None,1 MnK,2 Gamepad
                bool active = Il2CppRaw.GetComponentGameObjectActive(ih);

                MelonLogger.Msg($"[InputModeGate] _inUiCounter={inUiCounter} (0 => world free-roam, >0 => an overlay " +
                                $"owns input) CurrentDevice={device} handlerActive={active}");

                // The decisive readout: what scheme + action map is ACTUALLY live. If this prints map=World while paused,
                // the UI map never became active (so UI_Navigate/UI_Submit are disabled) and that's why arrows are dead —
                // regardless of SetIsInUIState/ForceSetUI returning clean. If it prints map=UI, the problem is elsewhere
                // (selection lost, or the InputModule not processing).
                if (_playerInputClass != IntPtr.Zero)
                {
                    IntPtr pi = Il2CppRaw.FindObjectIncludingInactive(_playerInputClass);
                    if (pi == IntPtr.Zero) { MelonLogger.Msg("[InputModeGate] PlayerInput instance NOT FOUND."); }
                    else
                    {
                        string? scheme = _getCurrentScheme != IntPtr.Zero ? Il2CppRaw.InvokeStringGetter(pi, _getCurrentScheme) : "(no getter)";
                        IntPtr map = _getCurrentMap != IntPtr.Zero ? Il2CppRaw.InvokeObjectGetter(pi, _getCurrentMap) : IntPtr.Zero;
                        string? mapName = (map != IntPtr.Zero && _getMapName != IntPtr.Zero) ? Il2CppRaw.InvokeStringGetter(map, _getMapName) : "(null map)";
                        bool mapEnabled = map != IntPtr.Zero && _getMapEnabled != IntPtr.Zero && Il2CppRaw.InvokeBoolGetter(map, _getMapEnabled);
                        MelonLogger.Msg($"[InputModeGate] PlayerInput scheme='{scheme}' currentActionMap='{mapName}' mapEnabled={mapEnabled}");
                    }
                }
                else MelonLogger.Msg("[InputModeGate] PlayerInput class UNRESOLVED (can't read live scheme/map).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputModeGate] Dump threw: {e.Message}");
            }
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _inputHandlingClass = Il2CppRaw.GetClass(GameAsm, "_Code.Player", "InputHandling");
                if (_inputHandlingClass != IntPtr.Zero)
                {
                    _getCurrentDevice = Il2CppRaw.GetMethod(_inputHandlingClass, "get_CurrentDevice", 0);
                }

                _playerInputClass = Il2CppRaw.GetClass("Unity.InputSystem.dll", "UnityEngine.InputSystem", "PlayerInput");
                if (_playerInputClass != IntPtr.Zero)
                {
                    _getCurrentScheme = Il2CppRaw.GetMethod(_playerInputClass, "get_currentControlScheme", 0);
                    _getCurrentMap = Il2CppRaw.GetMethod(_playerInputClass, "get_currentActionMap", 0);
                }
                _actionMapClass = Il2CppRaw.GetClass("Unity.InputSystem.dll", "UnityEngine.InputSystem", "InputActionMap");
                if (_actionMapClass != IntPtr.Zero)
                {
                    _getMapName = Il2CppRaw.GetMethod(_actionMapClass, "get_name", 0);
                    _getMapEnabled = Il2CppRaw.GetMethod(_actionMapClass, "get_enabled", 0);
                }

                MelonLogger.Msg($"[InputModeGate] resolved: inputHandling={_inputHandlingClass != IntPtr.Zero} " +
                                $"getCurrentDevice={_getCurrentDevice != IntPtr.Zero} playerInput={_playerInputClass != IntPtr.Zero} " +
                                $"getScheme={_getCurrentScheme != IntPtr.Zero} getMap={_getCurrentMap != IntPtr.Zero} " +
                                $"mapName={_getMapName != IntPtr.Zero} mapEnabled={_getMapEnabled != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputModeGate] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
