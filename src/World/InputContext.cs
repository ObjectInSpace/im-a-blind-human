using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>The mod-relevant input context, deciding whether the mod drives arrows/Enter or does nothing.</summary>
    public enum InputContextKind
    {
        /// <summary>Title / main menu. Arrows are DEAD here (only D-pad/mouse work) → mod bridges arrows to the menu.</summary>
        MainMenu,
        /// <summary>The in-game pause menu is up (over the 3D scene). Same handling as <see cref="MainMenu"/>: keep a
        /// uGUI control focused so arrows work + MenuNarrator speaks, but no per-keypress action of our own.</summary>
        Pause,
        /// <summary>A 2D room photo / close-up is up. Mod warps the cursor between highlightable objects.</summary>
        RoomPhoto,
        /// <summary>The fridge item grid is up. The grid is mouse-hover only in the base game, so the mod steps the
        /// drinks with arrows (driving the game's own hover) and uses the selected one with Enter.</summary>
        Fridge,
        /// <summary>The radio close-up is up. Tuning is a mouse-drag knob in the base game, so the mod sweeps it with
        /// held Left/Right (driving RotateKnob) and toggles AM/FM with Up/Down, narrating closeness to a station.</summary>
        Radio,
        /// <summary>The phone close-up is up. The dial pad is pointer/controller-only in the base game, so the mod maps
        /// the number keys (and # / *) onto the matching dial-pad buttons. The pad's own Selectables get native arrow
        /// nav via <see cref="UguiFocus"/> (kept focused in every context), so Call/Clear are reachable too.</summary>
        Phone,
        /// <summary>3D first-person free-roam. Mod drives the interactable action list (arrows cycle, Enter activates).</summary>
        ThreeD,
        /// <summary>Anything else — dialog, cutscene, pause, settings, popups, the phone, loading, the unknown. The mod
        /// does NOTHING (no arrow/Enter intercept); the game's own input handles it. This is the DEFAULT.</summary>
        None,
    }

    /// <summary>
    /// Classifies the current input context so the shared arrow/Enter handler routes correctly.
    ///
    /// KEY PRINCIPLE (user, 2026-06-02): the mod does NOTHING by default. It ONLY takes over arrows/Enter in the three
    /// contexts it explicitly handles — <see cref="InputContextKind.MainMenu"/>, <see cref="InputContextKind.RoomPhoto"/>,
    /// <see cref="InputContextKind.ThreeD"/>. Every other situation (dialog, cutscene, pause, settings, popup, phone,
    /// loading…) returns <see cref="InputContextKind.None"/> and the game's own input is left untouched. This replaces
    /// the earlier "detect every hand-off context" approach, which leaked: the hand-off markers never fired
    /// (active-only FindObjectOfType missed them), so dialog-over-3D fell through to ThreeD.
    ///
    /// Each "act" context must be POSITIVELY and EXCLUSIVELY detected:
    ///   - MainMenu : a <c>MainMenuMarker</c> is live.
    ///   - Pause    : <c>PauseMenuView.IsPaused</c> is true. Checked BEFORE 3D, because the pause menu overlays the 3D
    ///                scene (the interactable provider stays present), so without this gate pause leaks into ThreeD and
    ///                arrows wrongly drive the action list instead of the menu. Handled like MainMenu (focus-keep only).
    ///   - RoomPhoto: a 2D photo is up (any goActive room UIButton), via <see cref="TwoDProbe.IsPhotoActive"/>.
    ///   - Fridge   : the <c>FridgeCloseUpView</c> is present AND active. Checked BEFORE 3D, because the fridge
    ///                close-up overlays the 3D scene (the interactable provider stays present), so without this gate
    ///                the fridge leaks into ThreeD and arrows wrongly drive the action list instead of the drinks.
    ///   - Radio    : the <c>RadioCloseUpView</c> is present AND active. Same overlay reasoning as Fridge — checked
    ///                before 3D so the radio knob/band get the arrows instead of the 3D action list.
    ///   - ThreeD   : the interactable provider is present AND no dialog is active. The provider alone is NOT enough —
    ///                dialog/cutscene OVERLAYS the 3D scene (provider stays present), so we gate 3D on
    ///                <c>DialogView._isActive == false</c>. (DialogView is a concrete view with a real bool field,
    ///                unlike the markers, which the decision log proved never resolve as active.)
    ///   - None     : everything else (DEFAULT).
    ///
    /// All checks are Zenject-free (findable objects + a field read), because the Zenject resolver hard-crashes.
    /// </summary>
    public sealed class InputContext
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly TwoDProbe _twoD;

        private bool _resolved;
        private IntPtr _mainMenuViewClass;    // _Code.Infrastructure.MainMenu.MainMenuView (+ _areSettingsOpened field)
        private IntPtr _pauseMenuViewClass;   // _Code.Infrastructure.Pause.PauseMenuView (+ its IsPaused property)
        private IntPtr _actionProviderClass;  // ActionableObjectsViewProvider (3D interactables)
        private IntPtr _fridgeViewClass;      // _Code.Infrastructure.CloseUps.Views.FridgeCloseUpView
        private IntPtr _radioViewClass;       // _Code.Infrastructure.CloseUps.Views.Radio.RadioCloseUpView
        private IntPtr _phoneViewClass;       // _Code.Infrastructure.CloseUps.Views.Phone.PhoneCloseUpView
        private IntPtr _dialogViewClass;      // _Code.DialogSystem.DialogView (+ its _isActive field)
        private InputContextKind _lastLogged = (InputContextKind)(-1);

        public InputContext(TwoDProbe twoD) => _twoD = twoD;

        public InputContextKind Classify()
        {
            InputContextKind kind;
            bool mainMenu = false, paused = false, photo = false, provider = false, dialog = false, fridge = false, radio = false, phone = false;
            try
            {
                EnsureResolved();

                mainMenu = MainMenuActive();
                paused = PauseActive();
                // Count-FREE photo-open check (RoomDisplayer._isOpened): a photo with zero selectable objects is still
                // an open photo overlaying the 3D scene, and MUST classify as RoomPhoto — not fall through to ThreeD
                // (the old object-count check did, letting the 3D keys fire over the photo → softlock).
                photo = _twoD != null && _twoD.IsPhotoOpen();
                dialog = DialogActive();
                provider = ProviderPresent();
                fridge = FridgeActive();
                radio = RadioActive();
                phone = PhoneActive();

                if (mainMenu) kind = InputContextKind.MainMenu;
                // Pause overlays the 3D scene (provider stays present), so it MUST be checked before ThreeD.
                else if (paused) kind = InputContextKind.Pause;
                // A DIALOG (with choice buttons) can open ON TOP OF a close-up — the phone/radio start conversations via
                // IDialogManager, and the close-up view stays active underneath. If we routed to Fridge/Radio/Phone here
                // the mod would eat the arrows and the player couldn't navigate the dialog CHOICES. So when a dialog is
                // active, fall through to None and let the game's own native arrow nav drive the choice buttons. (Checked
                // before the close-up contexts for exactly this reason; normal close-up use has no dialog active.)
                else if (dialog) kind = InputContextKind.None;
                // Fridge/Radio close-ups open ON TOP OF the room photo — the underlying RoomDisplayer._isOpened stays
                // true behind them, so `photo` would win and misroute to RoomPhoto (the fridge grid then read "no
                // objects"). They are the more-specific overlay, so they MUST be checked BEFORE photo. (Confirmed
                // 2026-06-04: FridgeCloseUpView active=True while the room photo was still open underneath.)
                else if (fridge) kind = InputContextKind.Fridge;
                else if (radio) kind = InputContextKind.Radio;
                // Phone is a close-up overlaying the room photo (same reasoning as fridge/radio), so it MUST be checked
                // before photo, else `photo` wins and the dial keys never route here.
                else if (phone) kind = InputContextKind.Phone;
                else if (photo) kind = InputContextKind.RoomPhoto;
                // 3D only when the interactable provider is present AND no dialog/cutscene overlay is showing.
                else if (provider && !dialog) kind = InputContextKind.ThreeD;
                else kind = InputContextKind.None;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputContext] Classify threw: {e.Message}; doing nothing.");
                kind = InputContextKind.None;
            }

            if (kind != _lastLogged)
            {
                _lastLogged = kind;
                MelonLogger.Msg($"[InputContext] -> {kind} (mainMenu={mainMenu} paused={paused} photo={photo} " +
                                $"fridge={fridge} radio={radio} phone={phone} provider={provider} dialog={dialog})");
            }
            return kind;
        }

        /// <summary>
        /// A main-menu context is up (title screen OR its Settings sub-panel — both are the same MainMenuView scene
        /// and both want the mod's uGUI focus-keeping, so they're treated alike). True when MainMenuView is present and
        /// active. NOTE: we deliberately do NOT gate on <c>_areSettingsOpened</c> — F8 ground truth showed that field
        /// never flips, and the focus keeper is panel-agnostic anyway (it re-focuses whatever panel is active).
        /// </summary>
        private bool MainMenuActive()
        {
            if (_mainMenuViewClass == IntPtr.Zero) return false;
            IntPtr view = Il2CppRaw.FindObjectIncludingInactive(_mainMenuViewClass);
            return view != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(view);
        }

        /// <summary>The pause menu is up: <c>PauseMenuView.IsPaused</c> is true. Read via the property's backing field
        /// (<c>&lt;IsPaused&gt;k__BackingField</c>), the same way other concrete-view bool state is read.</summary>
        private bool PauseActive()
        {
            if (_pauseMenuViewClass == IntPtr.Zero) return false;
            IntPtr pv = Il2CppRaw.FindObjectIncludingInactive(_pauseMenuViewClass);
            if (pv == IntPtr.Zero) return false;
            return Il2CppRaw.ReadBoolField(pv, _pauseMenuViewClass, "<IsPaused>k__BackingField");
        }

        /// <summary>The fridge item grid is up: <c>FridgeCloseUpView</c> is present AND its GameObject is active.</summary>
        private bool FridgeActive()
        {
            if (_fridgeViewClass == IntPtr.Zero) return false;
            IntPtr fv = Il2CppRaw.FindObjectIncludingInactive(_fridgeViewClass);
            return fv != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(fv);
        }

        /// <summary>The radio close-up is up: <c>RadioCloseUpView</c> is present AND its GameObject is active.</summary>
        private bool RadioActive()
        {
            if (_radioViewClass == IntPtr.Zero) return false;
            IntPtr rv = Il2CppRaw.FindObjectIncludingInactive(_radioViewClass);
            return rv != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(rv);
        }

        /// <summary>The phone close-up is up: <c>PhoneCloseUpView</c> is present AND its GameObject is active.</summary>
        private bool PhoneActive()
        {
            if (_phoneViewClass == IntPtr.Zero) return false;
            IntPtr pv = Il2CppRaw.FindObjectIncludingInactive(_phoneViewClass);
            return pv != IntPtr.Zero && Il2CppRaw.GetComponentGameObjectActive(pv);
        }

        /// <summary>DialogView is present AND its <c>_isActive</c> flag is set (a dialog/cutscene is showing).</summary>
        private bool DialogActive()
        {
            if (_dialogViewClass == IntPtr.Zero) return false;
            IntPtr dv = Il2CppRaw.FindObjectIncludingInactive(_dialogViewClass);
            if (dv == IntPtr.Zero) return false;
            return Il2CppRaw.ReadBoolField(dv, _dialogViewClass, "_isActive");
        }

        private bool ProviderPresent()
        {
            if (_actionProviderClass == IntPtr.Zero) return false;
            return Il2CppRaw.FindObjectIncludingInactive(_actionProviderClass) != IntPtr.Zero;
        }

        /// <summary>One-shot diagnostic (F8): raw find-state of each signal, to verify detection from ground truth.</summary>
        public void DumpSignals()
        {
            EnsureResolved();
            Probe("MainMenuView", _mainMenuViewClass);
            Probe("ActionableObjectsViewProvider", _actionProviderClass);
            Probe("DialogView", _dialogViewClass);

            // Raw MainMenuView field reads so we can see exactly what the settings gate is reading in each state.
            if (_mainMenuViewClass != IntPtr.Zero)
            {
                IntPtr v = Il2CppRaw.FindObjectIncludingInactive(_mainMenuViewClass);
                if (v != IntPtr.Zero)
                    MelonLogger.Msg($"[InputContext.probe] MainMenuView active={Il2CppRaw.GetComponentGameObjectActive(v)} " +
                                    $"_areSettingsOpened={Il2CppRaw.ReadBoolField(v, _mainMenuViewClass, "_areSettingsOpened")} " +
                                    $"_areSettingsAnimating={Il2CppRaw.ReadBoolField(v, _mainMenuViewClass, "_areSettingsAnimating")}");
            }
            MelonLogger.Msg($"[InputContext.probe] => Classify={Classify()} (MainMenuActive={MainMenuActive()} DialogActive={DialogActive()})");
        }

        private static void Probe(string label, IntPtr klass)
        {
            if (klass == IntPtr.Zero) { MelonLogger.Msg($"[InputContext.probe] {label}: CLASS UNRESOLVED"); return; }
            IntPtr act = Il2CppRaw.FindObjectOfType(klass);
            if (act != IntPtr.Zero) { MelonLogger.Msg($"[InputContext.probe] {label}: found ACTIVE"); return; }
            IntPtr any = Il2CppRaw.FindAnyObjectByType(klass, includeInactive: true);
            if (any == IntPtr.Zero) { MelonLogger.Msg($"[InputContext.probe] {label}: NOT FOUND"); return; }
            MelonLogger.Msg($"[InputContext.probe] {label}: found inactive-incl, active={Il2CppRaw.GetComponentGameObjectActive(any)}");
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _mainMenuViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.MainMenu", "MainMenuView");
                _pauseMenuViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.Pause", "PauseMenuView");
                _actionProviderClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.ActionableObjects", "ActionableObjectsViewProvider");
                _fridgeViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.CloseUps.Views", "FridgeCloseUpView");
                _radioViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.CloseUps.Views.Radio", "RadioCloseUpView");
                _phoneViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.CloseUps.Views.Phone", "PhoneCloseUpView");
                _dialogViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.DialogSystem", "DialogView");

                MelonLogger.Msg($"[InputContext] resolved: mainMenuView={_mainMenuViewClass != IntPtr.Zero} " +
                                $"pauseMenuView={_pauseMenuViewClass != IntPtr.Zero} " +
                                $"actionProvider={_actionProviderClass != IntPtr.Zero} " +
                                $"fridgeView={_fridgeViewClass != IntPtr.Zero} radioView={_radioViewClass != IntPtr.Zero} " +
                                $"phoneView={_phoneViewClass != IntPtr.Zero} dialogView={_dialogViewClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputContext] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
