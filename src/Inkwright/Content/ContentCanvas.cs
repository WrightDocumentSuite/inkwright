using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright.Content;

/// <summary>How the edges of a stroked line are shaped (ISO 32000-1 §8.4.3.3).</summary>
public enum PdfLineCap
{
    /// <summary>The stroke stops squarely at the end point.</summary>
    Butt = 0,

    /// <summary>A half circle caps the end point.</summary>
    Round = 1,

    /// <summary>A square extends half the line width past the end point.</summary>
    Square = 2,
}

/// <summary>How corners between stroked segments are shaped (ISO 32000-1 §8.4.3.4).</summary>
public enum PdfLineJoin
{
    /// <summary>The outer edges meet at a point.</summary>
    Miter = 0,

    /// <summary>A circular arc rounds the corner.</summary>
    Round = 1,

    /// <summary>The corner is cut off square.</summary>
    Bevel = 2,
}

/// <summary>How glyphs are painted (ISO 32000-1 §9.3.6).</summary>
public enum PdfTextRenderMode
{
    /// <summary>Fill the glyphs.</summary>
    Fill = 0,

    /// <summary>Stroke the glyph outlines.</summary>
    Stroke = 1,

    /// <summary>Fill and stroke.</summary>
    FillStroke = 2,

    /// <summary>Paint nothing, which is how searchable text is put behind a scan.</summary>
    Invisible = 3,

    /// <summary>Fill and add to the clipping path.</summary>
    FillClip = 4,

    /// <summary>Stroke and add to the clipping path.</summary>
    StrokeClip = 5,

    /// <summary>Fill, stroke and add to the clipping path.</summary>
    FillStrokeClip = 6,

    /// <summary>Add to the clipping path only.</summary>
    Clip = 7,
}

/// <summary>
/// Builds a content stream. Every method writes operators straight into a UTF-8 buffer, so drawing
/// a page never builds an intermediate scene graph; the canvas is a pen, not a document model.
/// </summary>
public sealed class ContentCanvas : IDisposable
{
    private readonly MemoryStream _buffer = new(1024);
    private readonly Utf8Writer _writer;
    private readonly PdfPage? _page;
    private int _depth;
    private bool _inText;
    private Fonts.PdfFont? _currentFont;
    private double _currentSize;

    private ContentCanvas(PdfDocument document, PdfPage? page, PdfResources resources)
    {
        Document = document;
        _page = page;
        Resources = resources;
        _writer = new Utf8Writer(_buffer);
    }

    /// <summary>The document the canvas draws into.</summary>
    public PdfDocument Document { get; }

    /// <summary>The resources the operators refer to by name.</summary>
    public PdfResources Resources { get; }

    /// <summary>Starts drawing on a page, adding to whatever the page already holds.</summary>
    /// <param name="page">The page to draw on.</param>
    public static ContentCanvas ForPage(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new ContentCanvas(page.Document, page, new PdfResources(page.Document, page.Resources));
    }

    /// <summary>Starts drawing into a standalone stream, such as a form or an appearance.</summary>
    /// <param name="document">The document that will own the stream.</param>
    /// <param name="resources">The resource dictionary the operators refer to.</param>
    public static ContentCanvas ForStream(PdfDocument document, PdfDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resources);
        return new ContentCanvas(document, null, new PdfResources(document, resources));
    }

    /// <summary>The operators written so far.</summary>
    public byte[] ToArray()
    {
        _writer.Flush();
        return _buffer.ToArray();
    }

    /// <summary>Writes the operators onto the page the canvas was created for.</summary>
    /// <param name="append">Whether to add to the existing content rather than replace it.</param>
    public void Commit(bool append = true)
    {
        if (_page is null)
            throw new InvalidOperationException("This canvas is not attached to a page.");

        while (_depth > 0)
            Restore();

        if (_inText)
            EndText();

        byte[] content = ToArray();
        if (append)
            _page.AppendContent(content);
        else
            _page.SetContent(content);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writer.Dispose();
        _buffer.Dispose();
    }

    /// <summary>Saves the graphics state.</summary>
    public ContentCanvas Save()
    {
        _depth++;
        return Op(PdfOperator.SaveState);
    }

    /// <summary>Restores the graphics state saved most recently.</summary>
    public ContentCanvas Restore()
    {
        if (_depth > 0)
            _depth--;

        return Op(PdfOperator.RestoreState);
    }

    /// <summary>Concatenates a transformation onto the current one.</summary>
    /// <param name="matrix">The transformation to apply before everything already in effect.</param>
    public ContentCanvas Transform(PdfMatrix matrix) =>
        Numbers(PdfOperator.ConcatMatrix, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);

    /// <summary>Moves the origin.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset.</param>
    public ContentCanvas Translate(double dx, double dy) => Transform(PdfMatrix.Translation(dx, dy));

    /// <summary>Scales about the origin.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    public ContentCanvas Scale(double sx, double sy) => Transform(PdfMatrix.Scaling(sx, sy));

    /// <summary>Rotates counter-clockwise about the origin.</summary>
    /// <param name="degrees">The angle in degrees.</param>
    public ContentCanvas Rotate(double degrees) => Transform(PdfMatrix.Rotation(degrees));

    /// <summary>Sets the stroke width in user-space units.</summary>
    /// <param name="width">The line width; zero means the thinnest line the device can draw.</param>
    public ContentCanvas LineWidth(double width) => Numbers(PdfOperator.SetLineWidth, width);

    /// <summary>Sets the shape of line ends.</summary>
    /// <param name="cap">The cap style.</param>
    public ContentCanvas LineCap(PdfLineCap cap) => Numbers(PdfOperator.SetLineCap, (int)cap);

    /// <summary>Sets the shape of corners.</summary>
    /// <param name="join">The join style.</param>
    public ContentCanvas LineJoin(PdfLineJoin join) => Numbers(PdfOperator.SetLineJoin, (int)join);

    /// <summary>Sets the miter limit.</summary>
    /// <param name="limit">The ratio at which a miter turns into a bevel.</param>
    public ContentCanvas MiterLimit(double limit) => Numbers(PdfOperator.SetMiterLimit, limit);

    /// <summary>Sets the dash pattern.</summary>
    /// <param name="phase">How far into the pattern the line starts.</param>
    /// <param name="pattern">Alternating dash and gap lengths; empty means a solid line.</param>
    public ContentCanvas Dash(double phase, params ReadOnlySpan<double> pattern)
    {
        _writer.WriteByte((byte)'[');
        for (int i = 0; i < pattern.Length; i++)
        {
            if (i > 0)
                _writer.WriteSpace();

            _writer.Write(pattern[i]);
        }

        _writer.WriteByte((byte)']');
        _writer.WriteSpace();
        _writer.Write(phase);
        _writer.WriteSpace();
        return Op(PdfOperator.SetDash);
    }

    /// <summary>Sets the colour used for filling.</summary>
    /// <param name="color">The colour.</param>
    public ContentCanvas FillColor(PdfColor color) => Color(color, stroking: false);

    /// <summary>Sets the colour used for stroking.</summary>
    /// <param name="color">The colour.</param>
    public ContentCanvas StrokeColor(PdfColor color) => Color(color, stroking: true);

    /// <summary>
    /// Sets constant alpha for fills and strokes through a graphics state dictionary, which is the
    /// only way PDF expresses transparency.
    /// </summary>
    /// <param name="fill">The alpha applied to fills, from zero to one.</param>
    /// <param name="stroke">The alpha applied to strokes; the fill value when omitted.</param>
    public ContentCanvas Opacity(double fill, double? stroke = null)
    {
        var state = new PdfDictionary(3);
        state.Set(PdfNames.Type, PdfValue.Name(PdfNames.ExtGState));
        state.Set(PdfNames.ca, PdfValue.Number(Math.Clamp(fill, 0, 1)));
        state.Set(PdfNames.CA, PdfValue.Number(Math.Clamp(stroke ?? fill, 0, 1)));

        return ExtGState(state);
    }

    /// <summary>
    /// Turns black point compensation on or off for what follows, which PDF 2.0 added as the
    /// <c>UseBlackPtComp</c> entry of a graphics state parameter dictionary.
    /// </summary>
    /// <param name="setting">Whether to compensate, or to leave the decision to the reader.</param>
    /// <remarks>
    /// Compensation lines the darkest colour a document can describe up with the darkest colour the
    /// output device can actually produce, which keeps shadow detail from collapsing into flat
    /// black. It is useful for photographs and harmful for colours that have to come out exactly
    /// right, such as a brand colour, which is why it is set per graphics object rather than once
    /// per file. PDF 2.0 Application Note 001 discusses the trade-off.
    /// </remarks>
    public ContentCanvas BlackPointCompensation(PdfBlackPointCompensation setting)
    {
        var state = new PdfDictionary(2);
        state.Set(PdfNames.Type, PdfValue.Name(PdfNames.ExtGState));
        state.Set(PdfName.Get("UseBlackPtComp"), PdfValue.Name(PdfName.Get(setting switch
        {
            PdfBlackPointCompensation.On => "ON",
            PdfBlackPointCompensation.Off => "OFF",
            _ => "Default",
        })));

        return ExtGState(state);
    }

    /// <summary>Applies a graphics state parameter dictionary.</summary>
    /// <param name="state">The state to apply.</param>
    public ContentCanvas ExtGState(PdfDictionary state)
    {
        PdfName name = Resources.AddExtGState(state);
        Name(name);
        return Op(PdfOperator.SetExtGState);
    }

    /// <summary>Starts a new subpath.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    public ContentCanvas MoveTo(double x, double y) => Numbers(PdfOperator.MoveTo, x, y);

    /// <summary>Appends a straight segment.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    public ContentCanvas LineTo(double x, double y) => Numbers(PdfOperator.LineTo, x, y);

    /// <summary>Appends a cubic Bézier segment.</summary>
    /// <param name="x1">The first control point's horizontal coordinate.</param>
    /// <param name="y1">The first control point's vertical coordinate.</param>
    /// <param name="x2">The second control point's horizontal coordinate.</param>
    /// <param name="y2">The second control point's vertical coordinate.</param>
    /// <param name="x3">The end point's horizontal coordinate.</param>
    /// <param name="y3">The end point's vertical coordinate.</param>
    public ContentCanvas CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Numbers(PdfOperator.CurveTo, x1, y1, x2, y2, x3, y3);

    /// <summary>Closes the current subpath.</summary>
    public ContentCanvas ClosePath() => Op(PdfOperator.ClosePath);

    /// <summary>Appends a rectangle as its own subpath.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public ContentCanvas Rectangle(double x, double y, double width, double height) =>
        Numbers(PdfOperator.Rectangle, x, y, width, height);

    /// <summary>Appends a rectangle as its own subpath.</summary>
    /// <param name="rectangle">The rectangle.</param>
    public ContentCanvas Rectangle(PdfRectangle rectangle) =>
        Rectangle(rectangle.Left, rectangle.Bottom, rectangle.Width, rectangle.Height);

    /// <summary>Appends a rectangle with rounded corners.</summary>
    /// <param name="rectangle">The rectangle.</param>
    /// <param name="radius">The corner radius, clamped to half the shorter side.</param>
    public ContentCanvas RoundedRectangle(PdfRectangle rectangle, double radius)
    {
        double r = Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (r <= 0)
            return Rectangle(rectangle);

        const double Kappa = 0.5522847498;
        double c = r * Kappa;
        double left = rectangle.Left;
        double bottom = rectangle.Bottom;
        double right = rectangle.Right;
        double top = rectangle.Top;

        MoveTo(left + r, bottom);
        LineTo(right - r, bottom);
        CurveTo(right - r + c, bottom, right, bottom + r - c, right, bottom + r);
        LineTo(right, top - r);
        CurveTo(right, top - r + c, right - r + c, top, right - r, top);
        LineTo(left + r, top);
        CurveTo(left + r - c, top, left, top - r + c, left, top - r);
        LineTo(left, bottom + r);
        CurveTo(left, bottom + r - c, left + r - c, bottom, left + r, bottom);
        return ClosePath();
    }

    /// <summary>Appends an ellipse inscribed in a rectangle.</summary>
    /// <param name="bounds">The rectangle the ellipse fits into.</param>
    public ContentCanvas Ellipse(PdfRectangle bounds)
    {
        const double Kappa = 0.5522847498;
        double rx = bounds.Width / 2;
        double ry = bounds.Height / 2;
        double cx = bounds.Left + rx;
        double cy = bounds.Bottom + ry;
        double ox = rx * Kappa;
        double oy = ry * Kappa;

        MoveTo(cx - rx, cy);
        CurveTo(cx - rx, cy + oy, cx - ox, cy + ry, cx, cy + ry);
        CurveTo(cx + ox, cy + ry, cx + rx, cy + oy, cx + rx, cy);
        CurveTo(cx + rx, cy - oy, cx + ox, cy - ry, cx, cy - ry);
        CurveTo(cx - ox, cy - ry, cx - rx, cy - oy, cx - rx, cy);
        return ClosePath();
    }

    /// <summary>Strokes the current path.</summary>
    public ContentCanvas Stroke() => Op(PdfOperator.Stroke);

    /// <summary>Fills the current path with the nonzero winding rule.</summary>
    public ContentCanvas Fill() => Op(PdfOperator.Fill);

    /// <summary>Fills the current path with the even-odd rule.</summary>
    public ContentCanvas FillEvenOdd() => Op(PdfOperator.FillEvenOdd);

    /// <summary>Fills and then strokes the current path.</summary>
    public ContentCanvas FillStroke() => Op(PdfOperator.FillStroke);

    /// <summary>Ends the path without painting it.</summary>
    public ContentCanvas EndPath() => Op(PdfOperator.EndPath);

    /// <summary>Uses the current path as a clip for everything that follows.</summary>
    /// <param name="evenOdd">Whether to use the even-odd rule instead of nonzero winding.</param>
    public ContentCanvas Clip(bool evenOdd = false)
    {
        Op(evenOdd ? PdfOperator.ClipEvenOdd : PdfOperator.Clip);
        return Op(PdfOperator.EndPath);
    }

    /// <summary>Clips everything that follows to a rectangle.</summary>
    /// <param name="rectangle">The visible area.</param>
    public ContentCanvas ClipTo(PdfRectangle rectangle)
    {
        Rectangle(rectangle);
        return Clip();
    }

    /// <summary>Begins a text object.</summary>
    public ContentCanvas BeginText()
    {
        _inText = true;
        return Op(PdfOperator.BeginText);
    }

    /// <summary>Ends the text object.</summary>
    public ContentCanvas EndText()
    {
        _inText = false;
        return Op(PdfOperator.EndText);
    }

    /// <summary>Selects a font by the name it has in the resource dictionary.</summary>
    /// <param name="font">The resource name of the font.</param>
    /// <param name="size">The size in points.</param>
    public ContentCanvas Font(PdfName font, double size)
    {
        ArgumentNullException.ThrowIfNull(font);
        Name(font);
        return Numbers(PdfOperator.SetFont, size);
    }

    /// <summary>Selects a font, registering it as a resource if it is not there yet.</summary>
    /// <param name="font">The font to draw with.</param>
    /// <param name="size">The size in points.</param>
    public ContentCanvas Font(Fonts.PdfFont font, double size)
    {
        ArgumentNullException.ThrowIfNull(font);

        _currentFont = font;
        _currentSize = size;
        return Font(Resources.AddFont(font.Reference), size);
    }

    /// <summary>Shows text, encoding it for the font selected most recently.</summary>
    /// <param name="text">The text to show.</param>
    /// <exception cref="InvalidOperationException">No font has been selected.</exception>
    public ContentCanvas ShowText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_currentFont is null)
            throw new InvalidOperationException("Select a font before showing text.");

        return ShowText(_currentFont.Encode(text));
    }

    /// <summary>
    /// Draws a single run of text at a position, opening and closing the text object around it.
    /// The position is the left end of the baseline.
    /// </summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="x">The horizontal position of the baseline start.</param>
    /// <param name="y">The vertical position of the baseline.</param>
    /// <param name="font">The font to draw with.</param>
    /// <param name="size">The size in points.</param>
    /// <param name="color">The fill colour, or the colour already in effect when omitted.</param>
    public ContentCanvas DrawText(
        string text, double x, double y, Fonts.PdfFont font, double size, PdfColor? color = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);

        BeginText();
        if (color is { } fill)
            FillColor(fill);

        Font(font, size);
        TextMatrix(PdfMatrix.Translation(x, y));
        ShowText(text);
        return EndText();
    }

    /// <summary>Sets the text matrix, which places and orients the text.</summary>
    /// <param name="matrix">The matrix.</param>
    public ContentCanvas TextMatrix(PdfMatrix matrix) =>
        Numbers(PdfOperator.SetTextMatrix, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);

    /// <summary>Moves to the start of the next line, offset from the current line's start.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset.</param>
    public ContentCanvas MoveText(double dx, double dy) => Numbers(PdfOperator.TextMove, dx, dy);

    /// <summary>Sets the distance between the baselines of consecutive lines.</summary>
    /// <param name="leading">The distance in points.</param>
    public ContentCanvas Leading(double leading) => Numbers(PdfOperator.SetLeading, leading);

    /// <summary>Moves to the start of the next line.</summary>
    public ContentCanvas NextLine() => Op(PdfOperator.TextNextLine);

    /// <summary>Sets extra spacing added after every glyph.</summary>
    /// <param name="spacing">The spacing in unscaled text units.</param>
    public ContentCanvas CharSpacing(double spacing) => Numbers(PdfOperator.SetCharSpacing, spacing);

    /// <summary>Sets extra spacing added after every single-byte space.</summary>
    /// <param name="spacing">The spacing in unscaled text units.</param>
    public ContentCanvas WordSpacing(double spacing) => Numbers(PdfOperator.SetWordSpacing, spacing);

    /// <summary>Sets horizontal scaling as a percentage of the natural width.</summary>
    /// <param name="percent">The scale, where 100 is unscaled.</param>
    public ContentCanvas HorizontalScale(double percent) => Numbers(PdfOperator.SetHorizontalScale, percent);

    /// <summary>Raises or lowers the baseline.</summary>
    /// <param name="rise">The offset in unscaled text units.</param>
    public ContentCanvas TextRise(double rise) => Numbers(PdfOperator.SetTextRise, rise);

    /// <summary>Sets how glyphs are painted.</summary>
    /// <param name="mode">The rendering mode.</param>
    public ContentCanvas TextRenderMode(PdfTextRenderMode mode) =>
        Numbers(PdfOperator.SetTextRenderMode, (int)mode);

    /// <summary>Shows a string whose bytes are already encoded for the selected font.</summary>
    /// <param name="encoded">The encoded string.</param>
    public ContentCanvas ShowText(ReadOnlySpan<byte> encoded)
    {
        WriteString(encoded);
        _writer.WriteSpace();
        return Op(PdfOperator.ShowText);
    }

    /// <summary>
    /// Shows a run of encoded strings with position adjustments between them, the form kerning takes
    /// in a content stream.
    /// </summary>
    /// <param name="parts">Encoded strings and adjustments in thousandths of an em, in order.</param>
    public ContentCanvas ShowTextAdjusted(params ReadOnlySpan<PdfValue> parts)
    {
        _writer.WriteByte((byte)'[');
        foreach (PdfValue part in parts)
        {
            if (part.Kind == PdfValueKind.String)
                WriteString(part.AsStringBytes);
            else if (part.IsNumber)
                _writer.Write(part.AsNumber);
        }

        _writer.WriteByte((byte)']');
        _writer.WriteSpace();
        return Op(PdfOperator.ShowTextAdjusted);
    }

    /// <summary>Paints an external object by its resource name.</summary>
    /// <param name="xObject">The resource name.</param>
    public ContentCanvas DrawXObject(PdfName xObject)
    {
        ArgumentNullException.ThrowIfNull(xObject);
        Name(xObject);
        return Op(PdfOperator.DrawXObject);
    }

    /// <summary>
    /// Paints an image so that it fills the given rectangle. Images are drawn in a unit square, so
    /// the canvas wraps the call in the transformation that maps that square onto the rectangle.
    /// </summary>
    /// <param name="image">A reference to the image XObject.</param>
    /// <param name="destination">Where to paint it.</param>
    public ContentCanvas DrawImage(PdfValue image, PdfRectangle destination)
    {
        PdfName name = Resources.AddXObject(image);

        Save();
        Transform(new PdfMatrix(destination.Width, 0, 0, destination.Height, destination.Left, destination.Bottom));
        DrawXObject(name);
        return Restore();
    }

    /// <summary>Paints an image so that it fills the given rectangle.</summary>
    /// <param name="image">The image to paint.</param>
    /// <param name="destination">Where to paint it.</param>
    public ContentCanvas DrawImage(Images.PdfImage image, PdfRectangle destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        return DrawImage(image.Reference, destination);
    }

    /// <summary>Begins a marked-content sequence, the anchor a structure element points at.</summary>
    /// <param name="tag">The structure tag.</param>
    /// <param name="properties">A property list, usually carrying the <c>MCID</c>.</param>
    public ContentCanvas BeginMarkedContent(PdfName tag, PdfDictionary? properties = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        Name(tag);

        if (properties is null)
            return Op(PdfOperator.BeginMarkedContent);

        var serializer = new PdfObjectSerializer(_writer);
        serializer.WriteValue(PdfValue.Dictionary(properties));
        _writer.WriteSpace();
        return Op(PdfOperator.BeginMarkedContentProperties);
    }

    /// <summary>Ends the innermost marked-content sequence.</summary>
    public ContentCanvas EndMarkedContent() => Op(PdfOperator.EndMarkedContent);

    /// <summary>Writes an operator with no operands.</summary>
    /// <param name="op">The operator.</param>
    public ContentCanvas Op(PdfOperator op)
    {
        _writer.Write(PdfOperators.Bytes(op));
        _writer.WriteNewLine();
        return this;
    }

    /// <summary>Writes raw operators, for constructs the canvas does not model.</summary>
    /// <param name="raw">The bytes to append verbatim.</param>
    public ContentCanvas Raw(ReadOnlySpan<byte> raw)
    {
        _writer.Write(raw);
        return this;
    }

    private ContentCanvas Color(PdfColor color, bool stroking)
    {
        switch (color.Kind)
        {
            case PdfColorKind.Gray:
                return Numbers(stroking ? PdfOperator.SetStrokeGray : PdfOperator.SetFillGray, color.Component0);

            case PdfColorKind.Rgb:
                return Numbers(stroking ? PdfOperator.SetStrokeRgb : PdfOperator.SetFillRgb,
                    color.Component0, color.Component1, color.Component2);

            default:
                return Numbers(stroking ? PdfOperator.SetStrokeCmyk : PdfOperator.SetFillCmyk,
                    color.Component0, color.Component1, color.Component2, color.Component3);
        }
    }

    private ContentCanvas Numbers(PdfOperator op, params ReadOnlySpan<double> values)
    {
        foreach (double value in values)
        {
            _writer.Write(value);
            _writer.WriteSpace();
        }

        return Op(op);
    }

    private void Name(PdfName name)
    {
        var serializer = new PdfObjectSerializer(_writer);
        serializer.WriteValue(PdfValue.Name(name));
        _writer.WriteSpace();
    }

    private void WriteString(ReadOnlySpan<byte> bytes)
    {
        var serializer = new PdfObjectSerializer(_writer);
        serializer.WriteValue(PdfValue.String(bytes.ToArray()));
    }
}
