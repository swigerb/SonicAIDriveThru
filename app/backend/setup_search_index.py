"""
setup_search_index.py — Headless Azure AI Search index setup for Sonic menu items.

Reads menu items from app/frontend/src/data/menuItems.json, generates embeddings
using Azure OpenAI text-embedding-3-large, creates/updates the search index with
an AzureOpenAIVectorizer (for VectorizableTextQuery at query time), and uploads
documents.

Idempotent: safe to re-run. Uses DefaultAzureCredential for AAD authentication.
"""

import json
import logging
import os
import re
import subprocess
import sys
from pathlib import Path

from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from azure.search.documents import SearchClient
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    AzureOpenAIVectorizer,
    AzureOpenAIVectorizerParameters,
    HnswAlgorithmConfiguration,
    HnswParameters,
    SearchField,
    SearchFieldDataType,
    SearchIndex,
    SemanticConfiguration,
    SemanticField,
    SemanticPrioritizedFields,
    SemanticSearch,
    SimpleField,
    VectorSearch,
    VectorSearchAlgorithmMetric,
    VectorSearchProfile,
)
from dotenv import load_dotenv
from openai import AzureOpenAI
from rich.logging import RichHandler

logging.basicConfig(
    level=logging.WARNING,
    format="%(message)s",
    datefmt="[%X]",
    handlers=[RichHandler(rich_tracebacks=True)],
)
logger = logging.getLogger("voicerag")
logger.setLevel(logging.INFO)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
EMBEDDING_MODEL = "text-embedding-3-large"
EMBEDDING_DIMENSIONS = 3072
MENU_DATA_PATH = Path(__file__).resolve().parent.parent / "frontend" / "src" / "data" / "menuItems.json"


def load_azd_env():
    """Load the default azd environment file via python-dotenv."""
    result = subprocess.run(
        "azd env list -o json", shell=True, capture_output=True, text=True
    )
    if result.returncode != 0:
        raise RuntimeError("Error loading azd env")
    env_json = json.loads(result.stdout)
    env_file_path = None
    for entry in env_json:
        if entry["IsDefault"]:
            env_file_path = entry["DotEnvPath"]
    if not env_file_path:
        raise RuntimeError("No default azd env file found")
    logger.info("Loading azd env from %s", env_file_path)
    load_dotenv(env_file_path, override=True)


def sanitize_key(key: str) -> str:
    """Sanitize a document key to contain only valid characters."""
    return re.sub(r"[^a-zA-Z0-9_\-]", "_", key)


def create_or_update_index(
    index_client: SearchIndexClient,
    index_name: str,
    openai_endpoint: str,
    embedding_deployment: str,
) -> None:
    """Create or update the search index with the menu item schema."""
    index = SearchIndex(
        name=index_name,
        fields=[
            SimpleField(
                name="id",
                type=SearchFieldDataType.String,
                key=True,
                sortable=True,
                filterable=True,
            ),
            SearchField(
                name="category",
                type=SearchFieldDataType.String,
                sortable=True,
                filterable=True,
                facetable=True,
            ),
            SearchField(
                name="name",
                type=SearchFieldDataType.String,
                sortable=True,
                filterable=True,
                facetable=True,
            ),
            SearchField(name="description", type=SearchFieldDataType.String),
            SearchField(name="longDescription", type=SearchFieldDataType.String),
            SearchField(
                name="origin",
                type=SearchFieldDataType.String,
                filterable=True,
            ),
            SearchField(
                name="caffeineContent",
                type=SearchFieldDataType.String,
                filterable=True,
            ),
            SearchField(
                name="brewingMethod",
                type=SearchFieldDataType.String,
                filterable=True,
            ),
            SearchField(
                name="popularity",
                type=SearchFieldDataType.String,
                filterable=True,
                facetable=True,
            ),
            SearchField(
                name="sizes",
                type=SearchFieldDataType.String,
                filterable=False,
                facetable=False,
            ),
            SearchField(
                name="embedding",
                type=SearchFieldDataType.Collection(SearchFieldDataType.Single),
                vector_search_dimensions=EMBEDDING_DIMENSIONS,
                vector_search_profile_name="menuHnswProfile",
            ),
        ],
        vector_search=VectorSearch(
            algorithms=[
                HnswAlgorithmConfiguration(
                    name="menuHnsw",
                    parameters=HnswParameters(
                        metric=VectorSearchAlgorithmMetric.COSINE,
                        m=10,
                        ef_construction=200,
                    ),
                ),
            ],
            profiles=[
                VectorSearchProfile(
                    name="menuHnswProfile",
                    algorithm_configuration_name="menuHnsw",
                    vectorizer_name="menuVectorizer",
                ),
            ],
            vectorizers=[
                AzureOpenAIVectorizer(
                    vectorizer_name="menuVectorizer",
                    parameters=AzureOpenAIVectorizerParameters(
                        resource_url=openai_endpoint,
                        deployment_name=embedding_deployment,
                        model_name=EMBEDDING_MODEL,
                    ),
                ),
            ],
        ),
        semantic_search=SemanticSearch(
            configurations=[
                SemanticConfiguration(
                    name="menuSemanticConfig",
                    prioritized_fields=SemanticPrioritizedFields(
                        title_field=SemanticField(field_name="name"),
                        content_fields=[
                            SemanticField(field_name="description"),
                            SemanticField(field_name="longDescription"),
                            SemanticField(field_name="category"),
                        ],
                    ),
                ),
            ],
        ),
    )

    # create_or_update_index is idempotent — updates schema if index exists
    index_client.create_or_update_index(index)
    logger.info("Index '%s' created or updated successfully", index_name)


def prepare_documents(menu_data: dict) -> tuple[list[dict], list[str]]:
    """Transform menu JSON into search documents and embedding input texts."""
    documents = []
    texts_for_embedding = []

    for category_group in menu_data["menuItems"]:
        category_name = category_group["category"]
        for item in category_group["items"]:
            doc_id = sanitize_key(
                f"{category_name}_{item['name'].replace(' ', '_')}".lower()
            )
            combined_text = (
                f"{category_name} {item['name']} "
                f"{item['description']} {item.get('longDescription', '')}"
            )
            texts_for_embedding.append(combined_text)
            documents.append(
                {
                    "id": doc_id,
                    "category": category_name,
                    "name": item["name"],
                    "description": item["description"],
                    "longDescription": item.get("longDescription", ""),
                    "origin": item.get("origin", ""),
                    "caffeineContent": item.get("caffeineContent", ""),
                    "brewingMethod": item.get("brewingMethod", ""),
                    "popularity": item.get("popularity", ""),
                    "sizes": json.dumps(item["sizes"]),
                }
            )

    return documents, texts_for_embedding


def generate_embeddings(
    openai_client: AzureOpenAI, texts: list[str], deployment: str
) -> list[list[float]]:
    """Generate embeddings in batch using Azure OpenAI."""
    response = openai_client.embeddings.create(input=texts, model=deployment)
    return [item.embedding for item in response.data]


def upload_documents(
    search_client: SearchClient, documents: list[dict]
) -> None:
    """Upload documents to the search index using merge_or_upload (idempotent)."""
    batch_size = 100
    for i in range(0, len(documents), batch_size):
        batch = documents[i : i + batch_size]
        result = search_client.merge_or_upload_documents(batch)
        succeeded = sum(1 for r in result if r.succeeded)
        logger.info(
            "Uploaded batch %d-%d: %d/%d succeeded",
            i,
            i + len(batch),
            succeeded,
            len(batch),
        )


def main() -> None:
    load_azd_env()

    logger.info("Checking if we need to set up Azure AI Search index...")
    # AZURE_SEARCH_REUSE_EXISTING is an *infrastructure* flag — it tells Bicep not
    # to provision a new search service. It must NOT skip index setup: the free
    # SKU allows one service but three indexes per subscription, so sibling demos
    # share one service while each owning its own index. Conflating the two left
    # a demo pointing at an index that was never created.
    if os.environ.get("AZURE_SEARCH_SKIP_INDEX_SETUP") == "true":
        logger.info(
            "AZURE_SEARCH_SKIP_INDEX_SETUP is set — leaving the index untouched."
        )
        return

    # Read configuration from environment
    index_name = os.environ["AZURE_SEARCH_INDEX"]
    search_endpoint = os.environ["AZURE_SEARCH_ENDPOINT"]
    openai_endpoint = os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]
    embedding_deployment = os.environ.get(
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", EMBEDDING_MODEL
    )

    logger.info("Setting up Azure AI Search index '%s'...", index_name)

    # Authenticate with DefaultAzureCredential (works with azd auth + managed identity)
    credential = DefaultAzureCredential()
    token_provider = get_bearer_token_provider(
        credential, "https://cognitiveservices.azure.com/.default"
    )

    # Load menu data
    if not MENU_DATA_PATH.exists():
        logger.critical("Menu data file not found at %s", MENU_DATA_PATH)
        sys.exit(1)
    with open(MENU_DATA_PATH, encoding="utf-8") as f:
        menu_data = json.load(f)
    logger.info("Loaded menu data from %s", MENU_DATA_PATH)

    # Create or update the index
    index_client = SearchIndexClient(search_endpoint, credential)
    create_or_update_index(
        index_client, index_name, openai_endpoint, embedding_deployment
    )

    # Prepare documents and generate embeddings
    documents, texts_for_embedding = prepare_documents(menu_data)
    logger.info("Prepared %d documents for indexing", len(documents))

    openai_client = AzureOpenAI(
        azure_ad_token_provider=token_provider,
        api_version="2024-06-01",
        azure_endpoint=openai_endpoint,
    )
    logger.info("Generating embeddings for %d documents...", len(documents))
    embeddings = generate_embeddings(openai_client, texts_for_embedding, embedding_deployment)
    for i, emb in enumerate(embeddings):
        documents[i]["embedding"] = emb
    logger.info("Embeddings generated successfully")

    # Upload documents
    search_client = SearchClient(search_endpoint, index_name, credential)
    upload_documents(search_client, documents)
    logger.info("Search index setup complete!")


if __name__ == "__main__":
    main()
