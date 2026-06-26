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

from PIL import Image

from .ollama import OllamaClient

FORBIDDEN_WORDS = ("human", "imposter", "visitor", "safe", "dangerous")
PREPARATION_VERSION = "2-minimum-384"
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
    # EYE-WHITE describes the SCLERA (the white around the iris): is it clear/white, pink-tinged, or red/bloodshot.
    "EYE-WHITE": ("sclera-red", "sclera-pink", "sclera-clear"),
    "HANDS": ("skin-pale", "skin-tan", "skin-dark", "skin-flushed", "skin-grey"),
    # NOTE: no plain "white" here — strikingly bright-white teeth are the gameplay TELL (white-teeth), kept in the tell
    # group so it isn't double-rated. The appearance baseline covers only the natural shades.
    "TEETH": ("teeth-offwhite", "teeth-yellow", "teeth-stained", "teeth-grey"),
    "AURAPHOTO": ("photo-dark", "photo-bright", "photo-muted"),
    "ARMPIT": ("skin-pale", "skin-tan", "skin-dark", "skin-flushed", "skin-grey"),
    "EAR": ("ear-pale", "ear-tan", "ear-dark", "ear-flushed", "ear-grey"),
}

APPEARANCE_PHRASES = {
    "EYE-WHITE": {
        "sclera-red": "the whites of the eyes are red and bloodshot",
        "sclera-pink": "the whites of the eyes are faintly pink",
        "sclera-clear": "the whites of the eyes are clear and white",
    },
    "HANDS": {
        "skin-pale": "the skin of the hands is pale",
        "skin-tan": "the skin of the hands is tan",
        "skin-dark": "the skin of the hands is dark",
        "skin-flushed": "the skin of the hands is flushed red",
        "skin-grey": "the skin of the hands is greyish",
    },
    "TEETH": {
        "teeth-offwhite": "the teeth are an ordinary off-white",
        "teeth-yellow": "the teeth are yellowed",
        "teeth-stained": "the teeth are stained and discoloured",
        "teeth-grey": "the teeth are greyish",
    },
    "AURAPHOTO": {
        "photo-dark": "the aura photo is mostly dark",
        "photo-bright": "the aura photo is brightly lit",
        "photo-muted": "the aura photo is muted and washed out",
    },
    "ARMPIT": {
        "skin-pale": "the armpit skin is pale",
        "skin-tan": "the armpit skin is tan",
        "skin-dark": "the armpit skin is dark",
        "skin-flushed": "the armpit skin is flushed red",
        "skin-grey": "the armpit skin is greyish",
    },
    "EAR": {
        "ear-pale": "the ear is pale",
        "ear-tan": "the ear is tan",
        "ear-dark": "the ear is dark",
        "ear-flushed": "the ear is flushed red",
        "ear-grey": "the ear is greyish",
    },
}

# TELLS — additive findings layered after the appearance clause. No "none": absence of a tell simply means no tell
# clause is added (the appearance clause still stands alone).
SIGN_TRAITS = {
    "EYE-WHITE": (),  # the only static white tell (bloodshot) is now carried by the sclera-red appearance label
    "HANDS": ("dirty-nails", "irritated-skin", "unusual-fingers", "injured", "foreign-object"),
    "TEETH": ("white-teeth", "bleeding-gums", "damaged-teeth", "gaps", "foreign-object"),
    "AURAPHOTO": ("black-patches", "blurred", "colored-glow", "extra-silhouettes"),
    "ARMPIT": ("hair-present", "hair-absent", "irritated-skin", "fungal-growth", "wet", "injured"),
    "EAR": ("insect", "injured", "burned", "discharge", "foreign-object"),
}

TRAIT_PHRASES = {
    "HANDS": {
        "dirty-nails": "dirt is visible under the fingernails",
        "irritated-skin": "the hand skin is visibly red or irritated",
        "unusual-fingers": "the fingers have an unusual visible count or shape",
        "injured": "the hands have a visible injury",
        "foreign-object": "a foreign object is visible on the hands",
    },
    "TEETH": {
        "white-teeth": "the teeth are strikingly bright white",
        "bleeding-gums": "the gums are visibly bleeding",
        "damaged-teeth": "one or more teeth are visibly damaged",
        "gaps": "visible gaps separate some teeth",
        "foreign-object": "a foreign object is visible among the teeth",
    },
    "AURAPHOTO": {
        "black-patches": "black patches are visible in the aura photo",
        "blurred": "the aura photo is visibly blurred",
        "colored-glow": "a colored glow is visible around the figure",
        "extra-silhouettes": "additional silhouettes are visible in the photo",
    },
    "ARMPIT": {
        "hair-present": "hair is visible in the armpit",
        "hair-absent": "no hair is visible in the armpit",
        "irritated-skin": "the armpit skin is visibly red or irritated",
        "fungal-growth": "fungal-looking growth is visible on the armpit skin",
        "wet": "visible moisture is present in the armpit",
        "injured": "the armpit has a visible injury",
    },
    "EAR": {
        "insect": "an insect is visible inside the ear",
        "injured": "the ear has a visible injury",
        "burned": "the ear is visibly burned or charred",
        "discharge": "visible discharge is present in the ear",
        "foreign-object": "a foreign object is visible inside the ear",
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


def build_manifest(mapping_csv: Path, texture_dir: Path, prepared_dir: Path, output: Path) -> list[Task]:
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
        if len(resolved) == 1:
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

# Optional extra guidance appended to the appearance prompt, where the pick-one could otherwise collide with a tell.
_APPEARANCE_HINT = {
    # Reserve "strikingly bright/unnatural white" for the tell question; here pick the natural shade only.
    "TEETH": "If the teeth look ordinary, pick teeth-offwhite; only the colour matters here, not brightness.",
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
    """Back-compat single string used for the prompt-hash cache key; covers both sub-prompts so a change to either
    forces regeneration."""
    return _appearance_prompt(sign) + " || " + _tell_prompt(sign)


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


# Armpit and ear tells are encoded in the SPRITE NAME by the game (clean/hairy/fungal/redness, cockroach/burnt/injury),
# which is ground truth — the vision model misreads them (it called hairless "clean" armpits hairy). So for these signs
# we take the tells from the FILENAME, not the model. Each entry: substring in the name -> tell label. Order within a
# sign doesn't matter (all matching tells are added); hair-absent is only added when no "hairy" appears (see below).
_NAME_TELLS = {
    "ARMPIT": {
        "hairy": "hair-present",
        "fungal": "fungal-growth",
        "redness": "irritated-skin",
        "wet": "wet",
    },
    "EAR": {
        "cockroach": "insect",
        "injury": "injured",
        "burnt": "burned",
    },
}


def _name_traits(sign: str, sprites: list[str]) -> list[str]:
    """Tells derived from the sprite filename for armpit/ear (authoritative; the vision model misreads these). Returns
    [] for signs not in _NAME_TELLS so the caller keeps using the model's tells."""
    table = _NAME_TELLS.get(sign)
    if not table:
        return []
    name = " ".join(sprites).casefold()
    found = [label for key, label in table.items() if key in name]
    # Armpit hair is binary: "hairy" in the name => hair-present, otherwise the clean/clear side => hair-absent.
    if sign == "ARMPIT" and "hair-present" not in found:
        found.insert(0, "hair-absent")
    return found


def _coherent_traits(sign: str, appearance: str | None, traits: list[str]) -> list[str]:
    """Drop tells that contradict the appearance pick, so the readout never says two opposite things. The vision model
    (both 4B and 8B) calls almost every tooth 'strikingly bright white', so that tell is kept only when the appearance
    baseline did NOT already judge the teeth a non-white shade — 'stained and discoloured; strikingly bright white' is
    incoherent. Appearance is the more reliable signal for teeth colour, so it wins the conflict."""
    if sign == "TEETH" and "white-teeth" in traits and appearance in {"teeth-yellow", "teeth-stained", "teeth-grey"}:
        return [t for t in traits if t != "white-teeth"]
    return traits


def _render_description(sign: str, appearance: str | None, traits: list[str]) -> str:
    """Compose the spoken description: the appearance clause first (always present when resolved), then any tell
    clauses. Guarantees a concrete description — there is no empty/placeholder outcome when appearance resolves."""
    clauses: list[str] = []
    # The "strikingly bright white" TELL supersedes the neutral off-white baseline (don't say "ordinary off-white;
    # strikingly bright white"); the tell clause alone carries the teeth colour in that case.
    suppress_appearance = sign == "TEETH" and appearance == "teeth-offwhite" and "white-teeth" in traits
    appearance_phrase = "" if suppress_appearance else APPEARANCE_PHRASES.get(sign, {}).get(appearance or "")
    if appearance_phrase:
        clauses.append(appearance_phrase)
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
    if sign == "ARMPIT" and "hair-present" in traits and "hair-absent" in traits:
        issues.append("Conflicting armpit hair labels")
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

        # TELLS: for armpit/ear the sprite NAME is ground truth (the model misreads hair/insects), so derive from the
        # filename and skip the tell call entirely. For the other signs, ask the model, then drop tells that contradict
        # the appearance (e.g. the chronically over-fired teeth "bright white" against a stained-teeth appearance).
        name_traits = _name_traits(task["sign"], task["sprites"])
        if name_traits or task["sign"] in _NAME_TELLS:
            traits = name_traits
            tell_output = f"(from sprite name: {';'.join(task['sprites'])})"
        else:
            tell_output = client.describe(task["prepared_image"], _tell_prompt(task["sign"]))
            traits = _coherent_traits(task["sign"], appearance, _parse_traits(task["sign"], tell_output))
        description = _render_description(task["sign"], appearance, traits)
        model_output = f"appearance: {appearance_output} | tells: {tell_output}"
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
