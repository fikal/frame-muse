using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fraimic.Worker;

/// <summary>
/// Two-layer content safety for the family frame:
///  1) <see cref="IsPromptAllowedAsync"/> — the local LLM classifies the raw request (fails OPEN on
///     error, since the image screen backstops it).
///  2) <see cref="IsImageAllowedAsync"/> — the generated image is scanned by the local NudeNet service
///     (fails CLOSED on error — if the screen can't run, the image does NOT reach the frame).
/// </summary>
public sealed class SafetyScreen(HttpClient http, IOptions<WorkerOptions> options, ILogger<SafetyScreen> log)
{
    private readonly WorkerOptions _opt = options.Value;

    private const string ClassifierSystem = """
        You are a strict content filter for a young child's family photo frame.
        Reply with ONLY one word: BLOCK or ALLOW.
        Reply BLOCK if the request asks for any nudity, sexual, pornographic, fetish, or sexually
        suggestive content, or graphic gore. Otherwise reply ALLOW.
        """;

    /// <summary>True if the raw request is acceptable. Fails open (allows) if the classifier errors.</summary>
    public async Task<bool> IsPromptAllowedAsync(string rawInput, CancellationToken ct = default)
    {
        if (!_opt.SafetyEnabled) return true;
        try
        {
            var req = new OllamaRequest(_opt.OllamaModel,
                $"{ClassifierSystem}\n\nRequest: \"{rawInput}\"\nAnswer:", false, new OllamaOpts(0.0));
            using var resp = await http.PostAsJsonAsync(
                $"{_opt.OllamaBaseUrl.TrimEnd('/')}/api/generate", req, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
            string answer = (body?.Response ?? "").Trim().ToUpperInvariant();
            bool blocked = answer.Contains("BLOCK");
            if (blocked) log.LogWarning("Prompt blocked by classifier: {Input}", rawInput);
            return !blocked;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Prompt classifier failed; allowing (image screen still applies).");
            return true;
        }
    }

    /// <summary>True if the generated image is acceptable. Fails CLOSED (blocks) if the screen errors.</summary>
    public async Task<bool> IsImageAllowedAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (!_opt.SafetyEnabled) return true;
        try
        {
            using var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var resp = await http.PostAsync($"{_opt.NsfwServiceUrl.TrimEnd('/')}/check", content, ct);
            resp.EnsureSuccessStatusCode();
            var result = await resp.Content.ReadFromJsonAsync<NsfwResult>(cancellationToken: ct);
            if (result is null)
            {
                log.LogError("NSFW screen returned no result; blocking image (fail-closed).");
                return false;
            }
            if (result.Nsfw)
                log.LogWarning("Image blocked by NSFW screen: {Detections}",
                    string.Join(", ", result.Detections?.Select(d => $"{d.Class}={d.Score}") ?? []));
            return !result.Nsfw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "NSFW screen unreachable; blocking image (fail-closed). Is nsfw_service.py running?");
            return false;
        }
    }

    private record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOpts Options);
    private record OllamaOpts([property: JsonPropertyName("temperature")] double Temperature);
    private record OllamaResponse([property: JsonPropertyName("response")] string? Response);

    private record NsfwResult(
        [property: JsonPropertyName("nsfw")] bool Nsfw,
        [property: JsonPropertyName("detections")] List<NsfwDetection>? Detections);
    private record NsfwDetection(
        [property: JsonPropertyName("class")] string Class,
        [property: JsonPropertyName("score")] double Score);
}
