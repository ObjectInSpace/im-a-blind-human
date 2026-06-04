using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Context-agnostic uGUI keyboard-focus keeper for the main menu and ANY panel opened from it (Settings,
    /// Collection, Credits, Gacha, …). Two problems it solves, both because the game only does these handoffs for
    /// controller, not keyboard:
    ///
    ///   1. DEAD ARROWS: uGUI keyboard navigation only flows once a Selectable is current. If nothing is selected,
    ///      arrows do nothing — so we select the first active+interactable control.
    ///   2. STUCK FOCUS ON PANEL OPEN: when you activate a menu button that opens a sub-panel, focus stays on the
    ///      button (still "valid"), so arrows would navigate the wrong panel. We detect that a panel opened/closed —
    ///      the set of active selectables CHANGED — and move focus into the new panel's first control.
    ///
    /// PANEL-AGNOSTIC by design: we don't know or special-case any panel. We watch the COUNT of active+interactable
    /// <c>UnityEngine.UI.Selectable</c>s; when it changes (a panel opened or closed), we refocus the first active
    /// selectable that isn't the current selection. That handles Settings, Collection, Credits, back-to-root, etc.
    /// uniformly. After the refocus, native uGUI nav + <see cref="Menus.MenuNarrator"/> drive everything.
    ///
    /// Only invoked while the mod is in a menu context it owns (see AccessMod routing). Zenject-free; all via raw Unity
    /// statics: <c>EventSystem.current</c>, <c>Selectable.allSelectablesArray</c>, <c>EventSystem.SetSelectedGameObject</c>.
    /// </summary>
    public sealed class UguiFocus
    {
        private bool _resolved;
        private IntPtr _eventSystemClass, _getCurrentES, _setSelected, _getCurrentSelected;
        private IntPtr _selectableClass, _getAllArray, _getInteractable, _getGameObject;
        private IntPtr _mainMenuViewClass; // for honoring the game's designated initial selection on the main menu

        private readonly System.Collections.Generic.HashSet<IntPtr> _prevActive = new(); // active selectables last tick
        private bool _havePrev;
        private IntPtr _lastSelected; // throttle re-selecting the same control every frame

        /// <summary>Keep keyboard focus sensible in menus. Call each tick while in a menu context.</summary>
        public void EnsureSelection()
        {
            try
            {
                EnsureResolved();
                IntPtr es = Il2CppRaw.InvokeStaticObjectGetter(_getCurrentES);
                if (es == IntPtr.Zero) return;

                // Snapshot the active+interactable selectables (in array order) this tick.
                var active = ActiveSelectables();

                // The controls that are NEWLY active vs last tick = the panel that just opened. Its FIRST control (in
                // array order) is where focus should land. This is panel-agnostic and correct even when the main-menu
                // buttons stay active behind the panel (they're not "new", so they're excluded). Closing a panel adds
                // no new controls → newFirst is zero → we fall back to keeping/repairing the current selection.
                IntPtr newFirst = IntPtr.Zero;
                if (_havePrev)
                {
                    foreach (IntPtr go in active)
                        if (!_prevActive.Contains(go)) { newFirst = go; break; }
                }

                _prevActive.Clear();
                foreach (IntPtr go in active) _prevActive.Add(go);
                _havePrev = true;

                IntPtr current = Il2CppRaw.InvokeObjectGetter(es, _getCurrentSelected);
                // A selection is only VALID if it's still active AND still in the active+interactable set. The second
                // check matters: a control can stay active-in-hierarchy while becoming NON-interactable (e.g. the phone
                // disables every dial button the moment you press Call). uGUI won't navigate off a disabled-but-selected
                // control, so arrows wedge. Treating it as invalid here moves focus to a usable control and unsticks them.
                bool currentValid = current != IntPtr.Zero
                    && Il2CppRaw.GetGameObjectActiveInHierarchy(current)
                    && active.Contains(current);

                IntPtr target;
                bool usedMenuDefault = false;
                if (newFirst != IntPtr.Zero && current != newFirst)
                    target = newFirst;                 // a panel just opened → jump into it
                else if (!currentValid)
                {
                    // Nothing selected (menu just appeared). The game designates an initial selection per menu (it
                    // applies it for controller, not keyboard); on the main-menu root that's the New Game button.
                    // Honor it so focus lands where the player expects rather than on whatever happens to be first in
                    // the unordered allSelectablesArray (which is the CAR/Discord button). If that control isn't
                    // currently active+interactable (e.g. a sub-panel replaced the root), fall back to first control.
                    IntPtr menuDefault = MainMenuDefaultSelection(active);
                    if (menuDefault != IntPtr.Zero) { target = menuDefault; usedMenuDefault = true; }
                    else target = active.Count > 0 ? active[0] : IntPtr.Zero;
                }
                else
                {
                    _lastSelected = current;           // valid selection, no new panel → leave it to native nav
                    return;
                }

                if (target == IntPtr.Zero || target == _lastSelected) return;
                Il2CppRaw.SetSelectedGameObject(es, _setSelected, target);
                _lastSelected = target;
                string what = newFirst != IntPtr.Zero ? "new panel's first control"
                            : usedMenuDefault ? "menu's designated first control (New Game)"
                            : "first control";
                MelonLogger.Msg($"[UguiFocus] focus → {what} (active={active.Count}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[UguiFocus] EnsureSelection threw: {e.Message}");
            }
        }

        /// <summary>Active + interactable Selectable GameObjects, in <c>allSelectablesArray</c> order.</summary>
        private System.Collections.Generic.List<IntPtr> ActiveSelectables()
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            if (_selectableClass == IntPtr.Zero || _getAllArray == IntPtr.Zero) return list;
            IntPtr arrayPtr = Il2CppRaw.InvokeStaticObjectGetter(_getAllArray); // Selectable.allSelectablesArray (static)
            foreach (IntPtr sel in Il2CppRaw.ReadObjectArray(arrayPtr))
            {
                if (sel == IntPtr.Zero) continue;
                if (_getInteractable != IntPtr.Zero && !Il2CppRaw.InvokeBoolGetter(sel, _getInteractable)) continue;
                IntPtr go = Il2CppRaw.InvokeObjectGetter(sel, _getGameObject);
                if (go == IntPtr.Zero) continue;
                if (!Il2CppRaw.GetGameObjectActiveInHierarchy(go)) continue;
                list.Add(go);
            }
            return list;
        }

        /// <summary>
        /// The GameObject the game designates as the main menu's initial selection (New Game button), but only if it
        /// is currently in <paramref name="active"/> (active + interactable). Returns zero when not on the main-menu
        /// root or the button is unavailable, so the caller falls back to the generic first-control behavior. We find
        /// the single <c>MainMenuView</c> instance and read its <c>_newGameButtonGO</c> field.
        /// </summary>
        private IntPtr MainMenuDefaultSelection(System.Collections.Generic.List<IntPtr> active)
        {
            if (_mainMenuViewClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr view = Il2CppRaw.FindObjectIncludingInactive(_mainMenuViewClass);
            if (view == IntPtr.Zero) return IntPtr.Zero;
            IntPtr go = Il2CppRaw.ReadObjectField(view, _mainMenuViewClass, "_newGameButtonGO");
            if (go == IntPtr.Zero) return IntPtr.Zero;
            // Only honor it if it's one of the currently selectable controls — guards against the sub-panel case and
            // against selecting a hidden/disabled New Game button.
            return active.Contains(go) ? go : IntPtr.Zero;
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _eventSystemClass = Il2CppRaw.GetClass("UnityEngine.UI.dll", "UnityEngine.EventSystems", "EventSystem");
                if (_eventSystemClass == IntPtr.Zero)
                    _eventSystemClass = Il2CppRaw.GetClass("UnityEngine.UIModule.dll", "UnityEngine.EventSystems", "EventSystem");
                if (_eventSystemClass != IntPtr.Zero)
                {
                    _getCurrentES = Il2CppRaw.GetMethod(_eventSystemClass, "get_current", 0);
                    _setSelected = Il2CppRaw.GetMethod(_eventSystemClass, "SetSelectedGameObject", 1);
                    _getCurrentSelected = Il2CppRaw.GetMethod(_eventSystemClass, "get_currentSelectedGameObject", 0);
                }

                _selectableClass = Il2CppRaw.GetClass("UnityEngine.UI.dll", "UnityEngine.UI", "Selectable");
                if (_selectableClass != IntPtr.Zero)
                {
                    _getAllArray = Il2CppRaw.GetMethod(_selectableClass, "get_allSelectablesArray", 0);
                    _getInteractable = Il2CppRaw.GetMethod(_selectableClass, "get_interactable", 0);
                    _getGameObject = Il2CppRaw.GetMethod(_selectableClass, "get_gameObject", 0);
                }

                _mainMenuViewClass = Il2CppRaw.GetClass("Assembly-CSharp.dll", "_Code.Infrastructure.MainMenu", "MainMenuView");

                MelonLogger.Msg($"[UguiFocus] resolved: eventSystem={_eventSystemClass != IntPtr.Zero} " +
                                $"getCurrentES={_getCurrentES != IntPtr.Zero} selectable={_selectableClass != IntPtr.Zero} " +
                                $"allArray={_getAllArray != IntPtr.Zero} mainMenuView={_mainMenuViewClass != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[UguiFocus] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
