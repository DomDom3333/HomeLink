using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using HomeLink.Utils;

namespace HomeLink.Services;

/// <summary>
/// Service for fetching and rendering static map tiles from OpenStreetMap
/// </summary>
public class MapTileService
{
    private readonly HttpClient _httpClient;
    private readonly FontFamily _fontFamily;
    private readonly DrawingOptions _noAaOptions;

    public MapTileService(HttpClient httpClient, FontFamily fontFamily, DrawingOptions noAaOptions)
    {
        _httpClient = httpClient;
        _fontFamily = fontFamily;
        _noAaOptions = noAaOptions;
    }

    /// <summary>
    /// Downloads and draws a static map image from OpenStreetMap tiles
    /// </summary>
    public async Task DrawStaticMapAsync(Image<L8> image, double latitude, double longitude, int x, int y, int size)
    {
        try
        {
            // Zoom level: 15 = ~500m view, 16 = ~250m (good for neighborhood)
            int zoom = 16;
            
            // Convert lat/lon to tile coordinates
            (int tileX, int tileY, int pixelOffsetX, int pixelOffsetY) = GeoUtils.LatLonToTile(latitude, longitude, zoom);
            
            // We need to fetch multiple tiles to fill the size (tiles are 256x256)
            const int tileSize = 256;
            int tilesNeeded = (int)Math.Ceiling((double)size / tileSize) + 1;
            
            // Create a temporary image to composite tiles
            using Image<L8> mapComposite = new Image<L8>(tilesNeeded * tileSize, tilesNeeded * tileSize, new L8(240));
            
            // Fetch tiles in a grid around the center tile
            int startTileX = tileX - tilesNeeded / 2;
            int startTileY = tileY - tilesNeeded / 2;
            
            for (int ty = 0; ty < tilesNeeded; ty++)
            {
                for (int tx = 0; tx < tilesNeeded; tx++)
                {
                    int currentTileX = startTileX + tx;
                    int currentTileY = startTileY + ty;
                    
                    try
                    {
                        // Use OSM tile server (be respectful of usage policy)
                        string tileUrl = $"https://tile.openstreetmap.org/{zoom}/{currentTileX}/{currentTileY}.png";
                        
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, tileUrl);
                        request.Headers.Add("User-Agent", "HomeLink/1.0 (E-Ink Display Application)");
                        
                        HttpResponseMessage response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            byte[] tileBytes = await response.Content.ReadAsByteArrayAsync();
                            using Image<L8> tileImage = Image.Load<L8>(tileBytes);
                            
                            // Draw tile onto composite
                            int drawX = tx * tileSize;
                            int drawY = ty * tileSize;
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
            int centerPixelX = (tilesNeeded / 2) * tileSize + pixelOffsetX;
            int centerPixelY = (tilesNeeded / 2) * tileSize + pixelOffsetY;
            int cropX = Math.Max(0, centerPixelX - size / 2);
            int cropY = Math.Max(0, centerPixelY - size / 2);
            
            // Crop and resize to target size
            mapComposite.Mutate(ctx => ctx
                .Crop(new Rectangle(cropX, cropY, Math.Min(size, mapComposite.Width - cropX), Math.Min(size, mapComposite.Height - cropY)))
                .Resize(size, size));
            
            // Draw onto main image
            image.Mutate(ctx => ctx.DrawImage(mapComposite, new Point(x, y), 1f));

            // Draw a center marker (crosshair)
            int centerX = x + size / 2;
            int centerY = y + size / 2;
            int markerSize = 10;
            image.Mutate(ctx =>
            {
                // Outer black circle
                for (int angle = 0; angle < 360; angle += 30)
                {
                    double rad = angle * Math.PI / 180;
                    int px = centerX + (int)(markerSize * Math.Cos(rad));
                    int py = centerY + (int)(markerSize * Math.Sin(rad));
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
    /// Draws a placeholder when map cannot be loaded
    /// </summary>
    public void DrawMapPlaceholder(Image<L8> image, int x, int y, int size, double lat, double lon)
    {
        Color lightGray = new Color(new Rgba32(200, 200, 200));
        Color mediumGray = new Color(new Rgba32(150, 150, 150));
        Font font = _fontFamily.CreateFont(10);

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
                int offset = size * i / 4;
                ctx.DrawLine(_noAaOptions, mediumGray, 1, new PointF(x + offset, y), new PointF(x + offset, y + size));
                ctx.DrawLine(_noAaOptions, mediumGray, 1, new PointF(x, y + offset), new PointF(x + size, y + offset));
            }

            // Draw crosshair at center
            int centerX = x + size / 2;
            int centerY = y + size / 2;
            ctx.DrawLine(_noAaOptions, Color.Black, 2, new PointF(centerX - 10, centerY), new PointF(centerX + 10, centerY));
            ctx.DrawLine(_noAaOptions, Color.Black, 2, new PointF(centerX, centerY - 10), new PointF(centerX, centerY + 10));

            // Draw border
            ctx.DrawPolygon(_noAaOptions, Color.Black, 2,
                new PointF(x, y),
                new PointF(x + size, y),
                new PointF(x + size, y + size),
                new PointF(x, y + size));

            // Draw coordinates at bottom
            string coordText = $"{lat:F3}, {lon:F3}";
            ctx.DrawText(_noAaOptions, coordText, font, Color.Black, new PointF(x + 5, y + size - 15));
        });
    }
}

