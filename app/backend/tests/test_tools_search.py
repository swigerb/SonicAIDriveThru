import asyncio
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

sys.path.append(str(Path(__file__).resolve().parents[1]))

from rtmt import ToolResultDirection
from tools import _infer_category, _is_extra_item, search


class IsExtraItemTests(unittest.TestCase):
    def test_recognized_extras(self):
        self.assertTrue(_is_extra_item("Extra Patty"))
        self.assertTrue(_is_extra_item("Whipped Cream"))
        self.assertTrue(_is_extra_item("Flavor Add-In"))
        self.assertTrue(_is_extra_item("Extra Cheese"))

    def test_case_insensitive(self):
        self.assertTrue(_is_extra_item("extra patty"))
        self.assertTrue(_is_extra_item("WHIPPED CREAM"))

    def test_non_extras(self):
        self.assertFalse(_is_extra_item("Tots"))
        self.assertFalse(_is_extra_item("Cherry Limeade"))
        self.assertFalse(_is_extra_item("Sonic Cheeseburger"))
        self.assertFalse(_is_extra_item("Onion Rings"))


class InferCategoryTests(unittest.TestCase):
    def test_slush_inferred(self):
        cat = _infer_category("Cherry Limeade")
        self.assertIn("slush", cat)
        cat2 = _infer_category("Ocean Water")
        self.assertIn("slush", cat2)

    def test_shakes_inferred(self):
        cat = _infer_category("Classic Vanilla Shake")
        self.assertIn("shake", cat)
        cat2 = _infer_category("Oreo Blast")
        self.assertIn("shake", cat2)

    def test_combos_inferred(self):
        cat = _infer_category("Sonic Cheeseburger")
        self.assertTrue("burger" in cat or "combo" in cat)

    def test_hot_dogs_inferred(self):
        cat = _infer_category("Chili Cheese Coney")
        self.assertIn("hot dog", cat)
        cat2 = _infer_category("All-American Hot Dog")
        self.assertIn("hot dog", cat2)

    def test_sides_inferred(self):
        cat = _infer_category("Onion Rings")
        self.assertTrue("side" in cat or "tot" in cat or "ring" in cat or len(cat) > 0)

    def test_unknown_returns_empty(self):
        self.assertEqual(_infer_category("Mystery Item XYZ"), "")


class SearchToolTests(unittest.TestCase):
    """Tests for the search() tool function with mocked Azure Search client."""

    def _make_mock_client(self, records):
        """Create a mock SearchClient whose .search() returns an async iterable of records."""
        client = AsyncMock()

        async def _fake_search(**kwargs):
            async def _async_iter():
                for r in records:
                    yield r
            return _async_iter()

        client.search = _fake_search
        return client

    def test_formats_results_with_separator(self):
        records = [
            {"id": "1", "name": "Caramel Craze Latte", "category": "Signature Lattes",
             "description": "A rich latte", "sizes": "S, M, L"},
            {"id": "2", "name": "Glazed Donut", "category": "Donuts & Bakery",
             "description": "Classic glazed", "sizes": "Standard"},
        ]
        client = self._make_mock_client(records)
        result = asyncio.run(search(client, "menuSemanticConfig", "id", "description", "embedding", False, {"query": "latte"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("[1]", result.text)
        self.assertIn("[2]", result.text)
        self.assertIn("-----", result.text)
        self.assertIn("Caramel Craze Latte", result.text)

    def test_no_results_returns_fallback_message(self):
        client = self._make_mock_client([])
        result = asyncio.run(search(client, "menuSemanticConfig", "id", "description", "embedding", False, {"query": "xyz"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("No matching menu entries found", result.text)

    def test_missing_fields_use_defaults(self):
        records = [{"id": "3"}]
        client = self._make_mock_client(records)
        result = asyncio.run(search(client, "menuSemanticConfig", "id", "description", "embedding", False, {"query": "test"}))
        self.assertIn("N/A", result.text)
        self.assertIn("[3]", result.text)

    def test_generic_http_error_returns_apology(self):
        from azure.core.exceptions import HttpResponseError
        client = AsyncMock()
        client.search = AsyncMock(side_effect=HttpResponseError(message="Service unavailable"))
        result = asyncio.run(search(client, "menuSemanticConfig", "id", "description", "embedding", False, {"query": "latte"}))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertIn("can't reach", result.text.lower())

    def test_field_mismatch_triggers_fallback_retry(self):
        from azure.core.exceptions import HttpResponseError

        records = [{"id": "5", "description": "A tasty item"}]
        call_count = 0

        async def _search_with_fallback(**kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                raise HttpResponseError(message="Could not find a property named 'sizes'")
            async def _async_iter():
                for r in records:
                    yield r
            return _async_iter()

        client = AsyncMock()
        client.search = _search_with_fallback

        result = asyncio.run(search(client, "menuSemanticConfig", "id", "description", "embedding", False, {"query": "item"}))
        self.assertEqual(call_count, 2)
        self.assertIn("[5]", result.text)


if __name__ == "__main__":
    unittest.main()
