using System;
using MelonLoader;
using NoImNotAHumanAccess.Dialogue;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using NoImNotAHumanAccess.World;
using UnityEngine;

[assembly: MelonInfo(typeof(NoImNotAHumanAccess.AccessMod), "I'm a Blind Human", "0.7.0", "objectinspace")]
[assembly: MelonGame("Trioskaz", "NoImNotAHuman")]

namespace NoImNotAHumanAccess
{
    /// <summary>
    /// Mod entry point. Native screen-reader output (Phase 0) is proven; current scope adds menu narration
    /// (the focused UI control is spoken as the player navigates with the game's own arrows/Enter/Escape/Tab)
    /// and dialogue narration (a Harmony hook on the game's central subtitle sink speaks each rendered line).
    /// The full navigable AccessibilityNode hierarchy comes next.
    /// </summary>
    public sealed class AccessMod : MelonMod
    {
        private ISpeechOutput? _speech;
        private MenuNarrator? _menuNarrator;
        private DialogueNarrator? _dialogueNarrator;
        private HudNarrator? _hudNarrator;
        private StatusNarrator? _statusNarrator;
        private OrientationNarrator? _orientationNarrator;
        private ActionMenu? _actionMenu;
        private TwoDProbe? _twoDProbe;
        private InputContext? _inputContext;
        private UguiFocus? _uguiFocus;
        private RoomViewNarrator? _roomViewNarrator;
        private CloseUpNarrator? _closeUpNarrator;
        private ControlsNarrator? _controlsNarrator;
        private SignNarrator? _signNarrator;
        private ShotNarrator? _shotNarrator;

        // F7 = repeat the current context's control row ("what can I do here"); F8 = manual repeat/test trigger;
        // F9 = status readout (day/phase/energy/items); F10 = "where am I" orientation. The game's UI/world maps
        // bind no F-key (verified key set: arrows/WASD/enter/space/escape/tab/page/home/end/q/e/f/shift/backspace).
        private const KeyCode ControlsKey = KeyCode.F7;
        private const KeyCode RepeatKey = KeyCode.F8;
        private const KeyCode StatusKey = KeyCode.F9;
        private const KeyCode OrientationKey = KeyCode.F10;
        // Action menu (3D scene): arrows cycle the interactable list + Enter activates (see the ThreeD case in
        // OnUpdate). No dedicated F-keys — arrows are free in the 3D scene.

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initializing native speech channel...");
            try
            {
                _speech = new NativeAnnouncer();
                LoggerInstance.Msg($"Speech channel: {_speech.Name}, available={_speech.IsAvailable}");
                _menuNarrator = new MenuNarrator(_speech);

                // Dialogue: hook the game's central subtitle sink and speak each rendered line.
                _dialogueNarrator = new DialogueNarrator(_speech);
                DialoguePatches.Apply(HarmonyInstance, _dialogueNarrator);

                // Room-photo highlight (auto): speaks the highlighted object as the player moves the selection in the
                // still-photo close-up a door opens. Driven by a UIButton.OnHover hook in WorldPatches (created here
                // first so it can be passed in).
                _roomViewNarrator = new RoomViewNarrator(_speech);

                // Object close-ups (fridge item grid / consumable confirm): speak "name. description." on highlight,
                // driven by Fridge.OnPointerEntered + Consumable.SetupConsumable hooks in WorldPatches.
                _closeUpNarrator = new CloseUpNarrator(_speech);

                // Context control row: speaks the game's per-context key-hint strip (in-room / fridge / radio /
                // run-zone / dialog) the first time each context is entered, and on demand via F7. Created before
                // WorldPatches so the SetupAndShowControlsView hook can drive its first-encounter readout.
                _controlsNarrator = new ControlsNarrator(_speech);

                // Inspection signs (teeth/eyes/armpit/aura-photo): the visitor-detection loop. Speaks a DESCRIPTION of
                // the sign image (round-robin from balanced pools) so the player judges human vs visitor themselves —
                // never announces the answer. Driven by the DialogView.ShowSign hook in WorldPatches.
                _signNarrator = new SignNarrator(_speech);

                // Shooting outcome: after the player shoots someone, announce whether they were a human or a visitor
                // (the cutscene reveals it visually). Driven by the Yarn Kill* command hooks in WorldPatches (postfix,
                // so the announcement follows the shot).
                _shotNarrator = new ShotNarrator(_speech);

                // World HUD: hook the interaction-prompt sink ("press [action] to [subject]"), the room-photo
                // highlight (UIButton.OnHover), the object close-ups, the context control-row swap, the inspection
                // signs, and the shooting outcome.
                _hudNarrator = new HudNarrator(_speech);
                WorldPatches.Apply(HarmonyInstance, _hudNarrator, _roomViewNarrator, _closeUpNarrator, _controlsNarrator,
                    _signNarrator, _shotNarrator);

                // Status key (F9): on-demand readout of day/phase/energy/items via the Zenject-resolved controllers.
                _statusNarrator = new StatusNarrator(_speech);

                // Orientation key (F10): "what's around me" — currently-selectable interactables with bearings.
                _orientationNarrator = new OrientationNarrator(_speech);

                // Action menu (3D scene): arrows cycle an available interaction from a list and Enter activates it, so
                // the game performs it without the player walking to the object. Routed via the ThreeD input context.
                _actionMenu = new ActionMenu(_speech);

                // 2D diagnostic (F8): dump the live room-photo UIButton set, to design the 2D object-stepping menu.
                _twoDProbe = new TwoDProbe(_speech);

                // Routes the shared arrow/Enter keys by context: main menu / 2D photo / 3D walking, else does nothing.
                _inputContext = new InputContext(_twoDProbe);

                // uGUI focus keeper: arrows are dead in the menus (main menu / settings) until something is selected.
                // While in a menu context, this ensures a control is always focused so native nav + MenuNarrator work.
                _uguiFocus = new UguiFocus();
            }
            catch (Exception e)
            {
                LoggerInstance.Error($"Failed to create native speech channel: {e}");
                _speech = null;
            }
        }

        public override void OnLateInitializeMelon()
        {
            Speak("I'm a Blind Human accessibility mod loaded.");
        }

        public override void OnUpdate()
        {
            _menuNarrator?.Tick();
            _controlsNarrator?.Tick(); // drains the deferred first-encounter control-row read

            if (Input.GetKeyDown(ControlsKey))
            {
                _controlsNarrator?.Repeat();
            }

            if (Input.GetKeyDown(RepeatKey))
            {
                _twoDProbe?.Dump();         // F8: dump the live room-photo UIButton set (2D-menu design diagnostic)
                _inputContext?.DumpSignals(); // + dump raw context-signal find-state (markers/provider/popup)
            }

            // Routed by the active context. The mod takes over arrows/Enter ONLY in the contexts where the game leaves
            // them dead/free; everywhere else (None) it does nothing and the game's own input drives it.
            InputContextKind ctx = _inputContext?.Classify() ?? InputContextKind.None;

            // In a menu context, keep a uGUI control selected every frame so the dead arrow keys work and native nav
            // (+ MenuNarrator) drive them. This is panel-agnostic: it self-heals across main-menu ⇄ settings switches
            // (when a panel changes, the old selection goes inactive → we re-focus the new panel's first control), so
            // the mod needs no per-panel knowledge. The settings sub-panel reads as MainMenu (its own field never
            // flips), which is fine — both want the same focus behavior.
            // MainMenu and Pause both want pure focus-keeping (track the selected control so arrows + MenuNarrator work),
            // with no per-keypress action of our own. The pause menu overlays the 3D scene, so without this it'd be
            // classified ThreeD and arrows would wrongly drive the interactable list.
            if (ctx == InputContextKind.MainMenu || ctx == InputContextKind.Pause)
                _uguiFocus?.EnsureSelection();

            bool next = Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.DownArrow);
            bool prev = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.UpArrow);
            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);

            if (next || prev || enter)
            {
                switch (ctx)
                {
                    case InputContextKind.RoomPhoto:
                        // Arrows warp the game's cursor between objects; Enter is the game's own select (untouched).
                        if (next) _twoDProbe?.Step(backwards: false);
                        if (prev) _twoDProbe?.Step(backwards: true);
                        break;

                    case InputContextKind.ThreeD:
                        // Arrows cycle the interactable list (announce); Enter activates via Act().
                        if (next) _actionMenu?.Cycle(backwards: false);
                        if (prev) _actionMenu?.Cycle(backwards: true);
                        if (enter) _actionMenu?.Activate();
                        break;

                    // MainMenu / Pause: arrows handled natively once EnsureSelection has focus set (above); nothing
                    // per-keypress. None: do nothing — dialog/cutscene/popup/phone/etc. handled by the game.
                }
            }

            if (Input.GetKeyDown(StatusKey))
            {
                _statusNarrator?.Announce();
            }

            if (Input.GetKeyDown(OrientationKey))
            {
                _orientationNarrator?.Announce();
            }
        }

        private void Speak(string text)
        {
            if (_speech == null)
            {
                LoggerInstance.Warning($"No speech channel; would have said: {text}");
                return;
            }
            LoggerInstance.Msg($"Speak: {text}");
            _speech.Speak(text);
        }
    }
}
