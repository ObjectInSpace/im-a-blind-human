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
    /// separate energy meter — <c>AddEnergy()</c> just bumps <c>DayActions</c>), and held consumable items.
    ///
    /// The controllers are Zenject-injected interfaces backed by plain C# classes (<c>DayNightController :
    /// ASavableClass</c>), NOT MonoBehaviours — so <c>FindObjectOfType</c> can't reach them. We instead pull them
    /// from the Zenject <c>DiContainer</c>: find the scene's <c>SceneContext</c> (a MonoBehaviour, so findable),
    /// read its <c>Container</c>, and call the NON-generic <c>Resolve(System.Type)</c> (the generic <c>Resolve&lt;T&gt;</c>
    /// won't JIT in this interop build — same open-generic wall that breaks <c>GetComponents&lt;T&gt;</c>). Resolved
    /// instances are cached; if any step fails we log and the status key degrades to "couldn't read state" rather
    /// than throwing. (Fallback if the container path proves unusable in-game: capture each controller via a Harmony
    /// postfix on its Init/ctor — see the movement-model memo. We try the container first.)
    /// </summary>
    public sealed class GameStateAccess
    {
        // --- IL2CPP class/method handles (resolved lazily, cached in Il2CppRaw) ---
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string ZenjectAsm = "Zenject.dll";

        private IntPtr _dayNight;       // IDayNightController instance ptr
        private IntPtr _consumables;    // IConsumablesController instance ptr
        private bool _resolveAttempted;

        // Cached getter/method handles on the resolved instances' classes.
        private IntPtr _getDay, _getTimeOfDay, _getDayActions, _getMaxDayActions; // on IDayNightController
        private IntPtr _countMethod;                                              // IConsumablesController.Count(EConsumable)

        /// <summary>True once both controllers are resolved and their accessors bound.</summary>
        public bool IsReady => _dayNight != IntPtr.Zero && _consumables != IntPtr.Zero;

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

                string items = DescribeItems();
                if (items.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(". ");
                    sb.Append(items);
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[GameStateAccess] Describe threw: {e.Message}");
                return null;
            }
        }

        /// <summary>Iterate the held-consumable counts, naming non-zero ones, e.g. "Holding 3 cigarettes, 1 coffee."</summary>
        private string DescribeItems()
        {
            if (_countMethod == IntPtr.Zero) return string.Empty;
            var held = new List<string>();
            foreach (var (value, name) in Consumables)
            {
                int count = Il2CppRaw.InvokeInt32MethodWithEnum(_consumables, _countMethod, value, fallback: 0);
                if (count > 0) held.Add(count == 1 ? $"1 {name}" : $"{count} {name}s");
            }
            return held.Count == 0 ? string.Empty : "Holding " + string.Join(", ", held) + ".";
        }

        // EConsumable values worth reporting as inventory (Cockroach=100 is an oddity; omit unless it proves real).
        private static readonly (int value, string name)[] Consumables =
        {
            (0, "beer"), (1, "cigarette"), (2, "coffee"), (3, "energy drink"),
            (4, "pill"), (5, "mushroom"), (6, "cat food"), (7, "summons"),
            (8, "kombucha"), (9, "photo"),
        };

        private void EnsureResolved()
        {
            if (_resolveAttempted) return;
            _resolveAttempted = true;

            try
            {
                _dayNight = ResolveFromContainer("_Code.Infrastructure.DayNight", "IDayNightController");
                _consumables = ResolveFromContainer("_Code.Infrastructure.Consumables", "IConsumablesController");

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

        /// <summary>
        /// Resolve a single interface instance from the live Zenject container. Returns zero (and logs) on any miss
        /// so the caller can fall back. The interface type comes from the game assembly; <c>Resolve</c> is the
        /// non-generic <c>object Resolve(System.Type)</c> on <c>DiContainer</c>.
        /// </summary>
        private IntPtr ResolveFromContainer(string runtimeNamespace, string interfaceName)
        {
            IntPtr sceneCtxClass = Il2CppRaw.GetClass(ZenjectAsm, "Zenject", "SceneContext");
            if (sceneCtxClass == IntPtr.Zero)
            {
                MelonLogger.Warning("[GameStateAccess] Zenject.SceneContext class not found.");
                return IntPtr.Zero;
            }
            IntPtr sceneCtx = Il2CppRaw.FindObjectOfType(sceneCtxClass);
            if (sceneCtx == IntPtr.Zero)
            {
                MelonLogger.Warning("[GameStateAccess] No live SceneContext in scene (not in gameplay yet?).");
                return IntPtr.Zero;
            }

            // SceneContext.Container is on the Context base; the getter resolves on the instance's class fine.
            IntPtr getContainer = Il2CppRaw.GetMethod(IL2CPP.il2cpp_object_get_class(sceneCtx), "get_Container", 0);
            IntPtr container = Il2CppRaw.InvokeObjectGetter(sceneCtx, getContainer);
            if (container == IntPtr.Zero)
            {
                MelonLogger.Warning("[GameStateAccess] SceneContext.Container was null.");
                return IntPtr.Zero;
            }

            IntPtr ifaceClass = Il2CppRaw.GetClass(GameAsm, runtimeNamespace, interfaceName);
            IntPtr typeObj = Il2CppRaw.TypeObject(ifaceClass);
            if (typeObj == IntPtr.Zero)
            {
                MelonLogger.Warning($"[GameStateAccess] Type object for {runtimeNamespace}.{interfaceName} not found.");
                return IntPtr.Zero;
            }

            IntPtr containerClass = IL2CPP.il2cpp_object_get_class(container);
            IntPtr resolve = Il2CppRaw.GetMethod(containerClass, "Resolve", 1);
            if (resolve == IntPtr.Zero)
            {
                MelonLogger.Warning("[GameStateAccess] DiContainer.Resolve(Type) not found.");
                return IntPtr.Zero;
            }

            IntPtr instance = Il2CppRaw.InvokeObjectMethodWithObject(container, resolve, typeObj);
            if (instance == IntPtr.Zero)
                MelonLogger.Warning($"[GameStateAccess] Resolve({interfaceName}) returned null.");
            return instance;
        }
    }
}
