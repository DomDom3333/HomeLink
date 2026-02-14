using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using QRCoder;

namespace HomeLink.Services;

/// <summary>
/// Service for generating and drawing QR codes
/// </summary>
public class QrCodeService
{
    private readonly ILogger<QrCodeService> _logger;
    private readonly FontFamily _fontFamily;
    private readonly DrawingOptions _noAaOptions;

    public QrCodeService(ILogger<QrCodeService> logger, FontFamily fontFamily, DrawingOptions noAaOptions)
    {
        _logger = logger;
        _fontFamily = fontFamily;
        _noAaOptions = noAaOptions;
    }

    /// <summary>
    /// Generates and draws a QR code for the given data onto an image
    /// </summary>
    public void DrawQrCode(Image<L8> image, string data, int x, int y, int size)
    {
        try
        {
            using QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
            using PngByteQRCode qrCode = new(qrCodeData);
            byte[] qrBytes = qrCode.GetGraphic(20);

            using Image<L8> qrImage = Image.Load<L8>(qrBytes);
            qrImage.Mutate(ctx => ctx.Resize(size, size, KnownResamplers.NearestNeighbor));

            image.Mutate(ctx => ctx.DrawImage(qrImage, new Point(x, y), 1f));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate QR code");
            // Draw placeholder on error
            Font font = _fontFamily.CreateFont(12);
            DrawPlaceholder(image, x, y, size, "QR Error", font, new Color(new Rgba32(180, 180, 180)));
        }
    }

    /// <summary>
    /// Draws a placeholder box with text (used when QR generation fails)
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
}

