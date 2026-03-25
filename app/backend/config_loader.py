"""Centralized config loader for Sonic AI Drive-Thru backend.

Loads config.yaml once at startup and exposes it via get_config().
Fail-fast: raises on missing or malformed config file.
"""

import os
from pathlib import Path
from typing import Any

import yaml

__all__ = ["get_config", "reload_config"]

_CONFIG_PATH = Path(__file__).parent / "config.yaml"
_cache: dict[str, Any] | None = None


def _load() -> dict[str, Any]:
    if not _CONFIG_PATH.exists():
        raise FileNotFoundError(f"Config file not found: {_CONFIG_PATH}")
    with _CONFIG_PATH.open("r", encoding="utf-8") as f:
        data = yaml.safe_load(f)
    if not isinstance(data, dict):
        raise ValueError(f"Config file must be a YAML mapping, got {type(data).__name__}")
    # Validate required top-level sections
    required = {"model", "business_rules", "cache", "audio", "connection"}
    missing = required - set(data.keys())
    if missing:
        raise ValueError(f"Config file missing required sections: {missing}")
    return data


def get_config() -> dict[str, Any]:
    """Return the cached config dict. Loads from disk on first call."""
    global _cache
    if _cache is None:
        _cache = _load()
    return _cache


def reload_config() -> dict[str, Any]:
    """Force-reload config from disk. Used by DEV_MODE hot-reload."""
    global _cache
    _cache = _load()
    return _cache
