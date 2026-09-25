from pydantic import BaseModel, model_validator

from money_utils import format_money

__all__ = ["OrderItem", "OrderSummary"]


class OrderItem(BaseModel):
    item: str
    size: str
    quantity: int
    price: float
    display: str


class OrderSummary(BaseModel):
    items: list[OrderItem]
    total: float
    tax: float
    finalTotal: float
    # #47: exact, contract-rounded display strings (money_utils.format_money) computed from the
    # same Decimal values as total/tax/finalTotal, before any float conversion. Additive -- the
    # plain numeric fields above are unchanged and still present -- so the frontend ticket can
    # render these strings directly instead of re-rounding an already-noisy float with `.toFixed`.
    # Left as "" (rather than required) so existing call sites that only pass the numeric fields
    # keep working; the validator below fills them in from the numeric fields when omitted.
    totalDisplay: str = ""
    taxDisplay: str = ""
    finalTotalDisplay: str = ""

    @model_validator(mode="after")
    def _fill_display_defaults(self) -> "OrderSummary":
        if not self.totalDisplay:
            self.totalDisplay = format_money(self.total)
        if not self.taxDisplay:
            self.taxDisplay = format_money(self.tax)
        if not self.finalTotalDisplay:
            self.finalTotalDisplay = format_money(self.finalTotal)
        return self