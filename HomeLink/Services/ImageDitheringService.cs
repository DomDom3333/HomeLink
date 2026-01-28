using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using HomeLink.Models;

namespace HomeLink.Services;

/// <summary>
/// Service for dithering images and converting to 1-bit format for e-ink displays
/// </summary>
public class ImageDitheringService
{
    // Pre-computed Floyd-Steinberg coefficients
    private const float Coeff7_16 = 7f / 16f;
    private const float Coeff3_16 = 3f / 16f;
    private const float Coeff5_16 = 5f / 16f;
    private const float Coeff1_16 = 1f / 16f;

    /// <summary>
    /// Applies Floyd-Steinberg dithering to convert grayscale to 1-bit
    /// Uses ProcessPixelRows for efficient memory access
    /// </summary>
    public Image<L8> DitherImage(Image<L8> image)
    {
        int width = image.Width;
        int height = image.Height;

        // Copy image to work with
        Image<L8> working = image.Clone();

        // Use two row error buffers instead of full image buffer (saves memory)
        // currentRowError for current row, nextRowError for next row
        float[] currentRowError = new float[width];
        float[] nextRowError = new float[width];

        // Process rows using efficient span-based access
        working.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                bool hasNextRow = y + 1 < height;

                for (int x = 0; x < width; x++)
                {
                    byte pixelValue = row[x].PackedValue;

                    // Skip dithering for pure black or pure white pixels to preserve sharp edges
                    // and solid areas (like text and QR codes)
                    if (pixelValue == 0 || pixelValue == 255)
                    {
                        continue;
                    }

                    // Apply accumulated error
                    float value = pixelValue + currentRowError[x];

                    // Clamp value to valid range
                    value = Math.Clamp(value, 0f, 255f);

                    // Quantize to black or white
                    byte quantized = value < 128f ? (byte)0 : (byte)255;
                    float error = value - quantized;

                    row[x] = new L8(quantized);

                    // Distribute error to neighboring pixels (Floyd-Steinberg pattern)
                    if (x + 1 < width)
                        currentRowError[x + 1] += error * Coeff7_16;

                    if (hasNextRow)
                    {
                        if (x > 0)
                            nextRowError[x - 1] += error * Coeff3_16;
                        nextRowError[x] += error * Coeff5_16;
                        if (x + 1 < width)
                            nextRowError[x + 1] += error * Coeff1_16;
                    }
                }

                // Swap buffers and clear for next iteration
                (currentRowError, nextRowError) = (nextRowError, currentRowError);
                Array.Clear(nextRowError);
            }
        });

        return working;
    }

    /// <summary>
    /// Converts an image to packed 1-bit format using efficient span-based access
    /// </summary>
    public EInkBitmap ConvertToPacked1Bit(Image<L8> image)
    {
        int width = image.Width;
        int height = image.Height;
        int bytesPerLine = (width + 7) >> 3; // Equivalent to / 8
        int totalBytes = bytesPerLine * height;

        byte[] packedData = new byte[totalBytes];

        // Process rows efficiently using span-based access
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                int rowOffset = y * bytesPerLine;

                // Process 8 pixels at a time for efficiency
                int fullBytes = width >> 3; // Number of complete bytes
                int remaining = width & 7;   // Remaining pixels (width % 8)

                for (int byteIndex = 0; byteIndex < fullBytes; byteIndex++)
                {
                    int pixelStart = byteIndex << 3; // byteIndex * 8
                    byte packedByte = 0;

                    // Process 8 pixels into one byte (MSB first)
                    // Unrolled loop for performance
                    if (row[pixelStart].PackedValue < 128) packedByte |= 0b10000000;
                    if (row[pixelStart + 1].PackedValue < 128) packedByte |= 0b01000000;
                    if (row[pixelStart + 2].PackedValue < 128) packedByte |= 0b00100000;
                    if (row[pixelStart + 3].PackedValue < 128) packedByte |= 0b00010000;
                    if (row[pixelStart + 4].PackedValue < 128) packedByte |= 0b00001000;
                    if (row[pixelStart + 5].PackedValue < 128) packedByte |= 0b00000100;
                    if (row[pixelStart + 6].PackedValue < 128) packedByte |= 0b00000010;
                    if (row[pixelStart + 7].PackedValue < 128) packedByte |= 0b00000001;

                    packedData[rowOffset + byteIndex] = packedByte;
                }

                // Handle remaining pixels in the last partial byte
                if (remaining > 0)
                {
                    int pixelStart = fullBytes << 3;
                    byte packedByte = 0;

                    for (int bit = 0; bit < remaining; bit++)
                    {
                        if (row[pixelStart + bit].PackedValue < 128)
                        {
                            packedByte |= (byte)(0x80 >> bit);
                        }
                    }

                    packedData[rowOffset + fullBytes] = packedByte;
                }
            }
        });

        return new EInkBitmap
        {
            PackedData = packedData,
            Width = width,
            Height = height,
            BytesPerLine = bytesPerLine
        };
    }
}

