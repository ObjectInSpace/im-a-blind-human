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
    /// <b>Status — full trait text from a generated catalog.</b> Every sign's description comes from
    /// <see cref="SignDescriptionCatalog"/>, generated offline by the visitor-image-describer tool (a local vision model
    /// describes each prepared sprite; armpit/ear hair + incidental features come from the sprite name, which is more
    /// reliable than the model there). The runtime computes the SHOWN sprite signature and looks it up: direct signs by
    /// the sprite, eyes by the white alone (<c>0w|&lt;white&gt;</c>, its sclera colour). Descriptions are neutral and
    /// both-ways (e.g. "the whites of the eyes are white" or "...are red"; "the teeth are yellow"), never a verdict —
    /// they describe what's there and mention a notable feature only when present, never its absence. On a catalog miss
    /// we fall back to a bare neutral prompt ("Examine their eyes.") — a miss must never invent a tell. The
    /// human/imposter bool only selects WHICH sprite field to read. Rapid eye movement is added live (the catalog can't
    /// see motion). Never throws.
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
        /// DIAGNOSTIC (Ctrl+F8): dump the LIVE runtime sign signature for EVERY character × all six signs × human/imposter,
        /// each tagged with whether the catalog currently matches it. This is the ground truth for reconciling the catalog
        /// (built from a sprite rip) against the names the game actually reads at runtime — run once, read the log offline,
        /// fix the keys. Iterates all characters via ResourceMother.CharactersData (ICharactersSODataProvider). One-shot.
        /// </summary>
        public void DumpAllSignatures()
        {
            try
            {
                // CharacterSOData are ScriptableObject ASSETS (not scene objects), so a scene FindObjectsByType misses
                // them. Reach the full roster via the Zenject-resolved ICharactersSODataProvider.CharactersData instead.
                IntPtr provider = ZenjectResolver.Resolve("_Code.Infrastructure", "ICharactersSODataProvider");
                if (provider == IntPtr.Zero)
                {
                    MelonLogger.Warning("[SignDump] ICharactersSODataProvider unresolved.");
                    return;
                }
                IntPtr getCharsData = Il2CppRaw.GetMethod(IL2CPP.il2cpp_object_get_class(provider), "get_CharactersData", 0);
                IntPtr arr = getCharsData != IntPtr.Zero ? Il2CppRaw.InvokeObjectGetter(provider, getCharsData) : IntPtr.Zero;
                IntPtr[] chars = arr != IntPtr.Zero ? Il2CppRaw.ReadObjectArray(arr) : Array.Empty<IntPtr>();
                if (chars.Length == 0)
                {
                    MelonLogger.Warning("[SignDump] CharactersData empty / unreadable.");
                    return;
                }
                if (_characterSoDataClass == IntPtr.Zero)
                    _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");

                MelonLogger.Msg($"[SignDump] BEGIN — {chars.Length} characters. Format: char|type|sign|side|signature|catalogHit");
                int[] signs = { SignEye, SignHands, SignTeeth, SignAuraPhoto, SignArmpit, SignEar };
                int misses = 0, total = 0;
                foreach (IntPtr c in chars)
                {
                    if (c == IntPtr.Zero) continue;
                    string cname = Il2CppRaw.GetUnityObjectName(c) ?? "<noname>";
                    int ctype = _characterSoDataClass != IntPtr.Zero
                        ? Il2CppRaw.ReadInt32Field(c, _characterSoDataClass, "_characterType", fallback: -1) : -1;
                    foreach (int sign in signs)
                    {
                        foreach (bool imp in new[] { false, true })
                        {
                            string? sig = ShownSignSpriteSignature(c, sign, imp);
                            if (string.IsNullOrEmpty(sig)) continue; // null sig = that side/sign not applicable here

                            // "Hit" = the runtime produces a real trait description, NOT the bare neutral prompt. We ask
                            // the ACTUAL resolution (BaseDescription) the same way the game does, instead of re-checking
                            // only the composite catalog — that earlier check reported every eye as a MISS because eyes
                            // are described via the sclera-by-white COMPONENT lookup, not the composite key. A row is a
                            // real miss only when a signed sign falls through to the neutral prompt; an eye with no
                            // catalog entry is logged as "clear" (a tolerated gap, not a hard miss) rather than MISS.
                            string desc = BaseDescription(c, sign, imp);
                            bool neutral = IsNeutralPrompt(sign, desc);
                            bool clearByDesign = sign == SignEye && neutral; // eye with no entry: tolerated, not a hard miss
                            bool hit = !neutral;
                            total++;
                            if (neutral && !clearByDesign) misses++;
                            string state = hit ? "hit" : (clearByDesign ? "clear" : "MISS");
                            MelonLogger.Msg($"[SignDump] {cname}|{ctype}|{sign}|{(imp ? "imp" : "hum")}|{sig}|{state}");
                        }
                    }

                    // EYE-MOVEMENT distribution: log the raw CharacterEyeData animation params per side so we can set the
                    // "rapid" threshold from real data (and confirm whether imposter-side values genuinely run higher).
                    foreach (bool imp in new[] { false, true })
                        LogEyeMovement(cname, imp, c);
                }
                MelonLogger.Msg($"[SignDump] END — {total} signed (sign,side) checked, {misses} fall through to the neutral " +
                                "prompt (real gaps). 'clear' rows = all-clear eyes (no entry by design), not misses.");
                _speech.Speak($"Sign dump complete. {misses} misses. See log.", interrupt: true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SignDump] DumpAllSignatures threw: {e.Message}");
            }
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
            // the static text, e.g. "The whites of the eyes are red. The eyes are darting rapidly." A miss returns empty
            // and leaves baseDesc untouched, so it's never worse than today.
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

            // EYE lookup by the WHITE sprite alone. The game pairs a white + a pupil independently, so the whole-
            // composite catalog can't enumerate every pairing — but the eye's static appearance (its sclera colour:
            // white / pink / red) lives on the white. So we key each white by itself ("0w|<white>") and the catalog now
            // gives EVERY white a description (not just bloodshot ones), so a clean sclera reads "the whites of the eyes
            // are white" instead of falling back to the neutral prompt. The pupil carries no tell; rapid eye movement is
            // added separately from the live animation params.
            if (sign == SignEye && !string.IsNullOrEmpty(signature))
            {
                string sclera = EyeScleraFromWhite(signature!);
                if (sclera.Length > 0) return sclera;
            }

            if (sign == SignArmpit || sign == SignEar)
            {
                string trait = TraitFromSpriteName(sign, signature);
                if (trait.Length > 0) return trait;
            }
            return NextPrompt(sign, isImposter);
        }

        /// <summary>Look up the sclera-appearance description for an eye by its WHITE sprite alone (the signature is
        /// "white|iris" or a single sprite; we take the non-pupil part). Every white has an entry now (its colour:
        /// white/pink/red), so this normally returns a description; empty only on a genuine catalog miss → caller uses
        /// the neutral prompt.</summary>
        private string EyeScleraFromWhite(string signature)
        {
            string? white = null;
            foreach (string p in signature.Split('|'))
                if (p.IndexOf("pupil", StringComparison.OrdinalIgnoreCase) < 0) { white = p; break; }
            return SignDescriptionCatalog.TryGetEyeComponent("0w", white, out string wd) ? wd : string.Empty;
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
                // Some characters (e.g. Blind) have no iris — the _iris sprite is literally named "void". Treat that as
                // no-iris so the signature keys on the white alone (which IS described), not the unmatchable "white|void".
                if (string.Equals(iris, "void", StringComparison.OrdinalIgnoreCase)) iris = null;
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

        // RAPID-EYE-MOVEMENT threshold, calibrated from the live values across all 72 characters (Ctrl+F8 dump).
        // The discriminator is DURATION, not distance: _distanceMultiplier is 0.5 for ~all characters (not a tell), while
        // the per-move duration varies. The default is avg ~0.75s; a coherent FAST cluster sits at avg ~0.30–0.49s
        // (Experienced, Nervous, Jacob, Sexy, Alt, Fairytaller, …) — eyes that complete a dart in about a third of a
        // second, which a sighted player reads as the rapid eye movement the in-game TV broadcast flags. There's a clean
        // gap between that cluster (≤0.49) and the ~0.65 default, so the cut is avg ≤ 0.50s. (Distance is no longer
        // gated — it doesn't vary meaningfully.) The TV tells the player to watch for this, so we describe it for them.
        private const float RapidEyeMaxAvgDuration = 0.50f;

        /// <summary>
        /// A movement clause for the eye sign when the guest's eyes are darting rapidly, or empty otherwise. Reads the
        /// live <c>CharacterEyeData</c> the game would animate (the human/imposter side selects WHICH data is displayed,
        /// never narrated as a verdict) and judges rapid = high travel amplitude + short average move duration. The
        /// image catalog can't see motion, so this is the only source of the REM tell. Never throws; a miss returns empty.
        /// </summary>
        /// <summary>DIAGNOSTIC: log a character's raw eye-movement params for one side, so the bulk dump captures the
        /// full distance/duration distribution. Used to calibrate the "rapid" threshold from real values.</summary>
        private void LogEyeMovement(string cname, bool isImposter, IntPtr characterPtr)
        {
            try
            {
                if (_characterSoDataClass == IntPtr.Zero)
                    _characterSoDataClass = Il2CppRaw.GetClass(GameAsm, CharactersNs, "CharacterSOData");
                IntPtr eye = _characterSoDataClass != IntPtr.Zero
                    ? Il2CppRaw.ReadObjectField(characterPtr, _characterSoDataClass, isImposter ? "_eyeSpriteImposter" : "_eyeSpriteHuman")
                    : IntPtr.Zero;
                if (eye == IntPtr.Zero) return;
                EnsureEyeDataResolved(eye);
                if (_getDistanceMultiplier == IntPtr.Zero) return;
                float distance = Il2CppRaw.InvokeFloatGetter(eye, _getDistanceMultiplier);
                float minDur = Il2CppRaw.InvokeFloatGetter(eye, _getMinMoveDuration);
                float maxDur = Il2CppRaw.InvokeFloatGetter(eye, _getMaxMoveDuration);
                MelonLogger.Msg($"[EyeMove] {cname}|{(isImposter ? "imp" : "hum")}|dist={distance:F3}|min={minDur:F3}|max={maxDur:F3}");
            }
            catch { /* diagnostic only */ }
        }

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

                float minDur = Il2CppRaw.InvokeFloatGetter(eye, _getMinMoveDuration);
                float maxDur = Il2CppRaw.InvokeFloatGetter(eye, _getMaxMoveDuration);
                float avgDur = (minDur + maxDur) * 0.5f;
                bool rapid = avgDur > 0f && avgDur <= RapidEyeMaxAvgDuration;
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

        /// <summary>True if <paramref name="description"/> is one of this sign's NEUTRAL fallback prompts (the
        /// "Examine their …" placeholders), i.e. the resolution produced no real trait. Used by the diagnostic dump to
        /// classify a row as a real miss vs a described/all-clear sign. An empty description also counts as neutral.</summary>
        private static bool IsNeutralPrompt(int sign, string description)
        {
            if (string.IsNullOrEmpty(description)) return true;
            foreach (string prompt in DescriptionPool(sign, false))
                if (string.Equals(prompt, description, StringComparison.Ordinal)) return true;
            return false;
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
