using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Keyboard activation for the gacha/collections "watch cartoon" play button. In the main-menu Gacha window, an
    /// unlocked ending shows a <c>WatchCartoonButton</c> (ns <c>_Code.Infrastructure._NINAH__MainMenu.Gacha</c>) that
    /// plays the ending movie. Unlike the gacha ending tiles (which are <c>UISelectable</c>s, keyboard-navigable and
    /// already narrated), this play button implements ONLY <c>IPointer*</c> — no select/submit — so a blind player
    /// can't trigger it. We supply the keyboard equivalent by invoking its <c>OnPointerClick(PointerEventData)</c>,
    /// the same handler the mouse click runs.
    ///
    /// Routed inside the existing MainMenu input context (the gacha window classifies as MainMenu): when an active,
    /// unlocked button is present, <see cref="AccessMod"/> sends Enter here instead of leaving it to native nav. Found
    /// via FindObjectsByType (no Zenject). Never throws.
    /// </summary>
    public sealed class CartoonButton
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string GachaNs = "_Code.Infrastructure._NINAH__MainMenu.Gacha";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _buttonClass;     // WatchCartoonButton
        private IntPtr _onPointerClick;  // WatchCartoonButton.OnPointerClick(PointerEventData)

        public CartoonButton(ISpeechOutput speech) => _speech = speech;

        /// <summary>Whether an active "watch cartoon" play button is on screen right now (so Enter should activate it
        /// rather than fall through to native menu nav).</summary>
        public bool IsAvailable()
        {
            try
            {
                EnsureResolved();
                return FindActiveButton() != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// <summary>Play the cartoon by invoking the button's own click handler.</summary>
        public void Activate()
        {
            try
            {
                EnsureResolved();
                IntPtr btn = FindActiveButton();
                if (btn == IntPtr.Zero) return;

                IntPtr ped = Il2CppRaw.NewPointerEventData(); // may be zero; handler likely tolerates null
                _speech.Speak("Playing cartoon.", interrupt: true);
                bool ok = Il2CppRaw.InvokeVoidWithObject(btn, _onPointerClick, ped);
                MelonLogger.Msg($"[CartoonButton] OnPointerClick invoked (ped={ped != IntPtr.Zero} threw={!ok}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CartoonButton] Activate threw: {e.Message}");
            }
        }

        /// <summary>First active <c>WatchCartoonButton</c> in the scene, or zero. (There is one per shown ending; the
        /// gacha shows a single ending at a time, so at most one is active.)</summary>
        private IntPtr FindActiveButton()
        {
            if (_buttonClass == IntPtr.Zero) return IntPtr.Zero;
            foreach (IntPtr b in Il2CppRaw.FindObjectsByType(_buttonClass, includeInactive: false))
                if (b != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(b)) return b;
            return IntPtr.Zero;
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _buttonClass = Il2CppRaw.GetClass(GameAsm, GachaNs, "WatchCartoonButton");
                if (_buttonClass != IntPtr.Zero)
                    _onPointerClick = Il2CppRaw.GetMethod(_buttonClass, "OnPointerClick", 1);

                MelonLogger.Msg($"[CartoonButton] resolved: button={_buttonClass != IntPtr.Zero} " +
                                $"onPointerClick={_onPointerClick != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CartoonButton] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
