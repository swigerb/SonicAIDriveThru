"""Shared menu utilities — canonical size mappings and category inference.

Both ``tools.py`` and ``order_state.py`` need size normalisation and category
inference.  Keeping a single source of truth here avoids silent drift.
"""

from __future__ import annotations

import json
import logging
import os
import re
from pathlib import Path

__all__ = [
    "SIZE_MAP",
    "SIZE_ALIASES",
    "normalize_size",
    "canonical_size_key",
    "infer_category",
    "infer_combo_component",
    "MENU_CATEGORY_MAP",
]

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Canonical size map  (display-ready values)
# ---------------------------------------------------------------------------
SIZE_MAP: dict[str, str] = {
    "mini": "Mini",
    "small": "Small",
    "medium": "Medium",
    "large": "Large",
    "xl": "Extra Large",
    "route 44": "Route 44",
    "standard": "Standard",
}

# Aliases that normalise to a canonical key above
SIZE_ALIASES: dict[str, str] = {
    "s": "small",
    "m": "medium",
    "l": "large",
    "rt 44": "route 44",
    "rt44": "route 44",
    "44": "route 44",
    "44oz": "route 44",
    "route44": "route 44",
}

# Sizes that should be hidden in display strings (no prefix)
_NO_DISPLAY_SIZES = frozenset({"", "standard", "n/a", "na", "none", "n.a."})


def normalize_size(size: str) -> str:
    """Return a human-readable size string, or ``""`` for hidden/standard sizes.

    >>> normalize_size("rt44")
    'Route 44'
    >>> normalize_size("m")
    'Medium'
    >>> normalize_size("n/a")
    ''
    """
    key = (size or "").strip().lower()
    if key in _NO_DISPLAY_SIZES:
        return ""
    # Resolve aliases first
    canonical = SIZE_ALIASES.get(key, key)
    return SIZE_MAP.get(canonical, "")


def canonical_size_key(size: str) -> str:
    """Return the canonical, alias-resolved key used to match/merge/remove order lines (#40).

    All spellings of the same physical size must collapse to one key *before* any order-state
    matching happens, so e.g. ``"rt44"``, ``"route44"``, ``"44 oz"``, ``"RT 44"`` and
    ``"Route 44"`` are all treated as the same line item. This mirrors the alias resolution
    ``normalize_size`` already does for its display string, but returns the lookup key itself
    (not a human-readable label) and is case/whitespace-normalised even for sizes with no known
    alias, so callers get consistent matching regardless of input casing.

    >>> canonical_size_key("rt44")
    'route 44'
    >>> canonical_size_key("Route44")
    'route 44'
    >>> canonical_size_key("44 oz")
    'route 44'
    >>> canonical_size_key(" Medium ")
    'medium'
    """
    key = (size or "").strip().lower()
    # Collapse internal whitespace so "44 oz" and "44oz" resolve the same way as a plain "44".
    compact_key = "".join(key.split())
    if compact_key in SIZE_ALIASES:
        return SIZE_ALIASES[compact_key]
    return SIZE_ALIASES.get(key, key)


# ---------------------------------------------------------------------------
# Menu category map (loaded once from menuItems.json)
# ---------------------------------------------------------------------------
def _load_menu_category_map() -> dict[str, str]:
    env_override = (
        os.environ.get("SONIC_MENU_ITEMS_PATH")
        or os.environ.get("MENU_ITEMS_PATH")
    )

    candidate_paths: list[Path] = []
    if env_override:
        candidate_paths.append(Path(env_override))

    candidate_paths.append(Path(__file__).resolve().parent / "data" / "menuItems.json")
    candidate_paths.append(Path(__file__).resolve().parent.parent / "frontend" / "src" / "data" / "menuItems.json")

    menu_path = next((path for path in candidate_paths if path.exists()), None)
    if menu_path is None:
        return {}
    try:
        with menu_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        mapping: dict[str, str] = {}
        for category_entry in data.get("menuItems", []):
            category = category_entry.get("category", "").strip().lower()
            for item in category_entry.get("items", []):
                name = item.get("name")
                if name:
                    mapping[name.lower()] = category
        return mapping
    except Exception as exc:  # pragma: no cover
        logger.warning("Failed to load menu items; falling back to keyword inference: %s", exc)
        return {}


MENU_CATEGORY_MAP: dict[str, str] = _load_menu_category_map()


def infer_category(item_name: str) -> str:
    """Return the menu category for *item_name* (keyword fallback if not in the JSON map)."""
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
