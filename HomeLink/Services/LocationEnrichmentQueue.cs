using System.Collections.Concurrent;
using System.Threading.Channels;
using HomeLink.Models;

namespace HomeLink.Services;

public sealed record LocationEnrichmentJob(LocationInfo RawSnapshot, DateTime EnqueuedUtc);

public class LocationEnrichmentQueue
{
    private readonly ConcurrentDictionary<string, LocationEnrichmentJob> _latestByTracker = new();
    private readonly ConcurrentDictionary<string, byte> _queuedTrackers = new();
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Enqueue(LocationInfo rawSnapshot)
    {
        string trackerKey = string.IsNullOrWhiteSpace(rawSnapshot.TrackerId) ? "default" : rawSnapshot.TrackerId;
        _latestByTracker[trackerKey] = new LocationEnrichmentJob(rawSnapshot, DateTime.UtcNow);

        if (_queuedTrackers.TryAdd(trackerKey, 0))
        {
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
                return job;
            }
        }
    }
}
