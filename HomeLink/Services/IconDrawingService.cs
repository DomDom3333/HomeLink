using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HomeLink.Services;

/// <summary>
/// Service for drawing UI icons like playback controls, battery indicators, and progress bars
/// </summary>
public class IconDrawingService
{
    private readonly DrawingOptions _noAaOptions;

    public IconDrawingService(DrawingOptions noAaOptions)
    {
        _noAaOptions = noAaOptions;
    }

    /// <summary>
    /// Draws a play or pause icon
    /// </summary>
    public void DrawPlaybackIcon(Image<L8> image, bool isPlaying, int x, int y, int size, Color color)
    {
        image.Mutate(ctx =>
        {
            if (isPlaying)
            {
                // Draw Play Triangle
                PointF[] points =
                [
                    new(x, y),
                    new(x + size, y + size / 2f),
                    new(x, y + size)
                ];
                ctx.FillPolygon(_noAaOptions, color, points);
            }
            else
            {
                // Draw Pause Bars
                float barWidth = size / 3f;
                
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
    public void DrawBatteryIcon(Image<L8> image, int x, int y, int width, int height, int levelPercent, int? batteryStatus, Color outlineColor, Color fillColor)
    {
        // Clamp inputs
        levelPercent = Math.Clamp(levelPercent, 0, 100);
        if (width < 10) width = 10;
        if (height < 8) height = 8;

        // Terminal cap dimensions
        int capWidth = Math.Max(2, width / 8);
        int bodyWidth = width - capWidth - 1;

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
            int capX = x + bodyWidth + 1;
            float capTop = y + height / 3f;
            float capBottom = y + height - height / 3f;
            ctx.FillPolygon(_noAaOptions, outlineColor,
                new PointF(capX, capTop),
                new PointF(capX + capWidth, capTop),
                new PointF(capX + capWidth, capBottom),
                new PointF(capX, capBottom));

            // Fill level inside body (leave 2px padding)
            int innerX = x + 2;
            int innerY = y + 2;
            int innerW = Math.Max(0, bodyWidth - 4);
            int innerH = Math.Max(0, height - 4);
            int fillW = (int)Math.Round(innerW * (levelPercent / 100.0));
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
                float boltCenterX = x + bodyWidth / 2f;
                int boltTop = y + 2;
                int boltBottom = y + height - 2;
                float boltW = Math.Max(2f, bodyWidth * 0.20f);
                float boltMidY = (boltTop + boltBottom) / 2f;
                PointF[] boltPoints =
                [
                    new(boltCenterX - boltW * 0.3f, boltTop),
                    new(boltCenterX + boltW * 0.5f, boltTop),
                    new(boltCenterX - boltW * 0.2f, boltMidY),
                    new(boltCenterX + boltW * 0.4f, boltMidY),
                    new(boltCenterX - boltW * 0.7f, boltBottom),
                    new(boltCenterX - boltW * 0.3f, boltMidY)
                ];
                ctx.FillPolygon(_noAaOptions, Color.Black, boltPoints);
            }
        });
    }

    /// <summary>
    /// Draws a progress bar for track playback
    /// </summary>
    public void DrawProgressBar(Image<L8> image, long progressMs, long totalMs, int x, int y, int width, Color fillColor, Color bgColor)
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
            int progressWidth = (int)((double)progressMs / totalMs * width);
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
}

