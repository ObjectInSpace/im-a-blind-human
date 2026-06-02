using System;
using System.Collections.Generic;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Scene-wide action menu for the 3D first-person scene: cycle the available interactions with keys and activate
    /// the selected one WITHOUT walking to or standing near it. This bypasses the navigation problem entirely — the
    /// player picks an interaction from a list and the game performs it.
    ///
    /// Controls (always live, no modal state — uses F-keys the game binds nothing to, so it never fights the game's
    /// own arrows/WASD/Enter):
    ///   F11        — next interaction (announces name + coarse bearing/distance)
    ///   Shift+F11  — previous interaction
    ///   F12        — activate the selected interaction
    ///
    /// Source: <c>ActionableObjectsViewProvider.ActionableObjectViews</c> (the flat 3D interactable set; the 3D
    /// interactables are NOT grouped per-room — only the 2D room-photo views are). We list the views whose
    /// <c>CanShowHint</c> is true — the game's own "offerable right now" flag, which in this hub-and-spoke game is
    /// expected to already be scoped to the current room. If it leaks other rooms by ear, add a spatial room filter.
    /// All Zenject-free: the provider is found via <c>FindObjectOfType</c> (proven working) and the player-position
    /// proxy for bearings is <c>Camera.main</c> (the <see cref="ZenjectResolver"/> path hard-crashes the game).
    ///
    /// Activation: <c>AActionableObjectView.Act()</c> — the private method the game itself runs on interaction. Whether
    /// it fires on a NON-adjacent object (the whole point of this feature) is verified the first time F12 activates a
    /// far entry; if it no-ops/crashes cold, the activation strategy changes (e.g. MoveXZ/TeleportTo to its
    /// <c>_standingPos</c> first, or drive the input/raycast path). Never throws (a native Act() crash can't be caught).
    /// </summary>
    public sealed class ActionMenu
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _viewProviderClass, _getViews;     // ActionableObjectsViewProvider + get_ActionableObjectViews
        private IntPtr _viewClass, _getCanShowHint, _act;  // AActionableObjectView + get_CanShowHint + Act()

        // Current selection, keyed by the view pointer so it survives list rebuilds (positions/availability shift).
        private IntPtr _selected;

        public ActionMenu(ISpeechOutput speech) => _speech = speech;

        /// <summary>Advance the selection by one (or back by one) and announce the now-selected interaction.</summary>
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

                // Find where the current selection sits in the freshly-built list; step from there.
                int idx = MenuStepUtil.NextIndex(entries.FindIndex(e => e.View == _selected), entries.Count, backwards);

                Entry sel = entries[idx];
                _selected = sel.View;
                _speech.Speak($"{sel.Name}, {sel.Bearing}. {idx + 1} of {entries.Count}.", interrupt: true);
                MelonLogger.Msg($"[ActionMenu] selected {idx + 1}/{entries.Count}: '{sel.Name}' ({sel.Bearing}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Cycle threw: {e.Message}");
            }
        }

        /// <summary>Activate the selected interaction by invoking its <c>Act()</c> — no walking required.</summary>
        public void Activate()
        {
            try
            {
                EnsureResolved();
                if (_selected == IntPtr.Zero)
                {
                    _speech.Speak("Nothing selected. Press F11 to choose an interaction.", interrupt: true);
                    return;
                }

                string name = HumanizeName(Il2CppRaw.GetUnityObjectName(_selected));
                _speech.Speak($"Activating {name}.", interrupt: true);
                MelonLogger.Msg($"[ActionMenu] activating '{name}'; act resolved={_act != IntPtr.Zero}. Invoking Act()...");

                bool ok = Il2CppRaw.InvokeVoid(_selected, _act);
                MelonLogger.Msg($"[ActionMenu] Act() returned (threw={!ok}). Watch for the interaction to start.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] Activate threw: {e.Message}");
            }
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

            // FindObjectOfType is active-only; the provider's GameObject may be inactive. Fall back to the
            // inactive-inclusive FindAnyObjectByType (same pattern the Zenject SceneContext lookup needs).
            IntPtr provider = Il2CppRaw.FindObjectOfType(_viewProviderClass);
            if (provider == IntPtr.Zero)
            {
                provider = Il2CppRaw.FindAnyObjectByType(_viewProviderClass, includeInactive: true);
                if (provider != IntPtr.Zero)
                    MelonLogger.Msg("[ActionMenu] provider found via FindAnyObjectByType (inactive include).");
            }
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
                MelonLogger.Msg($"[ActionMenu]   [{i++}] '{name}' canShowHint={canHint} active={active} " +
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
                {
                    _getCanShowHint = Il2CppRaw.GetMethod(_viewClass, "get_CanShowHint", 0);
                    _act = Il2CppRaw.GetMethod(_viewClass, "Act", 0); // private void Act()
                }

                MelonLogger.Msg($"[ActionMenu] resolved (Zenject-free): provider={_viewProviderClass != IntPtr.Zero} " +
                                $"getViews={_getViews != IntPtr.Zero} canShowHint={_getCanShowHint != IntPtr.Zero} " +
                                $"act={_act != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ActionMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
