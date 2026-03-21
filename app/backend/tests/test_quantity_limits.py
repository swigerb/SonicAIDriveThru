import asyncio
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from order_state import order_state_singleton
from rtmt import ToolResultDirection
from tools import MAX_QUANTITY_PER_ITEM, MAX_TOTAL_ITEMS, update_order


class QuantityLimitTests(unittest.TestCase):
    def setUp(self):
        order_state_singleton.sessions = {}

    def _add_item(self, session_id: str, name: str, size: str, qty: int, price: float):
        order_state_singleton.handle_order_update(session_id, "add", name, size, qty, price)

    def _run(self, coro):
        return asyncio.run(coro)

    # ── Per-item limit tests ──

    def test_order_exactly_max_quantity_succeeds(self):
        """Ordering exactly MAX_QUANTITY_PER_ITEM (10) in one shot should succeed."""
        session_id = order_state_singleton.create_session()
        result = self._run(update_order({
            "action": "add",
            "item_name": "SuperSONIC Cheeseburger",
            "size": "standard",
            "quantity": MAX_QUANTITY_PER_ITEM,
            "price": 5.99,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(summary.items[0].quantity, MAX_QUANTITY_PER_ITEM)

    def test_order_one_over_max_quantity_rejected(self):
        """Ordering MAX_QUANTITY_PER_ITEM + 1 (11) should be rejected."""
        session_id = order_state_singleton.create_session()
        result = self._run(update_order({
            "action": "add",
            "item_name": "SuperSONIC Cheeseburger",
            "size": "standard",
            "quantity": MAX_QUANTITY_PER_ITEM + 1,
            "price": 5.99,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("up to", result.text.lower())
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 0)

    def test_incremental_add_up_to_max_succeeds(self):
        """Adding items incrementally up to MAX should succeed at exactly MAX."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", 7, 2.79)

        result = self._run(update_order({
            "action": "add",
            "item_name": "Tots",
            "size": "medium",
            "quantity": 3,
            "price": 2.79,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(summary.items[0].quantity, 10)

    def test_incremental_add_over_max_rejected(self):
        """Adding one more than allowed to an existing item should be rejected."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", MAX_QUANTITY_PER_ITEM, 2.79)

        result = self._run(update_order({
            "action": "add",
            "item_name": "Tots",
            "size": "medium",
            "quantity": 1,
            "price": 2.79,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("already have", result.text.lower())

    def test_different_sizes_have_separate_limits(self):
        """Same item in different sizes should have independent limits."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Cherry Limeade", "medium", MAX_QUANTITY_PER_ITEM, 2.99)

        result = self._run(update_order({
            "action": "add",
            "item_name": "Cherry Limeade",
            "size": "large",
            "quantity": MAX_QUANTITY_PER_ITEM,
            "price": 3.49,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(len(summary.items), 2)

    # ── Total order limit tests ──

    def test_total_order_at_max_succeeds(self):
        """Total items exactly at MAX_TOTAL_ITEMS should succeed."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", MAX_TOTAL_ITEMS - 1, 2.79)

        result = self._run(update_order({
            "action": "add",
            "item_name": "Cherry Limeade",
            "size": "large",
            "quantity": 1,
            "price": 3.49,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)

    def test_total_order_over_max_rejected(self):
        """Exceeding MAX_TOTAL_ITEMS should be rejected."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", MAX_TOTAL_ITEMS, 2.79)

        result = self._run(update_order({
            "action": "add",
            "item_name": "Cherry Limeade",
            "size": "large",
            "quantity": 1,
            "price": 3.49,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("tops out", result.text.lower())

    # ── Response format tests ──

    def test_success_response_goes_to_both(self):
        """Successful orders must go TO_BOTH so the AI can continue conversation."""
        session_id = order_state_singleton.create_session()
        result = self._run(update_order({
            "action": "add",
            "item_name": "Cherry Limeade",
            "size": "medium",
            "quantity": 1,
            "price": 2.99,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        self.assertIn("items", result.text)

    def test_limit_response_goes_to_server(self):
        """Limit-exceeded responses go TO_SERVER so the AI can relay and ask a follow-up."""
        session_id = order_state_singleton.create_session()
        result = self._run(update_order({
            "action": "add",
            "item_name": "SuperSONIC Cheeseburger",
            "size": "standard",
            "quantity": MAX_QUANTITY_PER_ITEM + 5,
            "price": 5.99,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("?", result.text)

    def test_limit_response_ends_with_question(self):
        """All limit-exceeded responses should end with a question to keep conversation alive."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", MAX_QUANTITY_PER_ITEM, 2.79)

        result = self._run(update_order({
            "action": "add",
            "item_name": "Tots",
            "size": "medium",
            "quantity": 1,
            "price": 2.79,
        }, session_id))

        self.assertIn("?", result.text)

    def test_remove_bypasses_limits(self):
        """Remove actions should never be blocked by limits."""
        session_id = order_state_singleton.create_session()
        self._add_item(session_id, "Tots", "medium", MAX_QUANTITY_PER_ITEM, 2.79)

        result = self._run(update_order({
            "action": "remove",
            "item_name": "Tots",
            "size": "medium",
            "quantity": 3,
            "price": 2.79,
        }, session_id))

        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(session_id)
        self.assertEqual(summary.items[0].quantity, 7)


if __name__ == "__main__":
    unittest.main()
