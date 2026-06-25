using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader;
using NoImNotAHumanAccess.Dialogue;
using NoImNotAHumanAccess.Interop;
using NoImNotAHumanAccess.Speech;
using UnityEngine;

namespace NoImNotAHumanAccess.World
{
    /// <summary>
    /// Speaks a description of the inspection sign the game shows during a test — the teeth / eyes / hands / aura-photo /
    /// armpit / ear close-up the player examines on a guest. This is the core detection loop and is purely visual: the
    /// game draws a sprite and the player judges it from the TRAITS shown (tooth colour/condition, eye redness, photo
    /// features, armpit smoothness, bugs in the ear, bleeding gums, …). We DESCRIBE those traits so a blind player has
    /// the same evidence a sighted one does.
    ///
    /// IMPORTANT (user 2026-06-04): the player must NOT be told whether the guest is a visitor — that judgement is the
    /// game. We never say "human"/"imposter"/"visitor". We describe only the observable indicators. (The sprite shown
    /// differs by the guest's true nature, so the truth flag selects WHICH trait set is accurate to describe, but the
    /// words are traits, never a verdict.)
    ///
    /// Two behaviours layered on top:
    ///  • NO SWALLOW: a test fires at a dialogue beat and the guest's next line lands almost immediately. Both go out as
    ///    UIA announcements with no queue, so the line was stomping the description. We hand the description to the
    ///    <see cref="DialogueNarrator"/> to PREPEND to that next line (one combined utterance). If no line arrives within
    ///    a short window, <see cref="Tick"/> speaks the description on its own — so it's heard exactly once, never lost.
    ///  • F9 REPEAT: the most recent test's description is kept so the player can re-hear it (AccessMod's F9 in a
    ///    conversation). It resets to "untested" when the conversation ends (the DialogView goes inactive).
    ///
    /// <b>Status — partial trait text.</b> Armpit and ear traits are read from the SHOWN sprite's asset name at runtime
    /// (the game's sprite names encode them: armpit <c>clean</c>/<c>hairy</c>/<c>redness</c>/<c>fungal</c>/<c>iodine</c>;
    /// ear <c>cockroach</c>/<c>injury</c>/<c>burnt</c>). Teeth / eyes / hands / aura-photo carry no trait in their names
    /// (just numeric IDs), so those stay NEUTRAL prompts — describing them faithfully would need looking at the pixels,
    /// which we don't do here. When a sprite/field is missing we fall back to the neutral prompt — a miss must never
    /// invent a tell. The human/imposter bool only selects WHICH sprite field to read; the words are traits, never a
    /// verdict. Never throws.
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

        // If a dialogue line doesn't consume the pending description within this long, speak it standalone so it's
        // never lost (some tests may not be immediately followed by a line).
        private const float PendingFlushSeconds = 0.5f;

        private readonly ISpeechOutput _speech;
        private readonly DialogueNarrator _dialogue;

        private IntPtr _characterSoDataClass; // resolved lazily for reading _isImposter / _characterType
        private IntPtr _dialogViewClass;      // for the conversation-end reset (DialogView._isActive)
        private bool _dialogResolved;
        private bool _dialogWasActive;

        // Eye-movement read (rapid-eye-movement tell): CharacterEyeData carries the iris animation params the image
        // catalog can't see. Resolved lazily; getters bound on first use. See EyeMovementClause.
        private IntPtr _eyeDataClass;
        private IntPtr _getDistanceMultiplier, _getMinMoveDuration, _getMaxMoveDuration;
        private bool _eyeDataResolved;

        // Round-robin cursor per (sign, variant) pool, so repeated reveals of the same image cycle the pool's entries.
        private readonly Dictionary<string, int> _poolCursor = new();

        // Pending description waiting to be prepended to the next dialogue line (empty = none); the time after which we
        // flush it standalone if no line consumed it.
        private string _pending = string.Empty;
        private float _pendingFlushAt;

        /// <summary>The most recent test's spoken description in the CURRENT conversation, or null if there hasn't been
        /// a test yet (F9 says "untested"). Cleared when the conversation ends.</summary>
        public string? LastTestDescription { get; private set; }

        /// <summary>The truth of the most recently revealed sign, for features that need the just-inspected guest's
        /// status (e.g. a shot that follows). NOT used to narrate the sign — that stays trait-only.</summary>
        public bool? LastSignWasImposter { get; private set; }

        public SignNarrator(ISpeechOutput speech, DialogueNarrator dialogue)
        {
            _speech = speech;
            _dialogue = dialogue;
        }

        /// <summary>
        /// A sign was revealed. <paramref name="characterPtr"/> is the guest's <c>CharacterSOData</c>;
        /// <paramref name="sign"/> is the <c>ECharacterSign</c> as its underlying int. Picks the matching trait
        /// description and hands it to the dialogue narrator to fold into the guest's next line (so it isn't swallowed),
        /// and records it for the F9 repeat.
        /// </summary>
        public void OnSignShown(IntPtr characterPtr, int sign)
        {
            try
            {
                bool isImposter = ReadIsImposter(characterPtr);
                LastSignWasImposter = isImposter;

                string text = NextDescription(characterPtr, sign, isImposter);
                if (text.Length == 0) return;

                LastTestDescription = text;            // for F9 repeat in this conversation

                // Hand to the dialogue narrator to PREPEND to the next line (one utterance, no swallow). Keep a copy
                // pending so Tick can flush it standalone if no line arrives shortly.
                _dialogue.SetPendingTestDescription(text);
                _pending = text;
                _pendingFlushAt = Time.realtimeSinceStartup + PendingFlushSeconds;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SignNarrator] OnSignShown threw: {e.Message}");
            }
        }

        /// <summary>True while a conversation (DialogView) is on screen — when F9 should repeat the test result rather
        /// than read general status.</summary>
        public bool InConversation => DialogActive();

        /// <summary>Speak the most recent test description again (F9), or "Untested." if none this conversation.</summary>
        public void RepeatLastTest()
        {
            string text = string.IsNullOrEmpty(LastTestDescription) ? "Untested." : LastTestDescription!;
            _speech.Speak(text, interrupt: true);
        }

        /// <summary>
        /// Per-frame upkeep (driven by AccessMod): flush a pending description standalone if the dialogue line never
        /// consumed it, and reset the F9 "last test" when the conversation ends.
        /// </summary>
        public void Tick()
        {
            try
            {
                // Flush an un-consumed description so a test is never silent.
                if (_pending.Length > 0 && Time.realtimeSinceStartup >= _pendingFlushAt)
                {
                    // Only speak it ourselves if the dialogue narrator still holds it (i.e. no line consumed it).
                    if (_dialogue.TakePendingTestDescription() is { Length: > 0 } stillPending)
                        _speech.Speak(stillPending, interrupt: true);
                    _pending = string.Empty;
                }

                // Reset the F9 last-test when the conversation (DialogView) goes from active → inactive.
                bool dialogActive = DialogActive();
                if (_dialogWasActive && !dialogActive)
                    LastTestDescription = null;
                _dialogWasActive = dialogActive;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SignNarrator] Tick threw: {e.Message}");
            }
        }

        private bool DialogActive()
        {
            if (!_dialogResolved)
            {
                _dialogResolved = true;
                _dialogViewClass = Il2CppRaw.GetClass(GameAsm, "_Code.DialogSystem", "DialogView");
            }
            if (_dialogViewClass == IntPtr.Zero) return false;
            IntPtr dv = Il2CppRaw.FindObjectIncludingInactive(_dialogViewClass);
            return dv != IntPtr.Zero && Il2CppRaw.ReadBoolField(dv, _dialogViewClass, "_isActive");
        }

        /// <summary>Read <c>CharacterSOData._isImposter</c> off the instance. Defaults to false on any resolution miss —
        /// a miss must never invent a tell.</summary>
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

        /// <summary>
        /// Build the description for this (sign, variant). The generated catalog is keyed by the exact sprite or eye
        /// composite shown by the game. If no catalog entry matches, preserve the former name-derived armpit/ear
        /// behavior and finally fall back to a neutral prompt rather than inventing a trait.
        /// </summary>
        private string NextDescription(IntPtr characterPtr, int sign, bool isImposter)
        {
            string baseDesc = BaseDescription(characterPtr, sign, isImposter);

            // RAPID EYE MOVEMENT is a motion tell the still-image catalog can't see, so we add it at runtime from the
            // live CharacterEyeData. Append it to the eye sign's description (when the eyes are darting); composes with
            // the static text, e.g. "The eyes are bloodshot. The eyes are darting rapidly." A miss returns empty and
            // leaves baseDesc untouched, so it's never worse than today.
            if (sign == SignEye)
            {
                string movement = EyeMovementClause(characterPtr, isImposter);
                if (movement.Length > 0)
                    baseDesc = baseDesc.Length > 0 ? $"{baseDesc} {movement}" : movement;
            }
            return baseDesc;
        }

        /// <summary>The catalog / name-trait / neutral-prompt description for this sign, before any runtime augmentation
        /// (e.g. eye movement). The generated catalog is keyed by the exact sprite or eye composite shown by the game;
        /// if no entry matches, preserve the name-derived armpit/ear behavior and finally fall back to a neutral prompt.</summary>
        private string BaseDescription(IntPtr characterPtr, int sign, bool isImposter)
        {
            string? signature = ShownSignSpriteSignature(characterPtr, sign, isImposter);
            if (SignDescriptionCatalog.TryGet(sign, isImposter, signature, out string catalogDescription))
                return catalogDescription;

            if (sign == SignArmpit || sign == SignEar)
            {
                string trait = TraitFromSpriteName(sign, signature);
                if (trait.Length > 0) return trait;
            }
            return NextPrompt(sign, isImposter);
        }

        /// <summary>The catalog signature of the exact image shown. Eye images are a white/iris two-sprite composite;
        /// direct signs use one Sprite; animated signs use their final fully revealed frame.</summary>
        private string? ShownSignSpriteSignature(IntPtr characterPtr, int sign, bool isImposter)
        {
            if (characterPtr == IntPtr.Zero) return null;
            if (_characterSoDataClass == IntPtr.Zero)
                _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");
            IntPtr cls = _characterSoDataClass;
            if (cls == IntPtr.Zero) return null;

            string suffix = isImposter ? "Imposter" : "Human";
            if (sign == SignHands)
                return DirectSpriteName(characterPtr, cls, $"_handsSprite{suffix}");
            if (sign == SignTeeth)
                return DirectSpriteName(characterPtr, cls, $"_teethSprite{suffix}");
            if (sign == SignAuraPhoto)
                return DirectSpriteName(characterPtr, cls, isImposter ? "_photoImposter" : "_photoHuman");
            if (sign == SignEye)
            {
                IntPtr eye = Il2CppRaw.ReadObjectField(characterPtr, cls, $"_eyeSprite{suffix}");
                if (eye == IntPtr.Zero) return null;
                IntPtr eyeClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterEyeData");
                if (eyeClass == IntPtr.Zero) eyeClass = IL2CPP.il2cpp_object_get_class(eye);
                string? white = DirectSpriteName(eye, eyeClass, "_white");
                string? iris = DirectSpriteName(eye, eyeClass, "_iris");
                if (string.IsNullOrEmpty(white)) return iris;
                if (string.IsNullOrEmpty(iris)) return white;
                return $"{white}|{iris}";
            }
            if (sign == SignArmpit || sign == SignEar)
                return ShownSignSpriteName(characterPtr, sign, isImposter);
            return null;
        }

        private static string? DirectSpriteName(IntPtr owner, IntPtr ownerClass, string field)
        {
            IntPtr sprite = Il2CppRaw.ReadObjectField(owner, ownerClass, field);
            return sprite == IntPtr.Zero ? null : Il2CppRaw.GetUnityObjectName(sprite);
        }

        // RAPID-EYE-MOVEMENT thresholds. The eye always animates the iris; the tell is darting = big travel done fast.
        // CharacterEyeData._distanceMultiplier is [0,1] (iris travel amplitude, 1 = full ±150x/±100y); the move durations
        // are [0.1,5]s. So "rapid" = high distance AND short average duration. These values are derived from the field
        // ranges; exact per-character params aren't recoverable offline (the asset export is stubbed), so tune in-game if needed.
        private const float RapidEyeMinDistance = 0.6f;
        private const float RapidEyeMaxAvgDuration = 1.0f;

        /// <summary>
        /// A movement clause for the eye sign when the guest's eyes are darting rapidly, or empty otherwise. Reads the
        /// live <c>CharacterEyeData</c> the game would animate (the human/imposter side selects WHICH data is displayed,
        /// never narrated as a verdict) and judges rapid = high travel amplitude + short average move duration. The
        /// image catalog can't see motion, so this is the only source of the REM tell. Never throws; a miss returns empty.
        /// </summary>
        private string EyeMovementClause(IntPtr characterPtr, bool isImposter)
        {
            try
            {
                if (characterPtr == IntPtr.Zero) return string.Empty;
                if (_characterSoDataClass == IntPtr.Zero)
                    _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");
                if (_characterSoDataClass == IntPtr.Zero) return string.Empty;

                string suffix = isImposter ? "Imposter" : "Human";
                IntPtr eye = Il2CppRaw.ReadObjectField(characterPtr, _characterSoDataClass, $"_eyeSprite{suffix}");
                if (eye == IntPtr.Zero) return string.Empty;

                EnsureEyeDataResolved(eye);
                if (_getDistanceMultiplier == IntPtr.Zero || _getMinMoveDuration == IntPtr.Zero || _getMaxMoveDuration == IntPtr.Zero)
                    return string.Empty;

                float distance = Il2CppRaw.InvokeFloatGetter(eye, _getDistanceMultiplier);
                float minDur = Il2CppRaw.InvokeFloatGetter(eye, _getMinMoveDuration);
                float maxDur = Il2CppRaw.InvokeFloatGetter(eye, _getMaxMoveDuration);
                float avgDur = (minDur + maxDur) * 0.5f;
                bool rapid = distance >= RapidEyeMinDistance && avgDur > 0f && avgDur <= RapidEyeMaxAvgDuration;
                return rapid ? "The eyes are darting rapidly." : string.Empty;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SignNarrator] EyeMovementClause threw: {e.Message}");
                return string.Empty;
            }
        }

        private void EnsureEyeDataResolved(IntPtr eyeInstance)
        {
            if (_eyeDataResolved) return;
            _eyeDataResolved = true;
            _eyeDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterEyeData");
            if (_eyeDataClass == IntPtr.Zero) _eyeDataClass = IL2CPP.il2cpp_object_get_class(eyeInstance);
            if (_eyeDataClass == IntPtr.Zero) return;
            _getDistanceMultiplier = Il2CppRaw.GetMethod(_eyeDataClass, "get_DistanceMultiplier", 0);
            _getMinMoveDuration = Il2CppRaw.GetMethod(_eyeDataClass, "get_MinMoveDuration", 0);
            _getMaxMoveDuration = Il2CppRaw.GetMethod(_eyeDataClass, "get_MaxMoveDuration", 0);
        }

        /// <summary>Pick the next neutral prompt from the round-robin pool for this (sign, variant), advancing the cursor.</summary>
        private string NextPrompt(int sign, bool isImposter)
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
        /// The asset name of the sprite the game shows for this sign+variant, or null if it can't be read. Armpit and ear
        /// are <c>AnimationData</c> fields (<c>_armpitHuman/Imposter</c>, <c>_earHuman/Imposter</c>); we read the field,
        /// then its <c>Frames</c> array, and take the LAST frame — the fully-revealed body part — and its name. Any miss
        /// (field absent, e.g. the human side that reuses the default portrait, or empty frames) returns null so the
        /// caller falls back to a neutral prompt.
        /// </summary>
        private string? ShownSignSpriteName(IntPtr characterPtr, int sign, bool isImposter)
        {
            if (characterPtr == IntPtr.Zero) return null;
            if (_characterSoDataClass == IntPtr.Zero)
                _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");
            IntPtr cls = _characterSoDataClass;
            if (cls == IntPtr.Zero) return null;

            string field = sign switch
            {
                SignArmpit => isImposter ? "_armpitImposter" : "_armpitHuman",
                SignEar    => isImposter ? "_earImposter" : "_earHuman",
                _          => string.Empty,
            };
            if (field.Length == 0) return null;

            IntPtr animData = Il2CppRaw.ReadObjectField(characterPtr, cls, field);
            if (animData == IntPtr.Zero) return null;

            IntPtr animClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "AnimationData");
            if (animClass == IntPtr.Zero) animClass = IL2CPP.il2cpp_object_get_class(animData);
            IntPtr framesArray = Il2CppRaw.ReadObjectField(animData, animClass, "<Frames>k__BackingField");
            IntPtr[] frames = Il2CppRaw.ReadObjectArray(framesArray);
            if (frames.Length == 0) return null;

            IntPtr lastFrame = frames[frames.Length - 1];
            return Il2CppRaw.GetUnityObjectName(lastFrame);
        }

        /// <summary>
        /// Map a sprite asset name to a trait sentence, or empty if the name carries no trait (so the caller uses the
        /// neutral prompt). The game's armpit/ear sprite names embed the trait; we translate the keyword(s) to plain
        /// description. NEVER mentions human/visitor — only what's observable. Order matters where modifiers stack
        /// (e.g. <c>hairy_fungal</c>).
        /// </summary>
        private static string TraitFromSpriteName(int sign, string? spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return string.Empty;
            string n = spriteName!.ToLowerInvariant();

            if (sign == SignArmpit)
            {
                bool hairy = n.Contains("hairy");
                bool clean = n.Contains("clean") || n.Contains("clear");
                string baseDesc = hairy ? "Their armpit is hairy and unkempt."
                                : clean ? "Their armpit is smooth and clean."
                                : string.Empty;
                string extra = n.Contains("fungal") ? " The skin is mottled with a fungal rash."
                             : n.Contains("redness") ? " The skin looks red and irritated."
                             : n.Contains("iodine")  ? " It's stained dark with iodine."
                             : string.Empty;
                if (baseDesc.Length == 0 && extra.Length == 0) return string.Empty;
                return (baseDesc + extra).Trim();
            }

            if (sign == SignEar)
            {
                if (n.Contains("cockroach")) return "A cockroach is crawling inside their ear.";
                if (n.Contains("injury"))    return "Their ear is injured and raw.";
                if (n.Contains("burnt"))     return "Their ear is charred and burnt.";
                // Other ear sprites (plain numbered / catlady / default) carry no clear trait in the name.
                return string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// The NEUTRAL fallback prompt pool for one (sign kind, variant): it names the body part being examined without
        /// healthy/abnormal wording, so nothing leaks or misleads. Used for teeth/eyes/hands/aura-photo (whose sprite
        /// names carry no trait) always, and for armpit/ear when the sprite-name trait read misses. Both variants share
        /// the prompt on purpose — the prompt itself must not be a tell.
        /// </summary>
        private static string[] DescriptionPool(int sign, bool isImposter) => sign switch
        {
            SignTeeth     => TeethPrompts,
            SignEye       => EyePrompts,
            SignHands     => HandsPrompts,
            SignAuraPhoto => AuraPhotoPrompts,
            SignArmpit    => ArmpitPrompts,
            SignEar       => EarPrompts,
            _             => Array.Empty<string>(),
        };

        // Neutral, non-leaking prompts. They tell the player WHAT they're looking at — the trait to judge — without
        // stating whether it's normal or abnormal. Teeth/eyes/hands/aura-photo have no name-derived trait, so these
        // stand; armpit/ear use them only when the sprite-name read misses.
        private static readonly string[] TeethPrompts     = { "Examine their teeth and gums." };
        private static readonly string[] EyePrompts       = { "Examine their eyes." };
        private static readonly string[] HandsPrompts     = { "Examine their hands." };
        private static readonly string[] AuraPhotoPrompts = { "Examine the aura photo." };
        private static readonly string[] ArmpitPrompts    = { "Examine their armpit." };
        private static readonly string[] EarPrompts       = { "Examine their ear." };
    }
}
