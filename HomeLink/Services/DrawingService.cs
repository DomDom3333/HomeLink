using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using HomeLink.Models;
using HomeLink.Utils;

namespace HomeLink.Services;

public class DrawingService
{
    // Lilygo T5 display resolution - HORIZONTAL orientation
    private const int DisplayWidth = 960;
    private const int DisplayHeight = 540;

    // Layout constants
    private const int Margin = 20;
    private const int AlbumArtSize = 250;
    private const int QrCodeSize = 120;
    private const int SmallQrCodeSize = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<DrawingService> _logger;
    private readonly FontFamily _fontFamily;
    private readonly DrawingOptions _noAaOptions;

    // Sub-services
    private readonly ImageDitheringService _ditheringService;
    private readonly QrCodeService _qrCodeService;
    private readonly MapTileService _mapTileService;
    private readonly IconDrawingService _iconDrawingService;

    public DrawingService(HttpClient httpClient, ILogger<DrawingService> logger, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        FontCollection fontCollection = new();
        _noAaOptions = new DrawingOptions
        {
            GraphicsOptions = new GraphicsOptions
            {
                Antialias = false
            }
        };
        
        // Load bundled font from Fonts folder
        string fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");
        string boldFontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans-Bold.ttf");
        
        if (File.Exists(fontPath))
        {
            _fontFamily = fontCollection.Add(fontPath);
            if (File.Exists(boldFontPath))
            {
                fontCollection.Add(boldFontPath);
            }
        }
        else
        {
            // Fallback to system fonts if bundled font not found
            _logger.LogWarning("Bundled font not found at {FontPath}. Falling back to system fonts.", fontPath);
            _fontFamily = SystemFonts.Get("DejaVu Sans");
        }

        // Initialize sub-services
        _ditheringService = new ImageDitheringService();
        _qrCodeService = new QrCodeService(
            loggerFactory.CreateLogger<QrCodeService>(), 
            _fontFamily, 
            _noAaOptions);
        _mapTileService = new MapTileService(_httpClient, _fontFamily, _noAaOptions);
        _iconDrawingService = new IconDrawingService(_noAaOptions);
    }

    /// <summary>
    /// Creates an e-ink bitmap combining Spotify and location information
    /// Horizontal layout for Lilygo T5 e-ink display
    /// </summary>
    /// <param name="spotifyData">Spotify track information</param>
    /// <param name="locationData">Location information</param>
    /// <param name="dither">Whether to apply dithering (default true)</param>
    public async Task<EInkBitmap> DrawDisplayDataAsync(SpotifyTrackInfo? spotifyData, LocationInfo? locationData, bool dither = true)
    {
        // Create a grayscale image at display resolution (horizontal)
        using Image<L8> image = new Image<L8>(DisplayWidth, DisplayHeight, new L8(255)); // White background

        DrawContent(image, spotifyData, locationData);
        
        // Draw dynamic content (album art, map)
        await DrawDynamicContentAsync(image, spotifyData, locationData);

        if (dither)
        {
            // Apply Floyd-Steinberg dithering for 1-bit conversion
            using Image<L8> ditheredBitmap = _ditheringService.DitherImage(image);
            // Convert to packed 1-bit format
            return _ditheringService.ConvertToPacked1Bit(ditheredBitmap);
        }
        else
        {
            // Return grayscale without dithering
            return _ditheringService.ConvertToGrayscaleBitmap(image);
        }
    }


    /// <summary>
    /// Renders the display image as PNG bytes for browser display
    /// </summary>
    /// <param name="spotifyData">Spotify track information</param>
    /// <param name="locationData">Location information</param>
    /// <param name="dither">Whether to apply dithering (default true)</param>
    public async Task<byte[]> RenderDisplayPngAsync(SpotifyTrackInfo? spotifyData, LocationInfo? locationData, bool dither = true)
    {
        // Create a grayscale image at display resolution (horizontal)
        using Image<L8> image = new Image<L8>(DisplayWidth, DisplayHeight, new L8(255)); // White background

        DrawContent(image, spotifyData, locationData);
        await DrawDynamicContentAsync(image, spotifyData, locationData);

        using MemoryStream ms = new MemoryStream();
        
        if (dither)
        {
            // Dither for 1-bit preview (matches e-ink look)
            using Image<L8> dithered = _ditheringService.DitherImage(image);
            await dithered.SaveAsPngAsync(ms);
        }
        else
        {
            // Return undithered grayscale image
            await image.SaveAsPngAsync(ms);
        }
        
        return ms.ToArray();
    }

    /// <summary>
    /// Draws all static content (text, icons, QR codes, etc.)
    /// </summary>
    private void DrawContent(Image<L8> image, SpotifyTrackInfo? spotifyData, LocationInfo? locationData)
    {
        Font font = _fontFamily.CreateFont(20);
        Font titleFont = _fontFamily.CreateFont(32, FontStyle.Bold);
        Font largeFont = _fontFamily.CreateFont(26);
        Font smallFont = _fontFamily.CreateFont(16);
        Font smallBoldFont = _fontFamily.CreateFont(16, FontStyle.Bold);
        Font tinyFont = _fontFamily.CreateFont(13);

        Color black = Color.Black;
        Color darkGray = new Color(new Rgba32(64, 64, 64));
        Color mediumGray = new Color(new Rgba32(100, 100, 100));
        Color lightGray = new Color(new Rgba32(160, 160, 160));

        // Calculate layout zones
        int leftColumnWidth = AlbumArtSize + Margin * 2;
        int rightColumnWidth = QrCodeSize + Margin * 2;
        int centerColumnWidth = DisplayWidth - leftColumnWidth - rightColumnWidth;
        int topSectionHeight = 290;

        // ============================================================
        // TOP SECTION - NOW PLAYING
        // ============================================================
        int centerX = leftColumnWidth;
        int yPos = Margin;

        if (spotifyData != null)
        {
            // Playback status header
            int statusY = yPos;
            string statusText = spotifyData.IsPlaying ? "PLAYING" : "PAUSED";
            
            _iconDrawingService.DrawPlaybackIcon(image, spotifyData.IsPlaying, centerX, statusY + 4, 12, darkGray);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, statusText, smallBoldFont, darkGray, new PointF(centerX + 20, statusY)));
            yPos += 25;

            // Track Title
            string trackText = TextUtils.TruncateText(spotifyData.Title, 30);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, trackText, titleFont, black, new PointF(centerX, yPos)));
            yPos += 45;

            // Artist
            string artistText = TextUtils.TruncateText(spotifyData.Artist, 35);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, artistText, largeFont, darkGray, new PointF(centerX, yPos)));
            yPos += 35;

            // Album
            string albumText = TextUtils.TruncateText(spotifyData.Album, 40);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, albumText, font, mediumGray, new PointF(centerX, yPos)));
            yPos += 35;

            // Progress bar
            int barWidth = centerColumnWidth - Margin * 2;
            _iconDrawingService.DrawProgressBar(image, spotifyData.ProgressMs, spotifyData.DurationMs, centerX, yPos, barWidth, black, lightGray);
            yPos += 20;

            // Time display
            string progressText = $"{TimeUtils.FormatTime(spotifyData.ProgressMs)} / {TimeUtils.FormatTime(spotifyData.DurationMs)}";
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, progressText, smallFont, darkGray, new PointF(centerX, yPos)));
        }
        else
        {
            // No track playing message
            int noTrackY = yPos + 50;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Nothing Playing", titleFont, mediumGray, new PointF(centerX, noTrackY)));
        }

        // === RIGHT: Spotify QR Code ===
        int spotifyQrX = DisplayWidth - QrCodeSize - Margin;
        int spotifyQrY = Margin;

        if (spotifyData != null && !string.IsNullOrEmpty(spotifyData.SpotifyUri))
        {
            _qrCodeService.DrawQrCode(image, spotifyData.SpotifyUri, spotifyQrX, spotifyQrY, QrCodeSize);
            int spotifyLabelY = spotifyQrY + QrCodeSize + 5;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Scan to Play", tinyFont, darkGray, new PointF(spotifyQrX + 15, spotifyLabelY)));
        }

        // ============================================================
        // SEPARATOR LINE
        // ============================================================
        int separatorY = topSectionHeight;
        image.Mutate(ctx =>
        {
            ctx.DrawLine(_noAaOptions, lightGray, 2, new PointF(Margin, separatorY), new PointF(DisplayWidth - Margin, separatorY));
        });

        // ============================================================
        // BOTTOM SECTION - LOCATION
        // ============================================================
        int bottomY = topSectionHeight + Margin;
        int mapSize = 180;

        if (locationData != null)
        {
            int infoX = Margin + mapSize + Margin;
            
            // Location header
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "CURRENT LOCATION", smallBoldFont, darkGray, new PointF(infoX, bottomY)));

            // Primary location name
            string locationText = !string.IsNullOrEmpty(locationData.HumanReadable)
                ? locationData.HumanReadable
                : (!string.IsNullOrEmpty(locationData.DisplayName) ? locationData.DisplayName : "Unknown Location");
            locationText = TextUtils.TruncateText(locationText, 35);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, locationText, largeFont, black, new PointF(infoX, bottomY + 22)));

            // City/District & Country
            string cityCountry = TextUtils.BuildCityCountryString(locationData);
            if (!string.IsNullOrEmpty(cityCountry))
            {
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, cityCountry, font, darkGray, new PointF(infoX, bottomY + 55)));
            }

            // Coordinates
            string coordsText = $"GPS: {locationData.Latitude:F5}, {locationData.Longitude:F5}";
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, coordsText, smallFont, darkGray, new PointF(infoX, bottomY + 85)));

            // Device status line
            DrawDeviceStatus(image, locationData, infoX, bottomY + 105, smallFont, darkGray);

            // Known location indicator
            if (locationData.MatchedKnownLocation != null)
            {
                int knownY = bottomY + 130;
                string iconPrefix = TextUtils.GetLocationIcon(locationData.MatchedKnownLocation.Icon);
                string knownText = $"{iconPrefix} {locationData.MatchedKnownLocation.DisplayText}";
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, knownText, font, black, new PointF(infoX, knownY)));

                int badgeWidth = knownText.Length * 10 + 20;
                image.Mutate(ctx =>
                {
                    ctx.DrawPolygon(_noAaOptions, darkGray, 1,
                        new PointF(infoX - 5, knownY - 3),
                        new PointF(infoX + badgeWidth, knownY - 3),
                        new PointF(infoX + badgeWidth, knownY + 22),
                        new PointF(infoX - 5, knownY + 22));
                });

                // Distance from known location
                double distance = GeoUtils.CalculateDistance(
                    locationData.Latitude, locationData.Longitude,
                    locationData.MatchedKnownLocation.Latitude, locationData.MatchedKnownLocation.Longitude);
                string distanceText = distance < 1000 
                    ? $"~{distance:F0}m from center" 
                    : $"~{distance/1000:F1}km from center";
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, distanceText, tinyFont, darkGray, new PointF(infoX, bottomY + 160)));
            }

            // === RIGHT: Maps QR Code ===
            string lat = locationData.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lon = locationData.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string mapsUrl = $"https://maps.google.com/?q={lat},{lon}";
            int mapsQrX = DisplayWidth - SmallQrCodeSize - Margin;
            int mapsQrY = bottomY + 10;

            _qrCodeService.DrawQrCode(image, mapsUrl, mapsQrX, mapsQrY, SmallQrCodeSize);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Open in Maps", tinyFont, darkGray, new PointF(mapsQrX + 5, mapsQrY + SmallQrCodeSize + 5)));
        }
        else
        {
            // No location data - draw placeholder
            int noLocX = Margin + mapSize + Margin;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Location not available", largeFont, mediumGray, new PointF(noLocX, bottomY + 50)));

            // Draw a placeholder map outline
            image.Mutate(ctx =>
            {
                ctx.DrawPolygon(_noAaOptions, lightGray, 2,
                    new PointF(Margin, bottomY),
                    new PointF(Margin + mapSize, bottomY),
                    new PointF(Margin + mapSize, bottomY + mapSize),
                    new PointF(Margin, bottomY + mapSize));
                
                ctx.DrawLine(_noAaOptions, lightGray, 1, new PointF(Margin, bottomY), new PointF(Margin + mapSize, bottomY + mapSize));
                ctx.DrawLine(_noAaOptions, lightGray, 1, new PointF(Margin + mapSize, bottomY), new PointF(Margin, bottomY + mapSize));
            });
        }

        // ============================================================
        // FOOTER
        // ============================================================
        int footerY = DisplayHeight - Margin - 12;
        string timestamp = DateTime.Now.ToString("MMM dd, yyyy  HH:mm");
        image.Mutate(ctx => ctx.DrawText(_noAaOptions, $"Updated: {timestamp}", tinyFont, mediumGray, new PointF(Margin, footerY)));
        image.Mutate(ctx => ctx.DrawText(_noAaOptions, "HomeLink", tinyFont, mediumGray, new PointF(DisplayWidth - 100, footerY)));
    }

    /// <summary>
    /// Draws dynamic content that requires async operations (album art, maps)
    /// </summary>
    private async Task DrawDynamicContentAsync(Image<L8> image, SpotifyTrackInfo? spotifyData, LocationInfo? locationData)
    {
        Font font = _fontFamily.CreateFont(20);
        Color mediumGray = new Color(new Rgba32(100, 100, 100));
        int topSectionHeight = 290;
        int bottomY = topSectionHeight + Margin;
        int mapSize = 180;

        // === LEFT: Album Art ===
        if (spotifyData != null && !string.IsNullOrEmpty(spotifyData.AlbumCoverUrl))
        {
            await DrawAlbumArtAsync(image, spotifyData.AlbumCoverUrl, Margin, Margin, AlbumArtSize);
        }
        else
        {
            DrawPlaceholder(image, Margin, Margin, AlbumArtSize, "No Album Art", font, mediumGray);
        }

        // === LEFT: Static Map Image ===
        if (locationData != null)
        {
            await _mapTileService.DrawStaticMapAsync(image, locationData.Latitude, locationData.Longitude, Margin, bottomY, mapSize);
        }
    }

    /// <summary>
    /// Draws device status information (battery, accuracy, speed, connection)
    /// </summary>
    private void DrawDeviceStatus(Image<L8> image, LocationInfo locationData, int startX, int y, Font font, Color color)
    {
        int nextX = startX;
        List<string> otherStatusParts = new List<string>();
        
        if (locationData.BatteryLevel.HasValue)
        {
            int batteryWidth = 28;
            int batteryHeight = 12;
            _iconDrawingService.DrawBatteryIcon(image, nextX, y + 2, batteryWidth, batteryHeight,
                Math.Clamp(locationData.BatteryLevel.Value, 0, 100), locationData.BatteryStatus, color, color);
            nextX += batteryWidth + 6;

            string percentText = $"{locationData.BatteryLevel}%";
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, percentText, font, color, new PointF(nextX, y)));
            nextX += percentText.Length * 8 + 12;
        }

        if (locationData.Accuracy.HasValue)
            otherStatusParts.Add($"±{locationData.Accuracy}m");
        
        if (locationData.Velocity.HasValue && locationData.Velocity.Value > 0)
            otherStatusParts.Add($"{locationData.Velocity} km/h");
        
        if (!string.IsNullOrEmpty(locationData.Connection))
        {
            string? connText = locationData.Connection switch
            {
                "w" => "WiFi",
                "m" => "Mobile",
                "o" => "Offline",
                _ => locationData.Connection
            };
            otherStatusParts.Add(connText);
        }

        if (otherStatusParts.Count > 0)
        {
            string restText = (locationData.BatteryLevel.HasValue ? "•  " : string.Empty) + string.Join("  •  ", otherStatusParts);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, restText, font, color, new PointF(nextX, y)));
        }
    }

    /// <summary>
    /// Downloads and draws album art onto the image
    /// </summary>
    private async Task DrawAlbumArtAsync(Image<L8> image, string url, int x, int y, int size)
    {
        try
        {
            byte[] imageBytes = await _httpClient.GetByteArrayAsync(url);
            using Image<L8> albumArt = Image.Load<L8>(imageBytes);
            
            albumArt.Mutate(ctx => ctx.Resize(size, size));
            image.Mutate(ctx => ctx.DrawImage(albumArt, new Point(x, y), 1f));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load album art from {Url}", url);
            Font font = _fontFamily.CreateFont(16);
            DrawPlaceholder(image, x, y, size, "Art unavailable", font, new Color(new Rgba32(180, 180, 180)));
        }
    }

    /// <summary>
    /// Draws a placeholder box with text
    /// </summary>
    private void DrawPlaceholder(Image<L8> image, int x, int y, int size, string text, Font font, Color color)
    {
        image.Mutate(ctx =>
        {
            ctx.DrawPolygon(_noAaOptions, color, 2,
                new PointF(x, y),
                new PointF(x + size, y),
                new PointF(x + size, y + size),
                new PointF(x, y + size));

            ctx.DrawText(_noAaOptions, text, font, color, new PointF(x + size / 4, y + size / 2));
        });
    }
}
