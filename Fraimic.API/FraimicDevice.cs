using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fraimic.Api;

/// <summary>
/// Robust front door to a Fraimic frame. Every call is placed on a single serial queue and sent
/// one at a time, with a minimum spacing (default 5s) between requests — the frame penalizes
/// back-to-back requests with a ~45 second stall, so the queue keeps it healthy.
///
/// All activity and errors are written through the supplied <see cref="ILogger"/>.
/// Dispose (await using) to drain the queue and shut the worker down cleanly.
/// </summary>
public sealed class FraimicDevice : IAsyncDisposable
{
    /// <summary>Default minimum gap between consecutive requests.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(5);

    private readonly FraimicClient _client;
    private readonly ILogger _logger;
    private readonly TimeSpan _minInterval;
    private readonly Channel<QueuedRequest> _queue;
    private readonly Task _worker;
    private readonly CancellationTokenSource _shutdown = new();
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public FraimicDevice(
        string host = "fraimic.local",
        TimeSpan? minInterval = null,
        ILogger? logger = null,
        HttpClient? http = null)
    {
        _client = new FraimicClient(host, http);
        _logger = logger ?? NullLogger.Instance;
        _minInterval = minInterval ?? DefaultMinInterval;
        _queue = Channel.CreateUnbounded<QueuedRequest>(new UnboundedChannelOptions { SingleReader = true });
        _worker = Task.Run(() => ProcessQueueAsync(_shutdown.Token));
        _logger.LogInformation("FraimicDevice ready (host={Host}, minInterval={Seconds}s).",
            host, _minInterval.TotalSeconds);
    }

    // ---- Public API: every method enqueues and returns a task that completes when its turn runs ----

    /// <summary>
    /// Upload a .bin via POST /upload (queued + throttled). Because /upload only stores the image,
    /// a display refresh is triggered afterward by default (non-fatal if it fails). Returns the
    /// upload response.
    /// </summary>
    public async Task<FraimicResponse> UploadImageAsync(byte[] bin, bool refresh = true, CancellationToken ct = default)
    {
        FraimicResponse upload = await Enqueue("upload image", c => _client.UploadImageAsync(bin, c), ct);
        if (refresh && upload.Success)
        {
            try
            {
                await Enqueue("refresh display (after upload)", _client.RefreshAsync, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-upload refresh failed (non-fatal).");
            }
        }
        return upload;
    }

    public Task<FraimicResponse> RefreshAsync(CancellationToken ct = default)
        => Enqueue("refresh display", _client.RefreshAsync, ct);

    public Task<FraimicResponse> RestartAsync(CancellationToken ct = default)
        => Enqueue("restart device", _client.RestartAsync, ct);

    public Task<FraimicResponse> SleepAsync(CancellationToken ct = default)
        => Enqueue("enter deep sleep", _client.SleepAsync, ct);

    public Task<FraimicResponse> GetInfoAsync(CancellationToken ct = default)
        => Enqueue("get device info", _client.GetInfoAsync, ct);

    public Task<FraimicResponse> GetBatteryAsync(CancellationToken ct = default)
        => Enqueue("get battery status", _client.GetBatteryAsync, ct);

    // ---------------------------------------------------------------------------------------------

    private Task<FraimicResponse> Enqueue(
        string description, Func<CancellationToken, Task<FraimicResponse>> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<FraimicResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new QueuedRequest(description, action, tcs, ct);

        if (_queue.Writer.TryWrite(request))
        {
            _logger.LogInformation("Queued: {Description}.", description);
        }
        else
        {
            _logger.LogError("Could not queue '{Description}' — device is shutting down.", description);
            tcs.TrySetException(new InvalidOperationException("FraimicDevice is disposed; queue is closed."));
        }
        return tcs.Task;
    }

    private async Task ProcessQueueAsync(CancellationToken shutdown)
    {
        try
        {
            await foreach (QueuedRequest req in _queue.Reader.ReadAllAsync(shutdown))
            {
                if (req.Cancellation.IsCancellationRequested)
                {
                    _logger.LogWarning("Skipped (canceled before send): {Description}.", req.Description);
                    req.Completion.TrySetCanceled(req.Cancellation);
                    continue;
                }

                await ThrottleAsync(req.Description, shutdown);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, req.Cancellation);
                _logger.LogInformation("Sending: {Description}.", req.Description);
                try
                {
                    FraimicResponse resp = await req.Action(linked.Token);
                    if (resp.Success)
                    {
                        _logger.LogInformation("Done [{Status}] {Description}: {Body}",
                            resp.StatusCode, req.Description, resp.Body);
                    }
                    else
                    {
                        _logger.LogWarning("Device returned error [{Status}] for {Description}: {Body}",
                            resp.StatusCode, req.Description, resp.Body);
                    }
                    req.Completion.TrySetResult(resp);
                }
                catch (OperationCanceledException) when (req.Cancellation.IsCancellationRequested)
                {
                    _logger.LogWarning("Canceled during send: {Description}.", req.Description);
                    req.Completion.TrySetCanceled(req.Cancellation);
                }
                catch (Exception ex)
                {
                    // Transport/connection failure — log it and surface to the caller, keep the queue alive.
                    _logger.LogError(ex, "Request failed: {Description}.", req.Description);
                    req.Completion.TrySetException(ex);
                }
                finally
                {
                    _lastRequestAt = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Forced shutdown: fail anything still queued so callers don't hang.
            DrainPending();
        }
        _logger.LogInformation("Queue worker stopped.");
    }

    /// <summary>Wait until at least <see cref="_minInterval"/> has elapsed since the last request finished.</summary>
    private async Task ThrottleAsync(string description, CancellationToken shutdown)
    {
        TimeSpan wait = _minInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
        if (wait > TimeSpan.Zero)
        {
            _logger.LogDebug("Throttling {Seconds:0.0}s before: {Description}.", wait.TotalSeconds, description);
            await Task.Delay(wait, shutdown);
        }
    }

    private void DrainPending()
    {
        while (_queue.Reader.TryRead(out QueuedRequest? req))
        {
            _logger.LogWarning("Dropped (shutdown): {Description}.", req.Description);
            req.Completion.TrySetException(new OperationCanceledException("FraimicDevice was disposed before this request ran."));
        }
    }

    /// <summary>Stop accepting new requests, let the worker drain what's queued, then shut down.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _worker;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queue worker faulted during shutdown.");
        }
        _shutdown.Cancel();
        _shutdown.Dispose();
        _logger.LogInformation("FraimicDevice disposed.");
    }

    private sealed record QueuedRequest(
        string Description,
        Func<CancellationToken, Task<FraimicResponse>> Action,
        TaskCompletionSource<FraimicResponse> Completion,
        CancellationToken Cancellation);
}
