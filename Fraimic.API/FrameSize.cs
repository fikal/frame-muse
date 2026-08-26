namespace Fraimic.Api;

/// <summary>
/// A Fraimic frame's pixel resolution. The .bin is row-major at this Width x Height, so the
/// dimensions must match what the target frame's firmware expects (in the panel's native
/// orientation, which is landscape for current models).
/// </summary>
public sealed record FrameSize(int Width, int Height, int ByteSize, string Id)
{
    /// <summary>
    /// Standard Canvas (13.3"): portrait-native 1200x1600, 960,000-byte .bin. Packed by the
    /// left/right-half interleave (<see cref="PanelPacker.PackStandard"/>).
    /// </summary>
    public static readonly FrameSize StandardCanvas = new(1200, 1600, 960_000, "133");

    /// <summary>
    /// Large Canvas (24x36", 31.5" GDEP315C01 panel): portrait-native 1440x2560, 2,304,000-byte .bin.
    /// Packed by the panel's dual-half / 4-chunk / 720-gate fold (<see cref="PanelPacker.PackLarge"/>),
    /// hardware-verified against fw 0.2.29. Both frames' layouts follow the field-tested tapframe encoder
    /// (github.com/dpellerin/tapframe, MIT) — see fraimic-bin-format.md.
    /// </summary>
    public static readonly FrameSize LargeCanvas = new(1440, 2560, 2_304_000, "315");

    /// <summary>Parse "WxH" and match it (in either orientation) to a known panel.</summary>
    public static FrameSize Parse(string text)
    {
        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h) || w <= 0 || h <= 0)
            throw new FormatException($"Invalid frame size '{text}'. Expected WxH, e.g. 1440x2560.");
        return ForSource(w, h);
    }

    /// <summary>Pick the panel whose native resolution matches the given source dimensions (either orientation).</summary>
    public static FrameSize ForSource(int width, int height)
    {
        int shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        if (shortSide == LargeCanvas.Width && longSide == LargeCanvas.Height)
            return LargeCanvas;
        return StandardCanvas;
    }

    public override string ToString() => $"{Width}x{Height}";
}
