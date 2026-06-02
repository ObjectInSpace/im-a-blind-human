using System;
using System.Text;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Reads live game-state for the status key: current day + time-of-day and energy (= day-actions; the game has no
    /// separate energy meter — <c>AddEnergy()</c> just bumps <c>DayActions</c>).
    ///
    /// The controller is a Zenject-injected interface backed by a plain C# class (<c>DayNightController :
    /// ASavableClass</c>), NOT a MonoBehaviour — so <c>FindObjectOfType</c> can't reach it. We instead pull it
    /// from the Zenject <c>DiContainer</c>: find the scene's <c>SceneContext</c> (a MonoBehaviour, so findable),
    /// read its <c>Container</c>, and call the NON-generic <c>Resolve(System.Type)</c> (the generic <c>Resolve&lt;T&gt;</c>
    /// won't JIT in this interop build — same open-generic wall that breaks <c>GetComponents&lt;T&gt;</c>). The resolved
    /// instance is cached; if any step fails we log and the status key degrades to "couldn't read state" rather
    /// than throwing.
    ///
    /// NOTE: held-items readout was REMOVED. <c>IConsumablesController.Count(EConsumable)</c> does not report the
    /// player's visible inventory (a confirmed-empty fresh game read beer=8, kombucha=1 — read mechanism verified
    /// correct, but the value is not what the game shows as held). The stripped decompile can't tell us what
    /// <c>Count</c>/<c>Storage</c> actually represents, so we don't speak it. Re-add only against a verified source.
    /// </summary>
    public sealed class GameStateAccess
    {
        // The DayNight controller is resolved from the Zenject container via ZenjectResolver; method handles below are
        // bound off its resolved class.
        private IntPtr _dayNight;       // IDayNightController instance ptr
        private bool _resolveAttempted;

        // Cached getter handles on the resolved instance's class.
        private IntPtr _getDay, _getTimeOfDay, _getDayActions, _getMaxDayActions; // on IDayNightController

        /// <summary>True once the day/night controller is resolved and its accessors bound.</summary>
        public bool IsReady => _dayNight != IntPtr.Zero;

        /// <summary>
        /// Compose the spoken status string, e.g. "Day 3, night, 2 of 5 energy. Holding 3 cigarettes, 1 coffee."
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

                if (day >= 0) sb.Append($"Day {day}");
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

                // NOTE: no held-items line. IConsumablesController.Count(EConsumable) does NOT report the player's
                // visible inventory — on a confirmed-empty fresh game it returned beer=8, kombucha=1 (read mechanism
                // verified correct via probe: right object, enum arg delivered, return unboxed). What Storage/Count
                // actually tracks isn't recoverable from the stripped decompile, so we don't speak it rather than
                // narrate a number the game contradicts. Re-add only against a verified inventory source.

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] Describe threw: {e.Message}");
                return null;
            }
        }

        private void EnsureResolved()
        {
            if (_resolveAttempted) return;
            _resolveAttempted = true;

            try
            {
                _dayNight = ZenjectResolver.Resolve("_Code.Infrastructure.DayNight", "IDayNightController");

                if (_dayNight != IntPtr.Zero)
                {
                    IntPtr dnClass = IL2CPP.il2cpp_object_get_class(_dayNight);
                    _getDay = Il2CppRaw.GetMethod(dnClass, "get_Day", 0);
                    _getTimeOfDay = Il2CppRaw.GetMethod(dnClass, "get_CurrentTimeOfDay", 0);
                    _getDayActions = Il2CppRaw.GetMethod(dnClass, "get_DayActions", 0);
                    _getMaxDayActions = Il2CppRaw.GetMethod(dnClass, "get_MaxDayActions", 0);
                }

                MelonLogger.Msg($"[GameStateAccess] resolved: dayNight={_dayNight != IntPtr.Zero} " +
                                $"getDay={_getDay != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] EnsureResolved threw: {e.Message}");
            }
        }
    }
}
