namespace HomeLink.Telemetry;

using System.Threading;

public class TelemetryDashboardState
{
    private static readonly TimeSpan CorrelationWindow15m = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CorrelationWindow1h = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan LatencyBucketSize = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LatencyHistoryWindow = TimeSpan.FromHours(6);
    private const int MaxBatteryHistorySamples = 120_000;
    private static readonly TimeSpan MinimumPredictionWindow = TimeSpan.FromHours(1);

    private readonly object _timelineLock = new();
    private readonly object _latencyLock = new();
    private readonly Queue<DateTimeOffset> _displayRequestTimeline = new();
    private readonly Queue<DeviceBatterySample> _batteryHistory = new();
    private readonly Queue<LatencyBucketAccumulator> _displayLatencyBuckets = new();
    private readonly Queue<LatencyBucketAccumulator> _locationLatencyBuckets = new();
    private readonly Queue<LatencyBucketAccumulator> _spotifyLatencyBuckets = new();
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
        RecordLatency(_displayLatencyBuckets, durationMs, now);

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
        RecordLatency(_locationLatencyBuckets, durationMs, DateTimeOffset.UtcNow);
    }

    public void RecordSpotify(double durationMs, bool isError)
    {
        Record(ref _spotifyRequests, ref _spotifyErrors, ref _spotifyTotalDurationMs, ref _spotifyLastDurationMs, durationMs, isError);
        RecordLatency(_spotifyLatencyBuckets, durationMs, DateTimeOffset.UtcNow);
    }

    public TelemetryDashboardSnapshot CreateSnapshot()
    {
        DeviceTelemetrySection device = CreateDeviceSection();
        RuntimeTelemetrySection runtime = _runtimeTelemetrySampler.CreateSnapshot();
        TelemetryCorrelationResult correlation15m = CalculateCpuToDisplayLatencyCorrelation(runtime.History, CorrelationWindow15m);
        TelemetryCorrelationResult correlation1h = CalculateCpuToDisplayLatencyCorrelation(runtime.History, CorrelationWindow1h);

        return new TelemetryDashboardSnapshot
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Display = CreateSection(_displayRequests, _displayErrors, _displayTotalDurationMs, _displayLastDurationMs),
            Location = CreateSection(_locationUpdates, _locationErrors, _locationTotalDurationMs, _locationLastDurationMs),
            Spotify = CreateSection(_spotifyRequests, _spotifyErrors, _spotifyTotalDurationMs, _spotifyLastDurationMs),
            Device = device,
            Runtime = runtime,
            TimeSeries = CreateTimeSeriesSection(),
            CpuToDisplayLatencyCorrelation15m = correlation15m,
            CpuToDisplayLatencyCorrelation1h = correlation1h
        };
    }

    private TelemetryCorrelationResult CalculateCpuToDisplayLatencyCorrelation(
        IReadOnlyList<RuntimeTelemetryPoint> runtimeHistory,
        TimeSpan window)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset threshold = now - window;
        List<RuntimeTelemetryPoint> recentCpu = runtimeHistory
            .Where(point => point.TimestampUtc >= threshold)
            .ToList();

        if (recentCpu.Count == 0)
        {
            return TelemetryCorrelationResult.Empty(window);
        }

        List<LatencyBucketAccumulator> latencyBuckets;
        lock (_latencyLock)
        {
            TrimOldLatencyBuckets(_displayLatencyBuckets, now);
            latencyBuckets = _displayLatencyBuckets
                .Where(bucket => bucket.BucketStartUtc >= threshold && bucket.SampleCount > 0)
                .ToList();
        }

        if (latencyBuckets.Count == 0)
        {
            return TelemetryCorrelationResult.Empty(window);
        }

        List<double> cpuSamples = new();
        List<double> latencySamples = new();

        foreach (LatencyBucketAccumulator bucket in latencyBuckets)
        {
            DateTimeOffset bucketEnd = bucket.BucketStartUtc + LatencyBucketSize;
            List<RuntimeTelemetryPoint> bucketCpu = recentCpu
                .Where(point => point.TimestampUtc >= bucket.BucketStartUtc && point.TimestampUtc < bucketEnd)
                .ToList();

            if (bucketCpu.Count == 0)
            {
                continue;
            }

            cpuSamples.Add(bucketCpu.Average(point => point.ProcessCpuPercent));
            latencySamples.Add(bucket.AverageDurationMs);
        }

        int sampleSize = cpuSamples.Count;
        if (sampleSize < 3)
        {
            return TelemetryCorrelationResult.Empty(window, sampleSize);
        }

        return new TelemetryCorrelationResult
        {
            WindowSeconds = (int)window.TotalSeconds,
            SampleSize = sampleSize,
            Pearson = Math.Round(TelemetryCorrelationCalculator.Pearson(cpuSamples, latencySamples), 3),
            Spearman = Math.Round(TelemetryCorrelationCalculator.Spearman(cpuSamples, latencySamples), 3),
            Confidence = TelemetryCorrelationCalculator.GetConfidence(sampleSize)
        };
    }

    private TelemetryTimeSeriesSection CreateTimeSeriesSection()
    {
        lock (_latencyLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TrimOldLatencyBuckets(_displayLatencyBuckets, now);
            TrimOldLatencyBuckets(_locationLatencyBuckets, now);
            TrimOldLatencyBuckets(_spotifyLatencyBuckets, now);

            return new TelemetryTimeSeriesSection
            {
                BucketSizeSeconds = (int)LatencyBucketSize.TotalSeconds,
                DisplayLatency = _displayLatencyBuckets.Select(CreateLatencyPoint).ToList(),
                LocationLatency = _locationLatencyBuckets.Select(CreateLatencyPoint).ToList(),
                SpotifyLatency = _spotifyLatencyBuckets.Select(CreateLatencyPoint).ToList()
            };
        }
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

    private void RecordLatency(Queue<LatencyBucketAccumulator> buckets, double durationMs, DateTimeOffset now)
    {
        long roundedDuration = Math.Max(0, (long)Math.Round(durationMs, MidpointRounding.AwayFromZero));
        DateTimeOffset bucketStart = GetBucketStart(now);

        lock (_latencyLock)
        {
            if (buckets.Count == 0 || buckets.Last().BucketStartUtc != bucketStart)
            {
                buckets.Enqueue(new LatencyBucketAccumulator(bucketStart));
            }

            buckets.Last().AddSample(roundedDuration);
            TrimOldLatencyBuckets(buckets, now);
        }
    }

    private static DateTimeOffset GetBucketStart(DateTimeOffset timestamp)
    {
        long bucketTicks = LatencyBucketSize.Ticks;
        long truncatedTicks = timestamp.UtcTicks - (timestamp.UtcTicks % bucketTicks);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private static void TrimOldLatencyBuckets(Queue<LatencyBucketAccumulator> buckets, DateTimeOffset now)
    {
        DateTimeOffset threshold = now - LatencyHistoryWindow;

        while (buckets.Count > 0 && buckets.Peek().BucketStartUtc < threshold)
        {
            buckets.Dequeue();
        }
    }

    private static LatencyTimeSeriesPoint CreateLatencyPoint(LatencyBucketAccumulator bucket)
    {
        return new LatencyTimeSeriesPoint
        {
            TimestampUtc = bucket.BucketStartUtc,
            AvgDurationMs = Math.Round(bucket.AverageDurationMs, 2),
            P95DurationMs = bucket.CalculatePercentile(0.95),
            SampleCount = bucket.SampleCount
        };
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

    public TelemetryTimeSeriesSection TimeSeries { get; set; } = new();

    public TelemetryCorrelationResult CpuToDisplayLatencyCorrelation15m { get; set; } = TelemetryCorrelationResult.Empty(CorrelationWindowSeconds15m);

    public TelemetryCorrelationResult CpuToDisplayLatencyCorrelation1h { get; set; } = TelemetryCorrelationResult.Empty(CorrelationWindowSeconds1h);

    private const int CorrelationWindowSeconds15m = 900;

    private const int CorrelationWindowSeconds1h = 3600;
}

public class TelemetryCorrelationResult
{
    public int WindowSeconds { get; set; }

    public int SampleSize { get; set; }

    public double? Pearson { get; set; }

    public double? Spearman { get; set; }

    public string Confidence { get; set; } = "insufficient";

    public static TelemetryCorrelationResult Empty(TimeSpan window, int sampleSize = 0)
    {
        return new TelemetryCorrelationResult
        {
            WindowSeconds = (int)window.TotalSeconds,
            SampleSize = sampleSize,
            Confidence = TelemetryCorrelationCalculator.GetConfidence(sampleSize)
        };
    }

    public static TelemetryCorrelationResult Empty(int windowSeconds, int sampleSize = 0)
    {
        return new TelemetryCorrelationResult
        {
            WindowSeconds = windowSeconds,
            SampleSize = sampleSize,
            Confidence = TelemetryCorrelationCalculator.GetConfidence(sampleSize)
        };
    }
}

public static class TelemetryCorrelationCalculator
{
    public static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
        {
            return 0;
        }

        double meanX = x.Average();
        double meanY = y.Average();
        double numerator = 0;
        double sumSquaresX = 0;
        double sumSquaresY = 0;

        for (int i = 0; i < x.Count; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            numerator += dx * dy;
            sumSquaresX += dx * dx;
            sumSquaresY += dy * dy;
        }

        double denominator = Math.Sqrt(sumSquaresX * sumSquaresY);
        if (denominator < 0.000001)
        {
            return 0;
        }

        return numerator / denominator;
    }

    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
        {
            return 0;
        }

        List<double> rankX = Rank(x);
        List<double> rankY = Rank(y);
        return Pearson(rankX, rankY);
    }

    public static string GetConfidence(int sampleSize)
    {
        return sampleSize switch
        {
            >= 25 => "high",
            >= 12 => "medium",
            >= 6 => "low",
            _ => "insufficient"
        };
    }

    private static List<double> Rank(IReadOnlyList<double> values)
    {
        List<(double Value, int Index)> ordered = values
            .Select((value, index) => (Value: value, Index: index))
            .OrderBy(item => item.Value)
            .ToList();

        double[] ranks = new double[values.Count];
        int i = 0;
        while (i < ordered.Count)
        {
            int j = i;
            while (j + 1 < ordered.Count && Math.Abs(ordered[j + 1].Value - ordered[i].Value) < 0.000001)
            {
                j++;
            }

            double averageRank = ((i + 1) + (j + 1)) / 2.0;
            for (int k = i; k <= j; k++)
            {
                ranks[ordered[k].Index] = averageRank;
            }

            i = j + 1;
        }

        return ranks.ToList();
    }
}

public class TelemetryTimeSeriesSection
{
    public int BucketSizeSeconds { get; set; }

    public List<LatencyTimeSeriesPoint> DisplayLatency { get; set; } = new();

    public List<LatencyTimeSeriesPoint> LocationLatency { get; set; } = new();

    public List<LatencyTimeSeriesPoint> SpotifyLatency { get; set; } = new();
}

public class LatencyTimeSeriesPoint
{
    public DateTimeOffset TimestampUtc { get; set; }

    public int SampleCount { get; set; }

    public double AvgDurationMs { get; set; }

    public long P95DurationMs { get; set; }
}

public class LatencyBucketAccumulator
{
    private readonly List<long> _durationsMs = new();

    public LatencyBucketAccumulator(DateTimeOffset bucketStartUtc)
    {
        BucketStartUtc = bucketStartUtc;
    }

    public DateTimeOffset BucketStartUtc { get; }

    public int SampleCount => _durationsMs.Count;

    public double AverageDurationMs => SampleCount == 0 ? 0 : _durationsMs.Average();

    public void AddSample(long durationMs)
    {
        _durationsMs.Add(durationMs);
    }

    public long CalculatePercentile(double percentile)
    {
        if (_durationsMs.Count == 0)
        {
            return 0;
        }

        List<long> sorted = _durationsMs.OrderBy(value => value).ToList();
        int rank = (int)Math.Ceiling(percentile * sorted.Count);
        int index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
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
