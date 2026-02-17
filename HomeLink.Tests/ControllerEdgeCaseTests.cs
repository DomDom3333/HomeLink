using HomeLink.Controllers;
using HomeLink.Models;
using HomeLink.Services;
using HomeLink.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeLink.Tests;

public class ControllerEdgeCaseTests
{
    [Fact]
    public void RenderDisplay_ReturnsUnauthorized_WhenSpotifyNotAuthorized()
    {
        DisplayFrameCacheService cache = new();
        DisplayController controller = BuildDisplayController(cache, spotifyAuthorized: false);

        IActionResult result = controller.RenderDisplay(dither: true, deviceBattery: 15);

        UnauthorizedObjectResult unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        ErrorResponse payload = Assert.IsType<ErrorResponse>(unauthorized.Value);
        Assert.Contains("not authorized", payload.Error, StringComparison.OrdinalIgnoreCase);

        DisplayRenderRequestOptions options = cache.GetRequestedRenderOptions();
        Assert.True(options.Dither);
        Assert.Equal(15, options.DeviceBattery);
    }

    [Fact]
    public void RenderDisplay_ReturnsServiceUnavailable_WhenNoFrameCached()
    {
        DisplayController controller = BuildDisplayController(new DisplayFrameCacheService(), spotifyAuthorized: true);

        IActionResult result = controller.RenderDisplay(dither: false, deviceBattery: 3);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("no-store, no-transform, private", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("-1", controller.Response.Headers["X-Frame-Age-Ms"].ToString());
    }

    [Fact]
    public void RenderDisplay_Returns304_WhenEtagMatchesIfNoneMatch_ForBandwidthConstrainedClients()
    {
        DisplayFrameCacheService cache = new();
        cache.UpdateFrame(
            new EInkBitmap { PackedData = [0xAA, 0x55], Width = 16, Height = 1, BytesPerLine = 2 },
            sourceHash: "etag123",
            generatedAtUtc: DateTimeOffset.UtcNow,
            renderDuration: TimeSpan.FromMilliseconds(10),
            diagnostics: "ok",
            dither: true,
            deviceBattery: 90);

        DisplayController controller = BuildDisplayController(cache, spotifyAuthorized: true);
        controller.Request.Headers.IfNoneMatch = "\"etag123\"";

        IActionResult result = controller.RenderDisplay();

        StatusCodeResult notModified = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, notModified.StatusCode);
        Assert.Equal("\"etag123\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public void RenderDisplay_ReturnsBinaryFrameAndMetadataHeaders_ForEsp32Polling()
    {
        DisplayFrameCacheService cache = new();
        cache.UpdateFrame(
            new EInkBitmap { PackedData = [1, 2, 3, 4], Width = 32, Height = 1, BytesPerLine = 4 },
            sourceHash: "framehash",
            generatedAtUtc: DateTimeOffset.UtcNow,
            renderDuration: TimeSpan.FromMilliseconds(12),
            diagnostics: "diag",
            dither: false,
            deviceBattery: 20);

        DisplayController controller = BuildDisplayController(cache, spotifyAuthorized: true);

        IActionResult result = controller.RenderDisplay(dither: false, deviceBattery: 9);

        FileContentResult file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal([1, 2, 3, 4], file.FileContents);
        Assert.Equal("32", controller.Response.Headers["X-Width"].ToString());
        Assert.Equal("1", controller.Response.Headers["X-Height"].ToString());
        Assert.Equal("false", controller.Response.Headers["X-Dithered"].ToString());
        Assert.Equal("9", controller.Response.Headers["X-Device-Battery"].ToString());
        Assert.Equal("attachment; filename=display.bin", controller.Response.Headers["Content-Disposition"].ToString());
    }

    [Fact]
    public async Task ReceiveOwnTracksUpdate_ReturnsEmptyArray_ForNonLocationMessages()
    {
        (LocationController controller, _) = BuildLocationController();

        ActionResult<object> response = await controller.ReceiveOwnTracksUpdate(new OwnTracksPayload { Type = "transition", TrackerId = "esp32" });

        OkObjectResult ok = Assert.IsType<OkObjectResult>(response.Result);
        object[] data = Assert.IsType<object[]>(ok.Value);
        Assert.Empty(data);
    }

    [Fact]
    public async Task ReceiveOwnTracksUpdate_ReturnsBadRequest_WhenCoordinatesMissing()
    {
        (LocationController controller, _) = BuildLocationController();

        ActionResult<object> response = await controller.ReceiveOwnTracksUpdate(new OwnTracksPayload { Type = "location" });

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        ErrorResponse payload = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Contains("Missing latitude", payload.Error);
    }

    [Fact]
    public async Task ReceiveOwnTracksUpdate_QueuesEnrichment_ForValidLocationPayload()
    {
        (LocationController controller, LocationEnrichmentQueue queue) = BuildLocationController();

        OwnTracksPayload payload = new()
        {
            Type = "location",
            Latitude = 48.2082,
            Longitude = 16.3738,
            Battery = 11,
            Accuracy = 20,
            Velocity = 3,
            Connection = "m",
            TrackerId = "esp32-A",
            Timestamp = 1_720_000_000
        };

        ActionResult<object> response = await controller.ReceiveOwnTracksUpdate(payload);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Empty(Assert.IsType<object[]>(ok.Value));
        Assert.Equal(1, queue.Depth);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
        LocationEnrichmentJob job = await queue.DequeueAsync(cts.Token);
        Assert.Equal(48.2082, job.RawSnapshot.Latitude);
        Assert.Equal("esp32-A", job.RawSnapshot.TrackerId);
        Assert.Equal(11, job.RawSnapshot.BatteryLevel);
    }

    private static DisplayController BuildDisplayController(DisplayFrameCacheService cache, bool spotifyAuthorized)
    {
        RuntimeTelemetrySampler sampler = new();
        TelemetryDashboardState dashboard = new(sampler);
        StatePersistenceService statePersistence = new(NullLogger<StatePersistenceService>.Instance);
        SpotifyService spotify = new(
            NullLogger<SpotifyService>.Instance,
            dashboard,
            statePersistence,
            clientId: null,
            clientSecret: null,
            refreshToken: spotifyAuthorized ? "refresh" : null);
        LocationService location = new(new HttpClient(), statePersistence, dashboard);
        DrawingService drawing = new(new HttpClient(), NullLogger<DrawingService>.Instance, NullLoggerFactory.Instance, dashboard);

        DisplayController controller = new(drawing, spotify, location, NullLogger<DisplayController>.Instance, dashboard, cache)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return controller;
    }

    private static (LocationController Controller, LocationEnrichmentQueue Queue) BuildLocationController()
    {
        RuntimeTelemetrySampler sampler = new();
        TelemetryDashboardState dashboard = new(sampler);
        StatePersistenceService statePersistence = new(NullLogger<StatePersistenceService>.Instance);
        LocationService locationService = new(new HttpClient(), statePersistence, dashboard);
        LocationEnrichmentQueue queue = new();

        LocationController controller = new(locationService, NullLogger<LocationController>.Instance, dashboard, queue)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return (controller, queue);
    }
}
