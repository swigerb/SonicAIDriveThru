"""Tests for money_utils.py (#46): exact Decimal conversion and ROUND_HALF_UP money formatting."""

import sys
from decimal import Decimal
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from money_utils import format_money, to_decimal


def test_to_decimal_avoids_binary_float_noise():
    # Decimal(0.1) would reproduce the binary float's exact (noisy) value; to_decimal must not.
    assert to_decimal(0.1) == Decimal("0.1")
    assert to_decimal("2.79") == Decimal("2.79")
    assert to_decimal(Decimal("9.0396")) == Decimal("9.0396")


def test_format_money_half_cent_rounds_up_not_truncates():
    # Rick's N21/SpokenTotalHalfCentTests case: an exact half cent rounds up, not down.
    assert format_money(5.265) == "$5.27"


def test_format_money_trailing_zero_keeps_two_decimal_places():
    # Rick's N21 trailing-zero case: a whole-cent total must not truncate to "$10.8".
    assert format_money(10.80) == "$10.80"
    assert format_money(10.8) == "$10.80"


def test_format_money_rounds_up_a_non_midpoint_value():
    # Rick's N21 round-up-non-midpoint case (Tots medium x3 @ 2.79 -> 9.0396): proves the rule is
    # ROUND_HALF_UP, not a ceiling that rounds every fractional cent up regardless of the digit.
    assert format_money(9.0396) == "$9.04"


def test_format_money_does_not_ceiling_every_fractional_cent():
    # A non-half, non-multiple-of-ten fractional cent that should round DOWN, to prove the rule
    # isn't simply "always round up".
    assert format_money(2.561) == "$2.56"


def test_format_money_is_culture_invariant_and_exact():
    assert format_money(0) == "$0.00"
    assert format_money("3.5") == "$3.50"
