#!/bin/sh

PRODUCTION_MODE="${1:-}"

echo 'Creating Python virtual environment and installing dependencies...'
sh scripts/load_python_env.sh

if [ "$PRODUCTION_MODE" != "--production" ]; then
    echo ""
    echo "Restoring frontend npm packages"
    echo ""
    cd app/frontend
    npm install
    if [ $? -ne 0 ]; then
        echo "Failed to restore frontend npm packages"
        exit $?
    fi

    echo ""
    echo "Building frontend"
    echo ""
    npm run build
    if [ $? -ne 0 ]; then
        echo "Failed to build frontend"
        exit $?
    fi
    cd ../../
fi

echo ""
echo "Starting backend"
echo ""

if [ "$PRODUCTION_MODE" = "--production" ]; then
    HOST="${HOST:-0.0.0.0}"
    PORT="${PORT:-8000}"
    LOG_LEVEL="${LOG_LEVEL:-info}"
    export RUNNING_IN_PRODUCTION=true
    cd app/backend
    python -m gunicorn app:create_app \
        -b "${HOST}:${PORT}" \
        --worker-class aiohttp.GunicornWebWorker \
        --workers 1 \
        --timeout 120 \
        --keep-alive 65 \
        --access-logfile - \
        --log-level "${LOG_LEVEL}"
else
    cd app/backend
    python app.py
fi

if [ $? -ne 0 ]; then
    echo "Failed to start backend"
    exit $?
fi