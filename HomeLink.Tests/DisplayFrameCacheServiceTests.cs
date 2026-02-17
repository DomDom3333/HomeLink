using HomeLink.Models;
using HomeLink.Services;

namespace HomeLink.Tests;

public class DisplayFrameCacheServiceTests
{
    [Fact]
    public void UpdateRequestedRenderOptions_NormalizesBatteryAndSignalsOnChange()
    {
        DisplayFrameCacheService cache = new();

        cache.UpdateRequestedRenderOptions(dither: true, deviceBattery: 150);
        DisplayRenderRequestOptions options = cache.GetRequestedRenderOptions();

        Assert.True(options.Dither);
        Assert.Equal(100, options.DeviceBattery);
    }

    [Fact]
    public async Task WaitForSignalOrIntervalAsync_ReturnsWhenSignaled()
    {
        DisplayFrameCacheService cache = new();
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));

        Task waitTask = cache.WaitForSignalOrIntervalAsync(TimeSpan.FromSeconds(10), cts.Token);
        cache.SignalRenderNeeded();

        await waitTask;
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForSignalOrIntervalAsync_CompletesOnCancellation()
    {
        DisplayFrameCacheService cache = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await cache.WaitForSignalOrIntervalAsync(TimeSpan.FromSeconds(10), cts.Token);
    }

    [Fact]
    public void UpdateFrame_CapturesSnapshotAndQuotesEtag()
    {
        DisplayFrameCacheService cache = new();
        byte[] payload = [1, 2, 3];
        EInkBitmap bitmap = new() { PackedData = payload, Width = 8, Height = 4, BytesPerLine = 1 };

        cache.UpdateFrame(bitmap, "abc123", DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(87), "rendered", true, -5);
        DisplayFrameSnapshot? frame = cache.GetLatestFrame();

        Assert.NotNull(frame);
        Assert.Equal("\"abc123\"", frame!.Etag);
        Assert.Equal("abc123", frame.SourceHash);
        Assert.Equal(0, frame.DeviceBattery);
        Assert.Equal(87, frame.RenderDurationMs);
        Assert.Equal(payload, frame.FrameBytes);
    }
}
