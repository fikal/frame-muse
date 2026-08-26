using Fraimic.Worker;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Fraimic.Tests;

public class StyleCatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-style")]
    public void Get_FallsBackToAuto(string? key) =>
        Assert.Equal("auto", StyleCatalog.Get(key).Key);

    [Theory]
    [InlineData("pixel")]
    [InlineData("PIXEL")]
    [InlineData(" pixel ")]
    public void Pixelate_IsCaseAndWhitespaceInsensitive(string key) =>
        Assert.True(StyleCatalog.Pixelate(key));

    [Fact]
    public void OnlyPixelStyleTriggersPixelation() =>
        Assert.Equal(["pixel"], StyleCatalog.All.Where(s => s.Pixelate).Select(s => s.Key));

    [Fact]
    public void EveryStyleHasCueWords() =>
        Assert.All(StyleCatalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Suffix)));
}

public class ImageOpsTests
{
    [Fact]
    public void Pixelate_ProducesUniformSquareBlocks()
    {
        // 64x64 gradient at blocksWide=32 -> logical 32x32 grid scaled back up = 2x2 hard blocks.
        using var src = TestData.GradientImage(64, 64);
        using var ms = new MemoryStream();
        src.Save(ms, new PngEncoder());

        byte[] outBytes = ImageOps.Pixelate(ms.ToArray(), 32);

        using var result = Image.Load<Rgb24>(outBytes);
        Assert.Equal(64, result.Width);
        Assert.Equal(64, result.Height);
        for (int by = 0; by < 64; by += 2)
            for (int bx = 0; bx < 64; bx += 2)
            {
                Rgb24 c = result[bx, by];
                Assert.Equal(c, result[bx + 1, by]);
                Assert.Equal(c, result[bx, by + 1]);
                Assert.Equal(c, result[bx + 1, by + 1]);
            }
    }

    [Fact]
    public void MakeFullJpeg_CapsWidth()
    {
        using var src = TestData.GradientImage(1440, 2560);
        using var ms = new MemoryStream();
        src.Save(ms, new PngEncoder());

        string b64 = ImageOps.MakeFullJpeg(ms.ToArray());

        using var result = Image.Load<Rgb24>(Convert.FromBase64String(b64));
        Assert.Equal(896, result.Width);
    }
}
