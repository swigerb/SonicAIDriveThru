import logging
import uuid
from dataclasses import dataclass
from datetime import datetime

from models import OrderItem, OrderSummary

__all__ = ["OrderState", "SessionIdentifiers", "order_state_singleton", "is_happy_hour"]

logger = logging.getLogger("order_state")


def is_happy_hour() -> bool:
    """Check if the current time is between 2:00 PM and 4:00 PM local time."""
    now = datetime.now()
    return 14 <= now.hour < 16


def _infer_combo_component(item_name: str) -> str:
    """Lightweight category check for combo component validation (sides vs drinks)."""
    n = item_name.lower()
    if "tot" in n or "fries" in n or "onion rings" in n:
        return "sides"
    if any(kw in n for kw in ("slush", "limeade", "ocean water", "drink", "tea", "lemonade", "shake", "blast", "malt")):
        return "drinks"
    return ""


@dataclass
class SessionIdentifiers:
    session_token: str
    round_trip_index: int
    round_trip_token: str


class OrderState:
    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance.sessions = {}
        return cls._instance

    def _update_summary(self, session_id: str):
        session = self.sessions[session_id]
        order_items = session["order_state"]
        happy_hour = is_happy_hour()
        total = 0.0
        for item in order_items:
            item_total = item.price * item.quantity
            if happy_hour and _infer_combo_component(item.item) == "drinks":
                item_total *= 0.5  # Happy Hour: 50% off drinks and slushes
            total += item_total
        tax = total * 0.08  # 8% tax
        finalTotal = total + tax
        summary = OrderSummary(items=order_items, total=total, tax=tax, finalTotal=finalTotal)
        session["order_summary"] = summary
        # Cache the JSON representation to avoid repeated Pydantic serialization
        session["order_summary_json"] = summary.model_dump_json()
        logger.debug("Order summary updated for session %s (items=%d, total=%.2f)", session_id, len(order_items), finalTotal)

    def create_session(self) -> str:
        session_id = str(uuid.uuid4())
        session_token = str(uuid.uuid4())
        empty_summary = OrderSummary(items=[], total=0.0, tax=0.0, finalTotal=0.0)
        self.sessions[session_id] = {
            "order_state": [],
            "order_summary": empty_summary,
            "order_summary_json": empty_summary.model_dump_json(),
            "session_token": session_token,
            "round_trip_index": 0,
            "round_trip_token": self._format_round_trip_token(session_token, 0)
        }
        logger.info("Session created: %s", session_id)
        return session_id

    def delete_session(self, session_id: str) -> None:
        if self.sessions.pop(session_id, None) is not None:
            logger.info("Session deleted: %s", session_id)

    def _format_round_trip_token(self, session_token: str, round_trip_index: int) -> str:
        return f"{session_token}-{round_trip_index:04d}"

    def handle_order_update(self, session_id: str, action: str, item_name: str, size: str, quantity: int, price: float):
        session = self.sessions[session_id]
        order_state = session["order_state"]

        normalized_size = (size or "").strip().lower()
        if normalized_size in {"", "standard", "n/a", "na", "none", "n.a."}:
            formatted_size = ""
        elif normalized_size in {"rt44", "rt 44", "route 44", "44", "44oz"}:
            formatted_size = "Route 44 "
        elif normalized_size in {"mini", "small", "medium", "large"}:
            formatted_size = f"{normalized_size.capitalize()} "
        else:
            formatted_size = ""

        display = f"{formatted_size}{item_name}".strip()

        existing_item_index = next((index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size), -1)

        if action == "add":
            if existing_item_index != -1:
                order_state[existing_item_index].quantity += quantity
                logger.debug("Updated quantity for %s in session %s", display, session_id)
            else:
                order_state.append(OrderItem(item=item_name, size=size, quantity=quantity, price=price, display=display))
                logger.debug("Added %s to session %s", display, session_id)
        elif action == "remove":
            if existing_item_index != -1:
                if order_state[existing_item_index].quantity > quantity:
                    order_state[existing_item_index].quantity -= quantity
                    logger.debug("Decreased quantity for %s in session %s", display, session_id)
                else:
                    order_state.pop(existing_item_index)
                    logger.debug("Removed %s from session %s", display, session_id)

        self._update_summary(session_id)

    def get_order_summary(self, session_id: str) -> OrderSummary:
        return self.sessions[session_id]["order_summary"]

    def get_order_items(self, session_id: str) -> list:
        """Return raw order item list — avoids Pydantic overhead for validation checks."""
        return self.sessions[session_id]["order_state"]

    def get_combo_requirements(self, session_id: str) -> dict:
        """Scans the order for combos and returns missing components.
        Helps the AI know exactly what to ask for next."""
        session = self.sessions[session_id]
        order_items = session["order_state"]

        combo_count = sum(item.quantity for item in order_items if "combo" in item.item.lower())
        side_count = sum(item.quantity for item in order_items if _infer_combo_component(item.item) == "sides")
        drink_count = sum(item.quantity for item in order_items if _infer_combo_component(item.item) in ("drinks",))

        missing = []
        if side_count < combo_count:
            missing.append("a side (fries or tots)")
        if drink_count < combo_count:
            missing.append("a drink or slush")

        return {
            "is_complete": len(missing) == 0,
            "missing_items": missing,
            "prompt_hint": f"Ask the guest for {', and '.join(missing)} to finish their combo." if missing else ""
        }

    def get_grouped_order_for_readback(self, session_id: str) -> str:
        """
        Groups items with the same display name for a natural voice read-back.
        Example: 'Two Medium Cherry Limeades and one Footlong Quarter Pound Coney.'
        """
        session = self.sessions[session_id]
        items = session["order_state"]
        if not items:
            return "Your order is currently empty."

        # Aggregate quantities by display name
        counts = {}
        for oi in items:
            clean_name = oi.display.replace("RT 44", "Route 44").replace("RT44", "Route 44")
            counts[clean_name] = counts.get(clean_name, 0) + oi.quantity

        # Build the natural language string
        parts = []
        for display, qty in counts.items():
            prefix = f"{qty} " if qty > 1 else "one "
            parts.append(f"{prefix}{display}")

        if len(parts) > 1:
            summary_str = ", ".join(parts[:-1]) + f", and {parts[-1]}"
        else:
            summary_str = parts[0]

        total = session["order_summary"].finalTotal
        return f"I have {summary_str}. Your total is {total:.2f}. "

    def reset_order(self, session_id: str):
        """Clears all items from the current session's order."""
        session = self.sessions[session_id]
        session["order_state"] = []
        self._update_summary(session_id)
        logger.info("Order fully reset for session %s", session_id)

    def get_order_summary_json(self, session_id: str) -> str:
        """Return cached JSON string — avoids repeated Pydantic serialization."""
        return self.sessions[session_id]["order_summary_json"]

    def get_session_identifiers(self, session_id: str) -> SessionIdentifiers:
        session = self.sessions[session_id]
        return SessionIdentifiers(
            session_token=session["session_token"],
            round_trip_index=session["round_trip_index"],
            round_trip_token=session["round_trip_token"],
        )

    def advance_round_trip(self, session_id: str) -> SessionIdentifiers:
        session = self.sessions[session_id]
        session["round_trip_index"] += 1
        session["round_trip_token"] = self._format_round_trip_token(
            session["session_token"], session["round_trip_index"]
        )
        logger.debug(
            "Round trip %s recorded for session %s", session["round_trip_index"], session_id
        )
        return self.get_session_identifiers(session_id)

# Create a singleton instance of OrderState
order_state_singleton = OrderState()