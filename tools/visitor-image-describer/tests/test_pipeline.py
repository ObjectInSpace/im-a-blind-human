import csv
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from image_tools.pipeline import (
    _combined_prompt,
    _hair_phrase,
    _issues,
    _name_traits,
    _parse_appearance,
    _parse_traits,
    _prompt,
    _render_description,
    build_manifest,
    describe_manifest,
)


class _Client:
    model = "test-model"

    def describe(self, _path, _prompt):
        # ONE combined call per image now: the reply carries both a colour label and a feature label.
        return "teeth-yellow; bleeding-gums"


class _FailingClient:
    """Interrupts the run on the SECOND describe call. Each task makes ONE combined call now, so the first task uses
    call 1 and the second task's call (call 2) raises — leaving the first task checkpointed alongside any cached entry."""

    model = "test-model"

    def __init__(self):
        self.calls = 0

    def describe(self, _path, _prompt):
        self.calls += 1
        if self.calls == 2:
            raise RuntimeError("interrupted")
        return "teeth-yellow; bleeding-gums"


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

    def test_combined_prompt_offers_both_colour_and_feature_labels(self):
        teeth = _combined_prompt("TEETH")
        self.assertIn("sclera-white", _combined_prompt("EYE-WHITE"))
        self.assertIn("teeth-yellow", teeth)   # colour part
        self.assertIn("bleeding-gums", teeth)   # feature part
        self.assertIn("COLOUR", teeth)
        self.assertIn("FEATURES", teeth)

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

    def test_armpit_hair_is_stated_both_ways_from_the_sprite_name(self):
        # Hair is part of the armpit's appearance, stated neutrally both ways, from the sprite name (authoritative —
        # the model mislabels smooth pits as hairy).
        self.assertEqual("the armpit is smooth", _hair_phrase(["armpit_1_clean_5"]))
        self.assertEqual("the armpit is smooth", _hair_phrase(["armpit_3_clear_5"]))
        self.assertEqual("the armpit is hairy", _hair_phrase(["armpit_2_hairy_fungal_5"]))
        # Composed: skin tone, then hair, then any present-only feature.
        self.assertEqual(
            "The armpit skin is pale; the armpit is smooth.",
            _render_description("ARMPIT", "skin-pale", [], _hair_phrase(["armpit_1_clean_5"])),
        )
        self.assertEqual(
            "The armpit skin is red; the armpit is hairy; there is a fungal growth on the skin.",
            _render_description(
                "ARMPIT", "skin-flushed",
                _name_traits("ARMPIT", ["armpit_2_hairy_fungal_5"]),
                _hair_phrase(["armpit_2_hairy_fungal_5"]),
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

    def test_best_animation_frame_picks_the_most_feature_present_frame(self):
        # Animated armpit/ear sprites are one sheet + a sliced Sprite asset per frame (m_Rect into the sheet). We crop
        # the frame that differs most from the plain baseline frame 0 — so a transient feature (a cockroach that crawls
        # in then out) is caught mid-loop, not lost on an empty last frame.
        from image_tools.pipeline import _best_animation_frame

        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sprite_dir = root / "sprites"
            sprite_dir.mkdir()
            # A 4-frame sheet, each cell 8x8 side by side: frame0 plain, frame2 has a bright "feature" block, others dim.
            sheet = Image.new("RGB", (32, 8), (10, 10, 10))
            for i, fill in enumerate([(10, 10, 10), (40, 40, 40), (250, 250, 250), (20, 20, 20)]):
                sheet.paste(Image.new("RGB", (8, 8), fill), (i * 8, 0))
            # Unity rects are bottom-left origin; here height==frame height so y is 0 for every cell.
            for i in range(4):
                (sprite_dir / f"anim_{i}.asset").write_text(
                    f"  m_Rect:\n    serializedVersion: 2\n    x: {i * 8}\n    y: 0\n    width: 8\n    height: 8\n",
                    encoding="utf-8",
                )
            best = _best_animation_frame(sheet.convert("RGBA"), sprite_dir, "anim")
            assert best is not None
            # Frame 2 (the bright cell) is the most different from frame 0 -> it's chosen.
            self.assertEqual((250, 250, 250), best.convert("RGB").getpixel((4, 4)))

        # A base with fewer than two sliced frames returns None (caller uses the whole texture).
        with tempfile.TemporaryDirectory() as temp:
            empty = Path(temp) / "sprites"
            empty.mkdir()
            self.assertIsNone(_best_animation_frame(Image.new("RGBA", (8, 8)), empty, "nope"))

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
