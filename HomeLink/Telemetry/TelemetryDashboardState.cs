namespace HomeLink.Telemetry;

using System.Threading;

public class TelemetryDashboardState
{
    private long _displayRequests;
    private long _displayErrors;
    private long _displayTotalDurationMs;
    private long _displayLastDurationMs;

    private long _locationUpdates;
    private long _locationErrors;
    private long _locationTotalDurationMs;
    private long _locationLastDurationMs;

    private long _spotifyRequests;
    private long _spotifyErrors;
    private long _spotifyTotalDurationMs;
    private long _spotifyLastDurationMs;

    public void RecordDisplay(double durationMs, bool isError)
    {
        Record(ref _displayRequests, ref _displayErrors, ref _displayTotalDurationMs, ref _displayLastDurationMs, durationMs, isError);
    }

    public void RecordLocation(double durationMs, bool isError)
    {
        Record(ref _locationUpdates, ref _locationErrors, ref _locationTotalDurationMs, ref _locationLastDurationMs, durationMs, isError);
    }

    public void RecordSpotify(double durationMs, bool isError)
    {
        Record(ref _spotifyRequests, ref _spotifyErrors, ref _spotifyTotalDurationMs, ref _spotifyLastDurationMs, durationMs, isError);
    }

    public TelemetryDashboardSnapshot CreateSnapshot()
    {
        return new TelemetryDashboardSnapshot
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Display = CreateSection(_displayRequests, _displayErrors, _displayTotalDurationMs, _displayLastDurationMs),
            Location = CreateSection(_locationUpdates, _locationErrors, _locationTotalDurationMs, _locationLastDurationMs),
            Spotify = CreateSection(_spotifyRequests, _spotifyErrors, _spotifyTotalDurationMs, _spotifyLastDurationMs)
        };
    }

    private static TelemetryDashboardSection CreateSection(long requests, long errors, long totalDurationMs, long lastDurationMs)
    {
        long count = Interlocked.Read(ref requests);
        long errorCount = Interlocked.Read(ref errors);
        long total = Interlocked.Read(ref totalDurationMs);
        long last = Interlocked.Read(ref lastDurationMs);

        double avg = count == 0 ? 0 : (double)total / count;
        double errorRate = count == 0 ? 0 : (double)errorCount / count;

        return new TelemetryDashboardSection
        {
            Count = count,
            Errors = errorCount,
            ErrorRate = Math.Round(errorRate * 100, 2),
            LastDurationMs = last,
            AvgDurationMs = Math.Round(avg, 2)
        };
    }

    private static void Record(ref long count, ref long errors, ref long totalDurationMs, ref long lastDurationMs, double durationMs, bool isError)
    {
        long roundedDuration = (long)Math.Round(durationMs, MidpointRounding.AwayFromZero);
        Interlocked.Increment(ref count);
        Interlocked.Exchange(ref lastDurationMs, roundedDuration);
        Interlocked.Add(ref totalDurationMs, roundedDuration);

        if (isError)
        {
            Interlocked.Increment(ref errors);
        }
    }
}

public class TelemetryDashboardSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; set; }

    public TelemetryDashboardSection Display { get; set; } = new();

    public TelemetryDashboardSection Location { get; set; } = new();

    public TelemetryDashboardSection Spotify { get; set; } = new();
}

public class TelemetryDashboardSection
{
    public long Count { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public long LastDurationMs { get; set; }

    public double AvgDurationMs { get; set; }
}
