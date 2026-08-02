using Inkwright.Cos;
using Inkwright.IO;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// ISO 19005-2/-3 §6.1.8 and ISO 19005-4 §6.1.7 make the UTF-8 recommendation
/// for names normative for fonts, colourants, and logical structure types.
/// </summary>
public class PdfANameEncodingTests
{
    public static TheoryData<PdfAConformance> ModernParts =>
    [
        PdfAConformance.A2B,
        PdfAConformance.A3B,
        PdfAConformance.A4,
    ];

    [Theory]
    [MemberData(nameof(ModernParts))]
    public void RejectsInvalidUtf8FontNames(PdfAConformance conformance) =>
        AssertInvalidName(conformance, SpecialNameKind.Font);

    [Theory]
    [MemberData(nameof(ModernParts))]
    public void RejectsInvalidUtf8SeparationColourantNames(PdfAConformance conformance) =>
        AssertInvalidName(conformance, SpecialNameKind.Separation);

    [Theory]
    [MemberData(nameof(ModernParts))]
    public void RejectsInvalidUtf8DeviceNColourantNames(PdfAConformance conformance) =>
        AssertInvalidName(conformance, SpecialNameKind.DeviceN);

    [Theory]
    [MemberData(nameof(ModernParts))]
    public void RejectsInvalidUtf8StructureTypeNames(PdfAConformance conformance) =>
        AssertInvalidName(conformance, SpecialNameKind.StructureType);

    [Theory]
    [MemberData(nameof(ModernParts))]
    public void AcceptsValidUtf8AfterNameEscapeExpansion(PdfAConformance conformance)
    {
        foreach (SpecialNameKind kind in Enum.GetValues<SpecialNameKind>())
        {
            byte[] file = BuildFile(conformance, kind, PdfName.Get("é"));

            Assert.Contains("/#C3#A9", System.Text.Encoding.ASCII.GetString(file), StringComparison.Ordinal);
            Assert.DoesNotContain(
                PdfAProfile.Validate(file, conformance),
                problem => problem.Message.Contains("valid UTF-8", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PartOneDoesNotApplyTheSpecialNameRule()
    {
        byte[] file = WithInvalidName(BuildFile(PdfAConformance.A1B, SpecialNameKind.Font, PdfName.Get("Bad")));

        Assert.DoesNotContain(
            PdfAProfile.Validate(file, PdfAConformance.A1B),
            problem => problem.Message.Contains("valid UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public void ByteValidationChecksSpecialNamesInUnreachableLiveObjects()
    {
        using PdfDocument document = PdfDocument.Create();
        document.Version = PdfVersion.Pdf20;
        document.Pages.Add();
        document.AddObject(PdfValue.Dictionary(new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfNames.Font),
            [PdfNames.Subtype] = PdfValue.Name(PdfNames.Type1),
            [PdfNames.BaseFont] = PdfValue.Name(PdfName.Get(new byte[] { 0xFF })),
        }));

        byte[] bytes = document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            UpdateModificationDate = false,
            SetProducer = false,
        });

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.1.7" &&
                       problem.Message.Contains("valid UTF-8", StringComparison.Ordinal));
    }

    private static void AssertInvalidName(PdfAConformance conformance, SpecialNameKind kind)
    {
        byte[] file = WithInvalidName(BuildFile(conformance, kind, PdfName.Get("Bad")));
        string clause = conformance.Part == 4 ? "6.1.7" : "6.1.8";

        Assert.Contains(
            PdfAProfile.Validate(file, conformance),
            problem => problem.Clause == clause &&
                       problem.Message.Contains("valid UTF-8", StringComparison.Ordinal));
    }

    private static byte[] BuildFile(PdfAConformance conformance, SpecialNameKind kind, PdfName name)
    {
        using var document = PdfDocument.Create();
        document.Version = conformance.Part switch
        {
            1 => PdfVersion.Pdf14,
            4 => PdfVersion.Pdf20,
            _ => PdfVersion.Pdf17,
        };

        PdfPage page = document.Pages.Add();
        switch (kind)
        {
            case SpecialNameKind.Font:
                AddFont(page, name);
                break;
            case SpecialNameKind.Separation:
                AddColourSpace(page, new PdfArray(
                    PdfValue.Name(PdfNames.Separation),
                    PdfValue.Name(name),
                    PdfValue.Name(PdfName.Get("DeviceCMYK")),
                    PdfValue.Null));
                break;
            case SpecialNameKind.DeviceN:
                AddColourSpace(page, new PdfArray(
                    PdfValue.Name(PdfNames.DeviceN),
                    PdfValue.Array(new PdfArray(PdfValue.Name(name))),
                    PdfValue.Name(PdfName.Get("DeviceCMYK")),
                    PdfValue.Null));
                break;
            case SpecialNameKind.StructureType:
                AddStructure(document, name);
                break;
        }

        return document.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            UpdateModificationDate = false,
            SetProducer = false,
        });
    }

    private static void AddFont(PdfPage page, PdfName name)
    {
        var font = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfNames.Font),
            [PdfNames.Subtype] = PdfValue.Name(PdfNames.Type1),
            [PdfNames.BaseFont] = PdfValue.Name(name),
        };
        page.Resources.Set(
            PdfNames.Font,
            PdfValue.Dictionary(new PdfDictionary { [PdfName.Get("F0")] = PdfValue.Dictionary(font) }));
    }

    private static void AddColourSpace(PdfPage page, PdfArray colourSpace) =>
        page.Resources.Set(
            PdfNames.ColorSpace,
            PdfValue.Dictionary(new PdfDictionary
            {
                [PdfName.Get("CS0")] = PdfValue.Array(colourSpace),
            }));

    private static void AddStructure(PdfDocument document, PdfName name)
    {
        var element = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfNames.StructElem),
            [PdfNames.S] = PdfValue.Name(name),
        };
        var root = new PdfDictionary
        {
            [PdfNames.Type] = PdfValue.Name(PdfNames.StructTreeRoot),
            [PdfNames.K] = PdfValue.Dictionary(element),
        };
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Dictionary(root));
    }

    private static byte[] WithInvalidName(byte[] file)
    {
        ReadOnlySpan<byte> valid = "/Bad"u8;
        ReadOnlySpan<byte> invalid = "/#FF"u8;
        int position = file.AsSpan().IndexOf(valid);
        Assert.True(position >= 0, "The test PDF did not contain its placeholder name.");
        Assert.True(file.AsSpan(position + valid.Length).IndexOf(valid) < 0, "The placeholder name was not unique.");

        invalid.CopyTo(file.AsSpan(position, valid.Length));
        return file;
    }

    private enum SpecialNameKind
    {
        Font,
        Separation,
        DeviceN,
        StructureType,
    }
}
