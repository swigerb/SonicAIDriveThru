"""Deployment prerequisites for order resume (plan step 0).

Resume state (orders, resume credentials, the detached-session grace hold) is
in-process memory, so the container must run exactly one worker process and
ingress must pin a browser to one replica. The HMAC session-token secret must be
shared across replicas/restarts instead of os.urandom per process.
"""

import json
import re
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from app import load_app_secret
from rtmt import create_hmac_token, validate_hmac_token

REPO = Path(__file__).resolve().parents[3]
DOCKERFILE = REPO / "app" / "Dockerfile"
MAIN_BICEP = REPO / "infra" / "main.bicep"
CONTAINER_APP_BICEP = REPO / "infra" / "core" / "host" / "container-app.bicep"
UPSERT_BICEP = REPO / "infra" / "core" / "host" / "container-app-upsert.bicep"
PARAMS = REPO / "infra" / "main.parameters.json"


def _backend_module_block(text: str) -> str:
    start = text.index("module acaBackend")
    end = text.index("\nvar ", start)
    return text[start:end]


class DockerfileWorkerTests(unittest.TestCase):

    def test_gunicorn_runs_exactly_one_worker(self):
        text = DOCKERFILE.read_text(encoding="utf-8").replace("\\\r\n", " ").replace("\\\n", " ")
        cmd = text[text.index("CMD ["):]
        args = json.loads(cmd[len("CMD "):cmd.index("]") + 1])
        self.assertIn("gunicorn", args)
        self.assertEqual(args[args.index("--workers") + 1], "1",
                         "a second worker has its own order state: resume would miss ~50% of reconnects")
        self.assertEqual(args.count("--workers"), 1)
        self.assertNotIn("-w", args)


class StickyIngressTests(unittest.TestCase):

    def test_backend_container_app_uses_sticky_affinity(self):
        block = _backend_module_block(MAIN_BICEP.read_text(encoding="utf-8"))
        self.assertRegex(block, r"stickySessionsAffinity:\s*'sticky'")

    def test_affinity_reaches_the_ingress_block(self):
        upsert = UPSERT_BICEP.read_text(encoding="utf-8")
        self.assertRegex(upsert, r"stickySessionsAffinity:\s*stickySessionsAffinity")
        app = CONTAINER_APP_BICEP.read_text(encoding="utf-8")
        ingress = app[app.index("ingress: ingressEnabled ?"):app.index("dapr:")]
        self.assertRegex(ingress, r"stickySessions:\s*\{\s*affinity:\s*stickySessionsAffinity\s*\}")

    def test_single_revision_mode_and_api_version_support_sticky(self):
        app = CONTAINER_APP_BICEP.read_text(encoding="utf-8")
        self.assertRegex(app, r"param revisionMode string = 'Single'")
        self.assertNotRegex(_backend_module_block(MAIN_BICEP.read_text(encoding="utf-8")), r"revisionMode")
        version = re.search(r"resource app 'Microsoft\.App/containerApps@([0-9-]+)(-preview)?'", app).group(1)
        self.assertGreaterEqual(version, "2023-05-02", "ingress.stickySessions needs API 2023-05-02-preview or later")

    def test_replica_bounds_unchanged(self):
        block = _backend_module_block(MAIN_BICEP.read_text(encoding="utf-8"))
        self.assertRegex(block, r"containerMinReplicas:\s*1\b")
        self.assertRegex(block, r"containerMaxReplicas:\s*5\b")


class SessionSecretWiringTests(unittest.TestCase):

    def test_secret_is_a_container_app_secret_mapped_to_env(self):
        block = _backend_module_block(MAIN_BICEP.read_text(encoding="utf-8"))
        self.assertRegex(block, r"'app-session-secret':\s*effectiveAppSessionSecret")
        self.assertRegex(block, r"APP_SESSION_SECRET:\s*'app-session-secret'")
        upsert = UPSERT_BICEP.read_text(encoding="utf-8")
        self.assertIn("secretRef: secretEnv[key]", upsert)

    def test_secret_has_a_generated_fallback_and_an_azd_override(self):
        text = MAIN_BICEP.read_text(encoding="utf-8")
        self.assertRegex(text, r"@secure\(\)[^\n]*\n@description[^\n]*\nparam appSessionSecret string = ''")
        self.assertRegex(text, r"param appSessionSecretFallback string = '\$\{newGuid\(\)\}\$\{newGuid\(\)\}'")
        params = json.loads(PARAMS.read_text(encoding="utf-8"))["parameters"]
        self.assertEqual(params["appSessionSecret"]["value"], "${APP_SESSION_SECRET=}")
        self.assertNotIn("appSessionSecretFallback", params)

    def test_out_of_band_auth_secret_is_preserved(self):
        block = _backend_module_block(MAIN_BICEP.read_text(encoding="utf-8"))
        self.assertIn("preserveExistingSecretNames: enableAuth && empty(authClientSecret) ? [ 'aad-client-secret' ] : []", block)


class LoadAppSecretTests(unittest.TestCase):

    def test_env_secret_is_used_and_stable_across_processes(self):
        env = {"APP_SESSION_SECRET": "x" * 48}
        a, b = load_app_secret(env), load_app_secret(env)
        self.assertEqual(a, b)
        self.assertTrue(validate_hmac_token(create_hmac_token(a), b), "token from replica A rejected by replica B")

    def test_local_dev_falls_back_to_random(self):
        a, b = load_app_secret({}), load_app_secret({})
        self.assertEqual(len(a), 32)
        self.assertNotEqual(a, b)

    def test_blank_env_value_falls_back(self):
        self.assertEqual(len(load_app_secret({"APP_SESSION_SECRET": "   "})), 32)

    def test_production_without_secret_warns(self):
        with self.assertLogs("app", level="WARNING") as logs:
            load_app_secret({"RUNNING_IN_PRODUCTION": "true"})
        self.assertIn("APP_SESSION_SECRET is not set", "\n".join(logs.output))


if __name__ == "__main__":
    unittest.main()
