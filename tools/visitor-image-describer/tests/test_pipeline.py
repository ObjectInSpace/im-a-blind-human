import csv
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from image_tools.pipeline import _issues, _parse_traits, _prompt, _render_traits, build_manifest, describe_manifest


class _Client:
    model = "test-model"

    def describe(self, _path, _prompt):
        return "white-teeth, bleeding-gums"


class _FailingClient:
    model = "test-model"

    def __init__(self):
        self.calls = 0

    def describe(self, _path, _prompt):
        self.calls += 1
        if self.calls == 2:
            raise RuntimeError("interrupted")
        return "white-teeth"


class PipelineTests(unittest.TestCase):
    def test_validator_flags_subjective_judgments(self):
        issues = _issues("The skin has a natural, healthy appearance.")
        self.assertIn("Contains subjective word: natural", issues)
        self.assertIn("Contains subjective word: healthy", issues)

    def test_prompts_define_evidence_for_each_sign(self):
        self.assertIn("bloodshot", _prompt("EYE"))
        self.assertIn("dirty-nails", _prompt("HANDS"))
        self.assertIn("bleeding-gums", _prompt("TEETH"))
        self.assertIn("black-patches", _prompt("AURAPHOTO"))
        self.assertIn("fungal-growth", _prompt("ARMPIT"))
        self.assertIn("insect", _prompt("EAR"))

    def test_traits_render_to_fixed_objective_description(self):
        traits = _parse_traits("TEETH", "WHITE_TEETH, bleeding gums")
        self.assertEqual(["white-teeth", "bleeding-gums"], traits)
        self.assertEqual("The teeth are bright white; the gums are visibly bleeding.", _render_traits("TEETH", traits))

    def test_prepare_uses_last_animation_frame_and_composites_eye(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            textures = root / "textures"
            textures.mkdir()
            for name, color in (("armpit_0", "red"), ("armpit_1", "blue"), ("white", "white"), ("pupil", "black")):
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
            eye = next(task for task in tasks if task.sign == "EYE")
            self.assertEqual(["armpit_1"], armpit.sprites)
            self.assertEqual(["white", "pupil"], eye.sprites)
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
            self.assertEqual(["white-teeth", "bleeding-gums"], results[0]["traits"])
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
                "traits": ["white-teeth"],
                "model_output": "white-teeth",
                "description": "The teeth are bright white.",
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
