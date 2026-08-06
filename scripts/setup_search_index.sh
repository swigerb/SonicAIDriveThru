 #!/bin/sh

. ./scripts/load_python_env.sh

./.venv/bin/python ./app/backend/setup_search_index.py
