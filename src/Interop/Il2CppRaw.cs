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

        /// <summary>
        /// Whether <paramref name="objPtr"/>'s runtime class is EXACTLY <paramref name="klass"/> (not a subclass). Reads
        /// the object's class via <c>il2cpp_object_get_class</c> and compares pointers. Use to tell concrete sibling
        /// types apart at runtime when you only hold a base-class pointer (e.g. DoorTrigger vs WindowBlindsTrigger, both
        /// AActionableObjectView). False on any zero/failure.
        /// </summary>
        public static bool IsExactClass(IntPtr objPtr, IntPtr klass)
        {
            if (objPtr == IntPtr.Zero || klass == IntPtr.Zero) return false;
            try { return IL2CPP.il2cpp_object_get_class(objPtr) == klass; }
            catch { return false; }
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

        /// <summary>
        /// Resolve a method on a class by name AND parameter signature, when several overloads share the same name +
        /// arity and <see cref="GetMethod"/>'s name+argc lookup would bind the wrong one. <paramref name="paramTypeNames"/>
        /// are the simple class names of each parameter, in order (e.g. <c>"Type"</c> for a <c>System.Type</c> arg). The
        /// match requires the param count to equal the list length and each parameter's class name to match. Cached by
        /// name + joined signature. Zero on failure.
        ///
        /// Motivating case: Zenject's <c>DiContainer</c> has THREE 1-arg <c>Resolve</c> overloads —
        /// <c>Resolve(Type)</c>, <c>Resolve(BindingId)</c>, <c>Resolve(InjectContext)</c>. Binding by arity alone can
        /// pick a non-Type overload; passing a <c>System.Type</c> to it dereferences the wrong native struct and the
        /// process segfaults (uncatchable in managed code). This finder pins the exact <c>Resolve(Type)</c>.
        /// </summary>
        public static unsafe IntPtr GetMethodBySignature(IntPtr klass, string name, params string[] paramTypeNames)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            string key = $"{klass}|{name}|sig:{string.Join(",", paramTypeNames)}";
            if (_methodCache.TryGetValue(key, out var cached)) return cached;

            IntPtr found = IntPtr.Zero;
            try
            {
                IntPtr iter = IntPtr.Zero;
                IntPtr method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    IntPtr namePtr = IL2CPP.il2cpp_method_get_name(method);
                    string? mName = namePtr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(namePtr);
                    if (mName != name) continue;
                    if ((int)IL2CPP.il2cpp_method_get_param_count(method) != paramTypeNames.Length) continue;

                    bool allMatch = true;
                    for (uint i = 0; i < (uint)paramTypeNames.Length; i++)
                    {
                        IntPtr pType = IL2CPP.il2cpp_method_get_param(method, i);
                        IntPtr pClass = pType == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_type_get_class_or_element_class(pType);
                        IntPtr pNamePtr = pClass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_name(pClass);
                        string? pName = pNamePtr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(pNamePtr);
                        if (pName != paramTypeNames[i]) { allMatch = false; break; }
                    }
                    if (allMatch) { found = method; break; }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Il2CppRaw] GetMethodBySignature {name}({string.Join(",", paramTypeNames)}): {e.Message}");
                found = IntPtr.Zero;
            }

            _methodCache[key] = found;
            return found;
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

        /// <summary>Read a 32-bit value-type instance field (int, or an int-backed enum) by name from an object
        /// pointer, by offset. Returns <paramref name="fallback"/> if the object/class/field is missing.</summary>
        public static unsafe int ReadInt32Field(IntPtr objPtr, IntPtr klass, string fieldName, int fallback = -1)
        {
            if (objPtr == IntPtr.Zero || klass == IntPtr.Zero) return fallback;
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
            if (field == IntPtr.Zero) return fallback;
            int value = 0;
            IL2CPP.il2cpp_field_get_value(objPtr, field, (void*)(&value));
            return value;
        }

        /// <summary>Read a 32-bit float instance field by name from an object pointer, by offset. Returns
        /// <paramref name="fallback"/> if the object/class/field is missing.</summary>
        public static unsafe float ReadFloatField(IntPtr objPtr, IntPtr klass, string fieldName, float fallback = 0f)
        {
            if (objPtr == IntPtr.Zero || klass == IntPtr.Zero) return fallback;
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
            if (field == IntPtr.Zero) return fallback;
            float value = 0f;
            IL2CPP.il2cpp_field_get_value(objPtr, field, (void*)(&value));
            return value;
        }

        /// <summary>Read a 1-byte bool instance field by name from an object pointer, by offset. Returns
        /// <paramref name="fallback"/> if the object/class/field is missing.</summary>
        public static unsafe bool ReadBoolField(IntPtr objPtr, IntPtr klass, string fieldName, bool fallback = false)
        {
            if (objPtr == IntPtr.Zero || klass == IntPtr.Zero) return fallback;
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
            if (field == IntPtr.Zero) return fallback;
            bool value = false;
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

        /// <summary>Read a <see cref="string"/>-typed instance field by name from an object pointer, marshaled to
        /// managed. Null if the object/class/field is missing or the field is null. Use for IL2CPP string FIELDS
        /// (read by offset), as opposed to <see cref="InvokeStringGetter"/> for string getter METHODS.</summary>
        public static unsafe string? ReadStringField(IntPtr objPtr, IntPtr klass, string fieldName)
        {
            IntPtr strPtr = ReadObjectField(objPtr, klass, fieldName);
            return strPtr == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(strPtr);
        }

        /// <summary>Read a <c>string[]</c>-typed instance field by name (e.g. Yarn <c>LocalizedLine.Substitutions</c>),
        /// marshaling each element to managed. Empty array if the object/class/field is missing or the field is null.</summary>
        public static string[] ReadStringArrayField(IntPtr objPtr, IntPtr klass, string fieldName)
        {
            IntPtr arrayPtr = ReadObjectField(objPtr, klass, fieldName);
            if (arrayPtr == IntPtr.Zero) return Array.Empty<string>();
            IntPtr[] elems = ReadObjectArray(arrayPtr);
            if (elems.Length == 0) return Array.Empty<string>();
            var result = new string[elems.Length];
            for (int i = 0; i < elems.Length; i++)
                result[i] = elems[i] == IntPtr.Zero ? string.Empty : (IL2CPP.Il2CppStringToManaged(elems[i]) ?? string.Empty);
            return result;
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

        /// <summary>Invoke a parameterless getter returning a 32-bit value (int, or an int-backed enum) on an object
        /// pointer. Returns <paramref name="fallback"/> on any failure. Use for interface/property getters like
        /// <c>get_Day</c>/<c>get_DayActions</c> where there is no directly-readable backing field by name.</summary>
        public static unsafe int InvokeInt32Getter(IntPtr objPtr, IntPtr method, int fallback = -1)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return fallback;
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero) return fallback;
            // Value-type returns come back boxed; the unboxed payload sits one object header in.
            return *(int*)IL2CPP.il2cpp_object_unbox(boxed);
        }

        /// <summary>Invoke a method taking a single value-type-by-int argument and returning a 32-bit value (e.g.
        /// <c>IConsumablesController.Count(EConsumable)</c>, where the enum is passed as its underlying int).
        /// Returns <paramref name="fallback"/> on failure.</summary>
        public static unsafe int InvokeInt32MethodWithEnum(IntPtr objPtr, IntPtr method, int enumValue, int fallback = -1)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return fallback;
            int arg = enumValue;
            void** args = stackalloc void*[1];
            args[0] = &arg;
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero) return fallback;
            return *(int*)IL2CPP.il2cpp_object_unbox(boxed);
        }

        /// <summary>Invoke <c>EventSystem.SetSelectedGameObject(GameObject)</c> (or any void instance method taking a
        /// single object-pointer arg) — passes the GameObject pointer through. Returns true if it ran without throwing.</summary>
        public static unsafe bool SetSelectedGameObject(IntPtr eventSystemPtr, IntPtr method, IntPtr goPtr)
        {
            if (eventSystemPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            void** args = stackalloc void*[1];
            args[0] = (void*)goPtr;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, eventSystemPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke a method taking a single value-type-by-int argument (an enum passed as its underlying int)
        /// and returning an object pointer — e.g. <c>ICharactersManager.GetCharacter(ECharacterType)</c> returning a
        /// <c>CharacterSOData</c>. Returns zero on failure or thrown exception.</summary>
        public static unsafe IntPtr InvokeObjectMethodWithEnum(IntPtr objPtr, IntPtr method, int enumValue)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return IntPtr.Zero;
            int arg = enumValue;
            void** args = stackalloc void*[1];
            args[0] = &arg;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>Invoke a method taking one object (reference) argument and returning an object pointer (e.g.
        /// Zenject <c>DiContainer.Resolve(System.Type)</c>). Returns zero on failure or thrown exception.</summary>
        public static unsafe IntPtr InvokeObjectMethodWithObject(IntPtr objPtr, IntPtr method, IntPtr arg)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)arg;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>Invoke a parameterless instance method that returns nothing (void), e.g. a private
        /// <c>Act()</c> / <c>Click()</c>. Returns true if it ran without throwing; false if the object/method was
        /// missing or the IL2CPP call raised an exception (the managed exception pointer is non-zero).</summary>
        public static unsafe bool InvokeVoid(IntPtr objPtr, IntPtr method)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>
        /// Like <see cref="InvokeVoid"/> but, when the IL2CPP call raises a managed exception, decodes it to a string
        /// (type + message + stack) via <see cref="Il2CppException"/> and returns it in <paramref name="error"/>. Use to
        /// see WHY a cold-invoked method (e.g. RadioInteractable.Interact()) throws, instead of only knowing that it did.
        /// Returns true on clean run (error null); false on throw (error populated) or a zero arg.
        /// </summary>
        public static unsafe bool TryInvokeVoid(IntPtr objPtr, IntPtr method, out string? error)
        {
            error = null;
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) { error = "null object or method"; return false; }
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            if (exc == IntPtr.Zero) return true;
            try { error = new Il2CppException(exc).ToString(); }
            catch (Exception e) { error = $"<exception, undecodable: {e.Message}>"; }
            return false;
        }

        /// <summary>Invoke a void instance method taking a single <see cref="float"/> argument (e.g. the radio knob's
        /// private <c>RotateKnob(float delta)</c>). Returns true if it ran without throwing.</summary>
        public static unsafe bool InvokeVoidWithFloat(IntPtr objPtr, IntPtr method, float arg)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            float a = arg;
            void** args = stackalloc void*[1];
            args[0] = &a;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke a void instance method taking a single value-type-by-int argument — an enum passed as its
        /// underlying int (e.g. <c>RadioModel.SwitchState(ERadioState)</c>). Returns true if it ran without throwing.</summary>
        public static unsafe bool InvokeVoidWithEnum(IntPtr objPtr, IntPtr method, int enumValue)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            int a = enumValue;
            void** args = stackalloc void*[1];
            args[0] = &a;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke a void instance method taking a single object-reference argument (e.g. a uGUI pointer
        /// handler's <c>OnPointerClick(PointerEventData)</c>). The arg may be zero (null). Returns true if it ran
        /// without throwing.</summary>
        public static unsafe bool InvokeVoidWithObject(IntPtr objPtr, IntPtr method, IntPtr arg)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            void** args = stackalloc void*[1];
            args[0] = (void*)arg;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke an instance method taking a <see cref="Vector3"/> (by value) plus a <see cref="float"/>
        /// argument (e.g. <c>IPlayerService.MoveXZ(Vector3 standingPos, float speed)</c>). The return (a UniTask
        /// struct) is ignored — fire-and-forget. Returns true if it ran without throwing.</summary>
        public static unsafe bool InvokeWithVector3Float(IntPtr objPtr, IntPtr method, Vector3 v, float f)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            Vector3 vv = v; float ff = f;
            void** args = stackalloc void*[2];
            args[0] = &vv;
            args[1] = &ff;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke a void instance method taking a single bool argument (e.g. a property setter
        /// <c>set_IsTargeted(bool)</c>). Returns true if it ran without throwing.</summary>
        public static unsafe bool InvokeVoidWithBool(IntPtr objPtr, IntPtr method, bool arg)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return false;
            bool a = arg;
            void** args = stackalloc void*[1];
            args[0] = &a;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke a parameterless getter returning a <see cref="float"/> by value (e.g. the radio knob's
        /// <c>get_Value</c>, <c>RadioModel.get_NormalisedDistance</c>). Returns <paramref name="fallback"/> on failure.</summary>
        public static unsafe float InvokeFloatGetter(IntPtr objPtr, IntPtr method, float fallback = 0f)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return fallback;
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero) return fallback;
            return *(float*)IL2CPP.il2cpp_object_unbox(boxed);
        }

        /// <summary>Invoke a STATIC parameterless getter returning an object pointer (e.g. a singleton
        /// <c>get_Instance</c> or <c>Camera.get_main</c>). Returns zero on failure.</summary>
        public static unsafe IntPtr InvokeStaticObjectGetter(IntPtr method)
        {
            if (method == IntPtr.Zero) return IntPtr.Zero;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, (void**)0, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>
        /// Construct a <c>UnityEngine.EventSystems.PointerEventData</c> bound to the current EventSystem, for feeding
        /// a uGUI pointer handler we invoke manually (e.g. <c>IPointerClickHandler.OnPointerClick(PointerEventData)</c>
        /// on a mouse-only button). Allocates the object and runs its 1-arg <c>.ctor(EventSystem)</c>. Returns zero on
        /// failure (no EventSystem, ctor unresolved, or the ctor threw).
        /// </summary>
        public static unsafe IntPtr NewPointerEventData()
        {
            // EventSystem.current (static getter).
            IntPtr esClass = GetClass("UnityEngine.UI.dll", "UnityEngine.EventSystems", "EventSystem");
            if (esClass == IntPtr.Zero) esClass = GetClass("UnityEngine.UIModule.dll", "UnityEngine.EventSystems", "EventSystem");
            IntPtr es = esClass == IntPtr.Zero ? IntPtr.Zero : InvokeStaticObjectGetter(GetMethod(esClass, "get_current", 0));
            if (es == IntPtr.Zero) return IntPtr.Zero;

            IntPtr pedClass = GetClass("UnityEngine.UI.dll", "UnityEngine.EventSystems", "PointerEventData");
            if (pedClass == IntPtr.Zero) pedClass = GetClass("UnityEngine.UIModule.dll", "UnityEngine.EventSystems", "PointerEventData");
            if (pedClass == IntPtr.Zero) return IntPtr.Zero;

            IntPtr ctor = GetMethod(pedClass, ".ctor", 1);
            if (ctor == IntPtr.Zero) return IntPtr.Zero;

            IntPtr obj = IL2CPP.il2cpp_object_new(pedClass);
            if (obj == IntPtr.Zero) return IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)es;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(ctor, obj, args, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : obj;
        }

        /// <summary>The main camera object pointer (<c>Camera.get_main</c>), or zero. For WorldToScreenPoint when a
        /// more specific hover camera isn't available.</summary>
        public static IntPtr MainCameraPtr()
        {
            IntPtr camClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Camera");
            return InvokeStaticObjectGetter(GetMethod(camClass, "get_main", 0));
        }

        /// <summary>Invoke a parameterless getter returning an object pointer (e.g. <c>SceneContext.get_Container</c>).
        /// Returns zero on failure.</summary>
        public static unsafe IntPtr InvokeObjectGetter(IntPtr objPtr, IntPtr method)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return IntPtr.Zero;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>
        /// Read an IL2CPP reference array's elements as object pointers, via Il2CppInterop's
        /// <c>Il2CppReferenceArray</c> wrapper (which knows the correct element layout). Empty if the pointer is zero.
        /// Used for getters that return <c>T[]</c> (e.g. <c>ActionableObjectViews</c>).
        /// </summary>
        public static IntPtr[] ReadObjectArray(IntPtr arrayPtr)
        {
            if (arrayPtr == IntPtr.Zero) return Array.Empty<IntPtr>();
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<
                Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase>(arrayPtr);
            int len = arr.Length;
            if (len <= 0) return Array.Empty<IntPtr>();
            var result = new IntPtr[len];
            for (int i = 0; i < len; i++)
            {
                var e = arr[i];
                result[i] = e == null ? IntPtr.Zero : IL2CPP.Il2CppObjectBaseToPtr(e);
            }
            return result;
        }

        /// <summary>Read the displayed text of a TMP_Text-typed instance FIELD (e.g. a view's <c>_name</c> /
        /// <c>_narrativeDescription</c> RTLTextMeshPro field): read the field object, then invoke <c>TMP_Text.get_text</c>
        /// on it (RTLTextMeshPro derives from TMP_Text, so the getter binds). Null if anything is missing.</summary>
        public static string? ReadTmpFieldText(IntPtr objPtr, IntPtr objClass, string fieldName)
        {
            IntPtr tmp = ReadObjectField(objPtr, objClass, fieldName);
            if (tmp == IntPtr.Zero) return null;
            IntPtr tmpClass = GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");
            IntPtr getText = GetMethod(tmpClass, "get_text", 0);
            return InvokeStringGetter(tmp, getText);
        }

        /// <summary>Whether a Component's owning GameObject is active in the hierarchy
        /// (<c>Component.get_gameObject</c> → <c>GameObject.get_activeInHierarchy</c>), raw. False on failure.</summary>
        public static bool GetComponentGameObjectActive(IntPtr componentPtr)
        {
            IntPtr go = GetComponentGameObject(componentPtr);
            return GetGameObjectActiveInHierarchy(go);
        }

        /// <summary>Whether a GameObject pointer is active in the hierarchy (<c>GameObject.get_activeInHierarchy</c>),
        /// raw. False on failure. Use when you already hold a GameObject (not a Component).</summary>
        public static bool GetGameObjectActiveInHierarchy(IntPtr goPtr)
        {
            if (goPtr == IntPtr.Zero) return false;
            IntPtr goClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            IntPtr m = GetMethod(goClass, "get_activeInHierarchy", 0);
            return InvokeBoolGetter(goPtr, m);
        }

        /// <summary>Read a UnityEngine.Object's <c>name</c> (via <c>get_name</c>) from an object pointer, raw.
        /// Null on failure. Works for any Component/GameObject pointer.</summary>
        public static string? GetUnityObjectName(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero) return null;
            IntPtr objClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            IntPtr getName = GetMethod(objClass, "get_name", 0);
            return InvokeStringGetter(objPtr, getName);
        }

        /// <summary>Get a GameObject pointer's Transform pointer (<c>GameObject.get_transform</c>), raw. Zero on
        /// failure.</summary>
        public static IntPtr GetGameObjectTransform(IntPtr goPtr)
        {
            if (goPtr == IntPtr.Zero) return IntPtr.Zero;
            IntPtr goClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            return InvokeObjectGetter(goPtr, GetMethod(goClass, "get_transform", 0));
        }

        /// <summary>Rotate a Transform to face a world point via <c>Transform.LookAt(Vector3)</c>, raw. Returns true if
        /// it ran without throwing. The 1-arg overload uses world-up. Use to aim the camera target at an object so the
        /// game's own raycaster (which casts along the camera forward) hits it.</summary>
        public static unsafe bool TransformLookAt(IntPtr transformPtr, Vector3 worldPoint)
        {
            if (transformPtr == IntPtr.Zero) return false;
            IntPtr trClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Transform");
            IntPtr m = GetMethod(trClass, "LookAt", 1);
            if (m == IntPtr.Zero) return false;
            Vector3 p = worldPoint;
            void** args = stackalloc void*[1];
            args[0] = &p;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(m, transformPtr, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Read a Component's world position: <c>Component.get_transform()</c> then
        /// <c>Transform.get_position()</c>, raw. Returns <c>Vector3.zero</c> on failure.</summary>
        public static Vector3 GetComponentWorldPosition(IntPtr componentPtr)
        {
            if (componentPtr == IntPtr.Zero) return Vector3.zero;
            IntPtr compClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Component");
            IntPtr getTransform = GetMethod(compClass, "get_transform", 0);
            IntPtr transform = InvokeObjectGetter(componentPtr, getTransform);
            if (transform == IntPtr.Zero) return Vector3.zero;
            IntPtr trClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Transform");
            IntPtr getPosition = GetMethod(trClass, "get_position", 0);
            return InvokeVector3Getter(transform, getPosition);
        }

        /// <summary>
        /// Convert a world point to screen-space (pixels) using the given <c>Camera</c> pointer's
        /// <c>WorldToScreenPoint(Vector3)</c>, raw. Returns <c>Vector3.zero</c> on failure. (x,y) are screen pixels,
        /// z is distance in front of the camera. Use the room photo's hover camera (UIRayCaster.Instance._camera).
        /// </summary>
        public static unsafe Vector3 WorldToScreenPoint(IntPtr cameraPtr, Vector3 world)
        {
            if (cameraPtr == IntPtr.Zero) return Vector3.zero;
            IntPtr camClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Camera");
            IntPtr m = GetMethod(camClass, "WorldToScreenPoint", 1);
            if (m == IntPtr.Zero) return Vector3.zero;
            Vector3 arg = world;
            void** args = stackalloc void*[1];
            args[0] = &arg;
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(m, cameraPtr, args, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero) return Vector3.zero;
            float* f = (float*)IL2CPP.il2cpp_object_unbox(boxed);
            return new Vector3(f[0], f[1], f[2]);
        }

        /// <summary>
        /// Warp the on-screen mouse pointer to a screen-space position (pixels, origin bottom-left) via the Input
        /// System's <c>Mouse.current.WarpCursorPosition(Vector2)</c>. The game reads input through the Input System,
        /// so this is the warp most likely to feed its own UI raycast. Returns true if the call ran. No-op (false) if
        /// the Input System / current Mouse isn't present.
        /// </summary>
        public static unsafe bool WarpMouse(Vector2 screenPos)
        {
            IntPtr mouseClass = GetClass("Unity.InputSystem.dll", "UnityEngine.InputSystem", "Mouse");
            if (mouseClass == IntPtr.Zero) return false;
            // Mouse.current is a STATIC getter — invoke with a null instance.
            IntPtr getCurrent = GetMethod(mouseClass, "get_current", 0);
            if (getCurrent == IntPtr.Zero) return false;
            IntPtr excCur = IntPtr.Zero;
            IntPtr mouse = IL2CPP.il2cpp_runtime_invoke(getCurrent, IntPtr.Zero, (void**)0, ref excCur);
            if (excCur != IntPtr.Zero || mouse == IntPtr.Zero) return false;
            IntPtr warp = GetMethod(mouseClass, "WarpCursorPosition", 1);
            if (warp == IntPtr.Zero) return false;
            Vector2 arg = screenPos;
            void** args = stackalloc void*[1];
            args[0] = &arg;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(warp, mouse, args, ref exc);
            return exc == IntPtr.Zero;
        }

        /// <summary>Invoke a parameterless bool getter on an object pointer. Returns <paramref name="fallback"/> on
        /// failure (treats a thrown getter as the fallback).</summary>
        public static unsafe bool InvokeBoolGetter(IntPtr objPtr, IntPtr method, bool fallback = false)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return fallback;
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero) return fallback;
            return *(bool*)IL2CPP.il2cpp_object_unbox(boxed);
        }

        /// <summary>Invoke a parameterless getter returning a <see cref="Vector3"/> by value (e.g.
        /// <c>IPlayerService.get_Position</c>, <c>Transform.get_position</c>). Returns <c>Vector3.zero</c> on failure.
        /// The boxed return is unboxed and read as three floats.</summary>
        public static unsafe Vector3 InvokeVector3Getter(IntPtr objPtr, IntPtr method)
        {
            if (objPtr == IntPtr.Zero || method == IntPtr.Zero) return Vector3.zero;
            IntPtr exc = IntPtr.Zero;
            IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)0, ref exc);
            if (exc != IntPtr.Zero || boxed == IntPtr.Zero) return Vector3.zero;
            float* f = (float*)IL2CPP.il2cpp_object_unbox(boxed);
            return new Vector3(f[0], f[1], f[2]);
        }

        /// <summary>
        /// World position of the main camera (<c>Camera.main.transform.position</c>), via raw static
        /// <c>Camera.get_main()</c> then <c>Component.get_transform</c> → <c>Transform.get_position</c>. In a
        /// first-person game the camera tracks the player, so this is a Zenject-free proxy for the player position.
        /// Returns <c>Vector3.zero</c> on failure (no main camera tagged, etc.).
        /// </summary>
        public static unsafe Vector3 GetMainCameraPosition()
        {
            IntPtr camClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Camera");
            IntPtr getMain = GetMethod(camClass, "get_main", 0);
            if (getMain == IntPtr.Zero) return Vector3.zero;
            IntPtr exc = IntPtr.Zero;
            IntPtr cam = IL2CPP.il2cpp_runtime_invoke(getMain, IntPtr.Zero, (void**)0, ref exc); // static
            if (exc != IntPtr.Zero || cam == IntPtr.Zero) return Vector3.zero;
            return GetComponentWorldPosition(cam);
        }

        /// <summary>
        /// Count live objects of an IL2CPP class via <c>UnityEngine.Object.FindObjectsByType(Type,
        /// FindObjectsInactive, FindObjectsSortMode)</c> (the non-deprecated Unity 2023+ API), returning the result
        /// array length. Diagnostic helper: confirms whether instances of a type exist in the scene at all,
        /// independent of any provider/registry. Returns -1 on failure.
        /// </summary>
        public static int CountObjectsByType(IntPtr klass, bool includeInactive = true) =>
            FindObjectsByType(klass, includeInactive).Length;

        /// <summary>
        /// Return all live objects of an IL2CPP class as object pointers, via
        /// <c>UnityEngine.Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)</c> (the
        /// non-deprecated Unity 2023+ API). Empty array on failure. Use to enumerate a scene type directly when there
        /// is no usable provider/registry (e.g. the room-photo <c>UIButton</c> set).
        /// </summary>
        public static unsafe IntPtr[] FindObjectsByType(IntPtr klass, bool includeInactive = true)
        {
            if (klass == IntPtr.Zero) return Array.Empty<IntPtr>();
            IntPtr objClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            IntPtr method = GetMethod(objClass, "FindObjectsByType", 3);
            if (method == IntPtr.Zero) return Array.Empty<IntPtr>();
            IntPtr typeObj = TypeObject(klass);
            if (typeObj == IntPtr.Zero) return Array.Empty<IntPtr>();
            int findInactive = includeInactive ? 1 : 0; // FindObjectsInactive.Include / .Exclude
            int sortMode = 0;                            // FindObjectsSortMode.None
            void** args = stackalloc void*[3];
            args[0] = (void*)typeObj;
            args[1] = &findInactive;
            args[2] = &sortMode;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, args, ref exc); // static
            if (exc != IntPtr.Zero || result == IntPtr.Zero) return Array.Empty<IntPtr>();
            return ReadObjectArray(result);
        }

        /// <summary>
        /// Find the first live object of an IL2CPP class via <c>UnityEngine.Object.FindObjectOfType(Type)</c>,
        /// invoked raw. Only works for UnityEngine.Object-derived classes (MonoBehaviour/Component) — the Zenject
        /// <c>SceneContext</c> qualifies. Returns the object pointer or zero.
        /// </summary>
        public static unsafe IntPtr FindObjectOfType(IntPtr klass)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr objClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            // FindObjectOfType(Type) is the 1-arg overload (the 2-arg one adds includeInactive in newer Unity).
            IntPtr method = GetMethod(objClass, "FindObjectOfType", 1);
            if (method == IntPtr.Zero) return IntPtr.Zero;
            IntPtr typeObj = TypeObject(klass);
            if (typeObj == IntPtr.Zero) return IntPtr.Zero;
            void** args = stackalloc void*[1];
            args[0] = (void*)typeObj;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, args, ref exc); // static
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>
        /// Find the first live object of an IL2CPP class via <c>UnityEngine.Object.FindAnyObjectByType(Type,
        /// FindObjectsInactive)</c>, the non-deprecated Unity 2023+ API. Unlike the legacy <see cref="FindObjectOfType"/>,
        /// this can include INACTIVE objects (<paramref name="includeInactive"/>), which matters for Zenject's
        /// <c>SceneContext</c> — its GameObject is frequently inactive after the container has been built, so the legacy
        /// active-only find returns zero. Returns the object pointer or zero.
        /// </summary>
        public static unsafe IntPtr FindAnyObjectByType(IntPtr klass, bool includeInactive = true)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr objClass = GetClass("UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            // FindAnyObjectByType(Type, FindObjectsInactive) is the 2-arg overload. FindObjectsInactive is an enum:
            // Exclude=0, Include=1 (its underlying int is what we pass).
            IntPtr method = GetMethod(objClass, "FindAnyObjectByType", 2);
            if (method == IntPtr.Zero) return IntPtr.Zero;
            IntPtr typeObj = TypeObject(klass);
            if (typeObj == IntPtr.Zero) return IntPtr.Zero;
            int findInactive = includeInactive ? 1 : 0; // FindObjectsInactive.Include / .Exclude
            void** args = stackalloc void*[2];
            args[0] = (void*)typeObj;
            args[1] = &findInactive;
            IntPtr exc = IntPtr.Zero;
            IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, args, ref exc); // static
            return exc != IntPtr.Zero ? IntPtr.Zero : result;
        }

        /// <summary>
        /// Find the first live object of an IL2CPP class, active or not: tries active-only
        /// <see cref="FindObjectOfType"/> first, then falls back to inactive-inclusive <see cref="FindAnyObjectByType"/>.
        /// This is the pattern nearly every lookup in the mod needs — the game's views/providers/SceneContext are
        /// frequently INACTIVE in the hierarchy (container built, panel hidden), so active-only find returns zero even
        /// when the object is live. Returns the object pointer or zero. <paramref name="klass"/>-zero returns zero.
        /// </summary>
        public static IntPtr FindObjectIncludingInactive(IntPtr klass)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr obj = FindObjectOfType(klass);
            return obj != IntPtr.Zero ? obj : FindAnyObjectByType(klass, includeInactive: true);
        }
    }
}
