using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks a description of the inspection sign the game is showing — the teeth / eyes / armpit / aura-photo /
    /// hands / ear close-up the player examines on a guest to judge human vs visitor. This is the core
    /// visitor-detection loop, and it is purely visual: the game shows a sprite and the player must decide.
    ///
    /// Fed by <see cref="WorldPatches"/>, which postfixes <c>DialogView.ShowSign(CharacterSOData, ECharacterSign)</c>
    /// — the real (non-mock) view method that fires at the dialogue beat where the sign is revealed. The postfix
    /// passes us the guest's <c>CharacterSOData</c> pointer (from which we read the ground-truth
    /// <c>_isImposter</c> flag and <c>_characterType</c>) and the sign kind as its underlying int.
    ///
    /// <b>Design — do NOT leak the answer (user-directed 2026-06-02):</b> the not-knowing IS the gameplay. The game
    /// holds the truth only as which sprite it draws (human vs imposter art); there is no text. We must DESCRIBE the
    /// image so the player judges it themselves — exactly as a sighted player does — never announce "this is a
    /// visitor." Critically, one description string per image would itself become the tell (the player learns
    /// "string X = imposter" after one encounter and stops judging). So each image is backed by a POOL of several
    /// short descriptions that we ROUND-ROBIN through, and the human-variant pool and imposter-variant pool must be
    /// authored INDISTINGUISHABLE in style, length, register and vocabulary — otherwise the player pattern-matches the
    /// pool rather than the picture. That balancing is an AUTHORING task that needs the real ripped art.
    ///
    /// <b>Status — foundation only.</b> The hook, the truth resolution, and the round-robin pool machinery are live
    /// now so the loop is testable in-game; the description pools currently hold a single placeholder entry per
    /// (sign, variant) that names the sign and variant plainly. Once the sign sprites are ripped, the placeholder
    /// pools get replaced with authored, balanced description sets and nothing else changes. Never throws.
    /// </summary>
    public sealed class SignNarrator
    {
        private const string GameAsm = "Assembly-CSharp.dll";
        private const string CharactersNs = "_Code.Characters";

        // ECharacterSign underlying ints (enum order verified in the decompile): Eye=0, Hands=1, Teeth=2,
        // AuraPhoto=3, Armpit=4, Ear=5. Kept as an int key so we never bind the interop enum type.
        private const int SignEye = 0;
        private const int SignHands = 1;
        private const int SignTeeth = 2;
        private const int SignAuraPhoto = 3;
        private const int SignArmpit = 4;
        private const int SignEar = 5;

        private readonly ISpeechOutput _speech;

        private IntPtr _characterSoDataClass; // resolved lazily for reading _isImposter / _characterType

        // Round-robin cursor per (sign, isImposter) pool, so repeated reveals of the same image cycle through the
        // pool's entries instead of repeating one string. Keyed by the same composite key the pools use.
        private readonly Dictionary<string, int> _poolCursor = new();

        /// <summary>The truth of the most recently revealed sign, for any feature that wants the just-inspected
        /// guest's status without re-resolving it (e.g. a shot that follows an inspection). Null until the first
        /// sign is shown this session. NOT used to narrate the sign itself — that stays description-only.</summary>
        public bool? LastSignWasImposter { get; private set; }

        public SignNarrator(ISpeechOutput speech) => _speech = speech;

        /// <summary>
        /// A sign was revealed. <paramref name="characterPtr"/> is the guest's <c>CharacterSOData</c>;
        /// <paramref name="sign"/> is the <c>ECharacterSign</c> as its underlying int. Reads the human/imposter truth,
        /// picks the next description from the matching round-robin pool, and speaks it. The truth selects WHICH pool
        /// (human art vs imposter art get described differently and accurately) but is never spoken outright.
        /// </summary>
        public void OnSignShown(IntPtr characterPtr, int sign)
        {
            try
            {
                bool isImposter = ReadIsImposter(characterPtr);
                LastSignWasImposter = isImposter;

                string text = NextDescription(sign, isImposter);
                if (text.Length == 0) return;

                _speech.Speak(text, interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SignNarrator] OnSignShown threw: {e.Message}");
            }
        }

        /// <summary>Read <c>CharacterSOData._isImposter</c> off the instance. Defaults to false (treat as human) on any
        /// resolution miss — a miss should never invent an imposter tell.</summary>
        private bool ReadIsImposter(IntPtr characterPtr)
        {
            if (characterPtr == IntPtr.Zero) return false;
            if (_characterSoDataClass == IntPtr.Zero)
                _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");
            IntPtr cls = _characterSoDataClass != IntPtr.Zero
                ? _characterSoDataClass
                : IL2CPP.il2cpp_object_get_class(characterPtr);
            return Il2CppRaw.ReadBoolField(characterPtr, cls, "_isImposter");
        }

        /// <summary>Pick the next description from the round-robin pool for this (sign, variant), advancing the cursor.
        /// Empty string if the sign is unknown (shouldn't happen — the enum is fixed).</summary>
        private string NextDescription(int sign, bool isImposter)
        {
            string[] pool = DescriptionPool(sign, isImposter);
            if (pool.Length == 0) return string.Empty;

            string key = $"{sign}|{(isImposter ? 1 : 0)}";
            int i = _poolCursor.TryGetValue(key, out int cur) ? cur : 0;
            string chosen = pool[i % pool.Length];
            _poolCursor[key] = (i + 1) % pool.Length;
            return chosen;
        }

        /// <summary>
        /// The description pool for one (sign kind, human/imposter) image. PLACEHOLDER pools today (one plain entry
        /// each) so the loop is testable before the art exists; replace with authored, BALANCED sets once the sprites
        /// are ripped. The two variants of a sign MUST end up indistinguishable in style/length/vocabulary — see the
        /// class remarks. Authoring contract: keep human and imposter pools the same size and cadence.
        /// </summary>
        private static string[] DescriptionPool(int sign, bool isImposter) => sign switch
        {
            SignTeeth     => isImposter ? TeethImposter     : TeethHuman,
            SignEye       => isImposter ? EyeImposter       : EyeHuman,
            SignHands     => isImposter ? HandsImposter     : HandsHuman,
            SignAuraPhoto => isImposter ? AuraPhotoImposter : AuraPhotoHuman,
            SignArmpit    => isImposter ? ArmpitImposter    : ArmpitHuman,
            SignEar       => isImposter ? EarImposter       : EarHuman,
            _             => Array.Empty<string>(),
        };

        // ---- Placeholder description pools (one entry each; authored balanced sets land after the asset rip). ----
        // Phrasing names the sign + variant plainly for now. These are deliberately the ONLY place the variant leaks,
        // because they are stubs; the authored replacements must NOT reveal the variant (see class remarks).
        private static readonly string[] TeethHuman        = { "Their teeth. Human variant." };
        private static readonly string[] TeethImposter     = { "Their teeth. Imposter variant." };
        private static readonly string[] EyeHuman          = { "Their eyes. Human variant." };
        private static readonly string[] EyeImposter       = { "Their eyes. Imposter variant." };
        private static readonly string[] HandsHuman        = { "Their hands. Human variant." };
        private static readonly string[] HandsImposter     = { "Their hands. Imposter variant." };
        private static readonly string[] AuraPhotoHuman    = { "The aura photo. Human variant." };
        private static readonly string[] AuraPhotoImposter = { "The aura photo. Imposter variant." };
        private static readonly string[] ArmpitHuman       = { "Their armpit. Human variant." };
        private static readonly string[] ArmpitImposter    = { "Their armpit. Imposter variant." };
        private static readonly string[] EarHuman          = { "Their ear. Human variant." };
        private static readonly string[] EarImposter       = { "Their ear. Imposter variant." };
    }
}
