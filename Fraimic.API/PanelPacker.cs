namespace Fraimic.Api;

/// <summary>
/// Packing step of the conversion: folds a portrait device-code grid (one 4-bit code per pixel,
/// row-major) into the frame's on-wire .bin layout, 2 pixels/byte with the high nibble first.
/// Both folds are ported verbatim from the field-tested tapframe encoder
/// (github.com/dpellerin/tapframe, MIT) and are hardware-verified — see fraimic-bin-format.md.
/// </summary>
public static class PanelPacker
{
    /// <summary>
    /// Pack the 1440x2560 portrait device-code grid into the 31.5" frame's 2,304,000-byte .bin.
    /// The panel is driven as two horizontal halves, each split into 4 source-chunks of 720 gate lines;
    /// every 400-byte gate row is white-padded then filled with the chunk's real pixels (tapframe packEl315).
    /// </summary>
    public static byte[] PackLarge(byte[] codes)
    {
        const int width = 1440, height = 2560, binSize = 2_304_000;
        if (codes.Length != width * height)
            throw new ArgumentException($"Large frame expects {width * height:N0} codes, got {codes.Length:N0}.", nameof(codes));

        // The firmware reads rows bottom-to-top, so flip vertically first.
        byte[] flipped = new byte[codes.Length];
        for (int y = 0; y < height; y++)
            Array.Copy(codes, (height - 1 - y) * width, flipped, y * width, width);

        byte[] output = new byte[binSize];
        int offset = 0;
        for (int half = 0; half < 2; half++)
        {
            int stripStart = half * 1280;
            for (int chunk = 0; chunk < 4; chunk++)
            {
                int realPixels = chunk == 3 ? 160 : 800; // last chunk is mostly overscan
                int start = chunk * 800;
                for (int gate = 0; gate < 720; gate++)
                {
                    Array.Fill(output, (byte)0x11, offset, 400); // white pad
                    for (int p = 0; p < realPixels; p += 2)
                    {
                        int q0 = start + p, q1 = start + p + 1;
                        byte c0 = flipped[(stripStart + q0 / 2) * width + (gate * 2 + (q0 & 1))];
                        byte c1 = flipped[(stripStart + q1 / 2) * width + (gate * 2 + (q1 & 1))];
                        output[offset + p / 2] = (byte)((c0 << 4) | c1);
                    }
                    offset += 400;
                }
            }
        }
        return output;
    }

    /// <summary>
    /// Pack the 1200x1600 portrait device-code grid into the 13.3" frame's 960,000-byte .bin: each row's
    /// left half writes forward from the start, the right half from the midpoint (tapframe packEl133).
    /// </summary>
    public static byte[] PackStandard(byte[] codes)
    {
        const int width = 1200, height = 1600, binSize = 960_000;
        if (codes.Length != width * height)
            throw new ArgumentException($"Standard frame expects {width * height:N0} codes, got {codes.Length:N0}.", nameof(codes));

        int halfCols = width / 2, halfSize = binSize / 2;
        byte[] output = new byte[binSize];
        int left = 0, right = halfSize;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < halfCols; x += 2)
                output[left++] = (byte)((codes[row + x] << 4) | codes[row + x + 1]);
            for (int x = halfCols; x < width; x += 2)
                output[right++] = (byte)((codes[row + x] << 4) | codes[row + x + 1]);
        }
        return output;
    }
}
