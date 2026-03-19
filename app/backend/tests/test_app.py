import os
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

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
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-4o-realtime",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
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
            "Your carhop will have that right out to you",
            mock_instance.system_message,
        )

    async def test_system_prompt_contains_get_order_tool_instruction(self):
        _, mock_instance = await self._run_create_app()
        self.assertIn("get_order", mock_instance.system_message)


if __name__ == "__main__":
    unittest.main()
