using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fraimic.Api;

/// <summary>
/// Converts ordinary images (JPEG/PNG/WebP/...) into the raw Fraimic <c>.bin</c> format for a given
/// <see cref="FrameSize"/>. The conversion is three focused steps:
/// <list type="number">
///   <item><see cref="ImageFitter"/> — rotate for the hanging orientation, fit to the panel grid;</item>
///   <item><see cref="Spectra6ColorMapper"/> — enhance + quantize + dither to the 6 pigments
///     (a validated port of Fraimic's own published converter);</item>
///   <item><see cref="PanelPacker"/> — fold the device codes into the panel's on-wire layout.</item>
/// </list>
/// </summary>
public static class FraimicConverter
{
    /// <summary>Convert an image file on disk to a Fraimic .bin byte array for the given frame size.</summary>
    public static byte[] Convert(string imagePath, FrameSize? size = null, FitMode fit = FitMode.Fill, FrameOrientation orientation = FrameOrientation.Landscape, float brightnessBoost = 1.0f)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(imagePath);
        return Convert(image, size, fit, orientation, brightnessBoost);
    }

    /// <summary>Convert an image from a stream to a Fraimic .bin byte array for the given frame size.</summary>
    public static byte[] Convert(Stream imageStream, FrameSize? size = null, FitMode fit = FitMode.Fill, FrameOrientation orientation = FrameOrientation.Landscape, float brightnessBoost = 1.0f)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(imageStream);
        return Convert(image, size, fit, orientation, brightnessBoost);
    }

    /// <summary>
    /// Convert an already-loaded image (mutated in place) to a Fraimic .bin byte array.
    /// <paramref name="brightnessBoost"/> (>1) pre-lifts brightness so warm colors read brighter on the
    /// reflective, gamut-limited e-ink; 1.0 = the vendor-exact color pipeline.
    /// </summary>
    public static byte[] Convert(Image<Rgb24> image, FrameSize? size = null, FitMode fit = FitMode.Fill, FrameOrientation orientation = FrameOrientation.Landscape, float brightnessBoost = 1.0f)
    {
        size ??= FrameSize.StandardCanvas;
        int w = size.Width, h = size.Height; // portrait-native panel dimensions

        ImageFitter.ApplyOrientation(image, orientation);
        ImageFitter.ResizeToFrame(image, w, h, fit);

        byte[] codes = Spectra6ColorMapper.MapToDeviceCodes(image, w, h, brightnessBoost);

        return size.Id == "315" ? PanelPacker.PackLarge(codes) : PanelPacker.PackStandard(codes);
    }
}
