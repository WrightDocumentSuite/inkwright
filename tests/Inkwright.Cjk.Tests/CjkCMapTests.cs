using Inkwright.Fonts;
using Xunit;

namespace Inkwright.Cjk.Tests;

public class CjkCMapTests
{
    public CjkCMapTests() => CjkCMaps.Register();

    [Fact]
    public void CarriesEveryMapTableOneEighteenNames()
    {
        Assert.Equal(168, CjkCMaps.Names.Count);

        foreach (string name in new[]
        {
            "90ms-RKSJ-H", "90ms-RKSJ-V", "GBK-EUC-H", "ETen-B5-H", "KSCms-UHC-H",
            "UniJIS-UCS2-H", "UniGB-UCS2-H", "UniCNS-UCS2-H", "UniKS-UCS2-H",
            "UniJIS-UTF16-H", "UniGB-UTF16-H",
            "Adobe-Japan1-UCS2", "Adobe-GB1-UCS2", "Adobe-CNS1-UCS2", "Adobe-Korea1-UCS2",
        })
        {
            Assert.Contains(name, CjkCMaps.Names);
        }
    }

    [Fact]
    public void ReadsEveryMapWithoutFaulting()
    {
        foreach (string name in CjkCMaps.Names)
            Assert.NotNull(CMapProvider.Load(name));
    }

    [Fact]
    public void SplitsShiftJisIntoOneAndTwoByteCodes()
    {
        CMap map = CMapProvider.Load("90ms-RKSJ-H")!;

        // Shift-JIS is a mixed-width encoding: ASCII and half-width katakana take one byte, and the
        // rest take two. Getting this wrong is what turns extracted Japanese into mojibake.
        Assert.Equal('A', (char)map.ReadCode("A"u8, 0, out int consumed));
        Assert.Equal(1, consumed);

        Assert.Equal(0xB1u, map.ReadCode([0xB1], 0, out consumed));
        Assert.Equal(1, consumed);

        Assert.Equal(0x82A0u, map.ReadCode([0x82, 0xA0], 0, out consumed));
        Assert.Equal(2, consumed);
    }

    [Fact]
    public void MapsShiftJisThroughTheCollectionToText()
    {
        CMap encoding = CMapProvider.Load("90ms-RKSJ-H")!;
        CMap collection = CMapProvider.Load("Adobe-Japan1-UCS2")!;

        // ISO 32000-1 9.10.2: the encoding turns a code into a CID, and the character collection's
        // own map turns that CID into text. This map sends single-byte codes to Adobe-Japan1's
        // half-width roman set, which starts at CID 231 for the space.
        Assert.Equal(231 + 'A' - ' ', encoding.GetCid('A'));
        Assert.Equal("A", collection.GetText((uint)encoding.GetCid('A')));
        Assert.Equal("あ", collection.GetText((uint)encoding.GetCid(0x82A0)));
        Assert.Equal("漢", collection.GetText((uint)encoding.GetCid(0x8ABF)));
    }

    [Fact]
    public void MapsTheOtherCollectionsToText()
    {
        Assert.Equal("中", Text("GBK-EUC-H", "Adobe-GB1-UCS2", 0xD6D0));
        Assert.Equal("中", Text("ETen-B5-H", "Adobe-CNS1-UCS2", 0xA4A4));
        Assert.Equal("한", Text("KSCms-UHC-H", "Adobe-Korea1-UCS2", 0xC7D1));
    }

    [Fact]
    public void MapsUnicodeEncodingsThroughTheirCollection()
    {
        // The Uni* maps are keyed by the Unicode value itself, so a correct chain returns the very
        // character it started from.
        Assert.Equal("あ", Text("UniJIS-UCS2-H", "Adobe-Japan1-UCS2", 0x3042));
        Assert.Equal("中", Text("UniGB-UCS2-H", "Adobe-GB1-UCS2", 0x4E2D));
        Assert.Equal("中", Text("UniCNS-UCS2-H", "Adobe-CNS1-UCS2", 0x4E2D));
        Assert.Equal("한", Text("UniKS-UCS2-H", "Adobe-Korea1-UCS2", 0xD55C));
    }

    [Fact]
    public void InheritsFromTheMapAVerticalVariantNames()
    {
        CMap horizontal = CMapProvider.Load("90ms-RKSJ-H")!;
        CMap vertical = CMapProvider.Load("90ms-RKSJ-V")!;

        Assert.False(horizontal.IsVertical);
        Assert.True(vertical.IsVertical);

        // A vertical map states only the glyphs that differ and borrows the rest through usecmap.
        Assert.Equal(horizontal.GetCid('A'), vertical.GetCid('A'));
        Assert.Equal(0x82A0u, vertical.ReadCode([0x82, 0xA0], 0, out int consumed));
        Assert.Equal(2, consumed);

        // The vertical forms themselves are the point of the map: the ideographic comma differs.
        Assert.NotEqual(horizontal.GetCid(0x8141), vertical.GetCid(0x8141));
    }

    [Fact]
    public void AnswersNothingForNamesItDoesNotCarry()
    {
        Assert.Null(CjkCMaps.Instance.Load("Nonesuch-H"));
        Assert.Null(CMapProvider.Load("Nonesuch-H"));
    }

    private static string? Text(string encodingName, string collectionName, uint code)
    {
        CMap encoding = CMapProvider.Load(encodingName)!;
        CMap collection = CMapProvider.Load(collectionName)!;
        return collection.GetText((uint)encoding.GetCid(code));
    }
}
