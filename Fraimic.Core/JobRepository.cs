using MongoDB.Driver;

namespace Fraimic.Core;

/// <summary>Mongo-backed job queue shared by the web app (producer) and the worker (consumer).</summary>
public sealed class JobRepository
{
    private readonly IMongoCollection<Job> _jobs;

    public JobRepository(string connectionString, string database = "FraimicMuse")
    {
        var client = new MongoClient(connectionString);
        _jobs = client.GetDatabase(database).GetCollection<Job>("jobs");

        // Index the queue scan (status + creation order) so claims and ETA counts stay fast.
        _jobs.Indexes.CreateOne(new CreateIndexModel<Job>(
            Builders<Job>.IndexKeys.Ascending(j => j.Status).Ascending(j => j.CreatedAt)));
    }

    /// <summary>Queue a new request. Returns the stored job (with its generated Id).</summary>
    public async Task<Job> SubmitAsync(string rawInput, string? submittedBy, string? referenceImageBase64 = null, bool autoSend = false, string? style = null, bool hideFromGallery = false, CancellationToken ct = default)
    {
        var job = new Job
        {
            RawInput = rawInput.Trim(),
            SubmittedBy = submittedBy,
            ReferenceImageBase64 = string.IsNullOrWhiteSpace(referenceImageBase64) ? null : referenceImageBase64,
            AutoSend = autoSend,
            Style = string.IsNullOrWhiteSpace(style) ? null : style.Trim(),
            HideFromGallery = hideFromGallery,
        };
        await _jobs.InsertOneAsync(job, cancellationToken: ct);
        return job;
    }

    public Task<Job?> GetAsync(string id, CancellationToken ct = default) =>
        _jobs.Find(j => j.Id == id).FirstOrDefaultAsync(ct)!;

    /// <summary>Delete a job (e.g. removing an image from the gallery).</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _jobs.DeleteOneAsync(j => j.Id == id, ct);

    private static readonly FilterDefinition<Job> ActiveFilter =
        Builders<Job>.Filter.In(j => j.Status, Job.ActiveStatuses);

    /// <summary>How many jobs are queued or in-flight (used for position/ETA).</summary>
    public Task<long> ActiveCountAsync(CancellationToken ct = default) =>
        _jobs.CountDocumentsAsync(ActiveFilter, cancellationToken: ct);

    /// <summary>Jobs still ahead of the given one in the queue (older and not finished).</summary>
    public Task<long> PositionAsync(Job job, CancellationToken ct = default) =>
        _jobs.CountDocumentsAsync(
            Builders<Job>.Filter.And(ActiveFilter, Builders<Job>.Filter.Lt(j => j.CreatedAt, job.CreatedAt)),
            cancellationToken: ct);

    /// <summary>Recent finished jobs, newest first — for the web gallery / "now showing". Excludes
    /// re-sends (HideFromGallery) so an image already in the gallery isn't duplicated when re-sent.
    /// Uses Ne(...,true) so older docs without the field still count.</summary>
    public Task<List<Job>> RecentDoneAsync(int limit = 12, CancellationToken ct = default) =>
        _jobs.Find(Builders<Job>.Filter.And(
                Builders<Job>.Filter.Eq(j => j.Status, JobStatus.Done),
                Builders<Job>.Filter.Ne(j => j.HideFromGallery, true)))
            .SortByDescending(j => j.CompletedAt)
            .Limit(limit)
            .ToListAsync(ct);

    /// <summary>
    /// Atomically claim the oldest queued job (sets it to <see cref="JobStatus.Claimed"/> and stamps
    /// StartedAt), so two workers never grab the same job. Returns null when the queue is empty.
    /// </summary>
    public Task<Job?> ClaimNextAsync(CancellationToken ct = default)
    {
        var update = Builders<Job>.Update
            .Set(j => j.Status, JobStatus.Claimed)
            .Set(j => j.StartedAt, DateTime.UtcNow)
            .Set(j => j.UpdatedAt, DateTime.UtcNow);
        var options = new FindOneAndUpdateOptions<Job>
        {
            Sort = Builders<Job>.Sort.Ascending(j => j.CreatedAt),
            ReturnDocument = ReturnDocument.After,
        };
        return _jobs.FindOneAndUpdateAsync<Job>(
            j => j.Status == JobStatus.Queued, update, options, ct)!;
    }

    /// <summary>Advance a job's status (and optionally attach fields) as the worker progresses.</summary>
    public async Task UpdateAsync(string id, JobStatus status, Action<UpdateDefinitionBuilder<Job>, List<UpdateDefinition<Job>>>? extra = null, CancellationToken ct = default)
    {
        var b = Builders<Job>.Update;
        var updates = new List<UpdateDefinition<Job>>
        {
            b.Set(j => j.Status, status),
            b.Set(j => j.UpdatedAt, DateTime.UtcNow),
        };
        if (status is JobStatus.Done or JobStatus.Failed)
            updates.Add(b.Set(j => j.CompletedAt, DateTime.UtcNow));
        extra?.Invoke(b, updates);
        await _jobs.UpdateOneAsync(j => j.Id == id, b.Combine(updates), cancellationToken: ct);
    }

    /// <summary>Average processing time (StartedAt→CompletedAt) over recent successes, for ETA.</summary>
    public async Task<TimeSpan> AverageProcessingTimeAsync(TimeSpan fallback, int sample = 10, CancellationToken ct = default)
    {
        var recent = await _jobs
            .Find(j => j.Status == JobStatus.Done && j.StartedAt != null && j.CompletedAt != null)
            .SortByDescending(j => j.CompletedAt).Limit(sample).ToListAsync(ct);
        if (recent.Count == 0) return fallback;
        double avgSeconds = recent.Average(j => (j.CompletedAt!.Value - j.StartedAt!.Value).TotalSeconds);
        return TimeSpan.FromSeconds(avgSeconds);
    }
}
