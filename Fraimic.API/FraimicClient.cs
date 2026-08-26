using System.Net.Http.Headers;

namespace Fraimic.Api;

/// <summary>A response from the Fraimic device: HTTP status plus the raw response body.</summary>
public sealed record FraimicResponse(int StatusCode, string Body)
{
    /// <summary>True for 2xx status codes.</summary>
    public bool Success => StatusCode is >= 200 and < 300;
}

/// <summary>
/// Low-level transport for the Fraimic REST API. One method per endpoint, one HTTP call each.
/// Does NOT queue or throttle — use <see cref="FraimicDevice"/> for that. Transport/connection
/// failures throw; device-level errors (4xx/5xx) come back as a <see cref="FraimicResponse"/>.
/// </summary>
public sealed class FraimicClient(string host = "fraimic.local", HttpClient? http = null)
{
    // Uploads can take a while; the working tooling allows ~90s. Default generously.
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    private readonly string _baseUrl = $"http://{host}";

    /// <summary>GET /api/info — full device snapshot.</summary>
    public Task<FraimicResponse> GetInfoAsync(CancellationToken ct = default) => GetAsync("/api/info", ct);

    /// <summary>GET /api/battery — battery data only.</summary>
    public Task<FraimicResponse> GetBatteryAsync(CancellationToken ct = default) => GetAsync("/api/battery", ct);

    /// <summary>POST /api/restart — reboot the frame.</summary>
    public Task<FraimicResponse> RestartAsync(CancellationToken ct = default) => PostAsync("/api/restart", ct);

    /// <summary>POST /api/sleep — enter deep sleep (blocked while charging).</summary>
    public Task<FraimicResponse> SleepAsync(CancellationToken ct = default) => PostAsync("/api/sleep", ct);

    /// <summary>POST /api/refresh — re-render the current image (clears E-Ink ghosting).</summary>
    public Task<FraimicResponse> RefreshAsync(CancellationToken ct = default) => PostAsync("/api/refresh", ct);

    /// <summary>
    /// POST /upload — upload a .bin payload as multipart/form-data (field "image", filename
    /// "image.bin"). This is the field-tested path. Do NOT use POST /api/image with a raw
    /// application/octet-stream body: it returns 501 and hangs the frame for 45+ seconds.
    /// The exact byte count must match the target frame's resolution (see <see cref="FrameSize.ByteSize"/>).
    /// Upload stores the image; call <see cref="RefreshAsync"/> afterward to render it.
    /// </summary>
    public async Task<FraimicResponse> UploadImageAsync(byte[] bin, CancellationToken ct = default)
    {
        if (bin is null || bin.Length == 0)
            throw new ArgumentException("Image payload is empty.", nameof(bin));
        if (bin.Length % 2 != 0)
            throw new ArgumentException($"Payload length {bin.Length:N0} is odd; a 4-bit .bin packs two pixels per byte.", nameof(bin));

        // Large-frame firmware counts ~50 bytes of multipart overhead against its 2,304,000-byte
        // buffer, so a full-size payload is rejected as "File too large" (the accepted window is
        // 2,304,000 ±1024 including that overhead). The final 300 bytes are invisible row padding,
        // so trimming them is lossless. Verified live on fw 0.2.29.
        if (bin.Length == FrameSize.LargeCanvas.ByteSize)
            bin = bin[..^300];

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bin);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Emit exactly: Content-Disposition: form-data; name="image"; filename="image.bin"
        // (quoted, no filename* extension) to match the field-tested client and keep the ESP32
        // multipart parser happy. Quotes are added explicitly because .NET would otherwise omit them.
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"image\"",
            FileName = "\"image.bin\"",
        };
        form.Add(part);
        using HttpResponseMessage resp = await _http.PostAsync($"{_baseUrl}/upload", form, ct);
        return new FraimicResponse((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task<FraimicResponse> GetAsync(string path, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _http.GetAsync($"{_baseUrl}{path}", ct);
        return new FraimicResponse((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task<FraimicResponse> PostAsync(string path, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _http.PostAsync($"{_baseUrl}{path}", content: null, ct);
        return new FraimicResponse((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }
}
