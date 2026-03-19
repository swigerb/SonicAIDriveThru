Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ProjectRoot {
  param([string]$StartingPath = (Get-Location).Path)

  $current = Resolve-Path $StartingPath
  while ($current -and -not (Test-Path (Join-Path $current "app"))) {
    $parent = Split-Path $current -Parent
    if ($parent -eq $current) {
      return $null
    }
    $current = $parent
  }
  return $current
}

$projectRoot = Resolve-ProjectRoot
if (-not $projectRoot) {
  throw "Unable to locate the project root containing the 'app' directory."
}

Push-Location $projectRoot
try {
  $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
  $pythonCmdPath = $null
  $pythonCmdArgs = @()
  $versionOutput = $null

  if ($pyLauncher) {
    try {
      $versionOutput = & $pyLauncher.Source -3.11 --version 2>$null
      if ($LASTEXITCODE -eq 0) {
        $pythonCmdPath = $pyLauncher.Source
        $pythonCmdArgs = @("-3.11")
      }
    } catch {
      $versionOutput = $null
    }
  }

  if (-not $pythonCmdPath) {
    $pythonCmd = Get-Command python -ErrorAction SilentlyContinue
    if (-not $pythonCmd) {
      $pythonCmd = Get-Command python3 -ErrorAction SilentlyContinue
    }
    if (-not $pythonCmd) {
      throw "Python executable not found. Install Python 3.11+ or add it to PATH."
    }
    $pythonCmdPath = $pythonCmd.Source
    $versionOutput = & $pythonCmdPath --version
  }

  if ($versionOutput -match "Python\s+(\d+)\.(\d+)") {
    $major = [int]$matches[1]
    $minor = [int]$matches[2]
    if ($major -lt 3 -or ($major -eq 3 -and $minor -lt 11)) {
      throw "Python 3.11 or later is required. Detected version $versionOutput. Install Python 3.11+ and re-run the script."
    }
  } else {
    throw "Unable to determine Python version from output: $versionOutput"
  }

  $venvPythonPath = Join-Path $projectRoot ".venv\scripts\python.exe"
  if ($IsLinux -or $IsMacOS) {
    $venvPythonPath = Join-Path $projectRoot ".venv/bin/python"
  }

  if (Test-Path $venvPythonPath) {
    Write-Host 'Using existing virtual environment ".venv"'
  } else {
    Write-Host 'Creating python virtual environment ".venv"'
    & $pythonCmdPath @pythonCmdArgs -m venv ./.venv
  }

  if (-not (Test-Path $venvPythonPath)) {
    throw "Python executable not found inside virtual environment at $venvPythonPath"
  }

  Write-Host 'Installing dependencies from "app/backend/requirements.txt" into virtual environment'
  & $venvPythonPath -m pip install -r (Join-Path $projectRoot "app/backend/requirements.txt")
}
finally {
  Pop-Location
}