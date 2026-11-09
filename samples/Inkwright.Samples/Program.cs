using System.Globalization;
using Inkwright;
using Inkwright.Annotations;
using Inkwright.Content;
using Inkwright.Fonts;
using Inkwright.Forms;
using Inkwright.Images;
using Inkwright.Layout;
using Inkwright.Layout.Elements;
using Inkwright.Samples;

string output = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "samples");
Directory.CreateDirectory(output);

Console.WriteLine($"Writing samples to {output}");

Invoice.Write(Path.Combine(output, "invoice.pdf"));
LowLevel.Write(Path.Combine(output, "low-level.pdf"));
Editing.Write(Path.Combine(output, "invoice-stamped.pdf"), Path.Combine(output, "invoice.pdf"));
Review.Write(Path.Combine(output, "reviewed.pdf"), Path.Combine(output, "invoice.pdf"));
Review.Report(Path.Combine(output, "reviewed.pdf"));
Review.Exchange(
    Path.Combine(output, "round-1.xfdf"),
    Path.Combine(output, "reviewed.pdf"),
    Path.Combine(output, "invoice.pdf"));

Review.Flatten(Path.Combine(output, "reviewed-flat.pdf"), Path.Combine(output, "reviewed.pdf"));
Review.Strip(Path.Combine(output, "reviewed-clean.pdf"), Path.Combine(output, "reviewed.pdf"));
Extraction.Report(Path.Combine(output, "invoice.pdf"));
Forms.Write(Path.Combine(output, "application-form.pdf"));
Archival.Write(Path.Combine(output, "archival.pdf"));

await MergeAndSplit.RunAsync(output);
await NavigationAndAttachments.RunAsync(output);
await PasswordProtection.RunAsync(output);
Console.WriteLine("Done.");

namespace Inkwright.Samples
{
    /// <summary>A one-page invoice built with the layout engine: the everyday case.</summary>
    internal static class Invoice
    {
        internal static void Write(string path)
        {
            (string Description, int Quantity, decimal Price)[] lines =
            [
                ("Design review workshop", 2, 850m),
                ("Implementation, sprint 14", 10, 1200m),
                ("Implementation, sprint 15", 10, 1200m),
                ("Out-of-hours support", 3, 480m),
                ("Documentation and handover", 4, 620m),
            ];

            decimal total = lines.Sum(line => line.Quantity * line.Price);

            Report.Create()
                .Page(page =>
                {
                    page.Size(PageSize.A4);
                    page.Margin(45);
                    page.Tagged("en-GB");
                    page.DefaultTextStyle(style => style.FontSize(10).FontFamily("Helvetica"));

                    page.Header().Row(row =>
                    {
                        row.RelativeItem().Column(column =>
                        {
                            column.Item().Text("Northwind Consulting", TextStyle.Default.FontSize(20).Bold());
                            column.Item().Text("14 Wharf Road, London", TextStyle.Default.FontSize(9).FontColor("#666666"));
                        });

                        row.ConstantItem(160).AlignRight().Column(column =>
                        {
                            column.Item().Text("INVOICE", TextStyle.Default.FontSize(18).Bold().AlignRight());
                            column.Item().Text("2026-014", TextStyle.Default.FontSize(11).AlignRight());
                            column.Item().Text(
                                DateTime.Today.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")),
                                TextStyle.Default.FontSize(9).FontColor("#666666").AlignRight());
                        });
                    });

                    page.Content().PaddingVertical(18).Column(column =>
                    {
                        column.Spacing(14);

                        column.Item().Text("Billed to Contoso Ltd, 5 Harbour Way, Bristol");

                        column.Item().Table(table =>
                        {
                            table.Columns(
                                ColumnWidth.Fraction(5),
                                ColumnWidth.Fraction(1),
                                ColumnWidth.Fraction(2),
                                ColumnWidth.Fraction(2));

                            table.Grid(0.4, PdfColor.Gray(0.8f));
                            table.CellPadding(6);

                            table.Header()
                                .Background(PdfColor.Parse("#EFF3F8"))
                                .Text("Description", TextStyle.Default.Bold())
                                .Text("Qty", TextStyle.Default.Bold().AlignRight())
                                .Text("Unit", TextStyle.Default.Bold().AlignRight())
                                .Text("Amount", TextStyle.Default.Bold().AlignRight());

                            foreach ((string description, int quantity, decimal price) in lines)
                            {
                                table.Row()
                                    .Text(description)
                                    .Text(quantity.ToString(CultureInfo.InvariantCulture), TextStyle.Default.AlignRight())
                                    .Text(Money(price), TextStyle.Default.AlignRight())
                                    .Text(Money(quantity * price), TextStyle.Default.AlignRight());
                            }

                            table.Row()
                                .Background(PdfColor.Parse("#EFF3F8"))
                                .Text("Total", TextStyle.Default.Bold())
                                .Text(string.Empty)
                                .Text(string.Empty)
                                .Text(Money(total), TextStyle.Default.Bold().AlignRight());
                        });

                        column.Item().PaddingVertical(6).Line(0.5, PdfColor.Gray(0.8f));

                        column.Item().Text(text =>
                        {
                            text.Style(TextStyle.Default.FontSize(9).FontColor("#555555").Justify());
                            text.Span(
                                "Payment is due within 30 days. Late payment interest is charged at the statutory " +
                                "rate. Please quote the invoice number with your transfer so it can be matched " +
                                "against this account without anyone having to guess.");
                        });
                    });

                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.Style(TextStyle.Default.FontSize(8).FontColor("#888888"));
                        text.PageNumbers("Page {0} of {1}");
                    });
                })
                .Save(path);

            Console.WriteLine($"  invoice.pdf          {new FileInfo(path).Length,8:N0} bytes");
        }

        private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture) + " GBP";
    }

    /// <summary>Drawing straight onto a page, with no layout engine in the way.</summary>
    internal static class LowLevel
    {
        internal static void Write(string path)
        {
            using var document = PdfDocument.Create();
            PdfPage page = document.Pages.Add(PageSize.A5.Landscape);
            PdfFont font = document.Fonts.HelveticaBold;

            using (ContentCanvas canvas = ContentCanvas.ForPage(page))
            {
                canvas.FillColor(PdfColor.Parse("#10233F"))
                      .Rectangle(0, 0, page.Width, page.Height)
                      .Fill();

                for (int i = 0; i < 12; i++)
                {
                    double radius = 20 + (i * 12);
                    canvas.Save()
                          .Opacity(0.08 + (i * 0.02))
                          .StrokeColor(PdfColor.Parse("#7FD1FF"))
                          .LineWidth(1.2)
                          .Ellipse(new PdfRectangle(120 - radius, 150 - radius, 120 + radius, 150 + radius))
                          .Stroke()
                          .Restore();
                }

                canvas.DrawText("Inkwright", 240, 190, font, 34, PdfColor.White);
                canvas.DrawText("PDF, drawn directly", 240, 160, document.Fonts.Helvetica, 12, PdfColor.Parse("#7FD1FF"));
                canvas.Commit();
            }

            document.Info.Title = "Low-level drawing";
            document.Save(path);
            Console.WriteLine($"  low-level.pdf        {new FileInfo(path).Length,8:N0} bytes");
        }
    }

    /// <summary>Changing an existing file without disturbing the parts nobody touched.</summary>
    internal static class Editing
    {
        internal static void Write(string path, string sourcePath)
        {
            using PdfDocument document = PdfDocument.Load(sourcePath);
            PdfPage page = document.Pages[0];

            using (ContentCanvas canvas = ContentCanvas.ForPage(page))
            {
                canvas.Save()
                      .Opacity(0.16)
                      .Transform(PdfMatrix.Rotation(38) * PdfMatrix.Translation(140, 240))
                      .DrawText("PAID", 0, 0, document.Fonts.HelveticaBold, 96, PdfColor.Parse("#1B7F3B"))
                      .Restore();

                canvas.Commit();
            }

            document.Info.Keywords = "paid, settled";

            // An appended save leaves every original byte where it was.
            document.SaveIncremental(path);

            Console.WriteLine($"  invoice-stamped.pdf  {new FileInfo(path).Length,8:N0} bytes " +
                              $"(+{new FileInfo(path).Length - new FileInfo(sourcePath).Length:N0} appended)");
        }
    }

    /// <summary>A review pass over a finished document: comments, marks, replies and a decision.</summary>
    internal static class Review
    {
        internal static void Write(string path, string sourcePath)
        {
            using PdfDocument document = PdfDocument.Load(sourcePath);
            PdfPage page = document.Pages[0];

            // Marks are placed by naming the words, not by measuring the page.
            page.Annotations.MarkText(
                "Implementation, sprint 15",
                PdfAnnotationType.StrikeOut,
                "Moved to the next invoice.",
                "Priya Raman");

            page.Annotations.MarkText("Out-of-hours support", PdfAnnotationType.Highlight, author: "Priya Raman");

            PdfTextAnnotation question = page.Annotations.AddNote(
                new PdfPoint(510, 500), "Is the workshop rate still 850?", "Priya Raman", PdfNoteIcon.Help);

            question.Reply("Yes, unchanged since March.", "Tom Weir");
            question.SetReviewState(PdfReviewState.Completed, "Priya Raman");

            page.Annotations.AddRectangle(
                new PdfRectangle(45, 60, 550, 95), "Payment terms need the new wording.", "Priya Raman");

            page.Annotations.AddStamp(new PdfRectangle(400, 720, 550, 770), "ForComment", author: "Priya Raman");

            // A callout writes on the page and points at what it is about.
            page.Annotations.AddCallout(
                new PdfRectangle(300, 300, 520, 350),
                new PdfPoint(250, 430),
                "The total excludes the retainer.",
                "Priya Raman");

            page.Annotations.AddLine(new PdfPoint(60, 240), new PdfPoint(240, 240), "Cut here", "Priya Raman")
                .SetEndings(PdfLineEnding.None, PdfLineEnding.ClosedArrow);

            // Comments are appended, so a signature over the original bytes stays valid.
            document.SaveIncremental(path);

            int comments = document.Comments.Count();
            int replies = document.Comments.Sum(comment => comment.Replies.Count);
            Console.WriteLine($"  reviewed.pdf         {new FileInfo(path).Length,8:N0} bytes " +
                              $"({comments} comments, {replies} replies)");
        }

        internal static void Report(string sourcePath)
        {
            using PdfDocument document = PdfDocument.Load(sourcePath);

            foreach (PdfCommentInfo comment in document.ExportComments())
            {
                string about = comment.QuotedText is { Length: > 0 } quoted ? $" \"{Shorten(quoted)}\"" : string.Empty;
                Console.WriteLine(
                    $"    p{comment.PageIndex + 1} {comment.Type,-9} {comment.Author,-12}{about} — {comment.Contents}");

                foreach (PdfCommentInfo reply in comment.Replies)
                    Console.WriteLine($"      ↳ {reply.Author}: {reply.Contents} [{reply.State}]");
            }

            static string Shorten(string text) => text.Length <= 32 ? text : text[..29] + "...";
        }

        /// <summary>
        /// The round of review leaving on its own and arriving somewhere else. This is how comments
        /// travel between people who never share a copy of the document.
        /// </summary>
        internal static void Exchange(string reviewPath, string reviewedPath, string sourcePath)
        {
            using PdfDocument reviewed = PdfDocument.Load(reviewedPath);
            reviewed.ExportReview(reviewPath);

            using PdfDocument clean = PdfDocument.Load(sourcePath);
            PdfReviewImportResult result = clean.ImportReview(reviewPath);

            Console.WriteLine($"  round-1.xfdf         {new FileInfo(reviewPath).Length,8:N0} bytes ({result})");
        }

        internal static void Flatten(string path, string sourcePath)
        {
            using PdfDocument document = PdfDocument.Load(sourcePath);
            int flattened = document.FlattenComments();
            document.Save(path, new PdfSaveOptions { RemoveUnusedObjects = true });

            Console.WriteLine($"  reviewed-flat.pdf    {new FileInfo(path).Length,8:N0} bytes ({flattened} comments burnt in)");
        }

        internal static void Strip(string path, string sourcePath)
        {
            using PdfDocument document = PdfDocument.Load(sourcePath);
            int removed = document.RemoveAllComments();

            // Unlinking a comment leaves its dictionary and its drawing behind; pruning is what
            // actually takes the text out of the file.
            document.Save(path, new PdfSaveOptions { RemoveUnusedObjects = true });

            Console.WriteLine($"  reviewed-clean.pdf   {new FileInfo(path).Length,8:N0} bytes ({removed} annotations removed)");
        }
    }

    /// <summary>Reading text back out, with positions.</summary>
    internal static class Extraction
    {
        internal static void Report(string path)
        {
            using PdfDocument document = PdfDocument.Load(path);
            IReadOnlyList<Text.PdfLetter> letters = document.Pages[0].ExtractLetters();
            string text = document.Pages[0].ExtractText();

            string firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            Console.WriteLine($"  extracted            {letters.Count,8:N0} glyphs, first line: \"{firstLine}\"");
        }
    }

    /// <summary>Building a form, filling it in from a typed record, and flattening the result.</summary>
    internal static partial class Forms
    {
        [PdfForm]
        internal partial record Application(
            [property: PdfField("applicant.name")] string Name,
            [property: PdfField("applicant.email")] string Email,
            [property: PdfField("applicant.experience")] int Years,
            [property: PdfField("applicant.remote")] bool Remote);

        internal static void Write(string path)
        {
            using var document = PdfDocument.Create();
            PdfPage page = document.Pages.Add(PageSize.A4);
            PdfForm form = PdfForm.OpenOrCreate(document);
            PdfFont font = document.Fonts.Helvetica;

            using (ContentCanvas canvas = ContentCanvas.ForPage(page))
            {
                canvas.DrawText("Application", 55, 770, document.Fonts.HelveticaBold, 18);

                double y = 715;
                foreach (string label in (string[])["Name", "Email", "Years of experience", "Happy to work remotely"])
                {
                    canvas.DrawText(label, 55, y + 6, font, 10, PdfColor.Gray(0.35f));
                    y -= 48;
                }

                canvas.Commit();
            }

            form.AddTextField(page, PdfRectangle.FromSize(200, 710, 300, 22), "applicant.name");
            form.AddTextField(page, PdfRectangle.FromSize(200, 662, 300, 22), "applicant.email");
            form.AddTextField(page, PdfRectangle.FromSize(200, 614, 80, 22), "applicant.experience");
            form.AddCheckBox(page, PdfRectangle.FromSize(200, 568, 16, 16), "applicant.remote");

            form.Fill(new Application("Ada Lovelace", "ada@example.org", 12, true));
            document.Save(path);

            Console.WriteLine($"  application-form.pdf {new FileInfo(path).Length,8:N0} bytes, " +
                              $"fields: {string.Join(", ", form.ReadValues().Select(pair => $"{pair.Key}={pair.Value}"))}");
        }
    }

    /// <summary>An archival document with an embedded font, a structure tree and an invoice attachment.</summary>
    internal static class Archival
    {
        internal static void Write(string path)
        {
            string? fontPath = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
            if (fontPath is null)
            {
                Console.WriteLine("  archival.pdf         skipped: no embeddable font was found on this machine");
                return;
            }

            using var document = PdfDocument.Create();
            PdfFont font = document.Fonts.Embed(fontPath);

            Report.Create()
                .Page(page =>
                {
                    page.Size(PageSize.A4);
                    page.Margin(45);
                    page.Tagged("en-GB");
                    page.DefaultTextStyle(TextStyle.Default.Font(font).FontSize(11));
                    page.Content().Column(column =>
                    {
                        column.Spacing(10);
                        column.Item().Text("Archival invoice", TextStyle.Default.Font(font).FontSize(18).Bold());
                        column.Item().Text(
                            "This file claims PDF/A-3a: the font travels with it, the colour space is pinned " +
                            "by an output intent, the pages carry a structure tree, and the machine-readable " +
                            "invoice is attached alongside.");
                    });
                })
                .Render(document);

            document.Info.Title = "Archival invoice";
            document.Info.Author = "Northwind Consulting";

            document.Attachments.AddFacturX(System.Text.Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rsm:CrossIndustryInvoice " +
                "xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\" />"));

            // Applying the profile validates as well as converts: a claim the document cannot keep
            // is refused here rather than by an auditor a decade from now.
            PdfAProfile.Apply(document, PdfAConformance.A3A, new PdfAOptions { Language = "en-GB" });
            document.Save(path);

            IReadOnlyList<PdfAProblem> problems = PdfAProfile.ValidateFile(path, PdfAConformance.A3A);

            Console.WriteLine($"  archival.pdf         {new FileInfo(path).Length,8:N0} bytes, " +
                              $"attachments: {string.Join(", ", document.Attachments.Names)}, " +
                              $"PDF/A-3a problems: {problems.Count}");
        }
    }
}
