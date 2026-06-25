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
SIGN_TRAITS = {
    "EYE": ("bloodshot", "pupil-unusual", "injured", "foreign-object", "none"),
    "HANDS": ("dirty-nails", "irritated-skin", "unusual-fingers", "injured", "foreign-object", "none"),
    "TEETH": ("white-teeth", "bleeding-gums", "damaged-teeth", "gaps", "foreign-object", "none"),
    "AURAPHOTO": ("black-patches", "blurred", "colored-glow", "extra-silhouettes", "none"),
    "ARMPIT": ("hair-present", "hair-absent", "irritated-skin", "fungal-growth", "wet", "injured", "none"),
    "EAR": ("insect", "injured", "burned", "discharge", "foreign-object", "none"),
}

TRAIT_PHRASES = {
    "EYE": {
        "bloodshot": "the eyes are bloodshot",
        "pupil-unusual": "the pupils have a visibly unusual position or shape",
        "injured": "the eye area has a visible injury",
        "foreign-object": "a foreign object is visible in the eye",
        "none": "none of the listed static eye signs is visible",
    },
    "HANDS": {
        "dirty-nails": "dirt is visible under the fingernails",
        "irritated-skin": "the hand skin is visibly red or irritated",
        "unusual-fingers": "the fingers have an unusual visible count or shape",
        "injured": "the hands have a visible injury",
        "foreign-object": "a foreign object is visible on the hands",
        "none": "none of the listed hand signs is visible",
    },
    "TEETH": {
        "white-teeth": "the teeth are bright white",
        "bleeding-gums": "the gums are visibly bleeding",
        "damaged-teeth": "one or more teeth are visibly damaged",
        "gaps": "visible gaps separate some teeth",
        "foreign-object": "a foreign object is visible among the teeth",
        "none": "none of the listed tooth or gum signs is visible",
    },
    "AURAPHOTO": {
        "black-patches": "black patches are visible in the aura photo",
        "blurred": "the aura photo is visibly blurred",
        "colored-glow": "a colored glow is visible around the figure",
        "extra-silhouettes": "additional silhouettes are visible in the photo",
        "none": "none of the listed aura-photo signs is visible",
    },
    "ARMPIT": {
        "hair-present": "hair is visible in the armpit",
        "hair-absent": "no hair is visible in the armpit",
        "irritated-skin": "the armpit skin is visibly red or irritated",
        "fungal-growth": "fungal-looking growth is visible on the armpit skin",
        "wet": "visible moisture is present in the armpit",
        "injured": "the armpit has a visible injury",
        "none": "none of the listed armpit signs is visible",
    },
    "EAR": {
        "insect": "an insect is visible inside the ear",
        "injured": "the ear has a visible injury",
        "burned": "the ear is visibly burned or charred",
        "discharge": "visible discharge is present in the ear",
        "foreign-object": "a foreign object is visible inside the ear",
        "none": "none of the listed ear signs is visible",
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
                grouped[(sign, side, tuple(sprites))].add(row["character"])
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


def _prompt(sign: str) -> str:
    labels = SIGN_TRAITS.get(sign, ("none",))
    return f"Inspect only the {sign.lower()}. Reply with labels only: {', '.join(labels)}."


def _parse_traits(sign: str, model_output: str) -> list[str]:
    lowered = model_output.casefold()
    found: list[str] = []
    for label in SIGN_TRAITS.get(sign, ()):
        pattern = r"\b" + r"[-_\s]+".join(re.escape(part) for part in label.split("-")) + r"\b"
        if re.search(pattern, lowered):
            found.append(label)
    return found


def _render_traits(sign: str, traits: list[str]) -> str:
    phrases = TRAIT_PHRASES.get(sign, {})
    selected = [phrases[trait] for trait in traits if trait in phrases]
    if not selected:
        return ""
    return "; ".join(selected).capitalize() + "."


def _trait_issues(sign: str, traits: list[str]) -> list[str]:
    issues: list[str] = []
    if not traits:
        issues.append("No recognized trait label")
    if "none" in traits and len(traits) > 1:
        issues.append("The none label conflicts with visible trait labels")
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

        model_output = client.describe(task["prepared_image"], prompt)
        traits = _parse_traits(task["sign"], model_output)
        description = _render_traits(task["sign"], traits)
        result = {
            **task,
            "traits": traits,
            "model_output": model_output,
            "description": description,
            "model": client.model,
            "prompt_hash": prompt_hash,
            "status": "candidate",
            "validation_issues": _trait_issues(task["sign"], traits) + _issues(description),
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
    fields = ["id", "sign", "side", "characters", "sprites", "traits", "model_output", "description", "status", "validation_issues", "prepared_image", "source_hash", "model", "prompt_hash"]
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
