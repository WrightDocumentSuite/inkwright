using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>The names the annotation layer uses, cached so hot paths never hit the intern table.</summary>
internal static class AnnotationNames
{
    internal static readonly PdfName Annot = PdfName.Get("Annot");
    internal static readonly PdfName Annots = PdfName.Get("Annots");
    internal static readonly PdfName Type = PdfName.Get("Type");
    internal static readonly PdfName Subtype = PdfName.Get("Subtype");
    internal static readonly PdfName Rect = PdfName.Get("Rect");
    internal static readonly PdfName Contents = PdfName.Get("Contents");
    internal static readonly PdfName P = PdfName.Get("P");
    internal static readonly PdfName NM = PdfName.Get("NM");
    internal static readonly PdfName M = PdfName.Get("M");
    internal static readonly PdfName F = PdfName.Get("F");
    internal static readonly PdfName AP = PdfName.Get("AP");
    internal static readonly PdfName N = PdfName.Get("N");
    internal static readonly PdfName C = PdfName.Get("C");
    internal static readonly PdfName IC = PdfName.Get("IC");
    internal static readonly PdfName CA = PdfName.Get("CA");
    internal static readonly PdfName T = PdfName.Get("T");
    internal static readonly PdfName Subj = PdfName.Get("Subj");
    internal static readonly PdfName RC = PdfName.Get("RC");
    internal static readonly PdfName CreationDate = PdfName.Get("CreationDate");
    internal static readonly PdfName IRT = PdfName.Get("IRT");
    internal static readonly PdfName RT = PdfName.Get("RT");
    internal static readonly PdfName R = PdfName.Get("R");
    internal static readonly PdfName Group = PdfName.Get("Group");
    internal static readonly PdfName Popup = PdfName.Get("Popup");
    internal static readonly PdfName Parent = PdfName.Get("Parent");
    internal static readonly PdfName Open = PdfName.Get("Open");
    internal static readonly PdfName Name = PdfName.Get("Name");
    internal static readonly PdfName State = PdfName.Get("State");
    internal static readonly PdfName StateModel = PdfName.Get("StateModel");
    internal static readonly PdfName QuadPoints = PdfName.Get("QuadPoints");
    internal static readonly PdfName InkList = PdfName.Get("InkList");
    internal static readonly PdfName Vertices = PdfName.Get("Vertices");
    internal static readonly PdfName L = PdfName.Get("L");
    internal static readonly PdfName LE = PdfName.Get("LE");
    internal static readonly PdfName IT = PdfName.Get("IT");
    internal static readonly PdfName BS = PdfName.Get("BS");
    internal static readonly PdfName W = PdfName.Get("W");
    internal static readonly PdfName S = PdfName.Get("S");
    internal static readonly PdfName DA = PdfName.Get("DA");
    internal static readonly PdfName Q = PdfName.Get("Q");
    internal static readonly PdfName A = PdfName.Get("A");
    internal static readonly PdfName URI = PdfName.Get("URI");
    internal static readonly PdfName Dest = PdfName.Get("Dest");
    internal static readonly PdfName Action = PdfName.Get("Action");
    internal static readonly PdfName FS = PdfName.Get("FS");
    internal static readonly PdfName Border = PdfName.Get("Border");
    internal static readonly PdfName BM = PdfName.Get("BM");
    internal static readonly PdfName Multiply = PdfName.Get("Multiply");
    internal static readonly PdfName ExtGState = PdfName.Get("ExtGState");
    internal static readonly PdfName XObject = PdfName.Get("XObject");
    internal static readonly PdfName Form = PdfName.Get("Form");
    internal static readonly PdfName FormType = PdfName.Get("FormType");
    internal static readonly PdfName BBox = PdfName.Get("BBox");
    internal static readonly PdfName Resources = PdfName.Get("Resources");
    internal static readonly PdfName Matrix = PdfName.Get("Matrix");
    internal static readonly PdfName Text = PdfName.Get("Text");
}
