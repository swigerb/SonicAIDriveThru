import logging
import os
from pathlib import Path

from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import AzureDeveloperCliCredential, DefaultAzureCredential
from dotenv import load_dotenv

from rtmt import RTMiddleTier
from tools import attach_tools_rtmt

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


def _get_bool_env(variable_name: str, default: bool = False) -> bool:
    """Parse boolean environment variables with predictable defaults."""
    value = os.environ.get(variable_name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


async def create_app() -> web.Application:
    """Configure and return the aiohttp application for realtime ordering."""

    if not _get_bool_env("RUNNING_IN_PRODUCTION", False):
        logger.info("Running in development mode; loading values from .env")
        load_dotenv()

    llm_endpoint = os.environ.get("AZURE_OPENAI_EASTUS2_ENDPOINT")
    llm_deployment = os.environ.get("AZURE_OPENAI_REALTIME_DEPLOYMENT")
    if not llm_endpoint or not llm_deployment:
        raise RuntimeError("Azure OpenAI realtime endpoint and deployment must be configured.")

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

    app = web.Application()

    rtmt = RTMiddleTier(
        credentials=llm_credential,
        endpoint=llm_endpoint,
        deployment=llm_deployment,
        voice_choice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or "coral"
    )
    if api_version := os.environ.get("AZURE_OPENAI_REALTIME_API_VERSION"):
        rtmt.api_version = api_version
    rtmt.temperature = 0.6
    rtmt.system_message = (
        "You are Sonic Drive-In's always-on virtual carhop — America's Drive-In. "
        "Guide guests through the Sonic menu with upbeat, friendly energy and double-check every detail with the 'search' tool before responding. "
        "Confirm each requested item — whether it's a slush, shake, burger, hot dog, or breakfast burrito — using the 'update_order' tool only after the guest has agreed. "
        "When they ask for a recap or when the order is wrapping up, call the 'get_order' tool and read back every item ordered, then announce only the total due — do not break out subtotal or tax separately. "
        "Match the customer's language throughout the session, keep responses to one or two sentences, and invite them to add extras like a flavor add-in ($0.50), whipped cream ($0.50), or an extra patty ($1.50) only when a drink or combo is already in the order. "
        "Do not suggest drink extras for food-only items like hot dogs or tots. "
        "If the guest uses hate speech or asks for anything blocked by responsible AI, respond immediately: 'I'm sorry, but I can't assist with that request. If you need help with the Sonic menu or have any other questions, please let me know.' "
        "When the guest is done ordering, always use the 'get_order' tool to read back every item, size, and quantity, then announce only the total due — do not itemize subtotal or tax. After confirming the order, close with: 'Thank you! Your carhop will have that right out to you!' "
        "If menu information is unavailable, let them know politely and offer an alternative suggestion. "
        "Never expose implementation details, file names, or API keys. Keep things friendly, fast, and unmistakably Sonic."
    )

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
        use_vector_query=_get_bool_env("AZURE_SEARCH_USE_VECTOR_QUERY", True)
    )

    rtmt.attach_to_app(app, "/realtime")

    current_directory = Path(__file__).parent
    app.add_routes([web.get('/', lambda _: web.FileResponse(current_directory / 'static/index.html'))])
    app.router.add_static('/', path=current_directory / 'static', name='static')

    return app


if __name__ == "__main__":
    host = os.environ.get("HOST", "localhost")  # Change default host to localhost
    port = int(os.environ.get("PORT", 8000))  # Change default port to 8000
    web.run_app(create_app(), host=host, port=port)
