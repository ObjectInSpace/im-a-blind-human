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
        private IntPtr _lookAtWithZoom;                      // PlayerService.LookAtWithZoom — resolved lazily on the concrete class

        // Current selection, keyed by the view pointer so it survives list rebuilds (positions/availability shift).
        private IntPtr _selected;

        public ActionMenu(ISpeechOutput speech) => _speech = speech;

        /// <summary>
        /// Step the selection by one (PageUp/PageDown), announce it, AND aim the player at it so the player can then
        /// press the game's own Space to interact. Selecting and aiming are one action — "PageUp/Down do everything
        /// Space doesn't" (user). We never interact ourselves.
        /// </summary>
        /// <param name="canAim">
        /// True only when the game is in world free-roam (<c>InputModeGate.IsWorldRoam</c>). Aiming pokes the object's
        /// <c>_raycastTarget.IsTargeted</c>, and for SOME interactables (e.g. the blinds) that flag doesn't just show a
        /// prompt — it OPENS the object's close-up. So if a close-up/overlay is already open (<c>_inUiCounter&gt;0</c>),
        /// re-aiming would stack a second interaction on top = softlock (seen 2026-06-03 by rapidly cycling blinds).
        /// When <paramref name="canAim"/> is false we still select + announce, but DO NOT aim — the player must leave
        /// the current close-up first. Selecting/announcing is always harmless.
        /// </param>
        public void Cycle(bool backwards, bool canAim)
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

                // Find where the current selection sits in the freshly-built list; step from there.
                int idx = MenuStepUtil.NextIndex(entries.FindIndex(e => e.View == _selected), entries.Count, backwards);

                Entry sel = entries[idx];
                _selected = sel.View;
                // Announce name + list position. The game's own interaction prompt (HUDPresenter.ShowHint → HudNarrator,
                // e.g. "view entrance") fires ~0.5s later when the look lands, landing AFTER ours — so the player hears
                // "peephole, 9 of 10" … then "view entrance".
                _speech.Speak($"{sel.Name}, {idx + 1} of {entries.Count}.", interrupt: true);
                MelonLogger.Msg($"[ActionMenu] selected {idx + 1}/{entries.Count}: '{sel.Name}' ({sel.Bearing}) canAim={canAim}.");

                // Only aim in world-roam — aiming while a close-up is open can stack interactions (softlock).
                if (canAim) Aim(sel.View, sel.Name);
                else MelonLogger.Msg("[ActionMenu] not aiming (an overlay/close-up is open); leave it first.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Cycle threw: {e.Message}");
            }
        }

        /// <summary>
        /// Turn the player to face <paramref name="view"/> using the game's own entry head, so the game's Interact
        /// (Space) then engages ONLY this object: mark its <c>_raycastTarget.IsTargeted=true</c> and call
        /// <c>PlayerService.LookAtWithZoom(_lookAtPos, _fov, dur)</c>. CRUCIALLY, every OTHER interactable's
        /// <c>IsTargeted</c> is cleared first — a real raycaster keeps exactly one target focused, so without this the
        /// IsTargeted flags accumulate across cycles and Space (Interact) fires on ALL of them at once (the bug seen
        /// 2026-06-03). Read-only on the view's own fields; never throws.
        /// </summary>
        private void Aim(IntPtr view, string name)
        {
            if (view == IntPtr.Zero) return;
            try
            {
                // Single-target invariant: clear IsTargeted on every interactable's raycast target, then set only this
                // one. Idempotent — also clears any stray focus the game's own raycaster left.
                ClearAllTargets(except: view);

                IntPtr raycastTarget = Il2CppRaw.ReadObjectField(view, _viewClass, "_raycastTarget");
                IntPtr lookAtPos = Il2CppRaw.ReadObjectField(view, _viewClass, "_lookAtPos");
                IntPtr playerSvc = Il2CppRaw.ReadObjectField(view, _viewClass, "PlayerService");
                float fov = Il2CppRaw.ReadFloatField(view, _viewClass, "_fov");

                bool focused = raycastTarget != IntPtr.Zero && _setIsTargeted != IntPtr.Zero
                    && Il2CppRaw.InvokeVoidWithBool(raycastTarget, _setIsTargeted, true);

                // Resolve LookAtWithZoom on the player service's CONCRETE class, not the interface — an interface
                // method pointer doesn't invoke on the instance (that's why lookAtZoom was False / no-op). Mirrors the
                // proven pattern in OrientationNarrator: get the instance's class, then the method. Cached once.
                IntPtr lookMethod = ResolveLookAtWithZoom(playerSvc);
                bool looked = playerSvc != IntPtr.Zero && lookAtPos != IntPtr.Zero && lookMethod != IntPtr.Zero
                    && Il2CppRaw.InvokeWithObjectFloatFloat(playerSvc, lookMethod, lookAtPos, fov > 0f ? fov : 40f, 0.5f);

                MelonLogger.Msg($"[ActionMenu] aimed at '{name}': focusedTarget={focused} lookAtZoom={looked} " +
                                $"(raycastTarget={raycastTarget != IntPtr.Zero} lookAtPos={lookAtPos != IntPtr.Zero} " +
                                $"playerSvc={playerSvc != IntPtr.Zero} lookMethod={lookMethod != IntPtr.Zero}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Aim threw: {e.Message}");
            }
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
        /// Resolve <c>LookAtWithZoom(Transform,float,float)</c> on the player service's CONCRETE runtime class (via
        /// <c>il2cpp_object_get_class</c> on the live instance), NOT the <c>IPlayerService</c> interface — an interface
        /// method pointer does not invoke on the instance. Cached after first success. Returns zero if the instance is
        /// null or the method isn't found.
        /// </summary>
        private IntPtr ResolveLookAtWithZoom(IntPtr playerSvc)
        {
            if (_lookAtWithZoom != IntPtr.Zero) return _lookAtWithZoom;
            if (playerSvc == IntPtr.Zero) return IntPtr.Zero;
            IntPtr psClass = IL2CPP.il2cpp_object_get_class(playerSvc);
            if (psClass == IntPtr.Zero) return IntPtr.Zero;
            _lookAtWithZoom = Il2CppRaw.GetMethod(psClass, "LookAtWithZoom", 3);
            return _lookAtWithZoom;
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
                bool canHint = Il2CppRaw.InvokeBoolGetter(v, _getCanShowHint);
                bool active = Il2CppRaw.GetComponentGameObjectActive(v);
                // Interaction state flags, logged so engage/leave behavior can be read from ground truth (which object
                // is currently looked-at / mid-animation) while we tune the aim model.
                bool looking = Il2CppRaw.ReadBoolField(v, _viewClass, "IsLooking");
                bool animating = Il2CppRaw.ReadBoolField(v, _viewClass, "_isAnimating");
                MelonLogger.Msg($"[ActionMenu]   [{i++}] '{name}' canShowHint={canHint} active={active} " +
                                $"looking={looking} animating={animating} " +
                                $"pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) dist={Flat(pos - camPos):F1}m");
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
