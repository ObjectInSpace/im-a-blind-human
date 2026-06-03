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
        // Aiming plumbing: the game's real entry HEAD — focus the view's raycast target + turn/zoom the player to it,
        // so the player can then press the game's own Space (Interact). We never invoke the interaction ourselves.
        private IntPtr _raycastTargetClass, _setIsTargeted;  // ARaycastTarget + set_IsTargeted(bool)
        private IntPtr _moveXZ, _getPosition;  // PlayerService methods — resolved lazily on the concrete class
        private IntPtr _setMouseEnabled;  // IPlayerService.set_IsMouseEnabled — freeze (false) / restore (true) the look
        private IntPtr _playerService;                              // Zenject-resolved IPlayerService (cached) — the live instance
        private IntPtr _playerControllerClass;                      // _Code.Player.PlayerController (holds cameraTarget + RealCamera)

        // Current selection, keyed by the view pointer so it survives list rebuilds (positions/availability shift).
        private IntPtr _selected;

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
        private const int ConvergeStallFrames = 12; // if re-issuing MoveXZ stops getting closer for this many frames,
                                                    // accept current pos (can't get nearer) rather than hang.
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
                _speech.Speak($"{sel.Name}, {idx + 1} of {entries.Count}.", interrupt: true);
                MelonLogger.Msg($"[ActionMenu] selected {idx + 1}/{entries.Count}: '{sel.Name}' ({sel.Bearing}). Press the go key to walk there.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Cycle threw: {e.Message}");
            }
        }

        /// <summary>
        /// Deliberate "go" (Backspace): turn + walk the player to the SELECTED interactable's standing position, so the
        /// game's own Space (Interact) can then engage it in range. One walk per press — never fired by cycling — so
        /// MoveXZ can't stack (the frozen-player race). No <c>IsTargeted</c> poke (that opens blinds). Safe to call when
        /// nothing's selected (announces) or already walking.
        /// </summary>
        public void GoToSelected()
        {
            try
            {
                EnsureResolved();
                if (_selected == IntPtr.Zero)
                {
                    _speech.Speak("Nothing selected. Use the page keys to choose something first.", interrupt: true);
                    return;
                }
                // If a previous go left the look frozen, release it before starting a new walk (re-enable the mouse so
                // the new MoveXZ/aim isn't fighting a stale freeze).
                if (_lookFrozen) ReleaseHold();

                string name = HumanizeName(Il2CppRaw.GetUnityObjectName(_selected));
                _speech.Speak($"Going to {name}.", interrupt: true);
                Vector3 standWorld = Aim(_selected, name);

                // Begin arrival polling: Tick() watches the player close on this standing pos and fires the arrival
                // cue when in range, so the player knows WHEN to press Space (the walk lags the keypress ~1-2s).
                _walkTarget = _selected;
                _walkTargetName = name;
                _walkStandingPos = standWorld;
                _arrivedAnnounced = false;
                _armedSpoke = false;
                _goAt = Time.time;
                _bestDist = float.MaxValue;
                _stallFrames = 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] GoToSelected threw: {e.Message}");
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

                MelonLogger.Msg($"[ActionMenu] going to '{name}': standingPos=({standWorld.x:F1},{standWorld.y:F1},{standWorld.z:F1}) dur={moveDuration:F2}.");
                return standWorld;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Aim threw: {e.Message}");
                return Vector3.zero;
            }
        }

        /// <summary>
        /// Aim the player's CAMERA at <paramref name="view"/> by rotating both <c>PlayerController.cameraTarget</c> and
        /// <c>RealCamera</c> to face the object's look point (<c>Transform.LookAt</c>). This makes the game's own
        /// raycaster (which casts along the camera forward) focus the object and set its
        /// <c>_raycastTarget.IsTargeted</c> — the gate for the game's Space/Interact. We do NOT set IsTargeted ourselves
        /// (that opens blinds); the game's ray does it safely. Returns true if the LookAt ran. Never throws.
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
            if (_walkTarget == IntPtr.Zero) return; // not walking
            try
            {
                IntPtr playerSvc = ResolvePlayerService();
                if (playerSvc == IntPtr.Zero || ResolveGetPosition(playerSvc) == IntPtr.Zero) return;
                Vector3 playerPos = Il2CppRaw.InvokeVector3Getter(playerSvc, _getPosition);
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
                        // Dead-on. Focus is FLEETING (the look-Update clears IsTargeted next frame), so FREEZE the look
                        // (IsMouseEnabled=false) + re-aim every frame — the camera stays locked on, the game keeps
                        // IsTargeted set, and Space works whenever pressed.
                        _arrivedAnnounced = true;
                        _armedAt = Time.time;
                        _hud?.ArmArrival();
                        ResolveMouseEnabled(playerSvc);
                        if (_setMouseEnabled != IntPtr.Zero) { Il2CppRaw.InvokeVoidWithBool(playerSvc, _setMouseEnabled, false); _lookFrozen = true; }
                        MelonLogger.Msg($"[ActionMenu] arrived dead-on at '{_walkTargetName}' (dist={dist:F2}m). Froze look; holding aim. Press space.");
                        return;
                    }

                    // Not dead-on yet: nudge toward the standing pos again. Track best-dist to detect a stall.
                    if (elapsed)
                    {
                        IntPtr moveMethod = ResolveMoveXZ(playerSvc);
                        float moveDuration = Il2CppRaw.ReadFloatField(_walkTarget, _viewClass, "_moveToStandingPosSpeed");
                        if (moveMethod != IntPtr.Zero)
                            Il2CppRaw.InvokeWithVector3Float(playerSvc, moveMethod, _walkStandingPos, moveDuration > 0f ? moveDuration : 0.3f);

                        if (dist < _bestDist - 0.02f) { _bestDist = dist; _stallFrames = 0; }
                        else if (++_stallFrames >= ConvergeStallFrames)
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

                // FROZEN HOLD: keep the camera locked on the object so IsTargeted stays set for the game's Space. The
                // mouse is disabled, so re-aiming isn't fought by the look-Update. We keep this until the player cycles
                // or goes elsewhere (EndWalk re-enables the mouse). Announce the prompt cue once.
                AimCameraAt(_walkTarget);
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
                    MelonLogger.Msg("[ActionMenu] released look-freeze (mouse re-enabled).");
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
            public Entry(IntPtr view, string name, string bearing) { View = view; Name = name; Bearing = bearing; }
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
            if (views.Length == 0) return entries;

            Vector3 camPos = Il2CppRaw.GetMainCameraPosition();
            int i = 0;
            foreach (IntPtr v in views)
            {
                if (v == IntPtr.Zero) { MelonLogger.Msg($"[ActionMenu]   [{i++}] <null>"); continue; }
                string name = HumanizeName(Il2CppRaw.GetUnityObjectName(v));
                Vector3 pos = Il2CppRaw.GetComponentWorldPosition(v);
                i++;
                entries.Add(new Entry(v, name, Bearing(camPos, pos)));
            }
            return entries;
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
                    _getCanShowHint = Il2CppRaw.GetMethod(_viewClass, "get_CanShowHint", 0);

                // Aiming: the raycast-target focus setter. LookAtWithZoom is resolved LAZILY on the player service's
                // concrete class (see ResolveLookAtWithZoom) — resolving it on the IPlayerService interface here gave a
                // non-invokable pointer (lookAtZoom no-op'd).
                _raycastTargetClass = Il2CppRaw.GetClass(GameAsm, "_Scripts.Raycast", "ARaycastTarget");
                if (_raycastTargetClass != IntPtr.Zero)
                    _setIsTargeted = Il2CppRaw.GetMethod(_raycastTargetClass, "set_IsTargeted", 1);

                MelonLogger.Msg($"[ActionMenu] resolved: provider={_viewProviderClass != IntPtr.Zero} " +
                                $"getViews={_getViews != IntPtr.Zero} canShowHint={_getCanShowHint != IntPtr.Zero} " +
                                $"| aim: raycastTarget={_raycastTargetClass != IntPtr.Zero} setIsTargeted={_setIsTargeted != IntPtr.Zero} " +
                                $"(lookAtWithZoom resolved lazily on concrete class)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
