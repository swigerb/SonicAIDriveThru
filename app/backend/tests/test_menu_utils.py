import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

import menu_utils
from menu_utils import (
    infer_category,
    infer_combo_component,
    is_happy_hour_discounted,
    strip_modifiers,
)

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


class CustomisedItemMenuLookupTests(unittest.TestCase):
    """PR #50 review (second round): modifiers travel inside item_name (e.g. "Tots (Extra
    Crispy)", tools.py's ``update_order``), so every menuItems.json-based lookup must strip them
    via the one shared ``strip_modifiers``/``_menu_key`` rule before classifying -- a customised
    item must classify EXACTLY like its base item, never fall through to a keyword fallback that
    disagrees with the base item's real menuItems.json category."""

    def test_strip_modifiers_removes_a_trailing_parenthesized_suffix(self):
        self.assertEqual(strip_modifiers("Tots (Extra Crispy)"), "Tots")
        self.assertEqual(strip_modifiers("Chili Cheese Tots (Extra Cheese)"), "Chili Cheese Tots")
        self.assertEqual(strip_modifiers("Cherry Limeade"), "Cherry Limeade")

    def test_chili_cheese_tots_customised_is_charged_in_full_not_absorbed(self):
        """Rick's repro: Cheeseburger Combo 8.49 + "Chili Cheese Tots (Extra Cheese)" was
        measuring as absorbed free (8.49) instead of charged in full (12.28) because the raw,
        un-stripped name missed the menuItems.json category lookup and fell through to a keyword
        fallback that (wrongly) matched "tots"."""
        self.assertEqual(infer_combo_component("Chili Cheese Tots (Extra Cheese)"), "")

    def test_chili_cheese_groovy_fries_customised_is_charged_in_full_not_absorbed(self):
        self.assertEqual(infer_combo_component("Chili Cheese Groovy Fries (No Chili)"), "")

    def test_plain_tots_customised_still_fills_the_combo_side_slot(self):
        """The allow-listed items themselves must still be absorbed once customised -- only the
        modifier is stripped, the underlying item is unchanged."""
        self.assertEqual(infer_combo_component("Tots (Extra Crispy)"), "sides")
        self.assertEqual(infer_combo_component("Groovy Fries (Extra Salty)"), "sides")

    def test_unknown_misspelled_item_never_fills_the_side_slot_even_as_a_substring_match(self):
        """PR #50 must-fix 2: the side fallback for unknown items is deleted entirely -- a
        misspelling/off-menu item (here "chilli cheese tots", a typo) must never silently absorb
        into a combo's side slot. A charged item is visible and correctable; a free one is a
        silent revenue loss."""
        self.assertEqual(infer_combo_component("chilli cheese tots"), "")
        self.assertEqual(infer_combo_component("totstastic snack"), "")

    def test_cherry_limeade_customised_still_gets_the_happy_hour_discount(self):
        self.assertTrue(is_happy_hour_discounted("Cherry Limeade (Extra Cherries)"))

    def test_ched_r_peppers_customised_is_still_not_a_combo_side_or_dr_pepper(self):
        self.assertEqual(infer_combo_component("Ched 'R' Peppers (Extra Spicy)"), "")

    def test_customised_category_matches_base_item_category(self):
        self.assertEqual(infer_category("Tots (Extra Crispy)"), infer_category("Tots"))

    def test_flipping_the_shakes_and_blasts_flag_changes_every_shake_blast_variant(self):
        """PR #50 review: prove the flag is genuinely the ONE single switch -- flipping it must
        change EVERY shake/blast, plain or customised, on-menu (menuItems.json category match) or
        off-menu (keyword fallback only), never just some of them."""
        on_menu_plain = "Vanilla Classic Shake"
        on_menu_customised = "Vanilla Classic Shake (No Whip)"
        on_menu_blast_customised = "SONIC Blast® made with OREO® Cookie Pieces (Extra Candy)"
        off_menu_plain = "Chocolate Malt"
        off_menu_customised = "Chocolate Malt (Extra Malt)"

        # Baseline: the flag is currently True (pending Brian) -- every variant is discounted.
        for name in (on_menu_plain, on_menu_customised, on_menu_blast_customised, off_menu_plain, off_menu_customised):
            self.assertTrue(is_happy_hour_discounted(name), name)

        with patch.object(menu_utils, "_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED", False):
            for name in (on_menu_plain, on_menu_customised, on_menu_blast_customised, off_menu_plain, off_menu_customised):
                self.assertFalse(is_happy_hour_discounted(name), name)

        # Combo-drink-slot eligibility is a SEPARATE question and must NOT be affected by the flag.
        with patch.object(menu_utils, "_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED", False):
            self.assertEqual(infer_combo_component(off_menu_customised), "drinks")


class KeywordFallbackWordBoundaryTests(unittest.TestCase):
    """PR #50 review (round 4, must-fix): the off-menu keyword fallbacks used to be plain
    substring checks (``kw in normalized``), so the keyword "tea" matched inside "steak" -- an
    off-menu "Philly Cheesesteak"/"Steak Sandwich" was silently absorbed into a combo's drink slot
    for free AND happy-hour discounted. Word-boundary regexes (``\\b...\\b``) fix this while still
    matching every real standalone keyword occurrence, plural or singular."""

    def test_steak_items_are_not_misclassified_as_a_tea_drink(self):
        for name in ("Philly Cheesesteak", "Steak Sandwich"):
            self.assertEqual(infer_combo_component(name), "", name)
            self.assertFalse(is_happy_hour_discounted(name), name)

    def test_real_tea_keyword_still_matches_as_a_standalone_word(self):
        for name in ("Sweet Tea", "Iced Tea", "Unsweetened Tea"):
            self.assertEqual(infer_combo_component(name), "drinks", name)
            self.assertTrue(is_happy_hour_discounted(name), name)

    def test_real_drink_keyword_plurals_still_match(self):
        """The word-boundary regex allows an optional trailing "s" so plural mentions keep
        matching (e.g. a guest ordering "2 Cokes")."""
        self.assertEqual(infer_combo_component("Cokes"), "drinks")
        self.assertTrue(is_happy_hour_discounted("Cokes"))

    def test_milkshake_is_not_misclassified_by_a_bare_shake_substring(self):
        """"shake" must not match merely because it's a substring of a longer compound word with
        no word boundary in between -- guards the shake/blast/malt list the same way."""
        self.assertEqual(infer_combo_component("Milkshake Mixer Cleaning Kit"), "")


class MenuCategoryMapDirectResolutionTests(unittest.TestCase):
    """PR #50 review (round 4, should-fix 2): every menuItems.json item must resolve via
    ``MENU_CATEGORY_MAP`` directly -- never by falling through to keyword-guessing luck. This is
    the regression class the OREO Blast's NBSP caused: ``MENU_CATEGORY_MAP`` used to be keyed by a
    bare ``name.lower()``, which doesn't collapse NBSP, so that item's own exact name missed its
    own map entry and only classified correctly because the substring "blast" happened to still
    match in the keyword fallback."""

    @classmethod
    def setUpClass(cls):
        cls.menu_names = _load_menu_item_names()

    def test_every_menu_item_name_resolves_directly_via_the_category_map(self):
        """Direct proof the map key resolves -- ``_menu_key(name)`` must be a member of
        ``MENU_CATEGORY_MAP`` for every real menu item, with no fallback involved at all."""
        missing = [name for name in self.menu_names if menu_utils._menu_key(name) not in menu_utils.MENU_CATEGORY_MAP]
        self.assertEqual(missing, [], f"Missing from MENU_CATEGORY_MAP: {missing}")

    def test_combo_and_happy_hour_classification_never_reaches_the_keyword_fallback(self):
        """Stronger proof, per Rick's suggestion: patch BOTH keyword-fallback functions to raise,
        then classify every real menu item name -- if either function still reached the fallback
        for an on-menu item, this raises instead of silently passing."""
        def _boom(_normalized):
            raise AssertionError("keyword fallback must not be reached for an on-menu item")

        with (
            patch.object(menu_utils, "_keyword_fallback_combo_drink", _boom),
            patch.object(menu_utils, "_keyword_fallback_happy_hour_discounted", _boom),
        ):
            for name in self.menu_names:
                infer_combo_component(name)
                is_happy_hour_discounted(name)


if __name__ == "__main__":
    unittest.main()
