using Inkwright;
using Inkwright.Content;
using Inkwright.IO;
using Inkwright.Security;

namespace Inkwright.Samples;

/// <summary>Encrypts a PDF, opens it with the user password and checks its readable content.</summary>
internal static class PasswordProtection
{
    public static Task RunAsync(string directory)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);
        using (ContentCanvas canvas = ContentCanvas.ForPage(page))
        {
            canvas.DrawText("Confidential project brief", 72, 750, document.Fonts.Helvetica, 18);
            canvas.Commit();
        }

        string path = Path.Combine(directory, "password-protected.pdf");
        document.Save(path, new PdfSaveOptions
        {
            Encryption = new PdfEncryptionSettings
            {
                UserPassword = "sample-reader-password",
                OwnerPassword = "sample-owner-password",
                Permissions = PdfPermissions.Print | PdfPermissions.ExtractForAccessibility,
            },
        });

        // Demonstration passwords only; supply application-managed secrets in production.
        using PdfDocument opened = PdfDocument.Load(path, new PdfReadOptions { Password = "sample-reader-password" });
        string text = opened.Pages[0].ExtractText();
        if (!opened.WasEncrypted || !text.Contains("Confidential project brief", StringComparison.Ordinal))
            throw new InvalidOperationException("The encrypted document could not be read back.");
        Console.WriteLine("  Password protection: encrypted PDF reopened and text verified");
        return Task.CompletedTask;
    }
}
