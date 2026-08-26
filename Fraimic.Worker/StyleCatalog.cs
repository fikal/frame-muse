namespace Fraimic.Worker;

/// <summary>
/// The art styles offered in the web dropdown. Each carries a curated set of cue words appended to the
/// enhanced prompt (far more reliable than hoping the model infers a style), plus a flag for styles that
/// need a post-process — "Pixel Art" gets a real downscale-and-blockify pass because Flux won't make
/// genuine low-res pixel art on its own.
/// </summary>
public static class StyleCatalog
{
    public sealed record Style(string Key, string Label, string Suffix, bool Pixelate);

    public static readonly Style[] All =
    {
        new("auto",       "Auto (smart)",  "bold, striking, high-contrast, vivid, clean composition", false),
        new("realistic",  "Realistic",     "photorealistic, cinematic photograph, natural lighting, sharp focus, highly detailed, not a cartoon or illustration", false),
        new("cartoon",    "Cartoon",       "bold cartoon illustration, thick clean outlines, flat vibrant colors, simple shapes, playful", false),
        new("anime",      "Anime",         "anime key visual, cel shaded, clean lineart, vibrant colors, studio anime style", false),
        new("comic",      "Comic Book",    "comic book art, bold black ink outlines, dramatic shading, halftone dots, high contrast", false),
        new("watercolor", "Watercolor",    "soft watercolor painting, delicate color washes, textured paper, gentle and painterly", false),
        new("oil",        "Oil Painting",  "oil painting, visible brushstrokes, rich warm colors, classical fine-art style", false),
        new("pixel",      "Pixel Art",     "pixel art, 16-bit SNES JRPG sprite, chunky low-res pixels, limited color palette, hard pixel edges, no anti-aliasing, flat cel shading, retro game", true),
        new("poster",     "Poster / Minimal", "minimalist flat vector poster art, bold simple shapes, limited color palette, clean, high contrast, graphic", false),
    };

    private static readonly Dictionary<string, Style> ByKey =
        All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Look up a style by key, defaulting to "Auto" for null/blank/unknown keys.</summary>
    public static Style Get(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key.Trim(), out var s) ? s : All[0];

    public static string Suffix(string? key) => Get(key).Suffix;
    public static bool Pixelate(string? key) => Get(key).Pixelate;
}
