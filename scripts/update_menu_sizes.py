"""
Update menuItems.json to include all size variants from production data.
Adds Mini and RT 44 sizes to drink/slush/shake/blast items.
"""
import json
import re
from pathlib import Path

DATA_DIR = Path(__file__).resolve().parent.parent / "app" / "frontend" / "src" / "data"
PRODUCTION_FILE = DATA_DIR / "sonic-menu-items.json"
MENU_FILE = DATA_DIR / "menuItems.json"

SIZE_PREFIXES = [
    ("Mini ", "mini"),
    ("Sm ", "small"),
    ("Small ", "small"),
    ("Med ", "medium"),
    ("Medium ", "medium"),
    ("Lg ", "large"),
    ("Large ", "large"),
    (r"RT 44\u00ae ", "rt 44"),
    ("RT 44® ", "rt 44"),
]

SIZE_ORDER = ["mini", "small", "medium", "large", "rt 44"]

# Map menuItems.json names to search terms in production data
PRODUCT_SEARCH_MAP = {
    "Cherry Limeade": "Cherry Limeade",
    "Blue Raspberry Slush": "Blue Raspberry Slush",
    "Ocean Water®": "Ocean Water",
    "Oreo® Peanut Butter Shake": "OREO® Peanut Butter Master Shake",
    "Classic Vanilla Shake": "Vanilla Classic Shake",
    "SONIC Blast® with M&M'S®": "SONIC Blast® made with M&M",
}


def load_production_products():
    with open(PRODUCTION_FILE, "r", encoding="utf-8") as f:
        data = json.load(f)
    menu = list(data["menus"].values())[0]
    return menu.get("products", {})


def extract_size(display_name):
    """Return (normalized_size, base_product_name) or None."""
    for prefix, size_key in SIZE_PREFIXES:
        if display_name.startswith(prefix):
            base = display_name[len(prefix):]
            return size_key, base
    return None, display_name


def find_sizes_for_product(products, search_term):
    """Find all sized variants of a product in production data."""
    search_lower = search_term.lower()
    sizes = {}
    for prod in products.values():
        name = prod.get("displayName", "")
        price = prod.get("price", 0)
        if price <= 0:
            continue
        if search_lower not in name.lower():
            continue
        size_key, base = extract_size(name)
        if size_key is None:
            continue
        # Avoid picking up unrelated products (e.g. "Cherry Limeade Slush" when searching "Cherry Limeade")
        # For Cherry Limeade, exclude Slush variants; for Blue Raspberry Slush, include only exact
        if search_term == "Cherry Limeade" and "slush" in base.lower():
            continue
        if search_term == "Cherry Limeade" and "diet" in base.lower():
            continue
        if search_term == "Ocean Water" and "diet" in name.lower():
            continue
        if size_key not in sizes:
            sizes[size_key] = price
    return sizes


def update_menu():
    products = load_production_products()

    with open(MENU_FILE, "r", encoding="utf-8") as f:
        menu_data = json.load(f)

    updated_count = 0

    for category in menu_data["menuItems"]:
        for item in category["items"]:
            name = item["name"]
            if name not in PRODUCT_SEARCH_MAP:
                continue

            search_term = PRODUCT_SEARCH_MAP[name]
            prod_sizes = find_sizes_for_product(products, search_term)

            if not prod_sizes:
                print(f"  SKIP {name}: no production data found")
                continue

            # Build new sizes array in correct order
            new_sizes = []
            for size_key in SIZE_ORDER:
                if size_key in prod_sizes:
                    new_sizes.append({"size": size_key, "price": prod_sizes[size_key]})

            old_size_keys = {s["size"] for s in item["sizes"]}
            new_size_keys = {s["size"] for s in new_sizes}
            if new_size_keys != old_size_keys or any(
                s["price"] != next((n["price"] for n in new_sizes if n["size"] == s["size"]), None)
                for s in item["sizes"] if s["size"] in new_size_keys
            ):
                old_count = len(item["sizes"])
                item["sizes"] = new_sizes
                updated_count += 1
                print(f"  UPDATED {name}: {old_count} -> {len(new_sizes)} sizes: {[s['size'] for s in new_sizes]}")
            else:
                print(f"  SKIP {name}: already up to date")

    with open(MENU_FILE, "w", encoding="utf-8") as f:
        json.dump(menu_data, f, indent=4, ensure_ascii=False)
        f.write("\n")

    print(f"\nUpdated {updated_count} items in menuItems.json")


if __name__ == "__main__":
    update_menu()
