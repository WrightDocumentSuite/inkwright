namespace Inkwright.Layout;

/// <summary>
/// The archival conformance levels of ISO 19005. The part decides which rule book applies — part 1
/// is written against PDF 1.4 and forbids transparency, JPEG 2000 and attachments outright — and the
/// level decides how much of it: B is about how the page looks, U adds text that can be read back,
/// A adds a structure tree that says what the page means.
/// </summary>
public enum PdfAConformance
{
    /// <summary>PDF/A-1b: visual reproducibility, based on PDF 1.4.</summary>
    A1B,

    /// <summary>PDF/A-1a: as 1b, and tagged with a logical structure.</summary>
    A1A,

    /// <summary>PDF/A-2b: visual reproducibility, based on PDF 1.7.</summary>
    A2B,

    /// <summary>PDF/A-2u: as 2b, and all text has a Unicode mapping.</summary>
    A2U,

    /// <summary>PDF/A-2a: as 2u, and tagged with a logical structure.</summary>
    A2A,

    /// <summary>PDF/A-3b: as 2b, and arbitrary files may be attached.</summary>
    A3B,

    /// <summary>PDF/A-3u: as 3b, and all text has a Unicode mapping.</summary>
    A3U,

    /// <summary>PDF/A-3a: as 3u, and tagged with a logical structure.</summary>
    A3A,

    /// <summary>
    /// PDF/A-4: the first part written against PDF 2.0. It drops the A, U and B levels entirely —
    /// there is one base level, and the two below extend it rather than grade it.
    /// </summary>
    A4,

    /// <summary>
    /// PDF/A-4e: the engineering profile; 3D artwork and embedded files of any type are permitted
    /// by Annex B.
    /// </summary>
    A4E,

    /// <summary>PDF/A-4f: as PDF/A-4, and files of any type may be embedded (Annex A).</summary>
    A4F,
}

/// <summary>What each conformance level asks for, in the terms the rules are written in.</summary>
public static class PdfAConformanceExtensions
{
    extension(PdfAConformance conformance)
    {
        /// <summary>Which part of ISO 19005 applies: 1, 2, 3 or 4.</summary>
        public int Part => conformance switch
        {
            PdfAConformance.A1B or PdfAConformance.A1A => 1,
            PdfAConformance.A2B or PdfAConformance.A2U or PdfAConformance.A2A => 2,
            PdfAConformance.A4 or PdfAConformance.A4E or PdfAConformance.A4F => 4,
            _ => 3,
        };

        /// <summary>
        /// The conformance level letter: <c>A</c>, <c>U</c> or <c>B</c> for parts 1 to 3, and
        /// <c>E</c> or <c>F</c> for part 4. Plain PDF/A-4 has no letter at all, which is why this
        /// can be the null character — part 4 replaced grading with two optional extensions.
        /// </summary>
        public char Level => conformance switch
        {
            PdfAConformance.A1A or PdfAConformance.A2A or PdfAConformance.A3A => 'A',
            PdfAConformance.A2U or PdfAConformance.A3U => 'U',
            PdfAConformance.A4 => '\0',
            PdfAConformance.A4E => 'E',
            PdfAConformance.A4F => 'F',
            _ => 'B',
        };

        /// <summary>
        /// Whether every glyph shown must map to Unicode. Part 1 has no U level, but its level A
        /// carries the same requirement under a different clause. Part 4 requires it of nobody:
        /// 6.2.10.7 states the <c>ToUnicode</c> entry as a recommendation, not a requirement.
        /// </summary>
        public bool RequiresUnicode => conformance.Level is 'U' or 'A';

        /// <summary>
        /// Whether the document must carry a logical structure tree. Part 4 dropped the level that
        /// demanded one: its 6.8 recommends structure and points at ISO 14289 for the real rules.
        /// </summary>
        public bool RequiresStructure => conformance.Level == 'A';

        /// <summary>Whether 3D artwork is permitted, which only PDF/A-4e allows (Annex B).</summary>
        public bool AllowsThreeDimensional => conformance == PdfAConformance.A4E;

        /// <summary>
        /// Whether a file of any type may be embedded. Part 3 allows it at every level; part 4
        /// grants it to PDF/A-4f in Annex A and PDF/A-4e in Annex B. Only PDF/A-4f requires the
        /// EmbeddedFiles key to be present.
        /// </summary>
        public bool AllowsAnyEmbeddedFile =>
            conformance.Part == 3 || conformance is PdfAConformance.A4F or PdfAConformance.A4E;

        /// <summary>The name a person would write, such as <c>PDF/A-2b</c> or <c>PDF/A-4f</c>.</summary>
        public string Name => conformance.Level == '\0'
            ? "PDF/A-4"
            : $"PDF/A-{conformance.Part}{char.ToLowerInvariant(conformance.Level)}";
    }
}
