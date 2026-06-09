using System;
using MelonLoader;
using NoImNotAHumanAccess.Dialogue;
using NoImNotAHumanAccess.InputShim;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using NoImNotAHumanAccess.World;
using UnityEngine;

[assembly: MelonInfo(typeof(NoImNotAHumanAccess.AccessMod), "I'm a Blind Human", "0.8.2", "objectinspace")]
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
        private CorpseNarrator? _corpseNarrator;
        private ActionMenu? _actionMenu;
        private FridgeMenu? _fridgeMenu;
        private RadioMenu? _radioMenu;
        private PhoneMenu? _phoneMenu;
        private CartoonButton? _cartoonButton;
        private TwoDProbe? _twoDProbe;
        private InputModeGate? _inputModeGate;
        private InputContext? _inputContext;
        private UguiFocus? _uguiFocus;
        private RoomViewNarrator? _roomViewNarrator;
        private CloseUpNarrator? _closeUpNarrator;
        private ControlsNarrator? _controlsNarrator;
        private SignNarrator? _signNarrator;
        private JawsArrowShim? _jawsArrowShim;

        // F7 = repeat the current context's control row ("what can I do here"); F8 = manual repeat/test trigger;
        // F9 = status readout (day/phase/energy/items); F10 = "where am I" orientation. The game binds NO F-key.
        // The game's COMPLETE keyboard binding set (read from PlayerInputActions.asset via the 2026-06-03 asset rip):
        //   space escape w a s d q e f shift leftCtrl enter upArrow downArrow leftArrow rightArrow
        // The arrows + enter ARE in that set, but ONLY via the Input System UI-map `Navigate`/`Submit` actions, which
        // are IDLE in the four contexts the mod drives (RoomPhoto/Fridge/Radio/ThreeD) — game code never polls raw arrow
        // keys (grep-confirmed). So the mod claims arrows + Enter in EXACTLY those four contexts and NEVER in MainMenu/
        // Pause/dialogue (where the game's own Navigate uses them); the ctx guards in OnUpdate enforce that. The F-keys
        // (F7–F11) are bound by nothing. Full action→key+gamepad map + the arrow-safety reasoning:
        // docs/input-and-keyboard.md + memory project-nimnah-arrows-for-stepping-feasibility.
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

        // ARROW-KEY STEPPING (user 2026-06-03, replacing the old PageUp/PageDown/Backspace scheme). The rip proved this
        // safe: game code NEVER polls raw arrow keys — arrows reach the game ONLY via the Input System `Navigate` action
        // in the UI map, which is IDLE in the four contexts the mod drives (RoomPhoto = mouse-raycast, no Selectables;
        // Fridge = hover-only grid; Radio = gamepad-only knob; ThreeD = Move is WASD, UI map inactive). So the mod claims
        // arrows in EXACTLY those four contexts and NEVER in MainMenu/Pause (where the game's own uGUI Navigate uses them
        // — see the ctx guards below). See memory project-nimnah-arrows-for-stepping-feasibility + project-nimnah-input-
        // bindings-rip. NOTE: JAWS swallows arrows; JAWS users need the separate jaws/ pass-through script (the old
        // PageUp/PageDown fallback was removed per the user's "replace outright" decision — NVDA works natively).
        //
        // Up/Down = step every vertical list (3D action menu, 2D room photo, fridge). Enter = activate the selection.
        // One scheme everywhere, so a 2D⇄3D context MISCLASSIFICATION can only ever select the wrong list. In the 3D
        // scene, selecting an interactable AIMS the player at it; Enter walks to it so the GAME's own Space interacts.
        private const KeyCode CyclePrevKey = KeyCode.UpArrow;
        private const KeyCode CycleNextKey = KeyCode.DownArrow;
        // Enter = the UNIFORM activate key: open the photo hotspot (UIButton.Click), drink the selected fridge beer
        // (FridgeItemView.Use), or walk to the selected 3D interactable so the game's Space engages it.
        private const KeyCode ActivateKey = KeyCode.Return;
        private const KeyCode ActivateKeyAlt = KeyCode.KeypadEnter; // numpad Enter, same role
        // Radio (close-up) uses all four arrows for its dial: held Left/Right SWEEP the tuning knob (continuous, natural
        // for a dial), Up/Down toggle the AM/FM band (edge). Left/Right are radio-ONLY — every other context uses Up/Down.
        private const KeyCode RadioTuneLeftKey = KeyCode.LeftArrow;
        private const KeyCode RadioTuneRightKey = KeyCode.RightArrow;
        private const KeyCode RadioBandDownKey = KeyCode.UpArrow;
        private const KeyCode RadioBandUpKey = KeyCode.DownArrow;
        // LEAVE the current interaction. PRIMARY exit is now the game's OWN q (the mod opens views through the game's
        // targeted-entry path — see ActionMenu.GoToSelected — so the game registers the open view as the current
        // interaction and its native q/back-out closes it; this replaced the old cold-Act() entry that q couldn't
        // unwind). Backspace is kept as a mod-driven FALLBACK (ForceLeave) in case a given view still won't unwind
        // natively — it's free (Enter took over activate) and a no-op when nothing's engaged, so it's safe anytime.
        private const KeyCode LeaveKey = KeyCode.Backspace;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initializing native speech channel...");
            try
            {
                // Unity AssistiveSupport — the proven channel for NVDA/Narrator/JAWS. (A self-raised UIA-notification
                // channel was tried to give JAWS interrupt via NotificationProcessing_MostRecent, but it reached NO
                // screen reader: this app's window UIA provider belongs to Unity's native engine, and a standalone
                // provider we raise from isn't the one the reader connects to. Unity exposes no priority-aware
                // announcement and no borrowable provider, so there is no cheap interrupt path. Interrupt now belongs
                // to the deferred AccessibilityHierarchy work — see memory project-nimnah-native-accessibility-hierarchy.)
                _speech = new NativeAnnouncer();
                LoggerInstance.Msg($"Speech channel: {_speech.Name}, available={_speech.IsAvailable}.");
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

                // Inspection signs (teeth/eyes/hands/aura-photo/armpit/ear): the detection loop. Describes the TRAITS of
                // the sign image so the player judges for themselves — never announces a verdict. Folds the description
                // into the guest's next line (via the dialogue narrator) so it isn't swallowed. Driven by the
                // DialogView.ShowSign hook in WorldPatches; F9 repeats the latest test in the conversation.
                _signNarrator = new SignNarrator(_speech, _dialogueNarrator);

                // World HUD: hook the interaction-prompt sink ("press [action] to [subject]"), the room-photo
                // highlight (UIButton.OnHover), the object close-ups, the context control-row swap, and the inspection
                // signs. (The post-shot human/visitor announcement was removed — the game's sound effect conveys it.)
                _hudNarrator = new HudNarrator(_speech);
                WorldPatches.Apply(HarmonyInstance, _hudNarrator, _roomViewNarrator, _closeUpNarrator, _controlsNarrator,
                    _signNarrator);

                // Status key (F9): on-demand readout of day/phase/energy/items via the Zenject-resolved controllers.
                _statusNarrator = new StatusNarrator(_speech);

                // Corpses: names the dead characters in the scene (+ whether each was a human or a visitor). Bodies are
                // de-buttoned, so the hover/stepper narration never speaks them — a blind player needs telling they're
                // there. Folded into the F10 orientation readout below.
                _corpseNarrator = new CorpseNarrator();

                // Orientation key (F10): "what's around me" — currently-selectable interactables with bearings, plus any
                // corpses present (read off the scene's CharacterRoomObjectViews in a death pose).
                _orientationNarrator = new OrientationNarrator(_speech, _corpseNarrator);

                // Action menu (3D scene): Up/Down select an interactable; Enter activates it through the game's own
                // targeted-entry path (focus IsTargeted, then Act()/Interact()), so the game owns the interaction and
                // native 'q' leaves it. Routed via the ThreeD input context.
                _actionMenu = new ActionMenu(_speech);

                // Fridge close-up: the drink grid is mouse-hover only in the base game, so arrows step the drinks
                // (driving the game's own FridgeItemView.OnHover → the CloseUpNarrator fridge hook) and Enter uses the
                // selected one (FridgeItemView.Use). Routed via the Fridge input context.
                _fridgeMenu = new FridgeMenu(_speech);

                // Radio close-up: tuning is a mouse-drag knob in the base game, so held Left/Right sweep the knob
                // (driving the game's RotateKnob), Up/Down toggle AM/FM, and closeness to a station is narrated by ear.
                // Routed via the Radio input context.
                _radioMenu = new RadioMenu(_speech);

                // uGUI focus keeper: the single focus authority. Arrows are dead in native menus until a Selectable is
                // focused; this keeps one focused (re-asserting it when the game clears it) so native nav + MenuNarrator
                // work. Constructed before PhoneMenu because the phone delegates its dial-pad focus to it.
                _uguiFocus = new UguiFocus();

                // Phone close-up: the dial pad is pointer/controller-only in the base game, so the number keys (and
                // # / *) are mapped onto the matching dial-pad buttons, dialing each digit (tone + screen + visual) via
                // the game's own button press path. Focus is delegated to UguiFocus (restricted to dial buttons).
                // Routed via the Phone input context.
                _phoneMenu = new PhoneMenu(_speech, _uguiFocus);

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
            _actionMenu?.Tick();       // reconciles forced focus after a leave (clears stale IsTargeted)
            _signNarrator?.Tick();     // flushes an un-consumed test description; resets F9 last-test on conversation end

            if (Input.GetKeyDown(ControlsKey))
            {
                _controlsNarrator?.Repeat();
            }

            if (Input.GetKeyDown(RepeatKey))
            {
                _twoDProbe?.Dump();                 // F8: dump the live room-photo UIButton set (2D-menu design diagnostic)
                _twoDProbe?.ProbeAllCloseUps("F8");  // + which close-up view is active NOW (press while the fridge is open)
                _inputContext?.DumpSignals();       // + dump raw context-signal find-state (markers/provider/popup)
                _inputModeGate?.Dump();             // + dump the game's own world-vs-UI signal (InputHandling._inUiCounter)
            }

            // The active context routes the mod's keys (Up/Down select, Enter activate, Left/Right radio-tune) per list,
            // but ONLY in RoomPhoto/Fridge/Radio/ThreeD — in MainMenu/Pause the game's own arrow nav drives (we only keep
            // a control focused), and in None the mod does nothing. The 3D go is additionally gated on world-roam.
            InputContextKind ctx = _inputContext?.Classify() ?? InputContextKind.None;

            // Keep a uGUI control selected every frame so the dead arrow keys work and native nav (+ MenuNarrator)
            // drive them. uGUI keyboard navigation only flows once a Selectable is current — without this, arrows do
            // nothing until the player clicks/D-pads onto a control (the D-pad routes through the Input System's
            // Navigate action which the game seeds with a selection, but plain arrows don't get a seeded selection).
            // This is the SAME fix that made the main menu work; the user asked for it EVERYWHERE (save/quit popup,
            // phone dial pad, any panel), so we call it in every context. It's panel-agnostic and self-healing, and a
            // no-op in the mod-driven contexts (RoomPhoto/Fridge/Radio/ThreeD) where the game's UI map has no active
            // Selectables — so it can't compete with the mod's own arrow stepping there.
            // EXCEPTION: in the Phone context, PhoneMenu.Tick owns focus (it knows the dial buttons specifically and
            // recovers focus after a call disables/re-enables them), so we don't also run the generic keeper there.
            // The phone restricts focus to its dial buttons (PhoneMenu.Tick delegates to UguiFocus with that filter);
            // every other context uses the unrestricted keeper. UguiFocus self-heals — it re-asserts the selection the
            // moment a menu owner clears it — so this single call covers MainMenu, Settings, popups, AND the pause menu.
            //
            // Why pause needs the self-heal (F8 ground truth, 2026-06-08): while paused the game is ALREADY on the UI
            // action map and it's enabled (scheme='Keyboard And Mouse' currentActionMap='UI' mapEnabled=True). The input
            // map/scheme was never the problem. The bug was that the pause menu NULLS currentSelectedGameObject every
            // frame (its ResetFocus path), leaving no selection for Navigate/Submit to act on — dead arrows + Enter. The
            // keeper restoring the selection each frame is the whole fix; no input-state meddling.
            if (ctx == InputContextKind.Phone)
                _phoneMenu?.Tick();
            else
                _uguiFocus?.EnsureSelection();

            // Clear the fridge selection once we leave the fridge, so re-opening it starts fresh (no stale highlight).
            if (ctx != InputContextKind.Fridge)
                _fridgeMenu?.Reset();

            // SAFETY NET for the forced-focus leak: when nothing is actually engaged, drop any lingering forced
            // IsTargeted that Tick's engaged-latch may have missed (a close-up that opened+closed between frames, or an
            // alt-tab that broke frame timing). Without this a stale phone/radio target stacked onto the next interaction
            // (the "phone fires with the radio" bug). We run it in:
            //   • ThreeD / None — back in the world or idle, the normal place a stale target hurts.
            //   • RoomPhoto    — a 2D photo overlays the 3D scene; if a stale 3D target lingers under it, the GAME'S OWN
            //     Space (pressed while looking at the photo) can engage that 3D object un-leaveably. The reconcile is
            //     a no-op unless something IS engaged, and an open photo engages nothing, so clearing here is safe and
            //     removes the lingering target the photo-Space bug rode on.
            // (Not in Fridge/Radio/Phone/Pause/MainMenu — those legitimately have something engaged or are menus.)
            if (ctx == InputContextKind.ThreeD || ctx == InputContextKind.None || ctx == InputContextKind.RoomPhoto)
                _actionMenu?.ReconcileWhenFreeRoam();

            // Radio: tuning is HELD (per-frame sweep), unlike the edge-based selection below. Held Left/Right arrows drive
            // RotateKnob every frame, and Tick narrates closeness while tuning. Band toggle (Up/Down) is an edge action
            // handled below. Left/Right are radio-ONLY (no other context uses them). Reset on leaving so reopening is fresh.
            if (ctx == InputContextKind.Radio)
            {
                bool tuneLeft = Input.GetKey(RadioTuneLeftKey);
                bool tuneRight = Input.GetKey(RadioTuneRightKey);
                if (tuneRight) _radioMenu?.Tune(backwards: false);
                else if (tuneLeft) _radioMenu?.Tune(backwards: true);
                _radioMenu?.Tick(tuningHeld: tuneLeft || tuneRight);
            }
            else
            {
                _radioMenu?.Reset();
            }

            // UNIFIED selection keys (edge-triggered): Up = previous, Down = next, in EVERY mod-driven vertical list.
            // Because the same keys mean "select" in 2D and 3D, a 2D⇄3D context misclassification can only select the
            // wrong list — it can never fire a 3D action in a 2D context (the old [ ]/Backspace-in-a-photo softlock).
            // (In Radio, Up/Down are the band toggle instead — there's no Radio case in the switch, so these fall
            // through harmlessly and the band-toggle line below handles them.)
            bool selPrev = Input.GetKeyDown(CyclePrevKey);
            bool selNext = Input.GetKeyDown(CycleNextKey);

            // 3D action keys must do NOTHING when the player is inside an overlay/engaged interaction the mod classified
            // as ThreeD anyway — e.g. looking through the peephole (no one outside): the interactable provider is still
            // present so ctx reads ThreeD, but you're "inside" the peephole and must 'q' out. Suppress when the game's
            // world-roam signal says an overlay owns input (_inUiCounter>0) OR any interactable is engaged (IsLooking).
            bool threeDBlocked = !(_inputModeGate?.IsWorldRoam() ?? true) || (_actionMenu?.IsAnyEngaged() ?? false);

            if (selPrev || selNext)
            {
                switch (ctx)
                {
                    case InputContextKind.RoomPhoto:
                        // Warp the game's cursor between objects; Enter then clicks the selected one (below).
                        if (selNext) _twoDProbe?.Step(backwards: false);
                        if (selPrev) _twoDProbe?.Step(backwards: true);
                        break;

                    case InputContextKind.Fridge:
                        // Step the drinks (the game's own hover narrates via the fridge hook); Enter uses one below.
                        if (selNext) _fridgeMenu?.Cycle(backwards: false);
                        if (selPrev) _fridgeMenu?.Cycle(backwards: true);
                        break;

                    case InputContextKind.ThreeD:
                        // SELECT + ANNOUNCE ONLY — no movement here (walking is Enter, below). Suppressed when the
                        // player is inside an overlay/engaged interaction (peephole etc.) — see threeDBlocked.
                        if (threeDBlocked) break;
                        if (selNext) _actionMenu?.Cycle(backwards: false);
                        if (selPrev) _actionMenu?.Cycle(backwards: true);
                        break;

                    // MainMenu / Pause: the game's native arrow nav drives (EnsureSelection keeps focus); selection
                    // keys do nothing here. Radio: Up/Down are the band toggle (below), Left/Right the HELD tune (above).
                    // None: nothing — dialog/cutscene/popup/phone are the game's.
                }
            }

            // Radio band toggle (Up/Down): edge action, only in the radio close-up. Checked BEFORE the Enter/activate
            // block so it can't be confused with anything else (Up/Down never reach the vertical-list switch in Radio).
            if (ctx == InputContextKind.Radio && (Input.GetKeyDown(RadioBandDownKey) || Input.GetKeyDown(RadioBandUpKey)))
                _radioMenu?.SwitchBand();

            // PHONE: the dial pad is navigated with the arrow keys like any uGUI menu — UguiFocus keeps a button focused
            // (every frame, for all contexts) and the game's own Selectable navigation moves between them, with
            // ControlDescriber speaking the correct key as focus lands. The buttons have NO uGUI submit handler, so
            // Enter is translated to a press of the focused button below (in the activate block). Typing digits directly
            // was removed (it fought the arrow focus and couldn't reach Call) per the user's call.

            // ENTER — the UNIFORM "activate the selected thing" key, the same role in every mod-driven list so the model
            // is consistent: Up/Down selects, Enter activates.
            //  • RoomPhoto: open/select the warped-to photo object via UIButton.Click(). Warping only HOVERS; the game
            //    opens a hotspot (fridge grid, radio dial, a person) on mouse CLICK, and no game key clicks a hovered
            //    hotspot (rip-confirmed). So the mod must click it — the ONLY way to reach the fridge grid / radio dial.
            //  • Fridge: drink the highlighted beer (FridgeItemView.Use) — so Enter both OPENS the fridge (from the
            //    photo) AND drinks the selected beer (in the grid), the consistency the user asked for.
            //  • ThreeD: the deliberate "go" — turn + walk to the SELECTED interactable so the game's own Space can
            //    interact in range. Walking is here, NOT on cycling, so MoveXZ can't stack (the frozen-player race).
            //    Suppressed in the same engaged/overlay state as the select keys (peephole etc.).
            //  • MainMenu: the gacha "watch cartoon" play button (pointer-only) when it's on screen.
            if (Input.GetKeyDown(ActivateKey) || Input.GetKeyDown(ActivateKeyAlt))
            {
                if (ctx == InputContextKind.RoomPhoto)
                    _twoDProbe?.Activate();
                else if (ctx == InputContextKind.Fridge)
                    _fridgeMenu?.Activate();
                else if (ctx == InputContextKind.ThreeD && !threeDBlocked)
                    _actionMenu?.GoToSelected();
                else if (ctx == InputContextKind.MainMenu
                    && _cartoonButton != null && _cartoonButton.IsAvailable())
                    _cartoonButton.Activate();
                else if (ctx == InputContextKind.Phone)
                    // The phone buttons have no uGUI submit handler, so native Enter won't press the focused one;
                    // the mod presses whatever button currently holds EventSystem focus (dials it / Call / Clear).
                    _phoneMenu?.PressFocused();
            }

            // Backspace = LEAVE the current interaction via the game's ForceLeave — the reliable exit for views the mod
            // opened via cold Act() that the game's own q can't unwind (window/blind softlock). Fires in any 3D-ish
            // context; ForceLeave is a no-op if nothing's engaged, so it's safe to press anytime.
            if (Input.GetKeyDown(LeaveKey))
                _actionMenu?.Leave();

            // F9 = the contextual readout. In a conversation it repeats the latest inspection-test result (or "untested"
            // if none yet) — the trait you examined, so you can re-hear it before deciding. In the phone close-up it
            // reads the player's known phone numbers. Otherwise it's the day/phase/energy/consumables status. Same key,
            // same "tell me what matters here" intent.
            if (Input.GetKeyDown(StatusKey))
            {
                if (_signNarrator != null && _signNarrator.InConversation)
                    _signNarrator.RepeatLastTest();
                else if (ctx == InputContextKind.Phone)
                    _phoneMenu?.ReadContacts();
                else
                    _statusNarrator?.Announce();
            }

            if (Input.GetKeyDown(OrientationKey))
            {
                // In a 2D room photo the player wants ONLY the corpse info — the 3D interactable bearings are
                // meaningless there. Elsewhere (3D room) F10 reads interactables, and corpses too if any are present.
                _orientationNarrator?.Announce(corpsesOnly: ctx == InputContextKind.RoomPhoto);
            }


            if (Input.GetKeyDown(ArrowShimToggleKey) && _jawsArrowShim != null)
            {
                bool on = !_jawsArrowShim.Enabled;
                _jawsArrowShim.SetEnabled(on);
                LoggerInstance.Msg($"[F11] arrow relay {(on ? "ON" : "OFF")}.");
                // Off by default (NVDA users need nothing). A JAWS user turns it ON to make the arrows navigate,
                // accepting that JAWS can't interrupt its own speech while on (the relay re-injects keys, which
                // desyncs JAWS's keyboard processing; Unity only reads raw input). Spell the tradeoff out by ear.
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
