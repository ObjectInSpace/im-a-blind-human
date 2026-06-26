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
  --sprites  "D:\Root\AssetRipper\NINAH\ExportedProject\ExportedProject\Assets\Sprite" `
  --prepared-dir "work\prepared" `
  --output "work\manifest.json"
```

`--sprites` (the sliced Sprite asset dir) enables best-frame cropping for the ANIMATED signs (armpit, and the few
animated ears like the cockroach). Those sprites are exported as one combined sheet texture plus a `Sprite/<base>_<n>.asset`
per frame carrying that frame's `m_Rect` into the sheet. Without `--sprites` the whole tiled sheet is flattened (the
feature becomes a few pixels in a contact sheet); with it, prepare crops the single frame where the feature is most
present — the game plays the frames as a loop, so a transient feature (the cockroach crawls in then out) is caught
mid-loop rather than lost on an empty last frame. Eye/hands/teeth/aura-photo are static single images and are unaffected.

Classify each image into a controlled set of visible traits and generate descriptions from fixed factual templates.
The command writes after every image and resumes from matching model/source/prompt entries.

```powershell
uv run image-tools describe `
  --manifest "work\manifest.json" `
  --json "work\descriptions.json" `
  --csv "work\descriptions.csv" `
  --model "qwen3-vl:8b-instruct" `
  --num-gpu 99 `
  --num-ctx 4096 `
  --num-batch 64
```

`--num-gpu 99` runs fully on the GPU (the 8B is ~5.3 GB and fits the RTX 3050's 8 GB VRAM; this is ~5x faster than the
old `--num-gpu 0` CPU path — the CUDA-failure worry that motivated CPU-only is outdated). The 4,096-token context is
required by some large animation frames; `--num-batch 64` bounds working memory. Each image makes ONE model call
(colour + features in one prompt), so the image is vision-encoded once. The catalog is checkpointed after every image
and preserves cached entries across interruption or prompt/source changes.

`--concurrency N` keeps N model requests in flight (needs the Ollama **server** started with
`OLLAMA_NUM_PARALLEL=N`). It exists but **does not help on an 8 GB GPU**: tested on the RTX 3050, a second parallel
KV-cache slot pushed VRAM to ~7.2/8 GB and the run stalled (slower than sequential), and a 3rd slot doesn't fit. Leave
it at the default `1` (sequential) unless you have a much larger-VRAM GPU.

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
