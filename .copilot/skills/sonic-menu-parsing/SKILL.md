# Skill: Sonic Menu Data Parsing

## When to Use
Whenever you need to parse the production `sonic-menu-items.json` data — for ingestion, analysis, or any backend/tool code that works with menu items.

## Data Source
`app/frontend/src/data/sonic-menu-items.json` (UTF-8 encoded, ~1.3MB)

## Structure
```
data['menus'][<menu-key>] = {
  'products': dict (1334 items, keyed by product ID),
  'categories': dict (43 items, keyed by category ID),
  'productGroups': dict (503 items, keyed by group ID),
  'displayName': str
}
```

## Key Patterns

### 1. Recursive Category Traversal
Categories can nest — `childRefs` may contain `"categories.<id>"` pointing to subcategories.
```python
def collect_products_from_category(cat_id, categories, leaf_name=None):
    cat = categories.get(cat_id)
    if not cat: return []
    current_name = leaf_name or cat.get('displayName', cat_id)
    result = []
    for ref in cat.get('childRefs', {}).keys():
        if ref.startswith('categories.'):
            sub_id = ref[len('categories.'):]
            sub_cat = categories.get(sub_id)
            sub_name = sub_cat.get('displayName', sub_id) if sub_cat else sub_id
            result.extend(collect_products_from_category(sub_id, categories, sub_name))
        elif ref.startswith('products.'):
            result.append((ref[len('products.'):], current_name))
    return result
```

### 2. Finding Top-Level Categories
Exclude any category referenced as a `categories.` childRef of another:
```python
child_cat_ids = set()
for cat in categories.values():
    for ref in cat.get('childRefs', {}).keys():
        if ref.startswith('categories.'):
            child_cat_ids.add(ref[len('categories.'):])
top_level = [cid for cid in categories if cid not in child_cat_ids]
```

### 3. Size Variant Resolution
Products → `relatedProducts.alternatives` → keys like `"productGroups.<id>"` → look up group → iterate `childRefs` → each child is a sized product with its own `displayName` and `price`.

### 4. Filtering
Only 172 of 1334 products are referenced from categories. All 172 are `isRecipe: False`. Always filter `isRecipe is False` for safety.

## Stats (as of March 2026)
- 172 non-recipe products across 27 leaf categories
- 86 have size alternatives, 86 don't
- Largest category: Combos (28 items)
- Must open file with `encoding='utf-8'` on Windows (contains special chars like ® and &)
