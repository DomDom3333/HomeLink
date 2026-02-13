using HomeLink.Models;

namespace HomeLink.Services;

public sealed class DisplayFrameCacheService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _renderSignal = new(0, int.MaxValue);

    private DisplayFrameSnapshot? _latestFrame;
    private DisplayRenderRequestOptions _requestedOptions = new(true, null);

    public DisplayFrameSnapshot? GetLatestFrame()
    {
        lock (_sync)
        {
            return _latestFrame;
        }
    }

    public DisplayRenderRequestOptions GetRequestedRenderOptions()
    {
        lock (_sync)
        {
            return _requestedOptions;
        }
    }

    public void UpdateRequestedRenderOptions(bool dither, int? deviceBattery)
    {
        bool changed;
        lock (_sync)
        {
            DisplayRenderRequestOptions normalized = new(dither, NormalizeBattery(deviceBattery));
            changed = !_requestedOptions.Equals(normalized);
            _requestedOptions = normalized;
        }

        if (changed)
        {
            SignalRenderNeeded();
        }
    }

    public void UpdateFrame(EInkBitmap bitmap, string sourceHash, DateTimeOffset generatedAtUtc, TimeSpan renderDuration, string diagnostics, bool dither, int? deviceBattery)
    {
        DisplayFrameSnapshot snapshot = new()
        {
            FrameBytes = bitmap.PackedData.ToArray(),
            Width = bitmap.Width,
            Height = bitmap.Height,
            BytesPerLine = bitmap.BytesPerLine,
            Etag = QuoteTag(sourceHash),
            SourceHash = sourceHash,
            GeneratedAtUtc = generatedAtUtc,
            Dithered = dither,
            DeviceBattery = NormalizeBattery(deviceBattery),
            Diagnostics = diagnostics,
            RenderDurationMs = renderDuration.TotalMilliseconds
        };

        lock (_sync)
        {
            _latestFrame = snapshot;
        }
    }

    public async Task WaitForSignalOrIntervalAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await _renderSignal.WaitAsync(interval, cancellationToken);
            while (_renderSignal.Wait(0))
            {
                // coalesce pending signals into a single wake-up
            }
        }
        catch (OperationCanceledException)
        {
            // graceful cancellation path
        }
    }

    public void SignalRenderNeeded()
    {
        try
        {
            _renderSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Signal already queued.
        }
    }

    private static int? NormalizeBattery(int? battery)
    {
        return battery.HasValue ? Math.Clamp(battery.Value, 0, 100) : null;
    }

    private static string QuoteTag(string tag) => $"\"{tag}\"";
}

public sealed class DisplayFrameSnapshot
{
    public byte[] FrameBytes { get; init; } = Array.Empty<byte>();

    public int Width { get; init; }

    public int Height { get; init; }

    public int BytesPerLine { get; init; }

    public string Etag { get; init; } = "\"\"";

    public string SourceHash { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public bool Dithered { get; init; }

    public int? DeviceBattery { get; init; }

    public string Diagnostics { get; init; } = string.Empty;

    public double RenderDurationMs { get; init; }
}

public readonly record struct DisplayRenderRequestOptions(bool Dither, int? DeviceBattery);
