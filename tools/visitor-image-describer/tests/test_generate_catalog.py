import json
import tempfile
import unittest
from pathlib import Path

from generate_csharp_catalog import generate


class GenerateCatalogTests(unittest.TestCase):
    def test_runtime_wording_preserves_full_description(self):
        records = [
            {
                "sign": "HANDS",
                "side": "imposter",
                "sprites": ["hands_1"],
                "traits": ["dirty-nails", "irritated-skin", "unusual-fingers", "injured"],
                "description": (
                    "Dirt is visible under the fingernails; the hand skin is visibly red or irritated; "
                    "the fingers have an unusual visible count or shape; the hands have a visible injury."
                ),
                "status": "candidate",
                "validation_issues": [],
            }
        ]
        with tempfile.TemporaryDirectory() as temp:
            catalog = Path(temp) / "catalog.json"
            output = Path(temp) / "Catalog.g.cs"
            catalog.write_text(json.dumps(records), encoding="utf-8")

            generate(catalog, output)
            generated = output.read_text(encoding="utf-8")

        self.assertIn("Dirt is visible under the fingernails", generated)
        self.assertIn("the hands have a visible injury.", generated)

    def test_duplicate_suffix_assets_remain_distinct(self):
        records = [
            {
                "sign": "HANDS",
                "side": "human",
                "sprites": ["same_name"],
                "description": "First image.",
                "status": "candidate",
                "validation_issues": [],
            },
            {
                "sign": "HANDS",
                "side": "human",
                "sprites": ["same_name (2)"],
                "description": "Second image.",
                "status": "candidate",
                "validation_issues": [],
            },
        ]
        with tempfile.TemporaryDirectory() as temp:
            catalog = Path(temp) / "catalog.json"
            output = Path(temp) / "Catalog.g.cs"
            catalog.write_text(json.dumps(records), encoding="utf-8")

            included, excluded = generate(catalog, output)
            generated = output.read_text(encoding="utf-8")

        # Each record is emitted under BOTH sides (a sprite image means the same thing whichever field the runtime
        # reads it from, and randomized characters can appear as either nature), so 2 records -> 4 entries.
        self.assertEqual(4, included)
        self.assertEqual(0, excluded)
        self.assertIn('1|0|same_name"', generated)
        self.assertIn('1|1|same_name"', generated)
        self.assertIn('1|0|same_name (2)"', generated)
        self.assertIn('1|1|same_name (2)"', generated)


if __name__ == "__main__":
    unittest.main()
