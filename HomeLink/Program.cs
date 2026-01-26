namespace HomeLink;

using System.Globalization;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Read only the Spotify refresh token (and optional client id).
        // Note: Spotify's token refresh may still require client_id or client_secret depending on your app type.
        // We accept that as a runtime requirement — caller can provide SPOTIFY_ID if needed.
        var spotifyRefreshToken = Environment.GetEnvironmentVariable("SPOTIFY_REFRESH_TOKEN");
        var spotifyClientId = Environment.GetEnvironmentVariable("SPOTIFY_ID"); 
        var spotifyClientSecret = Environment.GetEnvironmentVariable("SPOTIFY_SECRET"); 

        DateTime? spotifyTokenExpiry = null;
        var expiryEnv = Environment.GetEnvironmentVariable("SPOTIFY_TOKEN_EXPIRY");
        if (expiryEnv != null)
        {
            if (DateTime.TryParse(expiryEnv, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                spotifyTokenExpiry = parsed.ToUniversalTime();
            }
            else if (long.TryParse(expiryEnv, out var unixSeconds))
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

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }


        app.MapControllers();

        app.Run();
    }
}