# Visitor image describer

Offline image-description tools using a local [Ollama](https://ollama.com/) vision model. No image data is sent to
an online service.

## Requirements

- Python 3.11+
- Ollama running locally
- The non-reasoning vision model `qwen3-vl:4b-instruct`: `ollama pull qwen3-vl:4b-instruct`
- `uv` (recommended) or another Python package installer

## No, I'm not a Human inspection sprites

Prepare images from the recovered character/sign mapping. Eye layers are composited on a neutral background; ear and
armpit animations use the final listed frame.

```powershell
uv run image-tools prepare `
  --mapping "..\..\docs\sign-data\character_sign_sprites.csv" `
  --textures "D:\Root\AssetRipper\NINAH\ExportedProject\ExportedProject\Assets\Texture2D" `
  --prepared-dir "work\prepared" `
  --output "work\manifest.json"
```

Classify each image into a controlled set of visible traits and generate descriptions from fixed factual templates.
The command writes after every image and resumes from matching model/source/prompt entries.

```powershell
uv run image-tools describe `
  --manifest "work\manifest.json" `
  --json "work\descriptions.json" `
  --csv "work\descriptions.csv" `
  --model "qwen3-vl:4b-instruct" `
  --num-gpu 0 `
  --num-ctx 4096 `
  --num-batch 64
```

`--num-gpu 0` avoids CUDA-runner compatibility failures. The 4,096-token context is required by some large animation
frames; `--num-batch 64` limits CPU working memory. The catalog is checkpointed after every image and preserves cached
entries across interruption or prompt/source changes.

Use `--per-sign 2` for a balanced 12-image pilot or `--limit 20` for the first 20 tasks. The CSV is intended for
human validation; generated entries have `status=candidate`.
The model selects labels such as `bleeding-gums`, `dirty-nails`, or `ear-insect`; it does not author the spoken prose.
Fixed templates turn approved labels into reusable factual descriptions. The CSV includes the raw model output, parsed
labels, generated description, and validation issues. Prompt hashes force regeneration when classification rules change.
Each inspection category has a focused evidence list: eyes cover sclera/iris/pupils; hands cover fingers/nails;
teeth cover teeth/gums; aura photos cover silhouettes/glow; armpits cover hair/moisture/skin; and ears cover the canal,
injuries, insects, and other visible objects.

The primary sign taxonomy comes from the community-maintained
[Visitors reference](https://no-i-am-not-a-human.fandom.com/wiki/Visitors#Signs_of_someone_being_a_Visitor): white
teeth, dirty fingernails, bloodshot eyes, hairless armpits, black or blurred aura photos, ear insects, bleeding gums,
skin irritation, rapid pupil movement, and fungal armpit growth. These are prompt targets only; generated descriptions
must state visible evidence without calling the subject a visitor or assigning a verdict.

## Embed the catalog in the mod

After reviewing `work/descriptions.json`, regenerate the runtime lookup from this directory:

```powershell
uv run python generate_csharp_catalog.py `
  work/descriptions.json `
  ..\..\src\World\SignDescriptionCatalog.g.cs
```

Records marked `rejected`, records with validation issues, and empty descriptions are excluded. Candidate and approved
records are embedded; change a candidate's status to `rejected` to keep it out of the game.

## Tests

```powershell
uv run python -m unittest discover -s tests -v
```
