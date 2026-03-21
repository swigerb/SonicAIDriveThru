# Decision: Fix Greeting-Before-Session.Update Tool Blindness

**Author:** Summer (Backend Dev)
**Date:** 2026-03-22
**Status:** Implemented

## Problem

The AI conversed perfectly (asking about combos, sizes, drinks) but NEVER called `update_order`, `search`, or `get_order`. The order panel stayed at $0.00. Root cause: `from_client_to_server()` in `rtmt.py` sent the greeting (`conversation.item.create` + `response.create`) to OpenAI BEFORE forwarding the client's `session.update` message — which is where tools, system_message, and tool_choice are injected.

OpenAI received:
1. `conversation.item.create` (greeting)
2. `response.create` (generate response — no tools configured yet)
3. `session.update` (tools + system_message arrive too late)

The model generated responses using the system prompt (delivered via session.update) for conversation, but tool definitions were not reliably picked up after being registered post-first-response.

## Fix

1. **Reordered greeting**: Client messages are now forwarded FIRST (`_process_message_to_server` → `send_str`), then the greeting fires. OpenAI sees: session.update → greeting → response.create. Tools are configured before the first completion.

2. **Fallback tools_pending registration**: `response.output_item.added` now pre-registers `call_id` in `tools_pending` as a safety net. `conversation.item.created` always overwrites with the correct `previous_item_id`. Prevents silent tool call drops if API event ordering changes.

3. **Added diagnostic logging**: `session.update` now logs tool count and tool_choice. Tool execution logs tool name, args, and result direction.

## Impact

- Fixes #1 demo blocker — orders will now appear on the carhop ticket
- All 118 existing tests pass
- No API or schema changes required
