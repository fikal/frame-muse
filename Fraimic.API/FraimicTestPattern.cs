using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fraimic.Api;

/// <summary>
/// Generates a synthetic orientation/scale test card sized to a frame, for verifying a frame's .bin
/// orientation on real hardware. Each corner is a distinct Spectra color and an arrow points up, so
/// any rotation, mirroring, or aspect squish is immediately obvious on the panel.
///
/// Corners: top-left = Red, top-right = Green, bottom-left = Blue, bottom-right = Yellow.
/// A correctly-oriented frame shows Red in the upper-left with the arrow pointing toward the top.
/// </summary>
public static class FraimicTestPattern
{
    private static readonly Rgb24 White = new(255, 255, 255);
    private static readonly Rgb24 Black = new(0, 0, 0);
    private static readonly Rgb24 Red = new(255, 0, 0);
    private static readonly Rgb24 Green = new(0, 255, 0);
    private static readonly Rgb24 Blue = new(0, 0, 255);
    private static readonly Rgb24 Yellow = new(255, 255, 0);

    public static Image<Rgb24> Generate(FrameSize size)
    {
        int w = size.Width, h = size.Height;
        int cw = w / 4, ch = h / 4;           // corner block size
        int border = Math.Max(4, w / 200);    // outer frame thickness

        var img = new Image<Rgb24>(w, h, White);
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<Rgb24> row = acc.GetRowSpan(y);
                bool topRows = y < ch, bottomRows = y >= h - ch;
                bool edgeRow = y < border || y >= h - border;
                for (int x = 0; x < w; x++)
                {
                    if (topRows && x < cw) row[x] = Red;
                    else if (topRows && x >= w - cw) row[x] = Green;
                    else if (bottomRows && x < cw) row[x] = Blue;
                    else if (bottomRows && x >= w - cw) row[x] = Yellow;
                    else if (edgeRow || x < border || x >= w - border) row[x] = Black;
                }
            }
        });

        DrawUpArrow(img, w, h);
        return img;
    }

    /// <summary>
    /// Convenience: generate the card and pack it straight to a .bin for the frame, drawn upright
    /// for the given hanging orientation. If, when hung, the arrow points up and Red is top-left,
    /// that orientation is correct for real images.
    /// </summary>
    public static byte[] GenerateBin(FrameSize size, FrameOrientation orientation = FrameOrientation.Landscape)
    {
        // The panel grid is portrait-native; draw the card at those dimensions and pack it directly
        // (no orientation rotation, so Red stays top-left / arrow up in the portrait hang).
        using Image<Rgb24> img = Generate(size);
        return FraimicConverter.Convert(img, size, FitMode.Stretch, FrameOrientation.Landscape);
    }

    /// <summary>Solid black triangle pointing up, centered.</summary>
    private static void DrawUpArrow(Image<Rgb24> img, int w, int h)
    {
        int cx = w / 2;
        int top = h / 2 - h / 8;        // tip
        int bottom = h / 2 + h / 8;     // base
        int halfBase = h / 8;
        img.ProcessPixelRows(acc =>
        {
            for (int y = top; y < bottom; y++)
            {
                if (y < 0 || y >= h) continue;
                float t = (float)(y - top) / (bottom - top); // 0 at tip → 1 at base
                int hw = (int)(t * halfBase);
                Span<Rgb24> row = acc.GetRowSpan(y);
                for (int x = cx - hw; x <= cx + hw; x++)
                    if (x >= 0 && x < w) row[x] = Black;
            }
        });
    }
}
