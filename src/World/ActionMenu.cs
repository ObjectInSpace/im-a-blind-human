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
        private IntPtr _raycastTargetBaseClass, _getRaycastIsLocked;
        // Aiming plumbing: the game's real entry HEAD — focus the view's raycast target + turn/zoom the player to it,
        // so the player can then press the game's own Space (Interact). We never invoke the interaction ourselves.
        private IntPtr _raycastTargetClass, _setIsTargeted;  // ARaycastTarget + set_IsTargeted(bool)
        private IntPtr _moveXZ, _getPosition;  // PlayerService methods — resolved lazily on the concrete class
        private IntPtr _setMouseEnabled;  // IPlayerService.set_IsMouseEnabled — freeze (false) / restore (true) the look
        private IntPtr _playerService;                              // Zenject-resolved IPlayerService (cached) — the live instance
        private IntPtr _playerControllerClass;                      // _Code.Player.PlayerController (holds cameraTarget + RealCamera)

        // Current selection, keyed by the view pointer so it survives list rebuilds (positions/availability shift).
        private IntPtr _selected;
        // Whether the current selection is an AInteractableObject (radio/phone/cat/…) vs a door/window view — they
        // engage differently (see Entry.IsInteractable), so GoToSelected/Aim branch on this.
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

        // Arrival tracking: after GoToSelected, we poll the player's distance to this standing pos each Tick and, when
        // in range, fire the arrival cue (the game's re-shown prompt, or a fallback). Zero = not currently walking.
        private readonly HudNarrator _hud;
        private IntPtr _walkTarget;          // the view we're walking toward (zero = idle)
        private string _walkTargetName = string.Empty;
        private Vector3 _walkStandingPos;
        private float _armedAt;              // Time.time when we reset the hint dedupe on arrival (for the fallback timer)
        private bool _arrivedAnnounced;      // arrival cue already handled for this walk
        private bool _armedSpoke;            // arrival "press space" cue spoken once for this hold
        private bool _lookFrozen;            // we set IsMouseEnabled=false to lock the camera on target; release restores it
        private float _goAt;                  // Time.time when the walk started (min-walk gate before arrival can trigger)
        private float _bestDist;              // closest we've gotten to the standing pos this go (stall detection)
        private int _stallFrames;             // consecutive converge frames without getting closer
        private const float MinWalkS = 0.15f; // arrival can't trigger sooner than this after a go (a near-instant first
                                              // frame is the player still at the PREVIOUS spot, not the new one)
        private const float ArriveDeadOnM = 0.20f;  // freeze only when THIS close to the standing pos — a single MoveXZ
                                                    // undershoots (~0.36m) and isn't precise enough to interact; we
                                                    // re-issue until dead-on (~0.09m worked).
        private const int ConvergeStallFrames = 30; // if the body stops getting closer for this many frames, accept the
                                                    // current pos (can't get nearer) rather than hang. Raised from 12:
                                                    // now we let the tween play out instead of re-issuing every frame, so
                                                    // a real ~2-3m walk needs more frames before it's truly stalled.
        private const int ReissueEveryStallFrames = 6; // while stalled (not yet given up), re-issue MoveXZ only every Nth
                                                       // frame, so the tween isn't restarted every frame (never finishes).
        private const float MaxHoldS = 5f;         // sanity fallback: auto-release the look-freeze after this long so a
                                                   // player can never be stuck with a locked camera if a key-release is missed.

        public ActionMenu(ISpeechOutput speech, HudNarrator hud) { _speech = speech; _hud = hud; }

        /// <summary>
        /// Step the selection by one (PageUp/PageDown), announce it, AND aim the player at it so the player can then
        /// press the game's own Space to interact. Selecting and aiming are one action — "PageUp/Down do everything
        /// Space doesn't" (user). We never interact ourselves.
        /// </summary>
        /// <remarks>
        /// SELECT + ANNOUNCE ONLY — no movement. Walking is on a separate deliberate key (<see cref="GoToSelected"/>,
        /// Backspace), NOT on cycling. Firing the walk on every cycle stacked MoveXZ calls before the prior one
        /// finished, leaving <c>_isAwaitingMoveToStandingPos</c> stuck true = player frozen (the race seen 2026-06-03;
        /// log showed awaitingMove=True on every line). Separating browse from go removes that race by construction:
        /// you can't start a second walk just by scrolling.
        /// </remarks>
        public void Cycle(bool backwards)
        {
            try
            {
                EnsureResolved();
                if (_lookFrozen) ReleaseHold(); // cycling = the player wants something else → release the held aim
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
        /// Activate (Enter) the SELECTED interactable through the GAME'S OWN targeted-entry path — the path the game uses
        /// for EVERYTHING (doors, blinds, curtains, peephole, AND the radio/phone/cat interactables). Both interaction
        /// systems (<c>AActionableObjectView</c> and <c>AInteractableObject</c>) carry a raycast target + an update pump
        /// and open only while their target is FOCUSED (<c>IsTargeted</c>). So we (1) FOCUS the object's raycast target
        /// — single-focus, clearing the others — exactly as the game's own ray would when you look at it, then (2) aim
        /// the camera at it so the game's state agrees, then (3) invoke its action (<c>Act()</c> for a view,
        /// <c>Interact()</c> for an interactable). Because the open now happens WHILE the object is the registered
        /// current target, the game owns the interaction and its OWN back-out ('q') can close it — which the previous
        /// cold-<c>Act()</c> (no IsTargeted) could not, the blind softlock. The mod still drives it all (no WASD/Space).
        /// Safe to call when nothing's selected (announces).
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
                if (_lookFrozen) ReleaseHold(); // clear any stale hold/targets from the previous model's path

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

                // (1) FOCUS the object's raycast target — IsTargeted=true on it, false on all others. This is the gate the
                // game's action reads; setting it ourselves makes the upcoming Act()/Interact() register as the CURRENT
                // targeted interaction (so the game's own back-out can later close it). ownerClass differs per system.
                IntPtr ownerClass = _selectedIsInteractable ? _interactableClass : _viewClass;
                FocusTarget(_selected, ownerClass);
                _focusedView = _selected; // remember what WE forced focus on, so Tick can clear it after a native leave

                // (2) AIM the camera at it so the game's own raycaster/state agrees with the focus we forced (keeps the
                // target from flickering off the next frame). Best-effort; the forced IsTargeted above is the real gate.
                AimCameraAt(_selected);

                // (3) INVOKE the action while targeted.
                //  • INTERACTABLES (radio/phone/cat/…): Interact() is `public abstract` on AInteractableObject, so calling
                //    it on the BASE throws EntryPointNotFound ("abstract method"). Bind to the CONCRETE override via the
                //    runtime class (same pattern as ResolveMoveXZ) — this was the old "Interact threw", not a softlock.
                //  • VIEWS (doors/blinds/curtains/peephole): the private Act() toggle resolved on the view base.
                if (_selectedIsInteractable)
                {
                    IntPtr concrete = IL2CPP.il2cpp_object_get_class(_selected);
                    IntPtr interact = concrete != IntPtr.Zero ? Il2CppRaw.GetMethod(concrete, "Interact", 0) : IntPtr.Zero;
                    if (interact == IntPtr.Zero)
                    {
                        MelonLogger.Warning($"[ActionMenu] '{name}': couldn't resolve concrete Interact().");
                        _speech.Speak($"Can't use {name}.", interrupt: true);
                        ClearAllTargets(IntPtr.Zero); // unfocus — we're not entering after all
                        _focusedView = IntPtr.Zero;
                        return;
                    }
                    _speech.Speak($"Using {name}.", interrupt: true);
                    bool ran = Il2CppRaw.TryInvokeVoid(_selected, interact, out string? err);
                    if (ran) MelonLogger.Msg($"[ActionMenu] activated interactable '{name}' via concrete Interact() (targeted).");
                    else MelonLogger.Warning($"[ActionMenu] interactable '{name}' Interact() threw:\n{err}");
                    return;
                }

                if (_act != IntPtr.Zero)
                {
                    _speech.Speak($"Using {name}.", interrupt: true);
                    bool ok = Il2CppRaw.InvokeVoid(_selected, _act);
                    MelonLogger.Msg($"[ActionMenu] activated view '{name}' via Act() (targeted, threw={!ok}).");
                    return;
                }

                // No Act() resolved (shouldn't happen for a view) — leave it focused + aimed and hand off to the game's
                // own Space as a last resort.
                _speech.Speak($"Facing {name}. Press space to use it.", interrupt: true);
                MelonLogger.Msg($"[ActionMenu] aim-only '{name}' — no Act() resolved.");
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
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Leave threw: {e.Message}");
            }
        }

        /// <summary>
        /// Start a "go": aim the camera at <paramref name="view"/> (so the game's raycaster will focus it — see
        /// <see cref="AimCameraAt"/>) and walk the player toward its standing position (<c>MoveXZ</c>). We do NOT set
        /// IsTargeted ourselves (that opens stateful objects like blinds — we let the game's own ray do it). The
        /// per-frame convergence + look-freeze that finish the approach are in <see cref="Tick"/>. Never throws.
        /// </summary>
        /// <returns>The standing-position world point we're walking toward (zero on failure), for arrival polling.</returns>
        private Vector3 Aim(IntPtr view, string name)
        {
            if (view == IntPtr.Zero) return Vector3.zero;
            try
            {
                IntPtr playerSvc = ResolvePlayerService();
                if (playerSvc == IntPtr.Zero) playerSvc = Il2CppRaw.ReadObjectField(view, _viewClass, "PlayerService");

                // AIM the camera at the object so the game's raycaster focuses it (sets IsTargeted) — the piece that
                // makes the game's Space interact.
                AimCameraAt(view);

                // WALK to the standing position (MoveXZ) so the player is IN RANGE — Space needs targeted AND in-range.
                // The 2nd arg is a DOTween DURATION in seconds (impl param 'duration'), not a speed.
                IntPtr standingPos = Il2CppRaw.ReadObjectField(view, _viewClass, "_standingPos");
                float moveDuration = Il2CppRaw.ReadFloatField(view, _viewClass, "_moveToStandingPosSpeed");
                Vector3 standWorld = standingPos != IntPtr.Zero ? Il2CppRaw.GetComponentWorldPosition(standingPos) : Vector3.zero;
                IntPtr moveMethod = ResolveMoveXZ(playerSvc);
                if (playerSvc != IntPtr.Zero && standingPos != IntPtr.Zero && moveMethod != IntPtr.Zero)
                    Il2CppRaw.InvokeWithVector3Float(playerSvc, moveMethod, standWorld, moveDuration > 0f ? moveDuration : 0.3f);

                // Log the player's REAL transform position vs the standing pos, so the walk start distance is honest
                // (the service Position would read ~0 here once MoveXZ snaps it — see TryGetPlayerWorldPos).
                TryGetPlayerWorldPos(out Vector3 playerPos0);
                float startDist = Flat(playerPos0 - standWorld);
                MelonLogger.Msg($"[ActionMenu] going to '{name}': player=({playerPos0.x:F1},{playerPos0.y:F1},{playerPos0.z:F1}) " +
                                $"standingPos=({standWorld.x:F1},{standWorld.y:F1},{standWorld.z:F1}) startDist={startDist:F2}m " +
                                $"dur={moveDuration:F2}.");
                return standWorld;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Aim threw: {e.Message}");
                return Vector3.zero;
            }
        }

        /// <summary>
        /// Distance + side of the selected object from the player, as a short spoken cue ("a few steps away on your
        /// left") so a blind player can walk toward an aim-only interactable. Empty string if the player position can't
        /// be read. XZ-plane only; side is world-relative (good enough for "which way" in a sparse room). Never throws.
        /// </summary>
        private string BearingTo(IntPtr obj)
        {
            try
            {
                IntPtr playerSvc = ResolvePlayerService();
                IntPtr getPos = ResolveGetPosition(playerSvc);
                if (playerSvc == IntPtr.Zero || getPos == IntPtr.Zero) return string.Empty;
                Vector3 player = Il2CppRaw.InvokeVector3Getter(playerSvc, getPos);
                Vector3 obP = Il2CppRaw.GetComponentWorldPosition(obj);
                Vector3 to = obP - player; to.y = 0f;
                float d = to.magnitude;
                string range = d < 1.5f ? "right next to you" : d < 4f ? "a few steps away" : "across the room";
                return range;
            }
            catch { return string.Empty; }
        }

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
        /// Drive the walk to convergence after a <see cref="GoToSelected"/>: re-issue MoveXZ each frame until the player
        /// is dead-on (≤<see cref="ArriveDeadOnM"/>) at the standing pos, then FREEZE the look on the object so the
        /// game keeps it focused and Space interacts. Holds (re-aiming) until released. Driven each frame by AccessMod.
        /// Never throws.
        /// </summary>
        public void Tick()
        {
            // RECONCILE OUR FORCED FOCUS after a native leave. GoToSelected sets IsTargeted on the view out of band; the
            // game's own 'q' closes the view but doesn't clear our flag, so it lingers and a later open can land on a
            // stale target (the intermittent "only once" wedge). Once the open has actually ENGAGED (we saw IsInWindow /
            // engaged go true — _focusSeenEngaged), watch for it dropping back to "nothing engaged" = the player left;
            // then clear our targets and reset. Gating on _focusSeenEngaged avoids clearing during the open ramp (the
            // flag flips true a frame or two after we focus). Cheap manager-flag reads; all fail-open. Never throws here.
            if (_focusedView != IntPtr.Zero)
            {
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

            if (_walkTarget == IntPtr.Zero) return; // not walking
            try
            {
                IntPtr playerSvc = ResolvePlayerService();
                if (playerSvc == IntPtr.Zero) return;
                // Use the REAL transform position, NOT IPlayerService.Position — the latter snaps to the MoveXZ target
                // immediately, so it reads dist≈0 before the body has moved (the no-walk bug measured 2026-06-04). The
                // transform reflects the body's actual position each frame, so convergence tracks a real walk.
                if (!TryGetPlayerWorldPos(out Vector3 playerPos)) return;
                float dist = Flat(playerPos - _walkStandingPos);

                if (!_arrivedAnnounced)
                {
                    // CONVERGE PHASE — a single MoveXZ undershoots (first go left the player ~0.36m from the spot, not
                    // precise enough for the game to interact; a second go refined to 0.09m and worked). So we re-issue
                    // MoveXZ toward the standing pos each frame until we're DEAD-ON (≤ArriveDeadOnM), then freeze. A
                    // min-walk gate avoids treating the pre-move frame as arrival; a stall guard avoids hanging if the
                    // player physically can't get closer (re-issue stopped improving), falling back to the 5s release.
                    bool elapsed = Time.time - _goAt >= MinWalkS;

                    if (elapsed && dist <= ArriveDeadOnM)
                    {
                        // Dead-on. The game's own ray doesn't reliably focus the object from our forced turn (measured:
                        // Space only fires when _raycastTarget.IsTargeted==True, and the LookAt re-aim left it flickering),
                        // so we SET IsTargeted directly on the arrived object (clearing the others for single-focus) — for
                        // doors this is exactly the desired action gate, and FocusTarget re-asserts it during the hold.
                        // Still freeze the look so the camera doesn't drift off and the game doesn't clear it next frame.
                        _arrivedAnnounced = true;
                        _armedAt = Time.time;
                        _hud?.ArmArrival();
                        ResolveMouseEnabled(playerSvc);
                        if (_setMouseEnabled != IntPtr.Zero) { Il2CppRaw.InvokeVoidWithBool(playerSvc, _setMouseEnabled, false); _lookFrozen = true; }
                        FocusTarget(_walkTarget);
                        MelonLogger.Msg($"[ActionMenu] arrived dead-on at '{_walkTargetName}' (dist={dist:F2}m). Set IsTargeted; holding. Press space.");
                        return;
                    }

                    // Not dead-on yet. The initial MoveXZ was issued once in Aim; let its tween PLAY OUT (re-issuing every
                    // frame would restart the tween from the current spot each frame and it could never finish). Only
                    // re-issue when progress has STALLED — the body stopped getting closer for a few frames (tween done
                    // but undershot, or blocked). Track best-dist against the REAL transform pos to detect that stall.
                    if (elapsed)
                    {
                        if (dist < _bestDist - 0.02f) { _bestDist = dist; _stallFrames = 0; }
                        else if (++_stallFrames % ReissueEveryStallFrames == 0 && _stallFrames < ConvergeStallFrames)
                        {
                            // Stalled but not given up: nudge once more toward the standing pos.
                            IntPtr moveMethod = ResolveMoveXZ(playerSvc);
                            float moveDuration = Il2CppRaw.ReadFloatField(_walkTarget, _viewClass, "_moveToStandingPosSpeed");
                            if (moveMethod != IntPtr.Zero)
                                Il2CppRaw.InvokeWithVector3Float(playerSvc, moveMethod, _walkStandingPos, moveDuration > 0f ? moveDuration : 0.3f);
                        }

                        if (_stallFrames >= ConvergeStallFrames)
                        {
                            // Can't get closer — accept current position and freeze anyway (better to offer the cue than
                            // hang; the 5s fallback also covers true stuck cases).
                            _arrivedAnnounced = true;
                            _armedAt = Time.time;
                            _hud?.ArmArrival();
                            ResolveMouseEnabled(playerSvc);
                            if (_setMouseEnabled != IntPtr.Zero) { Il2CppRaw.InvokeVoidWithBool(playerSvc, _setMouseEnabled, false); _lookFrozen = true; }
                            MelonLogger.Msg($"[ActionMenu] converge stalled at '{_walkTargetName}' (dist={dist:F2}m, best={_bestDist:F2}m). Froze look; holding aim. Press space.");
                        }
                    }
                    return;
                }

                // SANITY FALLBACK: never stay frozen forever. If the keypress-release somehow misses, auto-release after
                // MaxHoldS so the player can always look/move again.
                if (Time.time - _armedAt >= MaxHoldS)
                {
                    MelonLogger.Msg($"[ActionMenu] hold auto-released after {MaxHoldS:F0}s (sanity fallback).");
                    ReleaseHold();
                    return;
                }

                // FROZEN HOLD: the focus was set ONCE on arrival (FocusTarget) and the look is frozen
                // (IsMouseEnabled=false), so IsTargeted stays put — we do NOT re-assert it every frame. Re-thrashing it
                // (ClearAllTargets→set) toggled the game's prompt off/on each frame, which re-fired "open X" endlessly
                // and buried the speech (the user's "it kept repeating" report). Passive hold instead. Speak the cue once.
                if (!_armedSpoke)
                {
                    _armedSpoke = true;
                    if (_hud == null || !_hud.SpokeSinceArm)
                        _speech.Speak($"At {_walkTargetName}. Press space.", interrupt: true);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Tick threw: {e.Message}");
                EndWalk();
            }
        }

        /// <summary>
        /// Release the look-freeze the hold put in place: re-enable the mouse so the player can look around again, and
        /// clear the walk/hold state. Called when the player does ANYTHING else after a go (presses a key / cycles), so
        /// they're never stuck with a frozen camera. Safe to call when not frozen (no-op). Public so AccessMod can fire
        /// it on the next keypress.
        /// </summary>
        public void ReleaseHold()
        {
            try
            {
                if (_lookFrozen)
                {
                    IntPtr playerSvc = ResolvePlayerService();
                    if (playerSvc != IntPtr.Zero && _setMouseEnabled != IntPtr.Zero)
                        Il2CppRaw.InvokeVoidWithBool(playerSvc, _setMouseEnabled, true);
                    _lookFrozen = false;
                    // Clear the IsTargeted we set on arrival, so a stale door isn't left "targeted" (the game's Space
                    // would otherwise still act on it after the player moved on) and the prompt path resets cleanly.
                    if (_walkTarget != IntPtr.Zero) ClearAllTargets(IntPtr.Zero);
                    MelonLogger.Msg("[ActionMenu] released look-freeze (mouse re-enabled, targets cleared).");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] ReleaseHold threw: {e.Message}");
            }
            EndWalk();
        }

        /// <summary>Whether a look-freeze hold is currently active (the camera is locked on a target awaiting Space).</summary>
        public bool IsHolding => _lookFrozen;

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

        private void EndWalk()
        {
            _walkTarget = IntPtr.Zero;
            _walkTargetName = string.Empty;
            _arrivedAnnounced = false;
            _armedSpoke = false;
        }

        /// <summary>
        /// Clear <c>IsTargeted</c> on every interactable's raycast target except <paramref name="except"/>'s, enforcing
        /// the single-focus invariant so the game's Interact (Space) acts on exactly one object. Iterates the provider's
        /// view array; reads each view's <c>_raycastTarget</c> and sets it false. Never throws.
        /// </summary>
        private void ClearAllTargets(IntPtr except)
        {
            if (_setIsTargeted == IntPtr.Zero) return;
            try
            {
                IntPtr provider = Il2CppRaw.FindObjectIncludingInactive(_viewProviderClass);
                if (provider == IntPtr.Zero) return;
                IntPtr[] views = Il2CppRaw.ReadObjectArray(Il2CppRaw.InvokeObjectGetter(provider, _getViews));
                foreach (IntPtr v in views)
                {
                    if (v == IntPtr.Zero || v == except) continue;
                    IntPtr rt = Il2CppRaw.ReadObjectField(v, _viewClass, "_raycastTarget");
                    if (rt != IntPtr.Zero) Il2CppRaw.InvokeVoidWithBool(rt, _setIsTargeted, false);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] ClearAllTargets threw: {e.Message}");
            }
        }

        /// <summary>
        /// The player's REAL world position, read from the <c>PlayerController</c> MonoBehaviour's transform. This is
        /// ground truth — unlike <c>IPlayerService.Position</c>, which (measured 2026-06-04) returns the player's true
        /// position at rest but SNAPS to the MoveXZ target the instant a walk is issued, so an arrival check against it
        /// reads dist≈0 immediately and the convergence loop "arrives" before the body has moved (the no-walk bug). The
        /// transform reflects where the body actually is each frame, so re-issuing MoveXZ until THIS reaches the target
        /// drives a real walk. Returns false if the controller can't be found this frame.
        /// </summary>
        private bool TryGetPlayerWorldPos(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (_playerControllerClass == IntPtr.Zero)
                _playerControllerClass = Il2CppRaw.GetClass(GameAsm, "_Code.Player", "PlayerController");
            if (_playerControllerClass == IntPtr.Zero) return false;
            IntPtr pc = Il2CppRaw.FindObjectIncludingInactive(_playerControllerClass);
            if (pc == IntPtr.Zero) return false;
            pos = Il2CppRaw.GetComponentWorldPosition(pc);
            return true;
        }

        /// <summary>The live <c>IPlayerService</c> from the Zenject container (the proven path used elsewhere in the
        /// mod), cached. The instance read off a view's <c>PlayerService</c> field returned (0,0,0) for LookDirection
        /// and didn't turn on LookAtWithZoom — apparently not the live one — so we resolve the container's instead.</summary>
        private IntPtr ResolvePlayerService()
        {
            if (_playerService != IntPtr.Zero) return _playerService;
            _playerService = ZenjectResolver.Resolve("_Code.Infrastructure.Player", "IPlayerService");
            return _playerService;
        }

        /// <summary>Resolve <c>MoveXZ(Vector3,float)</c> on the player service's CONCRETE class (interface method
        /// pointers don't invoke on the instance). Cached. Walks the player to a standing position. Zero on miss.</summary>
        private IntPtr ResolveMoveXZ(IntPtr playerSvc)
        {
            if (_moveXZ != IntPtr.Zero) return _moveXZ;
            if (playerSvc == IntPtr.Zero) return IntPtr.Zero;
            IntPtr psClass = IL2CPP.il2cpp_object_get_class(playerSvc);
            if (psClass == IntPtr.Zero) return IntPtr.Zero;
            _moveXZ = Il2CppRaw.GetMethod(psClass, "MoveXZ", 2);
            return _moveXZ;
        }

        /// <summary>Resolve IsMouseEnabled get+set on the player service's concrete class. Cached. The game disables
        /// the mouse (look + interaction raycast) during scripted MoveXZ walks; calling MoveXZ ourselves may leave it
        /// disabled, which would suppress the raycaster → no focus → no interact. We read + re-enable it on arrival.</summary>
        private void ResolveMouseEnabled(IntPtr playerSvc)
        {
            if (_setMouseEnabled != IntPtr.Zero || playerSvc == IntPtr.Zero) return;
            IntPtr psClass = IL2CPP.il2cpp_object_get_class(playerSvc);
            if (psClass == IntPtr.Zero) return;
            _setMouseEnabled = Il2CppRaw.GetMethod(psClass, "set_IsMouseEnabled", 1);
        }

        /// <summary>Resolve <c>get_Position</c> on the player service's concrete class. Cached. Lets us read where the
        /// player actually is (to see whether a MoveXZ walk arrived). Zero on miss.</summary>
        private IntPtr ResolveGetPosition(IntPtr playerSvc)
        {
            if (_getPosition != IntPtr.Zero) return _getPosition;
            if (playerSvc == IntPtr.Zero) return IntPtr.Zero;
            IntPtr psClass = IL2CPP.il2cpp_object_get_class(playerSvc);
            if (psClass == IntPtr.Zero) return IntPtr.Zero;
            _getPosition = Il2CppRaw.GetMethod(psClass, "get_Position", 0);
            return _getPosition;
        }

        private readonly struct Entry
        {
            public readonly IntPtr View;
            public readonly string Name;
            public readonly string Bearing;
            // True for the AInteractableObject system (radio/phone/cat/…), which has NO _standingPos/_lookAtPos/door
            // raycast plumbing — so GoToSelected aims at the object's own transform and lets the player walk + Space,
            // instead of running the door walk-to-standing-pos path. False = a door/window AActionableObjectView.
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

                    string name = HumanizeName(Il2CppRaw.GetUnityObjectName(o));
                    if (!IsInteractableValidNow(o, name))
                    {
                        gated++;
                        continue;
                    }

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
            MelonLogger.Msg($"[ActionMenu]   avail '{name}': active={active} _isEnabled={isEnabled} " +
                            $"_lockCount={lockCount} raycast={raycast != IntPtr.Zero} raycastLocked={raycastLocked} " +
                            $"hard={hard} soft={soft} valid={valid}.");
            return valid;
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

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

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
                _raycastTargetBaseClass = Il2CppRaw.GetClass(GameAsm, "_Scripts.Raycast", "ARaycastTarget");
                if (_raycastTargetBaseClass != IntPtr.Zero)
                {
                    _getRaycastIsLocked = Il2CppRaw.GetMethod(_raycastTargetBaseClass, "get_IsLocked", 0);
                    // set_IsTargeted lives on the ABSTRACT ARaycastTarget base (the IsTargeted property is declared
                    // there; both RaycastTargetHint (interactables) and the view raycast targets inherit it). Resolving
                    // on the base is fine because il2cpp dispatches the property setter virtually on the instance.
                    _setIsTargeted = Il2CppRaw.GetMethod(_raycastTargetBaseClass, "set_IsTargeted", 1);
                }

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
