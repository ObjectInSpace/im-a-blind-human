using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Reads live game-state for the status key: current day + time-of-day, energy (= day-actions; the game has no
    /// separate energy meter — <c>AddEnergy()</c> just bumps <c>DayActions</c>), and the player's CONSUMABLES counts.
    ///
    /// The controllers are Zenject-injected interfaces backed by plain C# classes (<c>DayNightController :
    /// ASavableClass</c>), NOT MonoBehaviours — so <c>FindObjectOfType</c> can't reach them. We instead pull them
    /// from the Zenject <c>DiContainer</c>: find the scene's <c>SceneContext</c> (a MonoBehaviour, so findable),
    /// read its <c>Container</c>, and call the NON-generic <c>Resolve(System.Type)</c> (the generic <c>Resolve&lt;T&gt;</c>
    /// won't JIT in this interop build — same open-generic wall that breaks <c>GetComponents&lt;T&gt;</c>). Resolved
    /// instances are cached; if any step fails we log and the status key degrades gracefully rather than throwing.
    ///
    /// CONSUMABLES (re-added 2026-06-04): <c>IConsumablesController.Count(EConsumable)</c> is the right source — it
    /// reads <c>ConsumablesSaveData.Storage</c>, the player's actual consumable counts. An earlier pass dropped this
    /// thinking the values were wrong (a "fresh game" read beer=8), but that's just the game's STARTING consumables
    /// (the player only gains fridge ACCESS after the first night; the count exists before then). It was mislabeled as
    /// "inventory/held items"; the data was always correct. We word it as "consumables" and speak the non-zero counts.
    /// </summary>
    public sealed class GameStateAccess
    {
        // Controllers are resolved from the Zenject container via ZenjectResolver; method handles below are bound off
        // the resolved instances' classes.
        private IntPtr _dayNight;       // IDayNightController instance ptr
        private IntPtr _consumables;    // IConsumablesController instance ptr
        private bool _resolveAttempted;

        // Cached getter/method handles on the resolved instances' classes.
        private IntPtr _getDay, _getTimeOfDay, _getDayActions, _getMaxDayActions; // on IDayNightController
        private IntPtr _countMethod;                                              // IConsumablesController.Count(EConsumable)

        // Calendar: read the current day's HOLIDAY NAME so the status speaks the actual calendar, not just "Day N".
        // CalendarInteractable is a MonoBehaviour (ZoomInteractable -> AInteractableObject), so it's findable directly
        // (no Zenject). Its _holidayName is a LocalizeStringEvent the game keeps set to the current day's holiday (set in
        // RefreshPage on load). We resolve that to text the same way ControlDescriber resolves its LocalizeStringEvents.
        // NOTE: the day-of-MONTH number shown on the calendar is baked into the paper-sheet sprite art, not readable
        // data, so we pair the holiday with the Day index (the only readable date value) rather than a real month-date.
        private IntPtr _calendarClass, _localizeStringEventClass, _getLocalizedString;
        private IntPtr _tmpTextClass, _tmpGetText;       // fallback: read the text the holiday LSE actually drives
        private IntPtr _localizedStringClass, _lsGetLocalizedString; // resolve the raw per-day LocalizedString directly
        private bool _calendarResolveAttempted;

        /// <summary>True once the day/night controller is resolved and its accessors bound. (Consumables are optional —
        /// the day/time/energy line still speaks if only the consumables controller fails to resolve.)</summary>
        public bool IsReady => _dayNight != IntPtr.Zero;

        /// <summary>
        /// Compose the spoken status string, e.g. "Day 3, night, 2 of 5 energy. Consumables: 8 beer, 1 kombucha."
        /// Returns null if state can't be read (caller speaks a fallback). Resolves controllers on first call.
        /// </summary>
        public string? Describe()
        {
            EnsureResolved();
            if (!IsReady) return null;

            try
            {
                var sb = new StringBuilder();

                int day = Il2CppRaw.InvokeInt32Getter(_dayNight, _getDay);
                int tod = Il2CppRaw.InvokeInt32Getter(_dayNight, _getTimeOfDay); // ETimeOfDay: Day=0, Night=1
                int actions = Il2CppRaw.InvokeInt32Getter(_dayNight, _getDayActions);
                int maxActions = Il2CppRaw.InvokeInt32Getter(_dayNight, _getMaxDayActions);

                if (day >= 0)
                {
                    sb.Append($"Day {day}");
                    // Append the calendar's holiday name for today, when it resolves (e.g. "Day 1, Halloween").
                    string? holiday = DescribeHoliday(day);
                    if (!string.IsNullOrWhiteSpace(holiday))
                        sb.Append($", {holiday}");
                }
                if (tod >= 0)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(tod == 1 ? "night" : "day");
                }
                if (actions >= 0 && maxActions >= 0)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append($"{actions} of {maxActions} energy");
                }

                string consumables = DescribeConsumables();
                if (consumables.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(". ");
                    sb.Append(consumables);
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] Describe threw: {e.Message}");
                return null;
            }
        }

        /// <summary>Name the non-zero consumable counts, e.g. "Consumables: 8 beer, 1 kombucha." Empty string when the
        /// controller isn't resolved or the player has none.</summary>
        private string DescribeConsumables()
        {
            if (_consumables == IntPtr.Zero || _countMethod == IntPtr.Zero) return string.Empty;
            var held = new List<string>();
            foreach (var (value, singular, plural) in Consumables)
            {
                int count = Il2CppRaw.InvokeInt32MethodWithEnum(_consumables, _countMethod, value, fallback: 0);
                if (count > 0) held.Add($"{count} {(count == 1 ? singular : plural)}");
            }
            return held.Count == 0 ? string.Empty : "Consumables: " + string.Join(", ", held) + ".";
        }

        // EConsumable values worth reporting (enum order from the decompile). Cockroach=100 is an oddity; omit unless
        // it proves real. Explicit plural per item so irregulars ("summons", "cat food") read naturally.
        private static readonly (int value, string singular, string plural)[] Consumables =
        {
            (0, "beer", "beers"), (1, "cigarette", "cigarettes"), (2, "coffee", "coffees"),
            (3, "energy drink", "energy drinks"), (4, "pill", "pills"), (5, "mushroom", "mushrooms"),
            (6, "cat food", "cat food"), (7, "summons", "summons"), (8, "kombucha", "kombuchas"),
            (9, "photo", "photos"),
        };

        /// <summary>Resolve the calendar's holiday name for the current day to spoken text, or null if it can't be read
        /// (the status then just says "Day N"). The CalendarInteractable keeps its _holidayName LocalizeStringEvent set
        /// to the current day; we resolve it via GetLocalizedString(), mirroring ControlDescriber. Never throws.</summary>
        private string? DescribeHoliday(int day)
        {
            try
            {
                EnsureCalendarResolved();
                IntPtr calendar = _calendarClass != IntPtr.Zero ? Il2CppRaw.FindObjectIncludingInactive(_calendarClass) : IntPtr.Zero;
                if (calendar == IntPtr.Zero) return null;

                // PRIMARY: read the per-day LocalizedString straight from the calendar's _calendarDays[day] and resolve
                // it. This is authoritative for the current day regardless of whether the player has opened the calendar
                // (the _holidayName LSE only reflects the page once it's been shown). _calendarDays is a CalendarListData[]
                // whose HolidayName property is backed by <HolidayName>k__BackingField (a LocalizedString).
                if (_lsGetLocalizedString != IntPtr.Zero)
                {
                    IntPtr daysArray = Il2CppRaw.ReadObjectField(calendar, _calendarClass, "_calendarDays");
                    IntPtr[] days = daysArray != IntPtr.Zero ? Il2CppRaw.ReadObjectArray(daysArray) : Array.Empty<IntPtr>();
                    if (day >= 0 && day < days.Length && days[day] != IntPtr.Zero)
                    {
                        IntPtr listDataClass = IL2CPP.il2cpp_object_get_class(days[day]);
                        IntPtr holiday = Il2CppRaw.ReadObjectField(days[day], listDataClass, "<HolidayName>k__BackingField");
                        if (holiday != IntPtr.Zero)
                        {
                            string? text = Il2CppRaw.InvokeStringGetter(holiday, _lsGetLocalizedString);
                            if (!string.IsNullOrWhiteSpace(text)) return text!.Trim();
                        }
                    }
                }

                // FALLBACK 1: the _holidayName LocalizeStringEvent's resolved string (works only after the page is shown).
                IntPtr holidayLse = Il2CppRaw.ReadObjectField(calendar, _calendarClass, "_holidayName");
                if (holidayLse != IntPtr.Zero)
                {
                    if (_getLocalizedString != IntPtr.Zero)
                    {
                        string? viaApi = Il2CppRaw.InvokeStringGetter(holidayLse, _getLocalizedString);
                        if (!string.IsNullOrWhiteSpace(viaApi)) return viaApi!.Trim();
                    }
                    // FALLBACK 2: the TMP text the LSE drives on its own GameObject.
                    if (_tmpTextClass != IntPtr.Zero && _tmpGetText != IntPtr.Zero)
                    {
                        IntPtr go = Il2CppRaw.GetComponentGameObject(holidayLse);
                        if (go != IntPtr.Zero)
                        {
                            IntPtr tmp = Il2CppRaw.GetComponentRaw(go, _tmpTextClass);
                            string? viaTmp = Il2CppRaw.InvokeStringGetter(tmp, _tmpGetText);
                            if (!string.IsNullOrWhiteSpace(viaTmp)) return viaTmp!.Trim();
                        }
                    }
                }
                return null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] DescribeHoliday threw: {e.Message}");
                return null;
            }
        }

        private void EnsureCalendarResolved()
        {
            if (_calendarResolveAttempted) return;
            _calendarResolveAttempted = true;
            try
            {
                _calendarClass = Il2CppRaw.GetClass("Assembly-CSharp.dll", "_Code.Infrastructure._NINAH__InteractableObjects.Objects", "CalendarInteractable");

                // Raw LocalizedString.GetLocalizedString() — the authoritative per-day resolver (the primary path).
                _localizedStringClass = Il2CppRaw.GetClass("Unity.Localization.dll", "UnityEngine.Localization", "LocalizedString");
                if (_localizedStringClass != IntPtr.Zero)
                    _lsGetLocalizedString = Il2CppRaw.GetMethod(_localizedStringClass, "GetLocalizedString", 0);

                // LocalizeStringEvent + TMP — fallbacks (only valid once the calendar page has been shown).
                _localizeStringEventClass = Il2CppRaw.GetClass("Unity.Localization.dll", "UnityEngine.Localization.Components", "LocalizeStringEvent");
                if (_localizeStringEventClass != IntPtr.Zero)
                    _getLocalizedString = Il2CppRaw.GetMethod(_localizeStringEventClass, "GetLocalizedString", 0);
                _tmpTextClass = Il2CppRaw.GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");
                if (_tmpTextClass != IntPtr.Zero)
                    _tmpGetText = Il2CppRaw.GetMethod(_tmpTextClass, "get_text", 0);

                MelonLogger.Msg($"[GameStateAccess] calendar resolved: class={_calendarClass != IntPtr.Zero} " +
                                $"lsGetLoc={_lsGetLocalizedString != IntPtr.Zero} lseGetLoc={_getLocalizedString != IntPtr.Zero} " +
                                $"tmp={_tmpGetText != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] EnsureCalendarResolved threw: {e.Message}");
            }
        }

        private void EnsureResolved()
        {
            if (_resolveAttempted) return;
            _resolveAttempted = true;

            try
            {
                _dayNight = ZenjectResolver.Resolve("_Code.Infrastructure.DayNight", "IDayNightController");
                _consumables = ZenjectResolver.Resolve("_Code.Infrastructure.Consumables", "IConsumablesController");

                if (_dayNight != IntPtr.Zero)
                {
                    IntPtr dnClass = IL2CPP.il2cpp_object_get_class(_dayNight);
                    _getDay = Il2CppRaw.GetMethod(dnClass, "get_Day", 0);
                    _getTimeOfDay = Il2CppRaw.GetMethod(dnClass, "get_CurrentTimeOfDay", 0);
                    _getDayActions = Il2CppRaw.GetMethod(dnClass, "get_DayActions", 0);
                    _getMaxDayActions = Il2CppRaw.GetMethod(dnClass, "get_MaxDayActions", 0);
                }
                if (_consumables != IntPtr.Zero)
                {
                    IntPtr cClass = IL2CPP.il2cpp_object_get_class(_consumables);
                    _countMethod = Il2CppRaw.GetMethod(cClass, "Count", 1);
                }

                MelonLogger.Msg($"[GameStateAccess] resolved: dayNight={_dayNight != IntPtr.Zero} " +
                                $"consumables={_consumables != IntPtr.Zero} getDay={_getDay != IntPtr.Zero} " +
                                $"count={_countMethod != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
