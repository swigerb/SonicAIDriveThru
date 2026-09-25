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


# ---------------------------------------------------------------------------
# Combo-component bucket classification (#39)
#
# Used for BOTH combo-slot-filling (which standalone items a combo can absorb into its side/
# drink slots) and happy-hour discount eligibility ("happy hour = drinks only"). Deliberately
# kept separate from ``infer_category`` above, whose raw category strings are relied on
# elsewhere (extras validation, upsell hints, search categorisation) and must not change.
# ---------------------------------------------------------------------------

# Items whose true bucket contradicts their raw JSON category, checked first: the four hot-dog
# entrees live in the "Hot Dogs & Tots" category alongside real sides (Tots, Onion Rings, Ched 'R'
# Peppers, ...), and the two sundaes live in "Shakes & Ice Cream" alongside real shakes/blasts.
# Per Brian's #39 decision, sundaes are full price during happy hour -- a sundae isn't a drink,
# so it must not be discounted or fill a combo's drink slot.
_BUCKET_EXCEPTIONS: dict[str, str] = {
    "all-american dog": "",
    "chili cheese coney": "",
    "footlong quarter pound coney": "",
    "corn dog": "",
    "hot fudge sundae": "",
    "caramel sundae": "",
}

# Raw JSON category string (already lower-cased by MENU_CATEGORY_MAP) -> combo-slot/happy-hour
# bucket. Anything not listed here (Burgers & Sandwiches, Combos, or an unmapped category)
# defaults to "" -- not a fillable side/drink slot and not happy-hour-discountable.
_CATEGORY_BUCKET: dict[str, str] = {
    "hot dogs & tots": "sides",
    "extras & sides": "sides",
    "slushes & drinks": "drinks",
    "shakes & ice cream": "drinks",
}

# Word-boundary so a side item merely *containing* the substring "pepper" (e.g. "Ched 'R'
# Peppers") isn't misclassified as the drink "Dr Pepper" (#39 / #28 N19 root cause).
_DR_PEPPER_RE = re.compile(r"\bdr\.?\s*pepper\b")


def infer_combo_component(item_name: str) -> str:
    """Classify *item_name* into its combo-slot/happy-hour bucket.

    Returns ``"sides"``, ``"drinks"``, or ``""`` (not fillable as a combo side/drink slot and not
    happy-hour-discountable -- this covers combos, burgers/sandwiches, hot-dog entrees, and
    sundaes). Category comes from ``menuItems.json`` first; keyword fallback only applies to
    items that aren't in the menu at all (#39).
    """
    normalized = item_name.lower()
    if normalized in _BUCKET_EXCEPTIONS:
        return _BUCKET_EXCEPTIONS[normalized]

    category = MENU_CATEGORY_MAP.get(normalized)
    if category is not None:
        return _CATEGORY_BUCKET.get(category, "")

    # Not in the menu at all (e.g. a spoken item never added to menuItems.json) -- fall back to
    # keyword scanning, using a word-boundary match for "dr pepper" specifically.
    if "tot" in normalized or "fries" in normalized or "onion rings" in normalized:
        return "sides"
    if _DR_PEPPER_RE.search(normalized) or any(
        kw in normalized
        for kw in ("slush", "limeade", "ocean water", "drink", "tea", "lemonade", "shake", "blast", "malt", "coke", "sprite", "root beer")
    ):
        return "drinks"
    return ""
