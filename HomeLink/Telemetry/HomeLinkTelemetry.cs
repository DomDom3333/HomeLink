namespace HomeLink;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class HomeLinkTelemetry
{
    public const string ActivitySourceName = "HomeLink.Telemetry";
    public const string MeterName = "HomeLink.Metrics";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> DisplayRenderRequests = Meter.CreateCounter<long>(
        "homelink.display.render.requests",
        description: "Number of display render requests received.");

    public static readonly Counter<long> LocationUpdates = Meter.CreateCounter<long>(
        "homelink.location.updates",
        description: "Number of OwnTracks location updates processed.");

    public static readonly Counter<long> SpotifyRequests = Meter.CreateCounter<long>(
        "homelink.spotify.requests",
        description: "Number of Spotify currently-playing requests.");

    public static readonly Histogram<double> DisplayRenderDurationMs = Meter.CreateHistogram<double>(
        "homelink.display.render.duration",
        unit: "ms",
        description: "Duration of display rendering request processing.");

    public static readonly Histogram<double> LocationLookupDurationMs = Meter.CreateHistogram<double>(
        "homelink.location.lookup.duration",
        unit: "ms",
        description: "Duration of reverse-geocode and location enrichment.");

    public static readonly Histogram<double> SpotifyRequestDurationMs = Meter.CreateHistogram<double>(
        "homelink.spotify.currently_playing.duration",
        unit: "ms",
        description: "Duration of Spotify currently-playing fetch operations.");

    public static readonly Histogram<double> DrawingStageDurationMs = Meter.CreateHistogram<double>(
        "homelink.drawing.stage.duration",
        unit: "ms",
        description: "Duration of drawing pipeline stages with component/stage tags.");

    public static readonly Histogram<double> LocationRawIngestDurationMs = Meter.CreateHistogram<double>(
        "homelink.location.raw_ingest.duration",
        unit: "ms",
        description: "Duration of ingesting and caching a raw OwnTracks location snapshot.");

    public static readonly Histogram<double> LocationReverseGeocodeDurationMs = Meter.CreateHistogram<double>(
        "homelink.location.reverse_geocode.duration",
        unit: "ms",
        description: "Duration of reverse-geocode network calls.");

    public static readonly Histogram<double> LocationPersistenceDurationMs = Meter.CreateHistogram<double>(
        "homelink.location.persistence.duration",
        unit: "ms",
        description: "Duration of location persistence operations.");

    public static readonly Histogram<double> SpotifyPollCycleDurationMs = Meter.CreateHistogram<double>(
        "homelink.spotify.poll_cycle.duration",
        unit: "ms",
        description: "Duration of Spotify polling cycles.");

    public static readonly Histogram<double> SpotifyTokenRefreshDurationMs = Meter.CreateHistogram<double>(
        "homelink.spotify.token_refresh.duration",
        unit: "ms",
        description: "Duration of Spotify token refresh operations.");

    public static readonly Histogram<double> SpotifySnapshotAgeMs = Meter.CreateHistogram<double>(
        "homelink.spotify.snapshot_age",
        unit: "ms",
        description: "Age of Spotify track snapshot data when consumed.");

    public static readonly Histogram<double> WorkerQueueEnqueueToStartLagMs = Meter.CreateHistogram<double>(
        "homelink.worker.queue.enqueue_to_start_lag",
        unit: "ms",
        description: "Lag from queue enqueue to worker processing start.");

    public static readonly Histogram<double> WorkerQueueProcessingDurationMs = Meter.CreateHistogram<double>(
        "homelink.worker.queue.processing.duration",
        unit: "ms",
        description: "Worker processing time for queued jobs.");

    public static readonly UpDownCounter<long> WorkerQueueDepth = Meter.CreateUpDownCounter<long>(
        "homelink.worker.queue.depth",
        description: "Current queue depth by worker queue.");
}
