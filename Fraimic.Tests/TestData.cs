using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fraimic.Tests;

/// <summary>Deterministic inputs + hashing so golden-file tests are reproducible everywhere.</summary>
internal static class TestData
{
    /// <summary>Lowercase hex SHA-256 of a byte array.</summary>
    public static string Sha256(byte[] data) => System.Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The six valid Spectra 6 device codes (0x4 is unused by the panel).</summary>
    public static readonly byte[] ValidCodes = { 0x0, 0x1, 0x2, 0x3, 0x5, 0x6 };

    /// <summary>Deterministic pseudo-random device-code grid (xorshift; no Random, no time).</summary>
    public static byte[] CodeGrid(int count, uint seed = 12345)
    {
        var codes = new byte[count];
        uint s = seed;
        for (int i = 0; i < count; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            codes[i] = ValidCodes[s % 6];
        }
        return codes;
    }

    /// <summary>Deterministic full-color gradient image (r=x, g=y, b=x+y — every channel exercised).</summary>
    public static Image<Rgb24> GradientImage(int width, int height)
    {
        var img = new Image<Rgb24>(width, height);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgb24> row = acc.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    row[x] = new Rgb24((byte)(x & 255), (byte)(y & 255), (byte)((x + y) & 255));
            }
        });
        return img;
    }
}
