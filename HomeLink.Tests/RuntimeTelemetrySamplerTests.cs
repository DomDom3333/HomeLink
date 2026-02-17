using HomeLink.Telemetry;

namespace HomeLink.Tests;

public class RuntimeTelemetrySamplerTests
{
    [Fact]
    public async Task StartStopAndSnapshot_ReturnsTelemetryPoints()
    {
        using RuntimeTelemetrySampler sampler = new();

        await sampler.StartAsync(CancellationToken.None);
        RuntimeTelemetrySection snapshot = sampler.CreateSnapshot(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), 50);
        await sampler.StopAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.History);
        Assert.NotNull(snapshot.Latest);
        Assert.True(snapshot.History.Count <= 50);
    }

    [Fact]
    public void CreateSnapshot_NormalizesResolutionAndMaxPoints()
    {
        using RuntimeTelemetrySampler sampler = new();

        RuntimeTelemetrySection snapshot = sampler.CreateSnapshot(TimeSpan.FromMinutes(1), TimeSpan.Zero, -100);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.History.Count <= 2000);
    }
}
