# Sign / Inspection-Test Art Rip — Findings

Question driving this: the human-vs-visitor inspection test is judged by the player from
**visual traits** in a sprite the game shows (teeth, eyes, hands, aura photo, armpit, ear). To
narrate it for a blind player we must describe those traits. **Can the rip's structure / file
names / metadata supply the trait (and the human-vs-imposter axis) WITHOUT per-image pixel
analysis?** Answer: **mostly yes**, with three different naming regimes across the six signs.

Source: existing AssetRipper export at
`D:\Root\AssetRipper\NINAH\ExportedProject\ExportedProject\Assets\` (Unity 6000.3.10, IL2CPP).
Sprites: `Assets/Sprite/*.asset` (1840 sprites). Character SOs: `Assets/MonoBehaviour/*.asset`.

## Data model (from decompiled `CharacterSOData`)

Each character SO holds **two parallel sprite sets**, human and imposter, one per sign:

| Sign (`ECharacterSign`) | Human field | Imposter field | Type |
|---|---|---|---|
| Eye=0    | `_eyeSpriteHuman`   | `_eyeSpriteImposter`   | `CharacterEyeData` (`_white` sclera + `_iris`) |
| Hands=1  | `_handsSpriteHuman` | `_handsSpriteImposter` | `Sprite` |
| Teeth=2  | `_teethSpriteHuman` | `_teethSpriteImposter` | `Sprite` |
| AuraPhoto=3 | `_photoHuman`    | `_photoImposter`       | `Sprite` |
| Armpit=4 | `_armpitHuman`      | `_armpitImposter`      | `AnimationData` (`Sprite[] Frames`) |
| Ear=5    | `_earHuman`         | `_earImposter`         | `AnimationData` (`Sprite[] Frames`) |

`IsImposter` (from `_isImposter`) selects which set is the live one. So the human/imposter
partition is **authoritative in the data** — the question is only whether the *export* preserves it.

Ear and Armpit are animations (reveal motion). **For our purposes the LAST frame — the fully
revealed body part — is what matters** (user, 2026-06-04); the in-between frames are the reveal.

## What the rip preserved — and what it lost

**LOST: the character-SO → sprite mapping.** `CharacterSOData` assets exported **stripped** —
only the header (`m_Name`, script GUID), no serialized fields. This is the standard
IL2CPP+AssetRipper limitation (MonoBehaviour field layout not reconstructed at export). Example:
`Doc.asset` and even `EmptyCharacter.asset` contain zero sprite GUIDs. So we **cannot** read
"Doc's imposter teeth = sprite X" from the asset YAML in this export.

**PRESERVED: the sprite file names** — and they carry the trait/verdict signal directly.
Sprite `.meta`/`.asset` carry no extra labels (`m_AtlasTags: []`, `userData:` empty), so **the
file name is the only structural signal** — but it's a strong one.

## Three naming regimes (the actionable finding)

**Regime A — trait words in the name (ideal; no pixels needed). Armpit.**
No human/fake prefix; the trait IS the name:
```
armpit_1_clean        armpit_1_hairy        armpit_1_clean_redness
armpit_1_hairy_fungal armpit_3_clean_iodine armpit_3_clear  ...  (_0.._5 = anim frames)
```
`clean` vs `hairy` is effectively the human↔imposter axis; `redness` / `fungal` / `iodine` are
modifiers. Counts: clean 54, hairy 60, fungal 42, redness 18, clear 12 (135 armpit sprites).
Body-id `_1.._6` = which character variant; frame `_0.._5`. → take frame `_5` (last).

**Regime B — `human_` vs `fake_` prefix + bare numeric index. Teeth, Hands, Aura Photo.**
The **verdict axis is explicit** in the prefix, the **specific trait is not**:
```
human_tooth_1..57 (54)   vs   fake_tooth_1..10 (16) + fake_mainchar_tooth
human_hands_*    (57)    vs   fake_hands1_* / fake_hands2_* (52)
human_photo_*    (53)    vs   fake_photo_1..16 (31)
```
`human_tooth_34` vs `human_tooth_35` — both known "human-set", but their look differs and the
name doesn't say how. Verdict is free; fine-grained trait copy would need a look at the image.

**Regime C — `fake_*` only; some traits named, human baseline unprefixed. Eye, Ear.**
No `human_ear` / `human_eye` / `human_armpit` exist (count 0). Imposter side often names the
trait; human side is the character's default/clean sprite:
```
fake_ear, fake_ear_cockroach (20), fake_ear_injury (5), fake_ear_burnt, fake_ear_catlady
fake_eyes (8), fake_tv_redeyes, fake_tv_redness
```
So for ear/eye the imposter tell is frequently in the name (`cockroach` = bug in the ear,
`injury`, `redeyes`), but the clean/human counterpart is generic.

## Bottom line for the authoring task ([[sign-test-descriptions-need-art]])

- **Human-vs-imposter axis:** recoverable from names for teeth/hands/photo (`human_`/`fake_`)
  and from trait words for armpit (`clean`/`hairy`). For eye/ear the imposter is usually named,
  human is the default. We do **not** need pixels just to know which set is the "tell".
- **Specific trait wording:** free for **armpit** (clean/hairy/redness/fungal) and for the
  **named imposter** ear/eye cases (cockroach/injury/redeyes). For **teeth, hands, aura photo**,
  and the generic human baselines, the name gives only the body part + verdict side, not the
  visual particulars — those would need a one-time look at the (last-frame) image to write
  accurate, varied copy.
- **Recommended approach:** author from names where they're descriptive (armpit, named
  ear/eye imposters); do a **bounded, one-time visual pass** only on the numeric-indexed sets
  (teeth/hands/photo + generic baselines) to write trait copy — not a per-runtime image
  analysis. Keep human/imposter pools balanced in style so the WORDING isn't itself a tell.

## Gaps / caveats

- This export stalled early (~5320/88268 in the structure log; batch run threw). The 73
  character SOs and the sign sprites above DID export, but a clean re-rip is advisable before
  final authoring, and would also be the moment to try to recover the SO→sprite mapping
  (e.g. via a typed MonoBehaviour pass / reading the bundles with field info) if we want the
  exact per-character sprite rather than per-sign trait pools.
- Duplicates like `human_tooth_49 (2)` indicate same-named assets across bundles — dedupe when
  building any index.
