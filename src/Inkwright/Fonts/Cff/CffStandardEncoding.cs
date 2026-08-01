namespace Inkwright.Fonts.Cff;

/// <summary>
/// The standard encoding of Adobe Technical Note #5176 Appendix B: which glyph name each code from
/// 0 to 255 stands for. A composite glyph names its base and its accent by these codes rather than
/// by glyph number, which is why a subsetter needs the table to work out what a composite depends
/// on.
/// </summary>
internal static class CffStandardEncoding
{
    /// <summary>
    /// The names by code. Codes the encoding leaves unassigned hold the empty string, which is how
    /// a lookup reports that nothing is there.
    /// </summary>
    private static readonly string[] Names = Build();

    /// <summary>The glyph name a code stands for, or the empty string when it stands for nothing.</summary>
    /// <param name="code">The code, 0 to 255.</param>
    internal static string NameOf(int code) => code >= 0 && code < Names.Length ? Names[code] : string.Empty;

    private static string[] Build()
    {
        var names = new string[256];
        Array.Fill(names, string.Empty);

        // Codes 32 to 126 follow the printable ASCII order of the standard strings, and the
        // standard strings were laid out in exactly that order, so one run covers the lot.
        for (int code = 32; code <= 126; code++)
            names[code] = CffCharset.StandardStrings[code - 31];

        // The upper half is sparse and irregular, so it is listed outright.
        (int Code, string Name)[] upper =
        [
            (161, "exclamdown"), (162, "cent"), (163, "sterling"), (164, "fraction"), (165, "yen"),
            (166, "florin"), (167, "section"), (168, "currency"), (169, "quotesingle"),
            (170, "quotedblleft"), (171, "guillemotleft"), (172, "guilsinglleft"),
            (173, "guilsinglright"), (174, "fi"), (175, "fl"), (177, "endash"), (178, "dagger"),
            (179, "daggerdbl"), (180, "periodcentered"), (182, "paragraph"), (183, "bullet"),
            (184, "quotesinglbase"), (185, "quotedblbase"), (186, "quotedblright"),
            (187, "guillemotright"), (188, "ellipsis"), (189, "perthousand"), (191, "questiondown"),
            (193, "grave"), (194, "acute"), (195, "circumflex"), (196, "tilde"), (197, "macron"),
            (198, "breve"), (199, "dotaccent"), (200, "dieresis"), (202, "ring"), (203, "cedilla"),
            (205, "hungarumlaut"), (206, "ogonek"), (207, "caron"), (208, "emdash"), (225, "AE"),
            (227, "ordfeminine"), (232, "Lslash"), (233, "Oslash"), (234, "OE"),
            (235, "ordmasculine"), (241, "ae"), (245, "dotlessi"), (248, "lslash"), (249, "oslash"),
            (250, "oe"), (251, "germandbls"),
        ];

        foreach ((int code, string name) in upper)
            names[code] = name;

        return names;
    }
}
