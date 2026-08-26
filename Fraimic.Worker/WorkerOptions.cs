namespace Fraimic.Worker;

/// <summary>All worker configuration, bound from the "FraimicMuse" section of appsettings.</summary>
public sealed class WorkerOptions
{
    // --- Queue (shared with the web app) ---
    public string MongoConnectionString { get; set; } = "";
    public string Database { get; set; } = "FraimicMuse";
    public int PollIntervalSeconds { get; set; } = 2;

    // --- Prompt enhancement (Ollama, local) ---
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.1:8b";
    public double OllamaTemperature { get; set; } = 0.8;

    // --- Image generation (ComfyUI, local) ---
    public string ComfyUiBaseUrl { get; set; } = "http://localhost:8188";
    public string ComfyWorkflowPath { get; set; } = "workflow.api.json";
    /// <summary>SDXL + Pixel Art XL LoRA workflow used for the "Pixel Art" style (real sprite output).</summary>
    public string PixelWorkflowPath { get; set; } = "workflow.sdxl.pixel.json";
    /// <summary>Workflow used when a reference photo is attached (image-to-image).</summary>
    public string Img2ImgWorkflowPath { get; set; } = "workflow.flux.img2img.json";
    /// <summary>How much the output departs from the reference (0=identical, 1=ignore it).</summary>
    public double Img2ImgDenoise { get; set; } = 0.72;
    public int Img2ImgSteps { get; set; } = 8;
    /// <summary>Face-identity workflow (PuLID) used when a reference photo contains a face.</summary>
    public string PulidWorkflowPath { get; set; } = "workflow.flux.pulid.json";
    /// <summary>How strongly to lock onto the reference face (0..1.5). ReActor face-swap restores the real
    /// face at the end, so PuLID only needs to guide rough pose here — keep this LOW (~0.55) so the prompt
    /// has room to render scene elements (e.g. a big alien behind her) instead of collapsing to a headshot.</summary>
    public double PulidWeight { get; set; } = 0.55;
    public int PulidSteps { get; set; } = 8;
    public int GenerationTimeoutSeconds { get; set; } = 300;
    /// <summary>Generation resolution (9:16-ish). The encoder resizes to the frame's 1440x2560.</summary>
    public int GenerationWidth { get; set; } = 832;
    public int GenerationHeight { get; set; } = 1472;

    // --- Safety (content filtering) ---
    public bool SafetyEnabled { get; set; } = true;
    public string NsfwServiceUrl { get; set; } = "http://127.0.0.1:8190";

    // Color engine is the pure-C# Fraimic converter baked into Fraimic.API (no external tool).
    /// <summary>Extra brightness applied before encoding (1.0 = none) to counter the reflective, gamut-
    /// limited e-ink reading darker than the on-screen preview — especially warm colors like orange.</summary>
    public float FrameBrightness { get; set; } = 1.0f;

    // --- Frame ---
    public string FrameHost { get; set; } = "fraimic.local";
    /// <summary>"large" (31.5") or "standard" (13.3").</summary>
    public string FrameModel { get; set; } = "large";
}
