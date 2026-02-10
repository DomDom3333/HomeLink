namespace HomeLink;

using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.HttpLogging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

        builder.Services.AddSingleton<Services.SpotifyService>(sp =>
            new Services.SpotifyService(
                sp.GetRequiredService<ILogger<Services.SpotifyService>>(),
                spotifyClientId,
                spotifyClientSecret,
                spotifyRefreshToken,
                spotifyTokenExpiry));

        builder.Services.AddHttpClient<Services.LocationService>();
        builder.Services.AddSingleton<Services.LocationService>();

        builder.Services.AddHttpClient<Services.DrawingService>();
        builder.Services.AddScoped<Services.DrawingService>();
        builder.Services.AddScoped<Services.DisplayDataService>();

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
            options.ResponseHeaders.Add("X-Trace-Id");
            options.CombineLogs = true;
        });

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        app.MapOpenApi();
        app.MapHealthChecks("/health");

        app.Use(async (context, next) =>
        {
            await next();
            string traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            context.Response.Headers["X-Trace-Id"] = traceId;
        });

        app.UseHttpLogging();

        app.MapControllers();

        app.Run();
    }
}
