import json
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from menu_utils import infer_combo_component, is_happy_hour_discounted

_REPO_ROOT = Path(__file__).resolve().parents[3]
_GOLDEN_CATEGORIES_PATH = _REPO_ROOT / "tests" / "conformance" / "testdata" / "golden-menu-categories.json"
_MENU_ITEMS_PATH = _REPO_ROOT / "app" / "frontend" / "src" / "data" / "menuItems.json"


def _load_golden_categories() -> list[dict]:
    with _GOLDEN_CATEGORIES_PATH.open("r", encoding="utf-8") as f:
        return json.load(f)["items"]


def _load_menu_item_names() -> set[str]:
    with _MENU_ITEMS_PATH.open("r", encoding="utf-8") as f:
        data = json.load(f)
    return {item["name"] for category in data["menuItems"] for item in category["items"]}


class InferComboComponentGoldenCategoryTests(unittest.TestCase):
    """#39 / PR #50 review: every menuItems.json item must classify into its documented
    combo-slot (side/drink/none, ``infer_combo_component``) AND happy-hour-discount eligibility
    (``is_happy_hour_discounted``) -- two SEPARATE columns in the golden table, enforced
    independently so a future change to one can't silently regress the other (e.g. Ched 'R'
    Peppers back into a free combo side via the wide "Extras & Sides"/"Hot Dogs & Tots" bucket,
    or a sundae back into the happy-hour discount)."""

    @classmethod
    def setUpClass(cls):
        cls.golden = _load_golden_categories()

    def test_golden_table_covers_every_menu_item_exactly(self):
        """Not just ">= 60" (PR #50 follow-up): the golden item names must be EXACTLY the
        menuItems.json names, so a renamed/added/removed menu item is caught immediately instead
        of silently leaving the golden table stale."""
        golden_names = {row["item"] for row in self.golden}
        menu_names = _load_menu_item_names()
        self.assertEqual(golden_names, menu_names, "Golden table item names must exactly match menuItems.json")

    def test_every_golden_item_matches_its_documented_combo_slot(self):
        mismatches = []
        for row in self.golden:
            expected = row["comboSlot"]
            actual = infer_combo_component(row["item"]) or "none"
            if actual != expected:
                mismatches.append(f"{row['item']!r}: expected comboSlot {expected!r}, got {actual!r}")
        self.assertEqual(mismatches, [], "\n".join(mismatches))

    def test_every_golden_item_matches_its_documented_happy_hour_discount(self):
        """Independent from the combo-slot check above -- PR #50 review: happy-hour discount
        eligibility must be verified on its own axis, not inferred from comboSlot=="drinks"."""
        mismatches = []
        for row in self.golden:
            expected = row["happyHourDiscounted"]
            actual = is_happy_hour_discounted(row["item"])
            if actual != expected:
                mismatches.append(f"{row['item']!r}: expected happyHourDiscounted {expected!r}, got {actual!r}")
        self.assertEqual(mismatches, [], "\n".join(mismatches))

    def test_ched_r_peppers_is_not_a_combo_side(self):
        """PR #50 pricing regression: 'Ched 'R' Peppers' is an "Extras & Sides"-adjacent item, not
        one of the two combo side-slot items (Tots, Groovy Fries) -- it must be charged in full,
        not silently absorbed for free into a combo. It also must not match the bare substring
        'pepper' as the drink 'Dr Pepper' (the original #39 bug)."""
        self.assertEqual(infer_combo_component("Ched 'R' Peppers"), "")

    def test_only_tots_and_groovy_fries_fill_the_combo_side_slot(self):
        """PR #50 must-fix: the menu's own combo description says "your choice of a side (Tots or
        Fries) and a drink" -- the combo side slot is an explicit allow-list of exactly those two
        items, not the whole "Hot Dogs & Tots"/"Extras & Sides" category."""
        self.assertEqual(infer_combo_component("Tots"), "sides")
        self.assertEqual(infer_combo_component("Groovy Fries"), "sides")

    def test_extras_and_sides_lookalikes_are_not_combo_sides(self):
        """PR #50 pricing regression: these 9+ items were being silently absorbed for free into a
        combo's side slot because the whole "Extras & Sides"/"Hot Dogs & Tots" category mapped to
        "sides". They must all charge in full alongside a combo."""
        for name in (
            "Crispy Tenders - 3 Piece",
            "Crispy Tenders - 5 Piece",
            "Premium Chicken Bites",
            "FRITOS® Chili Cheese Wrap",
            "FRITOS® Chili Cheese Jr. Wrap",
            "Fritos Chili Cheese Pie",
            "Soft Pretzel Twist",
            "Mozzarella Sticks",
            "Ched 'R' Peppers",
            "Onion Rings",
            "Cheese Tots",
            "Cheese Groovy Fries",
            "Chili Cheese Groovy Fries",
            "Chili Cheese Tots",
        ):
            self.assertEqual(infer_combo_component(name), "", name)

    def test_sundaes_are_neither_a_combo_drink_nor_happy_hour_discounted(self):
        """Brian's #39 decision: sundaes are full price during happy hour and can't fill a
        combo's drink slot, even though they live in the "Shakes & Ice Cream" category."""
        for name in ("Hot Fudge Sundae", "Caramel Sundae"):
            self.assertEqual(infer_combo_component(name), "", name)
            self.assertFalse(is_happy_hour_discounted(name), name)

    def test_hot_dog_entrees_are_not_the_sides_bucket(self):
        """Hot-dog entrees share the "Hot Dogs & Tots" JSON category with real sides (Tots,
        Onion Rings, ...) but are food items, not a fillable combo side slot."""
        for name in ("All-American Dog", "Chili Cheese Coney", "Footlong Quarter Pound Coney", "Corn Dog"):
            self.assertEqual(infer_combo_component(name), "", name)

    def test_dr_pepper_keyword_fallback_is_word_boundary(self):
        """An item not in menuItems.json at all still falls back to keyword scanning, but "dr
        pepper" must match on a word boundary, not as a bare "pepper" substring."""
        self.assertEqual(infer_combo_component("Dr Pepper"), "drinks")
        self.assertEqual(infer_combo_component("Diet Dr Pepper"), "drinks")
        self.assertEqual(infer_combo_component("Peppercorn Ranch Dip"), "")

    def test_slushes_and_drinks_are_happy_hour_discounted(self):
        self.assertTrue(is_happy_hour_discounted("Cherry Limeade"))
        self.assertTrue(is_happy_hour_discounted("Ocean Water®"))

    def test_shakes_and_blasts_are_happy_hour_discounted_pending_brian(self):
        """PR #50 review: leave as-is (Brian hasn't ruled yet) -- but this is deliberately the
        ONE test that pins the current answer, so flipping
        ``menu_utils._SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED`` is a one-line change once he
        decides, and this test is the one line that documents/enforces today's answer."""
        self.assertTrue(is_happy_hour_discounted("Vanilla Classic Shake"))
        self.assertTrue(is_happy_hour_discounted("SONIC Blast® made with OREO® Cookie Pieces"))

    def test_burgers_combos_and_hot_dog_entrees_are_never_happy_hour_discounted(self):
        for name in ("Crispy Chicken Sandwich", "SONIC® Cheeseburger Combo", "Corn Dog", "Tots", "Groovy Fries"):
            self.assertFalse(is_happy_hour_discounted(name), name)


if __name__ == "__main__":
    unittest.main()
