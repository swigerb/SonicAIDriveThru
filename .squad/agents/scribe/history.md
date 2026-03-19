# Project Context

- **Project:** SonicAIDriveThru
- **Created:** 2026-03-19

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-03-19  
📌 Sonic Rebrand project completed 2026-03-19T04-06:
  - Rick: Scope analysis (~100+ refs identified)
  - Morty: Frontend UI overhaul (13 tests pass)
  - Summer: Backend rebrand (69 tests pass)
  - Birdperson: Verification tests (12 tests created, all pass)

## Learnings

- **Sonic Rebrand**: Frontend theme (CSS custom properties, Nunito Sans, Sonic colors), backend system prompts rewritten as carhop persona, menu data replaced, verification tests cover all source files for forbidden terms.
- **Menu category coupling**: tools.py MENU_CATEGORY_MAP loads from frontend menuItems.json at init; ALLOWED/BLOCKED must include both JSON names and keyword-inferred fallbacks.
- **Decision documentation**: Merged 4 inbox decisions into decisions.md; cleaned up inbox folder.
