namespace HomeLink;

using System.Globalization;

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

        builder.Services.AddSingleton<Services.SpotifyService>(_ => 
            new Services.SpotifyService(
                spotifyClientId,
                spotifyClientSecret,
                spotifyRefreshToken,
                spotifyTokenExpiry));
        
        builder.Services.AddHttpClient<Services.LocationService>();
        builder.Services.AddSingleton<Services.LocationService>();

        builder.Services.AddHttpClient<Services.DrawingService>();
        builder.Services.AddScoped<Services.DrawingService>();
        
        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }


        app.MapControllers();

        app.Run();
    }
}