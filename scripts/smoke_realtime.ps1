# azd postdeploy hook: run the realtime session smoke check (scripts/smoke_realtime.py).
# It NEVER fails the deployment: on any problem it prints a loud warning and exits 0.
# Skip it with:  azd env set SONIC_SKIP_REALTIME_SMOKE true
$ErrorActionPreference = "Continue"

if ($env:SONIC_SKIP_REALTIME_SMOKE -eq "true") {
  Write-Host "Realtime smoke check skipped (SONIC_SKIP_REALTIME_SMOKE=true)."
  exit 0
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$candidates = @(
  (Join-Path $projectRoot ".venv\Scripts\python.exe"),
  (Join-Path $projectRoot ".venv/bin/python"),
  (Join-Path $projectRoot "app/backend/.venv/bin/python")
)
$python = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $python) {
  Write-Warning "Realtime smoke check skipped: no Python virtual environment found (run the postprovision hook first)."
  exit 0
}

& $python (Join-Path $PSScriptRoot "smoke_realtime.py")
$code = $LASTEXITCODE
if ($code -ne 0) {
  Write-Host ""
  Write-Warning "=================================================================="
  if ($code -eq 1) {
    Write-Warning " REALTIME SMOKE CHECK FAILED: the live deployment rejected part of"
    Write-Warning " the carhop's session config (tools may NOT register) or did not"
    Write-Warning " transcribe the test phrase word for word. See above."
  } else {
    Write-Warning " Realtime smoke check could not run (exit $code) - auth, network or"
    Write-Warning " missing settings. Right after a first provision the OpenAI role"
    Write-Warning " assignment can take a few minutes to apply; rerun with:"
    Write-Warning "   python scripts/smoke_realtime.py"
  }
  Write-Warning " The deployment itself was NOT rolled back."
  Write-Warning "=================================================================="
}
exit 0
