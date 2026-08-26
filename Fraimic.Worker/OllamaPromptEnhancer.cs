using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fraimic.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fraimic.Worker;

/// <summary>
/// Expands a short request into a rich image prompt using a local Ollama model. Fully local — no keys,
/// no cloud. Swap this class for a cloud <see cref="IPromptEnhancer"/> later without touching the pipeline.
/// </summary>
public sealed class OllamaPromptEnhancer(HttpClient http, IOptions<WorkerOptions> options, ILogger<OllamaPromptEnhancer> log)
    : IPromptEnhancer
{
    private readonly WorkerOptions _opt = options.Value;

    private const string System = """
        You expand a user's short request into ONE image-generation prompt for a text-to-image model.
        Faithfulness is the priority — the picture must clearly show what the user asked for.

        RULES:
        - Keep EVERY subject, feature, and action the user names, in their words. If they say "white fur",
          "really long tongue", "a person", "a long beard", "boogers", "a cat tail" — each MUST appear in
          your prompt, described so it is visually obvious.
        - Do NOT invent new subjects or objects the user didn't mention, and never replace their subject
          with something else. Do NOT add props just to add color.
        - You may ONLY add: lighting, camera angle, a simple background, and mood — briefly.
        - Do NOT add an art style or medium (painting, cartoon, photo, etc.) — the visual style is chosen
          separately and appended after. Keep the description vivid and clear, but do not list specific
          colors unless the user did.
        - This is for a family photo frame: everyone is fully clothed and the scene is wholesome. Never
          produce nudity, sexual, or explicit content, even if the user's wording leans that way.
        - Output ONLY the final prompt as one paragraph under 55 words. No preamble, quotes, or notes.
        """;

    private const string PortraitSystem = """
        The user attached a photo of a REAL person and wants THAT person in the picture — their real face
        must be kept. You write ONE image-generation prompt. Do EXACTLY what the user asks: no more, no less.
        HARD RULES:
        - Apply ONLY what the user actually asks for. If they ask to add something to the scene (e.g. "an
          alien head behind her", "on a beach"), keep the person as an ordinary real person in their OWN
          everyday clothes and just add that thing. If they ask to dress as or become someone (e.g. "a
          viking", "an astronaut", "a superhero"), then DO give them that outfit and role. Do NOT invent
          costumes, characters, objects, or settings the user did not mention. Never turn a plain request
          into a fantasy or space scene.
        - Framing: a WAIST-UP or half-body shot with the person a bit smaller in the frame and clear empty
          space behind and beside them, so added things have room. The person faces the viewer, face fully
          visible and unobstructed (no masks/helmets over the face, no back-of-head shots).
        - Anything the user asks to ADD must be clearly visible at a natural size, in the position they
          named, but SEPARATE from the person — never merged with their head/body, never covering the face,
          never filling the whole frame. "behind her" = a SINGLE one (unless the user asked for several)
          standing behind her and slightly to one side, peering over her shoulder, plainly visible in the
          background. Not two or mirrored, not tiny, not a sticker, not on her forehead, not a hat.
        - Add at most a brief style/lighting note. Wholesome and family-friendly. Output ONLY the final
          prompt, one paragraph under 60 words.
        """;

    public async Task<string> EnhanceAsync(string rawInput, bool portraitMode = false, string? style = null, CancellationToken ct = default)
    {
        // The chosen style's cue words drive the look, appended to whatever the model produces.
        string styleSuffix = StyleCatalog.Suffix(style);
        string AppendStyle(string p) => string.IsNullOrWhiteSpace(styleSuffix) ? p : $"{p.TrimEnd('.', ' ')}. Style: {styleSuffix}.";
        try
        {
            var req = new OllamaRequest(
                _opt.OllamaModel,
                $"{(portraitMode ? PortraitSystem : System)}\n\nUser idea: {rawInput}\n\nImage prompt:",
                false,
                new OllamaOpts(_opt.OllamaTemperature));

            using var resp = await http.PostAsJsonAsync(
                $"{_opt.OllamaBaseUrl.TrimEnd('/')}/api/generate", req, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);

            string text = (body?.Response ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(text))
            {
                log.LogWarning("Ollama returned empty text; falling back to raw input.");
                return AppendStyle(rawInput);
            }
            string styled = AppendStyle(text);
            log.LogInformation("Enhanced: {Prompt}", styled);
            return styled;
        }
        catch (Exception ex)
        {
            // Enhancement is a nice-to-have; never fail a job over it.
            log.LogWarning(ex, "Prompt enhancement failed; using raw input.");
            return AppendStyle(rawInput);
        }
    }

    private record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOpts Options);

    private record OllamaOpts([property: JsonPropertyName("temperature")] double Temperature);

    private record OllamaResponse([property: JsonPropertyName("response")] string? Response);
}
