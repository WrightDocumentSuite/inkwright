namespace Inkwright.Annotations;

/// <summary>The annotation subtypes of ISO 32000-1 table 169.</summary>
public enum PdfAnnotationType
{
    /// <summary>A subtype the library does not model; the dictionary is still reachable.</summary>
    Unknown,

    /// <summary>A sticky note: an icon on the page with a comment behind it.</summary>
    Text,

    /// <summary>A hyperlink or an action attached to a rectangle.</summary>
    Link,

    /// <summary>Text drawn directly on the page rather than in a pop-up.</summary>
    FreeText,

    /// <summary>A straight line, optionally with end decorations.</summary>
    Line,

    /// <summary>A rectangle.</summary>
    Square,

    /// <summary>An ellipse.</summary>
    Circle,

    /// <summary>A closed polygon.</summary>
    Polygon,

    /// <summary>An open sequence of connected lines.</summary>
    PolyLine,

    /// <summary>Text marked with a coloured wash.</summary>
    Highlight,

    /// <summary>Text with a line beneath it.</summary>
    Underline,

    /// <summary>Text with a wavy line beneath it.</summary>
    Squiggly,

    /// <summary>Text with a line through it.</summary>
    StrikeOut,

    /// <summary>A rubber stamp.</summary>
    Stamp,

    /// <summary>A caret marking where text was inserted.</summary>
    Caret,

    /// <summary>Freehand strokes.</summary>
    Ink,

    /// <summary>The window that shows a markup annotation's text.</summary>
    Popup,

    /// <summary>A file carried on the page.</summary>
    FileAttachment,

    /// <summary>A form field's presence on a page.</summary>
    Widget,

    /// <summary>Content marked for removal.</summary>
    Redact,
}

/// <summary>
/// The annotation flags of ISO 32000-1 table 165, which decide whether an annotation is shown,
/// printed and editable.
/// </summary>
[Flags]
public enum PdfAnnotationFlags
{
    /// <summary>Nothing set.</summary>
    None = 0,

    /// <summary>Hide an annotation of a type the reader does not understand.</summary>
    Invisible = 1 << 0,

    /// <summary>Do not display, print or interact with the annotation at all.</summary>
    Hidden = 1 << 1,

    /// <summary>Print the annotation. Comments that are meant to be printed need this.</summary>
    Print = 1 << 2,

    /// <summary>Keep the annotation the same size however far the page is zoomed.</summary>
    NoZoom = 1 << 3,

    /// <summary>Keep the annotation upright however the page is rotated.</summary>
    NoRotate = 1 << 4,

    /// <summary>Do not show the annotation on screen, though it may still print.</summary>
    NoView = 1 << 5,

    /// <summary>Show the annotation but do not let the user interact with it.</summary>
    ReadOnly = 1 << 6,

    /// <summary>Do not let the user delete the annotation or move it.</summary>
    Locked = 1 << 7,

    /// <summary>Invert the meaning of <see cref="NoView"/> for certain events.</summary>
    ToggleNoView = 1 << 8,

    /// <summary>Do not let the user change the annotation's contents.</summary>
    LockedContents = 1 << 9,
}

/// <summary>The review states of ISO 32000-1 table 171, recorded in a reply to the annotation.</summary>
public enum PdfReviewState
{
    /// <summary>Nothing has been said about the change.</summary>
    None,

    /// <summary>The reviewer agrees with the change.</summary>
    Accepted,

    /// <summary>The reviewer disagrees with the change.</summary>
    Rejected,

    /// <summary>The change has been cancelled.</summary>
    Cancelled,

    /// <summary>The change has been made.</summary>
    Completed,

    /// <summary>The reviewer has ticked the annotation off.</summary>
    Marked,

    /// <summary>The reviewer has un-ticked the annotation.</summary>
    Unmarked,
}

/// <summary>What a line or a callout wears at its ends (ISO 32000-1 table 176).</summary>
public enum PdfLineEnding
{
    /// <summary>Nothing; the line just stops.</summary>
    None,

    /// <summary>A filled triangle pointing outwards.</summary>
    ClosedArrow,

    /// <summary>Two strokes making an arrowhead.</summary>
    OpenArrow,

    /// <summary>A filled circle.</summary>
    Circle,

    /// <summary>A filled square.</summary>
    Square,

    /// <summary>A filled diamond.</summary>
    Diamond,

    /// <summary>A short stroke across the line.</summary>
    Butt,

    /// <summary>A filled triangle pointing back along the line.</summary>
    ReverseClosedArrow,

    /// <summary>An arrowhead pointing back along the line.</summary>
    ReverseOpenArrow,

    /// <summary>A short stroke at an angle.</summary>
    Slash,
}

/// <summary>The icons a reader is required to be able to draw for a sticky note.</summary>
public enum PdfNoteIcon
{
    /// <summary>A speech bubble.</summary>
    Comment,

    /// <summary>A folded note, the default.</summary>
    Note,

    /// <summary>A key.</summary>
    Key,

    /// <summary>A question mark in a circle.</summary>
    Help,

    /// <summary>A pilcrow marking a new paragraph.</summary>
    NewParagraph,

    /// <summary>A pilcrow.</summary>
    Paragraph,

    /// <summary>A caret marking an insertion.</summary>
    Insert,
}
