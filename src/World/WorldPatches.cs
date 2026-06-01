using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Harmony hooks for the first-person world HUD — currently the interaction prompt.
    ///
    /// <b>Interaction prompt</b> — <c>_Code.Menues.HUD.HUDPresenter.ShowHint(string subject, string action,
    /// Transform target, ERaycastHintIcon icon)</c> (interop <c>Il2Cpp_Code.Menues.HUD.HUDPresenter</c>; note the
    /// game's spelling "Menues"). This is the single sink where the game presents "press [action] to [subject]"
    /// after the camera raycast (<c>RaycastSource.Target</c>) lands on a <c>RaycastTargetHint</c> with satisfied
    /// conditions. The two strings arrive already resolved through Unity Localization, so a postfix here gets clean
    /// display text with no localization work — the same shape as the dialogue <c>UpdateText</c> sink.
    /// <c>HideHint()</c> is hooked too, only to reset the narrator's dedupe so re-targeting the same object re-speaks.
    ///
    /// Why reflection-resolved <c>MethodInfo</c> rather than typed <c>[HarmonyPatch(typeof(...))]</c>: same
    /// "dual-naming wall" as <see cref="Dialogue.DialoguePatches"/> — game types live behind the <c>Il2Cpp</c>-prefixed
    /// interop namespace and typed bindings are unreliable to compile against. We resolve the live interop
    /// <c>MethodInfo</c> from the loaded assemblies and patch manually. HarmonySupport marshals the two
    /// <see cref="string"/> args directly; the <c>Transform</c>/<c>ERaycastHintIcon</c> args (interop types we'd
    /// rather not bind by name) are simply not declared on the postfix, so Harmony skips injecting them. We resolve
    /// <c>ShowHint</c> by name + arity for the same reason.
    /// </summary>
    public static class WorldPatches
    {
        private const string GameAsmName = "Assembly-CSharp";
        private const string HudPresenterFullName = "Il2Cpp_Code.Menues.HUD.HUDPresenter";
        private const string ShowHintMethod = "ShowHint";
        private const string HideHintMethod = "HideHint";

        private static HudNarrator? _narrator;

        /// <summary>
        /// Resolve and apply the world-HUD hooks. Safe to call once at init; each hook logs and is skipped on
        /// failure rather than throwing (a missing hook degrades to "less speech", not a crashed mod).
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony, HudNarrator narrator)
        {
            _narrator = narrator;
            PatchShowHint(harmony);
            PatchHideHint(harmony);
        }

        // ---------------- Interaction prompt: HUDPresenter.ShowHint(string, string, Transform, ERaycastHintIcon) ----

        private static void PatchShowHint(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, HudPresenterFullName, ShowHintMethod, 4);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {HudPresenterFullName}.{ShowHintMethod}(subject, action, " +
                        "target, icon); interaction-prompt narration disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(ShowHintPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {HudPresenterFullName}.{ShowHintMethod}(subject, action, target, icon).");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchShowHint failed: {e}");
            }
        }

        /// <summary>
        /// Postfix on the interaction-prompt sink. Parameter names match the original (<c>subject</c>,
        /// <c>action</c>, <c>icon</c>) so Harmony injects them; the original's <c>target</c> Transform is
        /// intentionally not declared. <c>icon</c> is the <c>ERaycastHintIcon</c> enum, taken as its underlying
        /// <see cref="int"/> so we don't bind the interop enum type (None=0, Energy=1, EnergyX2=2, AllEnergy=3,
        /// Save=4); the narrator maps it to a spoken energy-cost suffix.
        /// </summary>
        private static void ShowHintPostfix(string subject, string action, int icon)
        {
            _narrator?.OnHint(subject, action, icon);
        }

        // ---------------- Prompt hidden: HUDPresenter.HideHint() ----------------

        private static void PatchHideHint(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, HudPresenterFullName, HideHintMethod, 0);
                if (target == null)
                {
                    // Non-fatal: without this the dedupe just won't reset on look-away, so re-targeting the same
                    // object stays silent until a different prompt intervenes. The prompt itself still speaks.
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {HudPresenterFullName}.{HideHintMethod}(); " +
                        "prompt-dedupe reset disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(HideHintPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {HudPresenterFullName}.{HideHintMethod}().");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchHideHint failed: {e}");
            }
        }

        private static void HideHintPostfix()
        {
            _narrator?.OnHintHidden();
        }

        // ---------------- shared resolution helpers ----------------

        private static HarmonyMethod PostfixOf(string name) =>
            new HarmonyMethod(typeof(WorldPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

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
                MelonLogger.Warning($"[WorldPatches] No {typeFullName}.{method} with {argc} args.");
                return null;
            }
            MelonLogger.Warning($"[WorldPatches] {matches.Length} overloads of {typeFullName}.{method}/{argc}; ambiguous.");
            return null;
        }

        private static Type? ResolveType(string asmName, string typeFullName)
        {
            Assembly? asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.Ordinal));
            if (asm == null)
            {
                MelonLogger.Warning($"[WorldPatches] Interop assembly '{asmName}' not loaded.");
                return null;
            }
            Type? type = asm.GetType(typeFullName, throwOnError: false);
            if (type == null)
                MelonLogger.Warning($"[WorldPatches] Type '{typeFullName}' not found in {asmName}.");
            return type;
        }
    }
}
