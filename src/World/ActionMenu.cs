using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Scene-wide action menu for the 3D first-person scene. The mod is an AIMING AID, not an interaction driver: it
    /// picks an interactable and TURNS THE PLAYER to face it; the player then uses the GAME'S OWN interact (Space) to
    /// engage it. This is the "rely on the game" model adopted 2026-06-03 after cold <c>Act()</c> invocation softlocked
    /// (it entered interactions through a side door the game's own leave couldn't unwind — see
    /// memory project-nimnah-interaction-entry-path).
    ///
    /// Controls (driven from AccessMod — DEDICATED keys, never the arrows; the arrows belong to the game everywhere):
    ///   PageDown  — next interactable: select it AND aim the player at it
    ///   PageUp    — previous interactable: same
    /// "PageUp/PageDown do everything Space doesn't" (user, 2026-06-03): they choose + aim. Then the player presses the
    /// GAME's own keys — Space to Interact (the game's Interact action binds Space, e, and LMB — confirmed via the
    /// asset rip), or WASD to walk away, or PageUp/Down to aim elsewhere. The mod NEVER invokes the interaction itself,
    /// so entry and leave both work natively (the game runs its own engage/leave state machine).
    ///
    /// Aiming = the game's real entry HEAD: mark the view's <c>_raycastTarget.IsTargeted=true</c> (the focus state the
    /// game's leave expects) and call <c>IPlayerService.LookAtWithZoom(_lookAtPos, _fov, dur)</c> to turn+zoom to it.
    /// We aim ONCE and trust the game to hold focus (LookAtWithZoom locks the look during its transition); if focus is
    /// found to drift before Space, re-assert IsTargeted per-frame.
    ///
    /// Source: <c>ActionableObjectsViewProvider.ActionableObjectViews</c> (the flat 3D interactable set). All
    /// Zenject-free: the provider is found via inactive-inclusive find; the player-position proxy for bearings is
    /// <c>Camera.main</c>. The PlayerService is read off the live view's own <c>PlayerService</c> field.
    /// </summary>
    public sealed class ActionMenu
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _viewProviderClass, _getViews;     // ActionableObjectsViewProvider + get_ActionableObjectViews
        private IntPtr _viewClass, _getCanShowHint;  // AActionableObjectView + get_CanShowHint (for the list)
        private IntPtr _act;  // AActionableObjectView.Act() (private) — invoked for ALL views AFTER focusing IsTargeted
                              // (the targeted-entry path), so the game owns the interaction and its own q can close it.
        private IntPtr _doorTriggerClass;  // _Code.…DoorTrigger — kept for type-ID in diagnostics, not an activate gate.
        private IntPtr _interactMethod;    // AInteractableObject.Interact() — DIAGNOSTIC capture of its cold-invoke throw.
        private IntPtr _getHardConditions, _getSoftConditions; // availability gates (resolved on concrete classes).
        private IntPtr _closeUpsController, _getIsAnyCloseUpActive; // ICloseUpsController + IsAnyCloseUpActive — the
                                                                   // CROSS-SYSTEM "a close-up is already open" guard.
        private IntPtr _windowsManager, _getIsInWindow; // IWindowsManager + IsInWindow — the window/blind re-entrancy guard.
        private IntPtr _actionablesManager, _forceLeave; // IActionableObjectsManager + ForceLeave() — the mod-driven
                                                         // exit, since the game's q can't unwind a cold-Act()-opened view.
        // The SECOND interactable system: AInteractableObject (radio, phone, cat, mushroom, hatch, …). These live in
        // InteractablesViewProvider, NOT the door/window provider above, so the action list missed them entirely —
        // which is why the radio was unfindable. They share the abstract base AInteractableObject, so we enumerate them
        // Zenject-free via FindObjectsByType(base) (Unity matches subclasses) and merge them into the same list.
        private IntPtr _interactableClass;  // _Code.Infrastructure.AInteractableObject (abstract base)
        private IntPtr _interactablesProviderClass; // _Code.Infrastructure.InteractablesViewProvider
        // The cat is a ROAMING, time-gated interactable: its Interact() no-ops when the cat isn't physically present
        // (it only appears in certain rooms / times of day). It still passes the generic validity filter, so Enter on it
        // was a silent no-op. We additionally gate the cat entry on a live CatInstance (the actual cat GameObject) being
        // active in the scene — readable Zenject-free, unlike CatController.IsCatActive (private setter, not on the
        // ICatController interface). _catInteractableClass identifies the cat among the interactables.
        private IntPtr _catInteractableClass; // _Code.Infrastructure.CatInteractable (identifies the cat among interactables)
        // Cat night gate. The cat is petable during the DAY only — at night the entry is dead (Enter no-ops). Neither
        // CatInstance-presence nor CatController.IsCatActive distinguishes this (both stay true at night, confirmed
        // in-game), so we gate on the day/night controller's time of day directly.
        private IntPtr _dayNight, _getTimeOfDay; // IDayNightController + get_CurrentTimeOfDay (ETimeOfDay: Day=0, Night=1)
        private IntPtr _windowBoardsClass;    // _Code.Infrastructure.WindowBoardsInteractable (board-up prompts, found by scene scan)
        private IntPtr _raycastTargetBaseClass, _getRaycastIsLocked;
        // Focus plumbing: set the selected object's raycast target IsTargeted so the game's Act()/Interact() registers it
        // as the current interaction (see GoToSelected). _setIsTargeted is set_IsTargeted(bool) on the ARaycastTarget base.
        private IntPtr _setIsTargeted;  // ARaycastTarget.set_IsTargeted(bool)
        private IntPtr _playerControllerClass;                      // _Code.Player.PlayerController (holds cameraTarget + RealCamera)

        // Localized label resolution: the spoken label should be the game's player-facing, LOCALIZED subject — the
        // RaycastTargetHint._subjectLocalizationKey (a LocalizedString) — not the raw English GameObject name (e.g. the
        // hatch is just "Hatch" in the scene, with the real prompt in the localization tables). Both interactable
        // systems hold the hint at _raycastTarget; we read its subject key and resolve it via GetLocalizedString(),
        // mirroring GameStateAccess's per-day holiday resolution. Falls back to the humanized name when unavailable.
        private IntPtr _raycastHintClass;        // _Code.Raycast.RaycastTargetHint (holds _subjectLocalizationKey)

        // Current selection, keyed by the view pointer so it survives list rebuilds (positions/availability shift).
        private IntPtr _selected;
        // Whether the current selection is an AInteractableObject (radio/phone/cat/…) vs a door/window view — they
        // engage differently (see Entry.IsInteractable), so GoToSelected branches on this.
        private bool _selectedIsInteractable;

        // The view we last forced IsTargeted=true on in GoToSelected (zero = none). We set that flag OUT OF BAND (the
        // game normally sets it via its own ray), so the game's native leave ('q') closes the view WITHOUT clearing our
        // forced flag — it lingers on the raycast target. A stale lingering target is what intermittently wedged a later
        // open (the "only once" softlock). Tick reconciles it: once nothing's engaged anymore, we clear our forced
        // targets and reset this. Tracked separately from _selected because the player can re-select after leaving.
        private IntPtr _focusedView;
        // True once a focused open has been SEEN engaged (IsInWindow/engaged went true). Tick only treats "nothing
        // engaged" as a leave AFTER this, so it can't clear our focus during the open ramp (before the flag flips true).
        private bool _focusSeenEngaged;

        // UNIFIED ACTIVATION STATE (walk → aim → synthesize the game's interact key). Used for EVERY 3D object so the
        // game opens/closes each interaction through its own input flow — the native back-out then always works, which is
        // what avoids the cold-invoke side-door softlocks. Started by GoToSelected, driven each frame by UpdateActivation.
        private IntPtr _activateObj;        // the object we're walking to + activating (zero = none)
        private int _activateFramesLeft;    // frames remaining in the walk-then-press window (timeout guard)
        private bool _interactPressed;      // one-shot: synthesize the interact key once we're in range, not per-frame
        // TIMING IS WALL-CLOCK, NOT FRAME-COUNTED. The injected interact (Space) used to settle/hold for a fixed number
        // of FRAMES, which is a different DURATION at different framerates — so plugging in a higher-refresh monitor made
        // the held press too short for the game's interact to read, and the player had to press Enter twice. Seconds are
        // refresh-rate-independent, so the same hold lands at 60 Hz, 144 Hz, etc. (user-reported regression, 2026-06-29.)
        private float _inRangeSettleUntil;  // realtimeSinceStartup until which we keep aiming before pressing (0 = not arrived)
        private float _interactReleaseAt;   // >0 ⇒ realtimeSinceStartup at which to release the held injected Space
        // Player-service plumbing for the walk (resolved via ZenjectResolver, like GameStateAccess).
        private IntPtr _playerService, _psMoveXZ, _psGetPosition;
        private const float InteractRangeM = 2.0f;   // how close we must get before the interact key takes
        private const float WalkSpeed = 3.0f;        // MoveXZ speed
        // Injected-interact timing in SECONDS (framerate-independent — see the activation-state fields). Settle ≈ 4
        // frames at 60 Hz; hold ≈ 3 frames at 60 Hz, but now generous enough that the game samples the edge even at high
        // refresh rates. Holding a touch longer is harmless (it's a single discrete interact press).
        private const float InRangeSettleSeconds = 0.07f; // keep aiming this long after arrival so the raycast locks on
        private const float InteractHoldSeconds = 0.06f;  // how long to hold the injected Space before releasing

        public ActionMenu(ISpeechOutput speech) { _speech = speech; }

        /// <summary>
        /// Step the selection by one (Up/Down) and announce it. SELECT + ANNOUNCE ONLY — activation is a separate
        /// deliberate key (<see cref="GoToSelected"/>, Enter), never on cycling, so browsing the list can't trigger an
        /// interaction. We never interact ourselves here.
        /// </summary>
        public void Cycle(bool backwards)
        {
            try
            {
                EnsureResolved();
                List<Entry> entries = BuildEntries();
                if (entries.Count == 0)
                {
                    _speech.Speak("No interactions available.", interrupt: true);
                    _selected = IntPtr.Zero;
                    return;
                }

                int idx = MenuStepUtil.NextIndex(entries.FindIndex(e => e.View == _selected), entries.Count, backwards);
                Entry sel = entries[idx];
                _selected = sel.View;
                _selectedIsInteractable = sel.IsInteractable;
                _speech.Speak($"{sel.Name}, {idx + 1} of {entries.Count}.", interrupt: true);
                MelonLogger.Msg($"[ActionMenu] selected {idx + 1}/{entries.Count}: '{sel.Name}' ({sel.Bearing}). Press the go key to walk there.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Cycle threw: {e.Message}");
            }
        }

        /// <summary>
        /// Activate (Enter) the SELECTED interactable by reproducing the GAME'S OWN interaction flow: walk to it, aim at
        /// it, then inject the game's real interact key (Space) through the Input System. The mod NO LONGER cold-invokes
        /// <c>Act()</c>/<c>Interact()</c> — those entered the interaction through a side door the game's own leave ('q')
        /// couldn't always unwind (the blind/window softlocks). Driving the real interact key means the game opens AND
        /// closes everything through its own state machine, so its native back-out always works — fewer softlocks by
        /// construction. The flow (kicked off here, completed in <see cref="UpdateActivation"/> over the next frames):
        ///   1. <c>MoveXZ</c> the player toward the object (harmless if already close; required when far — e.g. the cat,
        ///      which has no standing-pos auto-walk and whose interaction needs you physically in range).
        ///   2. Once within range, aim so the game's raycaster locks the object, settle a few frames, then inject a Space
        ///      press (held briefly so the game's own update consumes the edge — see <see cref="PressInteractKey"/>).
        /// Focus (<c>IsTargeted</c>) is still set so the object is the registered target while the key fires; the focus
        /// reconcile clears it after a native leave. Safe to call when nothing's selected (announces).
        /// </summary>
        public void GoToSelected()
        {
            try
            {
                EnsureResolved();
                if (_selected == IntPtr.Zero)
                {
                    _speech.Speak("Nothing selected. Use the up and down arrows to choose something first.", interrupt: true);
                    return;
                }

                string name = HumanizeName(Il2CppRaw.GetUnityObjectName(_selected));

                // RE-ENTRANCY GUARD — the Blinds1→Blinds2 softlock. Opening a SECOND view while a FIRST is still open
                // wedges both. The manager-level flags catch it (the per-view IsLooking/_isAnimating read FALSE for an
                // engaged blind): IWindowsManager.IsInWindow is true while any window/blind/curtain view is open, and
                // ICloseUpsController.IsAnyCloseUpActive covers the fridge/radio/phone close-ups. Both fail OPEN, so a
                // resolve glitch can never make activation permanently inert. If something's already open, REFUSE and
                // tell the player to leave first (now native 'q', the game's own back-out — see Leave()) rather than
                // stack. FindBusyOtherName names the culprit when readable; otherwise a generic prompt.
                if (IsInWindow() || IsAnyCloseUpActive())
                {
                    string? busy = FindBusyOtherName(_selected);
                    string where = busy != null ? $"at {busy}" : "in something";
                    _speech.Speak($"Already {where}. Press Q to leave first, then use {name}.", interrupt: true);
                    MelonLogger.Msg($"[ActionMenu] blocked activating '{name}' — already engaged (busy={busy ?? "?"}). Told player to leave.");
                    return;
                }

                // UNIFIED ACTIVATION — walk → aim → synthesize the game's own interact key, for EVERY 3D object (doors,
                // blinds, curtains, peephole, fridge, radio, phone, cat, …). We deliberately DON'T cold-invoke Act()/
                // Interact() anymore: those entered the interaction through a side door the game's own leave ('q') couldn't
                // always unwind (the blind/window softlocks). Pressing the game's REAL interact key instead means the game
                // opens AND closes everything through its own state machine, so its native back-out always works — fewer
                // softlocks by construction (user's call). The flow, all driven from Tick (UpdateActivation):
                //   1. MoveXZ the player toward the object (harmless if already close; required when far — e.g. the cat,
                //      which has no standing-pos auto-walk. Views DO auto-walk to their standing pos when the native key
                //      fires, but walking first does no harm and gets non-view objects in range).
                //   2. Once within range, aim at it (so the game's raycaster locks it / shows its hint), settle a few
                //      frames, then synthesize a Space press — the game's Interact action (bound to Space/E/LMB).
                // Focus is still set so the object is the registered target while the key fires; reconcile clears it after.
                IntPtr ownerClass = _selectedIsInteractable ? _interactableClass : _viewClass;
                FocusTarget(_selected, ownerClass);
                _focusedView = _selected; // remember what WE forced focus on, so Tick can clear it after a native leave

                if (_playerService != IntPtr.Zero && _psMoveXZ != IntPtr.Zero)
                {
                    Vector3 targetPos = Il2CppRaw.GetComponentWorldPosition(_selected);
                    Il2CppRaw.InvokeWithVector3Float(_playerService, _psMoveXZ, targetPos, WalkSpeed);
                    MelonLogger.Msg($"[ActionMenu] '{name}': walking to {targetPos} via MoveXZ, will synthesize interact when in range.");
                }
                else
                {
                    // No walk available — still aim + press from the current spot (works for already-close objects).
                    MelonLogger.Warning("[ActionMenu] player service / MoveXZ unresolved — aiming + pressing from current position.");
                }

                _activateObj = _selected;
                _activateFramesLeft = 240; // ~4s window to reach + engage, then give up (frame-count is fine for a coarse timeout)
                _interactPressed = false;
                _inRangeSettleUntil = 0f;
                _speech.Speak($"Using {name}.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] GoToSelected threw: {e.Message}");
            }
        }

        /// <summary>
        /// FALLBACK leave for the current interaction, via the game's public <c>IActionableObjectsManager.ForceLeave()</c>.
        /// Since <see cref="GoToSelected"/> now opens through the game's targeted-entry path, the game's OWN q closes the
        /// view (it's registered as the current interaction) — that's the primary exit. This is the belt-and-suspenders
        /// fallback (bound to Backspace) for any view that still won't unwind natively: ForceLeave gets the player out of
        /// whatever actionable they're in regardless of entry path, and is a no-op when nothing's engaged. Resolved
        /// lazily via Zenject + cached. Never throws.
        /// </summary>
        public void Leave()
        {
            try
            {
                if (_forceLeave == IntPtr.Zero)
                {
                    if (_actionablesManager == IntPtr.Zero)
                        _actionablesManager = ZenjectResolver.Resolve("_Code.Infrastructure.ActionableObjects", "IActionableObjectsManager");
                    if (_actionablesManager == IntPtr.Zero)
                    {
                        MelonLogger.Warning("[ActionMenu] Leave: couldn't resolve IActionableObjectsManager.");
                        return;
                    }
                    _forceLeave = Il2CppRaw.GetMethod(IL2CPP.il2cpp_object_get_class(_actionablesManager), "ForceLeave", 0);
                    if (_forceLeave == IntPtr.Zero) { MelonLogger.Warning("[ActionMenu] Leave: ForceLeave() not found."); return; }
                }
                bool ok = Il2CppRaw.InvokeVoid(_actionablesManager, _forceLeave);
                MelonLogger.Msg($"[ActionMenu] ForceLeave() invoked (threw={!ok}).");

                // ForceLeave only unwinds the ACTIONABLE-OBJECT system (doors/windows/blinds). Close-ups
                // (radio/fridge/phone/…) are a SEPARATE system that ForceLeave doesn't touch — and the radio's native
                // 'q' wasn't closing it either — so also hide any active close-up view directly. Hide() is the public
                // exit each close-up runs on its own back-out; calling it is the reliable programmatic leave.
                HideActiveCloseUp();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Leave threw: {e.Message}");
            }
        }

        /// <summary>
        /// Close whichever close-up (radio/fridge/phone/consumable/mushroomlist) is currently open by invoking its
        /// public <c>Hide()</c> — the same exit the view runs on its own back-out. Backspace's universal "get me out"
        /// covers the close-up system, which <see cref="Leave"/>'s ForceLeave (actionables only) can't reach. No-op when
        /// no close-up is active. Never throws.
        /// </summary>
        private void HideActiveCloseUp()
        {
            try
            {
                foreach (var (ns, name) in CloseUpViewTypes)
                {
                    IntPtr klass = Il2CppRaw.GetClass(GameAsm, ns, name);
                    if (klass == IntPtr.Zero) continue;
                    IntPtr view = Il2CppRaw.FindObjectIncludingInactive(klass);
                    if (view == IntPtr.Zero || !Il2CppRaw.GetComponentGameObjectActive(view)) continue;

                    IntPtr hide = Il2CppRaw.GetMethod(klass, "Hide", 0);
                    if (hide == IntPtr.Zero) continue;
                    bool ok = Il2CppRaw.InvokeVoid(view, hide); // returns a UniTask; fire-and-forget
                    MelonLogger.Msg($"[ActionMenu] Hide() on active close-up {name} (threw={!ok}).");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] HideActiveCloseUp threw: {e.Message}");
            }
        }

        // The concrete close-up view types and their namespaces (from the decompile). Radio/Phone are under their own
        // sub-namespaces; Fridge/Consumable/Mushroomlist under the _NINAH__ variant.
        private static readonly (string ns, string name)[] CloseUpViewTypes =
        {
            ("_Code.Infrastructure.CloseUps.Views.Radio", "RadioCloseUpView"),
            ("_Code.Infrastructure.CloseUps.Views.Phone", "PhoneCloseUpView"),
            ("_Code.Infrastructure.CloseUps.Views", "FridgeCloseUpView"),
            ("_Code.Infrastructure._NINAH__CloseUps.Views.Consumables", "ConsumableCloseUpView"),
            ("_Code.Infrastructure._NINAH__CloseUps.Views.Mushroomlist", "MushroomlistCloseUpView"),
        };

        /// <summary>
        /// Aim the player's CAMERA at <paramref name="view"/> by rotating both <c>PlayerController.cameraTarget</c> and
        /// <c>RealCamera</c> to face the object's look point (<c>Transform.LookAt</c>). This makes the game's own
        /// raycaster (which casts along the camera forward) agree with the focus, so the game's state keeps the object
        /// targeted while its action runs. <see cref="GoToSelected"/> sets <c>_raycastTarget.IsTargeted</c> directly (via
        /// <see cref="FocusTarget"/>) as the real gate; this camera aim backs it up. Returns true if the LookAt ran.
        /// Never throws.
        /// </summary>
        private bool AimCameraAt(IntPtr view)
        {
            try
            {
                if (_playerControllerClass == IntPtr.Zero)
                    _playerControllerClass = Il2CppRaw.GetClass(GameAsm, "_Code.Player", "PlayerController");
                IntPtr pc = _playerControllerClass != IntPtr.Zero ? Il2CppRaw.FindObjectIncludingInactive(_playerControllerClass) : IntPtr.Zero;
                if (pc == IntPtr.Zero) return false;

                IntPtr camTargetGo = Il2CppRaw.ReadObjectField(pc, _playerControllerClass, "cameraTarget");
                IntPtr camTargetTr = Il2CppRaw.GetGameObjectTransform(camTargetGo);
                if (camTargetTr == IntPtr.Zero) return false;

                IntPtr lookAtPos = Il2CppRaw.ReadObjectField(view, _viewClass, "_lookAtPos");
                Vector3 aimPoint = lookAtPos != IntPtr.Zero
                    ? Il2CppRaw.GetComponentWorldPosition(lookAtPos) : Il2CppRaw.GetComponentWorldPosition(view);

                // Aim BOTH the camera target (the controller follows it) AND RealCamera itself (the ray's actual origin),
                // so the ray points dead-on at the object this very frame — aiming only cameraTarget left a ~0.85 dot
                // near-miss because its position differs from the camera's.
                IntPtr realCam = Il2CppRaw.InvokeObjectGetter(pc, Il2CppRaw.GetMethod(_playerControllerClass, "get_RealCamera", 0));
                IntPtr realCamTr = realCam != IntPtr.Zero ? Il2CppRaw.GetGameObjectTransform(Il2CppRaw.GetComponentGameObject(realCam)) : IntPtr.Zero;
                bool aimedTarget = Il2CppRaw.TransformLookAt(camTargetTr, aimPoint);
                bool aimedCam = Il2CppRaw.TransformLookAt(realCamTr, aimPoint);
                return aimedTarget || aimedCam;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] AimCameraAt threw: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Per-frame upkeep, driven by AccessMod. Sole job now: RECONCILE the forced focus from <see cref="GoToSelected"/>.
        /// We set IsTargeted out of band (the game normally sets it via its own ray), so the game's native leave ('q')
        /// closes the view but doesn't clear our flag — it lingers on the raycast target, and a later open can land on a
        /// stale target (the intermittent "only once" wedge). Once the open has actually ENGAGED (IsInWindow / engaged
        /// went true — <see cref="_focusSeenEngaged"/>), we watch for it dropping back to "nothing engaged" = the player
        /// left (by any key), then clear our targets and reset. Gating on _focusSeenEngaged avoids clearing during the
        /// open ramp (the flag flips true a frame or two after we focus). Cheap manager-flag reads; all fail-open.
        /// Never throws.
        /// </summary>
        public void Tick()
        {
            // Release the held injected Space once the wall-clock hold elapses (runs independently of the activation
            // window, since the menu may already have opened and ended activation). Keeps the press an edge the game can
            // consume, with a hold duration that's the same at any framerate.
            if (_interactReleaseAt > 0f && Time.realtimeSinceStartup >= _interactReleaseAt)
            {
                _interactReleaseAt = 0f;
                ReleaseInteractKey();
            }

            UpdateActivation();

            if (_focusedView == IntPtr.Zero) return; // nothing we forced focus on to reconcile
            try
            {
                bool engaged = IsInWindow() || IsAnyCloseUpActive() || IsAnyEngaged();
                if (engaged)
                {
                    _focusSeenEngaged = true;
                }
                else if (_focusSeenEngaged)
                {
                    // Engaged → not engaged: the player left (q, Backspace, or the game closed it). Drop our flag.
                    ClearAllTargets(IntPtr.Zero);
                    _focusedView = IntPtr.Zero;
                    _focusSeenEngaged = false;
                    MelonLogger.Msg("[ActionMenu] reconciled forced focus after leave (cleared stale IsTargeted).");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] focus reconcile threw: {e.Message}");
                _focusedView = IntPtr.Zero; _focusSeenEngaged = false; // don't get stuck retrying a bad state
            }
        }

        /// <summary>
        /// SAFETY-NET reconcile, called when the input context returns to free-roam (no close-up / dialog overlay). The
        /// engaged-latch in <see cref="Tick"/> can be MISSED: a close-up that opens and closes between two frames, or an
        /// alt-tab that interrupts frame timing, means we may never observe the engaged→not-engaged edge, so a forced
        /// <c>IsTargeted</c> lingers forever — the phone/radio "stacking" the user reported (it worsened with alt-tab).
        /// Here we don't rely on the latch: if we still hold a forced focus but NOTHING is actually engaged right now,
        /// the interaction is long over, so clear every target unconditionally and reset. No-op when nothing is forced
        /// or something is genuinely engaged. Cheap fail-open reads; never throws.
        /// </summary>
        public void ReconcileWhenFreeRoam()
        {
            if (_focusedView == IntPtr.Zero) return;
            try
            {
                if (IsInWindow() || IsAnyCloseUpActive() || IsAnyEngaged()) return; // still engaged — leave it to Tick
                ClearAllTargets(IntPtr.Zero);
                _focusedView = IntPtr.Zero;
                _focusSeenEngaged = false;
                MelonLogger.Msg("[ActionMenu] free-roam reconcile cleared a lingering forced target (latch was missed).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] free-roam reconcile threw: {e.Message}");
                _focusedView = IntPtr.Zero; _focusSeenEngaged = false;
            }
        }

        // Per-frame driver for the unified walk → aim → inject-interact activation (any 3D object). Watches the distance
        // after GoToSelected kicked off the walk; once in range it aims, settles a few frames so the game's raycast locks
        // the object, then injects ONE interact keypress (Space). Stops on engage (something opened) or timeout. The game
        // then owns the interaction, so its native 'q'/back-out closes it — no cold-invoke side door.
        private void UpdateActivation()
        {
            if (_activateFramesLeft <= 0 || _activateObj == IntPtr.Zero) return;

            // Something engaged (a window/close-up/look opened) → the interact landed; we're done.
            if (IsInWindow() || IsAnyCloseUpActive() || IsAnyEngaged())
            {
                EndActivation();
                return;
            }

            Vector3 playerPos = _playerService != IntPtr.Zero && _psGetPosition != IntPtr.Zero
                ? Il2CppRaw.InvokeVector3Getter(_playerService, _psGetPosition) : Vector3.zero;
            Vector3 objPos = Il2CppRaw.GetComponentWorldPosition(_activateObj);
            float dx = playerPos.x - objPos.x, dz = playerPos.z - objPos.z;
            float dist2 = dx * dx + dz * dz;
            bool inRange = dist2 <= (InteractRangeM * InteractRangeM);

            if (inRange && !_interactPressed)
            {
                // Aim each frame so the game's raycaster locks the object (and shows its hint). Settle for a short
                // WALL-CLOCK window before injecting so the raycast registers the new aim, else the synthetic key fires
                // before the object is the game's target and is wasted. Time-based so it's the same at any framerate.
                AimCameraAt(_activateObj);
                FocusTarget(_activateObj, _selectedIsInteractable ? _interactableClass : _viewClass);
                float now = Time.realtimeSinceStartup;
                if (_inRangeSettleUntil <= 0f)
                    _inRangeSettleUntil = now + InRangeSettleSeconds; // first in-range frame: start the settle timer
                else if (now >= _inRangeSettleUntil)
                {
                    PressInteractKey();
                    _interactPressed = true;
                    MelonLogger.Msg("[ActionMenu] in range, aimed + settled, injected interact (Space).");
                }
            }

            _activateFramesLeft--;
            if (_activateFramesLeft == 0)
            {
                MelonLogger.Msg("[ActionMenu] activation timed out before engaging.");
                EndActivation();
            }
        }

        // Clears the unified activation state.
        private void EndActivation()
        {
            _activateFramesLeft = 0;
            _activateObj = IntPtr.Zero;
            _interactPressed = false;
            _inRangeSettleUntil = 0f;
        }

        // Inject Space DOWN (held). We deliberately DON'T release in the same call: an immediate release + a second
        // InputSystem.Update() consumes the press edge before the GAME'S own update reads it (InteractTriggered would be
        // true only in the gap between our two Updates — the game saw nothing). So we queue the press and let the game's
        // normal update consume the edge over the next frame(s), then release later via ReleaseInteractKey. We also don't
        // force an InputSystem.Update() here — the game pumps the Input System itself each frame; queuing is enough.
        private void PressInteractKey()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null)
                {
                    MelonLogger.Warning("[ActionMenu] PressInteractKey: Keyboard.current is null — can't inject interact.");
                    return;
                }
                var down = new UnityEngine.InputSystem.LowLevel.KeyboardState();
                down.Set(UnityEngine.InputSystem.Key.Space, true);
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb, down, -1.0);
                // Release after a WALL-CLOCK hold so the game's update sees the held press as an edge regardless of
                // framerate. (Frame-counted holds were too short on high-refresh monitors → the double-press bug.)
                _interactReleaseAt = Time.realtimeSinceStartup + InteractHoldSeconds;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] PressInteractKey threw: {e.Message}");
            }
        }

        // Release the injected Space (empty keyboard state). Called from Tick a few frames after the press.
        private void ReleaseInteractKey()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return;
                var up = new UnityEngine.InputSystem.LowLevel.KeyboardState();
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb, up, -1.0);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] ReleaseInteractKey threw: {e.Message}");
            }
        }

        /// <summary>
        /// Whether the player is currently ENGAGED in an interaction — any interactable has <c>IsLooking=true</c> (e.g.
        /// looking through the peephole). In that state the 3D action keys must do NOTHING: you're "inside" something and
        /// must leave it ('q') first; otherwise PgUp/PgDn/Backspace fall through and mislead. Inactive-inclusive scan;
        /// never throws (false on any failure, so a read error doesn't wrongly suppress).
        /// </summary>
        public bool IsAnyEngaged()
        {
            try
            {
                EnsureResolved();
                if (_viewClass == IntPtr.Zero) return false;
                IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
                if (provider == IntPtr.Zero) return false;
                foreach (IntPtr v in Il2CppRaw.ReadObjectArray(Il2CppRaw.InvokeObjectGetter(provider, _getViews)))
                {
                    if (v == IntPtr.Zero) continue;
                    if (Il2CppRaw.ReadBoolField(v, _viewClass, "IsLooking")) return true;
                }
            }
            catch { /* false on failure */ }
            return false;
        }

        /// <summary>
        /// The display name of any view OTHER than <paramref name="except"/> that is currently mid-interaction —
        /// <c>IsLooking</c> (engaged) or <c>_isAnimating</c> (its open/close animation is still playing, i.e. half-open).
        /// Returns null if none is busy. This is the re-entrancy guard for <see cref="GoToSelected"/>: activating a
        /// SECOND view while a FIRST is half-open wedges both (the user's "one half-open while the other took over"
        /// softlock). Inactive-inclusive scan via the provider array. Fails OPEN — on any read error returns null
        /// (allow), so a transient glitch can't make activation permanently inert.
        /// </summary>
        /// <summary>
        /// Whether ANY close-up (fridge/radio/phone/consumable/…) is currently open, via the Zenject-resolved
        /// <c>ICloseUpsController.IsAnyCloseUpActive</c>. This is the cross-system busy signal — true regardless of which
        /// interaction system owns the open view — so it guards activation of doors, blinds AND interactables uniformly.
        /// Resolved lazily + cached. Fails OPEN (false on any miss) so it can never make activation permanently inert.
        /// </summary>
        private bool IsAnyCloseUpActive()
        {
            try
            {
                if (_getIsAnyCloseUpActive == IntPtr.Zero)
                {
                    if (_closeUpsController == IntPtr.Zero)
                        _closeUpsController = ZenjectResolver.Resolve("_Code.Infrastructure.CloseUps", "ICloseUpsController");
                    if (_closeUpsController == IntPtr.Zero) return false; // can't resolve ⇒ allow (fail open)
                    _getIsAnyCloseUpActive = Il2CppRaw.GetMethod(
                        IL2CPP.il2cpp_object_get_class(_closeUpsController), "get_IsAnyCloseUpActive", 0);
                    if (_getIsAnyCloseUpActive == IntPtr.Zero) return false;
                }
                return Il2CppRaw.InvokeBoolGetter(_closeUpsController, _getIsAnyCloseUpActive);
            }
            catch { return false; } // fail open
        }

        /// <summary>
        /// Whether the player is currently IN a window interaction (window/blinds/curtains open), via the Zenject-
        /// resolved <c>IWindowsManager.IsInWindow</c>. Windows are NOT close-ups, so <see cref="IsAnyCloseUpActive"/>
        /// can't see them — this is the signal that catches the Blinds1→Curtains wedge. Lazy + cached; fails OPEN.
        /// </summary>
        private bool IsInWindow()
        {
            try
            {
                if (_getIsInWindow == IntPtr.Zero)
                {
                    if (_windowsManager == IntPtr.Zero)
                        _windowsManager = ZenjectResolver.Resolve("_Code.Infrastructure.Windows", "IWindowsManager");
                    if (_windowsManager == IntPtr.Zero) return false; // fail open
                    _getIsInWindow = Il2CppRaw.GetMethod(
                        IL2CPP.il2cpp_object_get_class(_windowsManager), "get_IsInWindow", 0);
                    if (_getIsInWindow == IntPtr.Zero) return false;
                }
                return Il2CppRaw.InvokeBoolGetter(_windowsManager, _getIsInWindow);
            }
            catch { return false; } // fail open
        }

        /// <summary>Ground-truth dump of all candidate "something is open" signals at activation time — the two
        /// Zenject manager flags plus every view's live IsLooking/_isAnimating — so we can identify which flag is true
        /// while a blind/window is open (the guard hasn't been catching it). Diagnostic only.</summary>
        private void DumpBusyState(string about, bool closeUp, bool inWindow)
        {
            try
            {
                MelonLogger.Msg($"[ActionMenu] busy-probe on activating '{about}': IsAnyCloseUpActive={closeUp} IsInWindow={inWindow}.");
                if (_viewClass == IntPtr.Zero) return;
                IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
                if (provider == IntPtr.Zero) return;
                foreach (IntPtr v in Il2CppRaw.ReadObjectArray(Il2CppRaw.InvokeObjectGetter(provider, _getViews)))
                {
                    if (v == IntPtr.Zero) continue;
                    bool looking = Il2CppRaw.ReadBoolField(v, _viewClass, "IsLooking");
                    bool animating = Il2CppRaw.ReadBoolField(v, _viewClass, "_isAnimating");
                    bool locked = Il2CppRaw.ReadBoolField(v, _viewClass, "_isLocked");
                    if (looking || animating || locked) // only log the interesting ones
                        MelonLogger.Msg($"[ActionMenu]   view '{HumanizeName(Il2CppRaw.GetUnityObjectName(v))}': " +
                                        $"IsLooking={looking} _isAnimating={animating} _isLocked={locked}.");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[ActionMenu] DumpBusyState threw: {e.Message}"); }
        }

        private string? FindBusyOtherName(IntPtr except)
        {
            try
            {
                if (_viewClass == IntPtr.Zero) return null;
                IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
                if (provider == IntPtr.Zero) return null;
                foreach (IntPtr v in Il2CppRaw.ReadObjectArray(Il2CppRaw.InvokeObjectGetter(provider, _getViews)))
                {
                    if (v == IntPtr.Zero || v == except) continue;
                    bool looking = Il2CppRaw.ReadBoolField(v, _viewClass, "IsLooking");
                    bool animating = Il2CppRaw.ReadBoolField(v, _viewClass, "_isAnimating");
                    if (looking || animating)
                        return HumanizeName(Il2CppRaw.GetUnityObjectName(v));
                }
            }
            catch { /* fail open: null = allow activation */ }
            return null;
        }

        /// <summary>
        /// Make <paramref name="view"/> the single focused target by setting its <c>_raycastTarget.IsTargeted=true</c>
        /// (and clearing every other view's, via <see cref="ClearAllTargets"/>). This is THE gate the game's Space/Act
        /// reads (F12 finding) — our forced camera turn doesn't reliably make the game's ray set it, so we set it
        /// directly. Safe + desired for doors (action = room transition). Re-asserted each hold frame because the look
        /// Update clears it. Never throws.
        /// </summary>
        private void FocusTarget(IntPtr view) => FocusTarget(view, _viewClass);

        /// <summary>
        /// Overload that reads the <c>_raycastTarget</c> field from <paramref name="ownerClass"/> — the view base for an
        /// AActionableObjectView, or AInteractableObject for the radio/phone/cat. Both systems name the field
        /// <c>_raycastTarget</c>, so only the owning class differs. Lets the unified entry focus EITHER system's target.
        /// </summary>
        private void FocusTarget(IntPtr view, IntPtr ownerClass)
        {
            if (view == IntPtr.Zero || _setIsTargeted == IntPtr.Zero || ownerClass == IntPtr.Zero) return;
            try
            {
                ClearAllTargets(view);
                IntPtr rt = Il2CppRaw.ReadObjectField(view, ownerClass, "_raycastTarget");
                if (rt != IntPtr.Zero) Il2CppRaw.InvokeVoidWithBool(rt, _setIsTargeted, true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] FocusTarget threw: {e.Message}");
            }
        }

        /// <summary>
        /// Clear <c>IsTargeted</c> on every interactable's raycast target except <paramref name="except"/>'s, enforcing
        /// the single-focus invariant so the game's Interact (Space) acts on exactly one object. There are TWO separate
        /// interaction systems and BOTH must be swept: the door/window <c>ActionableObjectsViewProvider</c> AND the
        /// <c>InteractablesViewProvider</c> (phone/radio/cat/hatch/…). Sweeping only the first left a forced phone/radio
        /// <c>IsTargeted</c> set when focus moved to another interactable — so the phone fired alongside the radio, and
        /// later a stray Space hit every still-targeted object (the close-up "stacking" bug). Each system reads the field
        /// off its OWN owner class (<c>_viewClass</c> vs <c>_interactableClass</c>). Never throws.
        /// </summary>
        private void ClearAllTargets(IntPtr except)
        {
            if (_setIsTargeted == IntPtr.Zero) return;
            try
            {
                // Door/window views.
                IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
                if (provider != IntPtr.Zero)
                {
                    IntPtr[] views = Il2CppRaw.ReadObjectArray(Il2CppRaw.InvokeObjectGetter(provider, _getViews));
                    foreach (IntPtr v in views)
                    {
                        if (v == IntPtr.Zero || v == except) continue;
                        IntPtr rt = Il2CppRaw.ReadObjectField(v, _viewClass, "_raycastTarget");
                        if (rt != IntPtr.Zero) Il2CppRaw.InvokeVoidWithBool(rt, _setIsTargeted, false);
                    }
                }

                // The SECOND system (phone/radio/cat/hatch/…). Same field name, different owner class.
                if (_interactableClass != IntPtr.Zero && _interactablesProviderClass != IntPtr.Zero)
                {
                    IntPtr ip = Il2CppRaw.FindObjectIncludingInactive(_interactablesProviderClass);
                    if (ip != IntPtr.Zero)
                    {
                        foreach (IntPtr o in EnumerateProviderInteractables(ip))
                        {
                            if (o == IntPtr.Zero || o == except) continue;
                            IntPtr rt = Il2CppRaw.ReadObjectField(o, _interactableClass, "_raycastTarget");
                            if (rt != IntPtr.Zero) Il2CppRaw.InvokeVoidWithBool(rt, _setIsTargeted, false);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] ClearAllTargets threw: {e.Message}");
            }
        }

        private readonly struct Entry
        {
            public readonly IntPtr View;
            public readonly string Name;
            public readonly string Bearing;
            // True for the AInteractableObject system (radio/phone/cat/…) vs. a door/window AActionableObjectView — they
            // resolve their action method differently in GoToSelected (Interact() vs Act()).
            public readonly bool IsInteractable;
            public Entry(IntPtr view, string name, string bearing, bool isInteractable)
            { View = view; Name = name; Bearing = bearing; IsInteractable = isInteractable; }
        }

        /// <summary>
        /// Build the current list of interactions from the provider's view array.
        ///
        /// DIAGNOSTIC MODE: the runtime <c>CanShowHint</c> getter returned false for EVERY view (empty list), so it is
        /// NOT the "available right now" gate it appeared to be — and the decompile can't settle this (bodies stripped:
        /// <c>CanShowHint =&gt; false</c> is a placeholder, not the real body). So we currently list ALL non-null views
        /// (no hard gate) and LOG per-view ground truth — name, position, <c>CanShowHint</c>, GameObject-active — so the
        /// real availability signal can be read from a live scene instead of guessed. Once we know the true gate from
        /// the log, restore a filter here.
        /// </summary>
        private List<Entry> BuildEntries()
        {
            var entries = new List<Entry>();
            if (_viewProviderClass == IntPtr.Zero) { MelonLogger.Msg("[ActionMenu] provider class unresolved."); return entries; }

            // The provider's GameObject may be inactive, so this also tries the inactive-inclusive find.
            IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
            if (provider == IntPtr.Zero)
            {
                // The provider isn't present. Probe whether the 3D interactables exist AT ALL by counting
                // AActionableObjectView instances directly (inactive-inclusive). If this is also zero, we're simply
                // not in a 3D gameplay scene; if it's non-zero, the provider is the wrong access path.
                int viewCount = _viewClass != IntPtr.Zero
                    ? Il2CppRaw.CountObjectsByType(_viewClass, includeInactive: true) : -1;
                MelonLogger.Msg($"[ActionMenu] provider not found this frame. Direct AActionableObjectView count={viewCount} " +
                                "(0 ⇒ not a 3D gameplay scene; >0 ⇒ provider is the wrong access path).");
                return entries;
            }
            IntPtr arrayPtr = Il2CppRaw.InvokeObjectGetter(provider, _getViews);
            IntPtr[] views = Il2CppRaw.ReadObjectArray(arrayPtr);
            MelonLogger.Msg($"[ActionMenu] provider views array length={views.Length}");

            Vector3 camPos = Il2CppRaw.GetMainCameraPosition();
            int i = 0;
            foreach (IntPtr v in views)
            {
                if (v == IntPtr.Zero) { MelonLogger.Msg($"[ActionMenu]   [{i++}] <null>"); continue; }
                string name = HumanizeName(Il2CppRaw.GetUnityObjectName(v));
                Vector3 pos = Il2CppRaw.GetComponentWorldPosition(v);
                i++;
                if (!IsActionableViewValidNow(v, name)) continue;
                entries.Add(new Entry(v, name, Bearing(camPos, pos), isInteractable: false));
            }

            // Merge the SECOND interactable system (radio/phone/cat/mushroom/hatch/save/…). Use the game's provider
            // field order, not broad scene enumeration: some objects exist in the scene before the manager makes them
            // interactable, and the provider order is the game's canonical order.
            AppendInteractablesFromProvider(entries, camPos);
            return entries;
        }

        /// <summary>
        /// Append <c>AInteractableObject</c>s in the game's provider order, filtering by live runtime state. Scene
        /// presence is not enough: objects like the cat/hole/mushroom can be serialized but not yet valid.
        /// </summary>
        private void AppendInteractablesFromProvider(List<Entry> entries, Vector3 camPos)
        {
            if (_interactableClass == IntPtr.Zero || _interactablesProviderClass == IntPtr.Zero) return;
            try
            {
                IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_interactablesProviderClass);
                if (provider == IntPtr.Zero)
                {
                    MelonLogger.Msg("[ActionMenu] InteractablesViewProvider not found this frame.");
                    return;
                }

                int added = 0, gated = 0;
                foreach (IntPtr o in EnumerateProviderInteractables(provider))
                {
                    if (o == IntPtr.Zero) continue;
                    bool dup = false;
                    foreach (Entry e in entries) if (e.View == o) { dup = true; break; }
                    if (dup) continue;

                    // Prefer the game's LOCALIZED subject over the raw GameObject name. The interactables (hatch/cat/
                    // save/…) are named generically/in English in the scene — the hatch GO is just "Hatch" — so the
                    // humanized name doesn't localize. The localized subject is the player-facing prompt; humanized name
                    // is the fallback. Validity is logged off the humanized name (stable, language-independent).
                    string rawName = HumanizeName(Il2CppRaw.GetUnityObjectName(o));
                    if (!IsInteractableValidNow(o, rawName))
                    {
                        gated++;
                        continue;
                    }
                    // Board-up prompts share their window's GameObject name ("Blinds 1", "Curtains", …), so without a
                    // prefix they're indistinguishable from the normal blinds/curtains entries. Prefix with "Board up" so
                    // they read as "Board up Blinds 1" etc. — keeps the per-window distinction, names the actual action.
                    // These keep the HUMANIZED name (the prefix is built around it; the localized subject would double
                    // the "board up"); every other interactable prefers the localized subject.
                    bool isWindowBoard = _windowBoardsClass != IntPtr.Zero
                        && IL2CPP.il2cpp_object_get_class(o) == _windowBoardsClass;
                    string name = isWindowBoard
                        ? "Board up " + rawName
                        : BestLabel(o, _interactableClass, Il2CppRaw.GetUnityObjectName(o));

                    Vector3 pos = Il2CppRaw.GetComponentWorldPosition(o);
                    entries.Add(new Entry(o, name, Bearing(camPos, pos), isInteractable: true));
                    added++;
                }
                MelonLogger.Msg($"[ActionMenu] merged {added} provider interactable(s); gated out {gated}.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] AppendInteractablesFromProvider threw: {e.Message}");
            }
        }

        private IEnumerable<IntPtr> EnumerateProviderInteractables(IntPtr provider)
        {
            yield return ReadProviderField(provider, "<HatchHouse>k__BackingField");
            yield return ReadProviderField(provider, "<HatchBasement>k__BackingField");
            yield return ReadProviderField(provider, "<Phone>k__BackingField");
            yield return ReadProviderField(provider, "<Radio>k__BackingField");
            yield return ReadProviderField(provider, "<Cigarettes>k__BackingField");
            yield return ReadProviderField(provider, "<SaveInteractable>k__BackingField");
            yield return ReadProviderField(provider, "<Mushroom>k__BackingField");
            yield return ReadProviderField(provider, "<TheHole>k__BackingField");
            yield return ReadProviderField(provider, "<Cat>k__BackingField");

            // Window board-up prompts (day 13: nail boards over each window). The provider's WindowBoards array reads
            // back with NULL elements even on board-up day (the references aren't populated there), so instead we find
            // the live board interactables directly in the scene by type. They're AInteractableObjects like the rest, so
            // the generic validity filter gates them — a board only appears when it's actually active (board-up day, that
            // window not yet boarded), which is exactly "prefer when present".
            if (_windowBoardsClass == IntPtr.Zero)
                _windowBoardsClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure", "WindowBoardsInteractable");
            if (_windowBoardsClass != IntPtr.Zero)
                foreach (IntPtr board in Il2CppRaw.FindObjectsByType(_windowBoardsClass, includeInactive: false))
                    yield return board;
        }

        private IntPtr ReadProviderField(IntPtr provider, string fieldName) =>
            Il2CppRaw.ReadObjectField(provider, _interactablesProviderClass, fieldName);

        private bool IsInteractableValidNow(IntPtr o, string name)
        {
            bool active = Il2CppRaw.GetComponentGameObjectActive(o);
            bool isEnabled = Il2CppRaw.ReadBoolField(o, _interactableClass, "_isEnabled");
            int lockCount = Il2CppRaw.ReadInt32Field(o, _interactableClass, "_lockCount");
            IntPtr raycast = Il2CppRaw.ReadObjectField(o, _interactableClass, "_raycastTarget");
            bool raycastLocked = IsRaycastTargetLocked(raycast);
            bool hard = InvokeConcreteBool(o, "get_HardConditions", fallback: false);
            bool soft = InvokeConcreteBool(o, "get_SoftConditions", fallback: false);

            bool valid = active && isEnabled && lockCount == 0 && raycast != IntPtr.Zero && !raycastLocked && hard && soft;

            // CAT availability gate: the cat passes every generic check above even when it isn't interactable (notably at
            // NIGHT — its Interact() no-ops then), so Enter on it was a silent no-op. Gate it on the game's own
            // CatController.IsCatActive (authoritative; false at night). (Other interactables are unaffected — this only
            // narrows the one object whose class is CatInteractable.)
            if (valid && _catInteractableClass != IntPtr.Zero && IsCat(o))
            {
                bool night = IsNight();
                MelonLogger.Msg($"[ActionMenu]   avail '{name}': IS CAT — dayNight={_dayNight != IntPtr.Zero} " +
                                $"getTimeOfDay={_getTimeOfDay != IntPtr.Zero} night={night} (cat petable in DAY only).");
                if (night) return false;
            }

            MelonLogger.Msg($"[ActionMenu]   avail '{name}': active={active} _isEnabled={isEnabled} " +
                            $"_lockCount={lockCount} raycast={raycast != IntPtr.Zero} raycastLocked={raycastLocked} " +
                            $"hard={hard} soft={soft} valid={valid}.");
            return valid;
        }

        /// <summary>True if <paramref name="o"/>'s runtime class is <c>CatInteractable</c> (matched by class pointer, so
        /// only the cat triggers the presence gate, not other interactables).</summary>
        private bool IsCat(IntPtr o)
        {
            if (o == IntPtr.Zero || _catInteractableClass == IntPtr.Zero) return false;
            return IL2CPP.il2cpp_object_get_class(o) == _catInteractableClass;
        }

        /// <summary>
        /// Whether it's currently NIGHT, per the game's <c>IDayNightController.CurrentTimeOfDay</c> (ETimeOfDay:
        /// Day=0, Night=1). Used to hide the cat at night (it's petable in the day only). FAILS CLOSED (returns false ⇒
        /// "not night" ⇒ don't hide) if the controller/getter can't be read, so a resolve glitch never permanently hides
        /// the cat. Never throws.
        /// </summary>
        private bool IsNight()
        {
            try
            {
                if (_dayNight == IntPtr.Zero || _getTimeOfDay == IntPtr.Zero) return false; // can't tell ⇒ don't hide
                return Il2CppRaw.InvokeInt32Getter(_dayNight, _getTimeOfDay, fallback: 0) == 1; // 1 == Night
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] IsNight threw: {e.Message}; treating as day.");
                return false;
            }
        }

        private bool IsRaycastTargetLocked(IntPtr raycast)
        {
            if (raycast == IntPtr.Zero) return true;
            bool locked = _getRaycastIsLocked != IntPtr.Zero && Il2CppRaw.InvokeBoolGetter(raycast, _getRaycastIsLocked);
            int lockedCount = Il2CppRaw.ReadInt32Field(raycast, _raycastTargetBaseClass, "LockedCount", 0);
            return locked || lockedCount > 0;
        }

        private bool InvokeConcreteBool(IntPtr obj, string getterName, bool fallback)
        {
            if (obj == IntPtr.Zero) return fallback;
            IntPtr concrete = IL2CPP.il2cpp_object_get_class(obj);
            IntPtr getter = concrete != IntPtr.Zero ? Il2CppRaw.GetMethod(concrete, getterName, 0) : IntPtr.Zero;
            if (getter == IntPtr.Zero)
            {
                if (getterName == "get_HardConditions") getter = _getHardConditions;
                else if (getterName == "get_SoftConditions") getter = _getSoftConditions;
            }
            return Il2CppRaw.InvokeBoolGetter(obj, getter, fallback);
        }

        private bool IsActionableViewValidNow(IntPtr view, string name)
        {
            bool active = Il2CppRaw.GetComponentGameObjectActive(view);
            bool locked = Il2CppRaw.ReadBoolField(view, _viewClass, "_isLocked");
            IntPtr raycast = Il2CppRaw.ReadObjectField(view, _viewClass, "_raycastTarget");
            bool raycastLocked = IsRaycastTargetLocked(raycast);
            bool canShowHint = InvokeActionableCanShowHint(view);

            bool valid = active && !locked && raycast != IntPtr.Zero && !raycastLocked && canShowHint;
            MelonLogger.Msg($"[ActionMenu]   avail-view '{name}': active={active} _isLocked={locked} " +
                            $"raycast={raycast != IntPtr.Zero} raycastLocked={raycastLocked} canShowHint={canShowHint} valid={valid}.");
            return valid;
        }

        private bool InvokeActionableCanShowHint(IntPtr view)
        {
            if (view == IntPtr.Zero) return false;
            IntPtr concrete = IL2CPP.il2cpp_object_get_class(view);
            IntPtr getter = concrete != IntPtr.Zero ? Il2CppRaw.GetMethod(concrete, "get_CanShowHint", 0) : IntPtr.Zero;
            if (getter == IntPtr.Zero) getter = _getCanShowHint;
            return Il2CppRaw.InvokeBoolGetter(view, getter, fallback: false);
        }

        /// <summary>Coarse distance + side of <paramref name="target"/> from the camera (XZ plane only). No look
        /// direction needed (Zenject-free) — we report distance and left/right/ahead/behind relative to world, which
        /// is enough for "which one is this" in a sparse room. Direction is camera-relative side via world axes.</summary>
        private static string Bearing(Vector3 from, Vector3 target)
        {
            Vector3 to = target - from; to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.5f) return "right here";
            string range = dist < 2.5f ? "close" : dist < 6f ? "a few steps away" : "far";
            return range;
        }

        private static string HumanizeName(string? raw) => MenuStepUtil.Humanize(raw);

        /// <summary>
        /// The label to speak for an interactable: the game's LOCALIZED subject when it resolves, else the humanized
        /// GameObject name. The localized subject is the player-facing prompt noun (already language-correct), which the
        /// raw GameObject name is not — most visibly for the hatch ("Hatch" in the scene, localized elsewhere).
        /// <paramref name="ownerClass"/> is the class declaring <c>_raycastTarget</c> (the view base for an actionable
        /// view, or AInteractableObject for the second system). Never throws.
        /// </summary>
        private string BestLabel(IntPtr obj, IntPtr ownerClass, string? rawName)
        {
            string? localized = LocalizedSubject(obj, ownerClass);
            return !string.IsNullOrWhiteSpace(localized) ? localized!.Trim() : HumanizeName(rawName);
        }

        /// <summary>
        /// Resolve the localized subject string off an interactable's raycast hint: read its <c>_raycastTarget</c>
        /// (a <c>RaycastTargetHint</c>), then that hint's <c>_subjectLocalizationKey</c> (a <c>LocalizedString</c>), and
        /// invoke <c>GetLocalizedString()</c>. Null if any link is missing or the resolver didn't bind — callers fall
        /// back to the humanized name. Mirrors GameStateAccess's per-day holiday resolution.
        /// </summary>
        private string? LocalizedSubject(IntPtr obj, IntPtr ownerClass)
        {
            if (obj == IntPtr.Zero || ownerClass == IntPtr.Zero || _raycastHintClass == IntPtr.Zero) return null;
            try
            {
                IntPtr hint = Il2CppRaw.ReadObjectField(obj, ownerClass, "_raycastTarget");
                if (hint == IntPtr.Zero) return null;
                return Il2CppRaw.ResolveLocalizedStringField(hint, _raycastHintClass, "_subjectLocalizationKey");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] LocalizedSubject threw: {e.Message}");
                return null;
            }
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _viewProviderClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.ActionableObjects", "ActionableObjectsViewProvider");
                if (_viewProviderClass != IntPtr.Zero)
                    _getViews = Il2CppRaw.GetMethod(_viewProviderClass, "get_ActionableObjectViews", 0);

                _viewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure.ActionableObjects", "AActionableObjectView");
                if (_viewClass != IntPtr.Zero)
                {
                    _getCanShowHint = Il2CppRaw.GetMethod(_viewClass, "get_CanShowHint", 0);
                    _act = Il2CppRaw.GetMethod(_viewClass, "Act", 0); // private; cold-invoked ONLY for DoorTrigger
                }

                // DoorTrigger is in the GLOBAL namespace (no namespace block in the decompile). All views now activate
                // uniformly through the targeted-entry path (GoToSelected), so this is kept only for type-identification
                // (telling a door from a blind/curtain in diagnostics), not for an auto-activate allowlist.
                _doorTriggerClass = Il2CppRaw.GetClass(GameAsm, "", "DoorTrigger");

                // The second interactable system's abstract base (radio/phone/cat/mushroom/hatch/…), enumerated via
                // FindObjectsByType. We FILTER the list by availability (HardConditions/SoftConditions + _isEnabled) so
                // objects that aren't supposed to be reachable yet never appear (user, 2026-06-04). _isEnabled is the
                // game's own enable flag; HardConditions/SoftConditions are the per-object availability gates.
                _interactableClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure", "AInteractableObject");
                if (_interactableClass != IntPtr.Zero)
                {
                    _interactMethod = Il2CppRaw.GetMethod(_interactableClass, "Interact", 0);
                    _getHardConditions = Il2CppRaw.GetMethod(_interactableClass, "get_HardConditions", 0);
                    _getSoftConditions = Il2CppRaw.GetMethod(_interactableClass, "get_SoftConditions", 0);
                }

                _interactablesProviderClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure", "InteractablesViewProvider");

                // Cat availability gate: the cat roams by time of day, and at NIGHT it isn't interactable (Enter no-ops),
                // but the live CatInstance STAYS in the scene at night (it just moves to a night position) — so the old
                // "CatInstance present?" gate let the cat show at night. The authoritative signal is the game's own
                // CatController.IsCatActive (false at night). Its SETTER is private, but the GETTER is public, so we
                // resolve ICatController via Zenject and invoke get_IsCatActive on the concrete instance.
                _catInteractableClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure", "CatInteractable");
                _dayNight = ZenjectResolver.Resolve("_Code.Infrastructure.DayNight", "IDayNightController");
                if (_dayNight != IntPtr.Zero)
                    _getTimeOfDay = Il2CppRaw.GetMethod(IL2CPP.il2cpp_object_get_class(_dayNight), "get_CurrentTimeOfDay", 0);

                // Player service for walking to the cat (the cat has no standing-pos auto-walk, so the mod walks it).
                _playerService = ZenjectResolver.Resolve("_Code.Infrastructure.Player", "IPlayerService");
                if (_playerService != IntPtr.Zero)
                {
                    IntPtr psClass = IL2CPP.il2cpp_object_get_class(_playerService);
                    _psMoveXZ = Il2CppRaw.GetMethod(psClass, "MoveXZ", 2);
                    _psGetPosition = Il2CppRaw.GetMethod(psClass, "get_Position", 0);
                }

                _raycastTargetBaseClass = Il2CppRaw.GetClass(GameAsm, "_Scripts.Raycast", "ARaycastTarget");
                if (_raycastTargetBaseClass != IntPtr.Zero)
                {
                    _getRaycastIsLocked = Il2CppRaw.GetMethod(_raycastTargetBaseClass, "get_IsLocked", 0);
                    // set_IsTargeted lives on the ABSTRACT ARaycastTarget base (the IsTargeted property is declared
                    // there; both RaycastTargetHint (interactables) and the view raycast targets inherit it). Resolving
                    // on the base is fine because il2cpp dispatches the property setter virtually on the instance.
                    _setIsTargeted = Il2CppRaw.GetMethod(_raycastTargetBaseClass, "set_IsTargeted", 1);
                }

                // Localized-label resolution (see field comment): RaycastTargetHint holds the subject LocalizedString,
                // resolved via the shared Il2CppRaw.ResolveLocalizedString.
                _raycastHintClass = Il2CppRaw.GetClass(GameAsm, "_Code.Raycast", "RaycastTargetHint");

                // GAME-FAITHFUL ENTRY: the game opens EVERY interaction (doors, blinds, curtains, peephole, AND the
                // radio/phone/cat interactables) by focusing the object's raycast target (IsTargeted=true) and then
                // running its action through the targeted path — see AActionableObjectView and AInteractableObject, both
                // of which hold a raycast target + InputHandling + an OnUpdate(Action) pump. The mod's old cold-Act()
                // shortcut bypassed IsTargeted, so the game never registered the open view as the CURRENT interaction and
                // its own back-out ('q') had nothing to close (the blind softlock). We now set IsTargeted first, then
                // invoke the action — so the game owns the interaction and native back-out works. _setIsTargeted is the
                // key that makes this work; it is now resolved (above) rather than left dead.

                MelonLogger.Msg($"[ActionMenu] resolved: provider={_viewProviderClass != IntPtr.Zero} " +
                                $"getViews={_getViews != IntPtr.Zero} act={_act != IntPtr.Zero} doorTrigger={_doorTriggerClass != IntPtr.Zero} " +
                                $"interactableBase={_interactableClass != IntPtr.Zero} interactablesProvider={_interactablesProviderClass != IntPtr.Zero} " +
                                $"raycastBase={_raycastTargetBaseClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
