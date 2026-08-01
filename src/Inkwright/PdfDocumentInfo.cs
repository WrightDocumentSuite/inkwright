using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// The document information dictionary of ISO 32000-1 §14.3.3, exposed as plain properties. The
/// underlying dictionary stays available for entries outside the standard set.
/// </summary>
public sealed class PdfDocumentInfo
{
    private readonly PdfDictionary _dictionary;
    private readonly Action? _onChange;

    internal PdfDocumentInfo(PdfDictionary dictionary, Action? onChange = null)
    {
        _dictionary = dictionary;
        _onChange = onChange;
    }

    /// <summary>The dictionary behind these properties.</summary>
    public PdfDictionary Dictionary => _dictionary;

    /// <summary>The document's title.</summary>
    public string? Title
    {
        get => _dictionary.GetTextString(PdfNames.Title);
        set => SetText(PdfNames.Title, value);
    }

    /// <summary>The person who wrote the document.</summary>
    public string? Author
    {
        get => _dictionary.GetTextString(PdfNames.Author);
        set => SetText(PdfNames.Author, value);
    }

    /// <summary>What the document is about.</summary>
    public string? Subject
    {
        get => _dictionary.GetTextString(PdfNames.Subject);
        set => SetText(PdfNames.Subject, value);
    }

    /// <summary>Keywords associated with the document.</summary>
    public string? Keywords
    {
        get => _dictionary.GetTextString(PdfNames.Keywords);
        set => SetText(PdfNames.Keywords, value);
    }

    /// <summary>The application that produced the original document.</summary>
    public string? Creator
    {
        get => _dictionary.GetTextString(PdfNames.Creator);
        set => SetText(PdfNames.Creator, value);
    }

    /// <summary>The application that converted the document to PDF.</summary>
    public string? Producer
    {
        get => _dictionary.GetTextString(PdfNames.Producer);
        set => SetText(PdfNames.Producer, value);
    }

    /// <summary>When the document was created.</summary>
    public DateTimeOffset? CreationDate
    {
        get => ReadDate(PdfNames.CreationDate);
        set => WriteDate(PdfNames.CreationDate, value);
    }

    /// <summary>When the document was last changed.</summary>
    public DateTimeOffset? ModificationDate
    {
        get => ReadDate(PdfNames.ModDate);
        set => WriteDate(PdfNames.ModDate, value);
    }

    private void SetText(PdfName key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            _dictionary.Remove(key);
        else
            _dictionary.Set(key, PdfValue.TextString(value));

        _onChange?.Invoke();
    }

    private DateTimeOffset? ReadDate(PdfName key) =>
        PdfDate.TryParse(_dictionary.GetTextString(key), out DateTimeOffset value) ? value : null;

    private void WriteDate(PdfName key, DateTimeOffset? value)
    {
        if (value is null)
            _dictionary.Remove(key);
        else
            _dictionary.Set(key, PdfValue.TextString(PdfDate.Format(value.Value)));

        _onChange?.Invoke();
    }
}
