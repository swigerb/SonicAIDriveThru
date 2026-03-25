import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from app import _get_bool_env


class GetBoolEnvTests(unittest.TestCase):
    """Tests for the _get_bool_env helper that parses boolean env vars."""

    def test_returns_default_when_unset(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertFalse(_get_bool_env("MISSING_VAR", False))
            self.assertTrue(_get_bool_env("MISSING_VAR", True))

    def test_truthy_values(self):
        for value in ("1", "true", "True", "TRUE", "yes", "Yes", "YES", "on", "On", "ON"):
            with patch.dict(os.environ, {"TEST_VAR": value}):
                self.assertTrue(_get_bool_env("TEST_VAR", False), f"Expected True for '{value}'")

    def test_falsy_values(self):
        for value in ("0", "false", "False", "no", "off", "maybe", ""):
            with patch.dict(os.environ, {"TEST_VAR": value}):
                self.assertFalse(_get_bool_env("TEST_VAR", False), f"Expected False for '{value}'")

    def test_whitespace_is_stripped(self):
        with patch.dict(os.environ, {"TEST_VAR": "  true  "}):
            self.assertTrue(_get_bool_env("TEST_VAR", False))

    def test_default_parameter_defaults_to_false(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertFalse(_get_bool_env("MISSING_VAR"))


class CreateAppConfigTests(unittest.IsolatedAsyncioTestCase):
    """Tests for create_app voice choice and system prompt configuration."""

    async def _run_create_app(self):
        """Run create_app with mocked Azure services; return (class_mock, instance_mock)."""
        with patch("app.RTMiddleTier") as mock_cls, \
             patch("app.attach_tools_rtmt"), \
             patch("app._check_service_connectivity", new_callable=AsyncMock), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-4o-realtime",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "test-index",
                 "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
             }):
            mock_instance = MagicMock()
            mock_cls.return_value = mock_instance
            from app import create_app
            await create_app()
            return mock_cls, mock_instance

    async def test_default_voice_is_coral(self):
        mock_cls, _ = await self._run_create_app()
        _, kwargs = mock_cls.call_args
        self.assertEqual(kwargs["voice_choice"], "coral")

    async def test_system_prompt_contains_carhop_closing(self):
        _, mock_instance = await self._run_create_app()
        self.assertIn(
            "Your carhop will have that right out",
            mock_instance.system_message,
        )

    async def test_system_prompt_contains_get_order_tool_instruction(self):
        _, mock_instance = await self._run_create_app()
        self.assertIn("get_order", mock_instance.system_message)


class HealthEndpointTests(unittest.IsolatedAsyncioTestCase):
    """Tests for the /health endpoint response structure."""

    async def test_health_returns_200_when_all_checks_pass(self):
        from app import _health_handler, _startup_checks
        original = dict(_startup_checks)
        _startup_checks.update(prompts_loaded=True, config_loaded=True, env_vars=True)
        try:
            response = await _health_handler(MagicMock())
            self.assertEqual(response.status, 200)
            body = json.loads(response.body)
            self.assertEqual(body["status"], "healthy")
            self.assertEqual(body["version"], "1.0.0")
            self.assertTrue(all(body["checks"].values()))
        finally:
            _startup_checks.update(original)

    async def test_health_returns_503_when_check_fails(self):
        from app import _health_handler, _startup_checks
        original = dict(_startup_checks)
        _startup_checks.update(prompts_loaded=False, config_loaded=True, env_vars=True)
        try:
            response = await _health_handler(MagicMock())
            self.assertEqual(response.status, 503)
            body = json.loads(response.body)
            self.assertEqual(body["status"], "unhealthy")
            self.assertFalse(body["checks"]["prompts_loaded"])
        finally:
            _startup_checks.update(original)

    async def test_health_response_has_required_fields(self):
        from app import _health_handler, _startup_checks
        original = dict(_startup_checks)
        _startup_checks.update(prompts_loaded=True, config_loaded=True, env_vars=True)
        try:
            response = await _health_handler(MagicMock())
            body = json.loads(response.body)
            self.assertIn("status", body)
            self.assertIn("version", body)
            self.assertIn("checks", body)
            self.assertIn("prompts_loaded", body["checks"])
            self.assertIn("config_loaded", body["checks"])
            self.assertIn("env_vars", body["checks"])
        finally:
            _startup_checks.update(original)


if __name__ == "__main__":
    unittest.main()
