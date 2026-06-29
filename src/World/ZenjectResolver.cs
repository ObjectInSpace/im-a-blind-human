using System;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Pulls live game services out of the Zenject DI container by interface type. The game's services are
    /// Zenject-injected interfaces backed by plain C# classes (e.g. <c>DayNightController : ASavableClass</c>), NOT
    /// MonoBehaviours, so <c>FindObjectOfType</c> can't reach them. We find the scene's <c>SceneContext</c> (a
    /// MonoBehaviour, so findable), read its <c>Container</c>, and call the NON-generic
    /// <c>DiContainer.Resolve(System.Type)</c> — the generic <c>Resolve&lt;T&gt;</c> won't JIT in this interop build
    /// (the same open-generic wall that breaks <c>GetComponents&lt;T&gt;</c>).
    ///
    /// Returns zero (and logs) on any miss; callers degrade gracefully. Used by <see cref="GameStateAccess"/>
    /// (status key) and <see cref="ActionMenu"/> (player service, to walk to the cat), so the resolve lives here once.
    /// </summary>
    public static class ZenjectResolver
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string ZenjectAsm = "Zenject.dll";

        /// <summary>
        /// Resolve a single interface instance from the live container. <paramref name="runtimeNamespace"/> +
        /// <paramref name="interfaceName"/> name the game-assembly interface (runtime namespace, no Il2Cpp prefix).
        /// Returns the instance pointer or zero.
        /// </summary>
        public static IntPtr Resolve(string runtimeNamespace, string interfaceName)
        {
            try
            {
                IntPtr sceneCtxClass = Il2CppRaw.GetClass(ZenjectAsm, "Zenject", "SceneContext");
                if (sceneCtxClass == IntPtr.Zero)
                {
                    MelonLogger.Warning("[ZenjectResolver] Zenject.SceneContext class not found.");
                    return IntPtr.Zero;
                }

                // The SceneContext GameObject is frequently INACTIVE once the container is built — so an active-only
                // find returns zero even mid-gameplay. FindObjectIncludingInactive falls back to the inactive-inclusive
                // Unity 2023+ FindAnyObjectByType, which reaches it.
                IntPtr sceneCtx = Il2CppRaw.FindObjectIncludingInactive(sceneCtxClass);
                if (sceneCtx == IntPtr.Zero)
                {
                    MelonLogger.Warning("[ZenjectResolver] No live SceneContext (not in a gameplay scene?).");
                    return IntPtr.Zero;
                }

                IntPtr getContainer = Il2CppRaw.GetMethod(IL2CPP.il2cpp_object_get_class(sceneCtx), "get_Container", 0);
                IntPtr container = Il2CppRaw.InvokeObjectGetter(sceneCtx, getContainer);
                if (container == IntPtr.Zero)
                {
                    MelonLogger.Warning("[ZenjectResolver] SceneContext.Container was null.");
                    return IntPtr.Zero;
                }

                IntPtr ifaceClass = Il2CppRaw.GetClass(GameAsm, runtimeNamespace, interfaceName);
                IntPtr typeObj = Il2CppRaw.TypeObject(ifaceClass);
                if (typeObj == IntPtr.Zero)
                {
                    MelonLogger.Warning($"[ZenjectResolver] Type object for {runtimeNamespace}.{interfaceName} not found.");
                    return IntPtr.Zero;
                }

                // DiContainer has THREE 1-arg Resolve overloads (Type / BindingId / InjectContext). Bind by SIGNATURE,
                // not arity — passing a System.Type to the BindingId/InjectContext overload segfaults natively.
                IntPtr resolve = Il2CppRaw.GetMethodBySignature(IL2CPP.il2cpp_object_get_class(container), "Resolve", "Type");
                if (resolve == IntPtr.Zero)
                {
                    MelonLogger.Warning("[ZenjectResolver] DiContainer.Resolve(Type) not found.");
                    return IntPtr.Zero;
                }

                IntPtr instance = Il2CppRaw.InvokeObjectMethodWithObject(container, resolve, typeObj);
                if (instance == IntPtr.Zero)
                    MelonLogger.Warning($"[ZenjectResolver] Resolve({interfaceName}) returned null.");
                return instance;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ZenjectResolver] Resolve({interfaceName}) threw: {e.Message}");
                return IntPtr.Zero;
            }
        }
    }
}
