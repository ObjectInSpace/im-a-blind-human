import csv
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from image_tools.pipeline import (
    _appearance_prompt,
    _hair_phrase,
    _issues,
    _name_traits,
    _parse_appearance,
    _parse_traits,
    _prompt,
    _render_description,
    _tell_prompt,
    build_manifest,
    describe_manifest,
)


def _is_appearance(prompt: str) -> bool:
    """The appearance sub-prompt asks for the ONE colour label; the tell sub-prompt asks for every visible label."""
    return "Pick the ONE label" in prompt


class _Client:
    model = "test-model"

    def describe(self, _path, prompt):
        # Two calls per image: appearance (pick-one) then tells. Answer each in kind.
        return "teeth-yellow" if _is_appearance(prompt) else "bleeding-gums"


class _FailingClient:
    """Interrupts the run after the FIRST task fully completes. Each task makes two calls (appearance + tell), so the
    first task uses calls 1-2 and the second task's appearance call (call 3) raises — leaving the first task
    checkpointed alongside any cached entry."""

    model = "test-model"

    def __init__(self):
        self.calls = 0

    def describe(self, _path, prompt):
        self.calls += 1
        if self.calls == 3:
            raise RuntimeError("interrupted")
        return "teeth-yellow" if _is_appearance(prompt) else "bleeding-gums"


class PipelineTests(unittest.TestCase):
    def test_validator_flags_subjective_judgments(self):
        issues = _issues("The skin has a natural, healthy appearance.")
        self.assertIn("Contains subjective word: natural", issues)
        self.assertIn("Contains subjective word: healthy", issues)

    def test_prompts_define_evidence_for_each_sign(self):
        self.assertIn("dirty-nails", _prompt("HANDS"))
        self.assertIn("bleeding-gums", _prompt("TEETH"))
        self.assertIn("black-patches", _prompt("AURAPHOTO"))
        self.assertIn("fungal-growth", _prompt("ARMPIT"))
        self.assertIn("insect", _prompt("EAR"))

    def test_appearance_prompt_offers_the_colour_labels(self):
        self.assertIn("sclera-white", _appearance_prompt("EYE-WHITE"))
        self.assertIn("teeth-yellow", _appearance_prompt("TEETH"))

    def test_appearance_and_tells_compose_into_one_description(self):
        appearance = _parse_appearance("TEETH", "teeth-yellow")
        traits = _parse_traits("TEETH", "bleeding-gums")
        self.assertEqual("teeth-yellow", appearance)
        self.assertEqual(["bleeding-gums"], traits)
        self.assertEqual(
            "The teeth are yellow; the gums are bleeding.",
            _render_description("TEETH", appearance, traits),
        )

    def test_normal_image_describes_the_normal_traits(self):
        # Abnormal -> mention it; normal -> describe the normal traits, neutrally and never empty.
        appearance = _parse_appearance("EYE-WHITE", "sclera-white")
        self.assertEqual("sclera-white", appearance)
        self.assertEqual(
            "The whites of the eyes are white.",
            _render_description("EYE-WHITE", appearance, []),
        )

    def test_colour_is_stated_both_ways_neutrally(self):
        # A red sclera and a white sclera are both described plainly, with no emphasis either way.
        self.assertEqual("The whites of the eyes are red.", _render_description("EYE-WHITE", "sclera-red", []))
        self.assertEqual("The teeth are white.", _render_description("TEETH", "teeth-white", []))

    def test_appearance_pick_one_respects_priority_order(self):
        # If the model hedges and names several, the first DECLARED label wins (sclera-red before sclera-white).
        self.assertEqual("sclera-red", _parse_appearance("EYE-WHITE", "sclera-white but also sclera-red"))

    def test_armpit_hair_trusts_the_model_then_falls_back_to_the_name(self):
        # Hair is part of the armpit's appearance, stated neutrally both ways. The MODEL's read wins...
        self.assertEqual("the armpit is hairy", _hair_phrase("hairy", ["armpit_1_clean_5"]))
        self.assertEqual("the armpit is smooth", _hair_phrase("smooth", ["armpit_2_hairy_fungal_5"]))
        # ...and the sprite name is only a backup when the model gives no usable answer.
        self.assertEqual("the armpit is smooth", _hair_phrase("(no idea)", ["armpit_1_clean_5"]))
        self.assertEqual("the armpit is hairy", _hair_phrase("", ["armpit_2_hairy_fungal_5"]))
        # Composed: skin tone, then hair, then any present-only feature.
        self.assertEqual(
            "The armpit skin is pale; the armpit is smooth.",
            _render_description("ARMPIT", "skin-pale", [], _hair_phrase("smooth", ["armpit_1_clean_5"])),
        )
        self.assertEqual(
            "The armpit skin is red; the armpit is hairy; there is a fungal growth on the skin.",
            _render_description(
                "ARMPIT", "skin-flushed",
                _name_traits("ARMPIT", ["armpit_2_hairy_fungal_5"]),
                _hair_phrase("hairy", ["armpit_2_hairy_fungal_5"]),
            ),
        )

    def test_armpit_ear_present_features_come_from_the_sprite_name(self):
        # Present-only features for armpit/ear are taken from the filename (the model misreads them).
        self.assertEqual(["fungal-growth"], _name_traits("ARMPIT", ["armpit_2_hairy_fungal_5"]))
        self.assertEqual(["insect"], _name_traits("EAR", ["fake_ear_cockroach_19"]))
        self.assertEqual(["burned"], _name_traits("EAR", ["fake_ear_burnt"]))
        # A clean armpit / plain ear yields no present-feature clause (absence is never stated).
        self.assertEqual([], _name_traits("ARMPIT", ["armpit_1_clean_5"]))
        # A sign whose features are NOT name-encoded returns nothing (caller keeps the model's tells).
        self.assertEqual([], _name_traits("TEETH", ["human_tooth_1"]))

    def test_prepare_uses_last_animation_frame_and_composites_eye(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            textures = root / "textures"
            textures.mkdir()
            # An EYE row is keyed by its WHITE alone, composited with a fixed reference pupil (human_pupil_50), so the
            # reference pupil must resolve as a texture; the row's own pupil sprite is dropped.
            for name, color in (
                ("armpit_0", "red"),
                ("armpit_1", "blue"),
                ("white", "white"),
                ("pupil", "black"),
                ("human_pupil_50", "black"),
            ):
                Image.new("RGBA", (10, 10), color).save(textures / f"{name}.png")
            mapping = root / "mapping.csv"
            with mapping.open("w", newline="", encoding="utf-8") as handle:
                writer = csv.DictWriter(handle, fieldnames=["character", "sign", "side", "sprites"])
                writer.writeheader()
                writer.writerow({"character": "A", "sign": "ARMPIT", "side": "human", "sprites": "armpit_0; armpit_1"})
                writer.writerow({"character": "A", "sign": "EYE", "side": "human", "sprites": "white; pupil"})
                writer.writerow({"character": "Font SDF", "sign": "EYE", "side": "human", "sprites": "white; pupil"})
            tasks = build_manifest(mapping, textures, root / "prepared", root / "manifest.json")
            self.assertEqual(2, len(tasks))
            armpit = next(task for task in tasks if task.sign == "ARMPIT")
            # The EYE row groups under EYE-WHITE: the white plus the fixed reference pupil, with the row's own pupil dropped.
            eye = next(task for task in tasks if task.sign == "EYE-WHITE")
            self.assertEqual(["armpit_1"], armpit.sprites)
            self.assertEqual(["white", "human_pupil_50"], eye.sprites)
            self.assertTrue(Path(eye.prepared_image).is_file())
            self.assertNotIn("Font SDF", eye.characters)

    def test_prepare_fails_when_a_mapped_texture_is_missing(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            textures = root / "textures"
            textures.mkdir()
            mapping = root / "mapping.csv"
            mapping.write_text("character,sign,side,sprites\nA,TEETH,human,missing_tooth\n", encoding="utf-8")
            with self.assertRaises(FileNotFoundError):
                build_manifest(mapping, textures, root / "prepared", root / "manifest.json")

    def test_describe_writes_json_and_csv(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            image = root / "image.png"
            Image.new("RGB", (10, 10), "white").save(image)
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps([{
                "id": "teeth",
                "sign": "TEETH",
                "side": "human",
                "characters": ["A"],
                "sprites": ["tooth"],
                "sources": [str(image)],
                "prepared_image": str(image),
                "source_hash": "abc",
                "warnings": [],
            }]))
            results = describe_manifest(manifest, root / "out.json", root / "out.csv", _Client())
            self.assertEqual("candidate", results[0]["status"])
            self.assertEqual("teeth-yellow", results[0]["appearance"])
            self.assertEqual(["bleeding-gums"], results[0]["traits"])
            self.assertEqual(
                "The teeth are yellow; the gums are bleeding.",
                results[0]["description"],
            )
            self.assertEqual([], results[0]["validation_issues"])
            self.assertTrue((root / "out.csv").is_file())

    def test_checkpoint_preserves_unvisited_cached_entries_on_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            image = root / "image.png"
            Image.new("RGB", (10, 10), "white").save(image)
            tasks = []
            for index in range(3):
                tasks.append({
                    "id": f"teeth-{index}",
                    "sign": "TEETH",
                    "side": "human",
                    "characters": ["A"],
                    "sprites": [f"tooth-{index}"],
                    "sources": [str(image)],
                    "prepared_image": str(image),
                    "source_hash": f"hash-{index}",
                    "warnings": [],
                })
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps(tasks))
            prompt_hash = hashlib.sha256(_prompt("TEETH").encode()).hexdigest()
            cached = {
                **tasks[2],
                "appearance": "teeth-yellow",
                "traits": ["bleeding-gums"],
                "model_output": "appearance: teeth-yellow | tells: bleeding-gums",
                "description": "The teeth are yellow; the gums are bleeding.",
                "model": "test-model",
                "prompt_hash": prompt_hash,
                "status": "candidate",
                "validation_issues": [],
            }
            output_json = root / "out.json"
            output_json.write_text(json.dumps([cached]))
            with self.assertRaises(RuntimeError):
                describe_manifest(manifest, output_json, root / "out.csv", _FailingClient())
            checkpoint_ids = {item["id"] for item in json.loads(output_json.read_text())}
            self.assertEqual({"teeth-0", "teeth-2"}, checkpoint_ids)


if __name__ == "__main__":
    unittest.main()
