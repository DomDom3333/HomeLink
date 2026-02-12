namespace HomeLink.Telemetry;

using System.Threading;

public class TelemetryDashboardState
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private const int MaxBatteryHistorySamples = 120_000;
    private static readonly TimeSpan MinimumPredictionWindow = TimeSpan.FromHours(1);

    private readonly object _timelineLock = new();
    private readonly Queue<DateTimeOffset> _displayRequestTimeline = new();
    private readonly Queue<DeviceBatterySample> _batteryHistory = new();
    private readonly RuntimeTelemetrySampler _runtimeTelemetrySampler;

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

    public TelemetryDashboardState(RuntimeTelemetrySampler runtimeTelemetrySampler)
    {
        _runtimeTelemetrySampler = runtimeTelemetrySampler;
    }

    public void RecordDisplay(double durationMs, bool isError, int? deviceBattery = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Record(ref _displayRequests, ref _displayErrors, ref _displayTotalDurationMs, ref _displayLastDurationMs, durationMs, isError);

        lock (_timelineLock)
        {
            _displayRequestTimeline.Enqueue(now);
            TrimOldEntries(now);

            if (deviceBattery.HasValue)
            {
                int clampedBattery = Math.Clamp(deviceBattery.Value, 0, 100);
                BatteryPrediction prediction = PredictBattery(clampedBattery, now);

                _batteryHistory.Enqueue(new DeviceBatterySample
                {
                    TimestampUtc = now,
                    BatteryPercent = clampedBattery,
                    PredictedBatteryPercent = prediction.NextHourBatteryPercent,
                    PredictedHoursToEmpty = prediction.HoursToEmpty
                });

                while (_batteryHistory.Count > MaxBatteryHistorySamples)
                {
                    _batteryHistory.Dequeue();
                }
            }
        }
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
        DeviceTelemetrySection device = CreateDeviceSection();

        return new TelemetryDashboardSnapshot
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Display = CreateSection(_displayRequests, _displayErrors, _displayTotalDurationMs, _displayLastDurationMs),
            Location = CreateSection(_locationUpdates, _locationErrors, _locationTotalDurationMs, _locationLastDurationMs),
            Spotify = CreateSection(_spotifyRequests, _spotifyErrors, _spotifyTotalDurationMs, _spotifyLastDurationMs),
            Device = device,
            Runtime = _runtimeTelemetrySampler.CreateSnapshot()
        };
    }

    private DeviceTelemetrySection CreateDeviceSection()
    {
        lock (_timelineLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TrimOldEntries(now);

            DeviceBatterySample? latest = _batteryHistory.Count > 0 ? _batteryHistory.Last() : null;
            List<DeviceBatterySample> history = _batteryHistory.ToList();

            return new DeviceTelemetrySection
            {
                LatestBatteryPercent = latest?.BatteryPercent,
                LatestPredictedBatteryPercent = latest?.PredictedBatteryPercent,
                LatestPredictedHoursToEmpty = latest?.PredictedHoursToEmpty,
                BatteryHistory = history,
                RequestsLastHour = CountSince(_displayRequestTimeline, now - OneHour),
                RequestsLastDay = CountSince(_displayRequestTimeline, now - OneDay),
                AvgRequestsPerHour = Math.Round(CalculateRate(_displayRequestTimeline, now, OneHour), 2),
                AvgRequestsPerDay = Math.Round(CalculateRate(_displayRequestTimeline, now, OneDay), 2)
            };
        }
    }

    private BatteryPrediction PredictBattery(int currentBatteryPercent, DateTimeOffset currentTimestampUtc)
    {
        List<DeviceBatterySample> candidates = _batteryHistory
            .Where(sample => sample.BatteryPercent.HasValue)
            .ToList();

        if (candidates.Count < 2)
        {
            return new BatteryPrediction(null, null);
        }

        DeviceBatterySample first = candidates[0];
        double totalSpanHours = (currentTimestampUtc - first.TimestampUtc).TotalHours;
        if (totalSpanHours < MinimumPredictionWindow.TotalHours)
        {
            return new BatteryPrediction(currentBatteryPercent, null);
        }

        // Use linear regression over retained history so brief battery dips are smoothed out
        // instead of becoming the sole baseline point.
        double xSum = 0;
        double ySum = 0;
        double xxSum = 0;
        double xySum = 0;

        foreach (DeviceBatterySample sample in candidates)
        {
            double x = (sample.TimestampUtc - first.TimestampUtc).TotalHours;
            double y = sample.BatteryPercent!.Value;

            xSum += x;
            ySum += y;
            xxSum += x * x;
            xySum += x * y;
        }

        double n = candidates.Count;
        double denominator = (n * xxSum) - (xSum * xSum);
        if (Math.Abs(denominator) < 0.000001)
        {
            return new BatteryPrediction(currentBatteryPercent, null);
        }

        double slopePerHour = ((n * xySum) - (xSum * ySum)) / denominator;

        // Flat/charging trend: keep battery prediction flat and no time-to-empty.
        if (slopePerHour >= 0)
        {
            return new BatteryPrediction(currentBatteryPercent, null);
        }

        double projectedNextHour = currentBatteryPercent + slopePerHour;
        int predictedNextHourPercent = (int)Math.Clamp(Math.Round(projectedNextHour, MidpointRounding.AwayFromZero), 0, 100);
        double hoursToEmpty = currentBatteryPercent / -slopePerHour;

        return new BatteryPrediction(predictedNextHourPercent, Math.Round(hoursToEmpty, 2));
    }

    private void TrimOldEntries(DateTimeOffset now)
    {
        DateTimeOffset oneDayAgo = now - OneDay;

        while (_displayRequestTimeline.Count > 0 && _displayRequestTimeline.Peek() < oneDayAgo)
        {
            _displayRequestTimeline.Dequeue();
        }

    }

    private static int CountSince(IEnumerable<DateTimeOffset> timeline, DateTimeOffset threshold)
        => timeline.Count(timestamp => timestamp >= threshold);

    private static double CalculateRate(IEnumerable<DateTimeOffset> timeline, DateTimeOffset now, TimeSpan window)
    {
        DateTimeOffset threshold = now - window;
        int countInWindow = timeline.Count(timestamp => timestamp >= threshold);
        return countInWindow / window.TotalHours;
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

    public DeviceTelemetrySection Device { get; set; } = new();

    public RuntimeTelemetrySection Runtime { get; set; } = new();
}

public class TelemetryDashboardSection
{
    public long Count { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public long LastDurationMs { get; set; }

    public double AvgDurationMs { get; set; }
}

public class DeviceTelemetrySection
{
    public int? LatestBatteryPercent { get; set; }

    public int? LatestPredictedBatteryPercent { get; set; }

    public double? LatestPredictedHoursToEmpty { get; set; }

    public int RequestsLastHour { get; set; }

    public int RequestsLastDay { get; set; }

    public double AvgRequestsPerHour { get; set; }

    public double AvgRequestsPerDay { get; set; }

    public List<DeviceBatterySample> BatteryHistory { get; set; } = new();
}

public class DeviceBatterySample
{
    public DateTimeOffset TimestampUtc { get; set; }

    public int? BatteryPercent { get; set; }

    public int? PredictedBatteryPercent { get; set; }

    public double? PredictedHoursToEmpty { get; set; }
}

public record BatteryPrediction(int? NextHourBatteryPercent, double? HoursToEmpty);

public class RuntimeTelemetrySection
{
    public RuntimeTelemetryPoint? Latest { get; set; }

    public List<RuntimeTelemetryPoint> History { get; set; } = new();
}

public class RuntimeTelemetryPoint
{
    public DateTimeOffset TimestampUtc { get; set; }

    public double ProcessCpuPercent { get; set; }

    public double WorkingSetMb { get; set; }

    public double GcHeapMb { get; set; }

    public int ThreadCount { get; set; }

    public int Gen0Collections { get; set; }

    public int Gen1Collections { get; set; }

    public int Gen2Collections { get; set; }
}
