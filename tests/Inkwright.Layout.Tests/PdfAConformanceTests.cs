using System.Text;
using Inkwright.Annotations;
using Inkwright.Cos;
using Inkwright.Fonts;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

/// <summary>
/// The matrix in docs/pdfa-matrix.md says what the library promises against each clause of
/// ISO 19005. These tests are the half of that promise a machine can keep: what the library writes
/// satisfies the rules, and what breaks them is found.
/// </summary>
public class PdfAConformanceTests
{
    public static TheoryData<PdfAConformance> Levels =>
    [
        PdfAConformance.A1B, PdfAConformance.A2B, PdfAConformance.A2U, PdfAConformance.A3B,
        PdfAConformance.A3U, PdfAConformance.A4, PdfAConformance.A4E, PdfAConformance.A4F,
    ];

    /// <summary>
    /// ISO 19005-4 §6.1.2. Part 4 is the only part written against PDF 2.0, and it says so in the
    /// header: the parts before it require %PDF-1.n, which is why no file can claim both.
    /// </summary>
    [Fact]
    public void WritesAPartFourFileAsPdf20()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A4, out _);

        Assert.StartsWith("%PDF-2.", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Empty(PdfAProfile.Validate(bytes, PdfAConformance.A4));
    }

    /// <summary>A PDF 1.7 header cannot satisfy part 4, and a 2.0 header cannot satisfy part 2.</summary>
    [Fact]
    public void RefusesAHeaderFromTheWrongGenerationOfTheStandard()
    {
        byte[] partTwo = BuildArchival(PdfAConformance.A2B, out _);
        byte[] partFour = BuildArchival(PdfAConformance.A4, out _);

        Assert.Contains(
            PdfAProfile.Validate(partTwo, PdfAConformance.A4),
            problem => problem.Clause == "6.1.2" && problem.Message.Contains("%PDF-2.n", StringComparison.Ordinal));

        Assert.Contains(
            PdfAProfile.Validate(partFour, PdfAConformance.A2B),
            problem => problem.Clause == "6.1.2" && problem.Message.Contains("%PDF-1.n", StringComparison.Ordinal));
    }

    /// <summary>
    /// §6.1.3: PDF 2.0 deprecated the document information dictionary and part 4 makes that
    /// normative, so the writer drops it and does not stamp a producer name into it either.
    /// </summary>
    [Fact]
    public void LeavesOutTheInformationDictionaryUnderPartFour()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A4, out _);

        using PdfDocument reloaded = PdfDocument.Load(bytes);

        // The model hands every document an information dictionary so callers can set a title
        // without checking, so the question is what the file carried, not what the model holds.
        Assert.False(reloaded.HadInformationDictionary);
        Assert.DoesNotContain("/Producer", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        // The title the caller set still reaches a reader, through the packet where PDF 2.0 wants it.
        PdfStream metadata = reloaded.Resolve(reloaded.Catalog.Get(PdfName.Get("Metadata"))).AsStream!;
        Assert.Contains("Archival sample", Encoding.UTF8.GetString(metadata.Decode().Span), StringComparison.Ordinal);
    }

    /// <summary>
    /// §6.7.3 with the errata ISO approved as pdf-issues #123: part 4 requires <c>pdfaid:rev</c>,
    /// and a file that is neither PDF/A-4e nor PDF/A-4f shall carry no conformance level at all.
    /// </summary>
    [Fact]
    public void WritesThePartFourIdentificationSchema()
    {
        string plain = Packet(PdfAConformance.A4);
        Assert.Contains("<pdfaid:part>4</pdfaid:part>", plain, StringComparison.Ordinal);
        Assert.Contains("<pdfaid:rev>2020</pdfaid:rev>", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("pdfaid:conformance", plain, StringComparison.Ordinal);

        Assert.Contains("<pdfaid:conformance>E</pdfaid:conformance>", Packet(PdfAConformance.A4E), StringComparison.Ordinal);
        Assert.Contains("<pdfaid:conformance>F</pdfaid:conformance>", Packet(PdfAConformance.A4F), StringComparison.Ordinal);

        static string Packet(PdfAConformance level)
        {
            byte[] bytes = BuildArchival(level, out _);
            using PdfDocument document = PdfDocument.Load(bytes);
            PdfStream metadata = document.Resolve(document.Catalog.Get(PdfName.Get("Metadata"))).AsStream!;
            return Encoding.UTF8.GetString(metadata.Decode().Span);
        }
    }

    [Fact]
    public void RejectsAPartFourRevisionTheValidatorDoesNotImplement()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A4, out _);
        using PdfDocument document = PdfDocument.Load(bytes);
        PdfStream metadata = document.Resolve(document.Catalog.Get(PdfName.Get("Metadata"))).AsStream!;
        string packet = Encoding.UTF8.GetString(metadata.DecodedSpan())
            .Replace(">2020</pdfaid:rev>", ">2099</pdfaid:rev>", StringComparison.Ordinal);
        metadata.SetData(Encoding.UTF8.GetBytes(packet), compress: false);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.7.3" &&
                       problem.Message.Contains("unsupported pdfaid:rev '2099'", StringComparison.OrdinalIgnoreCase) &&
                       problem.Message.Contains("19005-4:2020", StringComparison.Ordinal));
    }

    /// <summary>A level letter on a plain PDF/A-4 file is itself a violation of §6.7.3.</summary>
    [Fact]
    public void RejectsAConformanceLevelOnAPlainPartFourFile()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A4E, out _);

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.7.3" && problem.Message.Contains("shall not provide one", StringComparison.Ordinal));
    }

    /// <summary>
    /// Annex A.2: PDF/A-4f requires an EmbeddedFiles key in the catalog name dictionary. An
    /// otherwise valid PDF/A-4 document cannot claim the level without that exact marker.
    /// </summary>
    [Fact]
    public void RequiresEmbeddedFilesKeyFromAFileThatClaimsPartFourF()
    {
        // No EmbeddedFiles key: sound as plain PDF/A-4, wrong level as PDF/A-4f.
        byte[] bytes = BuildArchival(PdfAConformance.A4, out _);

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4F),
            problem => problem.Clause == "A.2");

        Assert.DoesNotContain(PdfAProfile.Validate(bytes, PdfAConformance.A4), problem => problem.Clause == "A.2");
    }

    /// <summary>
    /// §6.9 with Annex A: a plain PDF/A-4 EmbeddedFiles entry must be PDF/A, and PDF/A-4f lifts
    /// exactly that content restriction.
    /// </summary>
    [Fact]
    public void AdmitsAnyAttachmentTypeOnlyUnderPartFourF()
    {
        // The very file PDF/A-4f is written for: it carries a spreadsheet, which is not PDF/A.
        byte[] bytes = BuildArchival(PdfAConformance.A4F, out _);

        Assert.Contains(
            PdfAProfile.Validate(bytes, PdfAConformance.A4),
            problem => problem.Clause == "6.9" &&
                       problem.Message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            PdfAProfile.Validate(bytes, PdfAConformance.A4F),
            problem => problem.Message.Contains("Embedded PDF", StringComparison.Ordinal));
    }

    /// <summary>§6.1.12: the catalogue may override the header version, but only within PDF 2.0.</summary>
    [Fact]
    public void RejectsACatalogVersionFromTheWrongGeneration()
    {
        byte[] built = BuildArchival(PdfAConformance.A4, out _);

        // The value is judged on the model: a save would replace it with the document's own version.
        using PdfDocument document = PdfDocument.Load(built);
        document.Catalog.Set(PdfName.Get("Version"), PdfValue.Name(PdfName.Get("1.7")));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A4),
            problem => problem.Clause == "6.1.12");
    }

    /// <summary>
    /// §6.6.2: part 4 keeps JavaScript actions in the file and restricts the processor instead,
    /// where parts 1 to 3 forbid the action outright.
    /// </summary>
    [Fact]
    public void AllowsJavaScriptActionsOnlyFromPartFour()
    {
        byte[] partFour = WithOutlineAction(PdfAConformance.A4);
        byte[] partTwo = WithOutlineAction(PdfAConformance.A2B);

        Assert.DoesNotContain(PdfAProfile.Validate(partFour, PdfAConformance.A4), p => p.Message.Contains("JavaScript", StringComparison.Ordinal));
        Assert.Contains(PdfAProfile.Validate(partTwo, PdfAConformance.A2B), p => p.Message.Contains("JavaScript", StringComparison.Ordinal));

        static byte[] WithOutlineAction(PdfAConformance level)
        {
            byte[] bytes = BuildArchival(level, out _);
            using PdfDocument document = PdfDocument.Load(bytes);

            var action = new PdfDictionary(2);
            action.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("JavaScript")));
            action.Set(PdfName.Get("JS"), PdfValue.TextString("0;"));
            document.Catalog.Set(PdfName.Get("OpenAction"), PdfValue.Dictionary(action));
            document.MarkChanged(document.CatalogId);

            return document.ToArray();
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void WritesFilesThatSatisfyTheirOwnClaim(PdfAConformance level)
    {
        byte[] bytes = BuildArchival(level, out _);

        Assert.Empty(PdfAProfile.Validate(bytes, level));
    }

    [Fact]
    public void KeepsAPartOneFileWithinPdf14()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A1B, out _);
        string raw = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4", raw, StringComparison.Ordinal);

        // Object streams and cross-reference streams arrived in PDF 1.5. A file that says 1.4 and
        // uses them is lying about itself, which is exactly what an archival claim must not do.
        Assert.DoesNotContain("/ObjStm", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/XRef", raw, StringComparison.Ordinal);
        Assert.Contains("\nxref\n", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsAPartOneFileWithinPdf14AcrossAnIncrementalUpdate()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A1B, out _);

        using PdfDocument reloaded = PdfDocument.Load(bytes);
        reloaded.Pages[0].Annotations.AddNote(new PdfPoint(500, 700), "Amended a year later", "Archivist");

        using var stream = new MemoryStream();
        reloaded.SaveIncremental(stream);
        byte[] updated = stream.ToArray();

        // The appended section belongs to the same PDF 1.4 file, so the machinery of 1.5 — object
        // streams and a cross-reference stream — must not appear in it.
        string tail = Encoding.Latin1.GetString(updated.AsSpan(bytes.Length));
        Assert.DoesNotContain("/ObjStm", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /XRef", tail, StringComparison.Ordinal);
        Assert.Contains("\nxref\n", tail, StringComparison.Ordinal);

        Assert.Empty(PdfAProfile.Validate(updated, PdfAConformance.A1B));
    }

    [Fact]
    public void KeepsTheMetadataPacketInStepWithTheInformationDictionary()
    {
        using PdfDocument document = Fresh(out _);

        var metadata = new Inkwright.Metadata.XmpMetadata
        {
            Title = "Sample",
            Producer = "Somebody Else 1.0",
            ModificationDate = DateTimeOffset.Now.AddYears(-1),
        };
        metadata.ApplyTo(document);

        byte[] bytes = document.ToArray();
        string raw = Encoding.UTF8.GetString(bytes);

        // The save stamped the information dictionary with today's date and Inkwright's name; the
        // packet must have followed, or the file disagrees with itself the moment it is written.
        Assert.DoesNotContain("Somebody Else", raw, StringComparison.Ordinal);

        using PdfDocument reloaded = PdfDocument.Load(bytes);
        int start = raw.IndexOf("<xmp:ModifyDate>", StringComparison.Ordinal) + "<xmp:ModifyDate>".Length;
        int end = raw.IndexOf("</xmp:ModifyDate>", start, StringComparison.Ordinal);
        DateTimeOffset written = DateTimeOffset.Parse(raw[start..end], System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(Math.Abs((written - reloaded.Info.ModificationDate!.Value).TotalSeconds) < 1);
    }

    [Fact]
    public void RefusesToEncryptADocumentThatClaimsArchivalStatus()
    {
        _ = BuildArchival(PdfAConformance.A2B, out PdfDocument document);

        using (document)
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => document.ToArray(new PdfSaveOptions
                {
                    Encryption = new PdfEncryptionSettings { UserPassword = "open" },
                }));

            Assert.Contains("PDF/A-2b", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RefusesToEncryptAnArchivalFileItMerelyLoaded()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A1B, out _);

        // The claim of a loaded file lives in its XMP packet, not in this process's memory, and it
        // binds all the same: encrypting the file would falsify what the packet says.
        using PdfDocument reloaded = PdfDocument.Load(bytes);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => reloaded.ToArray(new PdfSaveOptions
            {
                Encryption = new PdfEncryptionSettings { UserPassword = "open" },
            }));

        Assert.Contains("PDF/A-1b", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JudgesAnImageWithAlphaByEachPartsRules()
    {
        using PdfDocument document = Fresh(out _);

        byte[] samples = new byte[4 * 4 * 3];
        Array.Fill(samples, (byte)200);
        byte[] alpha = new byte[4 * 4];
        Array.Fill(alpha, (byte)128);

        Images.PdfImage image = Images.PdfImage.FromPixels(document, 4, 4, samples, 3, alpha);

        using (Content.ContentCanvas canvas = Content.ContentCanvas.ForPage(document.Pages[0]))
        {
            canvas.DrawImage(image, new PdfRectangle(50, 50, 150, 150));
            canvas.Commit();
        }

        // A half-transparent picture is transparency, which part 1 forbids outright and part 2
        // accepts once the output intent names the blending space.
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A1B),
            problem => problem.Clause == "6.4");

        PdfAProfile.Apply(document, PdfAConformance.A2B);
        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A2B));
    }

    [Fact]
    public void FindsStreamsThatReachOutsideTheFile()
    {
        using PdfDocument document = Fresh(out _);

        var stream = PdfStream.FromData("q Q"u8.ToArray(), compress: false);
        stream.Dictionary.Set(PdfName.Get("F"), PdfValue.TextString("/etc/passwd"));
        AttachToCatalog(document, stream);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.1.7.1" && problem.Message.Contains("F,", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsWithdrawnAndUnknownFilters()
    {
        using PdfDocument document = Fresh(out _);

        var lzw = PdfStream.FromData("q Q"u8.ToArray(), compress: false);
        lzw.Dictionary.Set(PdfNames.Filter, PdfValue.Name(PdfName.Get("LZWDecode")));
        AttachToCatalog(document, lzw);

        IReadOnlyList<PdfAProblem> partTwo = PdfAProfile.Validate(document, PdfAConformance.A2B);
        Assert.Contains(partTwo, problem => problem.Clause == "6.1.7.2");

        IReadOnlyList<PdfAProblem> partOne = PdfAProfile.Validate(document, PdfAConformance.A1B);
        Assert.Contains(partOne, problem => problem.Clause == "6.1.10");
    }

    [Fact]
    public void FindsDataAfterTheEndOfFileMarker()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A2B, out _);
        byte[] littered = [.. bytes, .. "\nthis was appended by a careless tool\n"u8];

        Assert.Contains(
            PdfAProfile.Validate(littered, PdfAConformance.A2B),
            problem => problem.Clause == "6.1.3" && problem.Message.Contains("%%EOF", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsPagesOutsideTheAllowedSize()
    {
        using PdfDocument document = Fresh(out _);
        document.Pages[0].MediaBox = new PdfRectangle(0, 0, 20000, 2);

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A2B);

        Assert.Contains(problems, problem => problem.Clause == "6.1.13");
    }

    [Fact]
    public void FindsPermissionsTheStandardDoesNotAllow()
    {
        using PdfDocument document = Fresh(out _);

        var permissions = new PdfDictionary(1);
        permissions.Set(PdfName.Get("UR"), PdfValue.Null);
        document.Catalog.Set(PdfName.Get("Perms"), PdfValue.Dictionary(permissions));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.1.12");
    }

    [Fact]
    public void FindsDeviceCmykStandingBehindAnRgbOutputIntent()
    {
        using PdfDocument document = Fresh(out _);

        using (Content.ContentCanvas canvas = Content.ContentCanvas.ForPage(document.Pages[0]))
        {
            canvas.FillColor(PdfColor.Cmyk(0.1f, 0.2f, 0.3f, 0.4f)).Rectangle(50, 50, 100, 100).Fill();
            canvas.Commit();
        }

        // The sRGB intent the profile installs says nothing about what these four numbers mean.
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.4.3" && problem.Message.Contains("DeviceCMYK", StringComparison.Ordinal));
    }

    [Fact]
    public void PartOneRefusesTransparencyAndPartTwoAllowsIt()
    {
        using PdfDocument document = Fresh(out _);

        using (Content.ContentCanvas canvas = Content.ContentCanvas.ForPage(document.Pages[0]))
        {
            canvas.Save().Opacity(0.4).FillColor(PdfColor.Red).Rectangle(50, 50, 80, 80).Fill().Restore();
            canvas.Commit();
        }

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A1B),
            problem => problem.Clause == "6.4");

        // Part 2 admitted transparency, provided something names the blending space; the output
        // intent added below does exactly that.
        PdfAProfile.Apply(document, PdfAConformance.A2B);
        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A2B));
    }

    [Fact]
    public void FindsOperatorsTheSpecificationDoesNotDefine()
    {
        using PdfDocument document = Fresh(out _);
        document.Pages[0].AppendContent("q 1 0 0 1 0 0 cm /Foo pdfmark Q"u8.ToArray(), compress: false);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.2" && problem.Message.Contains("pdfmark", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsContentNestedDeeperThanTheStandardAllows()
    {
        using PdfDocument document = Fresh(out _);

        string content = string.Concat(Enumerable.Repeat("q ", 29)) + string.Concat(Enumerable.Repeat("Q ", 29));
        document.Pages[0].AppendContent(Encoding.ASCII.GetBytes(content), compress: false);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.1.13" && problem.Message.Contains("28", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsADeviceNSpaceWithTooManyColourants()
    {
        using PdfDocument document = Fresh(out _);

        var colourants = new PdfArray(33);
        for (int i = 0; i < 33; i++)
            colourants.Add(PdfValue.Name(PdfName.Get($"Ink{i}")));

        var deviceN = new PdfArray(
            PdfValue.Name(PdfName.Get("DeviceN")),
            PdfValue.Array(colourants),
            PdfValue.Name(PdfName.Get("DeviceGray")));

        var spaces = new PdfDictionary(1);
        spaces.Set(PdfName.Get("CS0"), PdfValue.Array(deviceN));
        document.Pages[0].Resources.Set(PdfName.Get("ColorSpace"), PdfValue.Dictionary(spaces));

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.1.13" && problem.Message.Contains("colourants", StringComparison.Ordinal));
    }

    [Fact]
    public void WalksIntoTypeThreeGlyphProcedures()
    {
        using PdfDocument document = Fresh(out _);

        var glyph = PdfStream.FromData("/Foo pdfmark"u8.ToArray(), compress: false);
        PdfObjectId glyphId = document.AddObject(PdfValue.Stream(glyph));

        var procedures = new PdfDictionary(1);
        procedures.Set(PdfName.Get("a"), PdfValue.Reference(glyphId));

        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Font")));
        font.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Type3")));
        font.Set(PdfName.Get("CharProcs"), PdfValue.Dictionary(procedures));

        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F9"), PdfValue.Dictionary(font));
        document.Pages[0].Resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));

        // The glyph procedure is a content stream like any other, and the operator it holds is not
        // in the standard; a validator that only reads pages would never see it.
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.2" && problem.Message.Contains("Type 3 glyph", StringComparison.Ordinal));
    }

    [Fact]
    public void SeesTransparencyThroughANestedForm()
    {
        using PdfDocument document = Fresh(out _);

        var state = new PdfDictionary(2);
        state.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("ExtGState")));
        state.Set(PdfName.Get("ca"), PdfValue.Number(0.5));

        var states = new PdfDictionary(1);
        states.Set(PdfName.Get("GS0"), PdfValue.Dictionary(state));

        var resources = new PdfDictionary(1);
        resources.Set(PdfName.Get("ExtGState"), PdfValue.Dictionary(states));

        var form = PdfStream.FromData("/GS0 gs 0 0 10 10 re f"u8.ToArray(), compress: false);
        form.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("XObject")));
        form.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("Form")));
        form.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(new PdfArray(
            PdfValue.Number(0), PdfValue.Number(0), PdfValue.Number(10), PdfValue.Number(10))));
        form.Dictionary.Set(PdfNames.Resources, PdfValue.Dictionary(resources));

        var xobjects = new PdfDictionary(1);
        xobjects.Set(PdfName.Get("Fm0"), PdfValue.Stream(form));
        document.Pages[0].Resources.Set(PdfName.Get("XObject"), PdfValue.Dictionary(xobjects));

        // The page's own resources are clean; the transparency sits one level down, inside the
        // form's resource dictionary, which Annex A.3 says to examine the same way.
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.10" && problem.Message.Contains("transparency", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsDatesThatDisagreeBetweenTheTwoLedgers()
    {
        _ = BuildArchival(PdfAConformance.A1B, out PdfDocument document);

        using (document)
        {
            document.Info.CreationDate = document.Info.CreationDate!.Value.AddDays(2);

            Assert.Contains(
                PdfAProfile.Validate(document, PdfAConformance.A1B),
                problem => problem.Clause == "6.7.3" && problem.Message.Contains("CreationDate", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FindsGraphicsStateKeysThatChangeHowColourIsRendered()
    {
        using PdfDocument document = Fresh(out _);

        var state = new PdfDictionary(3);
        state.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("ExtGState")));
        state.Set(PdfName.Get("TR"), PdfValue.Name(PdfName.Get("Identity")));
        state.Set(PdfName.Get("RI"), PdfValue.Name(PdfName.Get("Sideways")));
        document.Pages[0].Resources.Set(PdfName.Get("ExtGState"), PdfValue.Dictionary(Wrap(state)));

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A2B);

        Assert.Contains(problems, problem => problem.Clause == "6.2.5" && problem.Message.Contains("TR", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Clause == "6.2.6");

        static PdfDictionary Wrap(PdfDictionary state)
        {
            var states = new PdfDictionary(1);
            states.Set(PdfName.Get("GS0"), PdfValue.Dictionary(state));
            return states;
        }
    }

    [Fact]
    public void FindsPostScriptSmuggledIntoAnXObject()
    {
        using PdfDocument document = Fresh(out _);

        var stream = PdfStream.FromData("%!PS"u8.ToArray(), compress: false);
        stream.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("XObject")));
        stream.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get("PS")));
        AttachToCatalog(document, stream);

        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Clause == "6.2.9.3");
    }

    [Fact]
    public void DeclaresWhichGlyphsTheSubsetActuallyHas()
    {
        byte[] bytes = BuildArchival(PdfAConformance.A1B, out _);

        using PdfDocument reloaded = PdfDocument.Load(bytes);
        PdfDictionary descriptor = Descendant(reloaded).GetDictionary(PdfName.Get("FontDescriptor"))!;
        PdfStream set = descriptor.GetStream(PdfName.Get("CIDSet"))!;

        // Part 1 requires the declaration; every part requires it to be true. Bit zero stands for
        // CID 0, which the subsetter always keeps.
        ReadOnlySpan<byte> bits = set.DecodedSpan();
        Assert.True(bits.Length > 0);
        Assert.Equal(0x80, bits[0] & 0x80);

        int declared = 0;
        foreach (byte b in bits)
            declared += System.Numerics.BitOperations.PopCount(b);

        Assert.InRange(declared, 2, 512);
    }

    [Fact]
    public void FindsWidthsThatDisagreeWithTheEmbeddedProgram()
    {
        _ = BuildArchival(PdfAConformance.A2B, out PdfDocument document);

        using (document)
        {
            PdfDictionary descendant = Descendant(document);
            PdfArray widths = descendant.GetArray(PdfName.Get("W"))!;
            widths.Add(PdfValue.Integer(3));
            widths.Add(PdfValue.Array(new PdfArray(PdfValue.Integer(1))));

            Assert.Contains(
                PdfAProfile.Validate(document, PdfAConformance.A2B),
                problem => problem.Clause == "6.2.11.5");
        }
    }

    [Fact]
    public void FindsTheNotdefGlyphBeingShown()
    {
        _ = BuildArchival(PdfAConformance.A2B, out PdfDocument document);

        using (document)
        {
            PdfName name = FontResourceName(document);
            document.Pages[0].AppendContent(
                System.Text.Encoding.ASCII.GetBytes($"BT /{name.Value} 12 Tf 72 300 Td <0000> Tj ET"),
                compress: false);

            Assert.Contains(
                PdfAProfile.Validate(document, PdfAConformance.A2B),
                problem => problem.Clause == "6.2.11.8");
        }
    }

    [Fact]
    public void WritesTaggedFilesThatSatisfyLevelA()
    {
        string? path = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(path is null, "PDF/A needs an embeddable font and none was found.");

        using var document = PdfDocument.Create();
        PdfFont font = document.Fonts.Embed(path!);

        Report.Create()
            .Page(page =>
            {
                page.Tagged("en-GB");
                page.DefaultTextStyle(TextStyle.Default.Font(font));
                page.Content().Text("A document that says what its pages mean.");
            })
            .Render(document);

        foreach (PdfAConformance level in (PdfAConformance[])[PdfAConformance.A1A, PdfAConformance.A2A, PdfAConformance.A3A])
        {
            PdfAProfile.Apply(document, level, new PdfAOptions { Language = "en-GB" });
            Assert.Empty(PdfAProfile.Validate(document, level));
        }
    }

    [Fact]
    public void RefusesLevelAWithoutAStructureTree()
    {
        using PdfDocument document = Fresh(out _);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => PdfAProfile.Apply(document, PdfAConformance.A2A));

        Assert.Contains("StructTreeRoot", failure.Message, StringComparison.Ordinal);
        Assert.Contains("MarkInfo", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsStructureTypesThatLeadNowhere()
    {
        using PdfDocument document = Fresh(out _);

        var element = new PdfDictionary(2);
        element.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructElem")));
        element.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("Sidebar")));

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("StructTreeRoot")));
        root.Set(PdfName.Get("K"), PdfValue.Dictionary(element));

        var marked = new PdfDictionary(1);
        marked.Set(PdfName.Get("Marked"), PdfValue.True);

        document.Catalog.Set(PdfName.Get("StructTreeRoot"), PdfValue.Dictionary(root));
        document.Catalog.Set(PdfName.Get("MarkInfo"), PdfValue.Dictionary(marked));
        document.Catalog.Set(PdfName.Get("Lang"), PdfValue.TextString("english please"));

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A2A);

        Assert.Contains(problems, problem => problem.Clause == "6.7.3.4" && problem.Message.Contains("Sidebar", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Clause == "6.7.4");
    }

    [Fact]
    public void FindsActionsAndFormsThatChangeWhatThePageShows()
    {
        using PdfDocument document = Fresh(out _);

        var launch = new PdfDictionary(2);
        launch.Set(PdfNames.Type, PdfValue.Name(PdfName.Get("Action")));
        launch.Set(PdfName.Get("S"), PdfValue.Name(PdfName.Get("Launch")));

        var field = new PdfDictionary(2);
        field.Set(PdfName.Get("T"), PdfValue.TextString("name"));
        field.Set(PdfName.Get("A"), PdfValue.Dictionary(launch));

        var form = new PdfDictionary(2);
        form.Set(PdfName.Get("NeedAppearances"), PdfValue.True);
        form.Set(PdfName.Get("Fields"), PdfValue.Array(new PdfArray(PdfValue.Dictionary(field))));
        document.Catalog.Set(PdfName.Get("AcroForm"), PdfValue.Dictionary(form));

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A2B);

        Assert.Contains(problems, problem => problem.Clause == "6.4.1" && problem.Message.Contains("NeedAppearances", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Clause == "6.4.1" && problem.Message.Contains("may not act", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Clause == "6.5.1" && problem.Message.Contains("Launch", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsACommentedDocumentAtEveryPart()
    {
        using PdfDocument document = Fresh(out _);

        Annotations.PdfTextAnnotation note = document.Pages[0].Annotations.AddNote(
            new PdfPoint(500, 700), "Checked against the standard", "Reviewer");

        note.Reply("Agreed", "Author");

        // The comment layer writes Print, NoZoom and NoRotate and draws its own appearance, which is
        // what clauses 6.5.3 and 6.3.2 ask for.
        PdfAProfile.Apply(document, PdfAConformance.A1B);
        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A1B));

        PdfAProfile.Apply(document, PdfAConformance.A2B);
        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A2B));
    }

    [Fact]
    public void SurvivesARoundOfReviewArrivingFromAnotherReviewer()
    {
        using PdfDocument reviewed = Fresh(out _);
        Annotations.PdfTextAnnotation note = reviewed.Pages[0].Annotations.AddNote(
            new PdfPoint(500, 700), "Check the wording of clause four", "Reviewer");

        note.Reply("Rewritten", "Author");
        reviewed.Pages[0].Annotations.AddRectangle(new PdfRectangle(45, 400, 550, 460), "Look here", "Reviewer");

        using PdfDocument archival = Fresh(out _);
        Assert.True(archival.ImportXfdf(reviewed.ExportXfdf()).Added > 0);

        // Comments arriving from outside are drawn by the same layer that already satisfies clause
        // 6.3, so taking in a review must not cost the document its claim.
        PdfAProfile.Apply(archival, PdfAConformance.A2B);
        Assert.Empty(PdfAProfile.Validate(archival, PdfAConformance.A2B));
    }

    [Fact]
    public void PartOneRefusesAnAttachedFile()
    {
        using PdfDocument document = Fresh(out _);
        document.Pages[0].Annotations.AddAttachment(new PdfPoint(72, 200), "notes.txt", "figures"u8.ToArray());

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A1B);

        Assert.Contains(problems, problem => problem.Clause == "6.5.2");
        Assert.Contains(problems, problem => problem.Clause == "6.1.11");
    }

    [Fact]
    public void FindsOptionalContentThatDoesNotNameItsVariants()
    {
        using PdfDocument document = Fresh(out _);

        var configuration = new PdfDictionary(1);
        configuration.Set(PdfName.Get("AS"), PdfValue.Array(new PdfArray()));

        var properties = new PdfDictionary(1);
        properties.Set(PdfName.Get("D"), PdfValue.Dictionary(configuration));
        document.Catalog.Set(PdfName.Get("OCProperties"), PdfValue.Dictionary(properties));

        IReadOnlyList<PdfAProblem> partTwo = PdfAProfile.Validate(document, PdfAConformance.A2B);
        Assert.Contains(partTwo, problem => problem.Clause == "6.9" && problem.Message.Contains("no Name", StringComparison.Ordinal));
        Assert.Contains(partTwo, problem => problem.Clause == "6.9" && problem.Message.Contains("AS", StringComparison.Ordinal));

        // Part 1 has no notion of variants at all.
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A1B),
            problem => problem.Clause == "6.1.13");
    }

    [Fact]
    public void FindsSlideShowsAndReaderDemands()
    {
        using PdfDocument document = Fresh(out _);
        document.Pages[0].Dictionary.Set(PdfName.Get("PresSteps"), PdfValue.Dictionary(new PdfDictionary(0)));
        document.Catalog.Set(PdfName.Get("Requirements"), PdfValue.Array(new PdfArray()));

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A2B);

        Assert.Contains(problems, problem => problem.Clause == "6.10");
        Assert.Contains(problems, problem => problem.Clause == "6.11");
    }

    private static PdfDictionary Descendant(PdfDocument document)
    {
        PdfDictionary fonts = document.Pages[0].Resources.GetDictionary(PdfName.Get("Font"))!;
        PdfDictionary type0 = fonts.GetDictionary(fonts.Keys.ToArray()[0])!;
        return type0.GetArray(PdfName.Get("DescendantFonts"))!.GetDictionary(0)!;
    }

    private static PdfName FontResourceName(PdfDocument document) =>
        document.Pages[0].Resources.GetDictionary(PdfName.Get("Font"))!.Keys.ToArray()[0];

    /// <summary>A document with an embedded font, an output intent and metadata: archival but bare.</summary>
    internal static byte[] BuildArchival(PdfAConformance level, out PdfDocument document)
    {
        document = Fresh(out PdfFont font);
        _ = font;

        document.Info.Title = "Archival sample";
        document.Info.Author = "Inkwright";

        // PDF/A-4f is the level for a document built around an attachment, so one that claims it
        // and carries none is not a valid sample of it (Annex A.2).
        if (level == PdfAConformance.A4F)
        {
            document.Attachments.Add(
                "figures.csv",
                "a,b\n1,2\n"u8.ToArray(),
                "text/csv",
                "The figures behind the table",
                AttachmentRelationship.Data);
        }

        PdfAProfile.Apply(document, level, new PdfAOptions { Language = "en" });

        return document.ToArray();
    }

    internal static PdfDocument Fresh(out PdfFont font)
    {
        string? path = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(path is null, "PDF/A needs an embeddable font and none was found.");

        var document = PdfDocument.Create();
        PdfFont embedded = document.Fonts.Embed(path!);
        font = embedded;

        Report.Create()
            .Page(page =>
            {
                page.DefaultTextStyle(TextStyle.Default.Font(embedded));
                page.Content().Text("A document that intends to outlive its author.");
            })
            .Render(document);

        return document;
    }

    private static void AttachToCatalog(PdfDocument document, PdfStream stream)
    {
        PdfObjectId id = document.AddObject(PdfValue.Stream(stream));
        document.Catalog.Set(PdfName.Get("Inkwright"), PdfValue.Reference(id));
    }
}
