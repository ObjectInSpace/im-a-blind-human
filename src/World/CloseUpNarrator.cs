using System;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Menus;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks the object highlighted in an OBJECT close-up — the deeper photo views a station opens (the fridge's
    /// item grid, a consumable's use-confirm). Unlike the room photo (name-only), these views carry authored
    /// descriptions, so this speaks "name. description." A blind player hears what each item is and what it does as
    /// they move the highlight / open the confirm.
    ///
    /// Only two close-ups have a highlight-with-description surface (the others are different UIs):
    /// - <b>Fridge</b> (multi-item grid): hooked via <c>FridgeCloseUpView.OnPointerEntered(name, narrativeDescription,
    ///   gameplayDescription, EConsumable)</c> in <see cref="WorldPatches"/> — the strings arrive ALREADY RESOLVED,
    ///   so we just compose + speak (<see cref="OnFridgeItem"/>).
    /// - <b>Consumable</b> (single-item confirm): hooked via <c>ConsumableCloseUpView.SetupConsumable(EConsumable)</c>;
    ///   that method populates the view's <c>_name</c>/<c>_narrativeDescription</c>/<c>_gameplayDescription</c>
    ///   RTLTextMeshPro fields, which we read off the view pointer after it runs (<see cref="OnConsumableSetup"/>).
    ///
    /// Composition: "name. gameplay description." We prefer the GAMEPLAY description (what it does — e.g. "restores
    /// energy") over the narrative flavor; narrative is appended only when there's no gameplay text. Markup is cleaned
    /// with the shared cleaner. De-dupes the consecutive identical utterance (the grid can re-fire the same item).
    /// Never throws.
    /// </summary>
    public sealed class CloseUpNarrator
    {
        private const string ConsumableViewNs = "_Code.Infrastructure._NINAH__CloseUps.Views.Consumables";
        private const string GameAsm = "Assembly-CSharp.dll";

        private readonly ISpeechOutput _speech;
        private string _lastSpoken = string.Empty;

        private IntPtr _consumableViewClass; // resolved lazily for reading the consumable view's TMP fields

        public CloseUpNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>Fridge item highlighted: the resolved name + descriptions come straight from the hook.</summary>
        public void OnFridgeItem(string? name, string? narrativeDescription, string? gameplayDescription)
        {
            Announce(name, gameplayDescription, narrativeDescription);
        }

        /// <summary>Consumable confirm opened: read the name + descriptions off the view's TMP fields (the hooked
        /// <c>SetupConsumable</c> populated them just before this runs).</summary>
        public void OnConsumableSetup(IntPtr viewPtr)
        {
            try
            {
                if (viewPtr == IntPtr.Zero) return;
                if (_consumableViewClass == IntPtr.Zero)
                    _consumableViewClass = Il2CppRaw.GetClass(GameAsm, ConsumableViewNs, "ConsumableCloseUpView");
                // Read off the instance's actual class (the field is declared on ConsumableCloseUpView).
                IntPtr cls = _consumableViewClass != IntPtr.Zero ? _consumableViewClass : IL2CPP.il2cpp_object_get_class(viewPtr);

                string? name = Il2CppRaw.ReadTmpFieldText(viewPtr, cls, "_name");
                string? gameplay = Il2CppRaw.ReadTmpFieldText(viewPtr, cls, "_gameplayDescription");
                string? narrative = Il2CppRaw.ReadTmpFieldText(viewPtr, cls, "_narrativeDescription");
                Announce(name, gameplay, narrative);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CloseUpNarrator] OnConsumableSetup threw: {e.Message}");
            }
        }

        private void Announce(string? name, string? gameplay, string? narrative)
        {
            try
            {
                string n = Clean(name);
                string desc = Clean(gameplay);
                if (desc.Length == 0) desc = Clean(narrative); // fall back to flavor when no gameplay text

                string text;
                if (n.Length > 0 && desc.Length > 0) text = $"{n}. {desc}";
                else if (n.Length > 0) text = n;
                else text = desc;

                if (text.Length == 0 || text == _lastSpoken) return;
                _lastSpoken = text;
                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CloseUpNarrator] Announce threw: {e.Message}");
            }
        }

        private static string Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : ControlDescriber.Clean(s!).Trim();
    }
}
