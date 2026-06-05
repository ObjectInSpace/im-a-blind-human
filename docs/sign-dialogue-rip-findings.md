# Sign Dialogue Rip — ShowSign map + resolved English text

Follow-up to [sign-art-rip-findings.md](sign-art-rip-findings.md). Two passes, both text/structure
only (no images):

- **(a)** Extract the `ShowSign <Character> <SIGN>` map from the compiled Yarn program.
- **(b)** Resolve every dialogue line ID those checks play to its English text from the
  localization string-table bundles.

Result: a complete, joined dataset under [sign-data/](sign-data/).

## Sources

- **Yarn program:** `_NoImNotAHuman.asset` `compiledYarnProgram` (hex → 2.2 MB binary). Holds
  dialogue STRUCTURE only — node names, `line:` IDs, commands (`ShowSign`, `StopShowingSign`,
  `SetEmotion`, `ExileCharacter`), and `IsImposter` branch markers. **No prose.**
- **English text:** Unity Localization bundles in
  `Assets/StreamingAssets/aa/StandaloneWindows64/`:
  - `localization-assets-shared_assets_all.bundle` — `SharedTableData`: `m_Key` (the `line:` ID)
    ↔ `m_Id` (numeric). 21,406 keys.
  - `localization-string-tables-english(en)_assets_all.bundle` — `StringTable` `_en`:
    `m_Id` → `m_Localized` (English). 34,288 entries across UI/Dialogs/Checks/Endings/etc.

The join is: **`line:` ID → (shared) `m_Id` → (string table) text.**

> NOTE: the extracted `_Localization/Tables/Text/*_en.asset` files are STRIPPED (header only,
> IL2CPP limitation — same as `CharacterSOData`). The values live in the **bundles**, read here
> with **UnityPy** (`pip install UnityPy`), not in the loose `.asset` YAML.

## (a) ShowSign map

- **365 `ShowSign` commands** → **352 distinct sign nodes** across **59 characters**.
- Per sign (by node): EYE 59, HANDS 59, TEETH 59, AURAPHOTO 59, EAR 60, ARMPIT 56.
- **84 nodes carry an explicit `IsImposter` branch** — both the human and imposter dialogue
  variant are present, selected at runtime by `CharacterSOData._isImposter`.
- Node name is derived from the `line:` IDs in the block (robust to the `ShowSign` character arg
  differing from the node prefix — e.g. `ShowSign Buddy HANDS` lives in node `Fatman_Hands`,
  an alias/shared character).

**Coverage gaps (real game facts, not parse errors):** 55/59 characters have all 6 signs.
Exceptions: BigLebowski / Daughter / Tough miss ARMPIT; Fatman misses EYE/HANDS/EAR (shares
nodes with Buddy).

## (b) Resolved text — and the key caveat

All **937 unique line IDs resolved to English, 0 missing.** But the resolved lines are the
**guest's spoken reaction during the check**, NOT a description of the body part:

```
[TEETH] Firefighter_Teeth   "Firefighter: Take a look."   /  "Firefighter: ..."
[EAR]   Firefighter_Ears     "Firefighter: You won't find much to check. They're charred..."
```

So this **confirms** the earlier conclusion: the **visual tell lives only in the sprite**; the
string table gives the conversation, not the trait. The dialogue is still useful flavor to read
during a check, and it follows the game's language setting for free.

**Bonus signal:** some `IsImposter`-branch lines DO reference the tell in the writing. E.g.
Alkonost EYE imposter vs human:
```
"...They're not red, is what I mean."     (one branch)
"...Her's are a bit calmer, maybe."        (other branch)
```
For the 84 branched nodes, the game's own words sometimes hint at the trait — worth mining when
authoring, but it's per-character and inconsistent, not a general source.

## Artifacts ([sign-data/](sign-data/))

- `showsign_map.json` — 352 nodes: `{character, sign, node, isImposterBranch, lines[]}`.
- `sign_dialogue_en.json` — same, with each line ID joined to its English text.
- `sign_dialogue_en.csv` — flat one-row-per-line (character, sign, node, isImposterBranch,
  line_id, text). 937 rows. Easiest to skim/author from.

## Reproduce

Scripts (kept in repo root workspace tmp, not committed): decode `compiledYarnProgram` hex →
`yarn.bin`; `build_signmap.py` (a); `resolve_lines.py` + `build_final.py` (b). Requires
`pip install UnityPy`. Re-run after any clean re-rip.

## Bottom line for the narration task ([[sign-test-descriptions-need-art]])

- The **what-gets-checked structure** is fully recovered: which guest offers which sign, the
  human/imposter branch points, and the game's own line for each — no images needed.
- The **visual trait wording still must come from the sprites** (per the art-rip findings:
  armpit names + named ear/eye imposters are free; teeth/hands/photo need a bounded one-time look).
- Recommended: narrate the resolved guest line as flavor, and layer the authored trait
  description on top — keeping human/imposter wording balanced so the words aren't a tell.
