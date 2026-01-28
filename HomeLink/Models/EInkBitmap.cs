namespace HomeLink.Models;

/// <summary>
/// Represents a 1-bit packed bitmap suitable for e-ink displays
/// </summary>
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

