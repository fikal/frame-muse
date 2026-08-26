<#
  Start-FraimicStudio.ps1 - bring up the whole Frame Muse studio stack on the GPU PC:
  Ollama (prompt LLM), ComfyUI (images), the NSFW safety service, and the Frame Muse worker.
  Idempotent: anything already running is left alone, so it's safe to run at logon or by hand.

  Machine-specific paths: pass parameters, or create Start-FraimicStudio.local.ps1 next to this
  script (gitignored) that sets $ComfyDir / $NsfwVenvPython / $LogDir / $OllamaModel.
#>
param(
  [string]$RepoDir        = $PSScriptRoot,
  [string]$ComfyDir       = "$env:USERPROFILE\ComfyUI",
  [string]$NsfwVenvPython = "$env:USERPROFILE\nsfw\venv\Scripts\python.exe",
  [string]$LogDir         = "$PSScriptRoot\logs",
  [string]$OllamaModel    = "llama3.1:8b"
)

# Optional per-machine overrides (gitignored).
$localOverrides = Join-Path $PSScriptRoot "Start-FraimicStudio.local.ps1"
if (Test-Path $localOverrides) { . $localOverrides }

New-Item -ItemType Directory -Force $LogDir | Out-Null
function Test-Port($port) { [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }
function Wait-Port($port, $secs = 40) { for ($i = 0; $i -lt $secs; $i++) { if (Test-Port $port) { return $true }; Start-Sleep 1 }; return $false }

Write-Host "Frame Muse studio launcher" -ForegroundColor Cyan

# 1) Ollama (its installer auto-starts the tray app at logon; just make sure the server + model are ready)
if (-not (Test-Port 11434)) {
  $ollama = "$env:LOCALAPPDATA\Programs\Ollama\ollama app.exe"
  if (Test-Path $ollama) { Write-Host "Starting Ollama..."; Start-Process $ollama }
}
if (Wait-Port 11434 30) {
  Start-Process -NoNewWindow -Wait "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" -ArgumentList "pull", $OllamaModel -ErrorAction SilentlyContinue
} else { Write-Warning "Ollama not reachable on :11434." }

# 2) ComfyUI (venv install)
$comfyPy = "$ComfyDir\venv\Scripts\python.exe"
if (Test-Port 8188) { Write-Host "ComfyUI already up." }
elseif (Test-Path $comfyPy) {
  Write-Host "Starting ComfyUI..."
  Start-Process -FilePath $comfyPy -ArgumentList "main.py", "--listen", "127.0.0.1", "--port", "8188" `
    -WorkingDirectory $ComfyDir -WindowStyle Hidden `
    -RedirectStandardOutput "$LogDir\comfyui.log" -RedirectStandardError "$LogDir\comfyui.err.log"
} else { Write-Warning "ComfyUI venv not found at $comfyPy" }

# 3) NSFW safety service (fail-closed: without it the worker blocks every image)
$nsfwScript = "$RepoDir\nsfw-service\nsfw_service.py"
if (Test-Port 8190) { Write-Host "NSFW service already up." }
elseif ((Test-Path $NsfwVenvPython) -and (Test-Path $nsfwScript)) {
  Write-Host "Starting NSFW safety service..."
  Start-Process -FilePath $NsfwVenvPython -ArgumentList $nsfwScript, "8190" -WorkingDirectory (Split-Path $nsfwScript) `
    -WindowStyle Hidden -RedirectStandardOutput "$LogDir\nsfw.log" -RedirectStandardError "$LogDir\nsfw.err.log"
} else { Write-Warning "NSFW service not found; images will be blocked until it runs." }

# Give ComfyUI a moment to load before the worker starts pulling jobs
Wait-Port 8188 60 | Out-Null
Wait-Port 8190 30 | Out-Null

# 4) The worker (built Release exe)
$workerExe = "$RepoDir\Fraimic.Worker\bin\Release\net10.0\Fraimic.Worker.exe"
if (Get-Process Fraimic.Worker -ErrorAction SilentlyContinue) { Write-Host "Worker already running." }
elseif (Test-Path $workerExe) {
  Write-Host "Starting Frame Muse worker..."
  Start-Process -FilePath $workerExe -WorkingDirectory (Split-Path $workerExe) -WindowStyle Hidden `
    -RedirectStandardOutput "$LogDir\worker.log" -RedirectStandardError "$LogDir\worker.err.log"
} else { Write-Warning "Worker exe not found - build it: dotnet build Fraimic.Worker -c Release" }

Write-Host "Studio up. Submit from the Frame Muse web page. Logs in $LogDir" -ForegroundColor Cyan
