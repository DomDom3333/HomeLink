namespace HomeLink;

using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using HomeLink.Telemetry;

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        string serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "HomeLink.Api";
        string serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(HomeLinkTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    });

                string? otlpEndpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
                }
                else
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(HomeLinkTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                string? otlpEndpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
                }
                else
                {
                    metrics.AddConsoleExporter();
                }
            });

        // Read only the Spotify refresh token (and optional client id).
        // Note: Spotify's token refresh may still require client_id or client_secret depending on your app type.
        // We accept that as a runtime requirement — caller can provide SPOTIFY_ID if needed.
        string? spotifyRefreshToken = Environment.GetEnvironmentVariable("SPOTIFY_REFRESH_TOKEN");
        string? spotifyClientId = Environment.GetEnvironmentVariable("SPOTIFY_ID");
        string? spotifyClientSecret = Environment.GetEnvironmentVariable("SPOTIFY_SECRET");

        DateTime? spotifyTokenExpiry = null;
        string? expiryEnv = Environment.GetEnvironmentVariable("SPOTIFY_TOKEN_EXPIRY");
        if (expiryEnv != null)
        {
            if (DateTime.TryParse(expiryEnv, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            {
                spotifyTokenExpiry = parsed.ToUniversalTime();
            }
            else if (long.TryParse(expiryEnv, out long unixSeconds))
            {
                spotifyTokenExpiry = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
        }

        builder.Services.AddSingleton<RuntimeTelemetrySampler>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RuntimeTelemetrySampler>());
        builder.Services.AddSingleton<TelemetryDashboardState>();
        builder.Services.AddSingleton<Services.StatePersistenceService>();

        builder.Services.AddSingleton<Services.SpotifyService>(sp =>
            new Services.SpotifyService(
                sp.GetRequiredService<ILogger<Services.SpotifyService>>(),
                sp.GetRequiredService<TelemetryDashboardState>(),
                sp.GetRequiredService<Services.StatePersistenceService>(),
                spotifyClientId,
                spotifyClientSecret,
                spotifyRefreshToken,
                spotifyTokenExpiry));

        builder.Services.AddHttpClient<Services.LocationService>();
        builder.Services.AddSingleton<Services.LocationService>();

        builder.Services.AddHttpClient<Services.DrawingService>();
        builder.Services.AddScoped<Services.DrawingService>();
        builder.Services.AddScoped<Services.DisplayDataService>();
        builder.Services.AddSingleton<Services.DisplayFrameCacheService>();
        builder.Services.AddHostedService<Services.DisplayRenderWorker>();

        builder.Services.AddControllers();
        builder.Services.AddHealthChecks();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        builder.Services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestMethod
                | HttpLoggingFields.RequestPath
                | HttpLoggingFields.RequestQuery
                | HttpLoggingFields.RequestHeaders
                | HttpLoggingFields.ResponseStatusCode
                | HttpLoggingFields.ResponseHeaders
                | HttpLoggingFields.Duration;
            options.RequestHeaders.Add("User-Agent");
            options.RequestHeaders.Add("If-None-Match");
            options.ResponseHeaders.Add("ETag");
            options.ResponseHeaders.Add("X-Device-Battery");
            options.ResponseHeaders.Add("X-Frame-Age-Ms");
            options.ResponseHeaders.Add("X-Trace-Id");
            options.CombineLogs = true;
        });

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        app.MapOpenApi();
        app.MapHealthChecks("/health");

        app.MapGet("/api/telemetry/summary", (HttpRequest request, TelemetryDashboardState dashboardState) =>
        {
            TimeSpan? window = ParseDuration(request.Query["window"]);
            TimeSpan? resolution = ParseDuration(request.Query["resolution"]);
            int? maxPoints = ParseInt(request.Query["maxPoints"]);

            TelemetrySummaryOptions options = TelemetrySummaryOptions.Create(window, resolution, maxPoints);
            return Results.Ok(dashboardState.CreateSnapshot(options));
        });

        app.MapGet("/telemetry/dashboard", () =>
            Results.Content(TelemetryDashboardPage.Html, "text/html"));

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                string traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                context.Response.Headers["X-Trace-Id"] = traceId;
                return Task.CompletedTask;
            });

            await next();
        });

        app.UseHttpLogging();

        app.MapControllers();

        app.Run();
    }

    private static TimeSpan? ParseDuration(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string value = input.Trim().ToLowerInvariant();
        double scalar;

        if (value.EndsWith("ms") && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out scalar))
        {
            return TimeSpan.FromMilliseconds(scalar);
        }

        if (value.EndsWith('s') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out scalar))
        {
            return TimeSpan.FromSeconds(scalar);
        }

        if (value.EndsWith('m') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out scalar))
        {
            return TimeSpan.FromMinutes(scalar);
        }

        if (value.EndsWith('h') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out scalar))
        {
            return TimeSpan.FromHours(scalar);
        }

        if (value.EndsWith('d') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out scalar))
        {
            return TimeSpan.FromDays(scalar);
        }

        if (TimeSpan.TryParse(input, CultureInfo.InvariantCulture, out TimeSpan timespan))
        {
            return timespan;
        }

        return null;
    }

    private static int? ParseInt(string? input)
    {
        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }
}
