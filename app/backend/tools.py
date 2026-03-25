import json
import logging
import os
import time
from typing import Any

from azure.core.credentials import AzureKeyCredential
from azure.core.exceptions import HttpResponseError
from azure.identity import DefaultAzureCredential
from azure.search.documents.aio import SearchClient
from azure.search.documents.models import VectorizableTextQuery

from config_loader import get_config
from order_state import order_state_singleton, is_happy_hour
from menu_utils import infer_category as _infer_category, normalize_size, MENU_CATEGORY_MAP
from rtmt import RTMiddleTier, Tool, ToolResult, ToolResultDirection

logger = logging.getLogger(__name__)

__all__ = ["attach_tools_rtmt"]

# Load centralized config
_config = get_config()
_cache_cfg = _config.get("cache", {})
_search_cfg = _config.get("search", {})
_biz_cfg = _config.get("business_rules", {})

# Module-level prompt loader — set by attach_tools_rtmt() at startup
_prompt_loader = None

# ---------------------------------------------------------------------------
# Search result cache — avoids redundant Azure AI Search round-trips for
# repeated menu queries within a short window (e.g. "what sizes do you have?").
# ---------------------------------------------------------------------------
_SEARCH_CACHE_TTL_SEC = _cache_cfg.get("search_ttl_seconds", 60.0)
_SEARCH_CACHE_MAX_SIZE = _cache_cfg.get("search_max_size", 128)

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
MAX_QUANTITY_PER_ITEM = _biz_cfg.get("max_item_quantity", 10)
MAX_TOTAL_ITEMS = _biz_cfg.get("max_order_items", 25)


# ---------------------------------------------------------------------------
# Mock "Store Telemetry" - In production, this would be an Azure Function / IoT Hub call
# ---------------------------------------------------------------------------
MOCK_MACHINE_STATUS = {
    "ice_cream_machine": "down",  # Classic "shake machine is broken" scenario
    "slush_machine": "operational",
    "fryer": "operational",
}

# OOS keywords — items that depend on the ice cream machine
_ICE_CREAM_MACHINE_KEYWORDS = ("shake", "blast", "sundae", "ice cream")


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


def _is_extra_item(item_name: str) -> bool:
    normalized = item_name.lower()
    return any(keyword in normalized for keyword in EXTRAS_KEYWORDS)


def validate_customization(item_name: str, mods_string: str) -> str | None:
    """Return an error message if the mods are nonsensical for the item category, else None."""
    base_name = item_name.split("(")[0].strip()
    category = _infer_category(base_name)
    mods_lower = mods_string.lower()
    for cat_key, forbidden_list in INVALID_MODS.items():
        if cat_key in category.lower():
            for forbidden in forbidden_list:
                if forbidden in mods_lower:
                    if _prompt_loader:
                        return _prompt_loader.render_error("invalid_mod", forbidden_item=forbidden, base_name=base_name)
                    return f"I can't add {forbidden} to a {base_name} — that's a new one! Want to try a different topping?"
    return None


def _format_size_human_readable(size: str) -> str:
    """Convert size codes to human-readable format using shared size map."""
    result = normalize_size(size)
    return result if result else size.capitalize()



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
        vector_queries.append(VectorizableTextQuery(text=query, k_nearest_neighbors=_search_cfg.get("k_nearest_neighbors", 15), fields=embedding_field))

    # Only request fields we actually format into the result string
    select_fields = [
        identifier_field or "id",
        "name",
        "category",
        "description",
        "sizes",
    ]

    _top = _search_cfg.get("top_results", 3)

    try:
        search_results = await search_client.search(
            search_text=query,
            query_type="semantic",
            semantic_configuration_name=semantic_configuration,
            top=_top,
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
                top=_top,
                vector_queries=vector_queries or None,
                select=[f for f in fallback_select if f],
            )
        else:
            logger.error("Azure AI Search request failed: %s", exc)
            _err = _prompt_loader.render_error("search_service_unavailable") if _prompt_loader else "I'm sorry, I can't reach our menu data right now."
            return ToolResult(_err, ToolResultDirection.TO_SERVER)

    results = []
    async for record in search_results:
        identifier = record.get(identifier_field) or record.get("id", "unknown")

        # Format sizes into human-readable list so the Realtime API can speak them naturally
        raw_sizes = record.get('sizes', 'N/A')
        try:
            sizes_json = json.loads(raw_sizes)
            size_str = ", ".join([f"{_format_size_human_readable(s['size'])} (${s['price']})" for s in sizes_json])
        except Exception:
            size_str = raw_sizes

        item_name = record.get('name', 'N/A')
        summary = (
            f"[{identifier}]: "
            f"Item: {item_name}, Category: {record.get('category', 'N/A')}, "
            f"Available Sizes: {size_str}"
        )

        # Flag items affected by machine outages so the AI knows not to recommend them
        if MOCK_MACHINE_STATUS.get("ice_cream_machine") == "down":
            if any(kw in item_name.lower() for kw in _ICE_CREAM_MACHINE_KEYWORDS):
                summary += " [OOS: Ice cream machine is being cleaned]"

        results.append(summary)

    joined_results = "\n-----\n".join(results)
    logger.debug("Search results returned %d documents", len(results))
    _no_results = _prompt_loader.get_error_messages().get("search_no_results", "No matching menu entries found.") if _prompt_loader else "No matching menu entries found."
    result = ToolResult(joined_results or _no_results, ToolResultDirection.TO_SERVER)

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

    # ── Customization validation (reject nonsensical mods) ──
    if "(" in item_name:
        mods_content = item_name[item_name.find("(")+1:item_name.find(")")]
        error = validate_customization(item_name, mods_content)
        if error:
            return ToolResult(error, ToolResultDirection.TO_SERVER)

    # ── Hardened price validation (add only) ──
    price = args.get("price", 0.0)
    if args["action"] == "add" and price <= 0.0:
        logger.warning("Model attempted to add item %s with invalid price $%.2f (rejecting $0 items)", item_name, price)
        _err = _prompt_loader.render_error("price_validation_failed") if _prompt_loader else "I'm sorry, I had a glitch with the pricing for that. Could you say that again?"
        return ToolResult(_err, ToolResultDirection.TO_SERVER)

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
            if has_blocked_base:
                apology = _prompt_loader.render_error("extras_blocked_category") if _prompt_loader else (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but I can't add them to sides or hot dogs on their own."
                )
            else:
                apology = _prompt_loader.render_error("extras_no_base_item") if _prompt_loader else (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but not to sides or hot dogs on their own."
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
                if _prompt_loader:
                    msg = _prompt_loader.render_error("per_item_limit_maxed", item_name=item_name, max_per_item=MAX_QUANTITY_PER_ITEM, existing_qty=existing_qty)
                else:
                    msg = (
                        f"That's a lot of {item_name}! Our drive-thru can handle up to "
                        f"{MAX_QUANTITY_PER_ITEM} of any item. You already have {existing_qty} — "
                        f"would you like to keep it at {existing_qty}?"
                    )
            else:
                if _prompt_loader:
                    msg = _prompt_loader.render_error("per_item_limit_partial", item_name=item_name, max_per_item=MAX_QUANTITY_PER_ITEM, allowed=allowed)
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
                if _prompt_loader:
                    msg = _prompt_loader.render_error("total_order_limit_maxed", max_total=MAX_TOTAL_ITEMS)
                else:
                    msg = (
                        f"Wow, that's a big order! Our drive-thru tops out at "
                        f"{MAX_TOTAL_ITEMS} items total so we can keep things moving. "
                        f"You're already at the max — would you like to swap anything out?"
                    )
            else:
                if _prompt_loader:
                    msg = _prompt_loader.render_error("total_order_limit_partial", max_total=MAX_TOTAL_ITEMS, remaining=remaining)
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
    summary = order_state_singleton.get_order_summary(session_id)
    logger.debug("Session %s order summary after update: %s", session_id, json_order_summary)

    # ── Delta text for voice confirmation ──
    action = args["action"]
    display_size = size if size and size.lower() not in {"", "standard", "n/a", "na", "none", "n.a."} else ""
    display_name = f"{display_size.capitalize() + ' ' if display_size else ''}{item_name}"
    if _prompt_loader:
        tpl = _prompt_loader.get_delta_template(action)
        delta_text = _prompt_loader.render_template(tpl, quantity=quantity, display_name=display_name, total=f"{summary.finalTotal:.2f}")
    elif action == "add":
        delta_text = f"Added {quantity} {display_name} — your total is now ${summary.finalTotal:.2f}"
    else:
        delta_text = f"Removed {quantity} {display_name} — your total is now ${summary.finalTotal:.2f}"

    # ── Combo validation: flag missing components ──
    validation = order_state_singleton.get_combo_requirements(session_id)

    if not validation["is_complete"]:
        delta_text += f"\n\n[SYSTEM HINT: {validation['prompt_hint']}]"
        logger.info("Combo incomplete for session %s — missing: %s", session_id, validation["missing_items"])
    elif action == "add":
        # ── Category-aware upsell hints (only when combo requirements are met) ──
        category = _infer_category(item_name)
        if _prompt_loader:
            delta_text += _prompt_loader.get_upsell_hint(category)
        else:
            if category == "combos":
                delta_text += " (UPSELL HINT: Combos are a great base! Ask if they want to upgrade to a Large size, or add a delicious Shake or Dessert!)"
            elif category in ("burgers", "burgers & sandwiches"):
                delta_text += " (UPSELL HINT: Perfect choice! Ask if they want to make it a combo meal with Tots or Fries and a refreshing Drink!)"
            elif category in ("drinks", "slushes"):
                delta_text += " (UPSELL HINT: Great drink choice! Ask if they want to add a Flavor Add-In to customize it, or pair it with a tasty side!)"
            elif category in ("shakes", "desserts", "shakes & ice cream"):
                delta_text += " (UPSELL HINT: Yum! Shakes are perfect on their own, but ask if they'd like to add Whipped Cream or pair with a snack!)"
            elif category in ("sides", "hot dogs", "hot dogs & tots"):
                delta_text += " (UPSELL HINT: Tasty! Ask if they want to add a refreshing Drink or Slush to complete their meal!)"
            else:
                delta_text += " (UPSELL HINT: Ask if they'd like to add anything else — maybe a drink, side, or dessert!)"
        logger.debug("Upsell hint for category '%s'", category)

    happy_hour_note = " [HAPPY HOUR ACTIVE: drinks and slushes are half-price!]" if is_happy_hour() else ""
    return ToolResult(delta_text + happy_hour_note, ToolResultDirection.TO_BOTH, client_text=json_order_summary)


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
    readback = order_state_singleton.get_grouped_order_for_readback(session_id)
    json_summary = order_state_singleton.get_order_summary_json(session_id)
    happy_hour_note = " [HAPPY HOUR ACTIVE: drinks and slushes are half-price!]" if is_happy_hour() else ""
    return ToolResult(readback + happy_hour_note, ToolResultDirection.TO_BOTH, client_text=json_summary)


reset_order_tool_schema = {
    "type": "function",
    "name": "reset_order",
    "description": "Clear all items from the current order and start fresh.",
    "parameters": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": False
    }
}

async def reset_order(_args: Any, session_id: str) -> ToolResult:
    """Clear the entire order ticket."""
    logger.info("Resetting entire order for session %s", session_id)
    order_state_singleton.reset_order(session_id)
    json_summary = order_state_singleton.get_order_summary_json(session_id)
    return ToolResult(f"Order cleared. {json_summary}", ToolResultDirection.TO_BOTH, client_text=json_summary)


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
    prompt_loader=None,
) -> None:
    """Attach search and order tools to the RTMiddleTier instance."""
    global _prompt_loader
    _prompt_loader = prompt_loader

    # Use tool schemas from YAML if available, else fall back to hardcoded
    if prompt_loader:
        yaml_schemas = prompt_loader.get_tool_schemas()
        schema_map = {s["name"]: s for s in yaml_schemas}
    else:
        schema_map = {}

    if not isinstance(credentials, AzureKeyCredential):
        credentials.get_token("https://search.azure.com/.default")  # warm up prior to first call
    search_client = SearchClient(search_endpoint, search_index, credentials, user_agent="RTMiddleTier")

    rtmt.tools["search"] = Tool(schema=schema_map.get("search", search_tool_schema), target=lambda args: search(search_client, semantic_configuration, identifier_field, content_field, embedding_field, use_vector_query, args))
    rtmt.tools["update_order"] = Tool(schema=schema_map.get("update_order", update_order_tool_schema), target=lambda args, session_id: update_order(args, session_id))
    rtmt.tools["get_order"] = Tool(schema=schema_map.get("get_order", get_order_tool_schema), target=lambda args, session_id: get_order(args, session_id))
    rtmt.tools["reset_order"] = Tool(schema=schema_map.get("reset_order", reset_order_tool_schema), target=lambda args, session_id: reset_order(args, session_id))


