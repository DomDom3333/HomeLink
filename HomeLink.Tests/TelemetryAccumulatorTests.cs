using HomeLink.Telemetry;

namespace HomeLink.Tests;

public class TelemetryAccumulatorTests
{
    [Fact]
    public void LatencyBucketAccumulator_ComputesAverageAndPercentile()
    {
        LatencyBucketAccumulator accumulator = new(DateTimeOffset.UtcNow);
        accumulator.AddSample(50);
        accumulator.AddSample(10);
        accumulator.AddSample(20);

        Assert.Equal(3, accumulator.SampleCount);
        Assert.Equal(26.666666666666668, accumulator.AverageDurationMs);
        Assert.Equal(20, accumulator.CalculatePercentile(0.5));
        Assert.Equal(50, accumulator.CalculatePercentile(0.95));
    }

    [Fact]
    public void StageTelemetryAccumulator_TracksCountAveragesAndRounding()
    {
        StageTelemetryAccumulator accumulator = new();
        accumulator.Record(10.2);
        accumulator.Record(10.6);
        accumulator.Record(-5);

        StageTelemetrySnapshot snapshot = accumulator.CreateSnapshot("draw");

        Assert.Equal("draw", snapshot.Stage);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(0, snapshot.LastDurationMs);
        Assert.Equal(7, snapshot.AvgDurationMs);
    }

    [Fact]
    public void WorkerQueueTelemetryAccumulator_TracksLagAndProcessing()
    {
        WorkerQueueTelemetryAccumulator accumulator = new("q1", "w1");
        accumulator.RecordLag(5.4, depth: 3);
        accumulator.RecordLag(6.4, depth: -1);
        accumulator.RecordProcessing(20.6, depth: 2);

        WorkerQueueTelemetrySnapshot snapshot = accumulator.CreateSnapshot();

        Assert.Equal("q1", snapshot.Queue);
        Assert.Equal("w1", snapshot.Worker);
        Assert.Equal(2, snapshot.CurrentDepth);
        Assert.Equal(2, snapshot.LagCount);
        Assert.Equal(1, snapshot.ProcessingCount);
        Assert.Equal(6, snapshot.LastEnqueueToStartLagMs);
        Assert.Equal(5.5, snapshot.AvgEnqueueToStartLagMs);
        Assert.Equal(21, snapshot.LastProcessingDurationMs);
        Assert.Equal(21, snapshot.AvgProcessingDurationMs);
    }

    [Fact]
    public void TelemetrySummaryOptions_CreateNormalizesInvalidInputs()
    {
        TelemetrySummaryOptions options = TelemetrySummaryOptions.Create(TimeSpan.Zero, TimeSpan.Zero, -5);

        Assert.Equal(TimeSpan.FromHours(6), options.Window);
        Assert.InRange(options.MaxPoints, 25, 2000);
        Assert.True(options.Resolution >= TimeSpan.FromSeconds(1));
    }
}
