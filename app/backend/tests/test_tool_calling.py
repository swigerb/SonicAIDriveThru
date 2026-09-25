"""Tests for the search → tool result → response pipeline (tools.py),
order management, upsell hints, and menu utilities (menu_utils.py).

Covers the core RAG flow: search query → Azure AI Search → formatted result → AI,
plus order CRUD, quantity limits, caching, and upsell logic.
"""

import asyncio
import json
import math
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from menu_utils import (
    canonical_size_key,
    infer_category,
    normalize_size,
)
from order_state import order_state_singleton
from rtmt import ToolResult, ToolResultDirection
from tools import (
    MAX_QUANTITY_PER_ITEM,
    MAX_TOTAL_ITEMS,
    MOCK_MACHINE_STATUS,
    _format_size_human_readable,
    _is_extra_item,
    _search_cache,
    _search_cfg,
    _SearchCache,
    get_order,
    reset_order,
    search,
    update_order,
    validate_customization,
)

# ── Helpers ──

def _run(coro):
    return asyncio.run(coro)


def _make_mock_search_client(records):
    """Create a mock SearchClient that returns an async iterable of records."""
    client = AsyncMock()

    async def _fake_search(**kwargs):
        async def _async_iter():
            for r in records:
                yield r
        return _async_iter()

    client.search = _fake_search
    return client


def _make_session():
    """Create a fresh session and return its ID."""
    order_state_singleton.sessions = {}
    return order_state_singleton.create_session()


# ═══════════════════════════════════════════════════════════════════════════════
# SEARCH TOOL TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class SearchQueryFormattingTests(unittest.TestCase):
    """Test search query formatting and result parsing."""

    def setUp(self):
        _search_cache.clear()

    def test_formats_results_with_item_name_and_category(self):
        records = [
            {"id": "1", "name": "Cherry Limeade", "category": "Slushes & Drinks",
             "description": "A classic Sonic slush", "sizes": '[{"size":"Medium","price":"2.99"}]'},
        ]
        client = _make_mock_search_client(records)
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("Cherry Limeade", result.text)
        self.assertIn("Slushes & Drinks", result.text)
        self.assertIn("[1]", result.text)

    def test_sizes_json_formatted_human_readable(self):
        records = [
            {"id": "1", "name": "Cherry Limeade", "category": "Slushes",
             "sizes": json.dumps([{"size": "Medium", "price": "2.99"}, {"size": "Large", "price": "3.49"}])},
        ]
        client = _make_mock_search_client(records)
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        self.assertIn("Medium ($2.99)", result.text)
        self.assertIn("Large ($3.49)", result.text)

    def test_non_json_sizes_displayed_as_is(self):
        records = [{"id": "1", "name": "Tots", "category": "Sides", "sizes": "One Size"}]
        client = _make_mock_search_client(records)
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "tots"}))
        self.assertIn("One Size", result.text)


class SearchEmptyResultTests(unittest.TestCase):
    """Test empty search results handling."""

    def setUp(self):
        _search_cache.clear()

    def test_empty_results_returns_fallback(self):
        client = _make_mock_search_client([])
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "nonexistent"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("No matching menu entries found", result.text)


class SearchErrorHandlingTests(unittest.TestCase):
    """Test search error and timeout handling."""

    def setUp(self):
        _search_cache.clear()

    def test_http_error_returns_friendly_message(self):
        from azure.core.exceptions import HttpResponseError
        client = AsyncMock()
        client.search = AsyncMock(side_effect=HttpResponseError(message="Service unavailable"))
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "test"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertTrue("can't reach" in result.text.lower() or "sorry" in result.text.lower())

    def test_field_mismatch_retries_with_fallback(self):
        from azure.core.exceptions import HttpResponseError
        call_count = 0

        async def _search_fallback(**kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                raise HttpResponseError(message="Could not find a property named 'sizes'")
            async def _iter():
                yield {"id": "1", "description": "fallback result"}
            return _iter()

        client = AsyncMock()
        client.search = _search_fallback
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "item"}))
        self.assertEqual(call_count, 2)
        self.assertIn("[1]", result.text)

    def test_timeout_bounds_the_iteration_not_just_the_initial_call(self):
        """PR #50 review (should-fix 4): ``azure-search-documents``' async ``SearchClient.search``
        is lazy -- calling it does no HTTP I/O; the real request only happens once the returned
        async-iterable is actually iterated (see the comment above ``_fetch_records`` in
        tools.py). A mock client whose ``search()`` call returns instantly but whose iteration
        sleeps past the configured timeout reproduces exactly that shape: if
        ``asyncio.wait_for`` only wrapped the (instant) ``search()`` call, this would never time
        out. With the fix wrapping the whole collect, it must."""
        async def _slow_iteration_search(**kwargs):
            async def _iter():
                await asyncio.sleep(0.2)
                yield {"id": "1", "name": "Cherry Limeade", "category": "Slushes", "sizes": "N/A"}
            return _iter()  # the call itself returns immediately -- the delay is in iterating

        client = AsyncMock()
        client.search = _slow_iteration_search
        with patch.dict(_search_cfg, {"timeout_seconds": 0.05}):
            result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertTrue("try that again" in result.text.lower() or "trouble" in result.text.lower())

    def test_hanging_iterator_times_out_via_search_service_unavailable_error_key(self):
        """PR #50 review (second round, should-fix, kills Y5): the previous test above proves
        the timeout fires, but with no ``_prompt_loader`` configured it only ever exercises the
        hardcoded fallback string in ``tools.py`` -- it can never notice if the *error key*
        requested on timeout drifted away from ``"search_service_unavailable"`` (e.g. a typo'd
        key, or accidentally reusing a different error's key), because the fallback text is
        returned before ``render_error`` is ever called. This test wires up the real
        ``PromptLoader(brand="sonic")`` (the actual ``error_messages.yaml`` used in production)
        so the returned text is not the source-code fallback but the literal rendered value of
        ``search_service_unavailable`` -- reproducing the same hanging-iterator shape (the
        ``search()`` call returns instantly, the real HTTP request happens during iteration) with
        ``timeout_seconds: 0.05``."""
        from prompt_loader import PromptLoader

        async def _hanging_iteration_search(**kwargs):
            async def _iter():
                await asyncio.sleep(10)
                yield {"id": "1", "name": "Cherry Limeade", "category": "Slushes", "sizes": "N/A"}

            return _iter()

        client = AsyncMock()
        client.search = _hanging_iteration_search
        loader = PromptLoader(brand="sonic")
        with patch.dict(_search_cfg, {"timeout_seconds": 0.05}), patch("tools._prompt_loader", loader):
            result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertEqual(result.text, loader.render_error("search_service_unavailable"))


class SearchCacheTests(unittest.TestCase):
    """Test search result caching."""

    def setUp(self):
        _search_cache.clear()

    def test_cache_hit_returns_same_result(self):
        records = [{"id": "1", "name": "Cherry Limeade", "category": "Slushes", "sizes": "N/A"}]
        client = _make_mock_search_client(records)
        r1 = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        # Second call should hit cache
        r2 = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        self.assertEqual(r1.text, r2.text)

    def test_cache_case_insensitive(self):
        records = [{"id": "1", "name": "Tots", "category": "Sides", "sizes": "N/A"}]
        client = _make_mock_search_client(records)
        _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "TOTS"}))
        # Same query, different case
        r2 = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "  tots  "}))
        self.assertIn("Tots", r2.text)

    def test_cache_respects_ttl(self):
        cache = _SearchCache(max_size=10)
        tr = ToolResult("cached", ToolResultDirection.TO_SERVER)
        cache.put("key", tr)
        self.assertIsNotNone(cache.get("key"))
        # Expire the entry
        cache._store["key"] = (time.monotonic() - 999, tr)
        self.assertIsNone(cache.get("key"))

    def test_cache_evicts_oldest_when_full(self):
        cache = _SearchCache(max_size=2)
        cache.put("a", ToolResult("A", ToolResultDirection.TO_SERVER))
        time.sleep(0.01)
        cache.put("b", ToolResult("B", ToolResultDirection.TO_SERVER))
        time.sleep(0.01)
        cache.put("c", ToolResult("C", ToolResultDirection.TO_SERVER))
        # "a" should be evicted (oldest)
        self.assertIsNone(cache.get("a"))
        self.assertIsNotNone(cache.get("b"))
        self.assertIsNotNone(cache.get("c"))

    def test_cache_clear(self):
        cache = _SearchCache()
        cache.put("x", ToolResult("X", ToolResultDirection.TO_SERVER))
        cache.clear()
        self.assertIsNone(cache.get("x"))


class SearchOOSAnnotationTests(unittest.TestCase):
    """Test out-of-stock annotations for ice cream machine items."""

    def setUp(self):
        _search_cache.clear()

    def test_shake_flagged_oos_when_machine_down(self):
        records = [{"id": "1", "name": "Classic Vanilla Shake", "category": "Shakes", "sizes": "N/A"}]
        client = _make_mock_search_client(records)
        with patch.dict(MOCK_MACHINE_STATUS, {"ice_cream_machine": "down"}):
            result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "shake"}))
        self.assertIn("OOS", result.text)
        self.assertIn("Ice cream machine", result.text)

    def test_non_ice_cream_item_not_flagged(self):
        records = [{"id": "1", "name": "Cherry Limeade", "category": "Slushes", "sizes": "N/A"}]
        client = _make_mock_search_client(records)
        result = _run(search(client, "cfg", "id", "description", "embedding", False, {"query": "limeade"}))
        self.assertNotIn("OOS", result.text)


# ═══════════════════════════════════════════════════════════════════════════════
# ORDER MANAGEMENT TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class UpdateOrderAddTests(unittest.TestCase):
    """Test update_order with add action."""

    def test_add_valid_item(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 1, "price": 2.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        self.assertIn("Cherry Limeade", result.text)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 1)

    def test_delta_text_spoken_total_matches_finalTotalDisplay_exactly(self):
        """PR #50 review follow-up: the delta text's spoken total must be the exact same string
        as summary.finalTotalDisplay -- there is exactly one format_money() call per mutation
        (inside OrderSummary), and every spoken surface downstream reads that string rather than
        recomputing its own."""
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 1, "price": 2.79,
        }, sid))
        summary = order_state_singleton.get_order_summary(sid)
        self.assertIn(summary.finalTotalDisplay, result.text)

    def test_add_multiple_quantity(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 3, "price": 2.79,
        }, sid))
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(summary.items[0].quantity, 3)

    def test_add_zero_price_rejected(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 1, "price": 0.0,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 0)

    def test_add_negative_price_rejected(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 1, "price": -1.0,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)


class UpdateOrderRemoveTests(unittest.TestCase):
    """Test update_order with remove action."""

    def test_remove_existing_item(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 2, "price": 2.99,
        }, sid))
        result = _run(update_order({
            "action": "remove", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 1,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(summary.items[0].quantity, 1)

    def test_remove_all_clears_item(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 1, "price": 2.79,
        }, sid))
        _run(update_order({
            "action": "remove", "item_name": "Tots",
            "size": "medium", "quantity": 1,
        }, sid))
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 0)


class UpdateOrderQuantityLimitTests(unittest.TestCase):
    """Test per-item and total order quantity limits."""

    def test_per_item_limit_exceeded(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Burger",
            "size": "standard", "quantity": MAX_QUANTITY_PER_ITEM + 1, "price": 5.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 0)

    def test_per_item_limit_exact_succeeds(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Burger",
            "size": "standard", "quantity": MAX_QUANTITY_PER_ITEM, "price": 5.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)

    def test_incremental_add_over_limit_rejected(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Burger",
            "size": "standard", "quantity": MAX_QUANTITY_PER_ITEM - 1, "price": 5.99,
        }, sid))
        result = _run(update_order({
            "action": "add", "item_name": "Burger",
            "size": "standard", "quantity": 2, "price": 5.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(summary.items[0].quantity, MAX_QUANTITY_PER_ITEM - 1)

    def test_total_order_limit_exceeded(self):
        sid = _make_session()
        # Fill to just below total limit with varied items
        for i in range(MAX_TOTAL_ITEMS):
            order_state_singleton.handle_order_update(sid, "add", f"Item{i}", "standard", 1, 1.0)
        result = _run(update_order({
            "action": "add", "item_name": "One More",
            "size": "standard", "quantity": 1, "price": 1.0,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("big order", result.text.lower())


class GetOrderTests(unittest.TestCase):
    """Test get_order tool."""

    def test_get_order_empty(self):
        sid = _make_session()
        result = _run(get_order({}, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        self.assertIn("empty", result.text.lower())

    def test_get_order_with_items(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 2, 2.99)
        result = _run(get_order({}, sid))
        self.assertIn("Cherry Limeade", result.text)
        self.assertRegex(result.text, r"\d+\.\d{2}")

    def test_get_order_returns_json_summary_for_client(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        result = _run(get_order({}, sid))
        client_text = result.to_client_text()
        parsed = json.loads(client_text)
        self.assertIn("items", parsed)
        self.assertIn("finalTotal", parsed)


class ResetOrderTests(unittest.TestCase):
    """Test reset_order (clear_order) tool."""

    def test_reset_clears_all_items(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 2, 2.99)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        result = _run(reset_order({}, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 0)
        self.assertAlmostEqual(summary.finalTotal, 0.0)

    def test_reset_empty_order_is_safe(self):
        sid = _make_session()
        result = _run(reset_order({}, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)


class TaxCalculationTests(unittest.TestCase):
    """Test tax calculation accuracy."""

    def setUp(self):
        # Pin happy hour OFF so drink prices aren't affected by wall-clock time.
        self._hh_patcher = patch("order_state.is_happy_hour", return_value=False)
        self._hh_patcher.start()

    def tearDown(self):
        self._hh_patcher.stop()

    def test_tax_rate_applied_correctly(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 10.00)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertTrue(math.isclose(summary.tax, 0.80, rel_tol=1e-9))
        self.assertTrue(math.isclose(summary.finalTotal, 10.80, rel_tol=1e-9))

    def test_tax_on_multiple_items(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 2, 2.99)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        summary = order_state_singleton.get_order_summary(sid)
        expected_subtotal = (2 * 2.99) + 2.79
        expected_tax = expected_subtotal * 0.08
        self.assertTrue(math.isclose(summary.tax, expected_tax, rel_tol=1e-9))

    @patch("order_state.is_happy_hour", return_value=True)
    def test_tax_on_multiple_items_during_happy_hour(self, _mock_hh):
        """Tax is computed on the discounted subtotal during happy hour."""
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 2, 2.99)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        summary = order_state_singleton.get_order_summary(sid)
        expected_subtotal = (2 * 2.99 * 0.5) + 2.79
        expected_tax = expected_subtotal * 0.08
        self.assertTrue(math.isclose(summary.total, expected_subtotal, rel_tol=1e-9))
        self.assertTrue(math.isclose(summary.tax, expected_tax, rel_tol=1e-9))


# ═══════════════════════════════════════════════════════════════════════════════
# UPSELL HINT TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class UpsellHintTests(unittest.TestCase):
    """Test category-based upsell hints in tool results."""

    def test_burger_triggers_combo_upsell(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Sonic Cheeseburger",
            "size": "standard", "quantity": 1, "price": 5.99,
        }, sid))
        # Should contain upsell about combo
        self.assertTrue("combo" in result.text.lower() or "upsell" in result.text.lower())

    def test_drink_triggers_addon_upsell(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 1, "price": 2.99,
        }, sid))
        self.assertTrue(
            "flavor" in result.text.lower()
            or "add-in" in result.text.lower()
            or "upsell" in result.text.lower()
            or "side" in result.text.lower()
        )

    def test_side_triggers_drink_upsell(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 1, "price": 2.79,
        }, sid))
        self.assertTrue(
            "drink" in result.text.lower()
            or "slush" in result.text.lower()
            or "upsell" in result.text.lower()
        )

    def test_combo_triggers_upgrade_upsell(self):
        sid = _make_session()
        # Add side+drink first so combo is complete (no missing items hint)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.99)
        result = _run(update_order({
            "action": "add", "item_name": "SuperSONIC Cheeseburger Combo",
            "size": "standard", "quantity": 1, "price": 8.49,
        }, sid))
        # Should mention upgrade or upsell
        self.assertTrue(
            "upgrade" in result.text.lower()
            or "large" in result.text.lower()
            or "upsell" in result.text.lower()
            or "shake" in result.text.lower()
        )


class ComboValidationInToolsTests(unittest.TestCase):
    """Test combo validation hints in update_order results."""

    def test_incomplete_combo_triggers_system_hint(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "SuperSONIC Cheeseburger Combo",
            "size": "standard", "quantity": 1, "price": 8.49,
        }, sid))
        self.assertIn("SYSTEM HINT", result.text)
        self.assertIn("side", result.text.lower())
        self.assertIn("drink", result.text.lower())

    def test_complete_combo_no_hint(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.99)
        result = _run(update_order({
            "action": "add", "item_name": "SuperSONIC Cheeseburger Combo",
            "size": "standard", "quantity": 1, "price": 8.49,
        }, sid))
        self.assertNotIn("SYSTEM HINT", result.text)


# ═══════════════════════════════════════════════════════════════════════════════
# MENU UTILS TESTS
# ═══════════════════════════════════════════════════════════════════════════════

class NormalizeSizeTests(unittest.TestCase):
    """Test size normalization."""

    def test_canonical_sizes(self):
        self.assertEqual(normalize_size("small"), "Small")
        self.assertEqual(normalize_size("medium"), "Medium")
        self.assertEqual(normalize_size("large"), "Large")
        self.assertEqual(normalize_size("mini"), "Mini")

    def test_aliases(self):
        self.assertEqual(normalize_size("s"), "Small")
        self.assertEqual(normalize_size("m"), "Medium")
        self.assertEqual(normalize_size("l"), "Large")
        self.assertEqual(normalize_size("rt44"), "Route 44")
        self.assertEqual(normalize_size("rt 44"), "Route 44")
        self.assertEqual(normalize_size("44"), "Route 44")
        self.assertEqual(normalize_size("44oz"), "Route 44")

    def test_punctuation_and_spelling_aliases_still_display_route_44(self):
        """PR #50 review follow-up: "Route-44" and "rt. 44" must display like every other Route
        44 spelling -- these previously fell through to "" because normalize_size looked up
        SIZE_ALIASES verbatim instead of sharing canonical_size_key's punctuation-stripped lookup.
        """
        self.assertEqual(normalize_size("Route-44"), "Route 44")
        self.assertEqual(normalize_size("rt. 44"), "Route 44")
        self.assertEqual(normalize_size("44 oz"), "Route 44")

    def test_extra_large_alias_displays_extra_large(self):
        """PR #50 review follow-up: "Extra Large" must resolve through the same alias table as
        its own short form "xl" so the two spellings can never end up on different order lines.
        """
        self.assertEqual(normalize_size("Extra Large"), "Extra Large")
        self.assertEqual(normalize_size("xl"), "Extra Large")

    def test_hidden_sizes_return_empty(self):
        self.assertEqual(normalize_size("standard"), "")
        self.assertEqual(normalize_size("n/a"), "")
        self.assertEqual(normalize_size("na"), "")
        self.assertEqual(normalize_size("none"), "")
        self.assertEqual(normalize_size("n.a."), "")
        self.assertEqual(normalize_size(""), "")

    def test_case_insensitive(self):
        self.assertEqual(normalize_size("SMALL"), "Small")
        self.assertEqual(normalize_size("Medium"), "Medium")
        self.assertEqual(normalize_size("LARGE"), "Large")

    def test_whitespace_stripped(self):
        self.assertEqual(normalize_size("  small  "), "Small")
        self.assertEqual(normalize_size(" rt44 "), "Route 44")

    def test_unknown_size_returns_empty(self):
        self.assertEqual(normalize_size("jumbo"), "")
        self.assertEqual(normalize_size("venti"), "")

    def test_none_input_returns_empty(self):
        self.assertEqual(normalize_size(None), "")


class CanonicalSizeKeyTests(unittest.TestCase):
    """canonical_size_key is the wire contract: items[].size on the order-summary payload is
    documented (README, tests/conformance) to always be this canonical, lowercase, alias-resolved
    key -- never the raw spoken/typed spelling and never the human-readable display string. These
    tests pin every known Route 44 spelling (including the punctuation variants added by PR #50
    review item X3) and the Extra Large/xl pair onto a single key each, so two different spellings
    of the same size can never land on two different order lines.
    """

    def test_route_44_aliases_all_collapse_to_one_key(self):
        aliases = ["rt44", "rt 44", "44", "44oz", "44 oz", "route44", "Route-44", "rt. 44", "Route 44", "ROUTE 44"]
        for alias in aliases:
            with self.subTest(alias=alias):
                self.assertEqual(canonical_size_key(alias), "route 44")

    def test_extra_large_and_xl_collapse_to_one_key(self):
        self.assertEqual(canonical_size_key("Extra Large"), "xl")
        self.assertEqual(canonical_size_key("xl"), "xl")
        self.assertEqual(canonical_size_key("XL"), "xl")

    def test_key_is_always_lowercase(self):
        for raw in ["MEDIUM", "Small", "  Large  ", "RT44"]:
            with self.subTest(raw=raw):
                self.assertEqual(canonical_size_key(raw), canonical_size_key(raw).lower())


class InferCategoryTests(unittest.TestCase):
    """Test category inference from item names."""

    def test_slush_keywords(self):
        self.assertIn("slush", infer_category("Cherry Limeade"))
        self.assertIn("slush", infer_category("Ocean Water"))

    def test_shake_keywords(self):
        cat = infer_category("Classic Vanilla Shake")
        self.assertTrue("shake" in cat)
        cat2 = infer_category("Oreo Blast")
        self.assertTrue("shake" in cat2)

    def test_burger_keywords(self):
        cat = infer_category("Sonic Cheeseburger")
        self.assertTrue("burger" in cat or "combo" in cat)

    def test_hot_dog_keywords(self):
        self.assertIn("hot dog", infer_category("Chili Cheese Coney"))

    def test_sides_keywords(self):
        cat = infer_category("Tots")
        self.assertTrue("side" in cat or cat != "")
        cat2 = infer_category("Onion Rings")
        self.assertTrue("side" in cat2 or cat2 != "")

    def test_drink_keywords(self):
        cat = infer_category("Sweet Tea")
        self.assertTrue("drink" in cat)
        cat2 = infer_category("Lemonade")
        self.assertTrue("drink" in cat2)

    def test_unknown_returns_empty(self):
        self.assertEqual(infer_category("Mystery Item XYZ 999"), "")

    def test_case_insensitive(self):
        cat = infer_category("CHERRY LIMEADE")
        self.assertTrue(len(cat) > 0)

    def test_malt_inferred_as_shake(self):
        cat = infer_category("Chocolate Malt")
        self.assertTrue("shake" in cat)


class FormatSizeHumanReadableTests(unittest.TestCase):
    """Test _format_size_human_readable from tools.py."""

    def test_known_sizes(self):
        self.assertEqual(_format_size_human_readable("medium"), "Medium")
        self.assertEqual(_format_size_human_readable("large"), "Large")

    def test_unknown_size_capitalized(self):
        self.assertEqual(_format_size_human_readable("jumbo"), "Jumbo")

    def test_alias_resolved(self):
        self.assertEqual(_format_size_human_readable("rt44"), "Route 44")


class IsExtraItemTests(unittest.TestCase):
    """Test extra item detection."""

    def test_recognized_extras(self):
        self.assertTrue(_is_extra_item("Flavor Add-In"))
        self.assertTrue(_is_extra_item("Whipped Cream"))
        self.assertTrue(_is_extra_item("Extra Patty"))
        self.assertTrue(_is_extra_item("Extra Cheese"))
        self.assertTrue(_is_extra_item("Add Bacon"))

    def test_non_extras(self):
        self.assertFalse(_is_extra_item("Cherry Limeade"))
        self.assertFalse(_is_extra_item("Tots"))
        self.assertFalse(_is_extra_item("Sonic Cheeseburger"))

    def test_case_insensitive(self):
        self.assertTrue(_is_extra_item("EXTRA PATTY"))
        self.assertTrue(_is_extra_item("whipped cream"))


class ExtrasValidationTests(unittest.TestCase):
    """Test extras only allowed on appropriate base items."""

    def test_extra_blocked_without_base_item(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Flavor Add-In",
            "size": "standard", "quantity": 1, "price": 0.79,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)

    def test_extra_allowed_with_drink_in_order(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.99)
        result = _run(update_order({
            "action": "add", "item_name": "Flavor Add-In",
            "size": "standard", "quantity": 1, "price": 0.79,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)

    def test_extra_blocked_with_only_side_in_order(self):
        sid = _make_session()
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        result = _run(update_order({
            "action": "add", "item_name": "Flavor Add-In",
            "size": "standard", "quantity": 1, "price": 0.79,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)


class ValidateCustomizationTests(unittest.TestCase):
    """PR #50 review (third round, minor): validate_customization() used to strip parenthesized
    modifiers itself via item_name.split("(")[0] — a second, independent implementation of the
    same rule already centralized as menu_utils.strip_modifiers(). It now reuses that one helper."""

    def test_customised_item_name_is_recognised_by_category_lookup(self):
        # "Cherry Limeade (Light Ice)" must classify the same as "Cherry Limeade" so the
        # forbidden-mod list for slushes/drinks (which includes "cheese") still applies.
        error = validate_customization("Cherry Limeade (Light Ice)", "extra cheese")
        self.assertIsNotNone(error)

    def test_error_message_base_name_excludes_the_modifier_suffix(self):
        error = validate_customization("Cherry Limeade (Light Ice)", "extra cheese")
        self.assertNotIn("(Light Ice)", error)
        self.assertIn("Cherry Limeade", error)

    def test_plain_item_name_without_modifiers_is_unaffected(self):
        error = validate_customization("Cherry Limeade", "extra cheese")
        self.assertIsNotNone(error)
        self.assertIn("Cherry Limeade", error)

    def test_valid_customization_returns_none(self):
        error = validate_customization("Cherry Limeade (Light Ice)", "extra cherries")
        self.assertIsNone(error)


# ═══════════════════════════════════════════════════════════════════════════════
# EDGE CASES
# ═══════════════════════════════════════════════════════════════════════════════

class EdgeCaseTests(unittest.TestCase):
    """Test edge cases in the tool pipeline."""

    def test_special_characters_in_item_name(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "O'Reilly's Burger™",
            "size": "standard", "quantity": 1, "price": 6.99,
        }, sid))
        # Should not crash
        self.assertIsNotNone(result)

    def test_empty_cart_get_order(self):
        sid = _make_session()
        result = _run(get_order({}, sid))
        self.assertIn("empty", result.text.lower())

    def test_duplicate_item_different_sizes(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 1, "price": 2.99,
        }, sid))
        _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "large", "quantity": 1, "price": 3.49,
        }, sid))
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 2)

    def test_same_item_same_size_increments_quantity(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 1, "price": 2.79,
        }, sid))
        _run(update_order({
            "action": "add", "item_name": "Tots",
            "size": "medium", "quantity": 2, "price": 2.79,
        }, sid))
        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 1)
        self.assertEqual(summary.items[0].quantity, 3)

    def test_remove_from_empty_cart(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "remove", "item_name": "Phantom",
            "size": "medium", "quantity": 1,
        }, sid))
        # Should not crash, still returns valid result
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)


# ═══════════════════════════════════════════════════════════════════════════════
# HAPPY HOUR BANNER WORDING TESTS (PR #61 review, must-fix 2)
# ═══════════════════════════════════════════════════════════════════════════════

class HappyHourBannerWordingTests(unittest.TestCase):
    """The [HAPPY HOUR ACTIVE: ...] banner appended to update_order/get_order tool
    results must say explicitly that slushes and fountain drinks are half-price
    and that shakes, Blasts and sundaes are full price -- the old wording ("drinks
    and slushes are half-price!") said nothing about shakes/Blasts/sundaes at all,
    which is how the carhop kept treating them as discounted (issue #39 / #61).
    """

    NEW_BANNER = "[HAPPY HOUR ACTIVE: slushes and fountain drinks are half-price; shakes, Blasts and sundaes are full price]"

    def setUp(self):
        self._hh_patcher = patch("tools.is_happy_hour", return_value=True)
        self._hh_patcher.start()

    def tearDown(self):
        self._hh_patcher.stop()

    def test_update_order_banner_states_shakes_blasts_sundaes_are_full_price(self):
        sid = _make_session()
        result = _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 1, "price": 2.99,
        }, sid))
        self.assertIn(self.NEW_BANNER, result.text)

    def test_get_order_banner_states_shakes_blasts_sundaes_are_full_price(self):
        sid = _make_session()
        _run(update_order({
            "action": "add", "item_name": "Cherry Limeade",
            "size": "medium", "quantity": 1, "price": 2.99,
        }, sid))
        result = _run(get_order({}, sid))
        self.assertIn(self.NEW_BANNER, result.text)

    def test_no_banner_outside_happy_hour(self):
        self._hh_patcher.stop()
        with patch("tools.is_happy_hour", return_value=False):
            sid = _make_session()
            result = _run(update_order({
                "action": "add", "item_name": "Cherry Limeade",
                "size": "medium", "quantity": 1, "price": 2.99,
            }, sid))
            self.assertNotIn("HAPPY HOUR", result.text)
        self._hh_patcher.start()


if __name__ == "__main__":
    unittest.main()
