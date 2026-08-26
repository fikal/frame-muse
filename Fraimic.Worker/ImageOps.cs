using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Fraimic.Worker;

/// <summary>Image post-processing and web-preview helpers used by the pipeline.</summary>
internal static class ImageOps
{
    /// <summary>Turn an image into genuine chunky pixel art: average it down to ~blocksWide cells, then
    /// blow it back up with hard square pixels (nearest-neighbor). Flux won't do real low-res pixel art,
    /// so this guarantees the retro look for the "Pixel Art" style.</summary>
    public static byte[] Pixelate(byte[] imageBytes, int blocksWide)
    {
        using var img = Image.Load<Rgb24>(imageBytes);
        int w = img.Width, h = img.Height;
        int smallW = Math.Clamp(blocksWide, 16, w);
        int smallH = Math.Max(16, (int)Math.Round((double)smallW * h / w));
        img.Mutate(c => c
            .Resize(smallW, smallH, KnownResamplers.Box)          // average down → clean flat blocks
            .Resize(w, h, KnownResamplers.NearestNeighbor));      // scale up → hard square pixels
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    /// <summary>A small center-cropped JPEG data-URI (portrait) for the phone's status + gallery.</summary>
    public static string MakeThumbnail(byte[] imageBytes)
    {
        using var img = Image.Load<Rgb24>(imageBytes);
        img.Mutate(c => c.Resize(new ResizeOptions
        {
            Size = new Size(360, 640),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
        }));
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 78 });
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Full-resolution JPEG (base64, no prefix) of the image, retained for re-sending from the gallery.</summary>
    public static string MakeFullJpeg(byte[] imageBytes)
    {
        using var img = Image.Load<Rgb24>(imageBytes);
        if (img.Width > 896) img.Mutate(c => c.Resize(896, 0)); // cap to keep the job doc reasonable
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 90 });
        return Convert.ToBase64String(ms.ToArray());
    }
}
