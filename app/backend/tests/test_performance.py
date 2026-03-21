"""Performance test harness for the Sonic AI Drive-Thru voice interaction pipeline.

Validates latency benchmarks, memory efficiency, concurrency safety,
and production readiness — all without touching real Azure services.
"""

import asyncio
import concurrent.futures
import gc
import json
import os
import sys
import time
import tracemalloc
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from models import OrderItem, OrderSummary
from order_state import OrderState, order_state_singleton
from rtmt import ToolResult, ToolResultDirection

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _timed(fn, *args, **kwargs):
    """Run *fn* and return (result, elapsed_ms)."""
    start = time.perf_counter()
    result = fn(*args, **kwargs)
    elapsed_ms = (time.perf_counter() - start) * 1000
    return result, elapsed_ms


def _timed_async(coro):
    """Run an awaitable and return (result, elapsed_ms)."""
    loop = asyncio.new_event_loop()
    try:
        start = time.perf_counter()
        result = loop.run_until_complete(coro)
        elapsed_ms = (time.perf_counter() - start) * 1000
        return result, elapsed_ms
    finally:
        loop.close()


# ===================================================================
# 1. Response Latency Benchmarks
# ===================================================================

class OrderStateLatencyTests(unittest.TestCase):
    """order_state operations must complete in <5 ms."""

    THRESHOLD_MS = 5.0

    def setUp(self):
        order_state_singleton.sessions = {}

    def test_create_session_latency(self):
        _, elapsed = _timed(order_state_singleton.create_session)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"create_session took {elapsed:.2f} ms (limit {self.THRESHOLD_MS} ms)")

    def test_add_item_latency(self):
        sid = order_state_singleton.create_session()
        _, elapsed = _timed(
            order_state_singleton.handle_order_update,
            sid, "add", "Cherry Limeade", "Large", 1, 3.49,
        )
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"handle_order_update(add) took {elapsed:.2f} ms")

    def test_remove_item_latency(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "Large", 2, 3.49)
        _, elapsed = _timed(
            order_state_singleton.handle_order_update,
            sid, "remove", "Cherry Limeade", "Large", 1, 3.49,
        )
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"handle_order_update(remove) took {elapsed:.2f} ms")

    def test_get_order_summary_latency(self):
        sid = order_state_singleton.create_session()
        for i in range(10):
            order_state_singleton.handle_order_update(sid, "add", f"Item {i}", "Medium", 1, 2.99)
        _, elapsed = _timed(order_state_singleton.get_order_summary, sid)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"get_order_summary took {elapsed:.2f} ms")

    def test_advance_round_trip_latency(self):
        sid = order_state_singleton.create_session()
        _, elapsed = _timed(order_state_singleton.advance_round_trip, sid)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"advance_round_trip took {elapsed:.2f} ms")

    def test_delete_session_latency(self):
        sid = order_state_singleton.create_session()
        _, elapsed = _timed(order_state_singleton.delete_session, sid)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"delete_session took {elapsed:.2f} ms")


class MenuSearchFormattingLatencyTests(unittest.TestCase):
    """Menu search result formatting must complete in <10 ms."""

    THRESHOLD_MS = 10.0

    def _make_mock_client(self, records):
        client = AsyncMock()
        async def _fake_search(**kwargs):
            async def _async_iter():
                for r in records:
                    yield r
            return _async_iter()
        client.search = _fake_search
        return client

    def test_search_formatting_five_results(self):
        from tools import search

        records = [
            {"id": str(i), "name": f"Item {i}", "category": "Drinks",
             "description": f"Description {i}", "sizes": "S, M, L"}
            for i in range(5)
        ]
        client = self._make_mock_client(records)
        coro = search(client, "cfg", "id", "description", "embedding", False, {"query": "drink"})
        result, elapsed = _timed_async(coro)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"search formatting (5 results) took {elapsed:.2f} ms")
        self.assertIn("[0]", result.text)

    def test_search_formatting_empty_results(self):
        from tools import search

        client = self._make_mock_client([])
        coro = search(client, "cfg", "id", "description", "embedding", False, {"query": "xyz"})
        result, elapsed = _timed_async(coro)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"search formatting (0 results) took {elapsed:.2f} ms")


class WebSocketSerializationLatencyTests(unittest.TestCase):
    """WebSocket message serialization / deserialization must be fast."""

    THRESHOLD_MS = 2.0

    def _make_order_summary(self, n_items: int) -> dict:
        items = [
            {"item": f"Item {i}", "size": "Large", "quantity": i + 1,
             "price": 3.99 + i, "display": f"Large Item {i}"}
            for i in range(n_items)
        ]
        return {"items": items, "total": 99.99, "tax": 8.00, "finalTotal": 107.99}

    def test_serialize_order_summary(self):
        payload = self._make_order_summary(10)
        _, elapsed = _timed(json.dumps, payload)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"JSON serialize took {elapsed:.2f} ms")

    def test_deserialize_order_summary(self):
        payload_str = json.dumps(self._make_order_summary(10))
        _, elapsed = _timed(json.loads, payload_str)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"JSON deserialize took {elapsed:.2f} ms")

    def test_tool_result_to_text_speed(self):
        tr = ToolResult(json.dumps(self._make_order_summary(10)), ToolResultDirection.TO_BOTH)
        _, elapsed = _timed(tr.to_text)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"ToolResult.to_text took {elapsed:.2f} ms")

    def test_round_trip_websocket_message(self):
        """Simulates the full serialize → deserialize path for a WS message."""
        msg = {
            "type": "extension.middle_tier_tool_response",
            "tool_name": "update_order",
            "tool_result": json.dumps(self._make_order_summary(10)),
        }
        _, elapsed = _timed(lambda: json.loads(json.dumps(msg)))
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"WS message round-trip took {elapsed:.2f} ms")


# ===================================================================
# 2. Memory Efficiency Tests
# ===================================================================

class MemoryLeakTests(unittest.TestCase):
    """Repeated add/remove cycles must not leak memory."""

    def setUp(self):
        order_state_singleton.sessions = {}

    def test_add_remove_cycle_no_leak(self):
        """Add and remove 500 items — memory delta must stay under 1 MB."""
        gc.collect()
        tracemalloc.start()
        snapshot_before = tracemalloc.take_snapshot()

        sid = order_state_singleton.create_session()
        for i in range(500):
            order_state_singleton.handle_order_update(sid, "add", f"Item {i}", "Large", 1, 4.99)
        for i in range(500):
            order_state_singleton.handle_order_update(sid, "remove", f"Item {i}", "Large", 1, 4.99)

        gc.collect()
        snapshot_after = tracemalloc.take_snapshot()
        tracemalloc.stop()

        stats = snapshot_after.compare_to(snapshot_before, "lineno")
        total_diff_bytes = sum(s.size_diff for s in stats if s.size_diff > 0)
        total_diff_mb = total_diff_bytes / (1024 * 1024)

        self.assertLess(total_diff_mb, 1.0,
                        f"Memory grew by {total_diff_mb:.2f} MB after add/remove cycle")

    def test_session_create_delete_no_leak(self):
        """Create and delete 200 sessions — sessions dict must be empty."""
        for _ in range(200):
            sid = order_state_singleton.create_session()
            order_state_singleton.handle_order_update(sid, "add", "Slush", "Medium", 1, 2.99)
            order_state_singleton.delete_session(sid)

        self.assertEqual(len(order_state_singleton.sessions), 0,
                         "Sessions dict not empty after create/delete cycles")


class LargeOrderMemoryTests(unittest.TestCase):
    """Large orders must not cause excessive memory allocation."""

    def setUp(self):
        order_state_singleton.sessions = {}

    def test_large_order_memory_bounded(self):
        """An order with 100 unique items stays under 2 MB."""
        gc.collect()
        tracemalloc.start()

        sid = order_state_singleton.create_session()
        for i in range(100):
            order_state_singleton.handle_order_update(sid, "add", f"MenuItem {i}", "Large", 5, 9.99)

        current, peak = tracemalloc.get_traced_memory()
        tracemalloc.stop()
        peak_mb = peak / (1024 * 1024)

        self.assertLess(peak_mb, 2.0,
                        f"Peak memory for 100-item order was {peak_mb:.2f} MB")

    def test_order_summary_serialization_size(self):
        """JSON-serialized summary for 50 items stays under 50 KB."""
        sid = order_state_singleton.create_session()
        for i in range(50):
            order_state_singleton.handle_order_update(sid, "add", f"MenuItem {i}", "Medium", 2, 7.49)

        summary = order_state_singleton.get_order_summary(sid)
        payload = summary.model_dump_json()
        payload_kb = len(payload.encode("utf-8")) / 1024

        self.assertLess(payload_kb, 50.0,
                        f"Serialized summary was {payload_kb:.1f} KB")


# ===================================================================
# 3. Concurrency Tests
# ===================================================================

class ThreadSafetyTests(unittest.TestCase):
    """OrderState must be safe under concurrent access from multiple threads."""

    def setUp(self):
        order_state_singleton.sessions = {}

    def test_concurrent_add_operations(self):
        """10 threads each adding 50 items must produce 500 total items."""
        sid = order_state_singleton.create_session()

        def add_items(thread_id: int):
            for i in range(50):
                order_state_singleton.handle_order_update(
                    sid, "add", f"T{thread_id}-Item{i}", "Medium", 1, 1.99,
                )

        with concurrent.futures.ThreadPoolExecutor(max_workers=10) as pool:
            futures = [pool.submit(add_items, tid) for tid in range(10)]
            concurrent.futures.wait(futures)
            for f in futures:
                f.result()  # re-raise exceptions

        summary = order_state_singleton.get_order_summary(sid)
        self.assertEqual(len(summary.items), 500,
                         f"Expected 500 items, got {len(summary.items)}")

    def test_concurrent_session_create_delete(self):
        """Concurrent session lifecycle operations must not raise."""
        errors = []

        def lifecycle(n: int):
            try:
                for _ in range(20):
                    sid = order_state_singleton.create_session()
                    order_state_singleton.handle_order_update(sid, "add", f"Item-{n}", "Small", 1, 1.0)
                    order_state_singleton.get_order_summary(sid)
                    order_state_singleton.delete_session(sid)
            except Exception as exc:
                errors.append(exc)

        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            futures = [pool.submit(lifecycle, i) for i in range(8)]
            concurrent.futures.wait(futures)

        self.assertEqual(len(errors), 0, f"Concurrency errors: {errors}")


class WebSocketSessionIsolationTests(unittest.TestCase):
    """Multiple simultaneous WebSocket sessions must not interfere."""

    def setUp(self):
        order_state_singleton.sessions = {}

    def test_parallel_sessions_isolated(self):
        """Items added in one session must not appear in another."""
        sessions = [order_state_singleton.create_session() for _ in range(5)]

        for idx, sid in enumerate(sessions):
            order_state_singleton.handle_order_update(
                sid, "add", f"UniqueItem-{idx}", "Large", 1, 5.0 + idx,
            )

        for idx, sid in enumerate(sessions):
            summary = order_state_singleton.get_order_summary(sid)
            self.assertEqual(len(summary.items), 1)
            self.assertEqual(summary.items[0].item, f"UniqueItem-{idx}")

    def test_delete_one_session_preserves_others(self):
        s1 = order_state_singleton.create_session()
        s2 = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(s1, "add", "A", "M", 1, 1.0)
        order_state_singleton.handle_order_update(s2, "add", "B", "M", 1, 2.0)

        order_state_singleton.delete_session(s1)

        summary2 = order_state_singleton.get_order_summary(s2)
        self.assertEqual(len(summary2.items), 1)
        self.assertEqual(summary2.items[0].item, "B")
        self.assertNotIn(s1, order_state_singleton.sessions)


# ===================================================================
# 4. Production Readiness Checks
# ===================================================================

class AppStartupTests(unittest.IsolatedAsyncioTestCase):
    """The app must start without errors in production mode."""

    async def test_create_app_succeeds_in_production_mode(self):
        with patch("app.RTMiddleTier") as mock_rt, \
             patch("app.attach_tools_rtmt"), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-4o-realtime",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "menu-index",
             }):
            mock_instance = MagicMock()
            mock_rt.return_value = mock_instance
            from app import create_app
            app = await create_app()
            self.assertIsNotNone(app)

    async def test_create_app_raises_without_required_env(self):
        with patch.dict(os.environ, {
            "RUNNING_IN_PRODUCTION": "1",
        }, clear=True):
            from app import create_app
            with self.assertRaises(RuntimeError):
                await create_app()


class StaticFileTests(unittest.TestCase):
    """Static files must be present and serve-ready."""

    def test_static_index_html_exists(self):
        index = Path(__file__).resolve().parents[1] / "static" / "index.html"
        self.assertTrue(index.exists(), "static/index.html is missing")

    def test_static_index_html_has_content(self):
        index = Path(__file__).resolve().parents[1] / "static" / "index.html"
        if index.exists():
            content = index.read_text(encoding="utf-8")
            self.assertGreater(len(content), 100,
                               "index.html appears to be a stub")
            self.assertIn("<html", content.lower())


class HealthEndpointTests(unittest.IsolatedAsyncioTestCase):
    """Root endpoint must respond quickly."""

    async def test_root_serves_index_html(self):
        with patch("app.RTMiddleTier") as mock_rt, \
             patch("app.attach_tools_rtmt"), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-4o-realtime",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "menu-index",
             }):
            mock_rt.return_value = MagicMock()
            from app import create_app
            from aiohttp.test_utils import TestClient, TestServer

            app = await create_app()
            async with TestClient(TestServer(app)) as client:
                start = time.perf_counter()
                resp = await client.get("/")
                elapsed_ms = (time.perf_counter() - start) * 1000
                self.assertEqual(resp.status, 200)
                self.assertLess(elapsed_ms, 500,
                                f"Root endpoint took {elapsed_ms:.0f} ms")


class CorsConfigTests(unittest.IsolatedAsyncioTestCase):
    """Validate CORS is not accidentally wide-open or missing entirely."""

    async def test_app_does_not_add_wildcard_cors(self):
        """Verify the app doesn't set Access-Control-Allow-Origin: *."""
        with patch("app.RTMiddleTier") as mock_rt, \
             patch("app.attach_tools_rtmt"), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-4o-realtime",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "menu-index",
             }):
            mock_rt.return_value = MagicMock()
            from app import create_app
            from aiohttp.test_utils import TestClient, TestServer

            app = await create_app()
            async with TestClient(TestServer(app)) as client:
                resp = await client.get("/")
                cors_header = resp.headers.get("Access-Control-Allow-Origin", "")
                self.assertNotEqual(cors_header, "*",
                                    "Wildcard CORS should not be set in production")


# ===================================================================
# 5. Pydantic Model Serialization Performance
# ===================================================================

class ModelSerializationPerfTests(unittest.TestCase):
    """Pydantic model serialization must stay fast under load."""

    THRESHOLD_MS = 5.0

    def test_order_item_creation_speed(self):
        """Creating 1000 OrderItem instances must be fast."""
        start = time.perf_counter()
        for i in range(1000):
            OrderItem(item=f"Item {i}", size="Large", quantity=i, price=4.99, display=f"Large Item {i}")
        elapsed_ms = (time.perf_counter() - start) * 1000
        self.assertLess(elapsed_ms, 50.0,
                        f"1000 OrderItem creations took {elapsed_ms:.1f} ms")

    def test_order_summary_dump_json_speed(self):
        """model_dump_json on a 20-item summary must be under threshold."""
        items = [
            OrderItem(item=f"Item {i}", size="Medium", quantity=2, price=3.99, display=f"Medium Item {i}")
            for i in range(20)
        ]
        summary = OrderSummary(items=items, total=79.80, tax=6.38, finalTotal=86.18)
        _, elapsed = _timed(summary.model_dump_json)
        self.assertLess(elapsed, self.THRESHOLD_MS,
                        f"model_dump_json took {elapsed:.2f} ms")


if __name__ == "__main__":
    unittest.main()
