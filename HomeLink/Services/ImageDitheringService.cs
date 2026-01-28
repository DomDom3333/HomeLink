using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using HomeLink.Models;

namespace HomeLink.Services;

/// <summary>
/// Service for dithering images and converting to 1-bit format for e-ink displays
/// </summary>
public class ImageDitheringService
{
    private const float DitheringStrength = 1.0f;

    /// <summary>
    /// Applies Floyd-Steinberg dithering to convert grayscale to 1-bit
    /// </summary>
    public Image<L8> DitherImage(Image<L8> image)
    {
        int width = image.Width;
        int height = image.Height;

        // Create error accumulation buffer
        float[] errorBuffer = new float[width * height];

        // Copy image to work with
        Image<L8> working = image.Clone();

        // Floyd-Steinberg dithering
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                L8 pixel = working[x, y];

                // Skip dithering for pure black or pure white pixels to preserve sharp edges
                // and solid areas (like text and QR codes)
                if (pixel.PackedValue == 0 || pixel.PackedValue == 255)
                {
                    working[x, y] = pixel;
                    continue;
                }

                float value = pixel.PackedValue + errorBuffer[idx] * DitheringStrength;

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
    public EInkBitmap ConvertToPacked1Bit(Image<L8> image)
    {
        int width = image.Width;
        int height = image.Height;
        int bytesPerLine = (width + 7) / 8;
        int totalBytes = bytesPerLine * height;

        byte[] packedData = new byte[totalBytes];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                L8 pixel = image[x, y];
                // 0 = black (1 in 1-bit), 255 = white (0 in 1-bit)
                int bit = pixel.PackedValue < 128 ? 1 : 0;

                int byteIdx = y * bytesPerLine + x / 8;
                int bitIdx = 7 - (x % 8); // MSB first

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
}

