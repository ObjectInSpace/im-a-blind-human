"""Prepare game sprites and create reviewable description catalogs."""

from __future__ import annotations

import csv
import hashlib
import json
import re
import shutil
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

from PIL import Image, ImageChops, ImageStat

from .ollama import OllamaClient

FORBIDDEN_WORDS = ("human", "imposter", "visitor", "safe", "dangerous")
PREPARATION_VERSION = "3-anim-bestframe"
SUBJECTIVE_WORDS = (
    "abnormal",
    "attractive",
    "bad",
    "clean",
    "dangerous",
    "good",
    "healthy",
    "natural",
    "normal",
    "safe",
    "ugly",
    "unclean",
    "unkempt",
)
# Every readout must DESCRIBE THE ACTUAL IMAGE, for every sign and every variant — never fall back to a placeholder
# ("examine the …") and never substitute a judgement ("none of the listed signs is visible" is a verdict, not a
# description). So each sign has TWO label groups:
#   • APPEARANCE — a PICK-ONE baseline that is ALWAYS answerable (the dominant colour/condition of the body part).
#     This guarantees a concrete clause for a perfectly clean image, so there is no empty description and no fallback.
#   • TELLS — zero-or-more additive findings (the gameplay signs). Layered on top of the appearance clause when present.
# The rendered description is "<appearance>[; <tell>; <tell>].". No "none" label exists any more: an image with no tell
# still renders its appearance. (Rapid eye movement remains a runtime-only motion tell, added by the mod, not here.)

# Appearance is a single mutually-exclusive pick (the model returns exactly one of these). Order = priority when the
# model hedges and returns several; the FIRST listed that the model emits wins.
SIGN_APPEARANCE = {
    # EYE-WHITE describes the SCLERA (the white around the iris): white, pink, or red. Stated neutrally both ways.
    "EYE-WHITE": ("sclera-red", "sclera-pink", "sclera-white"),
    "HANDS": ("skin-pale", "skin-tan", "skin-dark", "skin-flushed", "skin-grey"),
    # Teeth COLOUR is just an appearance, described neutrally both ways (white is a shade, not an emphasised tell).
    "TEETH": ("teeth-white", "teeth-offwhite", "teeth-yellow", "teeth-stained", "teeth-grey"),
    "AURAPHOTO": ("photo-dark", "photo-bright", "photo-muted"),
    "ARMPIT": ("skin-pale", "skin-tan", "skin-dark", "skin-flushed", "skin-grey"),
    "EAR": ("ear-pale", "ear-tan", "ear-dark", "ear-flushed", "ear-grey"),
}

APPEARANCE_PHRASES = {
    "EYE-WHITE": {
        "sclera-red": "the whites of the eyes are red",
        "sclera-pink": "the whites of the eyes are pink",
        "sclera-white": "the whites of the eyes are white",
    },
    "HANDS": {
        "skin-pale": "the skin of the hands is pale",
        "skin-tan": "the skin of the hands is tan",
        "skin-dark": "the skin of the hands is dark",
        "skin-flushed": "the skin of the hands is red",
        "skin-grey": "the skin of the hands is grey",
    },
    "TEETH": {
        "teeth-white": "the teeth are white",
        "teeth-offwhite": "the teeth are off-white",
        "teeth-yellow": "the teeth are yellow",
        "teeth-stained": "the teeth are stained",
        "teeth-grey": "the teeth are grey",
    },
    "AURAPHOTO": {
        "photo-dark": "the aura photo is dark",
        "photo-bright": "the aura photo is bright",
        "photo-muted": "the aura photo is muted",
    },
    "ARMPIT": {
        "skin-pale": "the armpit skin is pale",
        "skin-tan": "the armpit skin is tan",
        "skin-dark": "the armpit skin is dark",
        "skin-flushed": "the armpit skin is red",
        "skin-grey": "the armpit skin is grey",
    },
    "EAR": {
        "ear-pale": "the ear is pale",
        "ear-tan": "the ear is tan",
        "ear-dark": "the ear is dark",
        "ear-flushed": "the ear is red",
        "ear-grey": "the ear is grey",
    },
}

# A SECOND appearance dimension for the armpit: its HAIR state, stated neutrally BOTH ways (every armpit is smooth or
# hairy and the player judges it, so we always say which). The MODEL is trusted first — it answers a smooth/hairy
# question — and the sprite NAME ("hairy" substring) is only a BACKUP when the model gives no usable hair answer.
ARMPIT_HAIR_LABELS = ("hairy", "smooth")
ARMPIT_HAIR_PHRASE = {"hairy": "the armpit is hairy", "smooth": "the armpit is smooth"}

# TELLS — additive findings stated only when PRESENT (no absence). These are incidental features, not the colour/
# condition baseline. Phrased plainly, without emphasis ("the gums are bleeding", not "visibly bleeding").
SIGN_TRAITS = {
    "EYE-WHITE": (),  # the eye's only static appearance (sclera colour) is the appearance pick; no separate tells
    # Skin redness/irritation is something to look for, but it's already carried by the skin-flushed ("red") appearance
    # pick, stated neutrally both ways — so it isn't a separate tell here (that would double-state the redness).
    "HANDS": ("dirty-nails", "unusual-fingers", "injured", "foreign-object"),
    "TEETH": ("bleeding-gums", "damaged-teeth", "gaps", "foreign-object"),  # colour moved to appearance
    "AURAPHOTO": ("black-patches", "blurred", "colored-glow", "extra-silhouettes"),
    "ARMPIT": ("fungal-growth", "wet", "injured"),  # hair -> NAME_APPEARANCE; redness is the skin-flushed appearance
    "EAR": ("insect", "injured", "burned", "discharge", "foreign-object"),
}

TRAIT_PHRASES = {
    "HANDS": {
        "dirty-nails": "there is dirt under the fingernails",
        "unusual-fingers": "the fingers have an unusual count or shape",
        "injured": "there is an injury on the hands",
        "foreign-object": "there is a foreign object on the hands",
    },
    "TEETH": {
        "bleeding-gums": "the gums are bleeding",
        "damaged-teeth": "one or more teeth are damaged",
        "gaps": "there are gaps between some teeth",
        "foreign-object": "there is a foreign object among the teeth",
    },
    "AURAPHOTO": {
        "black-patches": "there are black patches in the photo",
        "blurred": "the photo is blurred",
        "colored-glow": "there is a coloured glow around the figure",
        "extra-silhouettes": "there are extra silhouettes in the photo",
    },
    "ARMPIT": {
        "fungal-growth": "there is a fungal growth on the skin",
        "wet": "the skin is moist",
        "injured": "there is an injury on the armpit",
    },
    "EAR": {
        "insect": "there is an insect inside the ear",
        "injured": "there is an injury on the ear",
        "burned": "the ear is burned",
        "discharge": "there is discharge in the ear",
        "foreign-object": "there is a foreign object inside the ear",
    },
}


@dataclass
class Task:
    id: str
    sign: str
    side: str
    characters: list[str]
    sprites: list[str]
    sources: list[str]
    prepared_image: str
    source_hash: str
    warnings: list[str] = field(default_factory=list)

    def as_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "sign": self.sign,
            "side": self.side,
            "characters": self.characters,
            "sprites": self.sprites,
            "sources": self.sources,
            "prepared_image": self.prepared_image,
            "source_hash": self.source_hash,
            "warnings": self.warnings,
        }


def _split_sprites(value: str) -> list[str]:
    return [item.strip() for item in value.split(";") if item.strip()]


def _safe_id(value: str) -> str:
    normalized = re.sub(r"[^A-Za-z0-9._-]+", "_", value).strip("_")
    if len(normalized) <= 120:
        return normalized
    suffix = hashlib.sha256(value.encode("utf-8")).hexdigest()[:12]
    return f"{normalized[:107]}_{suffix}"


def _is_parser_noise(character: str) -> bool:
    """Exclude TextMesh Pro font assets accidentally identified as CharacterSOData."""

    return "sdf" in character.casefold()


def _sha256(paths: Iterable[Path]) -> str:
    digest = hashlib.sha256()
    digest.update(PREPARATION_VERSION.encode("ascii"))
    for path in paths:
        digest.update(path.name.encode("utf-8"))
        digest.update(path.read_bytes())
    return digest.hexdigest()


def _find_texture(texture_dir: Path, sprite: str, allow_frame_fallback: bool = False) -> Path | None:
    exact = texture_dir / f"{sprite}.png"
    if exact.is_file():
        return exact
    normalized = re.sub(r"\s*\(\d+\)$", "", sprite)
    fallback = texture_dir / f"{normalized}.png"
    if fallback.is_file():
        return fallback
    if allow_frame_fallback:
        frame_base = re.sub(r"_\d+$", "", normalized)
        frame_fallback = texture_dir / f"{frame_base}.png"
        if frame_fallback.is_file():
            return frame_fallback
    return None


def _flatten(image: Image.Image, minimum_size: int = 384) -> Image.Image:
    rgba = image.convert("RGBA")
    canvas = Image.new("RGBA", rgba.size, (235, 235, 235, 255))
    canvas.alpha_composite(rgba)
    result = canvas.convert("RGB")
    scale = max(1, (minimum_size + max(result.size) - 1) // max(result.size))
    if scale > 1:
        result = result.resize((result.width * scale, result.height * scale), Image.Resampling.NEAREST)
    return result


def _composite(paths: list[Path]) -> Image.Image:
    images = [Image.open(path).convert("RGBA") for path in paths]
    width = max(image.width for image in images)
    height = max(image.height for image in images)
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    for image in images:
        canvas.alpha_composite(image, ((width - image.width) // 2, (height - image.height) // 2))
    return _flatten(canvas)


# Armpit/ear sprites are ANIMATIONS: AssetRipper exports one combined sheet texture (Texture2D/<base>.png) plus a sliced
# sprite per frame (Sprite/<base>_<n>.asset) carrying that frame's m_Rect into the sheet. Flattening the whole sheet fed
# the model a tiled contact sheet (the feature became a few pixels), so we instead crop the single best frame. "Best" =
# the frame where the feature is most present: the game plays the frames as a LOOP, so for a TRANSIENT feature (the
# cockroach crawls in then out) the last frame can be empty — we pick the frame that differs most from the loop's plain
# baseline (frame 0). For a STATIC feature (fungal/redness, present every frame) any late frame scores high; all fine.

# Match the `m_Rect:` block specifically: an optional serializedVersion line, then x/y/width/height on consecutive
# lines. NOT re.DOTALL — `.` must not cross newlines, or it slides past the real values into a later empty textureRect.
_RECT_RE = re.compile(
    r"m_Rect:\s*\n(?:\s*serializedVersion:.*\n)?\s*x:\s*([\d.eE+-]+)\s*\n\s*y:\s*([\d.eE+-]+)\s*\n"
    r"\s*width:\s*([\d.eE+-]+)\s*\n\s*height:\s*([\d.eE+-]+)"
)


def _read_sprite_rect(asset_path: Path) -> tuple[int, int, int, int] | None:
    """The m_Rect (x, y, width, height) of a sliced Sprite asset, or None if absent/degenerate. Unity rects are
    bottom-left origin."""
    match = _RECT_RE.search(asset_path.read_text(encoding="utf-8", errors="replace"))
    if not match:
        return None
    x, y, w, h = (int(float(g)) for g in match.groups())
    return (x, y, w, h) if w > 0 and h > 0 else None  # a zero-size rect is unusable


def _animation_frames(sprite_dir: Path, base: str) -> list[Path]:
    """The per-frame sprite assets <base>_0.asset … in numeric order; [] if this isn't a sliced animation. The suffix
    must be PURELY numeric — `armpit_1_clean_*` must not also pick up `armpit_1_clean_redness_*` (a different sprite)."""
    pattern = re.compile(rf"^{re.escape(base)}_(\d+)\.asset$")
    numbered = []
    for path in sprite_dir.glob(f"{base}_*.asset"):
        m = pattern.match(path.name)
        if m:
            numbered.append((int(m.group(1)), path))
    return [path for _, path in sorted(numbered)]


def _best_animation_frame(sheet: Image.Image, sprite_dir: Path, base: str) -> Image.Image | None:
    """Crop every animation frame from the sheet and return the one where the feature is most present (max difference
    from the loop's plain baseline frame). None if the sprite isn't a sliced animation (caller uses the whole texture)."""
    frame_assets = _animation_frames(sprite_dir, base)
    crops: list[Image.Image] = []
    sheet_h, sheet_w = sheet.height, sheet.width
    for asset in frame_assets:
        rect = _read_sprite_rect(asset)
        if rect is None:
            continue  # skip a frame with missing/degenerate slice data; the others still give a good still
        x, y, w, h = rect
        if x < 0 or y < 0 or x + w > sheet_w or y + h > sheet_h:
            continue  # rect outside the sheet — ignore rather than crop garbage
        crops.append(sheet.crop((x, sheet_h - (y + h), x + w, sheet_h - y)))  # Y-flip to PIL top-left origin
    if len(crops) < 2:
        return None  # not enough frames to choose from — caller uses the whole texture
    baseline = crops[0].convert("RGB")
    scores = [sum(ImageStat.Stat(ImageChops.difference(c.convert("RGB"), baseline)).mean) for c in crops]
    return crops[max(range(len(crops)), key=lambda i: scores[i])]


def build_manifest(
    mapping_csv: Path, texture_dir: Path, prepared_dir: Path, output: Path, sprite_dir: Path | None = None
) -> list[Task]:
    prepared_dir.mkdir(parents=True, exist_ok=True)
    grouped: dict[tuple[str, str, tuple[str, ...]], set[str]] = defaultdict(set)

    with mapping_csv.open(encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            if _is_parser_noise(row["character"]):
                continue
            sign = row["sign"].upper()
            side = row["side"].lower()
            sprites = _split_sprites(row["sprites"])
            if sign in {"ARMPIT", "EAR"} and sprites:
                sprites = [sprites[-1]]
            if not sprites:
                continue
            if sign == "EYE":
                # The only static eye tell is bloodshot, which lives on the WHITE (sclera) — confirmed: a white's
                # bloodshot verdict is stable across every pupil it's paired with, and the game freely mixes whites and
                # pupils (e.g. fake_white + human_pupil for Widow/CultistOne/Intruder/Tourist). So we KEY each eye by its
                # white sprite. But the player sees the WHOLE composited eye, so the judged IMAGE composites the white
                # with ONE neutral reference pupil (the pupil doesn't affect redness; a single human pupil renders a
                # natural-looking complete eye for any white). Keyed by the white alone (sprites[0]).
                EYE_REF_PUPIL = "human_pupil_50"
                for sprite in sprites:
                    if "pupil" in sprite.lower():
                        continue
                    grouped[("EYE-WHITE", side, (sprite, EYE_REF_PUPIL))].add(row["character"])
            else:
                for sprite in sprites:
                    grouped[(sign, side, (sprite,))].add(row["character"])

    tasks: list[Task] = []
    unresolved: list[str] = []
    for (sign, side, sprite_tuple), characters in sorted(grouped.items()):
        sprites = list(sprite_tuple)
        resolved: list[Path] = []
        warnings: list[str] = []
        for sprite in sprites:
            path = _find_texture(texture_dir, sprite, allow_frame_fallback=sign in {"ARMPIT", "EAR"})
            if path is None:
                warnings.append(f"Missing texture: {sprite}.png")
                unresolved.append(f"{sign}/{side}/{sprite}")
            else:
                resolved.append(path)
        if not resolved:
            continue

        task_id = _safe_id(f"{sign}_{side}_{'_'.join(sprites)}")
        prepared = prepared_dir / f"{task_id}.png"

        # Armpit/ear: the resolved texture is an animation SHEET. Crop the single best (most-feature-present) frame using
        # the sliced Sprite assets, instead of flattening the whole tiled sheet. Falls back to the whole texture when the
        # sprite isn't a sliced animation (a static single-frame ear) or the slice data is missing.
        anim_frame: Image.Image | None = None
        if sign in {"ARMPIT", "EAR"} and sprite_dir is not None and len(resolved) == 1:
            base = re.sub(r"_\d+$", "", re.sub(r"\s*\(\d+\)$", "", sprites[0]))
            with Image.open(resolved[0]) as sheet:
                anim_frame = _best_animation_frame(sheet.convert("RGBA"), sprite_dir, base)

        if anim_frame is not None:
            _flatten(anim_frame).save(prepared)
        elif len(resolved) == 1:
            with Image.open(resolved[0]) as image:
                _flatten(image).save(prepared)
        else:
            _composite(resolved).save(prepared)

        tasks.append(
            Task(
                id=task_id,
                sign=sign,
                side=side,
                characters=sorted(characters),
                sprites=sprites,
                sources=[str(path.resolve()) for path in resolved],
                prepared_image=str(prepared.resolve()),
                source_hash=_sha256(resolved),
                warnings=warnings,
            )
        )

    if unresolved:
        preview = ", ".join(unresolved[:10])
        remainder = f" (+{len(unresolved) - 10} more)" if len(unresolved) > 10 else ""
        raise FileNotFoundError(f"Could not resolve {len(unresolved)} mapped textures: {preview}{remainder}")

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps([task.as_dict() for task in tasks], indent=2), encoding="utf-8")
    return tasks


_PROMPT_SUBJECT = {
    "EYE-WHITE": "the whites of the eyes (the sclera, the area around the iris)",
    "AURAPHOTO": "the aura photo",
}

# Optional extra guidance appended to the appearance prompt.
_APPEARANCE_HINT = {
    # Redness/irritation is something the player looks for, and it's carried by the "red" skin pick — so cue it.
    "HANDS": "If the skin looks red or irritated, pick skin-flushed.",
    "ARMPIT": "If the skin looks red or irritated, pick skin-flushed.",
    "EAR": "If the ear looks red or irritated, pick ear-flushed.",
}


def _appearance_prompt(sign: str) -> str:
    """Ask the model for the ONE dominant appearance label — always answerable, so the readout never falls back."""
    labels = SIGN_APPEARANCE.get(sign, ())
    subject = _PROMPT_SUBJECT.get(sign, sign.lower())
    hint = _APPEARANCE_HINT.get(sign, "")
    hint = (" " + hint) if hint else ""
    return f"Look at {subject}. Pick the ONE label that best matches its colour. Reply with one label only: {', '.join(labels)}.{hint}"


def _tell_prompt(sign: str) -> str:
    """Ask for any of the additive findings (zero or more). Empty reply = no finding (the appearance clause stands)."""
    labels = SIGN_TRAITS.get(sign, ())
    subject = _PROMPT_SUBJECT.get(sign, sign.lower())
    return (
        f"Inspect {subject}. Reply with every label that is clearly visible, or 'none' if none apply. "
        f"Labels: {', '.join(labels)}, none."
    )


def _prompt(sign: str) -> str:
    """Single string used for the prompt-hash cache key; covers every sub-prompt the sign uses (appearance, the armpit
    hair question, and tells) so a change to any of them forces regeneration."""
    parts = [_appearance_prompt(sign), _tell_prompt(sign)]
    if sign == "ARMPIT":
        parts.append(_hair_prompt())
    return " || ".join(parts)


def _parse_appearance(sign: str, model_output: str) -> str | None:
    """The single best appearance label (pick-one). Priority order is the declared label order, so if the model hedges
    and returns several, the first DECLARED label it mentions wins. None only if the model returned nothing usable."""
    lowered = model_output.casefold()
    for label in SIGN_APPEARANCE.get(sign, ()):
        pattern = r"\b" + r"[-_\s]+".join(re.escape(part) for part in label.split("-")) + r"\b"
        if re.search(pattern, lowered):
            return label
    return None


def _parse_traits(sign: str, model_output: str) -> list[str]:
    lowered = model_output.casefold()
    found: list[str] = []
    for label in SIGN_TRAITS.get(sign, ()):
        pattern = r"\b" + r"[-_\s]+".join(re.escape(part) for part in label.split("-")) + r"\b"
        if re.search(pattern, lowered):
            found.append(label)
    return found


# Some armpit/ear features are encoded in the SPRITE NAME by the game (fungal/wet, cockroach/burnt/injury), which is
# ground truth — the vision model misreads them. So for these signs we take the present-only features from the FILENAME,
# not the model. Each entry: substring in the name -> tell label. Order within a sign doesn't matter (every matching
# tell is added). Only PRESENT features are listed; absence is never stated.
_NAME_TELLS = {
    "ARMPIT": {
        "fungal": "fungal-growth",
        "wet": "wet",
    },
    "EAR": {
        "cockroach": "insect",
        "injury": "injured",
        "burnt": "burned",
    },
}


def _name_traits(sign: str, sprites: list[str]) -> list[str]:
    """Present-only tells derived from the sprite filename for armpit/ear (authoritative; the vision model misreads
    these). Returns [] for signs not in _NAME_TELLS so the caller keeps using the model's tells."""
    table = _NAME_TELLS.get(sign)
    if not table:
        return []
    name = " ".join(sprites).casefold()
    return [label for key, label in table.items() if key in name]


def _hair_prompt() -> str:
    """The armpit hair question (its own one-pick call). Model-first; the filename is only a backup."""
    return f"Look at the armpit. Is it hairy or smooth? Reply with one word only: {', '.join(ARMPIT_HAIR_LABELS)}."


def _hair_phrase(model_output: str, sprites: list[str]) -> str:
    """The armpit hair clause, stated both ways. Trust the MODEL'S read first; fall back to the sprite name ('hairy'
    substring => hairy, else smooth) only when the model didn't clearly answer."""
    lowered = model_output.casefold()
    for label in ARMPIT_HAIR_LABELS:
        if re.search(rf"\b{label}\b", lowered):
            return ARMPIT_HAIR_PHRASE[label]
    # Model gave nothing usable — back up with the filename, which encodes hair as "hairy" vs the clean/clear default.
    name = " ".join(sprites).casefold()
    return ARMPIT_HAIR_PHRASE["hairy" if "hairy" in name else "smooth"]


def _render_description(sign: str, appearance: str | None, traits: list[str], name_appearance: str = "") -> str:
    """Compose the spoken description: the colour/condition appearance first, then any name-derived appearance clause
    (e.g. smooth/hairy), then the present-only tell clauses. Always concrete — no empty/placeholder outcome."""
    clauses: list[str] = []
    appearance_phrase = APPEARANCE_PHRASES.get(sign, {}).get(appearance or "")
    if appearance_phrase:
        clauses.append(appearance_phrase)
    if name_appearance:
        clauses.append(name_appearance)
    clauses.extend(phrase for trait in traits if (phrase := TRAIT_PHRASES.get(sign, {}).get(trait)))
    if not clauses:
        return ""
    return "; ".join(clauses).capitalize() + "."


def _trait_issues(sign: str, appearance: str | None, traits: list[str]) -> list[str]:
    issues: list[str] = []
    # The appearance label is the always-present baseline; without it the readout has no description to fall back on
    # (which is the exact failure mode we're removing). Flag it for review so it's never silently empty.
    if SIGN_APPEARANCE.get(sign) and appearance is None:
        issues.append("No appearance label resolved")
    return issues


def _issues(text: str) -> list[str]:
    lowered = text.lower()
    found = [f"Contains forbidden word: {word}" for word in FORBIDDEN_WORDS if re.search(rf"\b{word}\b", lowered)]
    found.extend(
        f"Contains subjective word: {word}"
        for word in SUBJECTIVE_WORDS
        if word not in FORBIDDEN_WORDS and re.search(rf"\b{word}\b", lowered)
    )
    if len(text) > 300:
        found.append("Description exceeds 300 characters")
    if "\n" in text:
        found.append("Description contains multiple lines")
    return found


def describe_manifest(
    manifest: Path,
    output_json: Path,
    output_csv: Path,
    client: OllamaClient,
    limit: int | None = None,
    per_sign: int | None = None,
) -> list[dict[str, object]]:
    tasks = json.loads(manifest.read_text(encoding="utf-8"))
    if per_sign is not None:
        selected: list[dict[str, object]] = []
        sign_counts: dict[str, int] = defaultdict(int)
        for task in tasks:
            if sign_counts[task["sign"]] >= per_sign:
                continue
            selected.append(task)
            sign_counts[task["sign"]] += 1
        tasks = selected
    existing: dict[str, dict[str, object]] = {}
    if output_json.is_file():
        existing = {item["id"]: item for item in json.loads(output_json.read_text(encoding="utf-8"))}
    task_ids = {task["id"] for task in tasks}
    remaining_existing = {item_id: item for item_id, item in existing.items() if item_id in task_ids}

    results: list[dict[str, object]] = []
    processed = 0
    for task in tasks:
        prompt = _prompt(task["sign"])
        prompt_hash = hashlib.sha256(prompt.encode("utf-8")).hexdigest()
        prior = remaining_existing.get(task["id"])
        if (
            prior
            and prior.get("source_hash") == task["source_hash"]
            and prior.get("model") == client.model
            and prior.get("prompt_hash") == prompt_hash
        ):
            remaining_existing.pop(task["id"], None)
            results.append(prior)
            continue
        if limit is not None and processed >= limit:
            continue

        # APPEARANCE: always-answerable colour/condition pick from the model (so the readout never falls back).
        appearance_output = ""
        appearance = None
        if SIGN_APPEARANCE.get(task["sign"]):
            appearance_output = client.describe(task["prepared_image"], _appearance_prompt(task["sign"]))
            appearance = _parse_appearance(task["sign"], appearance_output)

        # ARMPIT HAIR: a second appearance clause stated both ways. Trust the model's smooth/hairy read first; the
        # sprite name backs it up only when the model didn't answer.
        name_appearance = ""
        hair_output = ""
        if task["sign"] == "ARMPIT":
            hair_output = client.describe(task["prepared_image"], _hair_prompt())
            name_appearance = _hair_phrase(hair_output, task["sprites"])

        # PRESENT-ONLY FEATURES: for armpit/ear the sprite NAME is ground truth for incidental features (fungal/insect/
        # burn/injury — the model misreads these), so derive from the filename and skip the tell call. Others: ask model.
        if task["sign"] in _NAME_TELLS:
            traits = _name_traits(task["sign"], task["sprites"])
            tell_output = f"(from sprite name: {';'.join(task['sprites'])})"
        else:
            tell_output = client.describe(task["prepared_image"], _tell_prompt(task["sign"]))
            traits = _parse_traits(task["sign"], tell_output)
        description = _render_description(task["sign"], appearance, traits, name_appearance)
        model_output = f"appearance: {appearance_output} | hair: {hair_output} | tells: {tell_output}"
        result = {
            **task,
            "appearance": appearance,
            "traits": traits,
            "model_output": model_output,
            "description": description,
            "model": client.model,
            "prompt_hash": prompt_hash,
            "status": "candidate",
            "validation_issues": _trait_issues(task["sign"], appearance, traits) + _issues(description),
        }
        remaining_existing.pop(task["id"], None)
        results.append(result)
        processed += 1
        _write_results(results + list(remaining_existing.values()), output_json, output_csv)

    _write_results(results + list(remaining_existing.values()), output_json, output_csv)
    return results


def _write_results(results: list[dict[str, object]], output_json: Path, output_csv: Path) -> None:
    output_json.parent.mkdir(parents=True, exist_ok=True)
    output_csv.parent.mkdir(parents=True, exist_ok=True)
    output_json.write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
    fields = ["id", "sign", "side", "characters", "sprites", "appearance", "traits", "model_output", "description", "status", "validation_issues", "prepared_image", "source_hash", "model", "prompt_hash"]
    with output_csv.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        for item in results:
            row = dict(item)
            for key in ("characters", "sprites", "traits", "validation_issues"):
                row[key] = "; ".join(row.get(key, []))
            writer.writerow(row)


def copy_manifest_images(manifest: Path, destination: Path, limit: int | None = None) -> int:
    """Copy prepared images to a portable review directory."""

    tasks = json.loads(manifest.read_text(encoding="utf-8"))
    destination.mkdir(parents=True, exist_ok=True)
    count = 0
    for task in tasks[:limit]:
        source = Path(task["prepared_image"])
        shutil.copy2(source, destination / source.name)
        count += 1
    return count
