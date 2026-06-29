using System;
using System.Collections.Generic;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Keyboard stepping for the FRIDGE item grid — the close-up where the player picks a drink (beer / coffee /
    /// Enerjeka). The grid is a MOUSE-ONLY affordance in the base game: items are highlighted by pointer raycast
    /// (<c>FridgeItemView.OnHover</c>/<c>OnUnhover</c> driven by the EventSystem GraphicRaycaster) and used by click
    /// (<c>FridgeItemView.Use</c>). The game's Navigate (arrows/WASD) action does NOT touch this grid, so a blind
    /// player has no way to select an item — this is keyboard-input gap (3) from docs/input-and-keyboard.md.
    ///
    /// We supply the keyboard equivalent by DRIVING THE GAME'S OWN hover/use methods rather than re-implementing the
    /// fridge: arrows call <c>OnUnhover()</c> on the old selection and <c>OnHover()</c> on the new one — which fires
    /// the game's visual highlight AND its <c>OnPointerEntered</c> sink, so <see cref="CloseUpNarrator.OnFridgeItem"/>
    /// (already hooked in <see cref="WorldPatches"/>) speaks "name. description." with zero extra narration code here.
    /// Enter calls <c>Use()</c>, the same method the game's click path runs.
    ///
    /// Item set: each beverage TYPE is one <c>FridgeItemController</c> with a stack of <c>FridgeItemView</c>s (a count).
    /// The selectable units are the TYPES, so we enumerate the live <c>FridgeItemView</c>s under the open fridge,
    /// dedupe by <c>ItemType</c> (keeping the first active one per type — that's the one the pointer would hit), and
    /// step those. An empty stack (count 0) has no active view, so it naturally drops out of the list.
    ///
    /// Selection survives list rebuilds by keying on <c>ItemType</c> (int), not the view pointer (the topmost view of a
    /// stack changes as items are consumed). All Zenject-free: the fridge view and its item views are found via
    /// <c>FindObjectsByType</c>. Never throws.
    /// </summary>
    public sealed class FridgeMenu
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string FridgeNs = "_Code.Infrastructure.CloseUps.Views";

        private readonly ISpeechOutput _speech;

        private bool _resolved;
        private IntPtr _itemViewClass;     // FridgeItemView
        private IntPtr _getItemType;       // FridgeItemView.get_ItemType  (EConsumable as int)
        private IntPtr _onHover, _onUnhover, _use; // FridgeItemView.OnHover() / OnUnhover() / Use()

        // Current selection keyed by EConsumable so it survives stack/list rebuilds. -1 = nothing selected.
        private int _selectedType = -1;

        public FridgeMenu(ISpeechOutput speech) => _speech = speech;

        /// <summary>Advance the highlight to the next (or previous) drink and let the game's own hover narrate it.</summary>
        public void Cycle(bool backwards)
        {
            try
            {
                EnsureResolved();
                List<Entry> entries = BuildEntries();
                if (entries.Count == 0)
                {
                    _speech.Speak("The fridge is empty.", interrupt: true);
                    _selectedType = -1;
                    return;
                }

                int cur = entries.FindIndex(e => e.ItemType == _selectedType);
                int idx = MenuStepUtil.NextIndex(cur, entries.Count, backwards);
                Entry sel = entries[idx];

                // Unhover whatever we had highlighted (its view may have changed; re-find by the OLD type).
                if (_selectedType != sel.ItemType)
                {
                    Entry? prev = cur >= 0 ? entries[cur] : (Entry?)null;
                    if (prev.HasValue) Il2CppRaw.InvokeVoid(prev.Value.View, _onUnhover);
                }

                _selectedType = sel.ItemType;
                // OnHover fires the game's visual highlight (and its OnPointerEntered, harmless if it narrates).
                bool ok = Il2CppRaw.InvokeVoid(sel.View, _onHover);

                // Speak the drink name OURSELVES — the game's hover→OnPointerEntered→CloseUpNarrator chain did NOT
                // narrate on step (user heard nothing), so we don't depend on it. Map the EConsumable type to a name
                // and announce it with position, so the player always knows what's focused.
                string drink = DrinkName(sel.ItemType);
                _speech.Speak($"{drink}, {idx + 1} of {entries.Count}.", interrupt: true);
                MelonLogger.Msg($"[FridgeMenu] selected {idx + 1}/{entries.Count}: {drink} (type={sel.ItemType}, hover threw={!ok}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FridgeMenu] Cycle threw: {e.Message}");
            }
        }

        /// <summary>Use the highlighted drink (the same call the game's click path runs).</summary>
        public void Activate()
        {
            try
            {
                EnsureResolved();
                if (_selectedType < 0)
                {
                    _speech.Speak("Nothing selected. Use the arrow keys to choose a drink.", interrupt: true);
                    return;
                }

                List<Entry> entries = BuildEntries();
                int idx = entries.FindIndex(e => e.ItemType == _selectedType);
                if (idx < 0)
                {
                    _speech.Speak("That drink is no longer available.", interrupt: true);
                    _selectedType = -1;
                    return;
                }

                MelonLogger.Msg($"[FridgeMenu] using type={_selectedType}. Invoking Use()...");
                bool ok = Il2CppRaw.InvokeVoid(entries[idx].View, _use);
                MelonLogger.Msg($"[FridgeMenu] Use() returned (threw={!ok}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FridgeMenu] Activate threw: {e.Message}");
            }
        }

        /// <summary>Reset the selection when the fridge closes, so re-opening starts fresh.</summary>
        public void Reset() => _selectedType = -1;

        /// <summary>Spoken name for an <c>EConsumable</c> value (the fridge's ItemType). Prefers the game's LOCALIZED
        /// name (so the readout follows the player's language); the English switch is the fallback when it can't be
        /// resolved. Falls back to "item N" for an unmapped value so stepping is never silent. Bobeer = the beer.</summary>
        private static string DrinkName(int itemType) => ConsumableNames.Localized(itemType, EnglishDrinkName(itemType));

        private static string EnglishDrinkName(int itemType) => itemType switch
        {
            0 => "beer",            // Bobeer
            1 => "cigarettes",      // Cigarette
            2 => "coffee",          // Coffee
            3 => "energy drink",    // Enerjeka
            4 => "pills",           // Pills
            5 => "mushroom",        // Mushroom
            6 => "cat food",        // CatFood
            7 => "draft notice",    // Povistka
            8 => "kombucha",        // Kombucha
            9 => "photo",           // Photo
            100 => "cockroach",     // Cockroach
            _ => $"item {itemType}",
        };

        private readonly struct Entry
        {
            public readonly IntPtr View;
            public readonly int ItemType;
            public Entry(IntPtr view, int itemType) { View = view; ItemType = itemType; }
        }

        /// <summary>
        /// The live, selectable drinks: active <c>FridgeItemView</c>s under the open fridge, one per <c>ItemType</c>
        /// (the first active view of each type, mirroring which one the pointer would land on). Ordered by item type so
        /// the cycle order is stable across rebuilds.
        /// </summary>
        private List<Entry> BuildEntries()
        {
            var entries = new List<Entry>();
            if (_itemViewClass == IntPtr.Zero) return entries;

            IntPtr[] views = Il2CppRaw.FindObjectsByType(_itemViewClass, includeInactive: false);
            var seen = new HashSet<int>();
            foreach (IntPtr v in views)
            {
                if (v == IntPtr.Zero) continue;
                if (!Il2CppRaw.GetComponentGameObjectActive(v)) continue; // empty stacks have inactive views
                int type = Il2CppRaw.InvokeInt32Getter(v, _getItemType);
                if (type < 0 || !seen.Add(type)) continue; // one entry per beverage type
                entries.Add(new Entry(v, type));
            }
            entries.Sort((a, b) => a.ItemType.CompareTo(b.ItemType));
            return entries;
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _itemViewClass = Il2CppRaw.GetClass(GameAsm, FridgeNs, "FridgeItemView");
                if (_itemViewClass != IntPtr.Zero)
                {
                    _getItemType = Il2CppRaw.GetMethod(_itemViewClass, "get_ItemType", 0);
                    _onHover = Il2CppRaw.GetMethod(_itemViewClass, "OnHover", 0);
                    _onUnhover = Il2CppRaw.GetMethod(_itemViewClass, "OnUnhover", 0);
                    _use = Il2CppRaw.GetMethod(_itemViewClass, "Use", 0);
                }

                MelonLogger.Msg($"[FridgeMenu] resolved: itemView={_itemViewClass != IntPtr.Zero} " +
                                $"getItemType={_getItemType != IntPtr.Zero} onHover={_onHover != IntPtr.Zero} " +
                                $"onUnhover={_onUnhover != IntPtr.Zero} use={_use != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FridgeMenu] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
