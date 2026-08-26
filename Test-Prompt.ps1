<#
  Test-Prompt.ps1 - generate an image from a text prompt and open it, WITHOUT sending to the frame.
  Uses the same Ollama enhance + ComfyUI generate pipeline as the worker, so what you see here is
  what the frame would get. Fast iteration for dialing in prompts and comparing models.

  Examples:
    .\Test-Prompt.ps1 "a dog with white fur and a really long tongue"
    .\Test-Prompt.ps1 "a red dragon over a castle" -Flux
    .\Test-Prompt.ps1 "really long dog tongue" -Raw        # skip the enhancer, send prompt as-is
    .\Test-Prompt.ps1 "a cozy cabin" -Model qwen2.5:32b    # try a different enhancer model
#>
param(
  [Parameter(Mandatory = $true, Position = 0)] [string]$Prompt,
  [switch]$Flux,                       # use Flux workflow instead of SDXL
  [switch]$Raw,                        # skip prompt enhancement
  [string]$Model = "llama3.1:8b",      # Ollama model for enhancement
  [double]$Temp = 0.35,
  [int]$Width = 768,
  [int]$Height = 1344,
  [string]$Comfy = "http://127.0.0.1:8188",
  [string]$Ollama = "http://localhost:11434",
  [string]$Nsfw = "http://127.0.0.1:8190",
  [switch]$NoSafety                    # skip content screening (for deliberately testing the filter)
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $MyInvocation.MyCommand.Path

$System = @'
You expand a user's short request into ONE image-generation prompt for a text-to-image model.
Faithfulness is the priority - the picture must clearly show what the user asked for.
RULES:
- Keep EVERY subject, feature, and action the user names, in their words. If they say "white fur",
  "really long tongue", "a person", "a long beard", "boogers", "a cat tail" - each MUST appear in
  your prompt, described so it is visually obvious.
- Do NOT invent new subjects or objects the user didn't mention, and never replace their subject
  with something else. Do NOT add props just to add color.
- You may ONLY add: art style, lighting, camera angle, a simple background, and mood - briefly.
- Favor a bold, high-contrast, vivid illustration style, but do not list specific colors unless the user did.
- Output ONLY the final prompt as one paragraph under 55 words. No preamble, quotes, or notes.
'@

# 0) Screen the request (same filter as the frame). -NoSafety bypasses for deliberate filter testing.
if (-not $NoSafety) {
  $clsSys = "You are a strict content filter for a young child's family photo frame. Reply with ONLY one word: BLOCK or ALLOW. Reply BLOCK if the request asks for any nudity, sexual, pornographic, fetish, or sexually suggestive content, or graphic gore. Otherwise reply ALLOW."
  try {
    $cbody = @{ model = $Model; prompt = "$clsSys`n`nRequest: `"$Prompt`"`nAnswer:"; stream = $false; options = @{ temperature = 0.0 } } | ConvertTo-Json
    $verdict = (Invoke-RestMethod -Uri "$Ollama/api/generate" -Method Post -Body $cbody -ContentType 'application/json' -TimeoutSec 60).response.ToUpper()
    if ($verdict -match 'BLOCK') { Write-Host "BLOCKED: that request isn't allowed (family-friendly only). Use -NoSafety only to test the filter." -ForegroundColor Red; return }
  } catch { Write-Warning "prompt screen unavailable: $($_.Exception.Message)" }
}

# 1) Enhance
if ($Raw) {
  $final = $Prompt
  Write-Host "PROMPT (raw): $final" -ForegroundColor Cyan
} else {
  Write-Host "Enhancing with $Model..." -ForegroundColor DarkGray
  $body = @{ model = $Model; prompt = "$System`n`nUser idea: $Prompt`n`nImage prompt:"; stream = $false; options = @{ temperature = $Temp } } | ConvertTo-Json
  $resp = Invoke-RestMethod -Uri "$Ollama/api/generate" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 180
  $final = $resp.response.Trim().Trim('"')
  Write-Host "ENHANCED: $final" -ForegroundColor Cyan
}

# 2) Build workflow graph
$wfFile = if ($Flux) { "$repo\Fraimic.Worker\workflow.flux.json" } else { "$repo\Fraimic.Worker\workflow.api.json" }
$tmpl = Get-Content $wfFile -Raw
$safe = ($final | ConvertTo-Json).Trim('"')   # JSON-escape for embedding
$seed = Get-Random -Minimum 1 -Maximum 2147483647
$graph = $tmpl.Replace('%PROMPT%', $safe).Replace('%SEED%', "$seed").Replace('%WIDTH%', "$Width").Replace('%HEIGHT%', "$Height")

# 3) Submit + poll
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$submit = @{ prompt = ($graph | ConvertFrom-Json); client_id = 'test-prompt' } | ConvertTo-Json -Depth 20
$promptId = (Invoke-RestMethod -Uri "$Comfy/prompt" -Method Post -Body $submit -ContentType 'application/json').prompt_id
Write-Host "Generating ($(if($Flux){'Flux'}else{'SDXL'}), ${Width}x${Height})..." -ForegroundColor DarkGray
$img = $null
while ($sw.Elapsed.TotalSeconds -lt 180) {
  Start-Sleep -Milliseconds 800
  $hist = Invoke-RestMethod -Uri "$Comfy/history/$promptId" -TimeoutSec 10
  $entry = $hist.$promptId
  if ($entry -and $entry.outputs) {
    foreach ($node in $entry.outputs.PSObject.Properties) {
      if ($node.Value.images) { $img = $node.Value.images[0]; break }
    }
  }
  if ($img) { break }
}
if (-not $img) { throw "Generation timed out." }

# 4) Download + open
$outDir = "$env:USERPROFILE\Desktop\FramePreviews"
New-Item -ItemType Directory -Force $outDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$slug = ($Prompt -replace '[^\w]+', '_').Trim('_'); if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40) }
$path = "$outDir\${stamp}_$slug.png"
Invoke-WebRequest -Uri "$Comfy/view?filename=$([uri]::EscapeDataString($img.filename))&subfolder=$([uri]::EscapeDataString($img.subfolder))&type=$($img.type)" -OutFile $path
$sw.Stop()

# Screen the generated image (fails closed like the frame). -NoSafety bypasses.
if (-not $NoSafety) {
  try {
    $r = Invoke-RestMethod -Uri "$Nsfw/check" -Method Post -InFile $path -ContentType 'application/octet-stream' -TimeoutSec 30
    if ($r.nsfw) { Remove-Item $path -Force; Write-Host "BLOCKED: generated image failed the content filter ($($r.detections.class -join ', ')). Not shown." -ForegroundColor Red; return }
  } catch { Remove-Item $path -Force; Write-Host "BLOCKED: content filter unavailable (fail-closed). Is nsfw_service.py running? $($_.Exception.Message)" -ForegroundColor Red; return }
}

Write-Host ("Done in {0:N1}s -> {1}" -f $sw.Elapsed.TotalSeconds, $path) -ForegroundColor Green
Invoke-Item $path
