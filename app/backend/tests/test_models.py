import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from models import OrderItem, OrderSummary


class OrderItemTests(unittest.TestCase):
    def test_basic_creation(self):
        item = OrderItem(item="Glazed Donut", size="standard", quantity=1, price=1.49, display="Glazed Donut")
        self.assertEqual(item.item, "Glazed Donut")
        self.assertEqual(item.size, "standard")
        self.assertEqual(item.quantity, 1)
        self.assertAlmostEqual(item.price, 1.49)
        self.assertEqual(item.display, "Glazed Donut")

    def test_serialization_round_trip(self):
        item = OrderItem(item="Caramel Craze Latte", size="large", quantity=2, price=5.49, display="Large Caramel Craze Latte")
        data = item.model_dump()
        restored = OrderItem(**data)
        self.assertEqual(item, restored)

    def test_json_round_trip(self):
        item = OrderItem(item="Cold Brew", size="medium", quantity=1, price=3.99, display="Medium Cold Brew")
        json_str = item.model_dump_json()
        restored = OrderItem.model_validate_json(json_str)
        self.assertEqual(item, restored)

    def test_zero_quantity(self):
        item = OrderItem(item="Muffin", size="standard", quantity=0, price=2.99, display="Muffin")
        self.assertEqual(item.quantity, 0)

    def test_zero_price(self):
        item = OrderItem(item="Free Sample", size="small", quantity=1, price=0.0, display="Small Free Sample")
        self.assertAlmostEqual(item.price, 0.0)

    def test_large_quantity(self):
        item = OrderItem(item="Munchkins", size="standard", quantity=100, price=0.25, display="Munchkins")
        self.assertEqual(item.quantity, 100)

    def test_float_price_precision(self):
        item = OrderItem(item="Latte", size="small", quantity=3, price=4.999, display="Small Latte")
        self.assertAlmostEqual(item.price, 4.999)


class OrderSummaryTests(unittest.TestCase):
    def test_empty_summary(self):
        summary = OrderSummary(items=[], total=0.0, tax=0.0, finalTotal=0.0)
        self.assertEqual(len(summary.items), 0)
        self.assertAlmostEqual(summary.total, 0.0)
        self.assertAlmostEqual(summary.tax, 0.0)
        self.assertAlmostEqual(summary.finalTotal, 0.0)

    def test_summary_with_items(self):
        items = [
            OrderItem(item="Glazed Donut", size="standard", quantity=2, price=1.49, display="Glazed Donut"),
            OrderItem(item="Latte", size="large", quantity=1, price=5.49, display="Large Latte"),
        ]
        total = 2 * 1.49 + 5.49
        tax = total * 0.08
        final = total + tax
        summary = OrderSummary(items=items, total=total, tax=tax, finalTotal=final)
        self.assertEqual(len(summary.items), 2)
        self.assertAlmostEqual(summary.total, total)
        self.assertAlmostEqual(summary.finalTotal, final)

    def test_serialization_round_trip(self):
        items = [OrderItem(item="Cold Brew", size="medium", quantity=1, price=3.99, display="Medium Cold Brew")]
        summary = OrderSummary(items=items, total=3.99, tax=0.3192, finalTotal=4.3092)
        data = summary.model_dump()
        restored = OrderSummary(**data)
        self.assertEqual(summary, restored)

    def test_json_round_trip(self):
        items = [OrderItem(item="Bagel", size="standard", quantity=1, price=2.49, display="Bagel")]
        summary = OrderSummary(items=items, total=2.49, tax=0.1992, finalTotal=2.6892)
        json_str = summary.model_dump_json()
        restored = OrderSummary.model_validate_json(json_str)
        self.assertEqual(len(restored.items), 1)
        self.assertAlmostEqual(restored.total, 2.49)

    def test_summary_items_are_order_items(self):
        item = OrderItem(item="Muffin", size="standard", quantity=1, price=2.99, display="Muffin")
        summary = OrderSummary(items=[item], total=2.99, tax=0.2392, finalTotal=3.2292)
        self.assertIsInstance(summary.items[0], OrderItem)
        self.assertEqual(summary.items[0].item, "Muffin")

    def test_display_fields_default_from_numeric_fields(self):
        # #47: callers that only pass the numeric fields (e.g. every pre-existing call site above)
        # must still get correct display strings -- the additive fields are never left blank.
        summary = OrderSummary(items=[], total=9.0396, tax=0.0, finalTotal=9.0396)
        self.assertEqual(summary.totalDisplay, "$9.04")
        self.assertEqual(summary.finalTotalDisplay, "$9.04")

    def test_explicit_display_fields_are_not_overwritten(self):
        # #47: order_state.py passes display strings computed from the pre-float-conversion
        # Decimal (more precise than re-deriving from the already-rounded float fields); those
        # explicit values must win over the auto-derived default.
        summary = OrderSummary(
            items=[],
            total=5.265,
            tax=0.0,
            finalTotal=5.265,
            totalDisplay="$5.27",
            taxDisplay="$0.00",
            finalTotalDisplay="$5.27",
        )
        self.assertEqual(summary.totalDisplay, "$5.27")
        self.assertEqual(summary.finalTotalDisplay, "$5.27")

    def test_display_fields_round_trip_through_json(self):
        summary = OrderSummary(items=[], total=10.80, tax=0.0, finalTotal=10.80)
        restored = OrderSummary.model_validate_json(summary.model_dump_json())
        self.assertEqual(restored.finalTotalDisplay, "$10.80")


if __name__ == "__main__":
    unittest.main()
