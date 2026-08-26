using Fraimic.Api;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Fraimic.Tests;

/// <summary>
/// Locks down the color pipeline (the validated port of Fraimic's published converter) and the
/// end-to-end conversion. Golden hashes were captured from the build whose output was verified
/// against the official tool (per-color histograms within &lt;1%).
/// </summary>
public class ColorPipelineTests
{
    [Fact]
    public void MapToDeviceCodes_MatchesGoldenHash()
    {
        using var img = TestData.GradientImage(256, 256);
        byte[] codes = Spectra6ColorMapper.MapToDeviceCodes(img, 256, 256);

        Assert.Equal(256 * 256, codes.Length);
        Assert.All(codes, c => Assert.Contains(c, TestData.ValidCodes));
        Assert.Equal("123c2538f4cd63b645079b016d3b8b5df3da73e7d4fd8d37466cb428d5104e8f", TestData.Sha256(codes));
    }

    [Theory]
    [InlineData(255, 0, 0, 0x3)]      // pure red -> red pigment
    [InlineData(255, 255, 255, 0x1)]  // pure white -> white pigment
    [InlineData(0, 0, 0, 0x0)]        // pure black -> black pigment
    public void MapToDeviceCodes_SolidPrimariesStayPure(byte r, byte g, byte b, byte expectedCode)
    {
        // A uniform primary passes through the enhancement chain unchanged (brightness/contrast/
        // saturation are no-ops at the extremes; convolutions are identity on uniform input),
        // so every pixel must quantize to that pigment with zero dither error.
        using var img = new SixLabors.ImageSharp.Image<Rgb24>(32, 32, new Rgb24(r, g, b));
        byte[] codes = Spectra6ColorMapper.MapToDeviceCodes(img, 32, 32);

        Assert.All(codes, c => Assert.Equal(expectedCode, c));
    }

    [Fact]
    public void Convert_LargeFrame_MatchesGoldenHash()
    {
        // Source already at panel size, so the geometry step is a stable pass-through.
        using var img = TestData.GradientImage(1440, 2560);
        byte[] bin = FraimicConverter.Convert(img, FrameSize.LargeCanvas);

        Assert.Equal(FrameSize.LargeCanvas.ByteSize, bin.Length);
        Assert.Equal("382ab591f5a1ec468f035c43c792698c468392f4f394047039deda5232549d05", TestData.Sha256(bin));
    }

    [Fact]
    public void Convert_StandardFrame_ProducesExactByteSize()
    {
        using var img = TestData.GradientImage(1200, 1600);
        byte[] bin = FraimicConverter.Convert(img, FrameSize.StandardCanvas);

        Assert.Equal(FrameSize.StandardCanvas.ByteSize, bin.Length);
    }

    [Fact]
    public void Convert_BrightnessBoost_ChangesOutput()
    {
        using var img1 = TestData.GradientImage(1200, 1600);
        using var img2 = TestData.GradientImage(1200, 1600);
        byte[] plain = FraimicConverter.Convert(img1, FrameSize.StandardCanvas);
        byte[] boosted = FraimicConverter.Convert(img2, FrameSize.StandardCanvas, brightnessBoost: 1.3f);

        Assert.NotEqual(TestData.Sha256(plain), TestData.Sha256(boosted));
    }
}

public class FrameSizeTests
{
    [Theory]
    [InlineData("1440x2560")]
    [InlineData("2560x1440")]  // either orientation matches
    [InlineData("2560X1440")]
    public void Parse_MatchesLargePanel(string text) =>
        Assert.Same(FrameSize.LargeCanvas, FrameSize.Parse(text));

    [Fact]
    public void Parse_DefaultsToStandardForOtherSizes() =>
        Assert.Same(FrameSize.StandardCanvas, FrameSize.Parse("1200x1600"));

    [Theory]
    [InlineData("bogus")]
    [InlineData("100")]
    [InlineData("0x100")]
    public void Parse_RejectsInvalidText(string text) =>
        Assert.Throws<FormatException>(() => FrameSize.Parse(text));
}
