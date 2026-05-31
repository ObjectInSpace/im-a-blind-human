using System;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Speech;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NoImNotAHumanAccess.Menus
{
    /// <summary>
    /// Speaks the currently focused UI control whenever menu selection changes. Driven by polling the active
    /// <see cref="EventSystem"/>'s <c>currentSelectedGameObject</c> from the mod's Update loop. The game drives
    /// selection through InputSystemUIInputModule -> EventSystem, so the game's own arrow/WASD/Enter/Escape/Tab
    /// navigation flows through here.
    ///
    /// Reading the label is the hard part on this IL2CPP build: a compile-time reference to
    /// <c>TMPro.TMP_Text</c> does NOT resolve at runtime (the runtime type is <c>Il2CppTMPro.TMP_Text</c>; the
    /// reference assembly is dual-named and the CLR type-load fails — verified). We therefore avoid any
    /// compile-time TMP type. We resolve the TMP_Text IL2CPP class by its NATIVE name ("TMPro","TMP_Text") to a
    /// runtime <see cref="Il2CppSystem.Type"/>, fetch the component with the non-generic
    /// <c>GetComponentInChildren(Il2CppSystem.Type, bool)</c>, and read its <c>text</c> property by reflection.
    /// All of these primitives are verified to JIT in this build.
    /// </summary>
    public sealed class MenuNarrator
    {
        private readonly ISpeechOutput _speech;
        private int _lastSelectedId;
        private string _lastSpoken = string.Empty;

        public MenuNarrator(ISpeechOutput speech) => _speech = speech;

        public void Tick()
        {
            try
            {
                EventSystem? es = EventSystem.current;
                if (es == null) { _lastSelectedId = 0; return; }

                GameObject? selected = es.currentSelectedGameObject;
                if (selected == null) { _lastSelectedId = 0; return; }

                int id = selected.GetInstanceID();
                if (id == _lastSelectedId) return;
                _lastSelectedId = id;

                string description = DescribeControl(selected);
                if (string.IsNullOrWhiteSpace(description)) return;
                if (description == _lastSpoken) return;
                _lastSpoken = description;

                _speech.Speak(description, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[MenuNarrator] Tick threw: {e.Message}");
            }
        }

        private string DescribeControl(GameObject go) => ReadLabel(go);

        /// <summary>
        /// Read the visible label: the first TMP_Text in the control's subtree, via runtime-type lookup +
        /// reflection (no compile-time TMP type). Falls back to the GameObject name (for image-only controls
        /// such as the main-menu sign items, whose name is the localization key — mapped in a later pass).
        /// </summary>
        private string ReadLabel(GameObject go)
        {
            try
            {
                string? text = RawReadTmpText(go);
                if (!string.IsNullOrWhiteSpace(text))
                    return Clean(text!);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[MenuNarrator] ReadLabel: {e.Message}");
            }
            // Fallback: GameObject name (image-only controls like the main-menu sign items, whose name is the
            // localization key — that mapping is a later pass).
            return Clean(go.name);
        }

        /// <summary>
        /// Read the first TMP_Text label in the subtree using RAW IL2CPP calls — the same technique proven to
        /// work for native speech. This sidesteps the Il2CppInterop dual-naming wall entirely:
        ///   * resolve TMP_Text's IL2CPP class by native name ("TMPro","TMP_Text"),
        ///   * resolve GameObject.GetComponentInChildren(System.Type,bool)'s IL2CPP method and get_text,
        ///   * invoke via il2cpp_runtime_invoke and marshal the result string back.
        /// Cached after first resolve. Returns null if unavailable.
        /// </summary>
        private unsafe string? RawReadTmpText(GameObject go)
        {
            if (!ResolveRawText()) return null;

            // Build the il2cpp Type object for TMP_Text to pass to GetComponentInChildren(Type, bool).
            IntPtr tmpTypeObj = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(_tmpClass));
            if (tmpTypeObj == IntPtr.Zero) return null;

            bool includeInactive = true;
            void** args = stackalloc void*[2];
            args[0] = (void*)tmpTypeObj;
            args[1] = &includeInactive;
            IntPtr exc = IntPtr.Zero;
            IntPtr goPtr = IL2CPP.Il2CppObjectBaseToPtr((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)go);
            IntPtr comp = IL2CPP.il2cpp_runtime_invoke(_gcicMethod, goPtr, args, ref exc);
            if (exc != IntPtr.Zero || comp == IntPtr.Zero) return null;

            // comp is a boxed/ref pointer to the component; runtime_invoke returns the Il2CppObject*.
            IntPtr compObj = comp;
            if (compObj == IntPtr.Zero) return null;

            IntPtr exc2 = IntPtr.Zero;
            IntPtr strPtr = IL2CPP.il2cpp_runtime_invoke(_getTextMethod, compObj, (void**)0, ref exc2);
            if (exc2 != IntPtr.Zero || strPtr == IntPtr.Zero) return null;

            return IL2CPP.Il2CppStringToManaged(strPtr);
        }

        private IntPtr _tmpClass;
        private IntPtr _gcicMethod;   // GameObject.GetComponentInChildren(Type, bool)
        private IntPtr _getTextMethod; // TMP_Text.get_text
        private bool _rawResolveTried;
        private bool _rawOk;

        private bool ResolveRawText()
        {
            if (_rawResolveTried) return _rawOk;
            _rawResolveTried = true;
            try
            {
                _tmpClass = IL2CPP.GetIl2CppClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");
                if (_tmpClass == IntPtr.Zero) { MelonLogger.Warning("[MenuNarrator] raw: TMP_Text class not found."); return false; }

                IntPtr goClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
                _gcicMethod = IL2CPP.il2cpp_class_get_method_from_name(goClass, "GetComponentInChildren", 2);
                _getTextMethod = IL2CPP.il2cpp_class_get_method_from_name(_tmpClass, "get_text", 0);

                _rawOk = _gcicMethod != IntPtr.Zero && _getTextMethod != IntPtr.Zero;
                MelonLogger.Msg($"[MenuNarrator] raw resolve: tmpClass={_tmpClass != IntPtr.Zero} gcic={_gcicMethod != IntPtr.Zero} getText={_getTextMethod != IntPtr.Zero}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[MenuNarrator] ResolveRawText: {e.Message}");
                _rawOk = false;
            }
            return _rawOk;
        }

        /// <summary>Strip rich-text tags and collapse whitespace for clean speech.</summary>
        private static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new System.Text.StringBuilder(raw.Length);
            bool inTag = false;
            foreach (char c in raw)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Replace('\n', ' ').Replace("  ", " ").Trim();
        }
    }
}
