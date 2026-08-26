using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Fraimic.Api;

/// <summary>How a source image is fitted to the target frame.</summary>
public enum FitMode
{
    /// <summary>Scale to cover the frame, center-crop the overflow (no distortion, may lose edges).</summary>
    Fill,
    /// <summary>Scale to fit inside the frame, letterbox the remainder with black (whole image visible).</summary>
    Fit,
    /// <summary>Stretch to exactly fill the frame, ignoring aspect ratio (may distort).</summary>
    Stretch,
}

/// <summary>
/// How the frame is physically hung. The panel buffer is always landscape-native; for portrait the
/// image is composed at viewer dimensions then rotated into the native buffer. The API can't report
/// this, so it must be specified. The two portrait directions correspond to rotating the frame either
/// way — use the test card to pick the one whose arrow points up.
/// </summary>
public enum FrameOrientation
{
    /// <summary>Hung landscape (native). No rotation.</summary>
    Landscape,
    /// <summary>Hung portrait, frame turned 90° clockwise. Image is rotated to compensate.</summary>
    PortraitClockwise,
    /// <summary>Hung portrait, frame turned 90° counter-clockwise.</summary>
    PortraitCounterClockwise,
    /// <summary>Hung landscape but upside-down (180°).</summary>
    LandscapeUpsideDown,
}

/// <summary>Geometry step of the conversion: rotate for the hanging orientation, then fit to the panel grid.</summary>
internal static class ImageFitter
{
    /// <summary>Rotate the composed image into the landscape-native buffer for the hanging orientation.</summary>
    public static void ApplyOrientation(Image<Rgb24> image, FrameOrientation orientation)
    {
        RotateMode mode = orientation switch
        {
            FrameOrientation.PortraitClockwise => RotateMode.Rotate90,         // 90° CW
            FrameOrientation.PortraitCounterClockwise => RotateMode.Rotate270, // 90° CCW
            FrameOrientation.LandscapeUpsideDown => RotateMode.Rotate180,
            _ => RotateMode.None,
        };
        if (mode != RotateMode.None)
            image.Mutate(ctx => ctx.Rotate(mode));
    }

    /// <summary>Resize <paramref name="image"/> in place to exactly w x h per the fit mode.</summary>
    public static void ResizeToFrame(Image<Rgb24> image, int w, int h, FitMode fit)
    {
        var options = new ResizeOptions
        {
            Size = new Size(w, h),
            Sampler = KnownResamplers.Lanczos3,
            Mode = fit switch
            {
                FitMode.Fill => ResizeMode.Crop,
                FitMode.Fit => ResizeMode.Pad,
                FitMode.Stretch => ResizeMode.Stretch,
                _ => ResizeMode.Crop,
            },
            Position = AnchorPositionMode.Center,
            PadColor = Color.Black,
        };
        image.Mutate(ctx => ctx.Resize(options));
    }
}
