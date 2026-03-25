import gzip
import logging
import os
import sys
from pathlib import Path

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import AzureDeveloperCliCredential, DefaultAzureCredential
from dotenv import load_dotenv

from config_loader import get_config
from prompt_loader import PromptLoader
from rtmt import RTMiddleTier, create_hmac_token
from tools import attach_tools_rtmt

# Production: INFO; override with LOG_LEVEL env var for debugging
_log_level = os.environ.get("LOG_LEVEL", "INFO").upper()
logging.basicConfig(level=getattr(logging, _log_level, logging.INFO))
logger = logging.getLogger(__name__)

# Load centralized config
_config = get_config()
_compression_cfg = _config.get("compression", {})

# Minimum response size worth compressing (bytes)
_COMPRESS_MIN_SIZE = _compression_cfg.get("min_size_bytes", 256)
# Cache-Control for immutable hashed assets (JS/CSS bundles from Vite)
_STATIC_IMMUTABLE_MAX_AGE = _compression_cfg.get("static_immutable_max_age", 31_536_000)
# Cache-Control for mutable files (index.html, etc.)
_STATIC_DEFAULT_MAX_AGE = _compression_cfg.get("static_default_max_age", 3600)
# Compressible content-type substrings
_COMPRESSIBLE_TYPES = ("text/", "application/json", "application/javascript", "image/svg")

# App version — exposed via /health endpoint
_APP_VERSION = "1.0.0"

# Required environment variables for the app to function
_REQUIRED_ENV_VARS = [
    "AZURE_OPENAI_EASTUS2_ENDPOINT",
    "AZURE_OPENAI_REALTIME_DEPLOYMENT",
    "AZURE_SEARCH_ENDPOINT",
    "AZURE_SEARCH_INDEX",
]

# Startup validation state — read by /health endpoint
_startup_checks = {
    "prompts_loaded": False,
    "config_loaded": True,  # validated at module load by get_config()
    "env_vars": False,
}


def _get_bool_env(variable_name: str, default: bool = False) -> bool:
    """Parse boolean environment variables with predictable defaults."""
    value = os.environ.get(variable_name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


# ---------------------------------------------------------------------------
# Middleware
# ---------------------------------------------------------------------------

@web.middleware
async def _compression_middleware(request: web.Request, handler):
    """Gzip-compress eligible responses when the client accepts it."""
    response = await handler(request)

    # Only compress regular Response objects (not FileResponse, StreamResponse, WebSocket)
    if not isinstance(response, web.Response) or isinstance(response, web.WebSocketResponse):
        return response
    if response.body is None or len(response.body) < _COMPRESS_MIN_SIZE:
        return response

    accept_encoding = request.headers.get("Accept-Encoding", "")
    if "gzip" not in accept_encoding:
        return response

    content_type = response.content_type or ""
    if not any(ct in content_type for ct in _COMPRESSIBLE_TYPES):
        return response

    compressed = gzip.compress(response.body, compresslevel=_compression_cfg.get("level", 6))
    if len(compressed) >= len(response.body):
        return response

    response.body = compressed
    response.headers["Content-Encoding"] = "gzip"
    response.headers["Vary"] = "Accept-Encoding"
    return response


# ---------------------------------------------------------------------------
# Static file helpers
# ---------------------------------------------------------------------------

async def _index_handler(_request: web.Request) -> web.FileResponse:
    current_directory = Path(__file__).parent
    resp = web.FileResponse(current_directory / "static" / "index.html")
    resp.headers["Cache-Control"] = "no-cache"
    return resp


async def _health_handler(_request: web.Request) -> web.Response:
    all_ok = all(_startup_checks.values())
    return web.json_response(
        {
            "status": "healthy" if all_ok else "unhealthy",
            "version": _APP_VERSION,
            "checks": _startup_checks,
        },
        status=200 if all_ok else 503,
    )


async def _check_service_connectivity() -> None:
    """Verify Azure service endpoints are reachable. Non-blocking — logs warnings only."""
    endpoints = {
        "Azure OpenAI": os.environ.get("AZURE_OPENAI_EASTUS2_ENDPOINT"),
        "Azure Search": os.environ.get("AZURE_SEARCH_ENDPOINT"),
    }
    timeout = aiohttp.ClientTimeout(total=5)
    try:
        async with aiohttp.ClientSession(timeout=timeout) as session:
            for name, url in endpoints.items():
                if not url:
                    continue
                try:
                    async with session.get(url, ssl=True) as resp:
                        logger.info("✅ %s reachable (HTTP %d)", name, resp.status)
                except Exception as exc:
                    logger.warning("⚠️ %s unreachable at %s — %s (non-fatal)", name, url, exc)
    except Exception as exc:
        logger.warning("⚠️ Service connectivity check failed — %s (non-fatal)", exc)


async def create_app() -> web.Application:
    """Configure and return the aiohttp application for realtime ordering."""

    if not _get_bool_env("RUNNING_IN_PRODUCTION", False):
        logger.info("Running in development mode; loading values from .env")
        load_dotenv()

    # ── Startup Validation ────────────────────────────────────────────────

    # 1. Validate required environment variables
    missing_vars = [v for v in _REQUIRED_ENV_VARS if not os.environ.get(v)]
    if missing_vars:
        logger.critical(
            "FATAL: Missing required environment variables: %s", ", ".join(missing_vars)
        )
        sys.exit(1)
    _startup_checks["env_vars"] = True

    # 2. Load prompts from YAML (fail-fast on missing/malformed files)
    try:
        prompt_loader = PromptLoader(brand="sonic")
    except (FileNotFoundError, ValueError) as exc:
        logger.critical("FATAL: Failed to load prompts — %s", exc)
        sys.exit(1)
    _startup_checks["prompts_loaded"] = True

    # 3. Optional: verify Azure service connectivity (non-blocking)
    await _check_service_connectivity()

    env_count = len(_REQUIRED_ENV_VARS)
    logger.info(
        "✅ Startup validation passed: prompts loaded, config valid, %d/%d env vars set",
        env_count,
        env_count,
    )

    # ── App Configuration ─────────────────────────────────────────────────

    model_cfg = _config.get("model", {})
    conn_cfg = _config.get("connection", {})

    llm_endpoint = os.environ.get("AZURE_OPENAI_EASTUS2_ENDPOINT")
    llm_deployment = os.environ.get("AZURE_OPENAI_REALTIME_DEPLOYMENT")

    llm_key = os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY")
    search_key = os.environ.get("AZURE_SEARCH_API_KEY")

    credential = None
    if not llm_key or not search_key:
        if tenant_id := os.environ.get("AZURE_TENANT_ID"):
            logger.info("Using AzureDeveloperCliCredential with tenant_id %s", tenant_id)
            credential = AzureDeveloperCliCredential(tenant_id=tenant_id, process_timeout=60)
        else:
            logger.info("Using DefaultAzureCredential")
            credential = DefaultAzureCredential()

    llm_credential = AzureKeyCredential(llm_key) if llm_key else credential
    search_credential = AzureKeyCredential(search_key) if search_key else credential

    app = web.Application(
        middlewares=[_compression_middleware],
        client_max_size=conn_cfg.get("client_max_size_bytes", 4 * 1024 * 1024),
    )

    rtmt = RTMiddleTier(
        credentials=llm_credential,
        endpoint=llm_endpoint,
        deployment=llm_deployment,
        voice_choice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or model_cfg.get("default_voice", "coral"),
        prompt_loader=prompt_loader,
    )
    # Generate a random secret for HMAC session tokens
    app_secret = os.urandom(32)
    rtmt.app_secret = app_secret
    if api_version := os.environ.get("AZURE_OPENAI_REALTIME_API_VERSION"):
        rtmt.api_version = api_version
    else:
        rtmt.api_version = model_cfg.get("api_version", "2024-10-01-preview")
    rtmt.temperature = model_cfg.get("temperature", 0.6)
    rtmt.max_tokens = model_cfg.get("max_response_output_tokens", 4096)
    rtmt.system_message = prompt_loader.get_system_prompt()

    attach_tools_rtmt(
        rtmt,
        credentials=search_credential,
        search_endpoint=os.environ.get("AZURE_SEARCH_ENDPOINT"),
        search_index=os.environ.get("AZURE_SEARCH_INDEX"),
        # Defaults aligned with the menu ingestion index schema; override via env vars as needed.
        semantic_configuration=os.environ.get("AZURE_SEARCH_SEMANTIC_CONFIGURATION") or "menuSemanticConfig",
        identifier_field=os.environ.get("AZURE_SEARCH_IDENTIFIER_FIELD") or "id",
        content_field=os.environ.get("AZURE_SEARCH_CONTENT_FIELD") or "description",
        embedding_field=os.environ.get("AZURE_SEARCH_EMBEDDING_FIELD") or "embedding",
        title_field=os.environ.get("AZURE_SEARCH_TITLE_FIELD") or "name",
        use_vector_query=_get_bool_env("AZURE_SEARCH_USE_VECTOR_QUERY", True),
        prompt_loader=prompt_loader,
    )

    rtmt.attach_to_app(app, "/realtime")

    # ── HMAC Session Token Endpoint (Task 4) ──
    async def get_session_token(_request: web.Request) -> web.Response:
        token = create_hmac_token(app_secret, expiry_seconds=900)
        return web.json_response({"token": token})

    current_directory = Path(__file__).parent
    app.add_routes([
        web.get('/', _index_handler),
        web.get('/health', _health_handler),
        web.get('/api/auth/session', get_session_token),
    ])
    app.router.add_static(
        '/',
        path=current_directory / 'static',
        name='static',
        append_version=True,
    )

    async def _on_startup(app: web.Application):
        rtmt.start_background_tasks()
        logger.info("Background tasks started (token refresh, idle checker)")

    async def _on_shutdown(app: web.Application):
        logger.info("Graceful shutdown initiated — cleaning up active sessions")
        rtmt.stop_background_tasks()

    app.on_startup.append(_on_startup)
    app.on_shutdown.append(_on_shutdown)

    return app


if __name__ == "__main__":
    host = os.environ.get("HOST", "localhost")
    port = int(os.environ.get("PORT", 8000))
    conn_cfg = _config.get("connection", {})
    web.run_app(
        create_app(),
        host=host,
        port=port,
        shutdown_timeout=conn_cfg.get("shutdown_timeout", 10.0),
        keepalive_timeout=conn_cfg.get("keepalive_timeout", 75.0),
    )
