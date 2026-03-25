import logging
import os
import uuid
from dataclasses import dataclass
from datetime import datetime
from zoneinfo import ZoneInfo

from config_loader import get_config
from models import OrderItem, OrderSummary
from menu_utils import infer_category, normalize_size

__all__ = ["OrderState", "SessionIdentifiers", "order_state_singleton", "is_happy_hour"]

logger = logging.getLogger("order_state")

_config = get_config()
_biz_cfg = _config.get("business_rules", {})

# Configurable store timezone — defaults to Sonic HQ (Oklahoma City).
# Override via STORE_TIMEZONE env var for stores in other time zones.
_STORE_TZ = ZoneInfo(os.environ.get("STORE_TIMEZONE", "America/Chicago"))


def is_happy_hour() -> bool:
    """Check if the current time is within the happy hour window (store-local time)."""
    now = datetime.now(_STORE_TZ)
    start = _biz_cfg.get("happy_hour_start", 14)
    end = _biz_cfg.get("happy_hour_end", 16)
    return start <= now.hour < end


def _infer_combo_component(item_name: str) -> str:
    """Lightweight category check for combo component validation (sides vs drinks).

    Delegates to the shared ``infer_category`` in menu_utils to avoid drift.
    """
    cat = infer_category(item_name)
    if cat in ("sides",):
        return "sides"
    if cat in ("drinks", "slushes", "shakes", "shakes & ice cream", "slushes & drinks"):
        return "drinks"
    # Fallback: keyword scan for items that don't hit the JSON map
    n = item_name.lower()
    if "tot" in n or "fries" in n or "onion rings" in n:
        return "sides"
    if any(kw in n for kw in ("slush", "limeade", "ocean water", "drink", "tea", "lemonade", "shake", "blast", "malt", "coke", "sprite", "pepper", "root beer")):
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
                item_total *= _biz_cfg.get("happy_hour_discount", 0.5)
            total += item_total
        tax = total * _biz_cfg.get("tax_rate", 0.08)
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
            "round_trip_token": self._format_round_trip_token(session_token, 0),
            "absorbed_sides": 0,
            "absorbed_drinks": 0,
            "absorbed_side_display": "",
            "absorbed_drink_display": "",
        }
        logger.info("Session created: %s", session_id)
        return session_id

    def delete_session(self, session_id: str) -> None:
        if self.sessions.pop(session_id, None) is not None:
            logger.info("Session deleted: %s", session_id)

    def _format_round_trip_token(self, session_token: str, round_trip_index: int) -> str:
        return f"{session_token}-{round_trip_index:04d}"

    def handle_order_update(self, session_id: str, action: str, item_name: str, size: str, quantity: int, price: float) -> dict:
        session = self.sessions[session_id]
        order_state = session["order_state"]
        result_info = {}

        resolved = normalize_size(size)
        formatted_size = f"{resolved} " if resolved else ""

        display = f"{formatted_size}{item_name}".strip()

        if action == "add":
            is_combo = "combo" in item_name.lower()

            # ── Combo conversion: auto-remove matching standalone entree ──
            if is_combo:
                combo_base = item_name.lower().replace(" combo", "").replace("®", "").strip()
                for i, existing in enumerate(order_state):
                    if "combo" in existing.item.lower():
                        continue  # skip other combos
                    existing_base = existing.item.split("(")[0].strip().lower().replace("®", "")
                    if existing_base == combo_base:
                        # Carry customization mods (e.g., "Pickles Only") to the combo
                        if "(" in existing.item:
                            mods = existing.item[existing.item.find("("):]
                            item_name = f"{item_name} {mods}"
                            display = f"{formatted_size}{item_name}".strip()
                            result_info["mods_carried"] = mods
                        result_info["combo_converted_from"] = existing.item
                        if existing.quantity > 1:
                            existing.quantity -= 1
                        else:
                            order_state.pop(i)
                        logger.info("Combo conversion: removed standalone '%s' for combo '%s'", existing.item, item_name)
                        break

            # ── Post-combo absorption: side/drink fills an incomplete combo slot ──
            if not is_combo:
                component = _infer_combo_component(item_name)
                if component in ("sides", "drinks"):
                    combo_count = sum(it.quantity for it in order_state if "combo" in it.item.lower())
                    if combo_count > 0:
                        if component == "sides":
                            filled = sum(it.quantity for it in order_state if _infer_combo_component(it.item) == "sides")
                            filled += session.get("absorbed_sides", 0)
                        else:
                            filled = sum(it.quantity for it in order_state if _infer_combo_component(it.item) == "drinks")
                            filled += session.get("absorbed_drinks", 0)

                        slots_available = combo_count - filled
                        if slots_available > 0:
                            to_absorb = min(quantity, slots_available)
                            if component == "sides":
                                session["absorbed_sides"] += to_absorb
                            else:
                                session["absorbed_drinks"] += to_absorb
                            remaining = quantity - to_absorb
                            result_info["absorbed_into_combo"] = True
                            result_info["absorbed_component"] = component
                            result_info["absorbed_display"] = display

                            # Update the combo item's display to show the absorbed component
                            for combo_item in order_state:
                                if "combo" in combo_item.item.lower():
                                    # Build component list from absorbed sides/drinks
                                    components = []
                                    if session.get("absorbed_side_display"):
                                        components.append(session["absorbed_side_display"])
                                    if session.get("absorbed_drink_display"):
                                        components.append(session["absorbed_drink_display"])
                                    # Store current component display for future reference
                                    if component == "sides":
                                        session["absorbed_side_display"] = display
                                        if display not in components:
                                            components.append(display)
                                    else:
                                        session["absorbed_drink_display"] = display
                                        if display not in components:
                                            components.append(display)
                                    # Rebuild combo display with components
                                    base_name = combo_item.item
                                    mods = ""
                                    if "(" in combo_item.display:
                                        mods_start = combo_item.display.find("(")
                                        mods_end = combo_item.display.find(")")
                                        if mods_end > mods_start:
                                            mods = " " + combo_item.display[mods_start:mods_end + 1]
                                    combo_item.display = f"{base_name}{mods} w/ {' & '.join(components)}"
                                    break

                            logger.info("Post-combo absorption: '%s' absorbed as combo %s", display, component)
                            if remaining <= 0:
                                self._update_summary(session_id)
                                return result_info
                            else:
                                quantity = remaining

            # ── Regular add ──
            existing_item_index = next(
                (index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size),
                -1
            )
            if existing_item_index != -1:
                order_state[existing_item_index].quantity += quantity
                logger.debug("Updated quantity for %s in session %s", display, session_id)
            else:
                order_state.append(OrderItem(item=item_name, size=size, quantity=quantity, price=price, display=display))
                logger.debug("Added %s to session %s", display, session_id)

            # ── Combo pivot: absorb standalone sides/drinks into a newly added combo ──
            if is_combo:
                absorbed_side = False
                absorbed_drink = False
                items_to_remove = []
                for i, existing in enumerate(order_state):
                    if existing.item == item_name:
                        continue  # skip the combo itself
                    component = _infer_combo_component(existing.item)
                    if component == "sides" and not absorbed_side:
                        logger.info("Absorbing '%s' into new combo '%s'", existing.display, item_name)
                        if existing.quantity > 1:
                            existing.quantity -= 1
                        else:
                            items_to_remove.append(i)
                        absorbed_side = True
                    elif component == "drinks" and not absorbed_drink:
                        logger.info("Absorbing '%s' into new combo '%s'", existing.display, item_name)
                        if existing.quantity > 1:
                            existing.quantity -= 1
                        else:
                            items_to_remove.append(i)
                        absorbed_drink = True
                for idx in reversed(items_to_remove):
                    order_state.pop(idx)
                if absorbed_side:
                    session["absorbed_sides"] += 1
                if absorbed_drink:
                    session["absorbed_drinks"] += 1

        elif action == "remove":
            existing_item_index = next((index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size), -1)
            if existing_item_index != -1:
                if order_state[existing_item_index].quantity > quantity:
                    order_state[existing_item_index].quantity -= quantity
                    logger.debug("Decreased quantity for %s in session %s", display, session_id)
                else:
                    order_state.pop(existing_item_index)
                    logger.debug("Removed %s from session %s", display, session_id)

        self._update_summary(session_id)
        return result_info

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

        # Include sides/drinks absorbed into combos during combo pivot
        side_count += session.get("absorbed_sides", 0)
        drink_count += session.get("absorbed_drinks", 0)

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
            # Convert parenthesized mods to speech-friendly format
            # e.g. "Sonic Cheeseburger (No Lettuce)" -> "Sonic Cheeseburger with no lettuce"
            if "(" in clean_name and ")" in clean_name:
                clean_name = clean_name.replace("(", "with ").replace(")", "")
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
        session["absorbed_sides"] = 0
        session["absorbed_drinks"] = 0
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