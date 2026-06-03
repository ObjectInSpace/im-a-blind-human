using System;
using MelonLoader;
using NoImNotAHumanAccess.Dialogue;
using NoImNotAHumanAccess.InputShim;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using NoImNotAHumanAccess.World;
using UnityEngine;

[assembly: MelonInfo(typeof(NoImNotAHumanAccess.AccessMod), "I'm a Blind Human", "0.7.5", "objectinspace")]
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
        private FridgeMenu? _fridgeMenu;
        private RadioMenu? _radioMenu;
        private CartoonButton? _cartoonButton;
        private TwoDProbe? _twoDProbe;
        private InputModeGate? _inputModeGate;
        private InputContext? _inputContext;
        private UguiFocus? _uguiFocus;
        private RoomViewNarrator? _roomViewNarrator;
        private CloseUpNarrator? _closeUpNarrator;
        private ControlsNarrator? _controlsNarrator;
        private SignNarrator? _signNarrator;
        private ShotNarrator? _shotNarrator;
        private JawsArrowShim? _jawsArrowShim;

        // F7 = repeat the current context's control row ("what can I do here"); F8 = manual repeat/test trigger;
        // F9 = status readout (day/phase/energy/items); F10 = "where am I" orientation. The game binds NO F-key.
        // The game's COMPLETE keyboard binding set (read from PlayerInputActions.asset via the 2026-06-03 asset rip):
        //   space escape w a s d q e f shift leftCtrl enter upArrow downArrow leftArrow rightArrow
        // Everything else is free. In particular [ ] PageUp PageDown Home End Backspace are NOT bound by the game on
        // any device — the mod can use them with zero collision. (An earlier comment wrongly listed page/home/end/
        // backspace/tab as game keys; the rip disproved that.)
        private const KeyCode ControlsKey = KeyCode.F7;
        private const KeyCode RepeatKey = KeyCode.F8;
        private const KeyCode StatusKey = KeyCode.F9;
        private const KeyCode OrientationKey = KeyCode.F10;
        // F11 = toggle the JAWS arrow relay. OFF BY DEFAULT — NVDA users already have working arrows + speech
        // interrupt, so the hook is never installed for them. A JAWS user presses F11 to turn the arrows ON: the relay
        // makes them navigate menus/dialogue, at the cost that JAWS can't interrupt its own speech (Ctrl / fast
        // next-arrow) while on, because the relay must re-inject the keys (Unity only reads raw input). F11 again fully
        // removes the hook and restores JAWS's normal interrupt. See jaws/README.md.
        private const KeyCode ArrowShimToggleKey = KeyCode.F11;

        // The mod NEVER touches the arrow keys. In menus the game's own arrow nav drives (the mod only keeps a control
        // focused); everywhere the mod drives a list it uses DEDICATED keys below. This removed all arrow contention —
        // the source of repeated 3D-selection bugs and a photo-over-photo softlock — and is the "rely on the game"
        // posture the user asked for: the game owns the arrows in every context.
        //
        // PageUp/PageDown = UNIFIED selection across every mod-driven list (3D action menu, 2D room photo, fridge). One
        // key pair everywhere, so a context MISCLASSIFICATION between 2D and 3D can only ever select the wrong list.
        // In the 3D scene, selecting an interactable also AIMS the player at it (ActionMenu.Aim) so the player can then
        // press the GAME's own Space (Interact) to engage — the mod never invokes the 3D interaction itself.
        private const KeyCode CyclePrevKey = KeyCode.PageUp;
        private const KeyCode CycleNextKey = KeyCode.PageDown;
        // Backspace = "use the selected fridge drink" (FridgeItemView.Use). FRIDGE ONLY now — the 3D action menu no
        // longer activates anything via the mod (no cold Act(); the player uses the game's own Space). See ActionMenu.
        private const KeyCode ActivateKey = KeyCode.Backspace;
        // Radio (close-up): held PageUp/PageDown sweep the tuning dial; Home/End toggle the AM/FM band.
        private const KeyCode RadioBandDownKey = KeyCode.Home;
        private const KeyCode RadioBandUpKey = KeyCode.End;

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

                // Fridge close-up: the drink grid is mouse-hover only in the base game, so arrows step the drinks
                // (driving the game's own FridgeItemView.OnHover → the CloseUpNarrator fridge hook) and Enter uses the
                // selected one (FridgeItemView.Use). Routed via the Fridge input context.
                _fridgeMenu = new FridgeMenu(_speech);

                // Radio close-up: tuning is a mouse-drag knob in the base game, so held Left/Right sweep the knob
                // (driving the game's RotateKnob), Up/Down toggle AM/FM, and closeness to a station is narrated by ear.
                // Routed via the Radio input context.
                _radioMenu = new RadioMenu(_speech);

                // Gacha "watch cartoon" play button (main-menu collections): pointer-only in the base game, so Enter
                // activates it when it's on screen. Routed inside the MainMenu input context.
                _cartoonButton = new CartoonButton(_speech);

                // 2D diagnostic (F8): dump the live room-photo UIButton set, to design the 2D object-stepping menu.
                _twoDProbe = new TwoDProbe(_speech);

                // Input-mode gate: the game's OWN world-vs-UI signal (InputHandling._inUiCounter). Load-bearing — the
                // destructive 3D activate (Backspace → Act()) fires ONLY when this says world-roam, so a context
                // misclassification can never open an interaction over a photo (the softlock). Also dumps on F8.
                _inputModeGate = new InputModeGate();

                // Classifies the context for per-list selection routing (main menu / 2D photo / fridge / radio / 3D).
                _inputContext = new InputContext(_twoDProbe);

                // uGUI focus keeper: arrows are dead in the menus (main menu / settings) until something is selected.
                // While in a menu context, this ensures a control is always focused so native nav + MenuNarrator work.
                _uguiFocus = new UguiFocus();

                // JAWS arrow relay: stops JAWS swallowing the arrow keys so the game's OWN navigation receives them
                // in menus/dialogue (NVDA already passes them; JAWS does not). Pure relay — does not interpret arrows.
                // OFF by default: NVDA users already have working arrows + speech interrupt and need nothing here, so
                // the hook isn't even installed until a JAWS user opts in with F11 (accepting the interrupt tradeoff).
                _jawsArrowShim = new JawsArrowShim();
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
                _twoDProbe?.Dump();           // F8: dump the live room-photo UIButton set (2D-menu design diagnostic)
                _inputContext?.DumpSignals(); // + dump raw context-signal find-state (markers/provider/popup)
                _inputModeGate?.Dump();       // + dump the game's own world-vs-UI signal (InputHandling._inUiCounter)
            }

            // The active context routes the mod's DEDICATED keys (PageUp/PageDown/Backspace/Home/End) per list. The
            // mod never touches arrows — menus use the game's native arrow nav (we only keep a control focused). In
            // None it does nothing. The destructive 3D activate is additionally gated on the game's world-roam signal.
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

            // Clear the fridge selection once we leave the fridge, so re-opening it starts fresh (no stale highlight).
            if (ctx != InputContextKind.Fridge)
                _fridgeMenu?.Reset();

            // Radio: tuning is HELD (per-frame sweep), unlike the edge-based selection below. Held PageUp/PageDown drive
            // RotateKnob every frame, and Tick narrates closeness while tuning. Band toggle (Home/End) is an edge
            // action handled with the other selection keys below. Reset state on leaving so reopening starts fresh.
            if (ctx == InputContextKind.Radio)
            {
                bool tuneDown = Input.GetKey(CyclePrevKey);   // PageUp held
                bool tuneUp = Input.GetKey(CycleNextKey);     // PageDown held
                if (tuneUp) _radioMenu?.Tune(backwards: false);
                else if (tuneDown) _radioMenu?.Tune(backwards: true);
                _radioMenu?.Tick(tuningHeld: tuneDown || tuneUp);
            }
            else
            {
                _radioMenu?.Reset();
            }

            // UNIFIED selection keys (edge-triggered): PageUp = previous, PageDown = next, in EVERY mod-driven list.
            // Because the same keys mean "select" in 2D and 3D, a 2D⇄3D context misclassification can only select the
            // wrong list — it can never fire a 3D action in a 2D context (the old [ ]/Backspace-in-a-photo softlock).
            bool selPrev = Input.GetKeyDown(CyclePrevKey);
            bool selNext = Input.GetKeyDown(CycleNextKey);

            if (selPrev || selNext)
            {
                switch (ctx)
                {
                    case InputContextKind.RoomPhoto:
                        // Warp the game's cursor between objects (the game's own Enter then selects — untouched by us).
                        if (selNext) _twoDProbe?.Step(backwards: false);
                        if (selPrev) _twoDProbe?.Step(backwards: true);
                        break;

                    case InputContextKind.Fridge:
                        // Step the drinks (the game's own hover narrates via the fridge hook); Backspace uses one below.
                        if (selNext) _fridgeMenu?.Cycle(backwards: false);
                        if (selPrev) _fridgeMenu?.Cycle(backwards: true);
                        break;

                    case InputContextKind.ThreeD:
                    {
                        // Cycle + aim. Aiming pokes IsTargeted, which for some objects (blinds) OPENS their close-up,
                        // so we only AIM when the game is genuinely in world-roam (_inUiCounter==0) — otherwise a fast
                        // cycle while a close-up is mid-open stacks interactions = softlock. Selecting/announcing is
                        // always safe; the aim is gated on the game's own signal.
                        bool canAim = _inputModeGate?.IsWorldRoam() ?? false;
                        if (selNext) _actionMenu?.Cycle(backwards: false, canAim: canAim);
                        if (selPrev) _actionMenu?.Cycle(backwards: true, canAim: canAim);
                        break;
                    }

                    // MainMenu / Pause: the game's native arrow nav drives (EnsureSelection keeps focus); selection
                    // keys do nothing here. Radio: PageUp/PageDown are the HELD tune (handled above), not selection.
                    // None: nothing — dialog/cutscene/popup/phone are the game's.
                }
            }

            // ACTIVATE (Backspace) — FRIDGE ONLY. The 3D action menu no longer activates anything itself: PageUp/Down
            // select+aim, and the player presses the GAME's own Space (Interact) to engage — so entry and leave run
            // through the game's own state machine (this replaced the cold Act() that softlocked; see ActionMenu). The
            // fridge "use" stays here because it's a 2D grid using FridgeItemView.Use, not the 3D interaction path.
            if (Input.GetKeyDown(ActivateKey) && ctx == InputContextKind.Fridge)
                _fridgeMenu?.Activate();

            // Radio band toggle (Home/End): edge action, only in the radio close-up.
            if (ctx == InputContextKind.Radio && (Input.GetKeyDown(RadioBandDownKey) || Input.GetKeyDown(RadioBandUpKey)))
                _radioMenu?.SwitchBand();

            // Gacha "watch cartoon" play button (main menu): pointer-only in the base game, so Enter plays it when it's
            // on screen. The game's native Enter handles all other menu activation; we only intercept when available.
            if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                && ctx == InputContextKind.MainMenu
                && _cartoonButton != null && _cartoonButton.IsAvailable())
            {
                _cartoonButton.Activate();
            }

            if (Input.GetKeyDown(StatusKey))
            {
                _statusNarrator?.Announce();
            }

            if (Input.GetKeyDown(OrientationKey))
            {
                _orientationNarrator?.Announce();
            }

            if (Input.GetKeyDown(ArrowShimToggleKey) && _jawsArrowShim != null)
            {
                bool on = !_jawsArrowShim.Enabled;
                _jawsArrowShim.SetEnabled(on);
                // Off by default (NVDA users need nothing). A JAWS user turns it ON to make the arrows navigate,
                // accepting that JAWS can't interrupt its own speech while on. Spell the tradeoff out by ear.
                Speak(on
                    ? "Arrow keys on for JAWS. Screen reader interrupt is limited while this is on."
                    : "Arrow keys off. Screen reader interrupt restored.");
            }
        }

        public override void OnDeinitializeMelon()
        {
            _jawsArrowShim?.Dispose();
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
