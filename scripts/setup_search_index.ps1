./load_python_env.ps1

$venvPythonPath = "./.venv/scripts/python.exe"
if (Test-Path -Path "/usr") {
  # fallback to Linux venv path
  $venvPythonPath = "./.venv/bin/python"
}

# Define the path to the Python script
$pythonScriptPath = "app/backend/setup_search_index.py"

Start-Process -FilePath $venvPythonPath -ArgumentList $pythonScriptPath -Wait -NoNewWindow
