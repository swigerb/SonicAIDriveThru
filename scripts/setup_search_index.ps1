# Resolve paths from this script's own location so the hook works regardless of
# the working directory azd invokes it from.
$scriptDir = $PSScriptRoot
$projectRoot = Split-Path $scriptDir -Parent

& (Join-Path $scriptDir "load_python_env.ps1")

$venvPythonPath = Join-Path $projectRoot ".venv\scripts\python.exe"
if ($IsLinux -or $IsMacOS) {
  $venvPythonPath = Join-Path $projectRoot ".venv/bin/python"
}

if (-not (Test-Path $venvPythonPath)) {
  throw "Python executable not found inside virtual environment at $venvPythonPath"
}

$pythonScriptPath = Join-Path $projectRoot "app/backend/setup_search_index.py"

Push-Location $projectRoot
try {
  & $venvPythonPath $pythonScriptPath
  if ($LASTEXITCODE -ne 0) {
    throw "setup_search_index.py failed with exit code $LASTEXITCODE"
  }
}
finally {
  Pop-Location
}
