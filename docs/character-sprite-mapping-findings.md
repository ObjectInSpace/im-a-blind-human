# Per-Character → Sign Sprite Mapping (Join #1 recovered)

This is the mapping the AssetRipper export LOST (stripped `CharacterSOData` YAML). Recovered by
reading the raw serialized MonoBehaviour bytes from the **base game data** with UnityPy and
parsing them against the field order from the decompiled `CharacterSOData`.

## Where the data actually lives

- **Not in Addressables.** The game's `aa/StandaloneWindows64/` holds only localization +
  monoscripts bundles. The character SOs + sprites are in the **base data**:
  `NoImNotAHuman_Data/sharedassets0.assets`.
- The 75 `CharacterSOData` MonoBehaviours are present there with **full raw serialized data**
  (Firefighter 992 B, Alkonost 1388 B, EmptyCharacter 556 B). AssetRipper dropped these fields
  on YAML export; UnityPy's `get_raw_data()` returns them intact.
- IL2CPP has no typetree, so we parse manually: skip the MonoBehaviour header, then scan 12-byte
  PPtrs (`m_FileID` int + `m_PathID` long) and resolve each PathID to its asset name via the
  file's object index. Sprite/Texture/AnimationClip refs in field order = the sign sets.

## What we got

`docs/sign-data/character_sign_sprites.{json,csv}` — **75 characters**, ~2568 sprite refs,
bucketed `<SIGN>_<side>`:

```
Firefighter  EAR_imposter   fake_ear_burnt         (he's a firefighter — ears burnt)
Doc          TEETH_imposter fake_tooth_1
Alkonost     ARMPIT_human   armpit_1_clean_*   ARMPIT_imposter armpit_1_hairy_*
```

- Coverage: ~73–74 of 75 characters have a ref for every sign.
- EYE resolves as a `_white` (sclera) + `_pupil`/`iris` pair (the `CharacterEyeData` sub-struct).
- EAR / ARMPIT are animations (frame arrays `_0.._5`) — **use the last frame** for the readout.
- Armpit is unprefixed; `clean`/`clear` → human, `hairy` → imposter (game convention). A few are
  numbered-only (e.g. Firefighter `armpit_6_*`) and land in `ARMPIT_other` — eyeball those.

### The asymmetry that matters

Most characters store **only one side's distinct sprite** (e.g. 6 chars have both EAR sides, 69
have both ARMPIT sides). The missing side is the character's **default/clean portrait** — there
is no separate `human_ear`/`human_eye` asset; the "human" ear/eye is just the normal face. So
the *distinct* asset present is usually the imposter tell, and its name often carries the trait
(`fake_ear_burnt`, `fake_ear_cockroach`, `fake_ear_injury`).

## Where this leaves the "exact per-character traits" goal

We now have BOTH joins for the authoring task:

1. **character + sign → exact sprite** — THIS doc (`character_sign_sprites.*`).
2. **character + sign → the guest's spoken line** — `sign_dialogue_en.*` (prior pass).

Still required to finish (unchanged): a **bounded one-time visual pass** to turn the numeric
sprite names (`human_tooth_16`, `fake_tooth_1`, `human_hands_5`, `human_photo_16`, …) into trait
prose. Names already carry the trait for armpit and many imposter ear/eye cases; the numeric
teeth/hands/photo sets are what need eyes on them. With Join #1 recovered, that pass can be
**per-character and exact** rather than generic pools — which is the model you chose.

## Reproduce

UnityPy over `sharedassets0.assets`; parse `CharacterSOData` raw bytes by field order
(see throwaway `parse_chars.py`/`finalize_chars.py`). Re-run if the game updates.

Related: [sign-art-rip-findings.md](sign-art-rip-findings.md) (sprite naming regimes),
[sign-dialogue-rip-findings.md](sign-dialogue-rip-findings.md) (ShowSign map + dialogue text).
