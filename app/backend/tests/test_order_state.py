import math
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from order_state import SessionIdentifiers, order_state_singleton


class OrderStateTests(unittest.TestCase):
    def setUp(self):
        order_state_singleton.sessions = {}

    def test_create_session_initializes_empty_summary(self):
        session_id = order_state_singleton.create_session()
        summary = order_state_singleton.get_order_summary(session_id)

        self.assertEqual(len(summary.items), 0)
        self.assertEqual(summary.total, 0)
        self.assertEqual(summary.tax, 0)
        self.assertEqual(summary.finalTotal, 0)

    def test_handle_order_update_adds_and_updates_totals(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Caramel Craze Latte", "medium", 2, 4.99)
        order_state_singleton.handle_order_update(session_id, "add", "Glazed Donut", "standard", 1, 1.49)

        summary = order_state_singleton.get_order_summary(session_id)

        self.assertEqual(len(summary.items), 2)
        self.assertEqual(summary.items[0].quantity, 2)

        expected_total = (2 * 4.99) + 1.49
        expected_tax = expected_total * 0.08
        expected_final = expected_total + expected_tax

        self.assertTrue(math.isclose(summary.total, expected_total, rel_tol=1e-9))
        self.assertTrue(math.isclose(summary.tax, expected_tax, rel_tol=1e-9))
        self.assertTrue(math.isclose(summary.finalTotal, expected_final, rel_tol=1e-9))

    def test_formatted_display_labels_handle_special_sizes(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Cherry Limeade", "rt44", 1, 3.99)

        summary = order_state_singleton.get_order_summary(session_id)

        self.assertEqual(summary.items[0].display, "Route 44 Cherry Limeade")

    def test_n_a_size_is_hidden_in_display(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Glazed Donut", "n/a", 1, 1.49)

        summary = order_state_singleton.get_order_summary(session_id)

        self.assertEqual(summary.items[0].display, "Glazed Donut")

    def test_session_identifiers_increment_with_round_trips(self):
        session_id = order_state_singleton.create_session()
        identifiers = order_state_singleton.get_session_identifiers(session_id)

        self.assertIsInstance(identifiers, SessionIdentifiers)
        self.assertEqual(identifiers.round_trip_index, 0)
        self.assertTrue(identifiers.round_trip_token.endswith("-0000"))

        first_round = order_state_singleton.advance_round_trip(session_id)
        self.assertEqual(first_round.round_trip_index, 1)
        self.assertTrue(first_round.round_trip_token.endswith("-0001"))
        self.assertEqual(first_round.session_token, identifiers.session_token)

    def test_session_tokens_are_unique_per_session(self):
        session_one = order_state_singleton.create_session()
        session_two = order_state_singleton.create_session()

        identifiers_one = order_state_singleton.get_session_identifiers(session_one)
        identifiers_two = order_state_singleton.get_session_identifiers(session_two)

        self.assertNotEqual(identifiers_one.session_token, identifiers_two.session_token)

    # ── Edge cases added below ──

    def test_delete_session_removes_state(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Glazed Donut", "standard", 1, 1.49)
        order_state_singleton.delete_session(session_id)
        self.assertNotIn(session_id, order_state_singleton.sessions)

    def test_delete_nonexistent_session_is_safe(self):
        order_state_singleton.delete_session("nonexistent-id")

    def test_concurrent_sessions_are_independent(self):
        s1 = order_state_singleton.create_session()
        s2 = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(s1, "add", "Glazed Donut", "standard", 2, 1.49)
        order_state_singleton.handle_order_update(s2, "add", "Cold Brew", "large", 1, 3.99)

        summary1 = order_state_singleton.get_order_summary(s1)
        summary2 = order_state_singleton.get_order_summary(s2)

        self.assertEqual(len(summary1.items), 1)
        self.assertEqual(summary1.items[0].item, "Glazed Donut")
        self.assertEqual(len(summary2.items), 1)
        self.assertEqual(summary2.items[0].item, "Cold Brew")

    def test_round_trip_token_format(self):
        session_id = order_state_singleton.create_session()
        ids = order_state_singleton.get_session_identifiers(session_id)
        self.assertRegex(ids.round_trip_token, r"^.+-0000$")

        for i in range(1, 4):
            ids = order_state_singleton.advance_round_trip(session_id)
            self.assertEqual(ids.round_trip_index, i)
            self.assertTrue(ids.round_trip_token.endswith(f"-{i:04d}"))

    def test_multiple_round_trip_advances_maintain_session_token(self):
        session_id = order_state_singleton.create_session()
        ids_initial = order_state_singleton.get_session_identifiers(session_id)
        for _ in range(5):
            ids = order_state_singleton.advance_round_trip(session_id)
        self.assertEqual(ids.session_token, ids_initial.session_token)
        self.assertEqual(ids.round_trip_index, 5)

    def test_remove_item_decreases_quantity(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Glazed Donut", "standard", 3, 1.49)
        order_state_singleton.handle_order_update(session_id, "remove", "Glazed Donut", "standard", 1, 1.49)

        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 1)
        self.assertEqual(summary.items[0].quantity, 2)

    def test_remove_item_fully_removes_when_quantity_matches(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Glazed Donut", "standard", 2, 1.49)
        order_state_singleton.handle_order_update(session_id, "remove", "Glazed Donut", "standard", 2, 1.49)

        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 0)
        self.assertAlmostEqual(summary.total, 0.0)

    def test_remove_nonexistent_item_is_noop(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "remove", "Phantom Item", "large", 1, 9.99)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 0)

    def test_add_duplicate_item_increments_quantity(self):
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Cold Brew", "large", 1, 3.99)
        order_state_singleton.handle_order_update(session_id, "add", "Cold Brew", "large", 2, 3.99)

        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 1)
        self.assertEqual(summary.items[0].quantity, 3)

    def test_display_formatting_for_various_sizes(self):
        _session_id = order_state_singleton.create_session()
        cases = [
            ("Latte", "small", "Small Latte"),
            ("Latte", "medium", "Medium Latte"),
            ("Latte", "large", "Large Latte"),
            ("Cherry Limeade", "mini", "Mini Cherry Limeade"),
            ("Cherry Limeade", "rt44", "Route 44 Cherry Limeade"),
            ("Cherry Limeade", "rt 44", "Route 44 Cherry Limeade"),
            ("Cherry Limeade", "route 44", "Route 44 Cherry Limeade"),
            ("Donut", "standard", "Donut"),
            ("Donut", "n/a", "Donut"),
            ("Donut", "na", "Donut"),
            ("Donut", "none", "Donut"),
            ("Donut", "", "Donut"),
            ("Donut", "n.a.", "Donut"),
            ("Cold Brew", "pot", "Cold Brew"),
            ("Cold Brew", "kannchen", "Cold Brew"),
        ]
        for item, size, expected_display in cases:
            order_state_singleton.sessions = {}
            sid = order_state_singleton.create_session()
            order_state_singleton.handle_order_update(sid, "add", item, size, 1, 1.0)
            summary = order_state_singleton.get_order_summary(sid)
            self.assertEqual(summary.items[0].display, expected_display,
                             f"Failed for size='{size}': expected '{expected_display}'")

    # ── Combo requirements tests ──

    def test_combo_requirements_no_combo_is_complete(self):
        """No combos in order means requirements are complete."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertTrue(result["is_complete"])
        self.assertEqual(result["missing_items"], [])
        self.assertEqual(result["prompt_hint"], "")

    def test_combo_requirements_combo_without_side_or_drink(self):
        """Combo with no side or drink should report both missing."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(result["is_complete"])
        self.assertEqual(len(result["missing_items"]), 2)
        self.assertIn("side", result["prompt_hint"].lower())
        self.assertIn("drink", result["prompt_hint"].lower())

    def test_combo_requirements_combo_with_side_missing_drink(self):
        """Combo with side but no drink should report drink missing."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(result["is_complete"])
        self.assertEqual(len(result["missing_items"]), 1)
        self.assertIn("drink", result["missing_items"][0])

    def test_combo_requirements_combo_with_drink_missing_side(self):
        """Combo with drink but no side should report side missing."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        order_state_singleton.handle_order_update(session_id, "add", "Cherry Limeade", "medium", 1, 2.99)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(result["is_complete"])
        self.assertEqual(len(result["missing_items"]), 1)
        self.assertIn("side", result["missing_items"][0])

    def test_combo_requirements_combo_fully_complete(self):
        """Combo with both side and drink is complete."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Cherry Limeade", "medium", 1, 2.99)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertTrue(result["is_complete"])
        self.assertEqual(result["missing_items"], [])

    def test_combo_requirements_two_combos_one_side_one_drink(self):
        """Two combos with only one side and one drink should still be incomplete."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 2, 8.49)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Cherry Limeade", "medium", 1, 2.99)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(result["is_complete"])
        self.assertEqual(len(result["missing_items"]), 2)

    def test_combo_requirements_empty_order(self):
        """Empty order should be complete (no combos to satisfy)."""
        session_id = order_state_singleton.create_session()
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertTrue(result["is_complete"])

    # ── Combo pivot / absorption tests ──

    def test_combo_absorbs_existing_side_and_drink(self):
        """Fish Sandwich + Tots + Diet Coke → 'make it a combo' absorbs both + removes standalone."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Fish Sandwich", "standard", 1, 5.49)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Diet Coke", "medium", 1, 1.99)
        # Guest says "make that a combo"
        order_state_singleton.handle_order_update(session_id, "add", "Fish Sandwich Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        # Standalone entree should be REMOVED (combo replaces it)
        self.assertNotIn("Fish Sandwich", item_names)
        self.assertIn("Fish Sandwich Combo", item_names)
        self.assertNotIn("Tots", item_names)
        self.assertNotIn("Diet Coke", item_names)
        self.assertEqual(len(items), 1, "Only the combo should remain")
        # Combo price only — no standalone entree or side/drink prices
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 8.49, places=2)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertTrue(result["is_complete"])

    def test_combo_absorbs_only_one_side(self):
        """Two standalone sides, combo absorbs only one."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Onion Rings", "medium", 1, 3.29)
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        side_items = [i for i in items if i.item in ("Tots", "Onion Rings")]
        self.assertEqual(len(side_items), 1, "Only one side should remain after absorption")

    def test_combo_absorbs_only_one_drink(self):
        """Two standalone drinks, combo absorbs only one."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Cherry Limeade", "medium", 1, 2.99)
        order_state_singleton.handle_order_update(session_id, "add", "Ocean Water", "medium", 1, 2.99)
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        drink_items = [i for i in items if i.item in ("Cherry Limeade", "Ocean Water")]
        self.assertEqual(len(drink_items), 1, "Only one drink should remain after absorption")

    def test_combo_absorbs_decrements_quantity_when_multiple(self):
        """Standalone side qty=2, combo absorbs one unit leaving qty=1."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 2, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        tots = next(i for i in items if i.item == "Tots")
        self.assertEqual(tots.quantity, 1, "Should decrement qty rather than remove")

    def test_combo_no_absorption_when_no_sides_or_drinks(self):
        """Adding a combo with no standalone sides/drinks absorbs nothing."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Fish Sandwich", "standard", 1, 5.49)
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 2)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(result["is_complete"])

    def test_non_combo_add_does_not_absorb(self):
        """Adding a regular item doesn't trigger absorption."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Fish Sandwich", "standard", 1, 5.49)
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 2)
        self.assertEqual(items[0].item, "Tots")

    def test_combo_absorbs_side_only_when_no_drink_present(self):
        """Side exists but no drink — absorb side, combo still needs drink."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Fish Sandwich Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        self.assertNotIn("Tots", item_names)
        result = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(result["is_complete"])
        self.assertEqual(len(result["missing_items"]), 1)
        self.assertIn("drink", result["missing_items"][0])

    # ── Combo conversion tests (standalone entree → combo) ──

    def test_combo_conversion_removes_standalone_burger(self):
        """Adding a combo auto-removes the matching standalone burger."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Double Cheeseburger", "standard", 1, 6.59)
        result_info = order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Double Cheeseburger Combo", "standard", 1, 10.19)
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        self.assertNotIn("SuperSONIC Double Cheeseburger", item_names)
        self.assertIn("SuperSONIC Double Cheeseburger Combo", item_names)
        self.assertEqual(len(items), 1)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 10.19, places=2)
        self.assertIn("combo_converted_from", result_info)

    def test_combo_conversion_carries_mods(self):
        """Mods like '(Pickles Only)' carry from standalone to combo."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Double Cheeseburger (Pickles Only)", "standard", 1, 6.59)
        result_info = order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Double Cheeseburger Combo", "standard", 1, 10.19)
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 1)
        combo_item = items[0]
        self.assertIn("Combo", combo_item.item)
        self.assertIn("(Pickles Only)", combo_item.item)
        self.assertAlmostEqual(combo_item.price, 10.19, places=2)
        self.assertIn("mods_carried", result_info)

    def test_combo_conversion_no_match_leaves_standalone(self):
        """Non-matching standalone is NOT removed (different entree name)."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Fish Sandwich", "standard", 1, 5.49)
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 2)
        item_names = [i.item for i in items]
        self.assertIn("Fish Sandwich", item_names)
        self.assertIn("SuperSONIC Cheeseburger Combo", item_names)

    # ── Post-combo side/drink absorption tests ──

    def test_post_combo_side_absorbed(self):
        """Side added AFTER combo is absorbed into combo slot."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        result_info = order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        items = order_state_singleton.get_order_items(session_id)
        # Tots should NOT appear as a standalone item
        item_names = [i.item for i in items]
        self.assertNotIn("Tots", item_names)
        self.assertTrue(result_info.get("absorbed_into_combo"))
        # Total should be combo price only
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 8.49, places=2)
        # Combo should report side filled, drink still missing
        req = order_state_singleton.get_combo_requirements(session_id)
        self.assertFalse(req["is_complete"])
        self.assertEqual(len(req["missing_items"]), 1)
        self.assertIn("drink", req["missing_items"][0])

    def test_post_combo_drink_absorbed(self):
        """Drink added AFTER combo is absorbed into combo slot."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        result_info = order_state_singleton.handle_order_update(session_id, "add", "Cherry Limeade", "medium", 1, 2.99)
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        self.assertNotIn("Cherry Limeade", item_names)
        self.assertTrue(result_info.get("absorbed_into_combo"))
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 8.49, places=2)

    def test_post_combo_both_side_and_drink_absorbed(self):
        """Both side and drink added after combo → both absorbed, correct total."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Diet Coke", "medium", 1, 2.49)
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 1, "Only the combo should be in order")
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 8.49, places=2)
        req = order_state_singleton.get_combo_requirements(session_id)
        self.assertTrue(req["is_complete"])

    def test_exact_bug_scenario_burger_then_combo_then_side_drink(self):
        """Reproduce the exact demo bug: standalone burger → combo → tots → diet coke."""
        session_id = order_state_singleton.create_session()
        # Step 1: Customer orders a standalone burger
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Double Cheeseburger (Pickles Only)", "standard", 1, 6.59)
        # Step 2: Customer says "make it a combo with tots and a Diet Coke"
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Diet Coke", "medium", 1, 2.49)
        items = order_state_singleton.get_order_items(session_id)
        # Only the combo should remain — standalone burger removed, side/drink absorbed
        self.assertEqual(len(items), 1)
        combo = items[0]
        self.assertIn("Combo", combo.item)
        self.assertIn("(Pickles Only)", combo.item)
        self.assertAlmostEqual(combo.price, 10.19, places=2)
        # Total: $10.19 + 8% tax = $11.01
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 10.19, places=2)
        expected_final = 10.19 * 1.08
        self.assertAlmostEqual(summary.finalTotal, expected_final, places=2)
        req = order_state_singleton.get_combo_requirements(session_id)
        self.assertTrue(req["is_complete"])

    def test_combo_display_no_duplicate_mods(self):
        """Mods like '(Pickles Only)' must appear exactly once in the combo display."""
        session_id = order_state_singleton.create_session()
        # Standalone burger with mods
        order_state_singleton.handle_order_update(
            session_id, "add", "SuperSONIC Double Cheeseburger (Pickles Only)", "standard", 1, 6.59
        )
        # Convert to combo — mods carry over
        order_state_singleton.handle_order_update(
            session_id, "add", "SuperSONIC Double Cheeseburger Combo", "standard", 1, 10.19
        )
        # Add side and drink (absorbed into combo)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(session_id, "add", "Diet Coke", "medium", 1, 2.49)

        combo = order_state_singleton.get_order_items(session_id)[0]
        # "(Pickles Only)" must appear exactly once
        self.assertEqual(combo.display.count("(Pickles Only)"), 1,
                         f"Mods duplicated in display: {combo.display}")
        # Should show side & drink
        self.assertIn("w/", combo.display)
        self.assertIn("Tots", combo.display)
        self.assertIn("Diet Coke", combo.display)

    def test_side_not_absorbed_when_no_combo(self):
        """Side added without any combo stays as standalone at full price."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].item, "Tots")
        self.assertAlmostEqual(items[0].price, 2.79, places=2)

    def test_extra_side_not_absorbed_when_combo_full(self):
        """Second side added after combo slot filled stays as standalone."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(session_id, "add", "SuperSONIC Cheeseburger Combo", "standard", 1, 8.49)
        order_state_singleton.handle_order_update(session_id, "add", "Tots", "medium", 1, 2.79)  # absorbed
        result_info = order_state_singleton.handle_order_update(session_id, "add", "Onion Rings", "medium", 1, 3.29)  # standalone
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        self.assertNotIn("Tots", item_names)
        self.assertIn("Onion Rings", item_names)
        self.assertFalse(result_info.get("absorbed_into_combo", False))
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 8.49 + 3.29, places=2)

    # ── Combo conversion with mods regression tests (mod-in-combo-name bug) ──

    def test_combo_with_mods_in_name_removes_standalone(self):
        """BUG REGRESSION: AI sends combo with mods already in name
        e.g. 'SuperSONIC® Bacon Double Cheeseburger Combo (Pickles Only)'.
        Must still match and remove the standalone 'SuperSONIC® Bacon Double Cheeseburger (Pickles Only)'."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger (Pickles Only)", "standard", 1, 7.99
        )
        result_info = order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger Combo (Pickles Only)", "standard", 1, 10.99
        )
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        # Standalone must be removed — not duplicated on the ticket
        self.assertNotIn("SuperSONIC® Bacon Double Cheeseburger (Pickles Only)", item_names)
        self.assertEqual(len(items), 1, "Only the combo should remain")
        self.assertIn("Combo", items[0].item)
        self.assertIn("combo_converted_from", result_info)

    def test_combo_conversion_without_mods_regression(self):
        """Regression: basic combo conversion (no mods) still works after the fix."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            session_id, "add", "Fish Sandwich", "standard", 1, 5.49
        )
        result_info = order_state_singleton.handle_order_update(
            session_id, "add", "Fish Sandwich Combo", "standard", 1, 8.49
        )
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        self.assertNotIn("Fish Sandwich", item_names)
        self.assertEqual(len(items), 1)
        self.assertIn("Fish Sandwich Combo", item_names)
        self.assertIn("combo_converted_from", result_info)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertAlmostEqual(summary.total, 8.49, places=2)

    def test_combo_conversion_with_different_mods_still_matches(self):
        """Standalone has mods A, combo arrives with mods B — base item matches,
        standalone still removed (mods are irrelevant for base matching)."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger (No Lettuce)", "standard", 1, 7.99
        )
        result_info = order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger Combo (Extra Pickles)", "standard", 1, 10.99
        )
        items = order_state_singleton.get_order_items(session_id)
        # Standalone removed because base names match
        self.assertEqual(len(items), 1)
        self.assertIn("Combo", items[0].item)
        self.assertIn("combo_converted_from", result_info)

    def test_combo_conversion_carries_mods_from_standalone(self):
        """When standalone has '(Pickles Only)' and combo has no mods,
        the mods must carry forward to the combo display."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger (Pickles Only)", "standard", 1, 7.99
        )
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger Combo", "standard", 1, 10.99
        )
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 1)
        combo = items[0]
        self.assertIn("(Pickles Only)", combo.item,
                       "Mods from standalone must carry to combo item name")

    def test_multiple_standalones_only_matching_one_removed(self):
        """Two different standalones on order. Converting one to combo
        removes only the matching standalone, other stays."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            session_id, "add", "Fish Sandwich", "standard", 1, 5.49
        )
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger (No Onions)", "standard", 1, 7.99
        )
        # Convert only the bacon cheeseburger to combo
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger Combo (No Onions)", "standard", 1, 10.99
        )
        items = order_state_singleton.get_order_items(session_id)
        item_names = [i.item for i in items]
        # Fish Sandwich must survive
        self.assertIn("Fish Sandwich", item_names)
        # Bacon standalone must be gone
        matching_standalone = [n for n in item_names if "Bacon Double Cheeseburger" in n and "Combo" not in n]
        self.assertEqual(len(matching_standalone), 0, "Matching standalone should be removed")
        # Combo must exist
        combos = [n for n in item_names if "Combo" in n]
        self.assertEqual(len(combos), 1)
        self.assertEqual(len(items), 2, "Fish Sandwich + combo")

    def test_combo_conversion_quantity_gt1_decrements(self):
        """Standalone has quantity 2. Adding combo removes one unit,
        leaving standalone with quantity 1."""
        session_id = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger (Pickles Only)", "standard", 2, 7.99
        )
        order_state_singleton.handle_order_update(
            session_id, "add",
            "SuperSONIC® Bacon Double Cheeseburger Combo (Pickles Only)", "standard", 1, 10.99
        )
        items = order_state_singleton.get_order_items(session_id)
        # Should have both: standalone qty 1 + combo qty 1
        standalone = [i for i in items if "Combo" not in i.item]
        combos = [i for i in items if "Combo" in i.item]
        self.assertEqual(len(standalone), 1, "Standalone should remain with reduced qty")
        self.assertEqual(standalone[0].quantity, 1, "Standalone qty should decrement from 2 to 1")
        self.assertEqual(len(combos), 1)
        self.assertEqual(combos[0].quantity, 1)

    def test_combo_with_registered_trademark_symbol_matches(self):
        """Ensures ® symbol is stripped during comparison so names match."""
        session_id = order_state_singleton.create_session()
        # Standalone without ®
        order_state_singleton.handle_order_update(
            session_id, "add", "SuperSONIC Bacon Double Cheeseburger", "standard", 1, 7.99
        )
        # Combo with ® — should still match
        result_info = order_state_singleton.handle_order_update(
            session_id, "add", "SuperSONIC® Bacon Double Cheeseburger Combo", "standard", 1, 10.99
        )
        items = order_state_singleton.get_order_items(session_id)
        self.assertEqual(len(items), 1)
        self.assertIn("Combo", items[0].item)
        self.assertIn("combo_converted_from", result_info)


if __name__ == "__main__":
    unittest.main()
