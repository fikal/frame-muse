using Fraimic.Api;
using Fraimic.Core;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fraimic.Worker;

/// <summary>
/// The heart of the studio PC: pull the next queued request, enhance → generate → encode → upload to
/// the frame, updating job status the whole way so the phone can follow along.
/// </summary>
public sealed class PipelineWorker(
    JobRepository repo,
    IPromptEnhancer enhancer,
    IImageGenerator generator,
    SafetyScreen safety,
    IOptions<WorkerOptions> options,
    ILogger<PipelineWorker> log) : BackgroundService
{
    private readonly WorkerOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var frameSize = _opt.FrameModel.Equals("standard", StringComparison.OrdinalIgnoreCase)
            ? FrameSize.StandardCanvas : FrameSize.LargeCanvas;
        log.LogInformation("Color engine: Fraimic official converter port (enhance + perceptual + Atkinson, pure C#).");
        var delay = TimeSpan.FromSeconds(Math.Max(1, _opt.PollIntervalSeconds));
        log.LogInformation("Worker started. Frame={Host} ({Model}), poll={Delay}s.",
            _opt.FrameHost, _opt.FrameModel, delay.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            Job? job = null;
            try
            {
                job = await repo.ClaimNextAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to poll the queue; retrying.");
            }

            if (job is null)
            {
                await Task.Delay(delay, stoppingToken);
                continue;
            }

            await ProcessAsync(job, frameSize, stoppingToken);
        }
    }

    private async Task ProcessAsync(Job job, FrameSize frameSize, CancellationToken ct)
    {
        log.LogInformation("Job {Id}: \"{Text}\"", job.Id, job.RawInput);
        try
        {
            // Decode the reference photo (if any) up front — it drives which mode we run.
            byte[]? reference = null;
            if (!string.IsNullOrWhiteSpace(job.ReferenceImageBase64))
            {
                try { reference = System.Convert.FromBase64String(job.ReferenceImageBase64); }
                catch (FormatException) { log.LogWarning("Job {Id}: bad reference image, ignoring.", job.Id); }
            }
            bool directPhoto = string.IsNullOrWhiteSpace(job.RawInput) && reference is not null;

            // 0) Screen the request text (unless it's a bare photo with no prompt).
            if (!directPhoto && !await safety.IsPromptAllowedAsync(job.RawInput, ct))
            {
                await repo.UpdateAsync(job.Id, JobStatus.Failed,
                    (b, u) => u.Add(b.Set(j => j.Error, "That request can't be shown on the frame — keep it family-friendly.")), ct);
                log.LogInformation("Job {Id}: blocked (prompt).", job.Id);
                return;
            }

            // Screen the reference photo itself (blocks NSFW source material and bare-photo uploads).
            if (reference is not null && !await safety.IsImageAllowedAsync(reference, ct))
            {
                await repo.UpdateAsync(job.Id, JobStatus.Failed,
                    (b, u) => u.Add(b.Set(j => j.Error, "The photo can't be used — keep it family-friendly.")), ct);
                log.LogInformation("Job {Id}: blocked (reference photo).", job.Id);
                return;
            }

            byte[] imageBytes;
            if (directPhoto)
            {
                // 1D) Bare photo, no prompt → just show it on the frame (no AI).
                await repo.UpdateAsync(job.Id, JobStatus.Encoding,
                    (b, u) => u.Add(b.Set(j => j.EnhancedPrompt, "(your photo)")), ct);
                imageBytes = reference!;
            }
            else
            {
                // 1) Enhance the prompt, then generate (face-identity / img2img if a photo is attached).
                await repo.UpdateAsync(job.Id, JobStatus.Enhancing, ct: ct);
                // A reference photo means "put me / this face into the scene" → keep the face forward.
                string prompt = await enhancer.EnhanceAsync(job.RawInput, portraitMode: reference is not null, style: job.Style, ct: ct);
                await repo.UpdateAsync(job.Id, JobStatus.Generating,
                    (b, u) => u.Add(b.Set(j => j.EnhancedPrompt, prompt)), ct);

                imageBytes = await generator.GenerateAsync(prompt, _opt.GenerationWidth, _opt.GenerationHeight, reference, style: job.Style, ct: ct);

                // 2b) Screen the generated image — the real safety net. Fails closed.
                if (!await safety.IsImageAllowedAsync(imageBytes, ct))
                {
                    await repo.UpdateAsync(job.Id, JobStatus.Failed,
                        (b, u) => u.Add(b.Set(j => j.Error, "The generated image was blocked by the content filter.")), ct);
                    log.LogInformation("Job {Id}: blocked (image).", job.Id);
                    return;
                }
            }

            // 2c) Retro post-process for styles the model can't do natively (Pixel Art → real blockiness).
            if (StyleCatalog.Pixelate(job.Style))
                imageBytes = ImageOps.Pixelate(imageBytes, 160);

            // 3) Build the web preview (thumbnail + full JPEG).
            string thumb = ImageOps.MakeThumbnail(imageBytes);
            string full = ImageOps.MakeFullJpeg(imageBytes);   // shown for review; also re-sendable from the gallery

            // Preview-first (default): stop here and let the user review + choose to send it. Only jobs
            // marked AutoSend (a "send to frame" action, or a bare-photo direct upload) go straight on.
            if (!job.AutoSend)
            {
                await repo.UpdateAsync(job.Id, JobStatus.Preview,
                    (b, u) => { u.Add(b.Set(j => j.ThumbnailDataUri, thumb)); u.Add(b.Set(j => j.FullImageBase64, full)); }, ct);
                log.LogInformation("Job {Id}: preview ready (awaiting send).", job.Id);
                return;
            }

            // 3b) Encode to the frame's .bin (pure-C# color engine).
            await repo.UpdateAsync(job.Id, JobStatus.Encoding,
                (b, u) => { u.Add(b.Set(j => j.ThumbnailDataUri, thumb)); u.Add(b.Set(j => j.FullImageBase64, full)); }, ct);
            byte[] bin;
            using (var image = Image.Load<Rgb24>(imageBytes))
                bin = FraimicConverter.Convert(image, frameSize, fit: FitMode.Fill, brightnessBoost: _opt.FrameBrightness);
            await repo.UpdateAsync(job.Id, JobStatus.Uploading, ct: ct);

            // 4) Upload to the frame (auto-refreshes).
            await using var device = new FraimicDevice(_opt.FrameHost, logger: log);
            var result = await device.UploadImageAsync(bin);
            if (!result.Success)
                throw new InvalidOperationException($"Frame upload failed: HTTP {result.StatusCode} {result.Body}");

            await repo.UpdateAsync(job.Id, JobStatus.Done, ct: ct);
            log.LogInformation("Job {Id}: done, now on the frame.", job.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {Id} failed.", job.Id);
            try
            {
                await repo.UpdateAsync(job.Id, JobStatus.Failed,
                    (b, u) => u.Add(b.Set(j => j.Error, Summarize(ex.Message))), CancellationToken.None);
            }
            catch { /* best effort */ }
        }
    }

    private static string Summarize(string message) =>
        message.Length <= 200 ? message : message[..200] + "…";
}
