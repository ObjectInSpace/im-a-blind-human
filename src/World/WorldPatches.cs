using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using NoImNotAHumanAccess.Interop;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Harmony hooks for the first-person world HUD — the interaction prompt and the room-photo highlight.
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
        // Context control-prompt row: the game swaps the whole control set per context via this one method.
        private const string SetupControlsMethod = "SetupAndShowControlsView";

        // Room-photo highlight: UIButton.OnHover() fires on the specific button being highlighted, giving us the
        // button directly (no FindObjectOfType — the RoomDisplayer instance proved unfindable that way).
        private const string UIButtonFullName = "Il2Cpp_Code.Rooms.UIButton";
        private const string OnHoverMethod = "OnHover";

        // Object close-ups: the two views with a highlight-with-description surface. Fridge gives resolved strings
        // directly; the consumable confirm populates its own TMP fields, so we read them off __instance after.
        private const string FridgeViewFullName = "Il2Cpp_Code.Infrastructure.CloseUps.Views.FridgeCloseUpView";
        private const string OnPointerEnteredMethod = "OnPointerEntered";
        private const string ConsumableViewFullName =
            "Il2Cpp_Code.Infrastructure._NINAH__CloseUps.Views.Consumables.ConsumableCloseUpView";
        private const string SetupConsumableMethod = "SetupConsumable";
        // Mushroomlist (Book of Smiles pages, read in the bedroom): no named text field — the page text is a child
        // TMP we find via the view's GameObject. We hook Show() (arity 0) and read the text once the view is live.
        private const string MushroomlistViewFullName =
            "Il2Cpp_Code.Infrastructure._NINAH__CloseUps.Views.Mushroomlist.MushroomlistCloseUpView";
        private const string ShowMethod = "Show";
        private const string HideMethod = "Hide";

        // Inspection sign (teeth/eyes/armpit/aura-photo/hands/ear): DialogView.ShowSign(CharacterSOData, ECharacterSign)
        // is the real (non-mock) reveal sink; it carries the guest's resolved data (→ IsImposter) and the sign enum.
        private const string DialogViewFullName = "Il2Cpp_Code.DialogSystem.DialogView";
        private const string ShowSignMethod = "ShowSign";

        // Popup windows: PopupWindow.Show(string title, PopupButtonData[] buttons) — title arrives resolved. The notable
        // case is the "ending unlocked" popup whose title (the ending you received) never read. Credits: TitlesLoadText
        // loads its text into a TMP on Start(); we read it after.
        private const string PopupWindowFullName = "Il2Cpp_Code.Utils.UI.Popup.PopupWindow";
        private const string PopupShowMethod = "Show";
        private const string TitlesLoadTextFullName = "Il2Cpp_Code.Menues.Titles.TitlesLoadText";
        private const string StartMethod = "Start";

        private static HudNarrator? _narrator;
        private static RoomViewNarrator? _roomView;
        private static CloseUpNarrator? _closeUp;
        private static ControlsNarrator? _controls;
        private static SignNarrator? _sign;
        private static PopupNarrator? _popup;

        /// <summary>
        /// Resolve and apply the world-HUD hooks. Safe to call once at init; each hook logs and is skipped on
        /// failure rather than throwing (a missing hook degrades to "less speech", not a crashed mod).
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony, HudNarrator narrator, RoomViewNarrator roomView,
            CloseUpNarrator closeUp, ControlsNarrator controls, SignNarrator sign, PopupNarrator popup)
        {
            _narrator = narrator;
            _roomView = roomView;
            _closeUp = closeUp;
            _controls = controls;
            _sign = sign;
            _popup = popup;
            PatchShowHint(harmony);
            PatchHideHint(harmony);
            PatchUIButtonHover(harmony);
            PatchFridgePointerEntered(harmony);
            PatchConsumableSetup(harmony);
            PatchPopupShow(harmony);
            PatchCreditsStart(harmony);
            PatchMushroomlistShow(harmony);
            PatchSetupControlsView(harmony);
            PatchShowSign(harmony);
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

        // ---------------- Context control row: HUDPresenter.SetupAndShowControlsView(EControlsList) ----------------

        private static void PatchSetupControlsView(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, HudPresenterFullName, SetupControlsMethod, 1);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {HudPresenterFullName}.{SetupControlsMethod}(controlsList); " +
                        "context control-row narration disabled (repeat key still works).");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(SetupControlsPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {HudPresenterFullName}.{SetupControlsMethod}(controlsList).");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchSetupControlsView failed: {e}");
            }
        }

        /// <summary>
        /// Postfix on the context control-row swap. <paramref name="controlsList"/> is the <c>EControlsList</c> enum
        /// taken as its underlying <see cref="int"/> (None=0, InRoom=1, InFridge=2, InPhone=3, InRadio=4,
        /// InUsualCloseUp=5, RunZone=6, InDialog=7) so we don't bind the interop enum type. The ControlView children
        /// populate asynchronously, so the narrator arms a deferred read rather than reading here.
        /// </summary>
        private static void SetupControlsPostfix(int controlsList)
        {
            _controls?.OnContextChanged(controlsList);
        }

        // ---------------- Room-photo highlight: UIButton.OnHover() ----------------

        private static void PatchUIButtonHover(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, UIButtonFullName, OnHoverMethod, 0);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {UIButtonFullName}.{OnHoverMethod}(); " +
                        "room-photo highlight readout disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(UIButtonHoverPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {UIButtonFullName}.{OnHoverMethod}().");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchUIButtonHover failed: {e}");
            }
        }

        /// <summary>Postfix on the room-photo button hover. <paramref name="__instance"/> is the hovered
        /// <c>UIButton</c>; forward its pointer to the room-view narrator to resolve + speak the object's name.</summary>
        private static void UIButtonHoverPostfix(Il2CppObjectBase __instance)
        {
            try
            {
                if (__instance == null) return;
                _roomView?.OnButtonHovered(Il2CppRaw.Ptr(__instance));
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] UIButtonHoverPostfix threw: {e.Message}");
            }
        }

        // ---------------- Object close-ups: Fridge.OnPointerEntered + Consumable.SetupConsumable ----------------

        private static void PatchFridgePointerEntered(HarmonyLib.Harmony harmony)
        {
            try
            {
                // OnPointerEntered(string name, string narrativeDescription, string gameplayDescription, EConsumable).
                MethodInfo? target = ResolveMethodByArity(GameAsmName, FridgeViewFullName, OnPointerEnteredMethod, 4);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {FridgeViewFullName}.{OnPointerEnteredMethod}; " +
                        "fridge item readout disabled.");
                    return;
                }
                harmony.Patch(target, postfix: PostfixOf(nameof(FridgePointerEnteredPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {FridgeViewFullName}.{OnPointerEnteredMethod}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchFridgePointerEntered failed: {e}");
            }
        }

        /// <summary>Postfix on the fridge item hover. The three strings match the original parameter names so Harmony
        /// injects them; the trailing <c>EConsumable</c> is intentionally not declared.</summary>
        private static void FridgePointerEnteredPostfix(string name, string narrativeDescription, string gameplayDescription)
        {
            _closeUp?.OnFridgeItem(name, narrativeDescription, gameplayDescription);
        }

        private static void PatchConsumableSetup(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, ConsumableViewFullName, SetupConsumableMethod, 1);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {ConsumableViewFullName}.{SetupConsumableMethod}; " +
                        "consumable readout disabled.");
                    return;
                }
                harmony.Patch(target, postfix: PostfixOf(nameof(ConsumableSetupPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {ConsumableViewFullName}.{SetupConsumableMethod}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchConsumableSetup failed: {e}");
            }
        }

        /// <summary>Postfix on the consumable confirm setup. <paramref name="__instance"/> is the view whose TMP
        /// description fields were just populated; hand its pointer to the narrator to read + speak them.</summary>
        private static void ConsumableSetupPostfix(Il2CppObjectBase __instance)
        {
            try
            {
                if (__instance == null) return;
                _closeUp?.OnConsumableSetup(Il2CppRaw.Ptr(__instance));
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] ConsumableSetupPostfix threw: {e.Message}");
            }
        }

        private static void PatchMushroomlistShow(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? show = ResolveMethodByArity(GameAsmName, MushroomlistViewFullName, ShowMethod, 0);
                if (show == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {MushroomlistViewFullName}.{ShowMethod}; " +
                        "mushroomlist readout disabled.");
                    return;
                }
                harmony.Patch(show, postfix: PostfixOf(nameof(MushroomlistShowPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {MushroomlistViewFullName}.{ShowMethod}.");

                // Hide() clears the "open" flag so F9 stops repeating once the player backs out. Optional: if it can't
                // be resolved the readout still works, F9-repeat just lingers until the next close-up opens.
                MethodInfo? hide = ResolveMethodByArity(GameAsmName, MushroomlistViewFullName, HideMethod, 0);
                if (hide != null)
                {
                    harmony.Patch(hide, postfix: PostfixOf(nameof(MushroomlistHidePostfix)));
                    MelonLogger.Msg($"[WorldPatches] Patched {MushroomlistViewFullName}.{HideMethod}.");
                }
                else
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {MushroomlistViewFullName}.{HideMethod}; " +
                        "F9-repeat will linger past close.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchMushroomlistShow failed: {e}");
            }
        }

        /// <summary>Postfix on the mushroomlist view opening. <paramref name="__instance"/> is the view (a Component);
        /// the narrator walks its GameObject subtree to the page TMP and reads it.</summary>
        private static void MushroomlistShowPostfix(Il2CppObjectBase __instance)
        {
            try
            {
                if (__instance == null) return;
                _closeUp?.OnMushroomlistShown(Il2CppRaw.Ptr(__instance));
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] MushroomlistShowPostfix threw: {e.Message}");
            }
        }

        /// <summary>Postfix on the mushroomlist view closing — drop the F9-repeat "open" flag.</summary>
        private static void MushroomlistHidePostfix()
        {
            try
            {
                _closeUp?.OnMushroomlistHidden();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] MushroomlistHidePostfix threw: {e.Message}");
            }
        }

        // ---------------- Inspection sign: DialogView.ShowSign(CharacterSOData, ECharacterSign) ----------------

        private static void PatchShowSign(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, DialogViewFullName, ShowSignMethod, 2);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {DialogViewFullName}.{ShowSignMethod}(character, sign); " +
                        "inspection-sign narration disabled.");
                    return;
                }

                harmony.Patch(target, postfix: PostfixOf(nameof(ShowSignPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {DialogViewFullName}.{ShowSignMethod}(character, sign).");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchShowSign failed: {e}");
            }
        }

        /// <summary>
        /// Postfix on the inspection-sign reveal. <paramref name="character"/> is the guest's <c>CharacterSOData</c>
        /// (we read <c>_isImposter</c> off it to pick the human vs imposter description pool); <paramref name="sign"/>
        /// is the <c>ECharacterSign</c> taken as its underlying int (Eye=0, Hands=1, Teeth=2, AuraPhoto=3, Armpit=4,
        /// Ear=5) so we don't bind the interop enum. The original parameter names (<c>character</c>, <c>sign</c>) match
        /// so Harmony injects them.
        /// </summary>
        private static void ShowSignPostfix(Il2CppObjectBase character, int sign)
        {
            try
            {
                _sign?.OnSignShown(Il2CppRaw.Ptr(character), sign);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] ShowSignPostfix threw: {e.Message}");
            }
        }

        // ---------------- Popup title + buttons: PopupWindow.Show(string, PopupButtonData[]) ----------------

        private static void PatchPopupShow(HarmonyLib.Harmony harmony)
        {
            try
            {
                // Show(string title, PopupButtonData[] buttons). Resolving by arity alone is AMBIGUOUS at runtime (the
                // IL2CPP type exposes two 2-arg Show methods), so pick the overload whose FIRST param is String (the
                // title) — that's the one we want; the array is taken positionally as an interop object on the postfix.
                Type? popupType = ResolveType(GameAsmName, PopupWindowFullName);
                MethodInfo? target = popupType?
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == PopupShowMethod
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {PopupWindowFullName}.{PopupShowMethod}(string, ...); popup readout disabled.");
                    return;
                }
                harmony.Patch(target, postfix: PostfixOf(nameof(PopupShowPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {PopupWindowFullName}.{PopupShowMethod}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchPopupShow failed: {e}");
            }
        }

        /// <summary>Postfix on a popup opening. <paramref name="title"/> is the resolved title string (name matches the
        /// original so Harmony injects it); <paramref name="buttons"/> is the <c>PopupButtonData[]</c> arg, taken as an
        /// interop object so we can read the labels off its pointer.</summary>
        private static void PopupShowPostfix(string title, Il2CppObjectBase buttons)
        {
            try
            {
                _popup?.OnPopupShown(title, buttons == null ? IntPtr.Zero : Il2CppRaw.Ptr(buttons));
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] PopupShowPostfix threw: {e.Message}");
            }
        }

        // ---------------- Credits: TitlesLoadText.Start() loads the credits text into its TMP ----------------

        private static void PatchCreditsStart(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = ResolveMethodByArity(GameAsmName, TitlesLoadTextFullName, StartMethod, 0);
                if (target == null)
                {
                    MelonLogger.Warning(
                        $"[WorldPatches] Could not resolve {TitlesLoadTextFullName}.{StartMethod}; credits readout disabled.");
                    return;
                }
                harmony.Patch(target, postfix: PostfixOf(nameof(CreditsStartPostfix)));
                MelonLogger.Msg($"[WorldPatches] Patched {TitlesLoadTextFullName}.{StartMethod}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[WorldPatches] PatchCreditsStart failed: {e}");
            }
        }

        /// <summary>Postfix on the credits view's Start (its text is loaded into <c>_text</c> by now).
        /// <paramref name="__instance"/> is the <c>TitlesLoadText</c>; the narrator reads + speaks its TMP.</summary>
        private static void CreditsStartPostfix(Il2CppObjectBase __instance)
        {
            try
            {
                if (__instance == null) return;
                _popup?.OnCreditsShown(Il2CppRaw.Ptr(__instance));
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[WorldPatches] CreditsStartPostfix threw: {e.Message}");
            }
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
