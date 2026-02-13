using System.Collections.Concurrent;
using System.Threading.Channels;
using HomeLink.Models;
using HomeLink.Telemetry;

namespace HomeLink.Services;

public sealed record LocationEnrichmentJob(LocationInfo RawSnapshot, DateTimeOffset EnqueuedUtc);

public class LocationEnrichmentQueue
{
    private readonly ConcurrentDictionary<string, LocationEnrichmentJob> _latestByTracker = new();
    private int _queueDepth;
    private readonly ConcurrentDictionary<string, byte> _queuedTrackers = new();
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Enqueue(LocationInfo rawSnapshot)
    {
        string trackerKey = string.IsNullOrWhiteSpace(rawSnapshot.TrackerId) ? "default" : rawSnapshot.TrackerId;
        _latestByTracker[trackerKey] = new LocationEnrichmentJob(rawSnapshot, DateTimeOffset.UtcNow);

        if (_queuedTrackers.TryAdd(trackerKey, 0))
        {
            Interlocked.Increment(ref _queueDepth);
            HomeLinkTelemetry.WorkerQueueDepth.Add(1, new KeyValuePair<string, object?>("queue", "location_enrichment"));
            _signals.Writer.TryWrite(trackerKey);
        }
    }

    public async ValueTask<LocationEnrichmentJob> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string trackerKey = await _signals.Reader.ReadAsync(cancellationToken);
            _queuedTrackers.TryRemove(trackerKey, out _);

            if (_latestByTracker.TryRemove(trackerKey, out LocationEnrichmentJob? job))
            {
                Interlocked.Decrement(ref _queueDepth);
                HomeLinkTelemetry.WorkerQueueDepth.Add(-1, new KeyValuePair<string, object?>("queue", "location_enrichment"));
                return job;
            }
        }
    }

    public int Depth => Math.Max(0, Volatile.Read(ref _queueDepth));
}
