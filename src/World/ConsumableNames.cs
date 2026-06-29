using System;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Resolves a consumable's player-facing, LOCALIZED name from the game's own data, so readouts follow the player's
    /// chosen language instead of a hardcoded English word. The source is <c>ConsumableLocalizationSOData.Name</c> (a
    /// <c>LocalizedString</c>), keyed by <c>EConsumable</c>; the list lives on the <c>ConsumableCloseUpView</c>'s
    /// <c>_localizationsList</c> field (a <c>ConsumableLocalizationsListSOData</c>), which we read off a scene instance
    /// of the view. The list-SO pointer is cached once found; the names are resolved fresh each call (cheap, and a
    /// language change mid-session then reflects immediately).
    ///
    /// Shared by the F9 status readout (<see cref="GameStateAccess"/>) and the fridge grid (<see cref="FridgeMenu"/>),
    /// which both previously hardcoded English. Falls back to the supplied English word when the localized name can't
    /// be read (no view in scene yet, resolver miss). Never throws.
    /// </summary>
    public static class ConsumableNames
    {
        private const string GameAsm = "Assembly-CSharp.dll";

        private static bool _resolved;
        private static IntPtr _viewClass;          // ConsumableCloseUpView (declares _localizationsList)
        private static IntPtr _listClass;          // ConsumableLocalizationsListSOData (+ get_Consumables)
        private static IntPtr _getConsumables;     // ConsumableLocalizationsListSOData.get_Consumables
        private static IntPtr _entryClass;         // ConsumableLocalizationSOData (+ get_Consumable / get_Name)
        private static IntPtr _getConsumableValue; // ConsumableLocalizationSOData.get_Consumable (EConsumable)
        private static IntPtr _getName;            // ConsumableLocalizationSOData.get_Name (LocalizedString)

        private static IntPtr _listInstance;       // cached _localizationsList SO pointer once found

        /// <summary>
        /// The localized name for <paramref name="consumable"/> (an <c>EConsumable</c> int), or
        /// <paramref name="englishFallback"/> when it can't be resolved. The returned name is whatever the game ships
        /// (typically singular); callers prepend the count themselves.
        /// </summary>
        public static string Localized(int consumable, string englishFallback)
        {
            try
            {
                string? localized = TryResolve(consumable);
                return string.IsNullOrWhiteSpace(localized) ? englishFallback : localized!.Trim();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ConsumableNames] Localized({consumable}) threw: {e.Message}");
                return englishFallback;
            }
        }

        private static string? TryResolve(int consumable)
        {
            EnsureResolved();
            if (_getConsumables == IntPtr.Zero || _getConsumableValue == IntPtr.Zero || _getName == IntPtr.Zero)
                return null;

            IntPtr list = EnsureListInstance();
            if (list == IntPtr.Zero) return null;

            IntPtr arrayPtr = Il2CppRaw.InvokeObjectGetter(list, _getConsumables);
            IntPtr[] entries = Il2CppRaw.ReadObjectArray(arrayPtr);
            foreach (IntPtr entry in entries)
            {
                if (entry == IntPtr.Zero) continue;
                int value = Il2CppRaw.InvokeInt32Getter(entry, _getConsumableValue, fallback: int.MinValue);
                if (value != consumable) continue;
                IntPtr nameLs = Il2CppRaw.InvokeObjectGetter(entry, _getName);
                return Il2CppRaw.ResolveLocalizedString(nameLs);
            }
            return null;
        }

        /// <summary>Find (and cache) the localizations-list SO via any scene <c>ConsumableCloseUpView</c>'s
        /// <c>_localizationsList</c>. The view is present in the game scene (it's the consumable confirm); inactive is
        /// fine, so we include inactive in the search.</summary>
        private static IntPtr EnsureListInstance()
        {
            if (_listInstance != IntPtr.Zero) return _listInstance;
            if (_viewClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr view = Il2CppRaw.FindObjectIncludingInactive(_viewClass);
            if (view == IntPtr.Zero) return IntPtr.Zero;
            _listInstance = Il2CppRaw.ReadObjectField(view, _viewClass, "_localizationsList");
            return _listInstance;
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _viewClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure._NINAH__CloseUps.Views.Consumables", "ConsumableCloseUpView");

                _listClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure._NINAH__CloseUps.Views.Consumables", "ConsumableLocalizationsListSOData");
                if (_listClass != IntPtr.Zero)
                    _getConsumables = Il2CppRaw.GetMethod(_listClass, "get_Consumables", 0);

                _entryClass = Il2CppRaw.GetClass(GameAsm, "_Code.Infrastructure._NINAH__CloseUps.Views.Consumables", "ConsumableLocalizationSOData");
                if (_entryClass != IntPtr.Zero)
                {
                    _getConsumableValue = Il2CppRaw.GetMethod(_entryClass, "get_Consumable", 0);
                    _getName = Il2CppRaw.GetMethod(_entryClass, "get_Name", 0);
                }

                MelonLogger.Msg($"[ConsumableNames] resolved: view={_viewClass != IntPtr.Zero} list={_listClass != IntPtr.Zero} " +
                                $"getConsumables={_getConsumables != IntPtr.Zero} getValue={_getConsumableValue != IntPtr.Zero} " +
                                $"getName={_getName != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ConsumableNames] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
