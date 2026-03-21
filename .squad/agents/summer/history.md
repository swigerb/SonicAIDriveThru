# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic Voice Chat Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Core Context

**Rebrand & Setup (2026-03-19)**: Completed Sonic rebrand from Dunkin across all backend systems. Key learning: `_infer_category()` in tools.py couples frontend menuItems.json to backend category inference — both ALLOWED/BLOCKED lists must include canonical names + inferred variants. Replaced malformed structured_menu_items with Sonic menu data. Team coordinated: Rick scope, Morty frontend rebrand, Summer backend rebrand (69 tests), Birdperson verification (12 tests).

**Performance Hardening (2026-03-19)**: Major backend perf pass. Critical fixes: (1) `_tools_pending` moved from shared to per-connection (race condition bug). (2) `_PASSTHROUGH_TYPES` frozenset for O(1) hot-path audio delta bypass. (3) `__slots__` on data classes, module-level search cache with TTL, compression middleware, gzip on HTTP. Shared asyncio single-threaded principle: concurrent WebSocket state safe without locks. All 100 tests pass.

**Menu Integration (2026-03-19)**: Created production menu ingestion notebook for nested Sonic data (menus/products/categories/productGroups). Key patterns: recursive category traversal, size variant resolution via relatedProducts, 172 of 1334 products referenced. Lesson: `.ipynb` files require programmatic Python JSON editing (text-based edit tools fail on escaping).

**Critical Ordering Bugs (2026-03-19)**: Fixed three issues blocking voice orders: (1) `.env` pointed to wrong search index → no menu results. (2) System prompt didn't explain price extraction from `sizes` JSON field → $0.00 prices. (3) `max_tokens=150` too aggressive → truncated closing phrases. Raised to 250, added explicit PRICING and CLOSING rules. All 100 tests pass.

## Recent Work

<!-- Append new learnings below for current sprint work. -->
- **Server-Side Echo Suppression (2026-03-20)**: Implemented rtmt.py audio gating (ai_speaking flag, 300ms cooldown, buffer clear) to block phantom transcriptions. Coordinated with Morty frontend muting. All 100 tests pass.
- **Azure Speech Mode (2026-03-20)**: Rewrote azurespeech.py with full tool calling loop, session management, base64 audio handling. Reuses rtmt.system_message, conditional mount on env vars.
- **Server-Side Echo Suppression Refined (2026-03-21)**: Used fast substring detection (no JSON parse) for ai_speaking tracking across concurrent connections. Defense-in-depth with frontend mic-muting. All 100 tests pass.
- **Demo Readiness: System Prompt Refactor (2026-03-21)**: Converted dense paragraphs to bulleted format with ALL CAPS emphasis and variety rules for gpt-realtime-1.5 optimal instruction-following. Added explicit anti-hallucination grounding. Rationale: Bulleted format reduces latency, variety prevents bot-like repetition, grounding prevents demo brand-damage. Demo-safe max_tokens=250, temperature=0.6. Coordinated with Morty's VAD tuning (0.8→0.7, prefix 200→300ms) for safe demo environment.

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- **Asyncio shared state safety**: mutable state shared between coroutines (like `ai_speaking` and `cooldown_end`) is safe in asyncio without locks — event loop is single-threaded, all state checks atomic within tick.
- **gpt-realtime-1.5 prompt best practices**: Bulleted format with ALL CAPS emphasis beats prose paragraphs for instruction following. Explicit phrase variety rules prevent bot-like repetition. Reduce prompt token count through conciseness.
- **WebSocket message ordering**: Client messages arrive ordered via WebSocket. Server must forward session.update (with tools + system message) before greeting, or OpenAI processes greeting without context.
- **Menu category mapping coupling**: `_infer_category()` in tools.py couples to frontend menuItems.json. ALLOWED/BLOCKED lists must include both canonical category names AND keyword-inferred fallbacks.
- **Python notebook programmatic editing**: `.ipynb` files have complex JSON escaping — use Python JSON manipulation instead of text-based edit tools.
- **Search index correctness**: If `.env` points to wrong index, no search results. Always verify AZURE_SEARCH_INDEX env var matches expected index name.
- **System prompt token accounting**: Closing phrases consume ~20 tokens (e.g., "Your carhop will have that right out to you!"). `max_tokens=150` too aggressive for order recap + closing. 250 is demo-safe.
- **Order quantity limits**: Added `MAX_QUANTITY_PER_ITEM = 10` and `MAX_TOTAL_ITEMS = 25` in `tools.py` to prevent abuse. Validation runs in `update_order` before `handle_order_update`, only on "add" actions. Per-item check uses item+size combo match. Error messages are warm/drive-thru-friendly and sent `TO_SERVER` so the AI relays them naturally. Constants at module top for easy tuning. Defense-in-depth with Unity's conversational guardrails.

