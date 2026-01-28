using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using QRCoder;
using Microsoft.Extensions.Logging;

namespace HomeLink.Services;

public class EInkBitmap
{
    /// <summary>
    /// Raw 1-bit bitmap data (packed, 8 pixels per byte)
    /// </summary>
    public byte[] PackedData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Width in pixels
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Height in pixels
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Bytes per scanline (should be Width / 8, rounded up)
    /// </summary>
    public int BytesPerLine { get; set; }
}

public class DrawingService
{
    // Lilygo T5 display resolution - HORIZONTAL orientation
    private const int DisplayWidth = 960;
    private const int DisplayHeight = 540;

    // Dithering constants
    private const float DitheringStrength = 1.0f;

    // Layout constants
    private const int Margin = 20;
    private const int AlbumArtSize = 250;
    private const int QrCodeSize = 120;
    private const int SmallQrCodeSize = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<DrawingService> _logger;
    private readonly FontCollection _fontCollection;
    private readonly FontFamily _fontFamily;
    private readonly DrawingOptions _noAaOptions;

    public DrawingService(HttpClient httpClient, ILogger<DrawingService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _fontCollection = new FontCollection();
        _noAaOptions = new DrawingOptions
        {
            GraphicsOptions = new GraphicsOptions
            {
                Antialias = false
            }
        };
        
        // Load bundled font from Fonts folder
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");
        var boldFontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans-Bold.ttf");
        
        if (File.Exists(fontPath))
        {
            _fontFamily = _fontCollection.Add(fontPath);
            if (File.Exists(boldFontPath))
            {
                _fontCollection.Add(boldFontPath);
            }
        }
        else
        {
            // Fallback to system fonts if bundled font not found
            _logger.LogWarning("Bundled font not found at {FontPath}. Falling back to system fonts.", fontPath);
            _fontFamily = SystemFonts.Get("DejaVu Sans");
        }
    }

    /// <summary>
    /// Creates an e-ink bitmap combining Spotify and location information
    /// Horizontal layout for Lilygo T5 e-ink display
    /// </summary>
    public async Task<EInkBitmap> DrawDisplayDataAsync(SpotifyTrackInfo? spotifyData, LocationInfo? locationData)
    {
        // Create a grayscale image at display resolution (horizontal)
        using var image = new Image<L8>(DisplayWidth, DisplayHeight, new L8(255)); // White background

        var font = _fontFamily.CreateFont(20);
        var titleFont = _fontFamily.CreateFont(32, FontStyle.Bold);
        var largeFont = _fontFamily.CreateFont(26);
        var smallFont = _fontFamily.CreateFont(16);
        var smallBoldFont = _fontFamily.CreateFont(16, FontStyle.Bold);
        var tinyFont = _fontFamily.CreateFont(13);

        var black = Color.Black;
        var darkGray = new Color(new Rgba32(64, 64, 64));
        var mediumGray = new Color(new Rgba32(100, 100, 100));
        var lightGray = new Color(new Rgba32(160, 160, 160));

        // Calculate layout zones
        var leftColumnWidth = AlbumArtSize + Margin * 2;
        var rightColumnWidth = QrCodeSize + Margin * 2;
        var centerColumnWidth = DisplayWidth - leftColumnWidth - rightColumnWidth;
        var topSectionHeight = 290; // Music section height

        // ============================================================
        // TOP SECTION - NOW PLAYING
        // ============================================================
        
        // === LEFT: Album Art ===
        if (spotifyData != null && !string.IsNullOrEmpty(spotifyData.AlbumCoverUrl))
        {
            await DrawAlbumArtAsync(image, spotifyData.AlbumCoverUrl, Margin, Margin, AlbumArtSize);
        }
        else
        {
            DrawPlaceholder(image, Margin, Margin, AlbumArtSize, "No Album Art", font, mediumGray);
        }

        // === CENTER: Track Info ===
        var centerX = leftColumnWidth;
        var yPos = Margin;

        if (spotifyData != null)
        {
            // Playback status header
            var statusY = yPos;
            var statusText = spotifyData.IsPlaying ? "PLAYING" : "PAUSED";
            
            // Draw icon (play triangle or pause bars)
            DrawPlaybackIcon(image, spotifyData.IsPlaying, centerX, statusY + 4, 12, darkGray);
            
            // Draw text next to icon
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, statusText, smallBoldFont, darkGray, new PointF(centerX + 20, statusY)));
            yPos += 25;

            // Track Title
            var trackText = TruncateText(spotifyData.Title, 30);
            var titleY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, trackText, titleFont, black, new PointF(centerX, titleY)));
            yPos += 45;

            // Artist
            var artistText = TruncateText(spotifyData.Artist, 35);
            var artistY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, artistText, largeFont, darkGray, new PointF(centerX, artistY)));
            yPos += 35;

            // Album
            var albumText = TruncateText(spotifyData.Album, 40);
            var albumY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, albumText, font, mediumGray, new PointF(centerX, albumY)));
            yPos += 35;

            // Progress bar
            var barWidth = centerColumnWidth - Margin * 2;
            DrawProgressBar(image, spotifyData.ProgressMs, spotifyData.DurationMs, centerX, yPos, barWidth, black, lightGray);
            yPos += 20;

            // Time display
            var progressText = $"{FormatTime(spotifyData.ProgressMs)} / {FormatTime(spotifyData.DurationMs)}";
            var timeY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, progressText, smallFont, darkGray, new PointF(centerX, timeY)));
        }
        else
        {
            // No track playing message
            var noTrackY = yPos + 50;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Nothing Playing", titleFont, mediumGray, new PointF(centerX, noTrackY)));
        }

        // === RIGHT: Spotify QR Code ===
        var spotifyQrX = DisplayWidth - QrCodeSize - Margin;
        var spotifyQrY = Margin;

        if (spotifyData != null && !string.IsNullOrEmpty(spotifyData.SpotifyUri))
        {
            DrawQrCode(image, spotifyData.SpotifyUri, spotifyQrX, spotifyQrY, QrCodeSize);
            
            // Label under QR code
            var spotifyLabelY = spotifyQrY + QrCodeSize + 5;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Scan to Play", tinyFont, darkGray, new PointF(spotifyQrX + 15, spotifyLabelY)));
        }

        // ============================================================
        // SEPARATOR LINE
        // ============================================================
        var separatorY = topSectionHeight;
        image.Mutate(ctx =>
        {
            ctx.DrawLine(_noAaOptions, lightGray, 2, new PointF(Margin, separatorY), new PointF(DisplayWidth - Margin, separatorY));
        });

        // ============================================================
        // BOTTOM SECTION - LOCATION
        // ============================================================
        var bottomY = topSectionHeight + Margin;
        var mapSize = 180; // Size for the static map

        if (locationData != null)
        {
            // === LEFT: Static Map Image ===
            await DrawStaticMapAsync(image, locationData.Latitude, locationData.Longitude, Margin, bottomY, mapSize);

            // === CENTER: Location Info ===
            var infoX = Margin + mapSize + Margin;
            
            // Location header
            var locHeaderY = bottomY;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "CURRENT LOCATION", smallBoldFont, darkGray, new PointF(infoX, locHeaderY)));

            // Primary location name (human readable or display name)
            var locationText = !string.IsNullOrEmpty(locationData.HumanReadable)
                ? locationData.HumanReadable
                : (!string.IsNullOrEmpty(locationData.DisplayName) ? locationData.DisplayName : "Unknown Location");
            locationText = TruncateText(locationText, 35);

            var locTextY = bottomY + 22;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, locationText, largeFont, black, new PointF(infoX, locTextY)));

            // City/District & Country
            var cityCountry = BuildCityCountryString(locationData);
            if (!string.IsNullOrEmpty(cityCountry))
            {
                var cityY = bottomY + 55;
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, cityCountry, font, darkGray, new PointF(infoX, cityY)));
            }

            // Coordinates with icon-style prefix
            var coordsText = $"GPS: {locationData.Latitude:F5}, {locationData.Longitude:F5}";
            var coordsY = bottomY + 85;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, coordsText, smallFont, darkGray, new PointF(infoX, coordsY)));

            // Device status line (battery icon + percent, accuracy, speed, connection)
            var deviceStatusY = bottomY + 105;
            var nextX = infoX;
            var otherStatusParts = new List<string>();
            
            if (locationData.BatteryLevel.HasValue)
            {
                // Draw battery icon
                int batteryWidth = 28;
                int batteryHeight = 12;
                int iconOffsetY = 2; // align with text
                DrawBatteryIcon(image, nextX, deviceStatusY + iconOffsetY, batteryWidth, batteryHeight,
                    Math.Clamp(locationData.BatteryLevel.Value, 0, 100), locationData.BatteryStatus, darkGray, darkGray);

                nextX += batteryWidth + 6; // space after icon

                // Draw battery percent text
                var percentText = $"{locationData.BatteryLevel}%";
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, percentText, smallFont, darkGray, new PointF(nextX, deviceStatusY)));
                // Advance by an estimated width (~8px per character at this font size) plus spacing
                var approxWidth = percentText.Length * 8;
                nextX += approxWidth + 12; // add spacing before other parts
            }

            // Build remaining status parts
            if (locationData.Accuracy.HasValue)
            {
                otherStatusParts.Add($"±{locationData.Accuracy}m");
            }
            
            if (locationData.Velocity.HasValue && locationData.Velocity.Value > 0)
            {
                otherStatusParts.Add($"{locationData.Velocity} km/h");
            }
            
            if (!string.IsNullOrEmpty(locationData.Connection))
            {
                var connText = locationData.Connection switch
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
                var restText = (locationData.BatteryLevel.HasValue ? "•  " : string.Empty) + string.Join("  •  ", otherStatusParts);
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, restText, smallFont, darkGray, new PointF(nextX, deviceStatusY)));
            }

            // Known location indicator (if matched)
            if (locationData.MatchedKnownLocation != null)
            {
                var knownY = bottomY + 130;  // Moved down to accommodate device status
                var iconPrefix = GetLocationIcon(locationData.MatchedKnownLocation.Icon);
                var knownText = $"{iconPrefix} {locationData.MatchedKnownLocation.DisplayText}";
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, knownText, font, black, new PointF(infoX, knownY)));

                // Draw a small box/badge around known location
                var badgeWidth = knownText.Length * 10 + 20;
                image.Mutate(ctx =>
                {
                    ctx.DrawPolygon(_noAaOptions, darkGray, 1,
                        new PointF(infoX - 5, knownY - 3),
                        new PointF(infoX + badgeWidth, knownY - 3),
                        new PointF(infoX + badgeWidth, knownY + 22),
                        new PointF(infoX - 5, knownY + 22));
                });
            }

            // Distance from known location (if applicable)
            if (locationData.MatchedKnownLocation != null)
            {
                var distance = CalculateDistance(
                    locationData.Latitude, locationData.Longitude,
                    locationData.MatchedKnownLocation.Latitude, locationData.MatchedKnownLocation.Longitude);
                var distanceText = distance < 1000 
                    ? $"~{distance:F0}m from center" 
                    : $"~{distance/1000:F1}km from center";
                var distY = bottomY + 160;  // Moved down to accommodate device status
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, distanceText, tinyFont, darkGray, new PointF(infoX, distY)));
            }

            // === RIGHT: Maps QR Code ===
            var lat = locationData.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon = locationData.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var mapsUrl = $"https://maps.google.com/?q={lat},{lon}";
            var mapsQrX = DisplayWidth - SmallQrCodeSize - Margin;
            var mapsQrY = bottomY + 10;

            DrawQrCode(image, mapsUrl, mapsQrX, mapsQrY, SmallQrCodeSize);

            // Label under Maps QR code
            var mapsLabelY = mapsQrY + SmallQrCodeSize + 5;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Open in Maps", tinyFont, darkGray, new PointF(mapsQrX + 5, mapsLabelY)));
        }
        else
        {
            // No location data - draw placeholder
            var noLocY = bottomY + 50;
            var noLocX = Margin + mapSize + Margin;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Location not available", largeFont, mediumGray, new PointF(noLocX, noLocY)));

            // Draw a placeholder map outline
            image.Mutate(ctx =>
            {
                ctx.DrawPolygon(_noAaOptions, lightGray, 2,
                    new PointF(Margin, bottomY),
                    new PointF(Margin + mapSize, bottomY),
                    new PointF(Margin + mapSize, bottomY + mapSize),
                    new PointF(Margin, bottomY + mapSize));
                
                // Draw X through it
                ctx.DrawLine(_noAaOptions, lightGray, 1, new PointF(Margin, bottomY), new PointF(Margin + mapSize, bottomY + mapSize));
                ctx.DrawLine(_noAaOptions, lightGray, 1, new PointF(Margin + mapSize, bottomY), new PointF(Margin, bottomY + mapSize));
            });
        }

        // ============================================================
        // FOOTER
        // ============================================================
        var footerY = DisplayHeight - Margin - 12;
        var timestamp = DateTime.Now.ToString("MMM dd, yyyy  HH:mm");
        image.Mutate(ctx => ctx.DrawText(_noAaOptions, $"Updated: {timestamp}", tinyFont, mediumGray, new PointF(Margin, footerY)));

        // HomeLink branding
        image.Mutate(ctx => ctx.DrawText(_noAaOptions, "HomeLink", tinyFont, mediumGray, new PointF(DisplayWidth - 100, footerY)));

        // Apply Floyd-Steinberg dithering for 1-bit conversion
        var ditheredBitmap = DitherImage(image);

        // Convert to packed 1-bit format
        var einkBitmap = ConvertToPacked1Bit(ditheredBitmap);

        ditheredBitmap.Dispose();

        return einkBitmap;
    }

    /// <summary>
    /// Synchronous version for backward compatibility
    /// </summary>
    public EInkBitmap DrawDisplayData(SpotifyTrackInfo? spotifyData, LocationInfo? locationData)
    {
        return DrawDisplayDataAsync(spotifyData, locationData).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Downloads and draws album art onto the image
    /// </summary>
    private async Task DrawAlbumArtAsync(Image<L8> image, string url, int x, int y, int size)
    {
        try
        {
            var imageBytes = await _httpClient.GetByteArrayAsync(url);
            using var albumArt = Image.Load<L8>(imageBytes);
            
            // Resize to fit
            albumArt.Mutate(ctx => ctx.Resize(size, size));

            // Draw onto main image
            image.Mutate(ctx => ctx.DrawImage(albumArt, new Point(x, y), 1f));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load album art from {Url}", url);
            // Draw placeholder on error
            var font = _fontFamily.CreateFont(16);
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
            // Draw border
            ctx.DrawPolygon(_noAaOptions, color, 2,
                new PointF(x, y),
                new PointF(x + size, y),
                new PointF(x + size, y + size),
                new PointF(x, y + size));

            // Draw text centered
            ctx.DrawText(_noAaOptions, text, font, color, new PointF(x + size / 4, y + size / 2));
        });
    }

    /// <summary>
    /// Generates and draws a QR code for the given data
    /// </summary>
    private void DrawQrCode(Image<L8> image, string data, int x, int y, int size)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var qrBytes = qrCode.GetGraphic(20);

            using var qrImage = Image.Load<L8>(qrBytes);
            qrImage.Mutate(ctx => ctx.Resize(size, size, KnownResamplers.NearestNeighbor));

            image.Mutate(ctx => ctx.DrawImage(qrImage, new Point(x, y), 1f));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate QR code");
            // Draw placeholder on error
            var font = _fontFamily.CreateFont(12);
            DrawPlaceholder(image, x, y, size, "QR Error", font, new Color(new Rgba32(180, 180, 180)));
        }
    }

    /// <summary>
    /// Draws a progress bar for track playback
    /// </summary>
    private void DrawProgressBar(Image<L8> image, long progressMs, long totalMs, int x, int y, int width, Color fillColor, Color bgColor)
    {
        const int barHeight = 12;

        // Background
        image.Mutate(ctx =>
        {
            ctx.FillPolygon(_noAaOptions, bgColor,
                new PointF(x, y),
                new PointF(x + width, y),
                new PointF(x + width, y + barHeight),
                new PointF(x, y + barHeight));
        });

        // Progress fill
        if (totalMs > 0)
        {
            var progressWidth = (int)((double)progressMs / totalMs * width);
            if (progressWidth > 0)
            {
                image.Mutate(ctx =>
                {
                    ctx.FillPolygon(_noAaOptions, fillColor,
                        new PointF(x, y),
                        new PointF(x + progressWidth, y),
                        new PointF(x + progressWidth, y + barHeight),
                        new PointF(x, y + barHeight));
                });
            }
        }

        // Border
        image.Mutate(ctx =>
        {
            ctx.DrawPolygon(_noAaOptions, fillColor, 1,
                new PointF(x, y),
                new PointF(x + width, y),
                new PointF(x + width, y + barHeight),
                new PointF(x, y + barHeight));
        });
    }

    /// <summary>
    /// Draws a play or pause icon
    /// </summary>
    private void DrawPlaybackIcon(Image<L8> image, bool isPlaying, int x, int y, int size, Color color)
    {
        image.Mutate(ctx =>
        {
            if (isPlaying)
            {
                // Draw Play Triangle
                var points = new PointF[]
                {
                    new PointF(x, y),
                    new PointF(x + size, y + size / 2f),
                    new PointF(x, y + size)
                };
                ctx.FillPolygon(_noAaOptions, color, points);
            }
            else
            {
                // Draw Pause Bars
                var barWidth = size / 3f;
                
                // Left bar
                ctx.FillPolygon(_noAaOptions, color,
                    new PointF(x, y),
                    new PointF(x + barWidth, y),
                    new PointF(x + barWidth, y + size),
                    new PointF(x, y + size));
                
                // Right bar
                ctx.FillPolygon(_noAaOptions, color,
                    new PointF(x + size - barWidth, y),
                    new PointF(x + size, y),
                    new PointF(x + size, y + size),
                    new PointF(x + size - barWidth, y + size));
            }
        });
    }

    /// <summary>
    /// Draws a battery icon with fill level and optional charging indicator.
    /// </summary>
    private void DrawBatteryIcon(Image<L8> image, int x, int y, int width, int height, int levelPercent, int? batteryStatus, Color outlineColor, Color fillColor)
    {
        // Clamp inputs
        levelPercent = Math.Clamp(levelPercent, 0, 100);
        if (width < 10) width = 10;
        if (height < 8) height = 8;

        // Terminal cap dimensions
        var capWidth = Math.Max(2, width / 8);
        var bodyWidth = width - capWidth - 1;

        // Battery body (rectangle)
        image.Mutate(ctx =>
        {
            // Body border
            ctx.DrawPolygon(_noAaOptions, outlineColor, 1,
                new PointF(x, y),
                new PointF(x + bodyWidth, y),
                new PointF(x + bodyWidth, y + height),
                new PointF(x, y + height));

            // Terminal cap
            var capX = x + bodyWidth + 1;
            var capTop = y + height / 3f;
            var capBottom = y + height - height / 3f;
            ctx.FillPolygon(_noAaOptions, outlineColor,
                new PointF(capX, capTop),
                new PointF(capX + capWidth, capTop),
                new PointF(capX + capWidth, capBottom),
                new PointF(capX, capBottom));

            // Fill level inside body (leave 2px padding)
            var innerX = x + 2;
            var innerY = y + 2;
            var innerW = Math.Max(0, bodyWidth - 4);
            var innerH = Math.Max(0, height - 4);
            var fillW = (int)Math.Round(innerW * (levelPercent / 100.0));
            if (fillW > 0)
            {
                ctx.FillPolygon(_noAaOptions, fillColor,
                    new PointF(innerX, innerY),
                    new PointF(innerX + fillW, innerY),
                    new PointF(innerX + fillW, innerY + innerH),
                    new PointF(innerX, innerY + innerH));
            }

            // If charging, draw a simple lightning bolt overlay
            if (batteryStatus == 2) // charging
            {
                var boltCenterX = x + bodyWidth / 2f;
                var boltTop = y + 2;
                var boltBottom = y + height - 2;
                var boltW = Math.Max(2f, bodyWidth * 0.20f);
                var boltMidY = (boltTop + boltBottom) / 2f;
                var boltPoints = new PointF[]
                {
                    new PointF(boltCenterX - boltW * 0.3f, boltTop),
                    new PointF(boltCenterX + boltW * 0.5f, boltTop),
                    new PointF(boltCenterX - boltW * 0.2f, boltMidY),
                    new PointF(boltCenterX + boltW * 0.4f, boltMidY),
                    new PointF(boltCenterX - boltW * 0.7f, boltBottom),
                    new PointF(boltCenterX - boltW * 0.3f, boltMidY)
                };
                ctx.FillPolygon(_noAaOptions, Color.Black, boltPoints);
            }
        });
    }

    /// <summary>
    /// Applies Floyd-Steinberg dithering to convert grayscale to 1-bit
    /// </summary>
    private Image<L8> DitherImage(Image<L8> image)
    {
        var width = image.Width;
        var height = image.Height;

        // Create error accumulation buffer
        var errorBuffer = new float[width * height];

        // Copy image to work with
        var working = image.Clone();

        // Floyd-Steinberg dithering
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var idx = y * width + x;
                var pixel = working[x, y];

                // Skip dithering for pure black or pure white pixels to preserve sharp edges
                // and solid areas (like text and QR codes)
                if (pixel.PackedValue == 0 || pixel.PackedValue == 255)
                {
                    working[x, y] = pixel;
                    continue;
                }

                var value = pixel.PackedValue + errorBuffer[idx] * DitheringStrength;

                // Clamp value to valid range
                value = Math.Max(0, Math.Min(255, value));

                // Quantize to black or white
                byte quantized = value < 128 ? (byte)0 : (byte)255;
                float error = value - quantized;

                working[x, y] = new L8(quantized);

                // Distribute error to neighboring pixels
                if (x + 1 < width)
                    errorBuffer[idx + 1] += error * 7f / 16f;
                if (y + 1 < height)
                {
                    if (x > 0)
                        errorBuffer[idx + width - 1] += error * 3f / 16f;
                    errorBuffer[idx + width] += error * 5f / 16f;
                    if (x + 1 < width)
                        errorBuffer[idx + width + 1] += error * 1f / 16f;
                }
            }
        }

        return working;
    }

    /// <summary>
    /// Converts an image to packed 1-bit format
    /// </summary>
    private EInkBitmap ConvertToPacked1Bit(Image<L8> image)
    {
        var width = image.Width;
        var height = image.Height;
        var bytesPerLine = (width + 7) / 8;
        var totalBytes = bytesPerLine * height;

        var packedData = new byte[totalBytes];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = image[x, y];
                // 0 = black (1 in 1-bit), 255 = white (0 in 1-bit)
                var bit = pixel.PackedValue < 128 ? 1 : 0;

                var byteIdx = y * bytesPerLine + x / 8;
                var bitIdx = 7 - (x % 8); // MSB first

                packedData[byteIdx] |= (byte)(bit << bitIdx);
            }
        }

        return new EInkBitmap
        {
            PackedData = packedData,
            Width = width,
            Height = height,
            BytesPerLine = bytesPerLine
        };
    }

    /// <summary>
    /// Truncates text to fit within character limit
    /// </summary>
    private string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength - 3) + "...";
    }

    /// <summary>
    /// Formats milliseconds to MM:SS format
    /// </summary>
    private string FormatTime(long milliseconds)
    {
        var totalSeconds = milliseconds / 1000;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes:D2}:{seconds:D2}";
    }

    /// <summary>
    /// Downloads and draws a static map image from OpenStreetMap tiles
    /// </summary>
    private async Task DrawStaticMapAsync(Image<L8> image, double latitude, double longitude, int x, int y, int size)
    {
        try
        {
            // Zoom level: 15 = ~500m view, 16 = ~250m (good for neighborhood)
            // Using zoom 16 for approximately 1-2 block radius view
            var zoom = 16;
            
            // Convert lat/lon to tile coordinates
            var (tileX, tileY, pixelOffsetX, pixelOffsetY) = LatLonToTile(latitude, longitude, zoom);
            
            // We need to fetch multiple tiles to fill the size (tiles are 256x256)
            const int tileSize = 256;
            var tilesNeeded = (int)Math.Ceiling((double)size / tileSize) + 1;
            
            // Create a temporary image to composite tiles
            using var mapComposite = new Image<L8>(tilesNeeded * tileSize, tilesNeeded * tileSize, new L8(240));
            
            // Fetch tiles in a grid around the center tile
            var startTileX = tileX - tilesNeeded / 2;
            var startTileY = tileY - tilesNeeded / 2;
            
            for (int ty = 0; ty < tilesNeeded; ty++)
            {
                for (int tx = 0; tx < tilesNeeded; tx++)
                {
                    var currentTileX = startTileX + tx;
                    var currentTileY = startTileY + ty;
                    
                    try
                    {
                        // Use OSM tile server (be respectful of usage policy)
                        var tileUrl = $"https://tile.openstreetmap.org/{zoom}/{currentTileX}/{currentTileY}.png";
                        
                        using var request = new HttpRequestMessage(HttpMethod.Get, tileUrl);
                        request.Headers.Add("User-Agent", "HomeLink/1.0 (E-Ink Display Application)");
                        
                        var response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var tileBytes = await response.Content.ReadAsByteArrayAsync();
                            using var tileImage = Image.Load<L8>(tileBytes);
                            
                            // Draw tile onto composite
                            var drawX = tx * tileSize;
                            var drawY = ty * tileSize;
                            mapComposite.Mutate(ctx => ctx.DrawImage(tileImage, new Point(drawX, drawY), 1f));
                        }
                    }
                    catch
                    {
                        // Skip failed tiles, they'll just be gray
                    }
                }
            }
            
            // Calculate the crop region to center on our location
            var centerPixelX = (tilesNeeded / 2) * tileSize + pixelOffsetX;
            var centerPixelY = (tilesNeeded / 2) * tileSize + pixelOffsetY;
            var cropX = Math.Max(0, centerPixelX - size / 2);
            var cropY = Math.Max(0, centerPixelY - size / 2);
            
            // Crop and resize to target size
            mapComposite.Mutate(ctx => ctx
                .Crop(new Rectangle(cropX, cropY, Math.Min(size, mapComposite.Width - cropX), Math.Min(size, mapComposite.Height - cropY)))
                .Resize(size, size));
            
            // Draw onto main image
            image.Mutate(ctx => ctx.DrawImage(mapComposite, new Point(x, y), 1f));

            // Draw a center marker (crosshair)
            var centerX = x + size / 2;
            var centerY = y + size / 2;
            var markerSize = 10;
            image.Mutate(ctx =>
            {
                // Outer black circle
                for (int angle = 0; angle < 360; angle += 30)
                {
                    var rad = angle * Math.PI / 180;
                    var px = centerX + (int)(markerSize * Math.Cos(rad));
                    var py = centerY + (int)(markerSize * Math.Sin(rad));
                    ctx.DrawLine(_noAaOptions, Color.Black, 2, new PointF(centerX, centerY), new PointF(px, py));
                }
                // Inner white dot
                ctx.FillPolygon(_noAaOptions, Color.White,
                    new PointF(centerX - 3, centerY - 3),
                    new PointF(centerX + 3, centerY - 3),
                    new PointF(centerX + 3, centerY + 3),
                    new PointF(centerX - 3, centerY + 3));
                // Center black dot
                ctx.FillPolygon(_noAaOptions, Color.Black,
                    new PointF(centerX - 1, centerY - 1),
                    new PointF(centerX + 1, centerY - 1),
                    new PointF(centerX + 1, centerY + 1),
                    new PointF(centerX - 1, centerY + 1));
            });

            // Draw border around map
            image.Mutate(ctx =>
            {
                ctx.DrawPolygon(_noAaOptions, Color.Black, 2,
                    new PointF(x, y),
                    new PointF(x + size, y),
                    new PointF(x + size, y + size),
                    new PointF(x, y + size));
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load static map: {ex.Message}");
            // Draw placeholder map on error
            DrawMapPlaceholder(image, x, y, size, latitude, longitude);
        }
    }

    /// <summary>
    /// Converts latitude/longitude to tile coordinates
    /// </summary>
    private (int tileX, int tileY, int pixelOffsetX, int pixelOffsetY) LatLonToTile(double lat, double lon, int zoom)
    {
        var n = Math.Pow(2, zoom);
        var latRad = lat * Math.PI / 180;
        
        var tileXExact = (lon + 180.0) / 360.0 * n;
        var tileYExact = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        
        var tileX = (int)Math.Floor(tileXExact);
        var tileY = (int)Math.Floor(tileYExact);
        
        // Calculate pixel offset within the tile (tiles are 256x256)
        var pixelOffsetX = (int)((tileXExact - tileX) * 256);
        var pixelOffsetY = (int)((tileYExact - tileY) * 256);
        
        return (tileX, tileY, pixelOffsetX, pixelOffsetY);
    }

    /// <summary>
    /// Draws a placeholder when map cannot be loaded
    /// </summary>
    private void DrawMapPlaceholder(Image<L8> image, int x, int y, int size, double lat, double lon)
    {
        var lightGray = new Color(new Rgba32(200, 200, 200));
        var mediumGray = new Color(new Rgba32(150, 150, 150));
        var font = _fontFamily.CreateFont(10);

        image.Mutate(ctx =>
        {
            // Fill background
            ctx.FillPolygon(_noAaOptions, lightGray,
                new PointF(x, y),
                new PointF(x + size, y),
                new PointF(x + size, y + size),
                new PointF(x, y + size));

            // Draw grid lines to simulate map
            for (int i = 1; i < 4; i++)
            {
                var offset = size * i / 4;
                ctx.DrawLine(_noAaOptions, mediumGray, 1, new PointF(x + offset, y), new PointF(x + offset, y + size));
                ctx.DrawLine(_noAaOptions, mediumGray, 1, new PointF(x, y + offset), new PointF(x + size, y + offset));
            }

            // Draw crosshair at center
            var centerX = x + size / 2;
            var centerY = y + size / 2;
            ctx.DrawLine(_noAaOptions, Color.Black, 2, new PointF(centerX - 10, centerY), new PointF(centerX + 10, centerY));
            ctx.DrawLine(_noAaOptions, Color.Black, 2, new PointF(centerX, centerY - 10), new PointF(centerX, centerY + 10));

            // Draw border
            ctx.DrawPolygon(_noAaOptions, Color.Black, 2,
                new PointF(x, y),
                new PointF(x + size, y),
                new PointF(x + size, y + size),
                new PointF(x, y + size));

            // Draw coordinates at bottom
            var coordText = $"{lat:F3}, {lon:F3}";
            ctx.DrawText(_noAaOptions, coordText, font, Color.Black, new PointF(x + 5, y + size - 15));
        });
    }

    /// <summary>
    /// Builds a city/country string from location data
    /// </summary>
    private string BuildCityCountryString(LocationInfo locationData)
    {
        var parts = new List<string>();

        // Add city/town/village (prefer city, then town, then village)
        if (!string.IsNullOrEmpty(locationData.City))
            parts.Add(locationData.City);
        else if (!string.IsNullOrEmpty(locationData.Town))
            parts.Add(locationData.Town);
        else if (!string.IsNullOrEmpty(locationData.Village))
            parts.Add(locationData.Village);

        // Add district if different from city
        if (!string.IsNullOrEmpty(locationData.District) && 
            locationData.District != locationData.City &&
            locationData.District != locationData.Town)
        {
            parts.Add(locationData.District);
        }

        // Add country
        if (!string.IsNullOrEmpty(locationData.Country))
            parts.Add(locationData.Country);

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gets an icon prefix for known location types
    /// </summary>
    private string GetLocationIcon(string? iconType)
    {
        return iconType?.ToLower() switch
        {
            "home" => "[HOME]",
            "work" => "[WORK]",
            "gym" => "[GYM]",
            "school" => "[SCHOOL]",
            "shop" => "[SHOP]",
            "restaurant" => "[FOOD]",
            "cafe" => "[CAFE]",
            "park" => "[PARK]",
            "hospital" => "[HOSPITAL]",
            "airport" => "[AIRPORT]",
            "station" => "[STATION]",
            "hotel" => "[HOTEL]",
            "friend" => "[FRIEND]",
            "family" => "[FAMILY]",
            _ => "[*]"
        };
    }

    /// <summary>
    /// Calculates distance between two coordinates using Haversine formula
    /// </summary>
    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6371000;

        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Converts degrees to radians
    /// </summary>
    private double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180;
    }

    /// <summary>
    /// Renders the display image as PNG bytes for browser display
    /// Similar to DrawDisplayDataAsync but without altering existing methods
    /// </summary>
    public async Task<byte[]> RenderDisplayPngAsync(SpotifyTrackInfo? spotifyData, LocationInfo? locationData)
    {
        // Create a grayscale image at display resolution (horizontal)
        using var image = new Image<L8>(DisplayWidth, DisplayHeight, new L8(255)); // White background

        var font = _fontFamily.CreateFont(20);
        var titleFont = _fontFamily.CreateFont(32, FontStyle.Bold);
        var largeFont = _fontFamily.CreateFont(26);
        var smallFont = _fontFamily.CreateFont(16);
        var smallBoldFont = _fontFamily.CreateFont(16, FontStyle.Bold);
        var tinyFont = _fontFamily.CreateFont(13);

        var black = Color.Black;
        var darkGray = new Color(new Rgba32(64, 64, 64));
        var mediumGray = new Color(new Rgba32(100, 100, 100));
        var lightGray = new Color(new Rgba32(160, 160, 160));

        // Layout constants mirroring DrawDisplayDataAsync
        var leftColumnWidth = AlbumArtSize + Margin * 2;
        var rightColumnWidth = QrCodeSize + Margin * 2;
        var centerColumnWidth = DisplayWidth - leftColumnWidth - rightColumnWidth;
        var topSectionHeight = 290; // Music section height

        // === LEFT: Album Art ===
        if (spotifyData != null && !string.IsNullOrEmpty(spotifyData.AlbumCoverUrl))
        {
            await DrawAlbumArtAsync(image, spotifyData.AlbumCoverUrl, Margin, Margin, AlbumArtSize);
        }
        else
        {
            DrawPlaceholder(image, Margin, Margin, AlbumArtSize, "No Album Art", font, mediumGray);
        }

        // === CENTER: Track Info ===
        var centerX = leftColumnWidth;
        var yPos = Margin;

        if (spotifyData != null)
        {
            var statusY = yPos;
            var statusText = spotifyData.IsPlaying ? "PLAYING" : "PAUSED";
            DrawPlaybackIcon(image, spotifyData.IsPlaying, centerX, statusY + 4, 12, darkGray);
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, statusText, smallBoldFont, darkGray, new PointF(centerX + 20, statusY)));
            yPos += 25;

            var trackText = TruncateText(spotifyData.Title, 30);
            var titleY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, trackText, titleFont, black, new PointF(centerX, titleY)));
            yPos += 45;

            var artistText = TruncateText(spotifyData.Artist, 35);
            var artistY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, artistText, largeFont, darkGray, new PointF(centerX, artistY)));
            yPos += 35;

            var albumText = TruncateText(spotifyData.Album, 40);
            var albumY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, albumText, font, mediumGray, new PointF(centerX, albumY)));
            yPos += 35;

            var barWidth = centerColumnWidth - Margin * 2;
            DrawProgressBar(image, spotifyData.ProgressMs, spotifyData.DurationMs, centerX, yPos, barWidth, black, lightGray);
            yPos += 20;

            var progressText = $"{FormatTime(spotifyData.ProgressMs)} / {FormatTime(spotifyData.DurationMs)}";
            var timeY = yPos;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, progressText, smallFont, darkGray, new PointF(centerX, timeY)));
        }
        else
        {
            var noTrackY = yPos + 50;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Nothing Playing", titleFont, mediumGray, new PointF(centerX, noTrackY)));
        }

        // === RIGHT: Spotify QR Code ===
        var spotifyQrX = DisplayWidth - QrCodeSize - Margin;
        var spotifyQrY = Margin;

        if (spotifyData != null && !string.IsNullOrEmpty(spotifyData.SpotifyUri))
        {
            DrawQrCode(image, spotifyData.SpotifyUri, spotifyQrX, spotifyQrY, QrCodeSize);
            var spotifyLabelY = spotifyQrY + QrCodeSize + 5;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Scan to Play", tinyFont, darkGray, new PointF(spotifyQrX + 15, spotifyLabelY)));
        }

        // Separator line
        var separatorY = topSectionHeight;
        image.Mutate(ctx => { ctx.DrawLine(_noAaOptions, lightGray, 2, new PointF(Margin, separatorY), new PointF(DisplayWidth - Margin, separatorY)); });

        // === BOTTOM: LOCATION ===
        var bottomY = topSectionHeight + Margin;
        var mapSize = 180;

        if (locationData != null)
        {
            await DrawStaticMapAsync(image, locationData.Latitude, locationData.Longitude, Margin, bottomY, mapSize);
            var infoX = Margin + mapSize + Margin;
            var locHeaderY = bottomY;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "CURRENT LOCATION", smallBoldFont, darkGray, new PointF(infoX, locHeaderY)));
            var locationText = !string.IsNullOrEmpty(locationData.HumanReadable)
                ? locationData.HumanReadable
                : (!string.IsNullOrEmpty(locationData.DisplayName) ? locationData.DisplayName : "Unknown Location");
            locationText = TruncateText(locationText, 35);
            var locTextY = bottomY + 22;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, locationText, largeFont, black, new PointF(infoX, locTextY)));
            var cityCountry = BuildCityCountryString(locationData);
            if (!string.IsNullOrEmpty(cityCountry))
            {
                var cityY = bottomY + 55;
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, cityCountry, font, darkGray, new PointF(infoX, cityY)));
            }
            var coordsText = $"GPS: {locationData.Latitude:F5}, {locationData.Longitude:F5}";
            var coordsY = bottomY + 85;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, coordsText, smallFont, darkGray, new PointF(infoX, coordsY)));

            // Device status row
            var deviceStatusY = bottomY + 105;
            var nextX = infoX;
            var otherStatusParts = new List<string>();
            if (locationData.BatteryLevel.HasValue)
            {
                int batteryWidth = 28;
                int batteryHeight = 12;
                DrawBatteryIcon(image, nextX, deviceStatusY + 2, batteryWidth, batteryHeight,
                    Math.Clamp(locationData.BatteryLevel.Value, 0, 100), locationData.BatteryStatus, darkGray, darkGray);
                nextX += batteryWidth + 6;

                var percentText = $"{locationData.BatteryLevel}%";
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, percentText, smallFont, darkGray, new PointF(nextX, deviceStatusY)));
                var approxWidth = percentText.Length * 8;
                nextX += approxWidth + 12;
            }
            if (locationData.Accuracy.HasValue)
            {
                otherStatusParts.Add($"±{locationData.Accuracy}m");
            }
            if (locationData.Velocity.HasValue && locationData.Velocity.Value > 0)
            {
                otherStatusParts.Add($"{locationData.Velocity} km/h");
            }
            if (!string.IsNullOrEmpty(locationData.Connection))
            {
                var connText = locationData.Connection switch
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
                var restText = (locationData.BatteryLevel.HasValue ? "•  " : string.Empty) + string.Join("  •  ", otherStatusParts);
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, restText, smallFont, darkGray, new PointF(nextX, deviceStatusY)));
            }
            if (locationData.MatchedKnownLocation != null)
            {
                var knownY = bottomY + 130;
                var iconPrefix = GetLocationIcon(locationData.MatchedKnownLocation.Icon);
                var knownText = $"{iconPrefix} {locationData.MatchedKnownLocation.DisplayText}";
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, knownText, font, black, new PointF(infoX, knownY)));
                var badgeWidth = knownText.Length * 10 + 20;
                image.Mutate(ctx =>
                {
                    ctx.DrawPolygon(_noAaOptions, darkGray, 1,
                        new PointF(infoX - 5, knownY - 3),
                        new PointF(infoX + badgeWidth, knownY - 3),
                        new PointF(infoX + badgeWidth, knownY + 22),
                        new PointF(infoX - 5, knownY + 22));
                });
            }
            if (locationData.MatchedKnownLocation != null)
            {
                var distance = CalculateDistance(
                    locationData.Latitude, locationData.Longitude,
                    locationData.MatchedKnownLocation.Latitude, locationData.MatchedKnownLocation.Longitude);
                var distanceText = distance < 1000 ? $"~{distance:F0}m from center" : $"~{distance/1000:F1}km from center";
                var distY = bottomY + 160;
                image.Mutate(ctx => ctx.DrawText(_noAaOptions, distanceText, tinyFont, darkGray, new PointF(infoX, distY)));
            }
            // === RIGHT: Maps QR Code ===
            var lat = locationData.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon = locationData.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var mapsUrl = $"https://maps.google.com/?q={lat},{lon}";
            var mapsQrX = DisplayWidth - SmallQrCodeSize - Margin;
            var mapsQrY = bottomY + 10;

            DrawQrCode(image, mapsUrl, mapsQrX, mapsQrY, SmallQrCodeSize);

            // Label under Maps QR code
            var mapsLabelY = mapsQrY + SmallQrCodeSize + 5;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Open in Maps", tinyFont, darkGray, new PointF(mapsQrX + 5, mapsLabelY)));
        }
        else
        {
            // No location data - draw placeholder
            var noLocY = bottomY + 50;
            var noLocX = Margin + mapSize + Margin;
            image.Mutate(ctx => ctx.DrawText(_noAaOptions, "Location not available", largeFont, mediumGray, new PointF(noLocX, noLocY)));

            // Draw a placeholder map outline
            image.Mutate(ctx =>
            {
                ctx.DrawPolygon(_noAaOptions, lightGray, 2,
                    new PointF(Margin, bottomY),
                    new PointF(Margin + mapSize, bottomY),
                    new PointF(Margin + mapSize, bottomY + mapSize),
                    new PointF(Margin, bottomY + mapSize));
                
                // Draw X through it
                ctx.DrawLine(_noAaOptions, lightGray, 1, new PointF(Margin, bottomY), new PointF(Margin + mapSize, bottomY + mapSize));
                ctx.DrawLine(_noAaOptions, lightGray, 1, new PointF(Margin + mapSize, bottomY), new PointF(Margin, bottomY + mapSize));
            });
        }

        // ============================================================
        // FOOTER
        // ============================================================
        var footerY = DisplayHeight - Margin - 12;
        var timestamp = DateTime.Now.ToString("MMM dd, yyyy  HH:mm");
        image.Mutate(ctx => ctx.DrawText(_noAaOptions, $"Updated: {timestamp}", tinyFont, mediumGray, new PointF(Margin, footerY)));

        // HomeLink branding
        image.Mutate(ctx => ctx.DrawText(_noAaOptions, "HomeLink", tinyFont, mediumGray, new PointF(DisplayWidth - 100, footerY)));

        // Dither for 1-bit preview (matches e-ink look)
        using var dithered = DitherImage(image);
        using var ms = new MemoryStream();
        await dithered.SaveAsPngAsync(ms);
        return ms.ToArray();
    }
}
