#!/bin/sh
set -e

# Resolve paths from this script's own location so the hook works regardless of
# the working directory azd invokes it from.
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PROJECT_ROOT=$(dirname -- "$SCRIPT_DIR")

cd "$PROJECT_ROOT"

. "$SCRIPT_DIR/load_python_env.sh"

# load_python_env.sh provisions the virtualenv at app/backend/.venv
"$PROJECT_ROOT/app/backend/.venv/bin/python" "$PROJECT_ROOT/app/backend/setup_search_index.py"
