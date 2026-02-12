namespace HomeLink.Telemetry;

using System.Diagnostics;

public class RuntimeTelemetrySampler : IHostedService, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private const int MaxHistoryPoints = 240;

    private readonly object _historyLock = new();
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

    public RuntimeTelemetrySection CreateSnapshot()
    {
        lock (_historyLock)
        {
            List<RuntimeTelemetryPoint> history = _history.ToList();
            return new RuntimeTelemetrySection
            {
                Latest = history.Count > 0 ? history[^1] : null,
                History = history
            };
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _process.Dispose();
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
