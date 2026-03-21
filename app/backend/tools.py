import json
import logging
import os
import time
from pathlib import Path
from typing import Any

from azure.core.credentials import AzureKeyCredential
from azure.core.exceptions import HttpResponseError
from azure.identity import DefaultAzureCredential
from azure.search.documents.aio import SearchClient
from azure.search.documents.models import VectorizableTextQuery

from order_state import order_state_singleton
from rtmt import RTMiddleTier, Tool, ToolResult, ToolResultDirection

logger = logging.getLogger(__name__)

__all__ = ["attach_tools_rtmt"]

# ---------------------------------------------------------------------------
# Search result cache — avoids redundant Azure AI Search round-trips for
# repeated menu queries within a short window (e.g. "what sizes do you have?").
# ---------------------------------------------------------------------------
_SEARCH_CACHE_TTL_SEC = 60.0
_SEARCH_CACHE_MAX_SIZE = 128

class _SearchCache:
    """Simple TTL cache for search results. Not thread-safe, but fine for
    single-threaded asyncio where all access is from the event loop."""
    __slots__ = ("_store", "_max_size")

    def __init__(self, max_size: int = _SEARCH_CACHE_MAX_SIZE):
        self._store: dict[str, tuple[float, ToolResult]] = {}
        self._max_size = max_size

    def get(self, key: str) -> ToolResult | None:
        entry = self._store.get(key)
        if entry is None:
            return None
        ts, result = entry
        if time.monotonic() - ts > _SEARCH_CACHE_TTL_SEC:
            del self._store[key]
            return None
        return result

    def put(self, key: str, result: ToolResult) -> None:
        if len(self._store) >= self._max_size:
            oldest_key = min(self._store, key=lambda k: self._store[k][0])
            del self._store[oldest_key]
        self._store[key] = (time.monotonic(), result)

    def clear(self) -> None:
        self._store.clear()

_search_cache = _SearchCache()


# ---------------------------------------------------------------------------
# Order quantity limits — prevents abuse (e.g. ordering 100 burgers) while
# staying realistic for a drive-thru window.
# ---------------------------------------------------------------------------
MAX_QUANTITY_PER_ITEM = 10   # max of any single item (name+size combo)
MAX_TOTAL_ITEMS = 25         # max total items across the entire order


# Extras may only be applied to specific beverage categories.
EXTRAS_KEYWORDS = (
    "flavor add-in",
    "whipped cream",
    "extra patty",
    "extra cheese",
    "add bacon",
)
ALLOWED_EXTRA_CATEGORIES = {"slushes & drinks", "shakes & ice cream", "burgers & sandwiches", "drinks", "slushes", "shakes", "combos"}
BLOCKED_EXTRA_CATEGORIES = {"hot dogs & tots", "sides", "hot dogs"}


def _load_menu_category_map() -> dict[str, str]:
    env_override = (
        os.environ.get("SONIC_MENU_ITEMS_PATH")
        or os.environ.get("MENU_ITEMS_PATH")
    )

    candidate_paths = []
    if env_override:
        candidate_paths.append(Path(env_override))

    # Preferred: keep backend self-contained (Docker image can copy this in).
    candidate_paths.append(Path(__file__).resolve().parent / "data" / "menuItems.json")

    # Fallback: repo layout (local dev).
    candidate_paths.append(Path(__file__).resolve().parent.parent / "frontend" / "src" / "data" / "menuItems.json")

    menu_path = next((path for path in candidate_paths if path.exists()), None)
    if menu_path is None:
        return {}
    try:
        with menu_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        mapping = {}
        for category_entry in data.get("menuItems", []):
            category = category_entry.get("category", "").strip().lower()
            for item in category_entry.get("items", []):
                name = item.get("name")
                if name:
                    mapping[name.lower()] = category
        return mapping
    except Exception as exc:  # pragma: no cover - defensive fallback
        logger.warning("Failed to load menu items; falling back to keyword category inference: %s", exc)
        return {}


MENU_CATEGORY_MAP = _load_menu_category_map()


def _is_extra_item(item_name: str) -> bool:
    normalized = item_name.lower()
    return any(keyword in normalized for keyword in EXTRAS_KEYWORDS)


def _infer_category(item_name: str) -> str:
    normalized = item_name.lower()
    if normalized in MENU_CATEGORY_MAP:
        return MENU_CATEGORY_MAP[normalized]
    if "slush" in normalized or "limeade" in normalized or "ocean water" in normalized:
        return "slushes"
    if "shake" in normalized or "blast" in normalized or "malt" in normalized:
        return "shakes"
    if "burger" in normalized or "combo" in normalized:
        return "combos"
    if "hot dog" in normalized or "coney" in normalized:
        return "hot dogs"
    if "tot" in normalized or "fries" in normalized or "onion rings" in normalized:
        return "sides"
    if "drink" in normalized or "tea" in normalized or "lemonade" in normalized:
        return "drinks"
    return ""



search_tool_schema = {
    "type": "function",
    "name": "search",
    "description": "Search the knowledge base. The knowledge base is in English, translate to and from English if " + \
                   "needed. Results are formatted as a source name first in square brackets, followed by the text " + \
                   "content, and a line with '-----' at the end of each result.",
    "parameters": {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "Search query"
            }
        },
        "required": ["query"],
        "additionalProperties": False
    }
}

async def search(
    search_client: SearchClient,
    semantic_configuration: str,
    identifier_field: str,
    content_field: str,
    embedding_field: str,
    use_vector_query: bool,
    args: Any,
) -> ToolResult:
    """Execute a hybrid Azure AI Search query with caching and safe fallbacks."""

    query = args["query"]
    logger.info("Knowledge search requested for query '%s'", query)

    # Check cache first — repeated questions about the same menu item are common
    cache_key = query.strip().lower()
    cached = _search_cache.get(cache_key)
    if cached is not None:
        logger.debug("Search cache hit for '%s'", query)
        return cached

    vector_queries = []
    if use_vector_query and embedding_field:
        vector_queries.append(VectorizableTextQuery(text=query, k_nearest_neighbors=15, fields=embedding_field))

    # Only request fields we actually format into the result string
    select_fields = [
        identifier_field or "id",
        "name",
        "category",
        "description",
        "sizes",
    ]

    try:
        search_results = await search_client.search(
            search_text=query,
            query_type="semantic",
            semantic_configuration_name=semantic_configuration,
            top=3,
            vector_queries=vector_queries or None,
            select=select_fields,
        )
    except HttpResponseError as exc:
        # Gracefully handle schema/field mismatches (e.g., invalid $select fields) by retrying with a minimal projection.
        if "Could not find a property named" in str(exc):
            logger.warning("Retrying search with minimal fields after select mismatch: %s", exc)
            fallback_select = [identifier_field or "id", content_field or "description"]
            search_results = await search_client.search(
                search_text=query,
                query_type="semantic",
                semantic_configuration_name=semantic_configuration,
                top=3,
                vector_queries=vector_queries or None,
                select=[f for f in fallback_select if f],
            )
        else:
            logger.error("Azure AI Search request failed: %s", exc)
            return ToolResult("I'm sorry, I can't reach our menu data right now.", ToolResultDirection.TO_SERVER)

    results = []
    async for record in search_results:
        identifier = record.get(identifier_field) or record.get("id", "unknown")
        summary = (
            f"[{identifier}]: "
            f"Name: {record.get('name', 'N/A')}, Category: {record.get('category', 'N/A')}, "
            f"Description: {record.get('description', 'N/A')}, Sizes: {record.get('sizes', 'N/A')}"
        )
        results.append(summary)

    joined_results = "\n-----\n".join(results)
    logger.debug("Search results returned %d documents", len(results))
    result = ToolResult(joined_results or "No matching menu entries found.", ToolResultDirection.TO_SERVER)

    # Cache the result for repeated queries
    _search_cache.put(cache_key, result)
    return result


update_order_tool_schema = {
    "type": "function",
    "name": "update_order",
    "description": "Update the current order by adding or removing items.",
    "parameters": {
        "type": "object",
        "properties": {
            "action": { 
                "type": "string", 
                "description": "Action to perform: 'add' or 'remove'.", 
                "enum": ["add", "remove"]
            },
            "item_name": { 
                "type": "string", 
                "description": "Name of the item to update, e.g., 'Cherry Limeade'."
            },
            "size": { 
                "type": "string", 
                "description": "Size of the item to update, e.g., 'Large'."
            },
            "quantity": { 
                "type": "integer", 
                "description": "Quantity of the item to update. Represents the number of items."
            },
            "price": { 
                "type": "number", 
                "description": "Price of a single item to add. Required only for 'add' action. Note: This is the price per individual item, not the total price for the quantity."
            }
        },
        "required": ["action", "item_name", "size", "quantity"],
        "additionalProperties": False
    }
}

async def update_order(args, session_id: str) -> ToolResult:
    """Update the current order by adding or removing items."""

    logger.info("Updating order for session %s with payload %s", session_id, args)

    item_name = args["item_name"]
    if args["action"] == "add" and _is_extra_item(item_name):
        current_items = order_state_singleton.get_order_items(session_id)
        has_allowed_base = False
        has_blocked_base = False

        for order_item in current_items:
            category = _infer_category(order_item.item)
            if category in ALLOWED_EXTRA_CATEGORIES:
                has_allowed_base = True
            if category in BLOCKED_EXTRA_CATEGORIES:
                has_blocked_base = True

        if not has_allowed_base:
            apology = (
                "I can add extras to drinks, slushes, shakes, or combos, "
                "but not to sides or hot dogs on their own."
            )
            if has_blocked_base:
                apology = (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but I can't add them to sides or hot dogs on their own."
                )
            logger.info("Blocked extra '%s' for session %s", item_name, session_id)
            return ToolResult(apology, ToolResultDirection.TO_SERVER)

    # ── Quantity limit validation (add only) ──
    quantity = args.get("quantity", 0)
    size = args["size"]
    if args["action"] == "add":
        current_items = order_state_singleton.get_order_items(session_id)

        # Per-item limit: check resulting quantity for this item+size combo
        existing_qty = 0
        for order_item in current_items:
            if order_item.item == item_name and order_item.size == size:
                existing_qty = order_item.quantity
                break
        new_item_qty = existing_qty + quantity
        if new_item_qty > MAX_QUANTITY_PER_ITEM:
            allowed = MAX_QUANTITY_PER_ITEM - existing_qty
            if allowed <= 0:
                msg = (
                    f"That's a lot of {item_name}! Our drive-thru can handle up to "
                    f"{MAX_QUANTITY_PER_ITEM} of any item. You already have {existing_qty} — "
                    f"would you like to keep it at {existing_qty}?"
                )
            else:
                msg = (
                    f"That's a lot of {item_name}! Our drive-thru can handle up to "
                    f"{MAX_QUANTITY_PER_ITEM} of any item. I can add {allowed} more — "
                    f"would you like me to do that?"
                )
            logger.info("Per-item limit hit for '%s' in session %s (requested %d, existing %d)",
                        item_name, session_id, quantity, existing_qty)
            return ToolResult(msg, ToolResultDirection.TO_SERVER)

        # Total order limit: check total items across the whole order
        total_qty = sum(oi.quantity for oi in current_items) + quantity
        if total_qty > MAX_TOTAL_ITEMS:
            remaining = MAX_TOTAL_ITEMS - sum(oi.quantity for oi in current_items)
            if remaining <= 0:
                msg = (
                    f"Wow, that's a big order! Our drive-thru tops out at "
                    f"{MAX_TOTAL_ITEMS} items total so we can keep things moving. "
                    f"You're already at the max — would you like to swap anything out?"
                )
            else:
                msg = (
                    f"Wow, that's a big order! Our drive-thru tops out at "
                    f"{MAX_TOTAL_ITEMS} items total so we can keep things moving. "
                    f"I can add {remaining} more — would you like me to do that?"
                )
            logger.info("Total order limit hit in session %s (would be %d items)", session_id, total_qty)
            return ToolResult(msg, ToolResultDirection.TO_SERVER)

    order_state_singleton.handle_order_update(
        session_id,
        args["action"],
        item_name,
        size,
        quantity,
        args.get("price", 0.0),
    )

    json_order_summary = order_state_singleton.get_order_summary_json(session_id)
    logger.debug("Session %s order summary after update: %s", session_id, json_order_summary)

    return ToolResult(json_order_summary, ToolResultDirection.TO_BOTH)


get_order_tool_schema = {
    "type": "function",
    "name": "get_order",
    "description": "Retrieve the current order summary.",
    "parameters": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": False
    }
}

async def get_order(_args: Any, session_id: str) -> ToolResult:
    """Retrieve the current order summary."""

    logger.info("Retrieving order summary for session %s", session_id)
    return ToolResult(order_state_singleton.get_order_summary_json(session_id), ToolResultDirection.TO_SERVER)


def attach_tools_rtmt(
    rtmt: RTMiddleTier,
    credentials: AzureKeyCredential | DefaultAzureCredential,
    search_endpoint: str,
    search_index: str,
    semantic_configuration: str,
    identifier_field: str,
    content_field: str,
    embedding_field: str,
    title_field: str,
    use_vector_query: bool,
) -> None:
    """Attach search and order tools to the RTMiddleTier instance."""

    if not isinstance(credentials, AzureKeyCredential):
        credentials.get_token("https://search.azure.com/.default")  # warm up prior to first call
    search_client = SearchClient(search_endpoint, search_index, credentials, user_agent="RTMiddleTier")

    rtmt.tools["search"] = Tool(schema=search_tool_schema, target=lambda args: search(search_client, semantic_configuration, identifier_field, content_field, embedding_field, use_vector_query, args))
    rtmt.tools["update_order"] = Tool(schema=update_order_tool_schema, target=lambda args, session_id: update_order(args, session_id))
    rtmt.tools["get_order"] = Tool(schema=get_order_tool_schema, target=lambda args, session_id: get_order(args, session_id))


