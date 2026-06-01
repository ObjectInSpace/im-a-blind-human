using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.Dialogue
{
    /// <summary>
    /// Harmony hooks for the game's dialogue/narration text. There are three distinct surfaces:
    ///
    /// 1. <b>Subtitles / info popups</b> — <c>_Code.DialogSystem.SubtitlesView.UpdateText(string)</c>
    ///    (interop <c>Il2Cpp_Code.DialogSystem.SubtitlesView.UpdateText</c>). Narration/popup text sink.
    /// 2. <b>Character dialogue</b> — the Yarn Spinner <c>Yarn.Unity.LineView.RunLine(LocalizedLine, Action)</c>
    ///    (interop <c>Il2CppYarn.Unity.LineView.RunLine</c>). This is where conversation lines are presented, with
    ///    a <c>LocalizedLine</c> carrying the resolved text (<c>RawText</c>) and the speaker (<c>CharacterName</c>).
    ///    The first attempt hooked only the subtitle sink; in-game that sink never fired for ordinary dialogue, so
    ///    the Yarn line view is the load-bearing hook for the bulk of the game.
    /// 3. <b>Intro / ending narration</b> — <c>CustomYarnReader.GetNodeContent(string)</c> (interop
    ///    <c>Il2Cpp_Code.Utils.CustomYarnReading.CustomYarnReader.GetNodeContent</c>). The opening cutscene and the
    ///    endings render through <c>EndingView</c>, which does NOT push text through (1) or (2): it resolves Yarn
    ///    NODE NAMES to lines via this reader and writes them straight into an RTLTextMeshPro on a Timeline. There
    ///    is no method choke point on the view itself (async UniTask, text set inside), so we hook the reader and
    ///    speak the lines it returns. (Confirmed: the subtitle sink never fired for the intro.)
    ///
    /// Why reflection-resolved <c>MethodInfo</c> instead of typed <c>[HarmonyPatch(typeof(...))]</c>: this
    /// Il2CppInterop build puts game/package types behind the <c>Il2Cpp</c>-prefixed namespace and typed bindings
    /// are unreliable to compile against (the project's "dual-naming wall" — same reason the menu path uses raw
    /// IL2CPP). We resolve the live interop <c>MethodInfo</c> from the loaded interop assemblies and patch manually.
    /// HarmonySupport marshals a <see cref="string"/> arg directly; the <c>LocalizedLine</c> arg arrives as an
    /// interop object whose <c>RawText</c> field / <c>CharacterName</c> getter we read via raw IL2CPP.
    /// </summary>
    public static class DialoguePatches
    {
        // --- Subtitle sink ---
        private const string GameAsmName = "Assembly-CSharp";
        private const string SubtitlesViewFullName = "Il2Cpp_Code.DialogSystem.SubtitlesView";
        private const string UpdateTextMethod = "UpdateText";

        // --- Yarn line view ---
        private const string YarnAsmName = "Il2CppYarnSpinner.Unity";
        private const string LineViewFullName = "Il2CppYarn.Unity.LineView";
        private const string RunLineMethod = "RunLine";

        // --- Intro / ending narration reader ---
        private const string YarnReaderFullName = "Il2Cpp_Code.Utils.CustomYarnReading.CustomYarnReader";
        private const string GetNodeContentMethod = "GetNodeContent";

        // LocalizedLine: read text from the RawText FIELD (read by offset) and the CharacterName GETTER.
        // IL2CPP image name is the ORIGINAL assembly name + ".dll" (NOT the "Il2Cpp"-prefixed interop filename) —
        // the interop type's own static ctor resolves itself via GetIl2CppClass("YarnSpinner.Unity.dll",
        // "Yarn.Unity", "LocalizedLine"), so we must match that exactly or GetClass returns zero (silent null reads).
        private const string YarnAsmFile = "YarnSpinner.Unity.dll";
        private const string YarnRuntimeNs = "Yarn.Unity";
        private const string LocalizedLineName = "LocalizedLine";

        // The narrator the postfixes forward lines to. Static because Harmony postfixes are static; set at apply.
        private static DialogueNarrator? _narrator;

        // Cached raw-IL2CPP handles for reading LocalizedLine (resolved lazily on first dialogue line).
        private static IntPtr _localizedLineClass;
        private static IntPtr _getCharacterName;
        private static bool _llResolved;

        /// <summary>
        /// Resolve and apply both dialogue hooks. Safe to call once at init; each hook logs and is skipped on
        /// failure rather than throwing (a missing hook degrades to "less speech", not a crashed mod).
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony, DialogueNarrator narrator)
        {
            _narrator = narrator;
            PatchSubtitleSink(harmony);
            PatchYarnLineView(harmony);
            PatchYarnReader(harmony);
        }

        // ---------------- Subtitle sink: SubtitlesView.UpdateText(string) ----------------

        private static void PatchSubtitleSink(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethod(
                    GameAsmName, SubtitlesViewFullName, UpdateTextMethod, new[] { typeof(string) });
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[DialoguePatches] Could not resolve {SubtitlesViewFullName}.{UpdateTextMethod}(string); " +
                        "subtitle/popup narration disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(UpdateTextPostfix)));
                MelonLogger.Msg($"[DialoguePatches] Patched {SubtitlesViewFullName}.{UpdateTextMethod}(string).");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[DialoguePatches] PatchSubtitleSink failed: {e}");
            }
        }

        private static void UpdateTextPostfix(string text)
        {
            _narrator?.OnLine(text);
        }

        // ---------------- Yarn dialogue: LineView.RunLine(LocalizedLine, Action) ----------------

        private static void PatchYarnLineView(HarmonyLib.Harmony harmony)
        {
            try
            {
                // RunLine(LocalizedLine, Action) — resolve by name + arg count rather than exact interop param
                // types (the param types are themselves interop types we'd rather not bind at compile time).
                MethodInfo? target = ResolveMethodByArity(YarnAsmName, LineViewFullName, RunLineMethod, 2);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[DialoguePatches] Could not resolve {LineViewFullName}.{RunLineMethod}(LocalizedLine, Action); " +
                        "Yarn dialogue narration disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(RunLinePostfix)));
                MelonLogger.Msg($"[DialoguePatches] Patched {LineViewFullName}.{RunLineMethod}(LocalizedLine, Action).");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[DialoguePatches] PatchYarnLineView failed: {e}");
            }
        }

        /// <summary>
        /// Postfix on the Yarn line view. <paramref name="dialogueLine"/> is the interop <c>LocalizedLine</c>
        /// (named to match the original parameter so Harmony injects it). Read its speaker + text and speak.
        /// </summary>
        private static void RunLinePostfix(Il2CppObjectBase dialogueLine)
        {
            try
            {
                if (dialogueLine == null) return;
                IntPtr linePtr = IL2CPP.Il2CppObjectBaseToPtr(dialogueLine);
                if (linePtr == IntPtr.Zero) return;

                EnsureLocalizedLineResolved();

                string? raw = _localizedLineClass != IntPtr.Zero
                    ? Il2CppRaw.ReadStringField(linePtr, _localizedLineClass, "RawText")
                    : null;
                string? speaker = _getCharacterName != IntPtr.Zero
                    ? Il2CppRaw.InvokeStringGetter(linePtr, _getCharacterName)
                    : null;

                _narrator?.OnLine(raw, speaker);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[DialoguePatches] RunLinePostfix threw: {e.Message}");
            }
        }

        private static void EnsureLocalizedLineResolved()
        {
            if (_llResolved) return;
            _llResolved = true;
            _localizedLineClass = Il2CppRaw.GetClass(YarnAsmFile, YarnRuntimeNs, LocalizedLineName);
            _getCharacterName = Il2CppRaw.GetMethod(_localizedLineClass, "get_CharacterName", 0);
            // Diagnostic: confirm the handles resolved (zero here = wrong image/ns/name → silent empty reads).
            MelonLogger.Msg($"[DialoguePatches] LocalizedLine resolved: class={_localizedLineClass != IntPtr.Zero} " +
                            $"get_CharacterName={_getCharacterName != IntPtr.Zero}");
        }

        // ---------------- Intro/ending narration: CustomYarnReader.GetNodeContent(string) ----------------

        private static void PatchYarnReader(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethod(
                    GameAsmName, YarnReaderFullName, GetNodeContentMethod, new[] { typeof(string) });
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[DialoguePatches] Could not resolve {YarnReaderFullName}.{GetNodeContentMethod}(string); " +
                        "intro/ending narration disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(GetNodeContentPostfix)));
                MelonLogger.Msg($"[DialoguePatches] Patched {YarnReaderFullName}.{GetNodeContentMethod}(string).");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[DialoguePatches] PatchYarnReader failed: {e}");
            }
        }

        /// <summary>
        /// Postfix on the Yarn node reader used by intro/ending narration. <paramref name="__result"/> is the
        /// resolved lines for the requested node; we join and speak them. (Yarn node content carries no separate
        /// speaker — this is narration, not attributed dialogue.)
        /// </summary>
        private static void GetNodeContentPostfix(Il2CppStringArray __result)
        {
            try
            {
                if (__result == null || __result.Length == 0) return;

                // A node can resolve to several lines. The view paces them over time, but there's no per-line
                // choke point we can hook, so we get them all here at once. Speaking each with interrupt:true
                // would truncate all but the last, so join them into one block and speak once. Better a full
                // narration block up front than silence; sync with on-screen timing is sacrificed deliberately.
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < __result.Length; i++)
                {
                    string? line = __result[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(line);
                }

                if (sb.Length > 0) _narrator?.OnLine(sb.ToString());
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[DialoguePatches] GetNodeContentPostfix threw: {e.Message}");
            }
        }

        // ---------------- shared resolution helpers ----------------

        private static HarmonyMethod PostfixOf(string name) =>
            new HarmonyMethod(typeof(DialoguePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        /// <summary>Resolve an interop method by exact managed parameter types. Null on any miss.</summary>
        private static MethodInfo? ResolveMethod(string asmName, string typeFullName, string method, Type[] paramTypes)
        {
            Type? type = ResolveType(asmName, typeFullName);
            return type?.GetMethod(
                method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: paramTypes, modifiers: null);
        }

        /// <summary>Resolve an interop method by name + parameter count (for signatures whose param types are
        /// themselves interop types we don't want to bind by name). Null on miss or ambiguity.</summary>
        private static MethodInfo? ResolveMethodByArity(string asmName, string typeFullName, string method, int argc)
        {
            Type? type = ResolveType(asmName, typeFullName);
            if (type == null) return null;
            var matches = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == method && m.GetParameters().Length == argc)
                .ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length == 0)
            {
                MelonLogger.Warning($"[DialoguePatches] No {typeFullName}.{method} with {argc} args.");
                return null;
            }
            MelonLogger.Warning($"[DialoguePatches] {matches.Length} overloads of {typeFullName}.{method}/{argc}; ambiguous.");
            return null;
        }

        private static Type? ResolveType(string asmName, string typeFullName)
        {
            Assembly? asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.Ordinal));
            if (asm == null)
            {
                MelonLogger.Warning($"[DialoguePatches] Interop assembly '{asmName}' not loaded.");
                return null;
            }
            Type? type = asm.GetType(typeFullName, throwOnError: false);
            if (type == null)
                MelonLogger.Warning($"[DialoguePatches] Type '{typeFullName}' not found in {asmName}.");
            return type;
        }
    }
}
