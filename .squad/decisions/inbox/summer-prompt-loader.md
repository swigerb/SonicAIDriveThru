# Summer: Prompt & Config Loader Infrastructure (2026-03-25)

## Decision
Built YAML-driven prompt and config externalization infrastructure with backward-compatible fallbacks.

## Architecture
1. **`config.yaml`** — All 25+ magic numbers from app.py, rtmt.py, tools.py, order_state.py centralized. Loaded once at startup via `config_loader.get_config()`.
2. **`prompt_loader.py`** — Manifest-driven loader for `prompts/{brand}/` directory. Validates sections, caches in memory, supports Jinja2 templates for error messages, optional DEV_MODE hot-reload.
3. **Backward compatibility** — Every call site has a hardcoded fallback when `_prompt_loader is None`. Tests that don't go through `create_app()` still work.

## Key Decisions
- **Module-level `_prompt_loader` pattern**: Set by `attach_tools_rtmt()` at startup. Handler functions check `if _prompt_loader:` before using YAML values. This avoids passing the loader through every function signature.
- **Manifest.yaml as entry point**: Unity's file structure uses a manifest listing all YAML files. Loader discovers files via manifest, not by convention. New brands just need a new manifest.
- **Jinja2 StrictUndefined**: Error templates use `StrictUndefined` so missing variables raise immediately rather than silently producing empty strings.
- **Config as dict, not dataclass**: Kept as plain dict for simplicity. If the config grows, consider Pydantic BaseSettings.

## Files Changed
- `app/backend/prompt_loader.py` — NEW
- `app/backend/config_loader.py` — NEW
- `app/backend/config.yaml` — NEW
- `app/backend/app.py` — Imports loaders, system prompt from YAML, config values from config.yaml
- `app/backend/rtmt.py` — Greeting from YAML, echo/connection from config
- `app/backend/tools.py` — Tool schemas, errors, hints from YAML; cache/search/quantity from config
- `app/backend/order_state.py` — Tax rate, happy hour config from config.yaml
- `app/backend/requirements.txt` — Added pyyaml, jinja2
- `app/backend/tests/test_rebrand_verification.py` — Updated to read prompt from YAML

## Impact
- Prompts editable without code changes (Brian or Unity can tune)
- Config changes don't require deploy (just container restart)
- All 125 tests pass — behavior identical to pre-refactor
