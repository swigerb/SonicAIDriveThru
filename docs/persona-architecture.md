# Persona architecture: one drive-thru app, three brands, two backends

- **Issue:** #19 (P1 design spike), including the design for #51. Part of epic #6.
- **Decision record:** [ADR-001](adr/ADR-001-persona-architecture.md)
- **Status:** Proposed, 2026-09-25T19:10:59-04:00. Review with Brian before any P2 work.
- **Author:** Rick (lead)

## 1. Summary

One app serves Sonic, McDonald's and Dunkin. Each brand is a **persona pack**: a folder of data
(`persona.json`, prompts, `menuItems.json`, assets) that both the Python and the C# backend load through the
same contract. Brand rules become data read by shared engines. The one behavior data can't express
(McDonald's meal-number lookup) becomes a named strategy that both backends implement.

The persona is **chosen per session** (`/realtime?persona=<id>`, with a picker in the UI) inside a
**per-deployment allow-list** (`PERSONAS`, `DEFAULT_PERSONA`). The unified app lives in this repo. The sibling
repos stay live and secured (McDonald's #6, Dunkin #11) until the unified app reaches parity, then they are
archived. P2 is 12 work items. The first three are behavior-neutral refactors of Sonic, run behind the existing
suite before any new brand lands.

## 2. Inputs and method

| Repo | Role | Branch and HEAD read | Working tree |
| --- | --- | --- | --- |
| `swigerb/SonicAIDriveThru` | Primary, reference | `dev` at `d720e16` (spike start); this branch is cut from `origin/dev` `0806b52`, which only adds a Scribe log commit | Clean apart from Scribe's `.squad` notes |
| `swigerb/McDonalds_AI_DriveThru` | Sibling, read-only | `dev` at `cb8cfb5` (Merge #5: scrub session.updated) | Clean |
| `swigerb/dunkin-chat-voice-assistant` | Sibling, read-only | `dev` at `efa6b64` (Merge #10: scrub session.updated) | Clean |

Method: a file-level diff of all tracked files, then a line-level diff of every shared backend, frontend, infra
and script file, then a direct read of each brand's rules (`menu_utils.py`, `order_state.py`, `tools.py`,
prompts, `config.yaml`, `menuItems.json`, `index.css`, `App.tsx`). Differences caused only by Sonic being ahead
on hardening (S1, S1.5) are not brand differences. They are listed once, in the last row of the inventory.

Live topology (from each repo's azd environment): three resource groups (`rg-sonic-demo`, `rg-mcd-demo`,
`rg-dunkin-demo`), three container apps, **one shared Search service** (free SKU, semantic ranker disabled)
holding three indexes (`sonic-menu-items`, `mcdonalds-menu-items`, `dunkin-menu-items`), and one shared Azure
OpenAI quota. Sonic uses the `gpt-realtime-2.1` deployment; the siblings use `gpt-realtime-2.1-dz`. The
comment in `setup_search_index.py` notes that the free SKU allows one service with three indexes.

## 3. Difference inventory

Classes:

- **Shared code:** one code path for every persona.
- **Persona data:** lives in the persona pack.
- **Strategy:** named, closed-set behavior in shared code, selected by the pack. Both backends implement it.
- **Drop:** not carried into the unified app. It stays in the archived repo's history.

"Mc" is McDonald's. File references are to each repo's `dev` HEAD above.

### 3.1 Prompts and conversation text

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | System prompt | `prompts/sonic/system_prompt.yaml`, 22 sections incl. `SONIC_BRANDING_AND_SIZING`, `HAPPY_HOUR`, `COMBO_LOGIC` | `prompts/mcdonalds/system_prompt.yaml`, 253 lines, adds `COMBO_MEAL_SYNONYMS`, `EXTRA_VALUE_MEALS` | Inline Python string `DUNKIN_SYSTEM_PROMPT` in `app.py:31` (about 14 sentences) | Persona data | `personas/<id>/prompts/system_prompt.yaml` (Dunkin converted to YAML) |
| 2 | Greeting | `greeting.yaml`: "Welcome to Sonic Drive-In! ..."; a second hard-coded copy in `session_manager.py:181` | `greeting.yaml`: "Welcome to McDonald's! ..." | Hard-coded in `rtmt.py:260`: "Welcome to Dunkin! How may I help you today?" | Persona data | `prompts/greeting.yaml`; the code copies are removed |
| 3 | Role name in nudge and transcript replay | "carhop" (`session_manager.py:153`, `:455`) | "crew member" | "crew" (`rtmt.py:991`) | Persona data | `persona.json` `roleName` |
| 4 | Tool schemas (descriptions) | `tool_schemas.yaml` | `tool_schemas.yaml` allows only add/remove, and it wins over the inline schema in `tools.py:413` that has `modify` (`tools.py:685`). The prompt (`system_prompt.yaml:155`) still tells the model to call `modify`, so `modify` is dormant: a sibling bug | Inline in `tools.py:296`, telling the model that extras are separate items | Persona data (text); shared code (`modify`) | `prompts/tool_schemas.yaml`; `modify` offered only where the pack's schema lists it (McDonald's, fixed in P2-6) |
| 5 | Upsell hints, delta templates | `hints.yaml`, plus in-code fallbacks in `tools.py:533` | `hints.yaml` (McFlurry), plus `tools.py:596` | None | Persona data | `prompts/hints.yaml`; the in-code fallbacks are deleted |
| 6 | Error and refusal text | `error_messages.yaml` | `error_messages.yaml` | Inline strings in `tools.py:408` | Persona data | `prompts/error_messages.yaml` |
| 7 | Happy-hour banner in tool results | Hard-coded in `tools.py:547` and `:569` | "drinks and slushes are half-price!" (a Sonic leftover) at `tools.py:609` | None: Dunkin's happy hour is silent | Persona data | `persona.json` `pricing.happyHour.banner` |
| 8 | Missing-combo-part hint | "a side (fries or tots)", "a drink or slush" in `order_state.py:338` | "... to finish their meal" | n/a | Persona data | `persona.json` `bundles.missingPartText` |
| 9 | Local-model prompt | n/a | `local_system_prompt.yaml` (Phi-4) | n/a | Drop | Goes with local mode (row 37) |

### 3.2 Menu, index and ingestion

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 10 | Menu data | `menuItems.json`: 6 categories, 60 items (10 combos); fields `name, sizes, description, longDescription, origin, popularity, image`; raw export `sonic-menu-items.json` (3.4 MB) | 5 categories, 71 items (20 meals); adds `menuPeriod` (breakfast 20, lunch 32, allDay 19), `mealNumber`, `calories`, `allergens`; sources `mcdonalds-menu-items.json`, `offline_menu.json` | 5 categories, 16 items; adds `caffeineContent`, `brewingMethod`, `calories`, `availability`; `structured_menu_items`; 3 PDFs in `public/` | Persona data | `personas/<id>/menu/menuItems.json`; raw exports in `menu/source/` |
| 11 | Search index name | `sonic-menu-items` | `mcdonalds-menu-items` | Live: `dunkin-menu-items`; stale defaults: `coffee-chat` (`.env-sample`) and `voicerag-intvect` (`main.parameters.json`) | Persona data | `persona.json` `search.indexName` |
| 12 | Index schema | `id, category, name, description, longDescription, origin, caffeineContent, brewingMethod, popularity, sizes, embedding` | Narrower: `id, category, name, description, sizes, embedding` | Same as Sonic | Shared code | One superset schema. Adding fields is additive for the McD index |
| 13 | Search content fields | `description` | `description, longDescription, category` | `description, longDescription, category` | Persona data | `persona.json` `search.contentFields` (default `description`) |
| 14 | Generic ingestion | `setup_search_index.py` | Same, McD-flavored strings | Same, Dunkin-flavored strings | Shared code | One script with `--persona` (or it loops over `PERSONAS`) |
| 15 | Brand raw-export converters | `extract_production_items.py`, `update_menu_sizes.py`, `sonic_menu_ingestion_search.ipynb` | `build_mcdonalds_menu.py`, `mcdonalds_menu_ingestion_search.ipynb` | `ingest_menu_local.py` (ChromaDB edge) | Persona data prep | `scripts/personas/<id>/`. The Dunkin edge script is dropped with row 41 |
| 16 | PDF integrated vectorization | n/a | `menu_ingestion_search_pdf.ipynb`, `setup_intvect.*` | Same plus 3 PDFs | Drop | VoiceRAG template leftovers; no live index uses them |

### 3.3 Ordering rules

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 17 | Sizes | Mini, Small, Medium, Large, Extra Large, **Route 44**; aliases `rt 44, rt44, 44, 44oz, route44, extralarge`; "RT 44" is always spoken "Route 44" (`order_state.py:361`, prompt `SONIC_BRANDING_AND_SIZING`) | Small, Medium, Large (default Medium); `Regular` on 2 items | small, medium, large (lowercase in data) | Persona data plus shared normalizer | `persona.json` `sizes`. Route 44 exists only in Sonic's pack |
| 18 | Bundle detection | `"combo" in name` | `"meal" in name or "combo" in name` (synonyms) | None | Persona data | Per-item `bundle`; off-menu fallback via `bundles.nameMarkers` |
| 19 | Bundle slots | Side (only Tots or Groovy Fries) and drink, both chosen by the guest; a separately ordered side or drink is absorbed | Side **auto-filled** (`{size} Fries`, Medium if unsized; Hash Browns for breakfast meals); drink absorbed | n/a | Shared code plus persona data | One bundle engine; slot `fill: "absorb"` or `"autoFill"` per item |
| 20 | Standalone to bundle conversion | Adding "X Combo" removes a standalone X and carries its mods | Same for meals | n/a | Shared code | Bundle engine |
| 21 | Resize a bundle in place | No | `modify` action changes size and re-prefixes components (`order_state.py:319`), but the loaded schema never offers it (row 4) | n/a | Shared code | Engine supports it; exposed only when the pack's tool schema lists `modify` |
| 22 | Bundle components on the wire | Folded into `display` ("X w/ Tots & Drink") | `OrderItem.components: list[str]` | n/a | Shared code | Additive optional `components` field for every persona; the ticket renders it when present |
| 23 | Meal numbers | n/a | `MEAL_NUMBER_MAP` and a regex in `tools.py:94` rewrite "number 1" to "Big Mac Meal" before search. The same numbers are already in `menuItems.json` (`mealNumber`, `menuPeriod`) | n/a | Strategy plus persona data | `strategies.searchQueryRewrite: "meal_numbers"`, reading `mealNumber` from the menu |
| 24 | Combo-slot classification | Menu category plus name tables `_COMBO_SIDE_ITEMS`, `_SUNDAES`, `_TOTS_ALIASES`, and a word-bounded off-menu fallback (`menu_utils.py:293` to `:440`) | Substring keywords (`order_state.py:39`: "tea", "pepper", "coffee", "shake", "mcflurry" ...), so "Philly Cheesesteak" counts as a drink | n/a | Persona data (#51) | Per-item `comboSlot`; `offMenu.slotRules` |
| 25 | Spoken aliases | `_TOTS_ALIASES` (14 exact forms, #60) | None | None | Persona data | Per-item `aliases` |
| 26 | Extras | Keywords: flavor add-in, whipped cream, extra patty, extra cheese, add bacon. Allowed with drinks, slushes, shakes, burgers, combos; blocked on hot dogs and tots and sides. Plain-text refusal | Keywords: extra patty, extra cheese, add bacon. Allowed with burgers and sandwiches, chicken and McNuggets, combos; blocked on drinks, shakes, sides, desserts | Whipped cream $0.50, flavor swirl $0.75, extra espresso shot $1.00. Allowed with signature lattes and cold beverages; blocked on donuts and bakery and breakfast sandwiches. Structured JSON rejection with `suggested_calls`; splits "Latte with extra shot" into two calls | Persona data plus shared code | `persona.json` `extras`; one guard with structured rejection for every persona |
| 27 | Invalid modifiers | `INVALID_MODS` in `tools.py:106` | Similar table in `tools.py:230` | None | Persona data | `persona.json` `invalidModifiers` |
| 28 | Out-of-stock machines | Ice cream machine "down": shake, blast, sundae, ice cream (`tools.py:84`) | Same keywords (McFlurry is not in the list) | None | Persona data | `persona.json` `machines` plus per-item `requiresMachine` |
| 29 | Category keyword fallback (extras eligibility, upsell) | Substring (`menu_utils.py:258`): "tea" matches "steak" | Substring (`tools.py:214`) | Substring (`tools.py:_infer_category`) | Persona data | `offMenu.categoryRules`, word-bounded |

### 3.4 Pricing

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 30 | Happy hour | 14:00 to 16:00 store time, price x 0.5, slushes and fountain drinks only; shakes, Blasts and sundaes full price (#39, decisions 43 to 46); announced | 14:00 to 16:00, x 0.5 on anything the substring check calls a drink, including shakes and McFlurry; the prompt says "half-price drinks" | 14:00 to 17:00, x 0.75 (25% off), signature lattes and cold beverages (`config.yaml` `happy_hour_categories`); never mentioned to the guest | Persona data | `pricing.happyHour` plus per-item `happyHourDiscounted` |
| 31 | Tax | 8% | 8% | 8% | Persona data | `pricing.taxRate` |
| 32 | Store timezone | `America/Chicago` | `America/Chicago` | `America/New_York` | Persona data | `store.timezone`; `STORE_TIMEZONE` stays as a global override for the conformance FixedClock profile |
| 33 | Money arithmetic | `Decimal`, `format_money`, `*Display` fields (#47) | float | float | Shared code | Sonic's |
| 34 | Quantity limits | 10 per item, 25 per order | Same | Same | Shared config | `config.yaml`; a persona may override |

### 3.5 Voice, audio, frontend

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 35 | Default voice | `marin` (`manifest.yaml` still says `coral`; nothing reads it) | `marin` | `marin` | Persona data | `persona.json` `voice.default`. The picker list and labels are shared |
| 36 | Apology clips | `apology-<lang>.wav`, voiced as "a friendly Sonic Drive-In carhop" | `rate-limit-apology-<lang>.wav` | `apology-<lang>.wav`, "a friendly Dunkin' drive-thru crew member". Same four phrases in all three | Persona data (assets); shared script | `assets/audio/apology-<lang>.wav`; `generate_apology_clips.py --persona` |
| 37 | McD local mode | n/a | Phi-4 ONNX, Piper TTS, Whisper STT, `processor_router.py`; about 3,000 backend lines; UI toggle; `docker-compose.local.yml` | n/a | Drop | Q7 |
| 38 | Azure Speech mode | Frontend toggle only | `azurespeech.py`, `azure_speech_gpt4o_mini.py`, but nothing imports them, and no backend registers `/azurespeech/*` | Same | Drop | Dead in all three; remove the toggle |
| 39 | Theme | `--brand-red 341 100% 45%`, `--brand-blue 208 52% 33%`, light/dark; Nunito Sans and Montserrat; **87 hard-coded hex values** in `App.tsx`, `order-summary.tsx`, `menu-panel.tsx` | `--brand-red 357 100% 43%`, dark `40 12% 14%`; 114 hex values in 5 files | `--brand-orange 28 100% 58%`, `--brand-pink 329 100% 45%`, cream, brown; Fredoka; 76 hex values | Persona data plus shared code | `persona.json` `ui.theme` tokens applied by `PersonaProvider` |
| 40 | Identity, copy, legal | Logo svg/png, title "Sonic Drive-In Voice Ordering", hero ("Carhop Pick"), ticket "Carhop ticket / Your Sonic Order", legal line naming Inspire Brands and Sonic Corp., `app.title` and `status.notRecordingMessage` in 4 locales | Logo, "McDonald's AI Drive-Thru", same keys | Logo, "Dunkin' Voice Crew", extra favicons, same keys | Persona data | `persona.json` `ui` block plus `assets/` |
| 41 | Menu panel | Imported at build time (`menu-panel.tsx:1`) | Adds a breakfast/lunch toggle filtering on `menuPeriod` | Categories only | Shared code plus persona data | Fetched at runtime; the daypart toggle is shown when the pack declares `dayparts` |
| 42 | Demo data, resume key | `dummyOrder.json`, `dummyTranscripts.json`; `sonic.resumeId` | Own demo data | Own demo data | Persona data; shared key | `assets/demo/`; key `drivethru.resumeId.<persona>` |

### 3.6 Config, infra and brand-only features

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 43 | VAD and search tuning | VAD 0.5 / 200 ms; KNN 15, top 3 (perf audit) | Same | VAD 0.7 / 500 ms; KNN 50, top 5 | Shared config | Sonic's tuned values. The browser sends its own VAD anyway |
| 44 | Env vars | `AZURE_SEARCH_INDEX`, `STORE_TIMEZONE`, `SONIC_MENU_ITEMS_PATH` / `MENU_ITEMS_PATH` | Adds `LOCAL_MODE_*`, `AZURE_SEARCH_CONTENT_FIELDS` | Adds `USE_LOCAL_PIPELINE`, `CRM_DB_PATH`; `.env.template` for AKS, ACR, Key Vault | Shared code | Add `PERSONAS`, `DEFAULT_PERSONA`, `PERSONAS_DIR`, optional `SEARCH_INDEX_<ID>`. `AZURE_SEARCH_INDEX` applies only when one persona is enabled. The menu-path vars are removed. Local and edge vars are dropped |
| 45 | Bicep, azd | `APP_SESSION_SECRET`, sticky ingress, EasyAuth, replicas 1 to 5, postdeploy smoke | Same, minus small ordering changes; `MCD_SKIP_REALTIME_SMOKE` | No `APP_SESSION_SECRET`, no replica bounds or health probe (hardening lag); `DUNKIN_SKIP_REALTIME_SMOKE` | Shared code | Sonic's, plus the persona env vars; one smoke that loops over personas |
| 46 | Crew dashboard and simulator | n/a | n/a | `/dashboard` WebSocket, `/simulator/demo`, `app/employee-dashboard` SPA served at `/crew/` (467 lines), `drive_thru/` simulator (595 lines), `session_manager` publishes orders to it | Drop (Q7) | Could return later as a persona-agnostic `features.crewDashboard` |
| 47 | CRM | n/a | n/a | `crm/` (252 lines, SQLite), `crm_seed.json`, `seed_crm.py`. Only the simulator's fake guests use it; the voice agent never calls it | Drop | |
| 48 | Azure Local edge | n/a | n/a | `Dockerfile.edge`, `requirements-edge.txt`, `rtmt_local.py` (667 lines), `k8s/` (5 files), `flux/` (22 files), `deploy-edge.*`, 2 docs | Drop (Q7) | |
| 49 | Internal protocol ids | `sonic_mt_` item-id prefix (#29 authorship), `sonic_*` event ids, logger `sonic-drive-in` | Own | Own | Shared code, **unchanged** | Never shown to guests; the #29 authorship contract depends on the prefix |
| 50 | Brand-rule tests | `test_combo_orders.py`, `test_menu_utils.py`, golden files, the conformance suite (about 420 scenarios) | `test_order_logic.py`, `test_menu_utils.py`, `test_extras_rules.py` | `test_happy_hour.py`, `test_extras_rules.py`, `test_update_order_result.py`, `test_crm.py` | Persona data (golden rows) | `tests/conformance/testdata/personas/<id>/`; the CRM and dashboard tests are dropped |
| 51 | Hardening lag (not brand) | S1 and S1.5 relay security, resume, rate limit, reasoning, exact money, conformance hooks | Partly ported; #6 open | Partly ported; #11 open | Shared code | Sonic's implementation is the base |

**Count:** 51 rows. 31 are persona data (some with a shared engine that reads them), 12 are shared code or
config, 1 is a strategy, and 7 are dropped (brand-only side features, dead code and template leftovers).

## 4. The persona contract

### 4.1 Directory layout

```text
personas/
  persona.schema.json          JSON Schema for persona.json (both backends and CI validate against it)
  menu.schema.json             JSON Schema for menuItems.json, including the #51 fields
  sonic/
    persona.json               rules, strategies, UI manifest
    prompts/
      system_prompt.yaml       moved from app/backend/prompts/sonic/ (manifest.yaml folds into persona.json)
      greeting.yaml
      tool_schemas.yaml
      error_messages.yaml
      hints.yaml
    menu/
      menuItems.json           the served menu and the index source (moved from app/frontend/src/data/)
      source/                  raw brand exports, e.g. sonic-menu-items.json
    assets/
      logo.svg  logo.png  favicon.ico
      audio/apology-en.wav  apology-es.wav  apology-fr.wav  apology-ja.wav
      demo/dummyOrder.json  demo/dummyTranscripts.json
  mcdonalds/  (same shape)
  dunkin/     (same shape)
scripts/personas/<id>/         brand raw-export converters (row 15)
tests/conformance/testdata/personas/<id>/
  golden-menu-categories.json  golden-order-pricing.json  golden-<feature>.json
```

`personas/` sits at the repo root because the Python backend, the C# backend, the frontend build, the
conformance suite and the scripts all read it. The Dockerfiles copy it to `/app/personas`.

### 4.2 `persona.json`

All money values are quoted decimal strings, so C# reads them as `decimal` without going through `double`
(the same convention as `golden-order-pricing.json`). Unknown fields are rejected. The Sonic pack, abridged:

```json
{
  "$schema": "../persona.schema.json",
  "schemaVersion": 1,
  "id": "sonic",
  "displayName": "Sonic Drive-In",
  "roleName": "carhop",
  "locales": { "default": "en", "supported": ["en", "es", "fr", "ja"] },
  "store": { "timezone": "America/Chicago" },
  "voice": { "default": "marin" },
  "search": { "indexName": "sonic-menu-items", "contentFields": ["description"] },

  "pricing": {
    "taxRate": "0.08",
    "happyHour": {
      "startHour": 14, "endHour": 16, "priceMultiplier": "0.5", "announce": true,
      "banner": "[HAPPY HOUR ACTIVE: slushes and fountain drinks are half-price; shakes, Blasts and sundaes are full price]"
    }
  },

  "sizes": {
    "canonical": { "mini": "Mini", "small": "Small", "medium": "Medium", "large": "Large",
                   "xl": "Extra Large", "route 44": "Route 44", "standard": "Standard" },
    "aliases":   { "s": "small", "m": "medium", "l": "large", "extralarge": "xl",
                   "rt 44": "route 44", "rt44": "route 44", "44": "route 44", "44oz": "route 44", "route44": "route 44" },
    "spokenAs":  { "RT 44": "Route 44", "RT44": "Route 44" },
    "hidden":    ["", "standard", "n/a", "na", "none", "n.a."],
    "default":   null
  },

  "bundles": {
    "nameMarkers": ["combo"],
    "convertStandalone": true,
    "missingPartText": { "sides": "a side (fries or tots)", "drinks": "a drink or slush" }
  },

  "extras": {
    "keywords": ["flavor add-in", "whipped cream", "extra patty", "extra cheese", "add bacon"],
    "allowedBaseCategories": ["slushes & drinks", "shakes & ice cream", "burgers & sandwiches",
                              "drinks", "slushes", "shakes", "combos"],
    "blockedBaseCategories": ["hot dogs & tots", "sides", "hot dogs"],
    "splitCombinedNames": false
  },

  "invalidModifiers": { "shake": ["lettuce", "tomato", "onion"], "slush": ["cheese", "bacon", "patty"] },
  "machines": { "ice_cream_machine": "down", "slush_machine": "operational", "fryer": "operational" },

  "offMenu": {
    "categoryRules": [
      { "match": "\\b(?:slush(?:ie|y)?|limeade|ocean water)(?:e?s)?\\b", "category": "slushes" },
      { "match": "(?:\\b|milk)(?:shake|blast|malt)(?:e?s)?\\b", "category": "shakes" }
    ],
    "slotRules": [
      { "match": "(?:\\b|milk)(?:shake|blast|malt)(?:e?s)?\\b", "comboSlot": "drinks", "happyHourDiscounted": false },
      { "match": "\\bdr\\.?\\s*pepper\\b", "comboSlot": "drinks", "happyHourDiscounted": true },
      { "match": "\\b(?:slush(?:ie|y)?|limeade|ocean water|drink|tea|lemonade|coke|sprite|root beer)(?:e?s)?\\b",
        "comboSlot": "drinks", "happyHourDiscounted": true }
    ]
  },

  "strategies": { "searchQueryRewrite": "none" },
  "features": { "dayparts": false },

  "ui": {
    "title": "Sonic Drive-In Voice Ordering",
    "theme": {
      "light": { "primary": "341 100% 45%", "secondary": "208 52% 33%", "background": "195 44% 96%", "foreground": "208 53% 20%" },
      "dark":  { "primary": "347 100% 71%" },
      "font":  { "family": "Nunito Sans", "importUrl": "https://fonts.googleapis.com/css2?family=Nunito+Sans:wght@400;600;700;800;900&display=swap" }
    },
    "assets": { "logo": "assets/logo.svg", "favicon": "assets/favicon.ico", "apologyClip": "assets/audio/apology-{lang}.wav" },
    "strings": {
      "en": { "app.title": "Sonic Voice Ordering", "status.notRecordingMessage": "Let's order from America's Drive-In!",
              "ticket.kicker": "Carhop ticket", "ticket.title": "Your Sonic Order", "menu.button": "View Sonic Menu" }
    },
    "hero": { "headline": "Sonic ordering powered by Azure conversation intelligence", "callouts": [] },
    "legal": "Disclaimer: This project is a non-commercial demo application ... not affiliated with, endorsed, or sponsored by Inspire Brands, Inc. or Sonic Corp. ..."
  }
}
```

Field rules:

| Block | Rule |
| --- | --- |
| `sizes` | The shared normalizer replaces `SIZE_MAP` and `SIZE_ALIASES`. It uses the compact-key matching from `menu_utils.py:64` for every persona. `spokenAs` drives readback. Route 44 appears only in Sonic's pack. |
| `bundles` | Engine settings. Which items are bundles, and their slots, is per item (4.3). `nameMarkers` is only for off-menu names the model invents. |
| `extras` | One guard for all personas. It checks that the order already has a base in an allowed category. A refusal is always the structured JSON result Dunkin uses today (`status: "rejected"`, `item_added: false`, optional `suggested_calls`), with the text from the pack's `error_messages.yaml`. |
| `offMenu` | Only for names not found in the menu (after `_menu_key` normalization and aliases). Each list is ordered and the first match wins. The default is `category: ""`, `comboSlot: "none"`, `happyHourDiscounted: false`. The loader **rejects any rule with `comboSlot: "sides"`**, which makes Rick's PR #50 revenue rule structural: an unknown item can never be absorbed as a free side. Patterns are limited to a portable subset (ASCII literals, `\b`, `\s`, `\.`, `(?:...)`, `|`, `?`, `*`, `+`, character classes) that Python `re` and .NET `Regex` treat the same way. |
| `strategies` | A closed set. P2 has one slot, `searchQueryRewrite`, with the values `none` and `meal_numbers`. Adding a value needs both backends and a conformance scenario in the same PR series. |
| `ui` | Everything the browser needs before it connects. It is served by `/api/personas/<id>`. Prompts and rules are never served to the browser. |

### 4.3 Per-item menu fields (#51)

`menuItems.json` keeps today's shape (`menuItems[].category`, `items[]` with `name`, `sizes[]`, `description`,
...). The new fields are optional and additive, and the defaults are the safe ones:

| Field | Type, default | Meaning | Replaces |
| --- | --- | --- | --- |
| `comboSlot` | `"sides" \| "drinks" \| "none"`, default `"none"` | Can this item fill a bundle's included side or drink slot? The values match `golden-menu-categories.json` exactly, so golden rows can be compared field by field | `_COMBO_SIDE_ITEMS`, `_SUNDAES`, `_COMBO_DRINK_CATEGORIES` and the category-derived rule |
| `happyHourDiscounted` | bool, default `false` | Does the happy-hour multiplier apply? Independent of `comboSlot` (PR #50 rule) | `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED`, Dunkin's `happy_hour_categories`, McD's keyword check |
| `aliases` | string[], default `[]` | Exact spoken names that resolve to this item after `_menu_key` normalization | `_TOTS_ALIASES` |
| `bundle` | object, absent means not a bundle | `{ "slots": ["sides", "drinks"], "autoFill": { "sides": "{size} Fries" }, "defaultSize": "Medium" }` | `"combo" in name`, McD `_is_meal`, `_get_default_side`, `_BREAKFAST_KEYWORDS` |
| `requiresMachine` | string, optional | Key into `persona.json` `machines` | `_ICE_CREAM_MACHINE_KEYWORDS` for on-menu items |
| `isExtra` | bool, default `false` | This item is an extra (Dunkin's Whipped Cream, Flavor Swirl, Extra Espresso Shot) | Extras detection for on-menu extras |
| `menuPeriod`, `mealNumber` | existing McD fields, now in the schema | Daypart filter and meal-number lookup | McD `MEAL_NUMBER_MAP`, `BREAKFAST_MEAL_NUMBER_MAP` |

Sonic examples: `Tots` gets `comboSlot: "sides"` and the #60 aliases. `Hot Fudge Sundae` gets
`comboSlot: "none"` and `happyHourDiscounted: false`. `Cherry Limeade` gets `comboSlot: "drinks"` and
`happyHourDiscounted: true`. `Chocolate Classic Shake` gets `drinks` and `false` (Brian, #39).
`Cheeseburger Combo` gets `bundle.slots: ["sides", "drinks"]`. McDonald's examples: `Big Mac® Meal` gets
`bundle: { slots: ["sides", "drinks"], autoFill: { sides: "{size} Fries" }, defaultSize: "Medium" }`, and
`Egg McMuffin® Meal` gets `autoFill: { sides: "Hash Browns" }`.

One deliberate scope change: #60 limited the Tots aliases to the combo side slot. Here an alias resolves the
item for every lookup (category, happy hour, machines). For Tots the result is the same everywhere: the alias
now gives the category `hot dogs & tots` instead of the fallback's `sides`, both are in the blocked-extras set,
both trigger the same upsell hint, and Tots is never discounted. P2-2 pins this with a test.

### 4.4 How each backend loads a pack

| Step | Python (P2) | C# (S2 to S4) |
| --- | --- | --- |
| Find packs | `PERSONAS_DIR` (default `<repo>/personas`, `/app/personas` in the container); enable `PERSONAS` (comma list, default: every folder); `DEFAULT_PERSONA` must be in the list | Same env vars, bound to `PersonaOptions` |
| Parse and validate | Pydantic models with `extra="forbid"` for `persona.json` and `menuItems.json`; CI also validates against the JSON Schemas | `System.Text.Json` source-generated records with `JsonUnmappedMemberHandling.Disallow`; prompts through YamlDotNet (already planned in #12) |
| Build | Per persona: `MenuIndex` (normalized key and aliases to item), compiled `offMenu` regexes, a `PromptLoader` pointed at `personas/<id>/prompts` (today's class, given a path instead of a brand), and one `SearchClient` per index | `PersonaCatalog` singleton; `Regex` with `CultureInvariant` and a match timeout; strategies as keyed services (`ISearchQueryRewriter`: `none`, `meal_numbers`) |
| Fail fast | Any invalid enabled pack stops startup with the file and field named | Same |
| Per session | `session_manager` stores `persona_id`; `tools.py` and `order_state.py` take the session's `Persona` instead of module globals | The `SessionActor` holds its `Persona` |
| Report | `/health` gains `personas: ["sonic", ...]` (additive) | Same |
| Hot reload | `DEV_MODE` keeps reloading prompts, per persona | Not required |

The normalization rules (`_menu_key`: paren groups, whitespace, lowercase invariant, `®`, `™`, curly
apostrophe) stay shared and stay documented in `tests/conformance/README.md`. They are not per persona.

## 5. Switching personas

### 5.1 Options

| | A. Per deployment (`PERSONA=sonic`) | B. Per session (URL and picker) | **C. B inside a per-deployment allow-list (recommended)** |
| --- | --- | --- | --- |
| Demo UX | Three URLs; switching means changing tabs | One URL; switching takes two clicks; brands can run side by side in two tabs | Same as B. A deployment with one persona looks like today's single-brand app |
| AI Search | One static index per app | Per-session client over the three existing indexes | Same as B |
| Voice | Per-app default; per-session picker still works | Per-session default from the pack (the voice is already set per session) | Same as B |
| Azure cost | Three always-on container apps (min 1 replica, 1 vCPU / 2 GiB each), three Entra redirect URIs; six apps after S7 | One container app; S7 adds one C# app, not three | One by default; add a single-brand deployment only where a demo needs isolation |
| Code | Smallest change; globals can stay | The persona is threaded through the session. C# needs this shape anyway (DI) | Same as B plus two env vars |
| Conformance | One backend process per persona | One backend process serves all personas | Same as B |

**Recommendation: C.** It gives the one-URL, switchable demo Brian asked for. It costs less. It keeps a clean
escape hatch for brand isolation (for example, an Inspire Brands demo that should not show McDonald's, Q2).
And it forces the per-session `Persona` object that makes the C# port clean.

**Not supported:** switching mid-session. The realtime voice locks after the first audio, the order rules
differ by brand, and swapping the instructions mid-conversation confuses the model. A switch is always a new
session.

### 5.2 Wire contract (both backends, pinned by conformance)

| Surface | Contract |
| --- | --- |
| `GET /api/personas` | `{ "default": "sonic", "personas": [ { "id", "displayName", "logoUrl", "theme" } ] }`, enabled personas only |
| `GET /api/personas/{id}` | The pack's `ui` block, plus `voice.default`, `locales`, `features.dayparts` and `menuUrl`. 404 if not enabled |
| `GET /personas/{id}/assets/*`, `GET /personas/{id}/menu.json` | Static files from the pack, immutable caching (the existing compression and caching middleware) |
| `GET /realtime?persona={id}` | Omitted: `DEFAULT_PERSONA`. Unknown or not enabled: **HTTP 404 before the WebSocket upgrade**, never a silent fallback. The persona is fixed for the session |
| `extension.metadata` | Gains `persona` (additive) |
| Resume | The held session remembers its persona. `extension.resume` from a socket opened with a different persona gets `extension.resume_rejected` with `reason: "persona_mismatch"`, then a fresh session under the URL's persona (the existing rejection path, then metadata) |
| Session token, Origin checks, EasyAuth | Unchanged. The persona is not a secret and is not in the HMAC token |

## 6. #51 and #64

**#51 is designed here and implemented in P2-2 (Sonic) and P2-6/P2-7 (the new brands).**

- On-menu classification comes only from the per-item fields in 4.3. `menu_utils.py` keeps the shared
  normalization and the lookup engine, but no item names. `grep` for any Sonic item name in
  `app/backend/*.py` returns nothing (acceptance check).
- Off-menu names use `offMenu` rules from the pack. The combo-slot and happy-hour rules are already
  word-bounded (PR #50 rounds 4 and 5) and move to data unchanged, so conformance stays unchanged. The
  shake-first precedence (decision 45) comes free from first-match ordering. Every fallback bucket fills the
  drink slot, so first-match gives the same combo answer as today's unordered OR.
- The **category** fallback (`infer_category`, used for extras eligibility and upsell hints) is still
  substring-based, which is the "tea" in "steak" bug. P2-2 word-bounds it, with explicit compound forms where
  real names need them (for example `(?:\b|cheese)burgers?\b`). Each behavior change is pinned by a new test.
- `golden-menu-categories.json` stays hand-owned. A new test checks it against each persona's menu data
  (checked against, not generated from), so the golden file remains an independent oracle.
- Q4 asks Brian whether to go further and delete the off-menu fallback completely.

**#64 (floats) is not decided here.** Either answer is one entry in Sonic's `persona.json`, plus pytest and
conformance rows:

| Brian's answer | Data | Effect |
| --- | --- | --- |
| Full price at happy hour | Add `{ "match": "\\bfloats?\\b", "comboSlot": "drinks", "happyHourDiscounted": false }` as the **first** `slotRule` | Root Beer, Coke and Dr Pepper Float fill the combo drink slot and stay full price |
| Half price at happy hour | Add the same rule with `"happyHourDiscounted": true` | Same as today's accidental behavior, but explicit and pinned instead of relying on the "root beer", "coke" and "dr pepper" keywords |
| Floats are real menu items | Add them to `menuItems.json` with explicit `comboSlot` and `happyHourDiscounted` | They show in the menu panel and the index; the rule above is not needed |

There is a sub-question too: should a float fill the combo drink slot at all? Today it does.

## 7. Conformance suite: the persona dimension (#21)

| Change | Detail |
| --- | --- |
| Personas under test | `CONFORMANCE_PERSONAS` (default: every folder in `personas/`). The harness launches the backend with `PERSONAS` set to the same list. In external mode, the operator must start the backend with that same `PERSONAS` list |
| Fake search | `FakeSearchServer` loads every persona's `menuItems.json` and routes by the index name in the request path |
| Data-driven scenarios | Ordering, pricing, happy-hour and combo theories take the persona as a parameter (`MemberData`) and read `testdata/personas/<id>/golden-*.json` |
| Persona-specific scenarios | Tagged `[Trait("persona", "<id>")]`: Route 44 and combos (Sonic), meal auto-fill, `modify` and meal numbers (McDonald's), extras split and 25% happy hour (Dunkin) |
| Not-applicable versus missing | A scenario that needs a capability (`bundles`, `happyHour`, `extras`, `dayparts`) is skipped for a persona only when the capability is absent from that pack. A `PersonaCoverageTests` fact fails if any (persona, capability) pair has no scenario, so a skip can never hide missing coverage |
| Transport, security, resume | Run once against the default persona. A `PersonaSmokeTests` theory per persona checks that the bootstrap `session.update` carries that pack's instructions, voice and tools, runs one search against that pack's index, and runs one `update_order` |
| New contract scenarios | Unknown persona gets 404; `persona_mismatch` on resume; `/api/personas` shape; metadata carries `persona` |
| Data checks | Every pack validates against the schemas; each golden table matches its menu fields; no `offMenu` rule sets `comboSlot: "sides"` |
| Brand guards | `test_rebrand_verification.py` and `locales.test.ts` are inverted: brand words may appear only in their own pack, and no pack mentions another brand |
| Money | Unchanged: `0.000001m` tolerance for Python and exact for C# |
| CI | Same `conformance-gate`. If the run gets too long, shard by persona trait in the matrix; the gate still requires every shard |

## 8. Frontend changes

This is the explicit exception to the "no frontend changes" rule (epic #6).

| # | Change | Files today |
| --- | --- | --- |
| F1 | `PersonaProvider`: read `?persona=`, fetch `/api/personas/<id>`, set CSS variables and `data-persona` on `<html>`, set the title and favicon, merge the pack's i18n strings over the shared locale files | new `context/persona-context.tsx`, `index.tsx`, `index.html` |
| F2 | Replace every hard-coded brand hex (87 in Sonic) with theme tokens (Tailwind `hsl(var(--primary))` and similar); support light and dark tokens | `App.tsx`, `order-summary.tsx`, `menu-panel.tsx`, `index.css` |
| F3 | Hero, callouts, legal line, source link title and ticket headings come from the manifest; rename `SonicApp` to `App` | `App.tsx`, `order-summary.tsx` |
| F4 | The menu panel fetches `menuUrl` at runtime instead of importing at build time; show the daypart toggle when `features.dayparts` is set (McDonald's breakfast and lunch) | `menu-panel.tsx`, new `menu-mode-context.tsx` (from McD) |
| F5 | The ticket renders `components` for bundles when present | `order-summary.tsx`, `types.ts` |
| F6 | Persona picker in the header, shown only when more than one persona is enabled. Choosing one navigates to `?persona=<id>`; if the ticket has items, it asks first | `App.tsx` |
| F7 | Per-persona resume key (`drivethru.resumeId.<persona>`), apology clip URL and demo data; the voice picker's default comes from the pack | `useRealtime.tsx`, `lib/apology.ts`, `App.tsx`, `settings.tsx` |
| F8 | The WebSocket URL becomes `/realtime?persona=<id>` | `useRealtime.tsx` |
| F9 | Remove the dead Azure Speech toggle (row 38) | `settings.tsx`, `azure-speech-context.tsx`, `useAzureSpeech.tsx` |

Fonts: the pack's `importUrl` must be on `fonts.googleapis.com` (the loader enforces this), so a pack cannot
inject arbitrary stylesheets.

## 9. Infrastructure, deployment and cost

- **Container app:** one, in `rg-sonic-demo` (Q10), with `PERSONAS=sonic,mcdonalds,dunkin` (per Q2) and
  `DEFAULT_PERSONA=sonic`. The current Sonic URL keeps working and opens as Sonic. Single-brand deployments
  are the same image with a one-entry allow-list.
- **Search:** the three existing indexes on the shared free service. No new index is needed, and the free
  tier's three-index cap would not allow one anyway. While the sibling demos are still live, they and the
  unified app share these indexes. P2-10 keeps document ids and fields compatible, and schema changes are
  additive only.
- **OpenAI:** one realtime deployment for all personas. The per-worker session cap (`max_concurrent_sessions:
  10`) is shared across personas.
- **Entra:** one redirect URI for the unified hostname. S7 adds one for the C# app.
- **Docker:** both Dockerfiles copy `personas/` to `/app/personas`. The frontend no longer bundles menu data.
- **azd hooks:** ingestion and the postdeploy smoke loop over `PERSONAS`.

## 10. Where the unified app lives, and the sibling repos

**Here, in `swigerb/SonicAIDriveThru`.** It has the conformance suite and the `conformance-gate`, the most
hardened backend, the prompt loader, the exact-money work, and the C# issues (#12 to #18). Renaming it once it
hosts three brands is Q8.

| Phase | McDonald's and Dunkin repos |
| --- | --- |
| Now until P2 is deployed and signed off | Stay live. Security fixes only (McDonald's #6, Dunkin #11). No new features. |
| Parity sign-off (P2-12) | README banner redirecting to the unified app with `?persona=mcdonalds` or `?persona=dunkin`; a `final-standalone` tag; GitHub archive (read-only). |
| After a grace period (Q9) | Delete the sibling container apps (`rg-mcd-demo`, `rg-dunkin-demo`). **Keep** their Search indexes, since the unified app uses them. |
| Later | Brand-only features (local mode, crew dashboard, edge) come back only through new issues here, persona-agnostic, if Brian wants them (Q7). |

## 11. P2 implementation breakdown

Sizes: S is up to 1 dev-day, M is 2 to 3, L is 3 to 5. Total: about 28 to 36 dev-days. With the parallel lanes
below, the critical path (P2-1, P2-2, P2-3, P2-5, P2-6, P2-10, P2-11, P2-12) is about 3 to 4 weeks.

| ID | Work item | Owner | Size | Depends on | Acceptance criteria |
| --- | --- | --- | --- | --- | --- |
| P2-1 | Persona pack skeleton and loader, Sonic only, no behavior change | Summer | M | Brian's approval | `personas/sonic/` holds today's prompts, menu and assets; both schemas exist; `PersonaCatalog` validates at startup and fails fast; `/health` lists personas; the Dockerfile copies `personas/`; every existing pytest, vitest and conformance test green **with no scenario edits** |
| P2-2 | #51: per-item menu fields and data-driven classification, Sonic | Summer | M | P2-1 | Name tables gone from `menu_utils.py`; Sonic's menu carries the 4.3 fields; `offMenu` rules in `persona.json`; category fallback word-bounded, deltas pinned; golden-versus-data check; conformance unchanged; mutation: flipping one field fails the golden check and one conformance row |
| P2-3 | Per-session persona binding in Python | Summer | L | P2-2 | Section 5.2 contract implemented; business rules, timezone, search client, voice default, greeting, nudge, role name and texts come from the session's persona; new contract scenarios green; Sonic unchanged when `?persona` is omitted |
| P2-4 | Conformance persona dimension | Birdperson (Beth reviews the harness) | L | P2-1 for the harness, P2-3 for multi-persona runs | Section 7 in place; every existing ordering theory runs for each applicable persona; `PersonaCoverageTests` and data checks green; brand guards inverted; CI time is recorded, and sharded if needed |
| P2-5 | Shared bundle and extras engines | Summer | M | P2-3 | `absorb` and `autoFill` slots, standalone conversion, in-place resize (`modify`), additive `components` on `OrderItem`; one extras guard with structured rejection and combined-name split for all personas (pytest refusal-text assertions move to the structured shape; Unity checks the model still relays the refusal); Sonic golden unchanged; order-summary wire schema in the conformance README updated |
| P2-6 | McDonald's persona pack | Summer (rules, data), Unity (prompts) | L | P2-5 | Pack ported from McD `cb8cfb5`; meals auto-fill fries by size (Hash Browns at breakfast); `meal_numbers` strategy reading `mealNumber`; extras rules; happy hour per Q5; McD golden files ported from its `test_order_logic.py`, `test_menu_utils.py` and `test_extras_rules.py`; every deliberate deviation from the sibling listed in the golden `description` |
| P2-7 | Dunkin persona pack | Summer (rules, data), Unity (prompts) | M | P2-5 | Prompt moved from the `app.py` string to YAML; extras $0.50 / $0.75 / $1.00 on lattes and cold beverages only; happy hour 14:00 to 17:00 America/New_York at x 0.75, announced or silent per Q6; Dunkin golden files ported from `test_happy_hour.py`, `test_extras_rules.py` and `test_update_order_result.py` |
| P2-8 | Frontend runtime persona and theming | Morty | L | P2-3 (API) | F1 to F9 done; no brand hex left in `app/frontend/src`; vitest per persona; one Playwright browser scenario per persona (theme, menu, one order); existing vitest green |
| P2-9 | Voices, greetings, clips and prompt review | Unity | M | P2-6, P2-7 | Pack voice defaults; `generate_apology_clips.py --persona` with whisper-verified clips; `smoke_realtime.py --persona` green for all three against the live app; no pack mentions another brand or the wrong role name |
| P2-10 | Ingestion and Search per persona | Summer (script), Squanchy (hook) | M | P2-6, P2-7 | `setup_search_index.py --persona` and a loop over `PERSONAS`; superset schema; stable document ids so the live sibling demos keep working; notebooks write to `personas/<id>/menu/`; document counts 60, 71 and 16 |
| P2-11 | Infra and deployment of the unified app | Squanchy | M | P2-8, P2-10 | Persona env vars in Bicep and parameters; both Dockerfiles copy packs; postdeploy smoke loops over personas; deployed per Q10; `conformance-gate` green; the existing Sonic URL opens as Sonic |
| P2-12 | Docs, parity sign-off, sibling cutover | Rick (with Scribe) | S | P2-11 | README section "Choose a persona"; an "Adding a persona" guide; this doc updated to as-built; a parity checklist (every row in section 3 marked done or dropped) signed off by Brian; siblings redirected and archived per Q9 |

Parallel lanes after P2-3: backend brand packs (P2-5 to P2-7), frontend (P2-8), tests (P2-4), then voices and
infra (P2-9 to P2-11).

## 12. What this means for the C# port

The C# port starts after P2 lands (roadmap, 2026-09-25) and ports the unified app once.

| Issue | Change from the current issue text |
| --- | --- |
| #12 S2 skeleton | Loads `personas/` through `PersonaCatalog` (not `app/backend/prompts/`); the same schemas; `/api/personas`, the `?persona=` binding and the 404 for unknown personas; `docs/dotnet_mapping.md` maps `personas.py` to `PersonaCatalog` and `Persona` |
| #13 S3 middle tier | Session config (instructions, voice, greeting, nudge, role name) comes from the session's `Persona` |
| #14 S4 tools and orders | Ports the data-driven engines (sizes, bundles, extras, happy hour, off-menu rules) and the `meal_numbers` strategy. There are no name tables to transcribe. Golden files per persona must match to the cent |
| #15 S5 sessions | Resume binds the persona (`persona_mismatch`) |
| #16 S6 tooling | Ingestion and clip generation take `--persona`; the McDonald's converter and notebook (`build_mcdonalds_menu.py`, `mcdonalds_menu_ingestion_search.ipynb`) join the inventory |
| #17 S7 | One C# container app serving every enabled persona, not one per brand |
| #18 S8 | The A/B runs the scripted orders for each persona |
| #21 | Becomes "the persona-dimensioned suite is green against C#", with no separate port |

## 13. Risks

| # | Risk | Mitigation |
| --- | --- | --- |
| R1 | The refactor from module globals to a per-session persona regresses hardened Sonic code | P2-1 to P2-3 are Sonic-only and behavior-neutral, behind the full existing suite with no scenario edits |
| R2 | A sibling brand rule is lost or changed in the port | Golden files come from the siblings' own tests; parity checklist in P2-12; Brian answers Q5 and Q6 before P2-6 and P2-7 |
| R3 | We copy sibling bugs (substring keywords) or silently fix them | Port intent, not bugs. Every deviation is a named golden row |
| R4 | Unified ingestion changes indexes that the live sibling demos still use | Stable document ids, additive schema only, `AZURE_SEARCH_SKIP_INDEX_SETUP` per persona until cutover |
| R5 | Prompt or brand leakage (a McDonald's crew member says "carhop") | Inverted brand guards; per-persona smoke; Unity's review in P2-9 |
| R6 | Theming is larger than it looks (76 to 114 hard-coded colors per repo) | Its own L-sized item; a vitest guard with no hex in `src/` |
| R7 | Conformance run time roughly triples for ordering scenarios | Only data-driven theories multiply; shard by persona if needed |
| R8 | Off-menu regexes behave differently in Python and .NET | Portable subset, validated at load; golden off-menu rows run against both backends |
| R9 | Trademark optics of three real brands in one demo | Per-pack legal line; allow-list per deployment (Q2) |
| R10 | A dropped side feature turns out to be demo-critical | Q7; the code stays in the archived repos' history |
| R11 | Sibling data quirks (for example McD's Big Mac Meal is filed under "Chicken & McNuggets®") affect extras and upsell | Reviewed while porting each menu in P2-6; fixes recorded in the golden description |

## 14. Open questions for Brian

1. **Switching model.** Approve per-session selection (`?persona=` plus a picker) inside a per-deployment
   allow-list (`PERSONAS`, `DEFAULT_PERSONA=sonic`)? The alternative is one deployment per brand.
2. **Main URL.** Should the main demo URL enable all three, or only the Inspire Brands pair (Sonic and
   Dunkin), with McDonald's on its own single-persona deployment (one more app)?
3. **#64, floats.** Full price or half price at happy hour? And should a float fill the combo drink slot?
   (Section 6 shows both as data.)
4. **Off-menu fallback.** Keep the word-bounded keyword rules as persona data (today's behavior, conformance
   unchanged)? Or remove them so any off-menu item is full price and never absorbed (simpler, but about a
   dozen pinned conformance rows flip, for example an off-menu "Dr Pepper Zero" or "Cherry Slushes" would stop
   filling the combo drink slot and lose the happy-hour price)?
5. **McDonald's happy hour.** The sibling half-prices every "drink" from 14:00 to 16:00, which by substring
   includes shakes and McFlurry. Keep a McDonald's happy hour? If yes, for which categories?
6. **Dunkin happy hour.** It is silent today: prices drop 25% from 14:00 to 17:00 Eastern on lattes and cold
   drinks, but the crew member never says so. Keep it silent, or announce it like Sonic?
7. **Brand-only features.** OK to leave McDonald's local mode, Dunkin's crew dashboard and CRM simulator, and
   Dunkin's Azure Local edge stack out of the unified app? Or should any of them come back later as a
   persona-agnostic option?
8. **Repo name.** Keep `SonicAIDriveThru`, or rename it (for example `AIDriveThru`) once it hosts three
   brands? GitHub keeps redirects.
9. **Sibling cutover.** After parity sign-off, archive both repos read-only with a redirect README and a
   `final-standalone` tag, and delete their container apps (keeping the Search indexes) after a grace period.
   How long should the grace period be?
10. **Deploy target.** Turn `rg-sonic-demo` into the unified app in place (the Sonic URL keeps working and
    defaults to Sonic), or stand up a new environment?

## 15. Recommendation

Approve ADR-001 as written. Answer Q1, Q2 and Q4 before P2-1 starts, and Q5 and Q6 before P2-6 and P2-7.
Q3 (#64) can land any time as one data row. Then file P2-1 to P2-12 as issues under #20, and update the C#
issues (#12 to #18, #21) with the changes in section 12.
