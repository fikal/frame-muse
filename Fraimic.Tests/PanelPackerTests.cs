using Fraimic.Api;
using Xunit;

namespace Fraimic.Tests;

/// <summary>
/// Locks down the hardware-verified panel folds. The golden hashes were captured from the build that
/// was verified pixel-perfect on a real 31.5" frame — any change to these folds shows up here first.
/// </summary>
public class PanelPackerTests
{
    [Fact]
    public void PackLarge_MatchesGoldenHash()
    {
        byte[] codes = TestData.CodeGrid(1440 * 2560);
        byte[] bin = PanelPacker.PackLarge(codes);

        Assert.Equal(2_304_000, bin.Length);
        Assert.Equal("8f2c3e85827212d16894f552a4b624714dcdb69832dcb6c44b6c88cf1e091bc6", TestData.Sha256(bin));
    }

    [Fact]
    public void PackLarge_PadsOverscanWithWhite()
    {
        byte[] bin = PanelPacker.PackLarge(TestData.CodeGrid(1440 * 2560));

        // Chunk 3 of each half carries only 160 real pixels (80 bytes) per 400-byte gate row;
        // the remaining 320 bytes must be the 0x11 white pad.
        foreach (int chunk3Start in new[] { 3 * 720 * 400, (4 + 3) * 720 * 400 })
            foreach (int gate in new[] { 0, 359, 719 })
            {
                int row = chunk3Start + gate * 400;
                for (int b = 80; b < 400; b++)
                    Assert.Equal(0x11, bin[row + b]);
            }
    }

    [Fact]
    public void PackLarge_RejectsWrongSize() =>
        Assert.Throws<ArgumentException>(() => PanelPacker.PackLarge(new byte[100]));

    [Fact]
    public void PackStandard_MatchesGoldenHash()
    {
        byte[] codes = TestData.CodeGrid(1200 * 1600, seed: 999);
        byte[] bin = PanelPacker.PackStandard(codes);

        Assert.Equal(960_000, bin.Length);
        Assert.Equal("41d623bebe24d257f6411bff9ad33391b7f403d2e2cb1fe2c85e234d7cad5530", TestData.Sha256(bin));
    }

    [Fact]
    public void PackStandard_SplitsEachRowIntoHalves()
    {
        byte[] codes = TestData.CodeGrid(1200 * 1600, seed: 7);
        byte[] bin = PanelPacker.PackStandard(codes);

        // Left half of row 0 packs forward from 0; right half packs forward from the midpoint.
        Assert.Equal((codes[0] << 4) | codes[1], bin[0]);
        Assert.Equal((codes[600] << 4) | codes[601], bin[480_000]);
    }

    [Fact]
    public void PackStandard_RejectsWrongSize() =>
        Assert.Throws<ArgumentException>(() => PanelPacker.PackStandard(new byte[100]));
}
