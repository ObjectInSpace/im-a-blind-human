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
        private IntPtr _dialogViewClass;      // _Code.DialogSystem.DialogView (+ its _isActive field)
        private InputContextKind _lastLogged = (InputContextKind)(-1);

        public InputContext(TwoDProbe twoD) => _twoD = twoD;

        public InputContextKind Classify()
        {
            InputContextKind kind;
            bool mainMenu = false, paused = false, photo = false, provider = false, dialog = false;
            try
            {
                EnsureResolved();

                mainMenu = MainMenuActive();
                paused = PauseActive();
                photo = _twoD != null && _twoD.IsPhotoActive();
                dialog = DialogActive();
                provider = ProviderPresent();

                if (mainMenu) kind = InputContextKind.MainMenu;
                // Pause overlays the 3D scene (provider stays present), so it MUST be checked before ThreeD.
                else if (paused) kind = InputContextKind.Pause;
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
                                $"provider={provider} dialog={dialog})");
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
                _dialogViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.DialogSystem", "DialogView");

                MelonLogger.Msg($"[InputContext] resolved: mainMenuView={_mainMenuViewClass != IntPtr.Zero} " +
                                $"pauseMenuView={_pauseMenuViewClass != IntPtr.Zero} " +
                                $"actionProvider={_actionProviderClass != IntPtr.Zero} dialogView={_dialogViewClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[InputContext] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
