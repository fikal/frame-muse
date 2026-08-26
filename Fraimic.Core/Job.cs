using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Fraimic.Core;

/// <summary>Lifecycle of an image request as it moves through the queue and the worker pipeline.</summary>
public enum JobStatus
{
    /// <summary>Submitted, waiting for the worker to claim it.</summary>
    Queued,
    /// <summary>Claimed by a worker (atomically) but not yet started.</summary>
    Claimed,
    /// <summary>The raw text is being expanded into a rich image prompt.</summary>
    Enhancing,
    /// <summary>The image is being generated on the GPU.</summary>
    Generating,
    /// <summary>The image is being converted to the frame's .bin format.</summary>
    Encoding,
    /// <summary>The .bin is being uploaded to the frame.</summary>
    Uploading,
    /// <summary>Generated and waiting for the user to review it and choose to send it to the frame.</summary>
    Preview,
    /// <summary>Finished and shown on the frame.</summary>
    Done,
    /// <summary>Failed; see <see cref="Job.Error"/>.</summary>
    Failed,
}

/// <summary>
/// One image request. Created by the web app, claimed and processed by the worker on the GPU box.
/// Stored in MongoDB (database <c>FraimicMuse</c>, collection <c>jobs</c>).
/// </summary>
public sealed class Job
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>The user's spoken or typed request, verbatim.</summary>
    public string RawInput { get; set; } = "";

    /// <summary>The enhanced prompt actually sent to the image generator (filled in by the worker).</summary>
    public string? EnhancedPrompt { get; set; }

    /// <summary>Optional reference photo (base64 JPEG, no data-URI prefix) for image-to-image.</summary>
    public string? ReferenceImageBase64 { get; set; }

    [BsonRepresentation(BsonType.String)]
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Optional label for who submitted it (device name / nickname), for a shared frame.</summary>
    public string? SubmittedBy { get; set; }

    /// <summary>When true the worker uploads straight to the frame; when false it stops at
    /// <see cref="JobStatus.Preview"/> so the user can review and choose to send it.</summary>
    public bool AutoSend { get; set; }

    /// <summary>Art-style key chosen in the web dropdown (see StyleCatalog): auto, realistic, cartoon,
    /// anime, comic, watercolor, oil, pixel, poster. Drives the appended style cues + any post-process.</summary>
    public string? Style { get; set; }

    /// <summary>Set on jobs that just re-send an already-saved image to the frame, so the gallery doesn't
    /// show a duplicate of a picture that's already in it.</summary>
    public bool HideFromGallery { get; set; }

    /// <summary>Small JPEG data-URI preview of the generated image, for the web UI.</summary>
    public string? ThumbnailDataUri { get; set; }

    /// <summary>Full generated image (base64 JPEG, no prefix) kept so the gallery can re-send it to the frame.</summary>
    public string? FullImageBase64 { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Statuses that still occupy the queue (not yet finished or failed).</summary>
    public static readonly JobStatus[] ActiveStatuses =
    [
        JobStatus.Queued, JobStatus.Claimed, JobStatus.Enhancing,
        JobStatus.Generating, JobStatus.Encoding, JobStatus.Uploading,
    ];
}
