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
    "is_happy_hour_discounted",
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
    "extralarge": "xl",  # PR #50 review follow-up: "Extra Large" must canonicalize to "xl", the
                         # same key its own short form already uses -- matched via the punctuation-
                         # and-whitespace-stripped compact key below, same as the Route 44 aliases.
    "rt 44": "route 44",
    "rt44": "route 44",
    "44": "route 44",
    "44oz": "route 44",
    "route44": "route 44",
}

# Sizes that should be hidden in display strings (no prefix)
_NO_DISPLAY_SIZES = frozenset({"", "standard", "n/a", "na", "none", "n.a."})

# Punctuation ignored when compacting a size string for alias lookup (PR #50 review follow-up):
# "Route-44" and "rt. 44" must resolve identically to "route44"/"rt44" -- whitespace alone wasn't
# enough to catch the hyphen or period variants.
_SIZE_ALIAS_IGNORED_CHARS = frozenset(" .-")


def _compact_size_key(size: str) -> str:
    key = (size or "").strip().lower()
    return "".join(ch for ch in key if ch not in _SIZE_ALIAS_IGNORED_CHARS)


def normalize_size(size: str) -> str:
    """Return a human-readable size string, or ``""`` for hidden/standard sizes.

    Delegates alias resolution to ``canonical_size_key`` so the display prefix and the
    order-matching key can never disagree (PR #50 review follow-up) -- e.g. adding a drink with
    size ``"44 oz"`` must display "Route 44 ..." exactly like ``"rt44"`` does, not silently drop
    the size prefix because that specific spelling wasn't in ``SIZE_ALIASES`` verbatim.

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
    return SIZE_MAP.get(canonical_size_key(size), "")


def canonical_size_key(size: str) -> str:
    """Return the canonical, alias-resolved key used to match/merge/remove order lines (#40).

    All spellings of the same physical size must collapse to one key *before* any order-state
    matching happens, so e.g. ``"rt44"``, ``"route44"``, ``"44 oz"``, ``"Route-44"``, ``"rt. 44"``,
    ``"RT 44"`` and ``"Route 44"`` are all treated as the same line item, and ``"Extra Large"``
    collapses onto the same key as ``"xl"``. This mirrors the alias resolution ``normalize_size``
    already does for its display string, but returns the lookup key itself (not a human-readable
    label) and is case/whitespace/punctuation-normalised even for sizes with no known alias, so
    callers get consistent matching regardless of input casing or spacing.

    >>> canonical_size_key("rt44")
    'route 44'
    >>> canonical_size_key("Route-44")
    'route 44'
    >>> canonical_size_key("rt. 44")
    'route 44'
    >>> canonical_size_key("44 oz")
    'route 44'
    >>> canonical_size_key("Extra Large")
    'xl'
    >>> canonical_size_key(" Medium ")
    'medium'
    """
    key = (size or "").strip().lower()
    # Collapse whitespace/periods/hyphens so "44 oz", "Route-44" and "rt. 44" all resolve the same
    # way as their tighter spellings ("44oz", "route44", "rt44").
    compact_key = _compact_size_key(size)
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
# Combo-slot classification & happy-hour discount eligibility (#39, PR #50 review)
#
# These are two SEPARATE questions and must never be derived from one shared bucket (Rick's PR
# #50 review): "can this item fill a combo's included side/drink slot" (``infer_combo_component``)
# vs. "does this item get the happy-hour discount" (``is_happy_hour_discounted``). They agree on
# almost everything today, but that's incidental, not structural -- e.g. Shakes & Blasts are
# currently happy-hour-discounted pending Brian's ruling (see the single flag below) regardless of
# whether they can ever fill a combo's drink slot, and a future change to one must not silently
# change the other.
# ---------------------------------------------------------------------------

# Combo SIDE slot: the menu's own combo description says "your choice of a side (Tots or Fries)
# and a drink" -- this is a literal, explicit allow-list of exactly those two items (any size),
# NOT the whole "Hot Dogs & Tots"/"Extras & Sides" JSON category. Mapping the whole category to
# "sides" was a pricing regression caught in PR #50 review: Crispy Tenders (3pc/5pc), Premium
# Chicken Bites, both FRITOS(R) wraps, Fritos Chili Cheese Pie, Soft Pretzel Twist, Mozzarella
# Sticks, Onion Rings, Ched 'R' Peppers, and the Cheese/Chili-Cheese Tots & Fries variants were
# all being silently absorbed for free into a combo's side slot instead of charged in full
# (measured regression: Cheeseburger Combo + Crispy Tenders 5pc totalled $8.49 instead of $15.98).
_COMBO_SIDE_ITEMS = frozenset({"tots", "groovy fries"})

# Combo DRINK slot: unchanged from dev's original behaviour (confirmed via git history) -- every
# "Slushes & Drinks" item, plus every "Shakes & Ice Cream" item except the two sundaes (Brian's
# #39 decision: a sundae isn't a drink, so it can't fill a combo's drink slot or be discounted).
_COMBO_DRINK_CATEGORIES = frozenset({"slushes & drinks", "shakes & ice cream"})
_SUNDAES = frozenset({"hot fudge sundae", "caramel sundae"})

# Word-boundary so a side item merely *containing* the substring "pepper" (e.g. "Ched 'R'
# Peppers") isn't misclassified as the drink "Dr Pepper" (#39 / #28 N19 root cause).
_DR_PEPPER_RE = re.compile(r"\bdr\.?\s*pepper\b")

_DRINK_KEYWORDS = ("slush", "limeade", "ocean water", "drink", "tea", "lemonade", "shake", "blast", "malt", "coke", "sprite", "root beer")


def _keyword_fallback_is_drink(normalized: str) -> bool:
    """Keyword scan used ONLY for items that aren't in ``menuItems.json`` at all (e.g. a spoken
    item never added to the menu). "Dr Pepper" matches on a word boundary, never a bare "pepper"
    substring (#39 / #28 N19 root cause)."""
    return bool(_DR_PEPPER_RE.search(normalized)) or any(kw in normalized for kw in _DRINK_KEYWORDS)


def infer_combo_component(item_name: str) -> str:
    """Classify *item_name* for combo-SLOT-FILLING only.

    Returns ``"sides"``, ``"drinks"``, or ``""`` (can't fill either combo slot). This answers
    ONLY "can this item fill a combo's included side/drink slot" -- happy-hour discount
    eligibility is a SEPARATE question, answered by ``is_happy_hour_discounted`` below, and must
    never be derived from this function's result (PR #50 review). Category comes from
    ``menuItems.json`` first; keyword fallback only applies to items that aren't in the menu at
    all (#39).
    """
    normalized = item_name.lower()
    if normalized in _COMBO_SIDE_ITEMS:
        return "sides"
    if normalized in _SUNDAES:
        return ""

    category = MENU_CATEGORY_MAP.get(normalized)
    if category is not None:
        return "drinks" if category in _COMBO_DRINK_CATEGORIES else ""

    # Not in the menu at all (e.g. a spoken item never added to menuItems.json) -- fall back to
    # keyword scanning. Deliberately conservative: only the two allow-listed side names, never a
    # bare "fries"/"tot" substring match on other Hot Dogs & Tots / Extras & Sides items.
    if "tots" in normalized or "groovy fries" in normalized:
        return "sides"
    if _keyword_fallback_is_drink(normalized):
        return "drinks"
    return ""


# Pending Brian's ruling (#39 follow-up / PR #50 review): Shakes & Blasts are currently
# happy-hour-discounted, matching dev's existing behaviour. Flip this ONE flag to ``False`` the
# moment he decides otherwise (leaving Slushes & Drinks discounted) -- no other code needs to
# change.
_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED = True


def is_happy_hour_discounted(item_name: str) -> bool:
    """Whether *item_name* gets the happy-hour discount -- a SEPARATE question from
    ``infer_combo_component`` above (PR #50 review): don't derive one from the other. Sundaes are
    never discounted (Brian's #39 decision). See ``_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED``
    above for the one open question (Shakes & Blasts, pending Brian)."""
    normalized = item_name.lower()
    if normalized in _SUNDAES:
        return False

    category = MENU_CATEGORY_MAP.get(normalized)
    if category == "slushes & drinks":
        return True
    if category == "shakes & ice cream":
        return _SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED
    if category is not None:
        return False

    # Not in the menu at all -- same keyword fallback as the combo-drink-slot check.
    return _keyword_fallback_is_drink(normalized)
