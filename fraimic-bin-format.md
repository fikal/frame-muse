---
name: fraimic-bin-format
description: "Exact Fraimic .bin image format and the spec gotchas (6-color Spectra panel, not grayscale; 1600x1200, not 1200x1600)"
metadata: 
  node_type: memory
  type: project
  originSessionId: b497e8e5-61a9-45d2-86d0-023b2b59ee9f
---

The FraimicBin project converts images to the Fraimic art frame's `.bin` upload format and uploads it.

**CRITICAL upload-endpoint gotcha** (from the community `docs/fraimic-api.md`, more current than the official PDF): upload via **`POST http://<frame>/upload`** as **`multipart/form-data`** — part field `name="image"`, filename `"image.bin"`, part content-type `application/octet-stream`, ~90s timeout. Then **`POST /api/refresh`** to render (upload only stores). **Do NOT** POST raw `application/octet-stream` to `/api/image` (what the official PDF says) — it returns 501 `unsupported_content_type` AND hangs the frame 45+ seconds. Concurrency also triggers 45s timeouts → all requests serialized + spaced (see [[fraimic-device-queue-throttle]]). `.bin` byte count must match the frame resolution (else `invalid_image_size`).

There are **two frame models** (fraimic.com), so size must be configurable — handled by `FrameSize` (Width, Height, ByteSize = W*H/2), presets `StandardCanvas` and `LargeCanvas`, CLI `--frame standard|large` / `--size WxH`:
- **Standard Canvas** (13.3"/14x18"): 1600x1200 native landscape → **960,000-byte** .bin. (Marketed as 1200x1600 portrait; .bin is native landscape per the working tool.)
- **Large Canvas** (24x36", 31.5" panel): **buffer = 2,304,000 bytes — HARDWARE-VERIFIED 8/24/2026** against user's frame (fw 0.2.29). Device `/logs`: `Invalid .bin size: 1843244 bytes (expected 2304000 ±1024)` / `buffer: 2304000`.
  **Panel identified: Good Display GDEP315C01** (E Ink Spectra 6 31.5", 2560×1440, QSPI, dual left/right CS, folded drive 5120×720 per datasheet — in scratchpad + `EN-DEAM-315E1.pdf`). The Fraimic = productized Good Display **DEAM-315E1** dev kit (ESP32-S3-WROOM-1-N16R8, "NeoFrame" firmware lineage; NeoFrame API doc describes the /upload protocol for the 7.3" sibling).
  **Format findings from probe campaign 8/24 (14+ diagnostic uploads, photos in repo root):**
  - Nibble palette: 0=black, 1=white, 3=blue, 4=red, 5=yellow; **index 2 is NOT green** on this panel (renders white/invisible — green likely 6, unverified). Hi-nibble = left pixel (4bpp) confirmed — solid colors render solid.
  - **Line-major, 1600 B per line stride, 1440 lines** (ruler probe: 30×76,800B units → separator ticks every ~48 lines = 76,800/1600 ✓). Each line carries 3200px worth of data for 2560 visible px (640px dummy/overscan per line, matches datasheet 6px/SDCLK timing).
  - **Within-line arrangement of the 3200px → 2560 visible = LAST OPEN QUESTION.** NOT plain linear (linear pack → 1px combing), NOT simple even/odd fold, NOT [320B plane][1280B pixels] (all disproven on-panel). "linelayout.bin" probe (every line identical = 5×320B color chunks) uploaded 8/24 evening — its photo reads out the within-line map directly.
  - E-ink **ghosting contaminates diagnostic photos** — always upload all-white (`0x11`×2,303,700) and let it render before a diagnostic.
  - E Ink's official converter (`E6_render-x86_64.exe`, from Good Display `xtacepSetup-V3.4.1` installer, extracted in scratchpad `xt/app/ACEP/E6`) implements the Spectra 6 pipeline incl. the HGD source-fold (`-t 1`: display (x,y) → gate y/2, source 2x+(y&1), verified by impulse test) and outputs per-pixel drive codes (black=0x00, white=0x78, green=0x30, blue=0x10, red=0x40, yellow=0x20 = nibble codes 0/F/6/2/8/4 <<3).
  - Frame USB-C is charge-only (no ESP32 serial enumerates) — no firmware dump path.

**Decompiled the official Good Display toolchain 8/24 (`xtacep` Java app: `imggenerator-2.0.1.jar` + `imggenerator-client-3.4.1.jar`, decompiled with CFR — sources in scratchpad `imggen_src`/`imggenc_src`; jars grabbed from the exe4j temp-extract dir while xtacep.exe ran):**
  - `ImgGenerator`: `SCREEN_31_5_E6 = 30`. For screen 30 it shells out to `E6_render-x86_64.exe -i in.bmp -o out.bmp -l lut/project_adaptive_LUT_V0.bin -d 6 -m 1 -t 0 ...` (ImgParams defaults: LUT `project_adaptive_LUT_V0.bin`, dither 6 = Stucki-serpentine, **mode 1 = external T-con, tft 0 = no HGD fold**, gamma 1.0, temp 25°C). Output is a **2560×1440 BMP of panel drive codes** — the T-con applies the 5120×720 physical fold in hardware.
  - **This means the official 31.5 format is 2560×1440 @ 4bpp = 1,843,200 bytes** (plain raster of nibble drive codes). **Fraimic's 2,304,000 is NOT produced by any official tool** — Fraimic firmware adds its own wrapper. 2,304,000 = **3200×1440 @ 4bpp exactly** (640 extra px/row vs 2560 visible) — strongest structural lead for the fold: treat the buffer as a 3200-wide raster, 2560 visible + 640 overscan, but the row↔panel-axis orientation is transposed/folded per the probe photos (identical .bin rows produced *horizontal* bands on the portrait panel → .bin rows map to panel columns, i.e. column-major / transposed).
  - **Drive codes** (from E6_render mode-1 output, per-color): black=0x00, white=0x78, green=0x30, blue=0x10, red=0x40, yellow=0x20 (nibble >>4: K=0,B=1,Y=2,G=3,R=4,W=7). These are E-Ink *panel* codes, distinct from Fraimic's upload palette.
  - **Good Display six-color upload index table** (`PicUtils.XTHH_PicConverSixColorPic`): black=0, white=1, yellow=2, red=3, **green=6**, blue=5. Packing = row-major, 2 px/byte, **hi-nibble = first(left) pixel** (`MadeUpLoadData2to1`), width padded up to a multiple of 8.
  - **Fraimic's upload palette differs from Good Display's** (proven on-panel): Fraimic 1=white, 3=blue, 4=red, 5=yellow, **2=renders invisible/near-white**, green≈6 (matches GD). ⚠️ The current `FraimicConverter.Spectra6` palette encodes **green at index 2** — which is why green is missing from every render on the large frame. Green-index test (idx6 top / idx2 bottom) uploaded 8/24 to confirm 6.

**✅ SOLVED 8/24 via tapframe.** A Discord user pointed to **github.com/dpellerin/tapframe** (MIT) — a beer-tap-menu tool with a field-tested Fraimic encoder (`src/displays/fraimic/bin.ts`). Its `packEl315` is the exact fold; ported verbatim into `FraimicConverter.PackLarge` (C# output confirmed **byte-identical** to a Python reimplementation of `packEl315`). Definitive facts:

- **Panels are PORTRAIT-native code grids**: EL133 = 1200×1600 (960,000 B), **EL315 = 1440×2560 (2,304,000 B)**. `codes[]` = one device code per pixel, row-major, `[y*width + x]`.
- **Palette (RGB → device code)**, matching my greentest/palette5 probes: black→0x0, white→0x1, **yellow→0x2, red→0x3, blue→0x5, green→0x6**; index/code **0x4 is unused**. (My earlier zprobe reads of 3=blue/4=red/5=yellow were misreads of ghosted/scrambled frames; the clean uniform-band tests + tapframe agree.)
- **EL315 fold** (`PackLarge`): vertical-flip the grid, then 2 halves × 4 source-chunks × 720 gate-lines × 400 bytes. Per chunk `ic`: `realPixels = ic==3 ? 160 : 800` (chunk 3 is mostly overscan), `start=ic*800`, `stripStart=half*1280`; each 400-byte gate row is filled with `0x11` (white) then overwritten with real data at `flipped[(stripStart + q/2)*1440 + (gate*2 + q%2)]`, `q = start+p`. 2×4×720×400 = 2,304,000 ✓.
- **EL133 fold** (`PackStandard`): per row, left half packs forward from offset 0, right half from the midpoint (480,000).
- Orientation: a landscape source is rotated CW90 into portrait before fitting (tapframe `rotateCw90`); the frame's hanging tab is on that side.

**Status:** `FraimicConverter` now produces correct output for both frames. The `HalfInterleaved`/`StrideBytes`/`FinalizeBuffer` scaffolding and the old wrong palette are gone. Green renders. Both `PackLarge`/`PackStandard` are public + credited to tapframe.

**✅ COLOR ENGINE — port of Fraimic's OFFICIAL converter (8/26).** The canonical, authoritative source is **`github.com/Fraimic/fraimic_bin_converter`** (`convert_to_bin_spectra6.py`) — Fraimic's own published tool. Ported verbatim into `FraimicConverter.FraimicColorPipeline`:
- **Enhance** (PIL-faithful): brightness ×1.1, contrast ×1.2 (pivot = whole-image mean luma, PIL semantics), saturation ×1.2, then `EDGE_ENHANCE`→`SMOOTH`→`SHARPEN` 3×3 convolutions (border copied). Gives the vivid "pop".
- **Quantize** to 6 **pure-RGB** anchors {(0,0,0),(255,255,255),(255,255,0),(255,0,0),(0,0,255),(0,255,0)} via a perceptual metric: `rgb_dist=(dR²·.25+dG²·.35+dB²·.40)·.75/255²`, `luma=(R·250+G·350+B·400)/255000`, `total=1.5·rgb_dist+0.60·lumaΔ²`.
- **Custom Atkinson** dither, diffuse 5/8: right 1/8, below-left 1/8, below 1/4, below-right 1/8 (non-serpentine) → codes {0,1,2,3,5,6} → PackLarge.
- **VALIDATED vs the real tool**: official `.py --fit crop --dither atkinson` vs ours on identical 1440×2560 input → histograms match <1% per color, visually identical, 79% exact pixels (rest = error-diffusion jitter). Repo also confirms our entire format (2,304,000B, 8 IC blocks, IC4/8 `0x11`-pad, codes 0/1/2/3/5/6). ~0.8s in C#. Worker: `Color engine: Fraimic official converter port`.

<details><summary>Superseded reverse-engineering attempts (kept for reference)</summary>

**[superseded] E6V3ColorMapAndDither (decompiled app)** — `dither/E6V3ColorMapAndDither.java` (scratchpad `imggenc_src/com/xingtai/imggenerator/`), Floyd-Steinberg with a "deblack" anchor RGB(42,38,53). Was the fix for "too dark on skin", but NOT the canonical converter above:
- **No 3D LUT.** Per pixel: pick the **nearest of 6 anchor colors** (squared RGB distance), emit its upload code, then **Floyd-Steinberg** error-diffuse (7/5/1/3 over base 16; neighbors (x+1,y),(x-1,y+1),(x,y+1),(x+1,y+1); non-serpentine; each diffused channel clamped 0..255 — matches `AbstractColorMapAndDither.processRed/Green/Blue`).
- **Anchors** (RGB; the app's `ChromaWF` is stored BGR): black **(42,38,53)**, white (220,220,220), red (200,20,20), yellow (230,210,30), green (30,140,30), blue (50,80,200). **The black anchor is a dark blue-gray at luma ~42, NOT pure black** — this is the whole trick: midtones/skin are closer to white/color anchors than to "black", so they stay light instead of crushing. → upload codes {black0, white1, red3, yellow2, green6, blue5}.
- Result: **black ~22%, white ~31%** — natural skin, deep blacks only where truly black; byte-identical to a Python reimpl of the Java. ~0.5s for 1440×2560, self-contained in `Fraimic.API.dll`, no external tool/file.
- **Superseded detour:** an earlier pass embedded E Ink's `E6_render.exe` 3D LUT (`project_adaptive_LUT_V0.bin` = 18B header + 64³×3 BGR, RE'd via the exe as oracle → trilinear + Atkinson, ~29% black/deep-vivid). But `E6_render -d6 -m1 -t0` is a *different/older* vendor path; the app uses V3, which is lighter. User's "too dark esp skin" (`Downloads/quality difference/ours.jpg` vs `theres.jpg`) confirmed V3 is the match. `Spectra6Lut.cs` + embedded LUT removed; `E:\AI\E6\E6_render*.exe` fully vestigial. (`E6_render --help` also exposes hue/sat/bright/contrast/gamma image-adjustment + a `Spectra6_Render_LUT_Default_v2.bin` — the app uses none of these.) Older `E6ColorMapAndDither.java` (9-anchor) + `E5…` are prior generations.

</details>

**Large-frame upload gotchas (fw 0.2.29, empirically mapped 8/24):**
- Firmware validates the *received* size INCLUDING ~44–54 bytes of multipart slop (varies by client: .NET ≈ +44, curl ≈ +54) against 2,304,000 ±1024. A file of exactly 2,304,000 bytes OVERFLOWS the buffer → "File too large". **Truncate the .bin to ~2,303,700 bytes** before upload (loses last ~600 px of the bottom row — invisible). File 2,303,000 verified OK; 2,303,999 verified fail.
- Anything ≥ ~2.36MB → "File too large"; other sizes → HTTP 400 "Invalid file size for E-ink display format" (fast, clean — no hang, unlike the /api/image 501 path).
- The **web-form `/upload` path AUTO-RENDERS** (success page: ~20–30s); `POST /api/refresh` right after returns `{"error":"device_busy"}` — no explicit refresh needed for this path.
- Frame serves useful pages: `/portal` (hub + battery), `/upload` (form; states "Required file size: 2.3MB … 31.5\" E-Ink display"), `/logs` (**goldmine** — logs expected sizes for rejected uploads), `/info`, `/wifi`, `/battery/status` (JSON).

Gotchas where the official REST API guide PDF is WRONG/misleading vs. the field-tested community tool (github.com/dsackr/fraimic-controller, `local-server/app.py`):

1. **It is a 6-color E Ink Spectra 6 panel, NOT grayscale.** The 4-bit nibbles are palette indices, not gray levels: `0=Black, 1=White, 2=Green, 3=Blue, 4=Red, 5=Yellow`. Sending true grayscale values renders as garbage color (e.g. mid-gray=2=green). The panel has no true gray. The guide's "4-bit grayscale" wording is misleading.
2. **1MB upload limit: DISPROVEN on the large frame 8/24** — it accepts ~2.3MB (see large-frame section above; the limit is the 2,304,000-byte panel buffer, not 1MB). The community doc's limit presumably applies to the standard frame only, where 960KB fits anyway.
3. **Pillow palette-padding bug (community tool only, NOT ours):** Pillow padding a 6-color palette to 256 with zeros makes duplicate black slots, so near-black maps to nibble >5 → garbage on device. Our converter does its own nearest-of-6 search, so nibbles are always 0–5 — structurally immune.

**Hanging orientation (portrait support):** the API exposes NO orientation/accelerometer/tilt — the frame can't report how it's hung, so it must be specified manually (community tool does the same). Panel buffer is always landscape-native (e.g. 1600x1200); for portrait the image is composed at viewer dims (1200x1600) then ROTATED into the native landscape buffer before packing, so byte size stays native (960,000). `FrameOrientation` enum {Landscape, PortraitClockwise, PortraitCounterClockwise, LandscapeUpsideDown}; threaded through `FraimicConverter.Convert(..., orientation)` and `FraimicTestPattern.GenerateBin(size, orientation)`; CLI `--orientation landscape|portrait|portrait-ccw|flip` (portrait = CW). The two portrait dirs are 180° apart in physical portrait view — exactly one renders upright; pick via the test card.

**Orientation test card:** `FraimicTestPattern.Generate(size)` / `GenerateBin(size, orientation)` and CLI `--test-pattern` produce a sized card: corners TL=Red, TR=Green, BL=Blue, BR=Yellow + a black up-arrow. Drawn upright for the chosen orientation. Upload it: if the arrow points up (Red top-left) when hung, that orientation is correct; if upside down, use the other portrait dir. Verified: pack→unpack round-trip pixel-accurate; portrait CW/CCW rotate correctly into the native buffer.

Packing: 2 pixels/byte, **high nibble = left pixel, low nibble = right pixel** (`out[i>>1] = (px[i]<<4) | px[i+1]`). Conversion uses a C# port of Fraimic's official converter (enhance + perceptual quantize + custom Atkinson; see the COLOR ENGINE section above).

Implemented in the **`Fraimic.API`** class library (namespace `Fraimic.Api`, sibling folder to `FraimicBin`, NOT nested; see [[fraimic-converter-imagesharp-license]]):
- `FraimicConverter.Convert(...)` — image → .bin (always full-color Spectra6; grayscale was dropped per user, the panel is color anyway).
- `FraimicClient` — low-level transport, one method per REST endpoint (info/battery/restart/sleep/refresh + `UploadImageAsync` → POST /upload multipart). Returns `FraimicResponse(StatusCode, Body)`. 120s HttpClient timeout. No queue/throttle.
- `FraimicDevice` — **the robust front door** (see [[fraimic-device-queue-throttle]]): serial queue + 5s min spacing + `ILogger` logging. `UploadImageAsync(bin, refresh=true)` uploads then auto-triggers refresh (non-fatal). Use this, not `FraimicClient` directly.
- `FileLoggerProvider` / `FraimicLog.ToFile(path)` — dependency-free file logger.

The **`FraimicBin`** console (solution `.slnx` + reference PDF live here) is a thin CLI referencing `Fraimic.API`. ImageSharp lives in the library, not the console.
