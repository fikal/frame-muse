# Frame Muse

**Talk to your picture frame.** Frame Muse is a self-hosted web app for
[Fraimic](https://fraimic.com) e-ink picture frames: say or type an idea on your phone, a local AI
on your own GPU paints it, you preview the result, and one tap sends it to the frame.

> **Unofficial.** This is a community project, not affiliated with or endorsed by Fraimic. It uses
> the frame's local upload API (uploads are unlimited on all plans), so nothing here touches your
> Fraimic account or its cloud AI quota.

## What it does

- 📱 **Phone-friendly web page** — type or dictate a prompt, pick an art style, hit Generate.
- 🎨 **Nine art styles** — Realistic, Cartoon, Anime, Comic Book, Watercolor, Oil Painting,
  Poster/Minimal, and a **Pixel Art** mode that uses a dedicated SDXL pixel-art model plus a real
  pixelation pass for genuine 16-bit sprites.
- 🧠 **Local prompt enhancement** — a small local LLM (Ollama) expands your idea into a rich image
  prompt. Fully offline; no API keys.
- 🖼️ **Local image generation** — Flux.1-schnell on your own NVIDIA GPU via ComfyUI.
- 🧑 **Put yourself in the picture** — attach a photo and the pipeline keeps the person's *real*
  face (PuLID guidance + ReActor face swap), or upload a photo directly with no AI at all.
- ✅ **Preview before it ships** — nothing hits the frame until you approve it; re-roll or discard
  freely. A gallery lets you re-send, download, or delete past images.
- 🛡️ **Family-safe by default** — prompts and every generated image pass a local NSFW screen
  (fails closed) before anything reaches the frame.
- 🎯 **Panel-exact color** — the encoder is a validated C# port of Fraimic's own converter
  (dither, palette, and the hardware panel folds), so quality matches the official app.

## How it fits together

```
 phone browser ──HTTP──▶ Fraimic.Web (ASP.NET) ──▶ MongoDB job queue ◀── Fraimic.Worker (GPU PC)
                                                                            │  Ollama  (prompt)
                                                                            │  ComfyUI (image)
                                                                            │  NudeNet (safety)
                                                                            ▼
                                                                     Fraimic frame (LAN upload)
```

The web app and worker can run on **one PC** or two (e.g. web on an always-on server, worker on
your gaming PC). They only share the MongoDB queue — the worker pulls jobs, so no ports need to
open toward the GPU machine.

## Hardware you need

| Piece | Requirement |
|---|---|
| Frame | A Fraimic frame on your LAN (31.5" and 13.3" supported) |
| GPU | NVIDIA, ~16 GB VRAM recommended (Flux.1-schnell fp8); pixel-art mode alone runs on ~8 GB |
| Disk | ~40 GB for models |
| OS | Windows (scripts are PowerShell; the .NET apps themselves are cross-platform) |
| Runtime | .NET 10 SDK, Python 3.12, Docker (for MongoDB) or your own MongoDB |

**No GPU?** You can still run web + worker in photo-only mode: uploading a photo with no prompt
skips AI entirely and just shows it on the frame.

## Setup

### 1. Queue (MongoDB)

```powershell
docker compose up -d       # uses docker-compose.yml in this repo
```

Or point `FraimicMuse:MongoConnectionString` at any MongoDB you already have — put real
credentials in `appsettings.Local.json` (gitignored) next to each app's `appsettings.json`.

### 2. AI stack (on the GPU PC)

**1. Ollama** — install from [ollama.com](https://ollama.com), then `ollama pull llama3.1:8b`.

**2. ComfyUI** — clone [ComfyUI](https://github.com/comfyanonymous/ComfyUI), create a venv
(Python 3.12), install its requirements + a CUDA build of PyTorch.

**3. Models** — download these and drop each one in the listed folder under `ComfyUI/models/`:

| File | Folder | Source |
|---|---|---|
| `flux1-schnell-fp8.safetensors` (~17 GB) | `checkpoints/` | [Comfy-Org/flux1-schnell](https://huggingface.co/Comfy-Org/flux1-schnell) |
| `sd_xl_base_1.0.safetensors` (~7 GB) | `checkpoints/` | [stabilityai/stable-diffusion-xl-base-1.0](https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0) |
| `pixel-art-xl.safetensors` | `loras/` | [nerijs/pixel-art-xl](https://huggingface.co/nerijs/pixel-art-xl) |
| `pulid_flux_v0.9.1.safetensors` | `pulid/` | [PuLID](https://huggingface.co/guozinan/PuLID) |
| `inswapper_128.onnx` | `insightface/` | [ReActor models](https://huggingface.co/datasets/Gourieff/ReActor) |

**4. Custom nodes** (into `ComfyUI/custom_nodes/`):
[ComfyUI_PuLID_Flux_ll](https://github.com/lldacing/ComfyUI_PuLID_Flux_ll) and
[ComfyUI-ReActor](https://github.com/Gourieff/ComfyUI-ReActor) — install each node's
`requirements.txt` into the ComfyUI venv (stop ComfyUI first). PuLID also needs
`facenet-pytorch` installed with `--no-deps`.

**5. Safety service** — create a venv with [NudeNet](https://github.com/notAI-tech/NudeNet) and
run `nsfw-service/nsfw_service.py 8190`. The worker **blocks all images if this is down** (set
   `SafetyEnabled: false` to opt out).

### 3. Build and run

```powershell
dotnet build -c Release
dotnet run --project Fraimic.Web        # the phone page, http://localhost:5000
.\Start-FraimicStudio.ps1               # brings up Ollama/ComfyUI/safety/worker (idempotent)
```

Machine-specific paths for the launcher go in `Start-FraimicStudio.local.ps1` (see the header of
the main script). Register the launcher as a logon scheduled task to survive reboots.

### 4. Point it at your frame

In the worker's `appsettings.json`: `FrameHost` (default `fraimic.local`) and `FrameModel`
(`large` = 31.5", `standard` = 13.3").

## Voice input & HTTPS

Browsers only allow the microphone on HTTPS. Typing and photo upload work fine over plain HTTP.
For voice, give the web app a real hostname + certificate (e.g. a subdomain of a domain you own
with a Let's Encrypt DNS-01 cert, resolved to the server's LAN IP by your local DNS), then set
`FraimicMuse:CanonicalUrl` so stray HTTP requests redirect to the secure name.

## ⚠️ Security notes

- **LAN only.** The web app has **no authentication** — do not port-forward it to the internet.
- Reference photos and generated images are stored **unencrypted** in MongoDB.
- The NSFW screen is a best-effort local model, not a guarantee.

## Configuration reference

Every setting lives under the `FraimicMuse` section (override via `appsettings.Local.json` or
`FraimicMuse__*` environment variables). Highlights:

| Setting | App | Meaning |
|---|---|---|
| `MongoConnectionString`, `Database` | both | The shared queue |
| `CanonicalUrl` | Web | Optional redirect target once HTTPS is set up |
| `FrameHost`, `FrameModel` | Worker | Which frame, which panel size |
| `OllamaModel`, `OllamaTemperature` | Worker | Prompt-enhancement LLM |
| `PulidWeight` | Worker | Face-identity strength (keep ~0.55; ReActor restores the real face) |
| `FrameBrightness` | Worker | >1 brightens for the reflective panel (warm colors go golden) |
| `SafetyEnabled`, `NsfwServiceUrl` | Worker | Content screening |

## Repository layout

| Project | What it is |
|---|---|
| `Fraimic.API` | Frame client + image→`.bin` encoder (fit → color-map → panel fold) |
| `Fraimic.Core` | Job model + MongoDB queue shared by web and worker |
| `Fraimic.Web` | The phone-facing site + minimal API |
| `Fraimic.Worker` | The GPU-side pipeline: enhance → generate → screen → encode → upload |
| `FraimicBin` | CLI: convert/upload an image or an orientation test card by hand |
| `nsfw-service` | Tiny Python HTTP wrapper around NudeNet |
| `fraimic-bin-format.md` | The reverse-engineering notes for the frame's `.bin` format |

## Credits

- [tapframe](https://github.com/dpellerin/tapframe) (MIT) — the hardware-verified panel folds.
- [Fraimic's published converter](https://github.com/Fraimic/fraimic_bin_converter) (MIT) — the
  color pipeline this encoder ports, and the packing spec docs.
- See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full list, including model licenses.

MIT licensed — see [LICENSE](LICENSE).
