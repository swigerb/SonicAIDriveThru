# Decision: ToolResultDirection.TO_BOTH for Order Updates

**Author:** Summer (Backend Dev)
**Date:** 2026-03-21
**Status:** Implemented

## Context

Successful `update_order` calls used `ToolResultDirection.TO_CLIENT`, which sent the order summary to the frontend UI but sent an **empty string** to the OpenAI model. The AI received no confirmation the order succeeded, causing dead silence after valid orders (including ordering exactly 10 items at the per-item limit).

## Decision

Added `ToolResultDirection.TO_BOTH = 3` to the enum. Successful order updates now use `TO_BOTH`, which sends the order summary JSON to both:
- **OpenAI server** — so the AI knows the item was added and can continue ("anything else?")
- **Frontend client** — so the UI updates with the current order

## Impact

- `rtmt.py`: New enum value + updated routing conditions
- `tools.py`: Success path changed from `TO_CLIENT` → `TO_BOTH`
- All existing tests updated, 14 new quantity-limit tests added (111 total, all passing)
- Error/limit responses remain `TO_SERVER` (they only need the AI to relay the message)

## Team Notes

This is a behavioral change to the WebSocket middleware. Morty's frontend already handles `extension.middle_tier_tool_response` messages — no frontend changes needed. The AI will now naturally continue the conversation after every successful order update.
