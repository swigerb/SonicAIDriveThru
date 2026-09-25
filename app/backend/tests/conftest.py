"""Process-wide pytest configuration for app/backend/tests.

Sets ``CONFORMANCE_TEST_HOOKS=1`` for the whole Python unit-test process,
before any test module (and therefore before ``rtmt.py``/
``conformance_hooks.py``) is imported -- pytest always loads a directory's
``conftest.py`` ahead of that directory's test modules. This mirrors what
``tests/conformance``'s ``BackendEnvironment``/``BackendProfiles`` already do
for the .NET harness's spawned backend subprocess: the flag is read once at
process/module-import time (see ``conformance_hooks.py``'s module docstring),
so a suite that needs ``response.create`` to remain client-allow-listed (PR
#49 review round 2, "S1" -- the real frontend never sends it, so it's
production-gated behind this same flag in ``rtmt.py``'s
``_CLIENT_TEST_ONLY_TYPES``) must set it before ``rtmt``'s module-level
constants are computed.

Setting ONLY this flag -- with none of its per-feature overrides
(``CONFORMANCE_FIXED_NOW``, ``CONFORMANCE_*_SECONDS``) -- has no effect on
any other test's timing or clock assumptions: ``conformance_hooks.now()``/
``seconds()`` only change behaviour when the *specific* override env var is
ALSO set (see that module's own docstring). ``test_conformance_hooks.py`` is
unaffected regardless of what this file does: its own autouse fixture
``monkeypatch.delenv()``s this same variable and reloads the module around
every one of its tests.

NEVER set this in a deployed environment -- see ``conformance_hooks.py``'s
module docstring and its infra/Dockerfile/azure.yaml guard test
(``TestNeverInInfraOrDockerfile``). This file only ever runs inside
``pytest``.
"""
from __future__ import annotations

import os

os.environ["CONFORMANCE_TEST_HOOKS"] = "1"
