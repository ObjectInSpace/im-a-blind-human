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
                    _getCurrentDevice = Il2CppRaw.GetMethod(_inputHandlingClass, "get_CurrentDevice", 0);

                MelonLogger.Msg($"[InputModeGate] resolved: inputHandling={_inputHandlingClass != IntPtr.Zero} " +
                                $"getCurrentDevice={_getCurrentDevice != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputModeGate] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
