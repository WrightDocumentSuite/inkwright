using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;
using Inkwright.Images;
using Inkwright.Markdown;
using Inkwright.Tagging;
using Xunit;

namespace Inkwright.Tests;

public class MarkdownExportTests
{
    [Fact]
    public void UntaggedExportIsDeterministicAndExplicitlyDiagnosed()
    {
        using PdfDocument document = PdfDocument.Load(MinimalPdf.OnePage());

        MarkdownDocument first = document.ToMarkdown();
        MarkdownDocument second = document.ToMarkdown();

        Assert.Equal("Hello, world\n", first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.Empty(first.Images);
        MarkdownExportWarning warning = Assert.Single(first.Diagnostics);
        Assert.Equal(MarkdownExportWarningKind.UntaggedFallback, warning.Kind);
    }

    [Fact]
    public void RequireTaggedRejectsAnUntaggedPdf()
    {
        using PdfDocument document = PdfDocument.Load(MinimalPdf.OnePage());

        Assert.Throws<InvalidOperationException>(() => document.ToMarkdown(new MarkdownExportOptions
        {
            StructureMode = MarkdownStructureMode.RequireTagged,
        }));
    }

    [Fact]
    public void InferLayoutProjectsUntaggedHeadingsListsAndTables()
    {
        using PdfDocument document = PdfDocument.Load(BuildUntaggedLayoutDocument());

        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            StructureMode = MarkdownStructureMode.InferLayout,
        });

        Assert.Equal(
            "# Report title\n\n" +
            "First paragraph line continues on its second line.\n\n" +
            "- First item\n" +
            "- Second item\n\n" +
            "| Name | Score |\n" +
            "| --- | --- |\n" +
            "| Ada | 10 |\n" +
            "| Bob | 20 |\n",
            markdown.Text);
        MarkdownExportWarning warning = Assert.Single(markdown.Diagnostics);
        Assert.Equal(MarkdownExportWarningKind.StructureApproximated, warning.Kind);
        Assert.Equal("Inferred layout", warning.Subject);
    }

    [Fact]
    public void InferLayoutStillPrefersTaggedStructure()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedDocument());

        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            StructureMode = MarkdownStructureMode.InferLayout,
        });

        Assert.Equal(
            "# A heading\n\nFirst \\*draft\\*\n\nreplacement \\[ok\\]\n",
            markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void InferLayoutUsesHtmlForTablesInCommonMark()
    {
        using PdfDocument document = PdfDocument.Load(BuildUntaggedLayoutDocument());

        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            Flavor = MarkdownFlavor.CommonMark,
            StructureMode = MarkdownStructureMode.InferLayout,
        });

        Assert.Contains("<table>\n  <tr>\n    <th>Name</th>", markdown.Text, StringComparison.Ordinal);
        Assert.Contains("<td>Ada</td>", markdown.Text, StringComparison.Ordinal);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.StructureApproximated);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.HtmlFallbackUsed);
    }

    [Fact]
    public void TaggedExportUsesRolesBlocksAndActualText()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedDocument());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal(
            "# A heading\n\nFirst \\*draft\\*\n\nreplacement \\[ok\\]\n",
            markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void TaggedExportDoesNotMutateThePdf()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedDocument());
        IReadOnlyList<Tagging.PdfStructureNode> structure = document.Structure;
        byte[] content = document.Pages[0].GetContent().ToArray();
        int catalogEntries = document.Catalog.Count;
        DateTimeOffset? modified = document.Info.ModificationDate;

        _ = document.ToMarkdown();

        Assert.Same(structure, document.Structure);
        Assert.Equal(content, document.Pages[0].GetContent().ToArray());
        Assert.Equal(catalogEntries, document.Catalog.Count);
        Assert.Equal(modified, document.Info.ModificationDate);
    }

    [Fact]
    public void StructureBuilderInvalidatesAnEarlierEmptyStructureSnapshot()
    {
        using PdfDocument document = PdfDocument.Create();
        document.Pages.Add();
        Assert.Empty(document.Structure);

        var builder = new StructureTreeBuilder(document);
        StructureElement root = builder.AddRoot("Document");
        root.Add("P").ActualText = "built later";
        builder.Build();

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("built later\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Theory]
    [InlineData("../media")]
    [InlineData("media//images")]
    [InlineData("/media")]
    [InlineData("C:\\media")]
    [InlineData("CON")]
    [InlineData("media.")]
    public void RejectsUnsafeMediaDirectories(string path)
    {
        using PdfDocument document = PdfDocument.Load(MinimalPdf.OnePage());

        Assert.Throws<ArgumentException>(() => document.ToMarkdown(new MarkdownExportOptions
        {
            MediaDirectoryName = path,
        }));
    }

    [Fact]
    public async Task SaveAsyncWritesUtf8WithoutBomAndLeavesUnrelatedFiles()
    {
        using PdfDocument document = PdfDocument.Load(MinimalPdf.OnePage());
        MarkdownDocument markdown = document.ToMarkdown();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("inkwright-md-");

        try
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            string unrelated = Path.Combine(directory.FullName, "keep.txt");
            await File.WriteAllTextAsync(unrelated, "keep", cancellationToken);

            await markdown.SaveAsync(directory.FullName, cancellationToken);

            byte[] bytes = await File.ReadAllBytesAsync(
                Path.Combine(directory.FullName, MarkdownDocument.DefaultFileName), cancellationToken);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Equal(markdown.Text, Encoding.UTF8.GetString(bytes));
            Assert.Equal("keep", await File.ReadAllTextAsync(unrelated, cancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExportsTaggedFigureOccurrencesAsDeduplicatedPngSidecars()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedImageDocument(drawTwice: true));

        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            MediaDirectoryName = "assets/pdf images",
        });

        Assert.Equal(
            "![A square](assets/pdf%20images/image1.png)\n\n" +
            "![The same square](assets/pdf%20images/image1.png)\n",
            markdown.Text);
        MarkdownImage image = Assert.Single(markdown.Images);
        Assert.Equal("image1.png", image.FileName);
        Assert.Equal("image/png", image.ContentType);
        Assert.True(image.Content.Span.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void CanDeliberatelyExportOnlyFigureAlternativeText()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedImageDocument(drawTwice: false));

        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            IncludeImages = false,
        });

        Assert.Equal("A square\n", markdown.Text);
        Assert.Empty(markdown.Images);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void FindsTaggedImagesPaintedThroughFormXObjects()
    {
        using PdfDocument document = PdfDocument.Load(
            BuildTaggedImageDocument(drawTwice: false, throughForm: true));

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("![A square](media/image1.png)\n", markdown.Text);
        Assert.Single(markdown.Images);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void DistinguishesTheSameMcidInPageAndFormContentStreams()
    {
        using PdfDocument document = PdfDocument.Load(BuildStreamQualifiedMcidDocument());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("page text\n\nform text\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void AppliesOptionalContentVisibilityToFigureImages()
    {
        using PdfDocument document = PdfDocument.Load(
            BuildTaggedImageDocument(drawTwice: false, hidden: true));

        MarkdownDocument visibleOnly = document.ToMarkdown();
        MarkdownDocument allLayers = document.ToMarkdown(new MarkdownExportOptions
        {
            IncludeHiddenLayers = true,
        });

        Assert.Equal("A square\n", visibleOnly.Text);
        Assert.Empty(visibleOnly.Images);
        Assert.Contains(visibleOnly.Diagnostics, warning => warning.Kind == MarkdownExportWarningKind.ImageSkipped);
        Assert.Equal("![A square](media/image1.png)\n", allLayers.Text);
        Assert.Single(allLayers.Images);
    }

    [Fact]
    public void AppliesOptionalContentVisibilityDeclaredOnTheImageXObject()
    {
        using PdfDocument document = PdfDocument.Load(
            BuildTaggedImageDocument(drawTwice: false, hiddenImageObject: true));

        MarkdownDocument visibleOnly = document.ToMarkdown();
        MarkdownDocument allLayers = document.ToMarkdown(new MarkdownExportOptions
        {
            IncludeHiddenLayers = true,
        });

        Assert.Equal("A square\n", visibleOnly.Text);
        Assert.Empty(visibleOnly.Images);
        Assert.Contains(visibleOnly.Diagnostics, warning => warning.Kind == MarkdownExportWarningKind.ImageSkipped);
        Assert.Equal("![A square](media/image1.png)\n", allLayers.Text);
        Assert.Single(allLayers.Images);
    }

    [Fact]
    public void UsesActualTextFromMarkedContentProperties()
    {
        using PdfDocument document = PdfDocument.Load(BuildMarkedActualTextDocument());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("semantic \\[text\\]\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void UsesActualTextFromNestedSpanWithoutItsOwnMcid()
    {
        using PdfDocument document = PdfDocument.Load(BuildNestedSpanActualTextDocument());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("Drucker\n", markdown.Text);
        Assert.Empty(markdown.Diagnostics);
    }

    [Fact]
    public void DoesNotLeakActualTextFromHiddenMarkedContent()
    {
        using PdfDocument document = PdfDocument.Load(BuildMarkedActualTextDocument(hidden: true));

        MarkdownDocument visibleOnly = document.ToMarkdown();
        MarkdownDocument allLayers = document.ToMarkdown(new MarkdownExportOptions
        {
            IncludeHiddenLayers = true,
        });

        Assert.Equal("\n", visibleOnly.Text);
        Assert.Contains(visibleOnly.Diagnostics, warning => warning.Kind == MarkdownExportWarningKind.ContentSkipped);
        Assert.Equal("semantic \\[text\\]\n", allLayers.Text);
    }

    [Fact]
    public async Task SaveAsyncWritesEveryReferencedSidecarInsideTheMediaDirectory()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedImageDocument(drawTwice: false));
        MarkdownDocument markdown = document.ToMarkdown(new MarkdownExportOptions
        {
            MediaDirectoryName = "assets/pdf images",
        });
        DirectoryInfo directory = Directory.CreateTempSubdirectory("inkwright-md-image-");

        try
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await markdown.SaveAsync(directory.FullName, cancellationToken);

            string imagePath = Path.Combine(directory.FullName, "assets", "pdf images", "image1.png");
            Assert.True(File.Exists(imagePath));
            Assert.Equal(
                Assert.Single(markdown.Images).Content.ToArray(),
                await File.ReadAllBytesAsync(imagePath, cancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PreCancelledExportStopsBeforeRenderingOrCreatingTheDestination()
    {
        using PdfDocument document = PdfDocument.Load(MinimalPdf.OnePage());
        string destination = Path.Combine(Path.GetTempPath(), $"inkwright-cancel-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await document.ExportMarkdownAsync(
                destination,
                new MarkdownExportOptions { StructureMode = MarkdownStructureMode.RequireTagged },
                cancellation.Token));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void ReplacesNullCharactersInUntaggedActualText()
    {
        using PdfDocument document = PdfDocument.Load(BuildUntaggedActualTextWithNull());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("A\uFFFDB\n", markdown.Text);
        Assert.DoesNotContain('\0', markdown.Text);
    }

    [Fact]
    public void SkipsAMalformedPageAndContinuesWithLaterPages()
    {
        using PdfDocument document = PdfDocument.Load(BuildMalformedThenValidDocument());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal("survives\n", markdown.Text);
        Assert.Contains(markdown.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.ContentSkipped && warning.Subject == "Page 1");
    }

    [Fact]
    public async Task SaveAsyncRejectsAnExistingMediaDirectorySymlink()
    {
        using PdfDocument document = PdfDocument.Load(BuildTaggedImageDocument(drawTwice: false));
        MarkdownDocument markdown = document.ToMarkdown();
        DirectoryInfo root = Directory.CreateTempSubdirectory("inkwright-md-link-");
        DirectoryInfo outside = Directory.CreateTempSubdirectory("inkwright-md-outside-");
        string link = Path.Combine(root.FullName, "media");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            await Assert.ThrowsAsync<IOException>(async () =>
                await markdown.SaveAsync(root.FullName, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(outside.FullName, "image1.png")));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReportsStreamQualifiedMarkedContentCoverageMetrics()
    {
        using PdfDocument document = PdfDocument.Load(BuildStreamQualifiedMcidDocument());

        MarkdownDocument markdown = document.ToMarkdown();

        Assert.Equal(1, markdown.Metrics.SourcePages);
        Assert.Equal(0, markdown.Metrics.SkippedPages);
        Assert.Equal(3, markdown.Metrics.StructureElements);
        Assert.Equal(2, markdown.Metrics.MarkedContentReferences);
        Assert.Equal(2, markdown.Metrics.ResolvedMarkedContentReferences);
        Assert.Equal(1, markdown.Metrics.MarkedContentCoverage);
        Assert.Equal(markdown.Text.Length, markdown.Metrics.OutputCharacters);
        Assert.Equal(markdown.Diagnostics.Count, markdown.Metrics.Warnings);
    }

    [Fact]
    public void ReportsPageFailuresAndImageResourceLimitsInMetrics()
    {
        using PdfDocument malformed = PdfDocument.Load(BuildMalformedThenValidDocument());
        using PdfDocument image = PdfDocument.Load(BuildTaggedImageDocument(drawTwice: false));

        MarkdownDocument pages = malformed.ToMarkdown();
        MarkdownDocument limitedImages = image.ToMarkdown(new MarkdownExportOptions
        {
            MaximumImages = 0,
        });

        Assert.Equal(2, pages.Metrics.SourcePages);
        Assert.Equal(1, pages.Metrics.SkippedPages);
        Assert.Equal("A square\n", limitedImages.Text);
        Assert.Equal(0, limitedImages.Metrics.Images);
        Assert.Equal(1, limitedImages.Metrics.FailedImageResources);
        Assert.Contains(limitedImages.Diagnostics, warning =>
            warning.Kind == MarkdownExportWarningKind.ImageSkipped &&
            warning.Subject == "Image resource limit");
    }

    [Fact]
    public void EnforcesPageStructureAndOutputResourceLimits()
    {
        using PdfDocument pages = PdfDocument.Load(BuildMalformedThenValidDocument());
        using PdfDocument structure = PdfDocument.Load(BuildTaggedDocument());
        using PdfDocument output = PdfDocument.Load(MinimalPdf.OnePage());

        Assert.Throws<InvalidOperationException>(() => pages.ToMarkdown(new MarkdownExportOptions
        {
            MaximumPages = 1,
        }));
        Assert.Throws<InvalidOperationException>(() => structure.ToMarkdown(new MarkdownExportOptions
        {
            MaximumStructureElements = 3,
        }));
        Assert.Throws<InvalidOperationException>(() => output.ToMarkdown(new MarkdownExportOptions
        {
            MaximumOutputCharacters = 5,
        }));
    }

    private static byte[] BuildUntaggedLayoutDocument()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfFont font = document.Fonts.Helvetica;

        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Report title", 60, 740, font, 24);
            canvas.DrawText("First paragraph line continues on", 60, 700, font, 12);
            canvas.DrawText("its second line.", 60, 684, font, 12);
            canvas.DrawText("- First item", 70, 640, font, 12);
            canvas.DrawText("- Second item", 70, 622, font, 12);

            canvas.DrawText("Name", 60, 570, font, 12);
            canvas.DrawText("Score", 250, 570, font, 12);
            canvas.DrawText("Ada", 60, 550, font, 12);
            canvas.DrawText("10", 250, 550, font, 12);
            canvas.DrawText("Bob", 60, 530, font, 12);
            canvas.DrawText("20", 250, 530, font, 12);
            canvas.Commit();
        }

        return document.ToArray();
    }

    private static byte[] BuildUntaggedActualTextWithNull()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        AddHelvetica(document, page);
        page.SetContent(Encoding.ASCII.GetBytes(
            "/Span <</ActualText <FEFF004100000042>>> BDC " +
            "BT /F1 12 Tf 60 700 Td (wrong) Tj ET EMC\n"));
        return document.ToArray();
    }

    private static byte[] BuildMalformedThenValidDocument()
    {
        using var document = PdfDocument.Create();
        PdfPage malformed = document.Pages.Add();
        PdfStream invalidFlate = PdfStream.FromData(new byte[] { 0x00, 0x01, 0x02 }, compress: false);
        invalidFlate.Dictionary.Set(PdfNames.Filter, PdfValue.Name("MalformedFilter"));
        var invalidParameters = new PdfDictionary(4);
        invalidParameters.Set(PdfNames.Predictor, PdfValue.Integer(12));
        invalidParameters.Set(PdfNames.Colors, PdfValue.Integer(32));
        invalidParameters.Set(PdfNames.BitsPerComponent, PdfValue.Integer(16));
        invalidParameters.Set(PdfNames.Columns, PdfValue.Integer(int.MaxValue));
        invalidFlate.Dictionary.Set(PdfNames.DecodeParms, invalidParameters);
        malformed.Dictionary.Set(
            PdfNames.Contents,
            PdfValue.Reference(document.AddObject(invalidFlate)));
        PdfPage valid = document.Pages.Add();
        AddHelvetica(document, valid);
        valid.SetContent(Encoding.ASCII.GetBytes("BT /F1 12 Tf 60 700 Td (survives) Tj ET\n"));
        return document.ToArray();
    }

    private static void AddHelvetica(PdfDocument document, PdfPage page)
    {
        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name("Font"));
        font.Set(PdfNames.Subtype, PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(font)));
        page.Resources.Set(PdfName.Get("Font"), fonts);
    }

    private static byte[] BuildTaggedDocument()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name("Font"));
        font.Set(PdfNames.Subtype, PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(font)));
        page.Resources.Set(PdfName.Get("Font"), fonts);

        const string content =
            "/H1 <</MCID 0>> BDC BT /F1 18 Tf 60 750 Td (A heading) Tj ET EMC\n" +
            "/P <</MCID 1>> BDC BT /F1 12 Tf 60 700 Td (First *draft*) Tj ET EMC\n" +
            "/P <</MCID 2>> BDC BT /F1 12 Tf 60 680 Td (wrong glyphs) Tj ET EMC\n";
        page.SetContent(Encoding.ASCII.GetBytes(content));

        var roleMap = new PdfDictionary(1);
        roleMap.Set(PdfName.Get("ChapterTitle"), PdfValue.Name("H1"));
        var root = new PdfDictionary(3);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        root.Set(PdfNames.RoleMap, roleMap);
        PdfObjectId rootId = document.AddObject(root);

        PdfObjectId documentId = document.AddObject(PdfValue.Null);
        var children = new PdfArray(3);
        children.Add(PdfValue.Reference(AddElement(document, page, documentId, "ChapterTitle", 0)));
        children.Add(PdfValue.Reference(AddElement(document, page, documentId, "P", 1)));
        children.Add(PdfValue.Reference(AddElement(
            document,
            page,
            documentId,
            "P",
            2,
            actualText: "replacement [ok]")));

        var top = new PdfDictionary(5);
        top.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        top.Set(PdfNames.S, PdfValue.Name("Document"));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.K, children);
        document.SetObject(documentId, top);

        root.Set(PdfNames.K, PdfValue.Reference(documentId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static byte[] BuildTaggedImageDocument(
        bool drawTwice,
        bool throughForm = false,
        bool hidden = false,
        bool hiddenImageObject = false)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();
        PdfImage embedded = PdfImage.Load(document, TestImages.Png(2, 2, withAlpha: false));

        if (hidden || hiddenImageObject)
        {
            var group = new PdfDictionary(2);
            group.Set(PdfNames.Type, PdfValue.Name("OCG"));
            group.Set(PdfNames.Name, PdfValue.TextString("Hidden figures"));
            PdfValue groupReference = PdfValue.Reference(document.AddObject(group));
            var groups = new PdfArray(groupReference);
            var off = new PdfArray(groupReference);
            var configuration = new PdfDictionary(1);
            configuration.Set(PdfNames.OFF, off);
            var optionalContent = new PdfDictionary(2);
            optionalContent.Set(PdfNames.OCGs, groups);
            optionalContent.Set(PdfNames.D, configuration);
            document.Catalog.Set(PdfNames.OCProperties, optionalContent);

            if (hidden)
            {
                var properties = new PdfDictionary(1);
                properties.Set(PdfName.Get("Hidden"), groupReference);
                page.Resources.Set(PdfName.Get("Properties"), properties);
            }

            if (hiddenImageObject)
                document.GetObject(embedded.Reference.AsReference).AsStream!.Dictionary.Set(PdfNames.OC, groupReference);
        }

        var objects = new PdfDictionary(1);
        string drawnName;
        if (throughForm)
        {
            var formObjects = new PdfDictionary(1);
            formObjects.Set(PdfName.Get("Im0"), embedded.Reference);
            var formResources = new PdfDictionary(1);
            formResources.Set(PdfNames.XObject, formObjects);
            PdfStream form = PdfStream.FromData(Encoding.ASCII.GetBytes("/Im0 Do\n"), compress: false);
            form.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
            form.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Form));
            form.Dictionary.Set(PdfNames.BBox, PdfArray.OfNumbers(0, 0, 1, 1));
            form.Dictionary.Set(PdfNames.Resources, formResources);
            objects.Set(PdfName.Get("Fm0"), PdfValue.Reference(document.AddObject(form)));
            drawnName = "Fm0";
        }
        else
        {
            objects.Set(PdfName.Get("Im0"), embedded.Reference);
            drawnName = "Im0";
        }
        page.Resources.Set(PdfNames.XObject, objects);

        var content = new StringBuilder();
        if (hidden)
            content.Append("/OC /Hidden BDC ");
        content.Append($"/Figure <</MCID 0>> BDC q 20 0 0 20 50 700 cm /{drawnName} Do Q EMC\n");
        if (drawTwice)
            content.Append($"/Figure <</MCID 1>> BDC q 20 0 0 20 100 700 cm /{drawnName} Do Q EMC\n");
        if (hidden)
            content.Append("EMC\n");
        page.SetContent(Encoding.ASCII.GetBytes(content.ToString()));

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(root);
        PdfObjectId documentId = document.AddObject(PdfValue.Null);

        var figures = new PdfArray(drawTwice ? 2 : 1);
        figures.Add(PdfValue.Reference(AddFigure(document, page, documentId, 0, "A square")));
        if (drawTwice)
            figures.Add(PdfValue.Reference(AddFigure(document, page, documentId, 1, "The same square")));

        var top = new PdfDictionary(4);
        top.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        top.Set(PdfNames.S, PdfValue.Name("Document"));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.K, figures);
        document.SetObject(documentId, top);

        root.Set(PdfNames.K, PdfValue.Reference(documentId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static byte[] BuildMarkedActualTextDocument(bool hidden = false)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name("Font"));
        font.Set(PdfNames.Subtype, PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(font)));
        page.Resources.Set(PdfName.Get("Font"), fonts);

        if (hidden)
        {
            var group = new PdfDictionary(2);
            group.Set(PdfNames.Type, PdfValue.Name("OCG"));
            group.Set(PdfNames.Name, PdfValue.TextString("Hidden text"));
            PdfValue groupReference = PdfValue.Reference(document.AddObject(group));
            var configuration = new PdfDictionary(1);
            configuration.Set(PdfNames.OFF, new PdfArray(groupReference));
            var optionalContent = new PdfDictionary(2);
            optionalContent.Set(PdfNames.OCGs, new PdfArray(groupReference));
            optionalContent.Set(PdfNames.D, configuration);
            document.Catalog.Set(PdfNames.OCProperties, optionalContent);
            var properties = new PdfDictionary(1);
            properties.Set(PdfName.Get("Hidden"), groupReference);
            page.Resources.Set(PdfName.Get("Properties"), properties);
        }

        string prefix = hidden ? "/OC /Hidden BDC " : string.Empty;
        string suffix = hidden ? " EMC" : string.Empty;
        page.SetContent(Encoding.ASCII.GetBytes(
            prefix + "/P <</MCID 0 /ActualText (semantic [text])>> BDC " +
            "BT /F1 12 Tf 60 700 Td (wrong glyphs) Tj ET EMC" + suffix + "\n"));

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(root);
        PdfObjectId documentId = document.AddObject(PdfValue.Null);
        PdfObjectId paragraphId = AddElement(document, page, documentId, "P", 0);

        var top = new PdfDictionary(4);
        top.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        top.Set(PdfNames.S, PdfValue.Name("Document"));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.K, PdfValue.Reference(paragraphId));
        document.SetObject(documentId, top);
        root.Set(PdfNames.K, PdfValue.Reference(documentId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static byte[] BuildNestedSpanActualTextDocument()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name("Font"));
        font.Set(PdfNames.Subtype, PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(font)));
        page.Resources.Set(PdfName.Get("Font"), fonts);

        page.SetContent(Encoding.ASCII.GetBytes(
            "/P <</MCID 0>> BDC BT /F1 12 Tf 60 700 Td " +
            "(Dru) Tj /Span <</ActualText (c)>> BDC (k-) Tj EMC (ker) Tj ET EMC\n"));

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(root);
        PdfObjectId documentId = document.AddObject(PdfValue.Null);
        PdfObjectId paragraphId = AddElement(document, page, documentId, "P", 0);

        var top = new PdfDictionary(4);
        top.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        top.Set(PdfNames.S, PdfValue.Name("Document"));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.K, PdfValue.Reference(paragraphId));
        document.SetObject(documentId, top);
        root.Set(PdfNames.K, PdfValue.Reference(documentId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static byte[] BuildStreamQualifiedMcidDocument()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add();

        var font = new PdfDictionary(3);
        font.Set(PdfNames.Type, PdfValue.Name("Font"));
        font.Set(PdfNames.Subtype, PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(font)));
        page.Resources.Set(PdfName.Get("Font"), fonts);

        PdfStream form = PdfStream.FromData(Encoding.ASCII.GetBytes(
            "/P <</MCID 0>> BDC BT /F1 12 Tf 60 650 Td (form text) Tj ET EMC\n"), compress: false);
        form.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        form.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Form));
        form.Dictionary.Set(PdfNames.BBox, PdfArray.OfNumbers(0, 0, 300, 700));
        PdfObjectId formId = document.AddObject(form);
        var objects = new PdfDictionary(1);
        objects.Set(PdfName.Get("Fm0"), PdfValue.Reference(formId));
        page.Resources.Set(PdfNames.XObject, objects);
        page.SetContent(Encoding.ASCII.GetBytes(
            "/P <</MCID 0>> BDC BT /F1 12 Tf 60 700 Td (page text) Tj ET EMC\n/Fm0 Do\n"));

        var root = new PdfDictionary(2);
        root.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructTreeRoot));
        PdfObjectId rootId = document.AddObject(root);
        PdfObjectId documentId = document.AddObject(PdfValue.Null);
        PdfObjectId pageParagraph = AddElement(document, page, documentId, "P", 0);

        var mcr = new PdfDictionary(4);
        mcr.Set(PdfNames.Type, PdfValue.Name("MCR"));
        mcr.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        mcr.Set(PdfName.Get("Stm"), PdfValue.Reference(formId));
        mcr.Set(PdfNames.MCID, PdfValue.Integer(0));
        var formParagraph = new PdfDictionary(5);
        formParagraph.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        formParagraph.Set(PdfNames.S, PdfValue.Name("P"));
        formParagraph.Set(PdfNames.P, PdfValue.Reference(documentId));
        formParagraph.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        formParagraph.Set(PdfNames.K, mcr);
        PdfObjectId formParagraphId = document.AddObject(formParagraph);

        var top = new PdfDictionary(4);
        top.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        top.Set(PdfNames.S, PdfValue.Name("Document"));
        top.Set(PdfNames.P, PdfValue.Reference(rootId));
        top.Set(PdfNames.K, new PdfArray(
            PdfValue.Reference(pageParagraph),
            PdfValue.Reference(formParagraphId)));
        document.SetObject(documentId, top);
        root.Set(PdfNames.K, PdfValue.Reference(documentId));
        document.Catalog.Set(PdfNames.StructTreeRoot, PdfValue.Reference(rootId));
        return document.ToArray();
    }

    private static PdfObjectId AddFigure(
        PdfDocument document,
        PdfPage page,
        PdfObjectId parent,
        int mcid,
        string alternateText)
    {
        var figure = new PdfDictionary(6);
        figure.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        figure.Set(PdfNames.S, PdfValue.Name("Figure"));
        figure.Set(PdfNames.P, PdfValue.Reference(parent));
        figure.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        figure.Set(PdfNames.K, PdfValue.Integer(mcid));
        figure.Set(PdfNames.Alt, PdfValue.TextString(alternateText));
        return document.AddObject(figure);
    }

    private static PdfObjectId AddElement(
        PdfDocument document,
        PdfPage page,
        PdfObjectId parent,
        string tag,
        int mcid,
        string? actualText = null)
    {
        var element = new PdfDictionary(6);
        element.Set(PdfNames.Type, PdfValue.Name(PdfNames.StructElem));
        element.Set(PdfNames.S, PdfValue.Name(tag));
        element.Set(PdfNames.P, PdfValue.Reference(parent));
        element.Set(PdfNames.Pg, PdfValue.Reference(page.Id));
        element.Set(PdfNames.K, PdfValue.Integer(mcid));
        if (actualText is not null)
            element.Set(PdfNames.ActualText, PdfValue.TextString(actualText));

        return document.AddObject(element);
    }
}
