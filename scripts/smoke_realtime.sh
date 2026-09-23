#!/bin/sh
# azd postdeploy hook: run the realtime session smoke check (scripts/smoke_realtime.py).
# It NEVER fails the deployment: on any problem it prints a loud warning and exits 0.
# Skip it with:  azd env set SONIC_SKIP_REALTIME_SMOKE true

if [ "$SONIC_SKIP_REALTIME_SMOKE" = "true" ]; then
  echo "Realtime smoke check skipped (SONIC_SKIP_REALTIME_SMOKE=true)."
  exit 0
fi

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
PROJECT_ROOT=$(dirname "$SCRIPT_DIR")
PYTHON=""
for candidate in "$PROJECT_ROOT/app/backend/.venv/bin/python" "$PROJECT_ROOT/.venv/bin/python"; do
  if [ -x "$candidate" ]; then
    PYTHON="$candidate"
    break
  fi
done
if [ -z "$PYTHON" ]; then
  echo "WARNING: Realtime smoke check skipped: no Python virtual environment found (run the postprovision hook first)."
  exit 0
fi

"$PYTHON" "$SCRIPT_DIR/smoke_realtime.py"
code=$?
if [ "$code" -ne 0 ]; then
  echo ""
  echo "WARNING: =================================================================="
  if [ "$code" -eq 1 ]; then
    echo "WARNING:  REALTIME SMOKE CHECK FAILED: the live deployment rejected part of"
    echo "WARNING:  the carhop's session config. Tools may NOT register. See above."
  else
    echo "WARNING:  Realtime smoke check could not run (exit $code) - auth, network or"
    echo "WARNING:  missing settings. Right after a first provision the OpenAI role"
    echo "WARNING:  assignment can take a few minutes to apply; rerun with:"
    echo "WARNING:    python scripts/smoke_realtime.py"
  fi
  echo "WARNING:  The deployment itself was NOT rolled back."
  echo "WARNING: =================================================================="
fi
exit 0
