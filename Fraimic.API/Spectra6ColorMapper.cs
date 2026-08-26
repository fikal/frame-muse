using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fraimic.Api;

/// <summary>
/// Color step of the conversion: maps a fitted RGB image onto the panel's six pigments, producing one
/// 4-bit device code per pixel. This is a faithful C# port of Fraimic's own published converter
/// (github.com/Fraimic/fraimic_bin_converter): enhance (brightness/contrast/saturation, then
/// edge-enhance/smooth/sharpen), then a perceptually-weighted nearest-color match with a custom
/// Atkinson error diffusion — same algorithm and constants as the vendor tool, validated against it
/// (histograms match within &lt;1% per color on identical inputs).
/// </summary>
internal static class Spectra6ColorMapper
{
    // Fraimic's published anchor colors (pure RGB) + our Spectra 6 upload codes, in the same order:
    // black, white, yellow, red, blue, green. (fraimic_bin_converter PALETTE_COLORS.)
    private static readonly (int R, int G, int B)[] PaletteColors =
    {
        (0, 0, 0), (255, 255, 255), (255, 255, 0), (255, 0, 0), (0, 0, 255), (0, 255, 0),
    };
    private static readonly byte[] PaletteCodes = { 0x0, 0x1, 0x2, 0x3, 0x5, 0x6 };
    // Per-anchor luma (R*250+G*350+B*400)/(255*1000), the weighting used by the distance metric.
    private static readonly double[] PaletteLuma = BuildPaletteLuma();

    // PIL ImageFilter kernels (3x3), applied in order after the brightness/contrast/saturation enhance.
    private static readonly int[] EdgeEnhanceKernel = { -1, -1, -1, -1, 10, -1, -1, -1, -1 }; // scale 2
    private static readonly int[] SmoothKernel = { 1, 1, 1, 1, 5, 1, 1, 1, 1 };               // scale 13
    private static readonly int[] SharpenKernel = { -2, -2, -2, -2, 32, -2, -2, -2, -2 };     // scale 16

    private static double[] BuildPaletteLuma()
    {
        var a = new double[PaletteColors.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = (PaletteColors[i].R * 250.0 + PaletteColors[i].G * 350.0 + PaletteColors[i].B * 400.0) / (255.0 * 1000.0);
        return a;
    }

    /// <summary>
    /// Convert a fitted image to one Spectra 6 device code per pixel (row-major). brightnessBoost (&gt;1)
    /// pre-lifts the image so warm colors read brighter on the reflective, gamut-limited e-ink
    /// (bright orange has no pigment, only dark red); 1.0 = the vendor-exact pipeline.
    /// </summary>
    public static byte[] MapToDeviceCodes(Image<Rgb24> image, int w, int h, float brightnessBoost = 1.0f)
    {
        // Pull to an interleaved RGB byte buffer.
        byte[] rgb = new byte[w * h * 3];
        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<Rgb24> row = acc.GetRowSpan(y);
                int bi = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    Rgb24 p = row[x];
                    int o = bi + x * 3;
                    rgb[o] = p.R; rgb[o + 1] = p.G; rgb[o + 2] = p.B;
                }
            }
        });

        // 1) Enhancement, matching PIL's ImageEnhance order + operators (each step clamps to 0..255).
        EnhanceBrightness(rgb, 1.1 * brightnessBoost);
        EnhanceContrast(rgb, 1.2);
        EnhanceSaturation(rgb, 1.2);
        rgb = Convolve3x3(rgb, w, h, EdgeEnhanceKernel, 2, 0);
        rgb = Convolve3x3(rgb, w, h, SmoothKernel, 13, 0);
        rgb = Convolve3x3(rgb, w, h, SharpenKernel, 16, 0);

        // 2) Perceptual 6-color quantize + custom Atkinson (diffuse to right, below-left, below, below-right).
        float[] r = new float[w * h], g = new float[w * h], b = new float[w * h];
        for (int i = 0; i < w * h; i++) { int o = i * 3; r[i] = rgb[o]; g[i] = rgb[o + 1]; b[i] = rgb[o + 2]; }

        byte[] codes = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int best = ClosestPalette(r[i], g[i], b[i]);
                codes[i] = PaletteCodes[best];
                float er = r[i] - PaletteColors[best].R, eg = g[i] - PaletteColors[best].G, eb = b[i] - PaletteColors[best].B;

                if (x + 1 < w) Diffuse(r, g, b, i + 1, er, eg, eb, 0.125f);          // right: 1/8
                if (y + 1 < h)
                {
                    int below = i + w;
                    if (x > 0) Diffuse(r, g, b, below - 1, er, eg, eb, 0.125f);      // below-left: 1/8
                    Diffuse(r, g, b, below, er, eg, eb, 0.25f);                      // below: 1/4
                    if (x + 1 < w) Diffuse(r, g, b, below + 1, er, eg, eb, 0.125f);  // below-right: 1/8
                }
            }
        }
        return codes;
    }

    /// <summary>Nearest palette index by Fraimic's metric: weighted-RGB + a luma term.</summary>
    private static int ClosestPalette(float R, float G, float B)
    {
        double lumaP = (R * 250.0 + G * 350.0 + B * 400.0) / (255.0 * 1000.0);
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < PaletteColors.Length; k++)
        {
            double dr = R - PaletteColors[k].R, dg = G - PaletteColors[k].G, db = B - PaletteColors[k].B;
            double rgbDist = (dr * dr * 0.250 + dg * dg * 0.350 + db * db * 0.400) * 0.75 / (255.0 * 255.0);
            double ld = lumaP - PaletteLuma[k];
            double total = 1.5 * rgbDist + 0.60 * (ld * ld);
            if (total < bd) { bd = total; best = k; }
        }
        return best;
    }

    private static void Diffuse(float[] r, float[] g, float[] b, int i, float er, float eg, float eb, float wt)
    {
        r[i] += er * wt; g[i] += eg * wt; b[i] += eb * wt; // float working buffer, unclamped (as numpy)
    }

    // --- PIL-faithful enhancement helpers (operate in place on an interleaved RGB byte buffer) ---

    /// <summary>ITU-R 601 luma as PIL's convert("L") computes it (fixed point).</summary>
    private static int Lum(int r, int g, int b) => (r * 19595 + g * 38470 + b * 7471 + 0x8000) >> 16;

    private static byte ClampTrunc(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(int)v;

    private static void EnhanceBrightness(byte[] rgb, double f)
    {
        for (int i = 0; i < rgb.Length; i++) rgb[i] = ClampTrunc(rgb[i] * f);
    }

    private static void EnhanceContrast(byte[] rgb, double f)
    {
        long sum = 0; int n = rgb.Length / 3;
        for (int i = 0; i < n; i++) { int o = i * 3; sum += Lum(rgb[o], rgb[o + 1], rgb[o + 2]); }
        int mean = (int)((double)sum / n + 0.5); // PIL: int(L-mean + 0.5), the contrast pivot
        for (int i = 0; i < rgb.Length; i++) rgb[i] = ClampTrunc(mean + f * (rgb[i] - mean));
    }

    private static void EnhanceSaturation(byte[] rgb, double f)
    {
        int n = rgb.Length / 3;
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            int gray = Lum(rgb[o], rgb[o + 1], rgb[o + 2]); // per-pixel gray = PIL degenerate image
            rgb[o] = ClampTrunc(gray + f * (rgb[o] - gray));
            rgb[o + 1] = ClampTrunc(gray + f * (rgb[o + 1] - gray));
            rgb[o + 2] = ClampTrunc(gray + f * (rgb[o + 2] - gray));
        }
    }

    /// <summary>3x3 convolution matching PIL's ImageFilter.Kernel: out = sum/scale + offset; the 1-px
    /// border is copied from the source unchanged.</summary>
    private static byte[] Convolve3x3(byte[] src, int w, int h, int[] k, int scale, int offset)
    {
        byte[] dst = (byte[])src.Clone();
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int o = (y * w + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    int s = k[0] * src[((y - 1) * w + (x - 1)) * 3 + c] + k[1] * src[((y - 1) * w + x) * 3 + c] + k[2] * src[((y - 1) * w + (x + 1)) * 3 + c]
                          + k[3] * src[(y * w + (x - 1)) * 3 + c] + k[4] * src[(y * w + x) * 3 + c] + k[5] * src[(y * w + (x + 1)) * 3 + c]
                          + k[6] * src[((y + 1) * w + (x - 1)) * 3 + c] + k[7] * src[((y + 1) * w + x) * 3 + c] + k[8] * src[((y + 1) * w + (x + 1)) * 3 + c];
                    double v = (double)s / scale + offset;
                    dst[o + c] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(int)(v + 0.5);
                }
            }
        }
        return dst;
    }
}
