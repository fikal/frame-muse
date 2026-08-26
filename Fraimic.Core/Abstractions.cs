namespace Fraimic.Core;

/// <summary>
/// Turns a short spoken/typed request into a rich image-generation prompt. Local (Ollama) today;
/// swap in a cloud implementation (Claude/OpenAI) later without touching the worker pipeline.
/// </summary>
public interface IPromptEnhancer
{
    /// <summary>
    /// Expand a short request into a full image prompt. When <paramref name="portraitMode"/> is set
    /// (a reference face photo is attached), the prompt is framed so the person's face stays clearly
    /// visible and forward-facing — otherwise face-identity has nowhere to show. <paramref name="style"/>
    /// is an optional art-style key (see StyleCatalog) whose cue words are appended to the result.
    /// </summary>
    Task<string> EnhanceAsync(string rawInput, bool portraitMode = false, string? style = null, CancellationToken ct = default);
}

/// <summary>Generates an image for a prompt and returns it as raw RGB or encoded bytes.</summary>
public interface IImageGenerator
{
    /// <summary>
    /// Generate an image at the given size and return it as a PNG (or other ImageSharp-decodable) byte array.
    /// If <paramref name="referenceImage"/> is provided, does image-to-image using it as the starting point.
    /// <paramref name="style"/> can route to a style-specific model (e.g. "pixel" → SDXL + Pixel Art LoRA).
    /// </summary>
    Task<byte[]> GenerateAsync(string prompt, int width, int height, byte[]? referenceImage = null, string? style = null, CancellationToken ct = default);
}
