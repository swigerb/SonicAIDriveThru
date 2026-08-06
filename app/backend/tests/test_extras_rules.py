import asyncio
import math
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from order_state import order_state_singleton
from rtmt import ToolResultDirection
from tools import update_order


class ExtrasRuleTests(unittest.TestCase):
    def setUp(self):
        order_state_singleton.sessions = {}
        # Pin happy hour OFF so tests are deterministic regardless of wall-clock time.
        self._hh_patcher = patch("order_state.is_happy_hour", return_value=False)
        self._hh_patcher.start()

    def tearDown(self):
        self._hh_patcher.stop()

    def _add_item(self, session_id: str, name: str, size: str, qty: int, price: float):
        order_state_singleton.handle_order_update(session_id, "add", name, size, qty, price)

    def test_block_extra_when_only_hot_dog(self):
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Chili Cheese Coney", "standard", 1, 3.99)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Extra Cheese",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )

        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("extras", result.text.lower())

        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 1)
        self.assertEqual(summary.items[0].item, "Chili Cheese Coney")
        self.assertTrue(math.isclose(summary.total, 3.99, rel_tol=1e-9))

    def test_allow_extra_when_slush_present(self):
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", 1, 2.99)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Flavor Add-In",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)

        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 2)
        extras_item = summary.items[1]
        self.assertEqual(extras_item.item, "Flavor Add-In")
        self.assertEqual(extras_item.quantity, 1)
        expected_total = (1 * 2.99) + 0.50
        self.assertTrue(math.isclose(summary.total, expected_total, rel_tol=1e-9))

    def test_block_extra_when_only_sides(self):
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", 1, 2.79)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Whipped Cream",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )

        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("extras", result.text.lower())

        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 1)
        self.assertEqual(summary.items[0].item, "Tots")
        self.assertTrue(math.isclose(summary.total, 2.79, rel_tol=1e-9))

    # ── Additional extras scenarios ──

    def test_allow_extra_with_shake(self):
        """Extras should be allowed when a shake is in the order."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Classic Vanilla Shake", "large", 1, 4.99)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Whipped Cream",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 2)

    def test_allow_extra_when_mixed_order_has_slush(self):
        """Extras allowed when order has both tots AND a slush."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", 1, 2.79)
        self._add_item(session_id, "Cherry Limeade", "medium", 1, 2.99)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Flavor Add-In",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 3)

    def test_block_extra_with_only_multiple_sides(self):
        """Even multiple sides should not unlock extras."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "large", 3, 3.29)
        self._add_item(session_id, "Onion Rings", "medium", 1, 2.99)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Extra Cheese",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("extras", result.text.lower())

    def test_allow_extra_with_multiple_drinks(self):
        """Multiple drinks in the order — extras should still be allowed."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "small", 1, 2.49)
        self._add_item(session_id, "Ocean Water", "large", 1, 3.49)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Whipped Cream",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 3)

    def test_block_extra_message_differs_with_blocked_base(self):
        """When order has only blocked-category items, the apology should mention them."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Chili Cheese Coney", "standard", 1, 3.99)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Flavor Add-In",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("can't add them", result.text.lower())

    def test_non_extra_item_always_allowed(self):
        """Non-extra items should always be addable regardless of order contents."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", 1, 2.79)

        result = asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": "Cherry Limeade",
                    "size": "medium",
                    "quantity": 1,
                    "price": 2.99,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 2)

    def test_remove_action_bypasses_extra_check(self):
        """Removing an extra should work even without a qualifying drink."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", 1, 2.99)
        self._add_item(session_id, "Flavor Add-In", "standard", 1, 0.50)

        # Now remove the slush, leaving only the extra
        order_state_singleton.handle_order_update(session_id, "remove", "Cherry Limeade", "medium", 1, 2.99)

        # Removing the extra should still work (remove is not gated)
        result = asyncio.run(
            update_order(
                {
                    "action": "remove",
                    "item_name": "Flavor Add-In",
                    "size": "standard",
                    "quantity": 1,
                    "price": 0.50,
                },
                session_id,
            )
        )
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 0)


class HappyHourPricingTests(unittest.TestCase):
    """Explicit coverage for happy-hour drink discount business rule."""

    def setUp(self):
        order_state_singleton.sessions = {}

    def _add_item(self, session_id: str, name: str, size: str, qty: int, price: float):
        order_state_singleton.handle_order_update(session_id, "add", name, size, qty, price)

    @patch("order_state.is_happy_hour", return_value=False)
    def test_drink_full_price_outside_happy_hour(self, _mock_hh):
        """Outside happy hour, drinks are charged at full price."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", 2, 2.99)
        self._add_item(session_id, "Tots", "medium", 1, 2.79)
        summary = order_state_singleton.get_order_summary(session_id)
        expected_subtotal = (2 * 2.99) + 2.79
        self.assertTrue(math.isclose(summary.total, expected_subtotal, rel_tol=1e-9))
        expected_tax = expected_subtotal * 0.08
        self.assertTrue(math.isclose(summary.tax, expected_tax, rel_tol=1e-9))

    @patch("order_state.is_happy_hour", return_value=True)
    def test_drink_discounted_during_happy_hour(self, _mock_hh):
        """During happy hour, drinks receive a 50% discount; sides are unaffected."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", 2, 2.99)
        self._add_item(session_id, "Tots", "medium", 1, 2.79)
        summary = order_state_singleton.get_order_summary(session_id)
        # Drinks discounted 50%, sides at full price
        expected_subtotal = (2 * 2.99 * 0.5) + 2.79
        self.assertTrue(math.isclose(summary.total, expected_subtotal, rel_tol=1e-9))
        expected_tax = expected_subtotal * 0.08
        self.assertTrue(math.isclose(summary.tax, expected_tax, rel_tol=1e-9))

    @patch("order_state.is_happy_hour", return_value=True)
    def test_happy_hour_does_not_discount_non_drinks(self, _mock_hh):
        """Happy hour only applies to drink-category items."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Chili Cheese Coney", "standard", 1, 3.99)
        self._add_item(session_id, "Tots", "medium", 1, 2.79)
        summary = order_state_singleton.get_order_summary(session_id)
        expected_subtotal = 3.99 + 2.79
        self.assertTrue(math.isclose(summary.total, expected_subtotal, rel_tol=1e-9))

    @patch("order_state.is_happy_hour", return_value=True)
    def test_happy_hour_multiple_drink_types(self, _mock_hh):
        """All drink-category items are discounted during happy hour."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", 1, 2.99)
        self._add_item(session_id, "Ocean Water", "large", 1, 3.49)
        self._add_item(session_id, "Classic Vanilla Shake", "large", 1, 4.99)
        summary = order_state_singleton.get_order_summary(session_id)
        expected_subtotal = (2.99 * 0.5) + (3.49 * 0.5) + (4.99 * 0.5)
        self.assertTrue(math.isclose(summary.total, expected_subtotal, rel_tol=1e-9))

    @patch("order_state.is_happy_hour", return_value=True)
    def test_extras_total_with_happy_hour_drink(self, _mock_hh):
        """Extras (non-drinks) are full price even when paired with a discounted drink."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", 1, 2.99)
        self._add_item(session_id, "Flavor Add-In", "standard", 1, 0.50)
        summary = order_state_singleton.get_order_summary(session_id)
        # Cherry Limeade discounted, Flavor Add-In full price
        expected_subtotal = (2.99 * 0.5) + 0.50
        self.assertTrue(math.isclose(summary.total, expected_subtotal, rel_tol=1e-9))


if __name__ == "__main__":
    unittest.main()
