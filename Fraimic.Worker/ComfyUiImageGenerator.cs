using System.Net.Http.Json;
using System.Text.Json;
using Fraimic.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fraimic.Worker;

/// <summary>
/// Generates an image with a local ComfyUI instance. The node graph lives in an external workflow
/// template (ComfyUI "Save (API Format)" JSON) with placeholder tokens, so the model/sampler/steps can
/// change without recompiling — just edit the template. Placeholders substituted per request:
///   %PROMPT%  %SEED%  %WIDTH%  %HEIGHT%
/// </summary>
public sealed class ComfyUiImageGenerator(HttpClient http, IOptions<WorkerOptions> options, ILogger<ComfyUiImageGenerator> log)
    : IImageGenerator
{
    private readonly WorkerOptions _opt = options.Value;

    public async Task<byte[]> GenerateAsync(string prompt, int width, int height, byte[]? referenceImage = null, string? style = null, CancellationToken ct = default)
    {
        string baseUrl = _opt.ComfyUiBaseUrl.TrimEnd('/');
        string safePrompt = JsonEncodedText.Encode(prompt).ToString();
        string seed = Random.Shared.NextInt64(1, long.MaxValue).ToString();
        string Common(string tmpl) => tmpl
            .Replace("%PROMPT%", safePrompt).Replace("%SEED%", seed)
            .Replace("%WIDTH%", width.ToString()).Replace("%HEIGHT%", height.ToString());
        string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Pixel Art style (no reference) → SDXL + Pixel Art XL LoRA for real sprite output.
        if (referenceImage is not { Length: > 0 } && string.Equals(style, "pixel", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Pixel Art model (SDXL + pixel-art-xl LoRA).");
            return await RunGraphAsync(baseUrl, Common(await File.ReadAllTextAsync(_opt.PixelWorkflowPath, ct)), 768, 1344, ct);
        }

        // No reference photo → plain text-to-image.
        if (referenceImage is not { Length: > 0 })
            return await RunGraphAsync(baseUrl, Common(await File.ReadAllTextAsync(_opt.ComfyWorkflowPath, ct)), width, height, ct);

        // Reference photo: upload once, then try face-identity (PuLID); if it has no usable face,
        // fall back to image-to-image (transform the photo).
        string uploaded = JsonEncodedText.Encode(await UploadReferenceAsync(baseUrl, referenceImage, ct)).ToString();
        try
        {
            string pg = Common(await File.ReadAllTextAsync(_opt.PulidWorkflowPath, ct))
                .Replace("%IMAGE%", uploaded).Replace("%WEIGHT%", Inv(_opt.PulidWeight)).Replace("%STEPS%", _opt.PulidSteps.ToString());
            log.LogInformation("PuLID face-identity (weight {W}).", _opt.PulidWeight);
            return await RunGraphAsync(baseUrl, pg, width, height, ct);
        }
        catch (ComfyExecutionException ex)
        {
            log.LogInformation("PuLID not usable for this photo ({Reason}); using image-to-image instead.", ex.Message);
            string ig = Common(await File.ReadAllTextAsync(_opt.Img2ImgWorkflowPath, ct))
                .Replace("%IMAGE%", uploaded).Replace("%DENOISE%", Inv(_opt.Img2ImgDenoise)).Replace("%STEPS%", _opt.Img2ImgSteps.ToString());
            return await RunGraphAsync(baseUrl, ig, width, height, ct);
        }
    }

    /// <summary>Submit a workflow graph, wait for its image, and return the bytes. Throws on a ComfyUI execution error.</summary>
    private async Task<byte[]> RunGraphAsync(string baseUrl, string graph, int width, int height, CancellationToken ct)
    {
        using var promptDoc = JsonDocument.Parse(graph);
        var submit = new { prompt = promptDoc.RootElement, client_id = "fraimic-worker" };

        using HttpResponseMessage resp = await PostToComfyAsync(baseUrl, submit, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI /prompt {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        var queued = await resp.Content.ReadFromJsonAsync<QueueResponse>(cancellationToken: ct);
        string promptId = queued?.PromptId ?? throw new InvalidOperationException("ComfyUI did not return a prompt_id.");
        log.LogInformation("ComfyUI job {Id} queued ({W}x{H}).", promptId, width, height);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_opt.GenerationTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1500, ct);
            using var hist = await http.GetAsync($"{baseUrl}/history/{promptId}", ct);
            if (!hist.IsSuccessStatusCode) continue;

            using var doc = JsonDocument.Parse(await hist.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty(promptId, out var entry)) continue;

            // Surface execution errors (e.g. PuLID finding no face) so the caller can fall back.
            if (entry.TryGetProperty("status", out var status) &&
                status.TryGetProperty("status_str", out var ss) && ss.GetString() == "error")
                throw new ComfyExecutionException(ExtractError(status));

            if (!entry.TryGetProperty("outputs", out var outputs)) continue;
            foreach (var node in outputs.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("images", out var images)) continue;
                foreach (var img in images.EnumerateArray())
                {
                    string fn = img.GetProperty("filename").GetString()!;
                    string sub = img.TryGetProperty("subfolder", out var s) ? s.GetString() ?? "" : "";
                    string type = img.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output";
                    var bytes = await http.GetByteArrayAsync(
                        $"{baseUrl}/view?filename={Uri.EscapeDataString(fn)}&subfolder={Uri.EscapeDataString(sub)}&type={type}", ct);
                    log.LogInformation("ComfyUI job {Id} produced {File} ({Bytes} bytes).", promptId, fn, bytes.Length);
                    return bytes;
                }
            }
        }
        throw new TimeoutException($"ComfyUI job {promptId} did not finish within {_opt.GenerationTimeoutSeconds}s.");
    }

    /// <summary>POST the workflow, turning a connection failure into a friendly, actionable error —
    /// without ComfyUI, AI jobs fail with guidance while plain photo uploads keep working.</summary>
    private async Task<HttpResponseMessage> PostToComfyAsync(string baseUrl, object submit, CancellationToken ct)
    {
        try
        {
            return await http.PostAsJsonAsync($"{baseUrl}/prompt", submit, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"The image engine (ComfyUI) isn't reachable at {baseUrl} — is it running on the studio PC? " +
                "Plain photo uploads still work without it.", ex);
        }
    }

    private static string ExtractError(JsonElement status)
    {
        if (status.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            foreach (var m in msgs.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 2 &&
                    m[0].GetString() is "execution_error" or "execution_interrupted" &&
                    m[1].TryGetProperty("exception_message", out var em))
                    return em.GetString() ?? "execution error";
        return "execution error";
    }

    /// <summary>Upload a reference photo to ComfyUI's input folder; returns the stored filename for LoadImage.</summary>
    private async Task<string> UploadReferenceAsync(string baseUrl, byte[] image, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(image);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "image", $"ref_{Guid.NewGuid():N}.jpg");
        form.Add(new StringContent("true"), "overwrite");
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync($"{baseUrl}/upload/image", form, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"The image engine (ComfyUI) isn't reachable at {baseUrl} — is it running on the studio PC? " +
                "Plain photo uploads still work without it.", ex);
        }
        using HttpResponseMessage response = resp;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<UploadResponse>(cancellationToken: ct);
        string name = body?.Name ?? throw new InvalidOperationException("ComfyUI /upload/image returned no name.");
        return string.IsNullOrEmpty(body!.Subfolder) ? name : $"{body.Subfolder}/{name}";
    }

    private record QueueResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("prompt_id")] string? PromptId);

    private record UploadResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("subfolder")] string? Subfolder);
}

/// <summary>A ComfyUI workflow reported an execution error (e.g. PuLID found no face) — lets the caller fall back.</summary>
public sealed class ComfyExecutionException(string message) : Exception(message);
