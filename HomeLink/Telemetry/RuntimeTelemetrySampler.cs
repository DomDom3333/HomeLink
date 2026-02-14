namespace HomeLink.Telemetry;

using System.Diagnostics;

public class RuntimeTelemetrySampler : IHostedService, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private const int MaxHistoryPoints = 240;

    private readonly Lock _historyLock = new();
    private readonly Queue<RuntimeTelemetryPoint> _history = new();
    private readonly Process _process;
    private Timer? _timer;
    private DateTimeOffset _lastSampleAtUtc;
    private TimeSpan _lastTotalProcessorTime;

    public RuntimeTelemetrySampler()
    {
        _process = Process.GetCurrentProcess();
        _lastSampleAtUtc = DateTimeOffset.UtcNow;
        _lastTotalProcessorTime = _process.TotalProcessorTime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Sample();
        _timer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public RuntimeTelemetrySection CreateSnapshot(TimeSpan? window = null, TimeSpan? resolution = null, int? maxPoints = null)
    {
        lock (_historyLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset threshold = now - window.GetValueOrDefault(TimeSpan.FromHours(6));

            List<RuntimeTelemetryPoint> history = _history
                .Where(point => point.TimestampUtc >= threshold)
                .ToList();

            TimeSpan resolvedResolution = resolution.GetValueOrDefault(SampleInterval);
            if (resolvedResolution <= TimeSpan.Zero)
            {
                resolvedResolution = SampleInterval;
            }

            int resolvedMaxPoints = Math.Clamp(maxPoints.GetValueOrDefault(200), 25, 2000);

            history = Downsample(history, resolvedResolution, resolvedMaxPoints);
            return new RuntimeTelemetrySection
            {
                Latest = history.Count > 0 ? history[^1] : null,
                History = history
            };
        }
    }

    private static List<RuntimeTelemetryPoint> Downsample(List<RuntimeTelemetryPoint> source, TimeSpan resolution, int maxPoints)
    {
        if (source.Count <= maxPoints)
        {
            return source;
        }

        long resolutionTicks = Math.Max(TimeSpan.FromSeconds(1).Ticks, resolution.Ticks);
        List<RuntimeTelemetryPoint> bucketed = new();
        List<RuntimeTelemetryPoint> bucket = new();
        long? currentBucketKey = null;

        foreach (RuntimeTelemetryPoint point in source)
        {
            long bucketKey = point.TimestampUtc.UtcTicks / resolutionTicks;
            if (currentBucketKey.HasValue && currentBucketKey.Value != bucketKey)
            {
                bucketed.Add(bucket[^1]);
                bucket = new List<RuntimeTelemetryPoint>();
            }

            currentBucketKey = bucketKey;
            bucket.Add(point);
        }

        if (bucket.Count > 0)
        {
            bucketed.Add(bucket[^1]);
        }

        if (bucketed.Count <= maxPoints)
        {
            return bucketed;
        }

        List<RuntimeTelemetryPoint> reduced = new();
        double step = (bucketed.Count - 1d) / (maxPoints - 1d);
        for (int i = 0; i < maxPoints; i++)
        {
            int index = (int)Math.Round(i * step, MidpointRounding.AwayFromZero);
            index = Math.Clamp(index, 0, bucketed.Count - 1);
            reduced.Add(bucketed[index]);
        }

        return reduced;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        // Cleanup
        _process.Dispose();
        _timer?.Dispose();
    }

    private void Sample()
    {
        try
        {
            _process.Refresh();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan cpuNow = _process.TotalProcessorTime;
            double elapsedMs = (now - _lastSampleAtUtc).TotalMilliseconds;

            double cpuPercent = 0;
            if (elapsedMs > 0)
            {
                double cpuMs = (cpuNow - _lastTotalProcessorTime).TotalMilliseconds;
                cpuPercent = Math.Max(0, Math.Round((cpuMs / (elapsedMs * Environment.ProcessorCount)) * 100, 2));
            }

            RuntimeTelemetryPoint point = new()
            {
                TimestampUtc = now,
                ProcessCpuPercent = cpuPercent,
                WorkingSetMb = Math.Round(_process.WorkingSet64 / (1024d * 1024d), 2),
                GcHeapMb = Math.Round(GC.GetTotalMemory(false) / (1024d * 1024d), 2),
                ThreadCount = _process.Threads.Count,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2)
            };

            lock (_historyLock)
            {
                _history.Enqueue(point);
                while (_history.Count > MaxHistoryPoints)
                {
                    _history.Dequeue();
                }
            }

            _lastSampleAtUtc = now;
            _lastTotalProcessorTime = cpuNow;
        }
        catch
        {
            // Telemetry sampler should never crash the app.
        }
    }
}
