"""Shared money formatting utilities (#46).

The Sonic AI Drive-Thru's spoken and displayed money values are computed with ``Decimal`` from
``menuItems.json`` prices and the config business-rule rates, with no intermediate rounding
anywhere in the calculation. Only the *final* result is rounded, once, when it needs to be shown
or spoken -- using ``ROUND_HALF_UP`` (an exact half cent rounds up; this is NOT the same as always
rounding up / a ceiling for non-half values).

Every spoken/displayed money surface in the backend (tools.py's tool responses, order_state.py's
readback) must go through ``format_money`` so they can never drift out of sync with each other or
with the conformance suite's golden values.
"""

from __future__ import annotations

from decimal import ROUND_HALF_UP, Decimal

__all__ = ["to_decimal", "format_money"]

_CENTS = Decimal("0.01")


def to_decimal(value) -> Decimal:
    """Convert *value* to an exact ``Decimal``.

    Values are converted via ``str()`` first -- ``Decimal(0.1)`` reproduces the binary float's
    exact (noisy) value, while ``Decimal(str(0.1))`` gives the clean decimal ``0.1`` a human
    actually meant. JSON numbers and config values always arrive as ``float``/``int``/``str``, so
    this is the correct, safe conversion for all of our money inputs.
    """
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


def format_money(value) -> str:
    """Render *value* as an exact, culture-invariant ``"$0.00"`` string.

    Rounds to the cent with ``ROUND_HALF_UP`` (an exact half cent, e.g. 5.265, rounds up to 5.27
    -- not a ceiling that would also round 5.261 up to 5.27). This must be the only place cents
    get rounded for display/speech; everywhere upstream of this call should stay in exact
    ``Decimal`` (or float) arithmetic with no intermediate rounding.
    """
    cents = to_decimal(value).quantize(_CENTS, rounding=ROUND_HALF_UP)
    return f"${cents:.2f}"
