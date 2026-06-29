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
    /// Three close-ups carry readable text (the others are pure photos):
    /// - <b>Fridge</b> (multi-item grid): hooked via <c>FridgeCloseUpView.OnPointerEntered(name, narrativeDescription,
    ///   gameplayDescription, EConsumable)</c> in <see cref="WorldPatches"/> — the strings arrive ALREADY RESOLVED,
    ///   so we just compose + speak (<see cref="OnFridgeItem"/>).
    /// - <b>Consumable</b> (single-item confirm): hooked via <c>ConsumableCloseUpView.SetupConsumable(EConsumable)</c>;
    ///   that method populates the view's <c>_name</c>/<c>_narrativeDescription</c>/<c>_gameplayDescription</c>
    ///   RTLTextMeshPro fields, which we read off the view pointer after it runs (<see cref="OnConsumableSetup"/>).
    /// - <b>Mushroomlist</b> (the Book of Smiles pages, read in the bedroom): hooked via
    ///   <c>MushroomlistCloseUpView.Show()</c>; the page text is a single child <c>TextMeshProUGUI</c> (no named field),
    ///   so we find it in the view's subtree and read it (<see cref="OnMushroomlistShown"/>).
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
        private IntPtr _tmpTextClass;        // resolved lazily for the mushroomlist's child page TMP
        private IntPtr _localizeStringEventClass, _lseGetLocalizedString; // mushroomlist localization (see OnMushroomlistShown)

        // Mushroomlist state, so F9 can repeat the verses while the close-up is open. Set on Show(), cleared on Hide().
        private string _mushroomlistText = string.Empty;
        private bool _mushroomlistOpen;

        /// <summary>True while the mushroomlist (Book of Smiles) close-up is open, so the F9 handler can route a repeat
        /// of the verses to <see cref="RepeatMushroomlist"/> instead of the status readout.</summary>
        public bool MushroomlistOpen => _mushroomlistOpen;

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

        /// <summary>Mushroomlist (the Book of Smiles pages the Mushroom Eater gives you, read in the bedroom) opened.
        /// The page text is on a child of the view. The TMP's raw text is the BAKED SOURCE-LANGUAGE string (Russian);
        /// the localized text is driven by a <c>LocalizeStringEvent</c> on that same GameObject. So we prefer the
        /// LocalizeStringEvent's <c>GetLocalizedString()</c> (the player's language) and only fall back to the raw TMP
        /// text if no localize event is present. The view's <c>Show()</c> just ran, so the GameObject is live.</summary>
        public void OnMushroomlistShown(IntPtr viewPtr)
        {
            try
            {
                if (viewPtr == IntPtr.Zero) return;
                IntPtr goPtr = Il2CppRaw.GetComponentGameObject(viewPtr);
                if (goPtr == IntPtr.Zero) return;
                EnsureMushroomlistResolved();

                // Prefer the LOCALIZED string from the LocalizeStringEvent in the view's subtree.
                string? text = null;
                bool viaLse = false;
                IntPtr lse = IntPtr.Zero;
                if (_localizeStringEventClass != IntPtr.Zero && _lseGetLocalizedString != IntPtr.Zero)
                {
                    lse = Il2CppRaw.GetComponentInChildrenRaw(goPtr, _localizeStringEventClass, includeInactive: true);
                    if (lse != IntPtr.Zero)
                    {
                        text = Il2CppRaw.InvokeStringGetter(lse, _lseGetLocalizedString);
                        viaLse = !string.IsNullOrWhiteSpace(text);
                    }
                }

                // Fallback: the raw TMP text (baked source language) if the localize event didn't resolve.
                if (string.IsNullOrWhiteSpace(text) && _tmpTextClass != IntPtr.Zero)
                {
                    IntPtr tmp = Il2CppRaw.GetComponentInChildrenRaw(goPtr, _tmpTextClass, includeInactive: true);
                    text = Il2CppRaw.ReadTmpComponentText(tmp);
                }

                string preview = (text ?? "").Replace("\n", " ");
                if (preview.Length > 60) preview = preview.Substring(0, 60);
                MelonLogger.Msg($"[CloseUpNarrator] mushroomlist: lseClass={_localizeStringEventClass != IntPtr.Zero} " +
                                $"lseGetter={_lseGetLocalizedString != IntPtr.Zero} lseFound={lse != IntPtr.Zero} " +
                                $"viaLse={viaLse} text='{preview}'.");

                _mushroomlistText = Clean(text);
                _mushroomlistOpen = true;
                Announce(text, null, null);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[CloseUpNarrator] OnMushroomlistShown threw: {e.Message}");
            }
        }

        private void EnsureMushroomlistResolved()
        {
            if (_tmpTextClass == IntPtr.Zero)
                _tmpTextClass = Il2CppRaw.GetClass("Unity.TextMeshPro.dll", "TMPro", "TMP_Text");
            if (_localizeStringEventClass == IntPtr.Zero)
            {
                _localizeStringEventClass = Il2CppRaw.GetClass("Unity.Localization.dll", "UnityEngine.Localization.Components", "LocalizeStringEvent");
                if (_localizeStringEventClass != IntPtr.Zero)
                    _lseGetLocalizedString = Il2CppRaw.GetMethod(_localizeStringEventClass, "GetLocalizedString", 0);
            }
        }

        /// <summary>Mushroomlist closed: drop the "open" flag so F9 stops repeating the verses. The text is kept (cheap)
        /// but unreachable until the view re-opens.</summary>
        public void OnMushroomlistHidden() => _mushroomlistOpen = false;

        /// <summary>Speak the mushroomlist verses again (F9 while the close-up is open). No-op if there's nothing to
        /// repeat. Bypasses the de-dupe so the same text speaks every press.</summary>
        public void RepeatMushroomlist()
        {
            if (_mushroomlistText.Length == 0) return;
            _lastSpoken = _mushroomlistText;
            _speech.Speak(_mushroomlistText, interrupt: true);
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
