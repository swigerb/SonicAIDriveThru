import json
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from menu_utils import infer_combo_component

_REPO_ROOT = Path(__file__).resolve().parents[3]
_GOLDEN_CATEGORIES_PATH = _REPO_ROOT / "tests" / "conformance" / "testdata" / "golden-menu-categories.json"


def _load_golden_categories() -> list[dict]:
    with _GOLDEN_CATEGORIES_PATH.open("r", encoding="utf-8") as f:
        return json.load(f)["items"]


class InferComboComponentGoldenCategoryTests(unittest.TestCase):
    """#39: every menuItems.json item must classify into its documented combo-slot/happy-hour
    bucket (sides/drinks/none), enforced against the golden table so future menu or keyword
    changes can't silently regress a specific item's classification (e.g. Ched 'R' Peppers back
    into "drinks" via the bare "pepper" keyword, or a sundae back into the happy-hour discount)."""

    @classmethod
    def setUpClass(cls):
        cls.golden = _load_golden_categories()

    def test_golden_table_is_non_empty_and_covers_the_full_menu(self):
        self.assertGreaterEqual(len(self.golden), 60, "Golden category table should cover the full ~60-item menu")

    def test_every_golden_item_matches_its_documented_bucket(self):
        mismatches = []
        for row in self.golden:
            expected = row["bucket"]
            actual = infer_combo_component(row["item"]) or "none"
            if actual != expected:
                mismatches.append(f"{row['item']!r}: expected {expected!r}, got {actual!r}")
        self.assertEqual(mismatches, [], "\n".join(mismatches))

    def test_ched_r_peppers_is_a_side_not_a_drink(self):
        """The headline #39 bug: 'Ched 'R' Peppers' contains the bare substring 'pepper' but is a
        side (Hot Dogs & Tots category), not the drink 'Dr Pepper'."""
        self.assertEqual(infer_combo_component("Ched 'R' Peppers"), "sides")

    def test_sundaes_are_not_the_drinks_bucket(self):
        """Brian's #39 decision: sundaes are full price during happy hour (happy hour = drinks
        only), so they must not classify as "drinks" here (this bucket also gates the discount)."""
        self.assertEqual(infer_combo_component("Hot Fudge Sundae"), "")
        self.assertEqual(infer_combo_component("Caramel Sundae"), "")

    def test_hot_dog_entrees_are_not_the_sides_bucket(self):
        """Hot-dog entrees share the "Hot Dogs & Tots" JSON category with real sides (Tots, Onion
        Rings, ...) but are food items, not a fillable combo side slot."""
        for name in ("All-American Dog", "Chili Cheese Coney", "Footlong Quarter Pound Coney", "Corn Dog"):
            self.assertEqual(infer_combo_component(name), "", name)

    def test_dr_pepper_keyword_fallback_is_word_boundary(self):
        """An item not in menuItems.json at all still falls back to keyword scanning, but "dr
        pepper" must match on a word boundary, not as a bare "pepper" substring."""
        self.assertEqual(infer_combo_component("Dr Pepper"), "drinks")
        self.assertEqual(infer_combo_component("Diet Dr Pepper"), "drinks")
        self.assertEqual(infer_combo_component("Peppercorn Ranch Dip"), "")


if __name__ == "__main__":
    unittest.main()
