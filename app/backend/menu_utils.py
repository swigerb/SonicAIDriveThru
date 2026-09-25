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
    "strip_modifiers",
    "_menu_key",
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
# Modifier-suffix stripping & the ONE lookup-key normalisation rule (PR #50 review)
#
# Defined BEFORE _load_menu_category_map()/MENU_CATEGORY_MAP below so the map itself can be keyed
# by _menu_key() (PR #50 review, round 4) -- previously it was keyed by a bare ``name.lower()``,
# which does NOT collapse Unicode whitespace (e.g. NBSP, U+00A0) or the registered-trademark
# symbol "®". Several real menuItems.json names contain an NBSP where a normal space would be
# expected (e.g. "SONIC Blast®\xa0made with OREO®\xa0Cookie Pieces" -- verified via the raw JSON
# bytes), so that item's own exact name MISSED its own map entry and only classified correctly by
# keyword-fallback luck (the substring "blast" happened to still match). Keying the map with the
# same _menu_key() used to look items up at runtime closes that gap for good, for every current and
# future menu item, not just this one.
# ---------------------------------------------------------------------------

# Strips a trailing parenthesized customization suffix, e.g. "Tots (Extra Crispy)" -> "Tots"
# (PR #50 review: customised items were bypassing every menuItems.json-based lookup because the
# modifiers travel inside item_name, tools.py's ``update_order`` -- see ``strip_modifiers`` below).
_MODIFIER_SUFFIX_RE = re.compile(r"\s*\([^)]*\)\s*")


def strip_modifiers(item_name: str) -> str:
    """Strip parenthesized customization suffix(es) from *item_name* and collapse whitespace.

    THE single normalisation rule for turning a possibly-customised order-line name (e.g.
    ``"Chili Cheese Tots (Extra Cheese)"``, ``"Tots (Extra Crispy)"``) into its base menu-item
    name. Used both for every menuItems.json-based lookup below (combo slot / sundae / category /
    happy-hour eligibility) *and* for combo-conversion base-name matching in ``order_state.py`` --
    one rule, one implementation, so the two can never drift (Rick's PR #50 review: "reuse one
    helper, don't duplicate").

    The exact algorithm (PR #50 review round 4 -- documented in full in the conformance README):
    every ``\\s*\\([^)]*\\)\\s*`` group ANYWHERE in the string (not just a trailing one) collapses
    to a single space, then ``str.split()``/``" ".join(...)`` collapses all whitespace runs --
    including Unicode whitespace such as NBSP (U+00A0), which Python's ``str.split()`` already
    treats as a separator. A modifier group in the middle of the name is stripped exactly like a
    trailing one, and multiple groups are all stripped. Nested or unbalanced parentheses are a
    deliberate fail-safe, NOT a special case: ``[^)]*`` cannot skip over an inner ``(``, so a nested
    group only ever partially matches, leaving a stray unmatched ``)`` in the result -- that stray
    character then guarantees the cleaned name won't equal any real (or allow-listed) menu key, so
    the item is classified as unknown and charged in full rather than risking an incorrect match.

    >>> strip_modifiers("Tots (Extra Crispy)")
    'Tots'
    >>> strip_modifiers("Chili Cheese Tots (Extra Cheese)")
    'Chili Cheese Tots'
    >>> strip_modifiers("Cherry Limeade")
    'Cherry Limeade'
    >>> strip_modifiers("Tots (Extra Crispy) (No Salt)")
    'Tots'
    >>> strip_modifiers("Chili Cheese (Extra Cheese) Tots")
    'Chili Cheese Tots'
    >>> strip_modifiers("Tots (Extra (Really) Crispy)")
    'Tots Crispy)'
    """
    return " ".join(_MODIFIER_SUFFIX_RE.sub(" ", item_name or "").split())


def _menu_key(item_name: str) -> str:
    """Lowercased, modifier-stripped, symbol-normalised key used for ALL menuItems.json-based
    classification (combo slot / sundae / category / happy-hour eligibility) AND for
    ``MENU_CATEGORY_MAP``'s own keys below. A customised item must classify identically to its
    uncustomised base item -- PR #50 review: "Chili Cheese Tots (Extra Cheese)" must be charged in
    full exactly like "Chili Cheese Tots" is, and "Cherry Limeade (Extra Cherries)" must still get
    the happy-hour discount exactly like "Cherry Limeade" does.

    Three symbol-normalisation rules live here -- and ONLY here, i.e. this is the one and only
    place any of them live (PR #50 review round 4/5: they used to live, or would otherwise need to
    live, as second, independently-maintained rules elsewhere -- e.g. ``order_state.py``'s
    combo-conversion matching used to strip "®" itself, before it started calling this function):
      - The registered-trademark symbol "®" is stripped (``SONIC® Cheeseburger``).
      - The trademark symbol "™" is stripped identically (PR #50 review round 5) -- eight
        ``menuItems.json`` names carry it (the "SONIC Smasher™" family, plain and Combo variants);
        without this, a spoken "All-American SONIC Smasher" (naturally omitting an unspeakable
        symbol) would miss its own map entry exactly like the OREO Blast's NBSP used to.
      - The curly/typographic apostrophe "\u2019" is normalised to a plain ASCII apostrophe "'"
        (PR #50 review round 5) -- ``menuItems.json``'s "SONIC Blast® made with REESE'S" uses the
        curly form verbatim, so a spoken "Reese's" (naturally typed/transcribed with a plain
        apostrophe) would otherwise miss its own map entry too."""
    return (
        strip_modifiers(item_name)
        .lower()
        .replace("®", "")
        .replace("\u2122", "")
        .replace("\u2019", "'")
    )


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
                    # PR #50 review round 4: key by _menu_key(name), not a bare name.lower() -- see
                    # the module comment above this section for the NBSP regression this closes.
                    mapping[_menu_key(name)] = category
        return mapping
    except Exception as exc:  # pragma: no cover
        logger.warning("Failed to load menu items; falling back to keyword inference: %s", exc)
        return {}


MENU_CATEGORY_MAP: dict[str, str] = _load_menu_category_map()


def infer_category(item_name: str) -> str:
    """Return the menu category for *item_name* (keyword fallback if not in the JSON map)."""
    normalized = _menu_key(item_name)
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

# Fountain-drink keywords: unconditionally eligible for both combo-drink-slot-filling and the
# happy-hour discount, matching every "Slushes & Drinks" menuItems.json item's unconditional
# behaviour. Used ONLY as a fallback for items that aren't in the menu at all (e.g. a spoken item
# never added to menuItems.json) -- on-menu items are always matched by JSON category first.
#
# Word-boundary (PR #50 review round 4): a plain substring check let "tea" match inside "steak",
# so an off-menu "Philly Cheesesteak"/"Steak Sandwich" was silently absorbed into a combo's drink
# slot AND happy-hour discounted. ``\b...\b`` requires the keyword to be its own word. Dr Pepper
# keeps its own separate, already-word-boundary regex above.
#
# PR #50 review (round 5, "keyword over-correction"): the initial word-boundary fix over-corrected
# -- ``s?`` only allows a single trailing "s", so it missed the "-es"/"-ie"/"-y" spoken variants
# entirely: "Cherry Slushes" (plural "-es"), "Blue Raspberry Slushie" ("-ie"), and a guest saying
# "Slushy" ("-y") are all genuinely off-menu names (the real items are "... Slush", singular) that
# must still hit this fallback. ``slush(?:ie|y)?`` matches the bare word plus either spoken
# variant, and the outer ``(?:e?s)?`` allows the regular "-s"/"-es" plural on TOP of that (so
# "Slushies" still matches too) without reopening the "tea"-in-"steak" hole: the boundary is still
# required immediately before the keyword.
_FOUNTAIN_DRINK_KEYWORD_RE = re.compile(
    r"\b(?:slush(?:ie|y)?|limeade|ocean water|drink|tea|lemonade|coke|sprite|root beer)(?:e?s)?\b"
)

# Shake/Blast/Malt keywords: same (unconditional) combo-drink-slot eligibility as fountain drinks,
# but the happy-hour DISCOUNT for this bucket must obey ``_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED``
# below -- PR #50 review: flipping that one flag must change *every* shake/blast variant, plain or
# customised, on-menu or off, not just the ones matched by JSON category.
#
# PR #50 review (round 5): a plain ``\bshake\b`` never matches "milkshake" at all -- there is no
# word boundary between "milk" and "shake" (both are word characters), so "Chocolate Milkshake"
# (a genuinely off-menu spoken variant; the real item is "... Classic Shake") fell all the way
# through to "" instead of "drinks". ``(?:\b|milk)`` is a deliberate, narrow carve-out: match
# either a normal word boundary OR the literal "milk" immediately before "shake"/"blast"/"malt",
# so "milkshake" resolves as a compound word without loosening the boundary for any other
# preceding text (a nonsense "overshake"/"bookshake" still correctly does NOT match).
_SHAKE_BLAST_KEYWORD_RE = re.compile(r"(?:\b|milk)(?:shake|blast|malt)(?:e?s)?\b")


def _keyword_fallback_combo_drink(normalized: str) -> bool:
    """Combo-drink-slot-filling fallback for items that aren't in menuItems.json at all.
    Combo-slot eligibility is unconditional for both buckets -- it never depends on the
    happy-hour-discount flag, which is a separate question (see ``is_happy_hour_discounted``)."""
    return (
        bool(_DR_PEPPER_RE.search(normalized))
        or bool(_FOUNTAIN_DRINK_KEYWORD_RE.search(normalized))
        or bool(_SHAKE_BLAST_KEYWORD_RE.search(normalized))
    )


def _keyword_fallback_happy_hour_discounted(normalized: str) -> bool:
    """Happy-hour-discount fallback for items that aren't in menuItems.json at all. Fountain
    drinks are always discounted; shakes/blasts/malts obey
    ``_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED`` so that flag is the single switch for every
    shake/blast, on-menu or off, plain or customised (PR #50 review)."""
    if _DR_PEPPER_RE.search(normalized) or _FOUNTAIN_DRINK_KEYWORD_RE.search(normalized):
        return True
    if _SHAKE_BLAST_KEYWORD_RE.search(normalized):
        return _SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED
    return False


def infer_combo_component(item_name: str) -> str:
    """Classify *item_name* for combo-SLOT-FILLING only.

    Returns ``"sides"``, ``"drinks"``, or ``""`` (can't fill either combo slot). This answers
    ONLY "can this item fill a combo's included side/drink slot" -- happy-hour discount
    eligibility is a SEPARATE question, answered by ``is_happy_hour_discounted`` below, and must
    never be derived from this function's result (PR #50 review). Category comes from
    ``menuItems.json`` first; keyword fallback only applies to items that aren't in the menu at
    all (#39). *item_name* may carry a parenthesized customization suffix (e.g. "Tots (Extra
    Crispy)") -- ``_menu_key`` strips it before any lookup so a customised item classifies
    identically to its base item (PR #50 review).
    """
    normalized = _menu_key(item_name)
    if normalized in _COMBO_SIDE_ITEMS:
        return "sides"
    if normalized in _SUNDAES:
        return ""

    category = MENU_CATEGORY_MAP.get(normalized)
    if category is not None:
        return "drinks" if category in _COMBO_DRINK_CATEGORIES else ""

    # Not in the menu at all (e.g. a spoken item never added to menuItems.json). PR #50 review:
    # an unknown item must NEVER silently fill the combo side slot for free -- a charged item is
    # visible and correctable, a free absorption is silent revenue loss -- so there is no side
    # fallback here at all, only the (unconditional) drink fallback for genuinely off-menu
    # fountain drinks/shakes/blasts.
    if _keyword_fallback_combo_drink(normalized):
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
    above for the one open question (Shakes & Blasts, pending Brian). *item_name* may carry a
    parenthesized customization suffix -- ``_menu_key`` strips it before any lookup so a
    customised drink is discounted (or not) exactly like its base item (PR #50 review)."""
    normalized = _menu_key(item_name)
    if normalized in _SUNDAES:
        return False

    category = MENU_CATEGORY_MAP.get(normalized)
    if category == "slushes & drinks":
        return True
    if category == "shakes & ice cream":
        return _SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED
    if category is not None:
        return False

    # Not in the menu at all -- same keyword fallback categories as the combo-drink-slot check,
    # but gated so the shakes/blasts flag above is genuinely the single switch (PR #50 review).
    return _keyword_fallback_happy_hour_discounted(normalized)
