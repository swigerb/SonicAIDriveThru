"""Prompt loader tests for Sonic AI Drive-Thru.

Validates Phase 1 PromptLoader: YAML loading, required-section validation,
Jinja2 template rendering, get_system_prompt(), get_tool_schemas(), and
error edge cases.
"""

import json
import os
import sys
import textwrap
from pathlib import Path
from unittest.mock import patch

import pytest
import yaml

# Ensure the backend package is importable
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from prompt_loader import PromptLoader


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture
def brand_dir(tmp_path):
    """Create a temporary brand directory with valid YAML prompt files."""
    brand = tmp_path / "prompts" / "testbrand"
    brand.mkdir(parents=True)

    # Manifest
    (brand / "manifest.yaml").write_text(yaml.dump({
        "version": "1.0.0",
        "brand": "testbrand",
        "files": {
            "system_prompt": "system_prompt.yaml",
            "greeting": "greeting.yaml",
            "tool_schemas": "tool_schemas.yaml",
            "error_messages": "error_messages.yaml",
            "hints": "hints.yaml",
        },
    }), encoding="utf-8")

    # System prompt
    (brand / "system_prompt.yaml").write_text(yaml.dump({
        "version": "1.0.0",
        "sections": [
            {"name": "IDENTITY", "priority": 1, "content": "You are TestBot."},
            {"name": "RULES", "priority": 2, "content": "Be helpful."},
        ],
    }), encoding="utf-8")

    # Greeting
    (brand / "greeting.yaml").write_text(yaml.dump({
        "version": "1.0.0",
        "greeting": {
            "type": "conversation.item.create",
            "item": {
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": "Hello!"}],
            },
        },
    }), encoding="utf-8")

    # Tool schemas
    (brand / "tool_schemas.yaml").write_text(yaml.dump({
        "version": "1.0.0",
        "tools": [
            {"name": "search", "type": "function", "description": "Search menu."},
            {"name": "update_order", "type": "function", "description": "Update order."},
        ],
    }), encoding="utf-8")

    # Error messages (with Jinja2 template)
    (brand / "error_messages.yaml").write_text(yaml.dump({
        "version": "1.0.0",
        "messages": {
            "not_found": "Sorry, I could not find {{ item_name }}.",
            "limit_hit": "Max {{ max_qty }} per item.",
        },
    }), encoding="utf-8")

    # Hints
    (brand / "hints.yaml").write_text(yaml.dump({
        "upsell_hints": {
            "combo": {
                "hint": "Make it a combo?",
                "trigger_categories": ["burger", "sandwich"],
            },
        },
        "delta_templates": {
            "item_added": "Added {{quantity}} {{display_name}}.",
            "item_removed": "Removed {{quantity}} {{display_name}}.",
        },
    }), encoding="utf-8")

    return brand


@pytest.fixture
def loader(brand_dir):
    """Return a PromptLoader pointed at the temp brand directory."""
    with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
        return PromptLoader(brand="testbrand")


# ===========================================================================
# Loading valid files
# ===========================================================================

class TestLoadValidPrompts:
    def test_loader_initialises_without_error(self, loader):
        assert loader is not None

    def test_get_system_prompt_returns_string(self, loader):
        prompt = loader.get_system_prompt()
        assert isinstance(prompt, str)
        assert len(prompt) > 0

    def test_system_prompt_contains_all_sections(self, loader):
        prompt = loader.get_system_prompt()
        assert "You are TestBot." in prompt
        assert "Be helpful." in prompt

    def test_system_prompt_sections_ordered_by_priority(self, loader):
        prompt = loader.get_system_prompt()
        identity_pos = prompt.index("You are TestBot.")
        rules_pos = prompt.index("Be helpful.")
        assert identity_pos < rules_pos

    def test_get_greeting_returns_dict(self, loader):
        greeting = loader.get_greeting()
        assert isinstance(greeting, dict)
        assert greeting["type"] == "conversation.item.create"

    def test_get_greeting_json_str(self, loader):
        raw = loader.get_greeting_json_str()
        parsed = json.loads(raw)
        assert parsed["type"] == "conversation.item.create"

    def test_get_tool_schemas_returns_list_of_dicts(self, loader):
        schemas = loader.get_tool_schemas()
        assert isinstance(schemas, list)
        assert len(schemas) == 2
        for s in schemas:
            assert isinstance(s, dict)
            assert "name" in s
            assert "type" in s

    def test_tool_schemas_have_expected_names(self, loader):
        names = {s["name"] for s in loader.get_tool_schemas()}
        assert "search" in names
        assert "update_order" in names

    def test_get_error_messages_returns_dict(self, loader):
        msgs = loader.get_error_messages()
        assert isinstance(msgs, dict)
        assert "not_found" in msgs
        assert "limit_hit" in msgs

    def test_get_hints_returns_dict(self, loader):
        hints = loader.get_hints()
        assert isinstance(hints, dict)
        assert "upsell_hints" in hints


# ===========================================================================
# Missing file errors
# ===========================================================================

class TestMissingFiles:
    def test_missing_brand_directory_raises(self, tmp_path):
        with patch("prompt_loader._PROMPTS_DIR", tmp_path):
            with pytest.raises(FileNotFoundError, match="Prompt directory not found"):
                PromptLoader(brand="nonexistent")

    def test_missing_manifest_raises(self, tmp_path):
        brand = tmp_path / "badbrand"
        brand.mkdir()
        with patch("prompt_loader._PROMPTS_DIR", tmp_path):
            with pytest.raises(FileNotFoundError, match="manifest.yaml not found"):
                PromptLoader(brand="badbrand")

    def test_missing_system_prompt_file_raises(self, brand_dir):
        (brand_dir / "system_prompt.yaml").unlink()
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(FileNotFoundError, match="System prompt file not found"):
                PromptLoader(brand="testbrand")

    def test_missing_greeting_file_raises(self, brand_dir):
        (brand_dir / "greeting.yaml").unlink()
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(FileNotFoundError, match="Greeting file not found"):
                PromptLoader(brand="testbrand")

    def test_missing_tool_schemas_file_raises(self, brand_dir):
        (brand_dir / "tool_schemas.yaml").unlink()
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(FileNotFoundError, match="Tool schemas file not found"):
                PromptLoader(brand="testbrand")


# ===========================================================================
# Validation errors (missing required sections)
# ===========================================================================

class TestValidationErrors:
    def test_empty_sections_list_raises(self, brand_dir):
        (brand_dir / "system_prompt.yaml").write_text(
            yaml.dump({"version": "1.0.0", "sections": []}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="must have a 'sections' list"):
                PromptLoader(brand="testbrand")

    def test_missing_sections_key_raises(self, brand_dir):
        (brand_dir / "system_prompt.yaml").write_text(
            yaml.dump({"version": "1.0.0"}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="must have a 'sections' list"):
                PromptLoader(brand="testbrand")

    def test_greeting_without_type_raises(self, brand_dir):
        (brand_dir / "greeting.yaml").write_text(
            yaml.dump({"version": "1.0.0", "greeting": {"item": {}}}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="must have a 'type' field"):
                PromptLoader(brand="testbrand")

    def test_greeting_missing_key_raises(self, brand_dir):
        (brand_dir / "greeting.yaml").write_text(
            yaml.dump({"version": "1.0.0"}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="must have a 'greeting' key"):
                PromptLoader(brand="testbrand")

    def test_tool_schemas_empty_list_raises(self, brand_dir):
        (brand_dir / "tool_schemas.yaml").write_text(
            yaml.dump({"version": "1.0.0", "tools": []}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="non-empty 'tools' list"):
                PromptLoader(brand="testbrand")

    def test_tool_without_name_raises(self, brand_dir):
        (brand_dir / "tool_schemas.yaml").write_text(
            yaml.dump({"version": "1.0.0", "tools": [{"type": "function"}]}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="missing 'name'"):
                PromptLoader(brand="testbrand")

    def test_tool_without_type_raises(self, brand_dir):
        (brand_dir / "tool_schemas.yaml").write_text(
            yaml.dump({"version": "1.0.0", "tools": [{"name": "foo"}]}), encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="missing 'type'"):
                PromptLoader(brand="testbrand")

    def test_malformed_yaml_raises(self, brand_dir):
        (brand_dir / "system_prompt.yaml").write_text(
            "sections:\n  - [broken", encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises((ValueError, yaml.YAMLError)):
                PromptLoader(brand="testbrand")

    def test_non_dict_yaml_raises(self, brand_dir):
        (brand_dir / "system_prompt.yaml").write_text(
            "- item1\n- item2\n", encoding="utf-8"
        )
        with patch("prompt_loader._PROMPTS_DIR", brand_dir.parent):
            with pytest.raises(ValueError, match="must be a YAML mapping"):
                PromptLoader(brand="testbrand")


# ===========================================================================
# Jinja2 template rendering
# ===========================================================================

class TestJinja2Rendering:
    def test_render_error_with_variables(self, loader):
        result = loader.render_error("not_found", item_name="Cherry Limeade")
        assert "Cherry Limeade" in result
        assert "could not find" in result.lower() or "Cherry Limeade" in result

    def test_render_error_with_numeric_variable(self, loader):
        result = loader.render_error("limit_hit", max_qty=10)
        assert "10" in result

    def test_render_error_unknown_key_returns_fallback(self, loader):
        result = loader.render_error("does_not_exist")
        assert "error occurred" in result.lower() or "does_not_exist" in result

    def test_render_template_method(self, loader):
        result = loader.render_template(
            "Hello {{ name }}, you ordered {{ count }} items.",
            name="Brian",
            count=3,
        )
        assert "Brian" in result
        assert "3" in result

    def test_render_delta_template_add(self, loader):
        template = loader.get_delta_template("add")
        result = loader.render_template(
            template,
            quantity=2,
            display_name="Cherry Limeade",
            total="8.99",
        )
        assert "2" in result
        assert "Cherry Limeade" in result

    def test_render_delta_template_remove(self, loader):
        template = loader.get_delta_template("remove")
        result = loader.render_template(
            template,
            quantity=1,
            display_name="Tots",
            total="5.49",
        )
        assert "Tots" in result


# ===========================================================================
# Production prompts smoke test
# ===========================================================================

class TestProductionPrompts:
    """Smoke test against the real Sonic prompts on disk."""

    def test_sonic_loader_initialises(self):
        loader = PromptLoader(brand="sonic")
        assert loader is not None

    def test_sonic_system_prompt_non_empty(self):
        loader = PromptLoader(brand="sonic")
        prompt = loader.get_system_prompt()
        assert isinstance(prompt, str)
        assert len(prompt) > 100  # realistic prompt is hundreds of chars

    def test_sonic_tool_schemas_non_empty(self):
        loader = PromptLoader(brand="sonic")
        schemas = loader.get_tool_schemas()
        assert isinstance(schemas, list)
        assert len(schemas) >= 3  # search, update_order, get_order, reset_order

    def test_sonic_tool_schemas_are_dicts(self):
        loader = PromptLoader(brand="sonic")
        for s in loader.get_tool_schemas():
            assert isinstance(s, dict)
            assert "name" in s

    def test_sonic_greeting_has_type(self):
        loader = PromptLoader(brand="sonic")
        greeting = loader.get_greeting()
        assert greeting["type"] == "conversation.item.create"

    def test_sonic_error_messages_populated(self):
        loader = PromptLoader(brand="sonic")
        msgs = loader.get_error_messages()
        assert isinstance(msgs, dict)
        assert len(msgs) > 0

    def test_sonic_upsell_hint_for_burger(self):
        loader = PromptLoader(brand="sonic")
        hint = loader.get_upsell_hint("burger")
        # Should return a non-empty hint string (combo suggestion)
        assert isinstance(hint, str)


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
