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
}
