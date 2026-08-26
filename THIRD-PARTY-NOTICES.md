# Third-party notices

Frame Muse builds on the work of others. Code that was ported or adapted is noted below with its
license; models and tools are downloaded by you at install time under their own licenses.

## Code ported into this repository

### tapframe (MIT)

The panel packing folds in `Fraimic.API/PanelPacker.cs` (`PackLarge`, `PackStandard`) are ports of
`packEl315` / `packEl133` from **tapframe** by Doug Pellerin —
<https://github.com/dpellerin/tapframe> — used under the MIT License:

> Copyright (c) dpellerin
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions: The above copyright notice and this
> permission notice shall be included in all copies or substantial portions of the Software.
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

### fraimic_bin_converter (MIT)

The color pipeline in `Fraimic.API/Spectra6ColorMapper.cs` (enhancement constants, perceptual
distance metric, Atkinson diffusion weights) is a C# port of Fraimic's published converter
`convert_to_bin_spectra6.py` — <https://github.com/Fraimic/fraimic_bin_converter> — used under the
MIT License (same terms as above, copyright Fraimic).

## Tools and models you install separately (not distributed here)

| Component | Purpose | License |
|---|---|---|
| [ComfyUI](https://github.com/comfyanonymous/ComfyUI) | Image-generation runtime | GPL-3.0 |
| [Ollama](https://ollama.com) | Local LLM runtime (prompt enhancement) | MIT |
| [Flux.1-schnell (fp8)](https://huggingface.co/Comfy-Org/flux1-schnell) | Main image model | Apache-2.0 |
| [Stable Diffusion XL base 1.0](https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0) | Pixel-art base model | CreativeML Open RAIL++-M |
| [pixel-art-xl LoRA](https://huggingface.co/nerijs/pixel-art-xl) | Pixel-art style | See model card |
| [ComfyUI_PuLID_Flux_ll](https://github.com/lldacing/ComfyUI_PuLID_Flux_ll) + [PuLID](https://github.com/ToTheBeginning/PuLID) | Face identity | Apache-2.0 |
| [ComfyUI-ReActor](https://github.com/Gourieff/ComfyUI-ReActor) + InsightFace inswapper | Face swap | See repo (research/personal-use terms apply to the swap model) |
| [NudeNet](https://github.com/notAI-tech/NudeNet) | Content safety screen | AGPL-3.0 (runs as a separate service) |
| [llama3.1:8b](https://ollama.com/library/llama3.1) | Prompt-enhancement LLM | Llama 3.1 Community License |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) (NuGet) | Image processing | Six Labors Split License |
| [MongoDB.Driver](https://github.com/mongodb/mongo-csharp-driver) (NuGet) | Queue storage | Apache-2.0 |

**Trademark note:** "Fraimic" is a trademark of its owner. This project is an unofficial community
companion and is not affiliated with, endorsed by, or supported by Fraimic.
