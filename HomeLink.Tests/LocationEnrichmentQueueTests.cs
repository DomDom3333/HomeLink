using HomeLink.Models;
using HomeLink.Services;

namespace HomeLink.Tests;

public class LocationEnrichmentQueueTests
{
    [Fact]
    public async Task Enqueue_BurstyUpdatesForSameTracker_CoalescesToLatestSnapshot()
    {
        LocationEnrichmentQueue queue = new();

        queue.Enqueue(new LocationInfo { Latitude = 1, Longitude = 1, TrackerId = "esp32-main" });
        queue.Enqueue(new LocationInfo { Latitude = 2, Longitude = 2, TrackerId = "esp32-main" });
        queue.Enqueue(new LocationInfo { Latitude = 3, Longitude = 3, TrackerId = "esp32-main" });

        Assert.Equal(1, queue.Depth);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
        LocationEnrichmentJob job = await queue.DequeueAsync(cts.Token);

        Assert.Equal(3, job.RawSnapshot.Latitude);
        Assert.Equal(3, job.RawSnapshot.Longitude);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task Enqueue_MultipleTrackers_PreservesDistinctPendingJobs()
    {
        LocationEnrichmentQueue queue = new();
        queue.Enqueue(new LocationInfo { Latitude = 10, Longitude = 10, TrackerId = "esp32-a" });
        queue.Enqueue(new LocationInfo { Latitude = 20, Longitude = 20, TrackerId = "esp32-b" });

        Assert.Equal(2, queue.Depth);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
        LocationEnrichmentJob first = await queue.DequeueAsync(cts.Token);
        LocationEnrichmentJob second = await queue.DequeueAsync(cts.Token);

        Assert.NotEqual(first.RawSnapshot.TrackerId, second.RawSnapshot.TrackerId);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task Enqueue_EmptyTrackerId_UsesDefaultQueueKey()
    {
        LocationEnrichmentQueue queue = new();

        queue.Enqueue(new LocationInfo { Latitude = 1, Longitude = 1, TrackerId = "" });
        queue.Enqueue(new LocationInfo { Latitude = 9, Longitude = 9, TrackerId = null });

        Assert.Equal(1, queue.Depth);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
        LocationEnrichmentJob job = await queue.DequeueAsync(cts.Token);
        Assert.Equal(9, job.RawSnapshot.Latitude);
    }
}
