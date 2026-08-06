#!/usr/bin/env python3
"""Extract production menu items from sonic-menu-items.json using the SAME
logic as sonic_menu_ingestion_search.ipynb.  Outputs a formatted report of
every item in the Azure AI Search index grouped by category, then performs
a gap analysis against the UI's menuItems.json."""

import json
import os
import re
from collections import Counter

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)

POS_DATA_PATH = os.path.join(
    REPO_ROOT, "app", "frontend", "src", "data", "sonic-menu-items.json"
)
UI_MENU_PATH = os.path.join(
    REPO_ROOT, "app", "frontend", "src", "data", "menuItems.json"
)


# ── helpers (identical to the notebook) ──────────────────────────────────

def collect_products_from_category(cat_id, categories, leaf_name=None):
    """Recursively traverse categories, collecting (product_id, leaf_category_name) tuples."""
    cat = categories.get(cat_id)
    if not cat:
        return []
    current_name = leaf_name or cat.get("displayName", cat_id)
    result = []
    for ref in cat.get("childRefs", {}).keys():
        if ref.startswith("categories."):
            sub_cat_id = ref[len("categories."):]
            sub_cat = categories.get(sub_cat_id)
            sub_name = sub_cat.get("displayName", sub_cat_id) if sub_cat else sub_cat_id
            result.extend(
                collect_products_from_category(sub_cat_id, categories, sub_name)
            )
        elif ref.startswith("products."):
            prod_id = ref[len("products."):]
            result.append((prod_id, current_name))
    return result


def normalize_size_name(child_display_name, parent_display_name):
    """Extract clean size label from verbose product-specific display name."""
    prefixes = [
        ("Mini ", "Mini"),
        ("Sm ", "Small"),
        ("Med ", "Medium"),
        ("Lg ", "Large"),
        ("RT 44", "RT 44"),
        ("Rt. 44", "RT 44"),
    ]
    for prefix, label in prefixes:
        if child_display_name.startswith(prefix):
            return label
    stripped = child_display_name.replace(parent_display_name, "").strip()
    stripped = stripped.replace("\u00ae", "").strip()
    if stripped:
        return stripped
    return "Standard"


def get_size_variants(product, products, product_groups):
    """Resolve size variants for a product from relatedProducts.alternatives."""
    alternatives = product.get("relatedProducts", {}).get("alternatives", {})
    sizes = []
    for group_ref in alternatives.keys():
        if group_ref.startswith("productGroups."):
            group_id = group_ref[len("productGroups."):]
            group = product_groups.get(group_id)
            if not group:
                continue
            for child_ref in group.get("childRefs", {}).keys():
                if child_ref.startswith("products."):
                    child_id = child_ref[len("products."):]
                    child_product = products.get(child_id)
                    if child_product:
                        sizes.append(
                            {
                                "size": normalize_size_name(
                                    child_product.get("displayName", child_id),
                                    product.get("displayName", ""),
                                ),
                                "price": child_product.get("price", 0.0),
                            }
                        )
    return sizes


# ── main extraction ──────────────────────────────────────────────────────

def extract_production_items():
    with open(POS_DATA_PATH, encoding="utf-8") as f:
        raw_data = json.load(f)

    menu = list(raw_data["menus"].values())[0]
    products = menu["products"]
    categories = menu["categories"]
    product_groups = menu["productGroups"]

    # Find top-level categories
    child_cat_ids = set()
    for cat in categories.values():
        for ref in cat.get("childRefs", {}).keys():
            if ref.startswith("categories."):
                child_cat_ids.add(ref[len("categories."):])
    top_level_cats = [cid for cid in categories.keys() if cid not in child_cat_ids]

    # Collect all product→category mappings
    product_category_map = {}
    for cat_id in top_level_cats:
        for prod_id, leaf_cat_name in collect_products_from_category(cat_id, categories):
            if prod_id not in product_category_map:
                product_category_map[prod_id] = leaf_cat_name

    # Build structured items (skip recipes, same as notebook)
    structured = []
    for prod_id, cat_name in product_category_map.items():
        product = products.get(prod_id)
        if not product:
            continue
        if product.get("isRecipe", False):
            continue

        size_variants = get_size_variants(product, products, product_groups)
        if not size_variants:
            price = product.get("price", 0.0) or 0.0
            size_variants = [{"size": "Standard", "price": price}]

        structured.append(
            {
                "productId": prod_id,
                "name": product.get("displayName", prod_id),
                "description": product.get("description", ""),
                "imageUrl": product.get("imageUrl", ""),
                "category": cat_name,
                "sizes": size_variants,
            }
        )

    return structured


def load_ui_items():
    with open(UI_MENU_PATH, encoding="utf-8") as f:
        data = json.load(f)
    items = []
    for group in data.get("menuItems", []):
        cat = group["category"]
        for item in group["items"]:
            items.append({"name": item["name"], "category": cat})
    return items


def normalize(name: str) -> str:
    """Lowercase, strip trademark symbols, collapse whitespace for comparison."""
    name = name.lower()
    name = name.replace("\u00ae", "").replace("\u2122", "")
    name = re.sub(r"[^a-z0-9 ]", " ", name)
    return " ".join(name.split())


# ── reporting ────────────────────────────────────────────────────────────

def main():
    production = extract_production_items()
    ui_items = load_ui_items()

    # ── Production report ────────────────────────────────────────────
    print("=" * 70)
    print("  PRODUCTION ITEMS IN AZURE AI SEARCH INDEX")
    print("  (Extracted using same logic as sonic_menu_ingestion_search.ipynb)")
    print("=" * 70)

    by_category: dict[str, list] = {}
    for item in production:
        by_category.setdefault(item["category"], []).append(item)

    cat_counts = Counter(item["category"] for item in production)
    print(f"\nTotal production items: {len(production)}")
    print(f"Total categories: {len(cat_counts)}\n")

    for cat, count in cat_counts.most_common():
        print(f"\n{'─' * 60}")
        print(f"  {cat}  ({count} items)")
        print(f"{'─' * 60}")
        for item in sorted(by_category[cat], key=lambda x: x["name"]):
            sizes_str = ", ".join(
                f"{s['size']}=${s['price']:.2f}" for s in item["sizes"]
            )
            print(f"  • {item['name']}")
            print(f"    Sizes: {sizes_str}")

    # ── Gap analysis ─────────────────────────────────────────────────
    prod_names = {normalize(i["name"]) for i in production}
    ui_names = {normalize(i["name"]) for i in ui_items}

    in_ui_not_prod = sorted(ui_names - prod_names)
    in_prod_not_ui = sorted(prod_names - ui_names)

    print("\n" + "=" * 70)
    print("  GAP ANALYSIS: UI menuItems.json vs Production Index")
    print("=" * 70)

    print(f"\n  Production index items: {len(prod_names)}")
    print(f"  UI sidebar items:      {len(ui_names)}")
    print(f"  Overlap:               {len(prod_names & ui_names)}")

    print(f"\n  ⛔ In UI but NOT in production ({len(in_ui_not_prod)}):")
    if in_ui_not_prod:
        for n in in_ui_not_prod:
            print(f"     • {n}")
    else:
        print("     (none — UI is clean)")

    print(f"\n  ✅ In production but NOT in UI ({len(in_prod_not_ui)}):")
    if in_prod_not_ui:
        for n in in_prod_not_ui:
            print(f"     • {n}")
    else:
        print("     (none)")

    # Return data for programmatic use
    return {
        "production": production,
        "ui_items": ui_items,
        "in_ui_not_prod": in_ui_not_prod,
        "in_prod_not_ui": in_prod_not_ui,
        "prod_names": prod_names,
        "by_category": by_category,
    }


if __name__ == "__main__":
    main()
