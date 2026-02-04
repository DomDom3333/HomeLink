using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using HomeLink.Models;

namespace HomeLink.Services;

/// <summary>
/// Service for dithering images and converting to packed formats for e-ink displays.
/// Note: ConvertToPacked1Bit keeps its name for compatibility, but packs according to levelsCount.
/// </summary>
public class ImageDitheringService
{
    // Floyd-Steinberg coefficients
    private const float Coeff7_16 = 7f / 16f;
    private const float Coeff3_16 = 3f / 16f;
    private const float Coeff5_16 = 5f / 16f;
    private const float Coeff1_16 = 1f / 16f;

    // Auto-tune settings (reasonable defaults for e-paper)
    private const float LowPercent = 0.01f;   // 1%
    private const float HighPercent = 0.99f;  // 99%
    private const int MinStretchRange = 24;   // avoid over-amplifying near-flat images

    /// <summary>
    /// Applies Floyd–Steinberg dithering to convert grayscale to N-level output.
    /// levelsCount:
    ///  2  -> black/white (1-bit)
    ///  4  -> 4 grays (2-bit)
    ///  16 -> 16 grays (4-bit)
    /// Uses per-image auto tone mapping to keep output stable across varying images.
    /// </summary>
    public Image<L8> DitherImage(Image<L8> image, int levelsCount = 4)
    {
        levelsCount = NormalizeLevels(levelsCount);

        int width = image.Width;
        int height = image.Height;

        // Per-image auto tone mapping LUT (0..255 -> 0..255)
        byte[] toneLut = BuildAutoToneLut(image);

        // Work on a clone
        Image<L8> working = image.Clone();

        // Two-row error buffers (memory efficient)
        float[] currentRowError = new float[width];
        float[] nextRowError = new float[width];

        // Serpentine scan reduces directional artifacts
        working.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                bool hasNextRow = y + 1 < height;
                bool serpentine = (y & 1) == 1;

                if (!serpentine)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float value = toneLut[row[x].PackedValue] + currentRowError[x];
                        value = Math.Clamp(value, 0f, 255f);

                        byte quantized = QuantizeEvenLevels(value, levelsCount);
                        float error = value - quantized;

                        row[x] = new L8(quantized);

                        if (x + 1 < width) currentRowError[x + 1] += error * Coeff7_16;

                        if (hasNextRow)
                        {
                            if (x > 0) nextRowError[x - 1] += error * Coeff3_16;
                            nextRowError[x] += error * Coeff5_16;
                            if (x + 1 < width) nextRowError[x + 1] += error * Coeff1_16;
                        }
                    }
                }
                else
                {
                    for (int x = width - 1; x >= 0; x--)
                    {
                        float value = toneLut[row[x].PackedValue] + currentRowError[x];
                        value = Math.Clamp(value, 0f, 255f);

                        byte quantized = QuantizeEvenLevels(value, levelsCount);
                        float error = value - quantized;

                        row[x] = new L8(quantized);

                        if (x - 1 >= 0) currentRowError[x - 1] += error * Coeff7_16;

                        if (hasNextRow)
                        {
                            if (x + 1 < width) nextRowError[x + 1] += error * Coeff3_16;
                            nextRowError[x] += error * Coeff5_16;
                            if (x - 1 >= 0) nextRowError[x - 1] += error * Coeff1_16;
                        }
                    }
                }

                (currentRowError, nextRowError) = (nextRowError, currentRowError);
                Array.Clear(nextRowError);
            }
        });

        return working;
    }

    /// <summary>
    /// Converts an image to packed format based on levelsCount.
    /// Name kept for compatibility.
    /// levelsCount=2  -> 1 bit/pixel (same as before)
    /// levelsCount=4  -> 2 bits/pixel
    /// levelsCount=16 -> 4 bits/pixel
    /// </summary>
    public EInkBitmap ConvertToPacked1Bit(Image<L8> image, int levelsCount = 4)
    {
        levelsCount = NormalizeLevels(levelsCount);

        int width = image.Width;
        int height = image.Height;

        int bpp = BitsPerPixel(levelsCount);
        int bytesPerLine = ((width * bpp) + 7) >> 3;
        int totalBytes = bytesPerLine * height;

        byte[] packedData = new byte[totalBytes];

        // Fast paths for the common modes
        if (bpp == 1)
        {
            Pack1Bpp(image, packedData, width, height, bytesPerLine);
        }
        else if (bpp == 2)
        {
            Pack2Bpp(image, packedData, width, height, bytesPerLine, levelsCount);
        }
        else if (bpp == 4)
        {
            Pack4Bpp(image, packedData, width, height, bytesPerLine, levelsCount);
        }
        else
        {
            // Generic bit packer (rarely needed)
            PackGeneric(image, packedData, width, height, bytesPerLine, levelsCount, bpp);
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
    /// Converts an image to raw grayscale bytes (8 bits per pixel, no dithering).
    /// </summary>
    public EInkBitmap ConvertToGrayscaleBitmap(Image<L8> image)
    {
        int width = image.Width;
        int height = image.Height;
        int bytesPerLine = width;
        int totalBytes = bytesPerLine * height;

        byte[] grayscaleData = new byte[totalBytes];

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                int rowOffset = y * bytesPerLine;

                for (int x = 0; x < width; x++)
                    grayscaleData[rowOffset + x] = row[x].PackedValue;
            }
        });

        return new EInkBitmap
        {
            PackedData = grayscaleData,
            Width = width,
            Height = height,
            BytesPerLine = bytesPerLine
        };
    }

    // -------------------- Auto-tuning + Quantization --------------------

    private static int NormalizeLevels(int levelsCount)
    {
        if (levelsCount < 2) return 2;
        if (levelsCount > 256) return 256; // keep generic; e-paper usually <=16
        return levelsCount;
    }

    private static int BitsPerPixel(int levelsCount)
    {
        // bits needed to encode [0..levelsCount-1]
        int v = levelsCount - 1;
        int bits = 0;
        while (v > 0) { bits++; v >>= 1; }
        return Math.Max(bits, 1);
    }

    private static byte QuantizeEvenLevels(float value, int levelsCount)
    {
        if (levelsCount <= 2)
            return value < 128f ? (byte)0 : (byte)255;

        int maxIdx = levelsCount - 1;

        int idx = (int)MathF.Round(value * maxIdx / 255f);
        idx = Math.Clamp(idx, 0, maxIdx);

        float q = idx * (255f / maxIdx);
        return (byte)MathF.Round(q);
    }

    private static int QuantizedIndexFromByte(byte quantizedValue, int levelsCount)
    {
        if (levelsCount <= 2)
            return quantizedValue < 128 ? 1 : 0; // if you used 0=black,255=white, swap if needed

        int maxIdx = levelsCount - 1;

        // round(quantizedValue * maxIdx / 255)
        int idx = (quantizedValue * maxIdx + 127) / 255;
        return Math.Clamp(idx, 0, maxIdx);
    }

    /// <summary>
    /// Builds a 256-entry tone LUT tuned to the specific image:
    ///  - percentile stretch (1%..99%)
    ///  - mild auto-gamma based on median (clamped), to keep midtones usable on e-paper
    /// </summary>
    private static byte[] BuildAutoToneLut(Image<L8> image)
    {
        int[] hist = new int[256];
        long total = (long)image.Width * image.Height;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < image.Height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    hist[row[x].PackedValue]++;
            }
        });

        int low = FindPercentile(hist, total, LowPercent);
        int high = FindPercentile(hist, total, HighPercent);
        int median = FindPercentile(hist, total, 0.5f);

        // If range is too small or degenerate, don't stretch (avoids noise blow-up)
        if (high <= low || (high - low) < MinStretchRange)
            return IdentityLut();

        var lut = new byte[256];

        // Linear stretch first
        float invRange = 255f / (high - low);
        for (int i = 0; i < 256; i++)
        {
            if (i <= low) lut[i] = 0;
            else if (i >= high) lut[i] = 255;
            else lut[i] = (byte)MathF.Round((i - low) * invRange);
        }

        // Mild auto-gamma so median maps closer to 0.5 after stretch
        float m = (median - low) / (float)(high - low);
        m = Math.Clamp(m, 1e-4f, 1f - 1e-4f);

        float gamma = MathF.Log(0.5f) / MathF.Log(m);
        // Clamp to keep tuning stable (avoid flicker between frames)
        gamma = Math.Clamp(gamma, 0.75f, 1.35f);

        // If gamma is near 1, skip
        if (MathF.Abs(gamma - 1f) < 0.03f)
            return lut;

        for (int i = 0; i < 256; i++)
        {
            float x = lut[i] / 255f;
            float y = MathF.Pow(x, gamma);
            lut[i] = (byte)MathF.Round(y * 255f);
        }

        return lut;
    }

    private static byte[] IdentityLut()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)i;
        return lut;
    }

    private static int FindPercentile(int[] hist, long total, float percentile)
    {
        if (total <= 0) return 0;

        long target = (long)MathF.Round(percentile * (total - 1));
        long cum = 0;

        for (int i = 0; i < 256; i++)
        {
            cum += hist[i];
            if (cum > target) return i;
        }

        return 255;
    }

    // -------------------- Packing implementations --------------------

    private static void Pack1Bpp(Image<L8> image, byte[] packedData, int width, int height, int bytesPerLine)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                int rowOffset = y * bytesPerLine;

                int fullBytes = width >> 3;
                int remaining = width & 7;

                for (int byteIndex = 0; byteIndex < fullBytes; byteIndex++)
                {
                    int pixelStart = byteIndex << 3;
                    byte packedByte = 0;

                    // Treat <128 as black bit=1 (same as your original)
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

                if (remaining > 0)
                {
                    int pixelStart = fullBytes << 3;
                    byte packedByte = 0;

                    for (int bit = 0; bit < remaining; bit++)
                        if (row[pixelStart + bit].PackedValue < 128)
                            packedByte |= (byte)(0x80 >> bit);

                    packedData[rowOffset + fullBytes] = packedByte;
                }
            }
        });
    }

    private static void Pack2Bpp(Image<L8> image, byte[] packedData, int width, int height, int bytesPerLine, int levelsCount)
    {
        // 4 pixels per byte: [7:6][5:4][3:2][1:0]
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                int rowOffset = y * bytesPerLine;

                // Reset x for each row - calculate from byte index
                for (int bi = 0; bi < bytesPerLine; bi++)
                {
                    int x = bi * 4; // 4 pixels per byte
                    byte b = 0;

                    for (int i = 0; i < 4 && x < width; i++, x++)
                    {
                        int idx = QuantizedIndexFromByte(row[x].PackedValue, levelsCount);
                        int shift = 6 - (i * 2);
                        b |= (byte)((idx & 0x03) << shift);
                    }

                    packedData[rowOffset + bi] = b;
                }
            }
        });
    }

    private static void Pack4Bpp(Image<L8> image, byte[] packedData, int width, int height, int bytesPerLine, int levelsCount)
    {
        // 2 pixels per byte: high nibble then low nibble
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                int rowOffset = y * bytesPerLine;

                for (int bi = 0; bi < bytesPerLine; bi++)
                {
                    int x = bi * 2; // 2 pixels per byte
                    int idx0 = 0, idx1 = 0;

                    if (x < width)
                        idx0 = QuantizedIndexFromByte(row[x].PackedValue, levelsCount);
                    if (x + 1 < width)
                        idx1 = QuantizedIndexFromByte(row[x + 1].PackedValue, levelsCount);

                    packedData[rowOffset + bi] = (byte)(((idx0 & 0x0F) << 4) | (idx1 & 0x0F));
                }
            }
        });
    }

    private static void PackGeneric(Image<L8> image, byte[] packedData, int width, int height, int bytesPerLine, int levelsCount, int bpp)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<L8> row = accessor.GetRowSpan(y);
                int rowOffset = y * bytesPerLine;

                // Clear line
                packedData.AsSpan(rowOffset, bytesPerLine).Clear();

                for (int x = 0; x < width; x++)
                {
                    int idx = QuantizedIndexFromByte(row[x].PackedValue, levelsCount);

                    // Write idx as bpp bits, MSB-first, contiguous
                    int bitBase = x * bpp;
                    for (int i = 0; i < bpp; i++)
                    {
                        int bit = (idx >> (bpp - 1 - i)) & 1;
                        int outBit = bitBase + i;
                        int outByte = outBit >> 3;
                        int outOff = outBit & 7;
                        int maskBit = 7 - outOff;

                        if (bit == 1)
                            packedData[rowOffset + outByte] |= (byte)(1 << maskBit);
                    }
                }
            }
        });
    }
}
