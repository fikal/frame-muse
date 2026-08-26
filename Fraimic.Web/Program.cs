using Fraimic.Core;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific settings (real Mongo credentials, canonical host) live in appsettings.Local.json,
// which is gitignored — appsettings.json ships only safe defaults. Env vars (FraimicMuse__*) also work.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

string mongo = builder.Configuration["FraimicMuse:MongoConnectionString"]
    ?? throw new InvalidOperationException("Set FraimicMuse:MongoConnectionString.");
string database = builder.Configuration["FraimicMuse:Database"] ?? "FraimicMuse";
int fallbackSeconds = builder.Configuration.GetValue("FraimicMuse:FallbackJobSeconds", 300);

builder.Services.AddSingleton(new JobRepository(mongo, database));

var app = builder.Build();

// Optional canonical URL (e.g. "https://frame.example.com"): requests arriving on any other
// host/scheme are redirected there, so voice (which needs HTTPS) always lands on the secure name.
// Leave unset to serve on whatever host the request came in on. Loopback is exempt for local testing.
string? canonicalUrl = builder.Configuration["FraimicMuse:CanonicalUrl"];
if (Uri.TryCreate(canonicalUrl, UriKind.Absolute, out Uri? canonical))
{
    app.Use(async (ctx, next) =>
    {
        string host = ctx.Request.Host.Host;
        bool isLoopback = host is "localhost" or "127.0.0.1" or "[::1]";
        bool matches = host.Equals(canonical.Host, StringComparison.OrdinalIgnoreCase)
            && (!canonical.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || ctx.Request.IsHttps);
        if (!isLoopback && !matches)
        {
            ctx.Response.Redirect($"{canonical.Scheme}://{canonical.Host}{ctx.Request.Path}{ctx.Request.QueryString}");
            return;
        }
        await next();
    });
}

// Optional shared PIN: when FraimicMuse:AccessPin is set, every /api call must carry it (header
// X-FrameMuse-Pin, or ?pin= for plain download links). The page itself stays open — the front-end
// prompts for the PIN on the first 401 and remembers it. Leave unset for a trusted LAN.
string? accessPin = builder.Configuration["FraimicMuse:AccessPin"];
if (!string.IsNullOrWhiteSpace(accessPin))
{
    byte[] pinBytes = System.Text.Encoding.UTF8.GetBytes(accessPin);
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            string supplied = ctx.Request.Headers["X-FrameMuse-Pin"].FirstOrDefault()
                ?? ctx.Request.Query["pin"].FirstOrDefault()
                ?? "";
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    pinBytes, System.Text.Encoding.UTF8.GetBytes(supplied)))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "PIN required" });
                return;
            }
        }
        await next();
    });
}

app.UseDefaultFiles();   // serve index.html at /

// Serve the self-signed cert (.cer) so phones can download + trust it for the HTTPS/voice link.
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".cer"] = "application/x-x509-ca-cert";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    // Never cache the app shell or its css/js — a stale copy breaks after every deploy (the page and
    // its script must stay in lockstep with the API). All three are tiny; LAN revalidation is free.
    OnPrepareResponse = ctx =>
    {
        string name = ctx.File.Name;
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    },
});

// Submit a new request → returns id, queue position, and ETA.
app.MapPost("/api/jobs", async (SubmitRequest req, JobRepository repo, CancellationToken ct) =>
{
    string text = (req.Text ?? "").Trim();
    if (text.Length > 800)
        text = text[..800];

    string? who = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name!.Trim();

    // Optional reference photo (data-URI or bare base64). Cap to keep queue docs small.
    string? refImage = req.ImageBase64;
    if (!string.IsNullOrWhiteSpace(refImage))
    {
        int comma = refImage.IndexOf(',');
        if (refImage.StartsWith("data:") && comma > 0) refImage = refImage[(comma + 1)..];
        if (refImage.Length > 8_000_000) return Results.BadRequest(new { error = "Reference photo is too large." });
    }

    // Need at least a prompt OR a photo (a bare photo just gets shown on the frame).
    if (text.Length < 2 && string.IsNullOrWhiteSpace(refImage))
        return Results.BadRequest(new { error = "Tell me what to draw, or attach a photo." });

    var job = await repo.SubmitAsync(text, who, refImage, style: req.Style, ct: ct);

    long position = await repo.PositionAsync(job, ct);
    var avg = await repo.AverageProcessingTimeAsync(TimeSpan.FromSeconds(fallbackSeconds), ct: ct);
    int etaSeconds = (int)Math.Round((position + 1) * avg.TotalSeconds);

    return Results.Ok(new SubmitResponse(job.Id, (int)position, etaSeconds));
});

// Poll a job's status.
app.MapGet("/api/jobs/{id}", async (string id, JobRepository repo, CancellationToken ct) =>
{
    var job = await repo.GetAsync(id, ct);
    if (job is null) return Results.NotFound();

    long position = Job.ActiveStatuses.Contains(job.Status) ? await repo.PositionAsync(job, ct) : 0;
    var avg = await repo.AverageProcessingTimeAsync(TimeSpan.FromSeconds(fallbackSeconds), ct: ct);
    int etaSeconds = Job.ActiveStatuses.Contains(job.Status)
        ? (int)Math.Round((position + 1) * avg.TotalSeconds) : 0;

    return Results.Ok(new StatusResponse(
        job.Id, job.Status.ToString(), job.RawInput, job.EnhancedPrompt,
        job.Error, (int)position, etaSeconds, job.ThumbnailDataUri, job.FullImageBase64));
});

// Send a generated/stored image to the frame (queues it as a direct photo that uploads straight away).
// /send = approving a fresh preview → it becomes the gallery entry.
// /resend = re-sending an image ALREADY in the gallery → hidden so it isn't duplicated there.
app.MapPost("/api/jobs/{id}/send", (string id, JobRepository repo, CancellationToken ct) => sendToFrame(id, repo, hideFromGallery: false, ct));
app.MapPost("/api/jobs/{id}/resend", (string id, JobRepository repo, CancellationToken ct) => sendToFrame(id, repo, hideFromGallery: true, ct));
static async Task<IResult> sendToFrame(string id, JobRepository repo, bool hideFromGallery, CancellationToken ct)
{
    var job = await repo.GetAsync(id, ct);
    if (job is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(job.FullImageBase64))
        return Results.BadRequest(new { error = "That image can't be sent (no stored copy)." });

    var send = await repo.SubmitAsync("", job.SubmittedBy, job.FullImageBase64, autoSend: true, hideFromGallery: hideFromGallery, ct: ct);
    // A preview that's been approved has done its job — drop it so it doesn't linger (gallery shows the send).
    if (job.Status == JobStatus.Preview) await repo.DeleteAsync(id, ct);
    return Results.Ok(new { id = send.Id });
}

// Try again: regenerate a fresh image from the SAME request (prompt + reference photo), new preview.
app.MapPost("/api/jobs/{id}/retry", async (string id, JobRepository repo, CancellationToken ct) =>
{
    var job = await repo.GetAsync(id, ct);
    if (job is null) return Results.NotFound();
    var again = await repo.SubmitAsync(job.RawInput, job.SubmittedBy, job.ReferenceImageBase64, style: job.Style, ct: ct);
    if (job.Status == JobStatus.Preview) await repo.DeleteAsync(id, ct);
    return Results.Ok(new { id = again.Id });
});

// Download a finished image as a JPEG file.
app.MapGet("/api/jobs/{id}/image", async (string id, JobRepository repo, CancellationToken ct) =>
{
    var job = await repo.GetAsync(id, ct);
    if (job is null || string.IsNullOrWhiteSpace(job.FullImageBase64)) return Results.NotFound();
    byte[] bytes;
    try { bytes = Convert.FromBase64String(job.FullImageBase64); }
    catch (FormatException) { return Results.NotFound(); }

    string slug = new string((job.RawInput ?? "image").ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
    if (slug.Length == 0) slug = "image";
    if (slug.Length > 40) slug = slug[..40];
    return Results.File(bytes, "image/jpeg", $"frame-muse-{slug}.jpg");
});

// Delete a finished image from the gallery/history.
app.MapDelete("/api/jobs/{id}", async (string id, JobRepository repo, CancellationToken ct) =>
{
    await repo.DeleteAsync(id, ct);
    return Results.Ok();
});

// Recent finished images, for the little gallery under the box.
app.MapGet("/api/recent", async (JobRepository repo, CancellationToken ct) =>
{
    var done = await repo.RecentDoneAsync(12, ct);
    return Results.Ok(done.Select(j => new RecentItem(
        j.Id, j.RawInput, j.EnhancedPrompt, j.ThumbnailDataUri, j.CompletedAt)));
});

app.Run();

record SubmitRequest(string? Text, string? Name, string? ImageBase64, string? Style);
record SubmitResponse(string Id, int Position, int EtaSeconds);
record StatusResponse(string Id, string Status, string RawInput, string? EnhancedPrompt,
    string? Error, int Position, int EtaSeconds, string? ThumbnailDataUri, string? FullImageBase64);
record RecentItem(string Id, string RawInput, string? EnhancedPrompt, string? ThumbnailDataUri, DateTime? CompletedAt);
