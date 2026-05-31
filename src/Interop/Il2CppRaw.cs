using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace NoImNotAHumanAccess.Interop
{
    /// <summary>
    /// Raw IL2CPP invocation helpers. Needed because this game's Il2CppInterop build hides game-bundled Unity
    /// package types behind an "Il2Cpp"-prefixed namespace (e.g. runtime <c>Il2CppTMPro.TMP_Text</c> vs.
    /// compile-time <c>TMPro.TMP_Text</c>) and omits all type-argument component getters at runtime. These
    /// helpers resolve classes/methods by their NATIVE names and invoke them directly, bypassing the broken
    /// managed bindings. Handles are cached. See memory: project-nimnah-il2cpp-interop.
    /// </summary>
    public static class Il2CppRaw
    {
        private static readonly Dictionary<string, IntPtr> _classCache = new();
        private static readonly Dictionary<string, IntPtr> _methodCache = new();

        /// <summary>Resolve an IL2CPP class by assembly + native namespace + name. Cached. Zero on failure.</summary>
        public static IntPtr GetClass(string assembly, string @namespace, string name)
        {
            string key = $"{assembly}|{@namespace}|{name}";
            if (_classCache.TryGetValue(key, out var c)) return c;
            IntPtr klass;
            try { klass = IL2CPP.GetIl2CppClass(assembly, @namespace, name); }
            catch (Exception e) { MelonLogger.Warning($"[Il2CppRaw] GetClass {key}: {e.Message}"); klass = IntPtr.Zero; }
            _classCache[key] = klass;
            return klass;
        }

        /// <summary>Resolve a method on a class by name + arg count. Cached. Zero on failure.</summary>
        public static IntPtr GetMethod(IntPtr klass, string name, int argc)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            string key = $"{klass}|{name}|{argc}";
            if (_methodCache.TryGetValue(key, out var m)) return m;
            IntPtr method;
            try { method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, argc); }
            catch (Exception e) { MelonLogger.Warning($"[Il2CppRaw] GetMethod {name}/{argc}: {e.Message}"); method = IntPtr.Zero; }
            _methodCache[key] = method;
            return method;
        }

        /// <summary>An IL2CPP <c>System.Type</c> object for a class, suitable as a reflection-style argument.</summary>
        public static IntPtr TypeObject(IntPtr klass) =>
            klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(klass));

        /// <summary>Pointer to a managed interop object (component/GameObject), or zero.</summary>
        public static IntPtr Ptr(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj) =>
            obj == null ? IntPtr.Zero : IL2CPP.Il2CppObjectBaseToPtr(obj);

        /// <summary>
        /// Find a component of the given IL2CPP class in <paramref name="go"/>'s subtree via
        /// <c>GameObject.GetComponentInChildren(Type, bool)</c>, invoked raw. Returns the component pointer or zero.
        /// </summary>
        public static unsafe IntPtr GetComponentInChildren(GameObject go, IntPtr componentClass, bool includeInactive = true)
        {
            if (go == null || componentClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr goClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            IntPtr method = GetMethod(goClass, "GetComponentInChildren", 2);
            if (method == IntPtr.Zero) return IntPtr.Zero;

            IntPtr typeObj = TypeObject(componentClass);
            if (typeObj == IntPtr.Zero) return IntPtr.Zero;

            bool inc = includeInactive;
            void** args = stackalloc void*[2];
            args[0] = (void*)typeObj;
            args[1] = &inc;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, Ptr(go), args, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>Overload-friendly pointer accessor for any interop object passed as a base reference.</summary>
        private static IntPtr Ptr(GameObject go)
        {
            return go == null ? IntPtr.Zero
                : IL2CPP.Il2CppObjectBaseToPtr((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)go);
        }

        /// <summary>
        /// Find a component of the given IL2CPP class ON this GameObject only (not children), via
        /// <c>GameObject.GetComponent(Type)</c>, invoked raw. Returns the component pointer or zero.
        /// </summary>
        public static unsafe IntPtr GetComponent(GameObject go, IntPtr componentClass)
        {
            if (go == null || componentClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr goClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            IntPtr method = GetMethod(goClass, "GetComponent", 1);
            if (method == IntPtr.Zero) return IntPtr.Zero;
            IntPtr typeObj = TypeObject(componentClass);
            if (typeObj == IntPtr.Zero) return IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)typeObj;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, Ptr(go), args, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>Get a Component pointer's owning GameObject pointer via Component.get_gameObject. Zero on failure.</summary>
        public static unsafe IntPtr GetComponentGameObject(IntPtr componentPtr)
        {
            if (componentPtr == IntPtr.Zero) return IntPtr.Zero;
            IntPtr compClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Component");
            IntPtr m = GetMethod(compClass, "get_gameObject", 0);
            if (m == IntPtr.Zero) return IntPtr.Zero;
            IntPtr exc = IntPtr.Zero;
            IntPtr go = IL2CPP.il2cpp_runtime_invoke(m, componentPtr, (void**)0, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : go;
        }

        /// <summary>GetComponent(Type) given a raw GameObject pointer (not a managed wrapper). Zero on failure.</summary>
        public static unsafe IntPtr GetComponentRaw(IntPtr goPtr, IntPtr componentClass)
        {
            if (goPtr == IntPtr.Zero || componentClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr goClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            IntPtr method = GetMethod(goClass, "GetComponent", 1);
            IntPtr typeObj = TypeObject(componentClass);
            if (method == IntPtr.Zero || typeObj == IntPtr.Zero) return IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)typeObj;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, goPtr, args, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>Read an object-typed instance field by name from an object pointer. Returns the field's
        /// object pointer (e.g. a component reference), or zero.</summary>
        public static unsafe IntPtr ReadObjectField(IntPtr objPtr, IntPtr klass, string fieldName)
        {
            if (objPtr == IntPtr.Zero || klass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
            if (field == IntPtr.Zero) return IntPtr.Zero;
            IntPtr value = IntPtr.Zero;
            IL2CPP.il2cpp_field_get_value(objPtr, field, (void*)(&value));
            return value;
        }

        /// <summary>
        /// Find a component of the given class on <paramref name="go"/> or any ANCESTOR, by walking the
        /// transform parent chain and calling raw <see cref="GetComponent"/> at each level. Returns zero if
        /// none. Useful when the focused object is a child (e.g. the Slider) and the logical control component
        /// sits on the row/parent.
        /// </summary>
        public static IntPtr GetComponentInParent(GameObject go, IntPtr componentClass)
        {
            if (go == null || componentClass == IntPtr.Zero) return IntPtr.Zero;
            UnityEngine.Transform? t = go.transform;
            int guard = 0;
            while (t != null && guard++ < 16)
            {
                IntPtr c = GetComponent(t.gameObject, componentClass);
                if (c != IntPtr.Zero) return c;
                t = t.parent;
            }
            return IntPtr.Zero;
        }

        /// <summary>Invoke a parameterless getter on an object pointer, returning a managed string (or null).</summary>
        public static unsafe string? InvokeStringGetter(IntPtr objPtr, IntPtr method)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return null;
            IntPtr exc = IntPtr.Zero;
            IntPtr str = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            if (exc != IntPtr.Zero || str == IntPtr.Zero) return null;
            return IL2CPP.Il2CppStringToManaged(str);
        }
    }
}
