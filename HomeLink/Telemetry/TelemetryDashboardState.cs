namespace HomeLink.Telemetry;

using System.Threading;
using System.Collections.Concurrent;

public class TelemetryDashboardState
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan LatencyBucketSize = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LatencyHistoryWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan PercentileWindow = TimeSpan.FromHours(1);
    private const int MaxBatteryHistorySamples = 120_000;
    private static readonly TimeSpan MinimumPredictionWindow = TimeSpan.FromHours(1);

    private readonly Lock _timelineLock = new();
    private readonly Lock _latencyLock = new();
    private readonly Queue<DateTimeOffset> _displayRequestTimeline = new();
    private DateTimeOffset? _lastDisplayRefreshAtUtc;
    private readonly Queue<DeviceBatterySample> _batteryHistory = new();
    private readonly Queue<LatencyBucketAccumulator> _displayLatencyBuckets = new();
    private readonly Queue<LatencyBucketAccumulator> _locationLatencyBuckets = new();
    private readonly Queue<LatencyBucketAccumulator> _spotifyLatencyBuckets = new();
    private readonly Queue<TimedDurationSample> _displayLatencySamples = new();
    private readonly Queue<TimedDurationSample> _locationLatencySamples = new();
    private readonly Queue<TimedDurationSample> _spotifyLatencySamples = new();
    private readonly RuntimeTelemetrySampler _runtimeTelemetrySampler;
    private readonly ConcurrentDictionary<string, StageTelemetryAccumulator> _drawingStages = new();
    private readonly ConcurrentDictionary<string, StageTelemetryAccumulator> _locationStages = new();
    private readonly ConcurrentDictionary<string, StageTelemetryAccumulator> _spotifyStages = new();
    private readonly ConcurrentDictionary<string, WorkerQueueTelemetryAccumulator> _workerQueues = new();

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

    public void RecordDisplay(double durationMs, bool isError, int? deviceBattery = null, bool didRefreshScreen = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Record(ref _displayRequests, ref _displayErrors, ref _displayTotalDurationMs, ref _displayLastDurationMs, durationMs, isError);
        RecordLatency(_displayLatencyBuckets, _displayLatencySamples, durationMs, now);

        lock (_timelineLock)
        {
            _displayRequestTimeline.Enqueue(now);
            TrimOldEntries(now);

            if (didRefreshScreen)
            {
                _lastDisplayRefreshAtUtc = now;
            }

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
        RecordLatency(_locationLatencyBuckets, _locationLatencySamples, durationMs, DateTimeOffset.UtcNow);
    }

    public void RecordSpotify(double durationMs, bool isError)
    {
        Record(ref _spotifyRequests, ref _spotifyErrors, ref _spotifyTotalDurationMs, ref _spotifyLastDurationMs, durationMs, isError);
        RecordLatency(_spotifyLatencyBuckets, _spotifyLatencySamples, durationMs, DateTimeOffset.UtcNow);
    }

    public void RecordDrawingStage(string stage, double durationMs)
    {
        RecordStage(_drawingStages, stage, durationMs);
    }

    public void RecordLocationStage(string stage, double durationMs)
    {
        RecordStage(_locationStages, stage, durationMs);
    }

    public void RecordSpotifyStage(string stage, double durationMs)
    {
        RecordStage(_spotifyStages, stage, durationMs);
    }

    public void RecordSpotifySnapshotAge(double ageMs)
    {
        RecordStage(_spotifyStages, "snapshot_age", ageMs);
    }

    public void RecordWorkerQueueLag(string queue, string worker, double lagMs, int depth)
    {
        GetWorkerQueueAccumulator(queue, worker).RecordLag(lagMs, depth);
    }

    public void RecordWorkerQueueProcessing(string queue, string worker, double durationMs, int depth)
    {
        GetWorkerQueueAccumulator(queue, worker).RecordProcessing(durationMs, depth);
    }

    public TelemetryDashboardSnapshot CreateSnapshot(TelemetrySummaryOptions? options = null)
    {
        TelemetrySummaryOptions resolvedOptions = options ?? TelemetrySummaryOptions.Default;
        DeviceTelemetrySection device = CreateDeviceSection(resolvedOptions);
        RuntimeTelemetrySection runtime = _runtimeTelemetrySampler.CreateSnapshot(
            resolvedOptions.Window,
            resolvedOptions.Resolution,
            resolvedOptions.MaxPoints);
        return new TelemetryDashboardSnapshot
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Display = CreateSection(_displayRequests, _displayErrors, _displayTotalDurationMs, _displayLastDurationMs, _displayLatencySamples),
            Location = CreateSection(_locationUpdates, _locationErrors, _locationTotalDurationMs, _locationLastDurationMs, _locationLatencySamples),
            Spotify = CreateSection(_spotifyRequests, _spotifyErrors, _spotifyTotalDurationMs, _spotifyLastDurationMs, _spotifyLatencySamples),
            DrawingStages = CreateStageSnapshot(_drawingStages),
            LocationStages = CreateStageSnapshot(_locationStages),
            SpotifyStages = CreateStageSnapshot(_spotifyStages),
            WorkerQueues = CreateWorkerQueueSnapshot(),
            Device = device,
            Runtime = runtime,
            TimeSeries = CreateTimeSeriesSection(resolvedOptions)
        };
    }

    private TelemetryTimeSeriesSection CreateTimeSeriesSection(TelemetrySummaryOptions options)
    {
        lock (_latencyLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TrimOldLatencyBuckets(_displayLatencyBuckets, now);
            TrimOldLatencyBuckets(_locationLatencyBuckets, now);
            TrimOldLatencyBuckets(_spotifyLatencyBuckets, now);

            DateTimeOffset threshold = now - options.Window;
            TimeSpan resolution = options.Resolution;

            return new TelemetryTimeSeriesSection
            {
                BucketSizeSeconds = (int)resolution.TotalSeconds,
                DisplayLatency = DownsampleLatencyBuckets(_displayLatencyBuckets, threshold, resolution, options.MaxPoints),
                LocationLatency = DownsampleLatencyBuckets(_locationLatencyBuckets, threshold, resolution, options.MaxPoints),
                SpotifyLatency = DownsampleLatencyBuckets(_spotifyLatencyBuckets, threshold, resolution, options.MaxPoints)
            };
        }
    }

    private DeviceTelemetrySection CreateDeviceSection(TelemetrySummaryOptions options)
    {
        lock (_timelineLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TrimOldEntries(now);

            DeviceBatterySample? latest = _batteryHistory.Count > 0 ? _batteryHistory.Last() : null;
            DateTimeOffset threshold = now - options.Window;
            List<DeviceBatterySample> history = DownsampleSeries(
                _batteryHistory.Where(sample => sample.TimestampUtc >= threshold),
                sample => sample.TimestampUtc,
                options.Resolution,
                options.MaxPoints,
                samplesInBucket => samplesInBucket[^1]);

            return new DeviceTelemetrySection
            {
                LatestBatteryPercent = latest?.BatteryPercent,
                LatestPredictedBatteryPercent = latest?.PredictedBatteryPercent,
                LatestPredictedHoursToEmpty = latest?.PredictedHoursToEmpty,
                BatteryHistory = history,
                RequestsLastHour = CountSince(_displayRequestTimeline, now - OneHour),
                RequestsLastDay = CountSince(_displayRequestTimeline, now - OneDay),
                AvgRequestsPerHour = Math.Round(CalculateRate(_displayRequestTimeline, now, OneHour), 2),
                AvgRequestsPerDay = Math.Round(CalculateRate(_displayRequestTimeline, now, OneDay), 2),
                LastDisplayRefreshAtUtc = _lastDisplayRefreshAtUtc
            };
        }
    }

    private static List<LatencyTimeSeriesPoint> DownsampleLatencyBuckets(
        IEnumerable<LatencyBucketAccumulator> source,
        DateTimeOffset threshold,
        TimeSpan resolution,
        int maxPoints)
    {
        List<LatencyBucketAccumulator> filtered = source
            .Where(bucket => bucket.BucketStartUtc >= threshold && bucket.SampleCount > 0)
            .ToList();

        return DownsampleSeries(
            filtered,
            bucket => bucket.BucketStartUtc,
            resolution,
            maxPoints,
            AggregateLatencyBuckets)
            .Select(CreateLatencyPoint)
            .ToList();
    }

    private static LatencyBucketAccumulator AggregateLatencyBuckets(List<LatencyBucketAccumulator> buckets)
    {
        DateTimeOffset bucketStart = buckets[0].BucketStartUtc;
        LatencyBucketAccumulator aggregate = new(bucketStart);

        foreach (LatencyBucketAccumulator bucket in buckets)
        {
            foreach (long duration in bucket.GetDurations())
            {
                aggregate.AddSample(duration);
            }
        }

        return aggregate;
    }

    private static List<T> DownsampleSeries<T>(
        IEnumerable<T> source,
        Func<T, DateTimeOffset> timestampSelector,
        TimeSpan resolution,
        int maxPoints,
        Func<List<T>, T> aggregateBucket)
    {
        List<T> ordered = source.OrderBy(timestampSelector).ToList();
        if (ordered.Count <= maxPoints)
        {
            return ordered;
        }

        long resolutionTicks = Math.Max(TimeSpan.FromSeconds(1).Ticks, resolution.Ticks);
        List<T> result = new();
        List<T> currentBucket = new();
        long? currentBucketKey = null;

        foreach (T sample in ordered)
        {
            DateTimeOffset timestamp = timestampSelector(sample);
            long bucketKey = timestamp.UtcTicks / resolutionTicks;

            if (currentBucketKey.HasValue && currentBucketKey.Value != bucketKey)
            {
                result.Add(aggregateBucket(currentBucket));
                currentBucket = new List<T>();
            }

            currentBucketKey = bucketKey;
            currentBucket.Add(sample);
        }

        if (currentBucket.Count > 0)
        {
            result.Add(aggregateBucket(currentBucket));
        }

        return result.Count <= maxPoints
            ? result
            : UniformReduce(result, maxPoints);
    }

    private static List<T> UniformReduce<T>(List<T> source, int maxPoints)
    {
        if (source.Count <= maxPoints)
        {
            return source;
        }

        List<T> reduced = new();
        double step = (source.Count - 1d) / (maxPoints - 1d);

        for (int i = 0; i < maxPoints; i++)
        {
            int index = (int)Math.Round(i * step, MidpointRounding.AwayFromZero);
            index = Math.Clamp(index, 0, source.Count - 1);
            reduced.Add(source[index]);
        }

        return reduced;
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

    private static void RecordStage(ConcurrentDictionary<string, StageTelemetryAccumulator> buckets, string stage, double durationMs)
    {
        StageTelemetryAccumulator accumulator = buckets.GetOrAdd(stage, _ => new StageTelemetryAccumulator());
        accumulator.Record(durationMs);
    }

    private WorkerQueueTelemetryAccumulator GetWorkerQueueAccumulator(string queue, string worker)
    {
        // Avoid capturing `queue` and `worker` in a lambda (which creates a closure allocation).
        // Build a composite key and use a static value factory that parses it so no outer variables are captured.
        string key = $"{queue}:{worker}";
        return _workerQueues.GetOrAdd(key, static k =>
        {
            int idx = k.IndexOf(':');
            string q = idx >= 0 ? k[..idx] : k;
            string w = idx >= 0 && idx + 1 < k.Length ? k[(idx + 1)..] : string.Empty;
            return new WorkerQueueTelemetryAccumulator(q, w);
        });
    }

    private static List<StageTelemetrySnapshot> CreateStageSnapshot(ConcurrentDictionary<string, StageTelemetryAccumulator> source)
    {
        return source
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Value.CreateSnapshot(entry.Key))
            .ToList();
    }

    private List<WorkerQueueTelemetrySnapshot> CreateWorkerQueueSnapshot()
    {
        return _workerQueues.Values
            .OrderBy(item => item.Queue)
            .ThenBy(item => item.Worker)
            .Select(item => item.CreateSnapshot())
            .ToList();
    }

    private TelemetryDashboardSection CreateSection(
        long requests,
        long errors,
        long totalDurationMs,
        long lastDurationMs,
        Queue<TimedDurationSample> rollingSamples)
    {
        long count = Interlocked.Read(ref requests);
        long errorCount = Interlocked.Read(ref errors);
        long total = Interlocked.Read(ref totalDurationMs);
        long last = Interlocked.Read(ref lastDurationMs);

        double avg = count == 0 ? 0 : (double)total / count;
        double errorRate = count == 0 ? 0 : (double)errorCount / count;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long p50;
        long p95;
        long p99;
        int percentileSampleCount;

        lock (_latencyLock)
        {
            TrimOldLatencySamples(rollingSamples, now);
            IReadOnlyList<long> sampleDurations = rollingSamples.Select(sample => sample.DurationMs).ToList();
            p50 = CalculatePercentile(sampleDurations, 0.50);
            p95 = CalculatePercentile(sampleDurations, 0.95);
            p99 = CalculatePercentile(sampleDurations, 0.99);
            percentileSampleCount = sampleDurations.Count;
        }

        return new TelemetryDashboardSection
        {
            Count = count,
            Errors = errorCount,
            ErrorRate = Math.Round(errorRate * 100, 2),
            LastDurationMs = last,
            AvgDurationMs = Math.Round(avg, 2),
            P50DurationMs = p50,
            P95DurationMs = p95,
            P99DurationMs = p99,
            PercentileSampleCount = percentileSampleCount,
            PercentileWindowSeconds = (int)PercentileWindow.TotalSeconds
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

    private void RecordLatency(
        Queue<LatencyBucketAccumulator> buckets,
        Queue<TimedDurationSample> rollingSamples,
        double durationMs,
        DateTimeOffset now)
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
            rollingSamples.Enqueue(new TimedDurationSample(now, roundedDuration));
            TrimOldLatencyBuckets(buckets, now);
            TrimOldLatencySamples(rollingSamples, now);
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

    private static void TrimOldLatencySamples(Queue<TimedDurationSample> rollingSamples, DateTimeOffset now)
    {
        DateTimeOffset threshold = now - PercentileWindow;

        while (rollingSamples.Count > 0 && rollingSamples.Peek().TimestampUtc < threshold)
        {
            rollingSamples.Dequeue();
        }
    }

    private static long CalculatePercentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        List<long> sorted = values.OrderBy(value => value).ToList();
        int rank = (int)Math.Ceiling(percentile * sorted.Count);
        int index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
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

    public List<StageTelemetrySnapshot> DrawingStages { get; set; } = new();

    public List<StageTelemetrySnapshot> LocationStages { get; set; } = new();

    public List<StageTelemetrySnapshot> SpotifyStages { get; set; } = new();

    public List<WorkerQueueTelemetrySnapshot> WorkerQueues { get; set; } = new();

    public DeviceTelemetrySection Device { get; set; } = new();

    public RuntimeTelemetrySection Runtime { get; set; } = new();

    public TelemetryTimeSeriesSection TimeSeries { get; set; } = new();
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

    public IReadOnlyList<long> GetDurations()
    {
        return _durationsMs;
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


public class StageTelemetryAccumulator
{
    private long _count;
    private long _totalDurationMs;
    private long _lastDurationMs;

    public void Record(double durationMs)
    {
        long roundedDuration = Math.Max(0, (long)Math.Round(durationMs, MidpointRounding.AwayFromZero));
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalDurationMs, roundedDuration);
        Interlocked.Exchange(ref _lastDurationMs, roundedDuration);
    }

    public StageTelemetrySnapshot CreateSnapshot(string stage)
    {
        long count = Interlocked.Read(ref _count);
        long totalDuration = Interlocked.Read(ref _totalDurationMs);

        return new StageTelemetrySnapshot
        {
            Stage = stage,
            Count = count,
            LastDurationMs = Interlocked.Read(ref _lastDurationMs),
            AvgDurationMs = count == 0 ? 0 : Math.Round((double)totalDuration / count, 2)
        };
    }
}

public class WorkerQueueTelemetryAccumulator
{
    private long _lagCount;
    private long _lagTotalDurationMs;
    private long _lagLastDurationMs;
    private long _processingCount;
    private long _processingTotalDurationMs;
    private long _processingLastDurationMs;
    private int _currentDepth;

    public WorkerQueueTelemetryAccumulator(string queue, string worker)
    {
        Queue = queue;
        Worker = worker;
    }

    public string Queue { get; }

    public string Worker { get; }

    public void RecordLag(double lagMs, int depth)
    {
        long roundedLag = Math.Max(0, (long)Math.Round(lagMs, MidpointRounding.AwayFromZero));
        Interlocked.Increment(ref _lagCount);
        Interlocked.Add(ref _lagTotalDurationMs, roundedLag);
        Interlocked.Exchange(ref _lagLastDurationMs, roundedLag);
        Interlocked.Exchange(ref _currentDepth, Math.Max(0, depth));
    }

    public void RecordProcessing(double durationMs, int depth)
    {
        long roundedDuration = Math.Max(0, (long)Math.Round(durationMs, MidpointRounding.AwayFromZero));
        Interlocked.Increment(ref _processingCount);
        Interlocked.Add(ref _processingTotalDurationMs, roundedDuration);
        Interlocked.Exchange(ref _processingLastDurationMs, roundedDuration);
        Interlocked.Exchange(ref _currentDepth, Math.Max(0, depth));
    }

    public WorkerQueueTelemetrySnapshot CreateSnapshot()
    {
        long lagCount = Interlocked.Read(ref _lagCount);
        long processingCount = Interlocked.Read(ref _processingCount);

        return new WorkerQueueTelemetrySnapshot
        {
            Queue = Queue,
            Worker = Worker,
            CurrentDepth = Math.Max(0, Volatile.Read(ref _currentDepth)),
            LagCount = lagCount,
            ProcessingCount = processingCount,
            LastEnqueueToStartLagMs = Interlocked.Read(ref _lagLastDurationMs),
            AvgEnqueueToStartLagMs = lagCount == 0 ? 0 : Math.Round((double)Interlocked.Read(ref _lagTotalDurationMs) / lagCount, 2),
            LastProcessingDurationMs = Interlocked.Read(ref _processingLastDurationMs),
            AvgProcessingDurationMs = processingCount == 0 ? 0 : Math.Round((double)Interlocked.Read(ref _processingTotalDurationMs) / processingCount, 2)
        };
    }
}

public record TelemetrySummaryOptions(TimeSpan Window, TimeSpan Resolution, int MaxPoints)
{
    private const int DefaultMaxChartPoints = 200;

    public static TelemetrySummaryOptions Default { get; } = Create(null, null, null);

    public static TelemetrySummaryOptions Create(TimeSpan? window, TimeSpan? resolution, int? maxPoints)
    {
        TimeSpan resolvedWindow = window.GetValueOrDefault(TimeSpan.FromHours(6));
        if (resolvedWindow <= TimeSpan.Zero)
        {
            resolvedWindow = TimeSpan.FromHours(6);
        }

        int resolvedMaxPoints = Math.Clamp(maxPoints.GetValueOrDefault(DefaultMaxChartPoints), 25, 2000);

        TimeSpan minResolution = TimeSpan.FromSeconds(1);
        TimeSpan computedResolution = TimeSpan.FromTicks(Math.Max(
            minResolution.Ticks,
            (long)Math.Ceiling(resolvedWindow.Ticks / (double)resolvedMaxPoints)));

        TimeSpan resolvedResolution = resolution.HasValue && resolution.Value > TimeSpan.Zero
            ? resolution.Value
            : computedResolution;

        if (resolvedResolution < minResolution)
        {
            resolvedResolution = minResolution;
        }

        return new TelemetrySummaryOptions(resolvedWindow, resolvedResolution, resolvedMaxPoints);
    }
}

public class TelemetryDashboardSection
{
    public long Count { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public long LastDurationMs { get; set; }

    public double AvgDurationMs { get; set; }

    public long P50DurationMs { get; set; }

    public long P95DurationMs { get; set; }

    public long P99DurationMs { get; set; }

    public int PercentileSampleCount { get; set; }

    public int PercentileWindowSeconds { get; set; }
}

public readonly record struct TimedDurationSample(DateTimeOffset TimestampUtc, long DurationMs);

public class StageTelemetrySnapshot
{
    public string Stage { get; set; } = string.Empty;

    public long Count { get; set; }

    public long LastDurationMs { get; set; }

    public double AvgDurationMs { get; set; }
}

public class WorkerQueueTelemetrySnapshot
{
    public string Queue { get; set; } = string.Empty;

    public string Worker { get; set; } = string.Empty;

    public int CurrentDepth { get; set; }

    public long LagCount { get; set; }

    public long ProcessingCount { get; set; }

    public long LastEnqueueToStartLagMs { get; set; }

    public double AvgEnqueueToStartLagMs { get; set; }

    public long LastProcessingDurationMs { get; set; }

    public double AvgProcessingDurationMs { get; set; }
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

    public DateTimeOffset? LastDisplayRefreshAtUtc { get; set; }

    public List<DeviceBatterySample> BatteryHistory { get; set; } = new();
}

public class DeviceBatterySample
{
    public DateTimeOffset TimestampUtc { get; init; }

    public int? BatteryPercent { get; init; }

    public int? PredictedBatteryPercent { get; init; }

    public double? PredictedHoursToEmpty { get; init; }
}

public record BatteryPrediction(int? NextHourBatteryPercent, double? HoursToEmpty);

public class RuntimeTelemetrySection
{
    public RuntimeTelemetryPoint? Latest { get; set; }

    public List<RuntimeTelemetryPoint> History { get; init; } = [];
}

public class RuntimeTelemetryPoint
{
    public DateTimeOffset TimestampUtc { get; init; }

    public double ProcessCpuPercent { get; init; }

    public double WorkingSetMb { get; set; }

    public double GcHeapMb { get; set; }

    public int ThreadCount { get; set; }

    public int Gen0Collections { get; set; }

    public int Gen1Collections { get; set; }

    public int Gen2Collections { get; set; }
}
