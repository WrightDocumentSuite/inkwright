using System.Diagnostics;
using System.Text;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Images;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// A mask that refers back to its image used to recurse until the stack ran out, which no handler
/// can catch and which ends the whole process. Each case therefore decodes in a child process, so a
/// regression fails one test instead of taking down the test host.
/// </summary>
public sealed class ImageMaskCycleTests
{
    private const string ScenarioVariable = "INKWRIGHT_MASK_SCENARIO";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Whether this process was started to run one decode scenario.</summary>
    public static bool IsChildProcess => Environment.GetEnvironmentVariable(ScenarioVariable) is not null;

    [Theory]
    [InlineData("soft-mask-of-itself")]
    [InlineData("explicit-mask-of-itself")]
    [InlineData("two-soft-masks-in-a-loop")]
    [InlineData("very-long-chain")]
    public async Task MalformedMasksFailWithoutEndingTheProcess(string scenario)
    {
        string assembly = typeof(ImageMaskCycleTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(ImageMaskCycleTests).FullName}.{nameof(DecodeScenarioInThisProcess)}");
        start.Environment[ScenarioVariable] = scenario;
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";

        using Process child = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ChildTimeout);

        Task<string> readOutput = child.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> readErrors = child.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            Assert.Fail($"The '{scenario}' decode did not finish within {ChildTimeout}.");
        }

        string output = await readOutput;
        string errors = await readErrors;

        Assert.True(child.ExitCode == 0, $"Exit code {child.ExitCode}.\n{output}\n{errors}");

        // A filter that matched nothing also exits with zero, so the run itself has to be confirmed.
        Assert.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0", output, StringComparison.Ordinal);
    }

    [Fact(Skip = "Runs only as the child process of MalformedMasksFailWithoutEndingTheProcess.", SkipUnless = nameof(IsChildProcess))]
    public void DecodeScenarioInThisProcess()
    {
        string scenario = Environment.GetEnvironmentVariable(ScenarioVariable)!;
        Exception exception = Assert.ThrowsAny<Exception>(() => DecodeAfterSaveAndReload(scenario));

        if (scenario == "very-long-chain")
            Assert.IsType<PdfResourceLimitException>(exception);
        else
            Assert.IsType<PdfFormatException>(exception);
    }

    [Fact]
    public void AMaskWithinTheDepthLimitStillDecodes()
    {
        using var document = PdfDocument.Create();
        PdfObjectId innerMask = AddImage(document, 0x80);
        PdfObjectId mask = AddImage(document, 0xFF, PdfNames.SMask, innerMask);
        PdfObjectId image = AddImage(document, 0x40, PdfNames.SMask, mask);

        PdfImageData decoded = PdfImageReader.Decode(document.Resolve(PdfValue.Reference(image)).AsStream!);

        Assert.Equal(255, decoded.Pixels[3]);
    }

    private static void DecodeAfterSaveAndReload(string scenario)
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.Letter);
        PdfObjectId image = BuildScenario(document, scenario);

        var xObjects = new PdfDictionary();
        xObjects.Set(PdfName.Get("Im"), PdfValue.Reference(image));
        page.Resources.Set(PdfNames.XObject, PdfValue.Dictionary(xObjects));
        page.SetContent(Encoding.ASCII.GetBytes("q 10 0 0 10 0 0 cm /Im Do Q"));

        using var reloaded = PdfDocument.Load(document.ToArray());
        reloaded.Pages[0].Images[0].Decode();
    }

    private static PdfObjectId BuildScenario(PdfDocument document, string scenario)
    {
        switch (scenario)
        {
            case "soft-mask-of-itself":
                {
                    PdfObjectId id = AddImage(document, 0x80);
                    SetEntry(document, id, PdfNames.SMask, id);
                    return id;
                }

            case "explicit-mask-of-itself":
                {
                    PdfObjectId id = AddImage(document, 0x80);
                    SetEntry(document, id, PdfNames.Mask, id);
                    return id;
                }

            case "two-soft-masks-in-a-loop":
                {
                    PdfObjectId first = AddImage(document, 0x80);
                    PdfObjectId second = AddImage(document, 0x40, PdfNames.SMask, first);
                    SetEntry(document, first, PdfNames.SMask, second);
                    return first;
                }

            case "very-long-chain":
                {
                    PdfObjectId current = AddImage(document, 0x80);
                    for (int i = 0; i < 64; i++)
                        current = AddImage(document, 0x40, PdfNames.SMask, current);

                    return current;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void SetEntry(PdfDocument document, PdfObjectId image, PdfName key, PdfObjectId value) =>
        document.Resolve(PdfValue.Reference(image)).AsStream!.Dictionary.Set(key, PdfValue.Reference(value));

    private static PdfObjectId AddImage(PdfDocument document, byte sample, PdfName? key = null, PdfObjectId value = default)
    {
        PdfStream stream = PdfStream.FromData(new byte[] { sample }, compress: false);
        stream.Dictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.XObject));
        stream.Dictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfNames.Image));
        stream.Dictionary.Set(PdfNames.Width, PdfValue.Integer(1));
        stream.Dictionary.Set(PdfNames.Height, PdfValue.Integer(1));
        stream.Dictionary.Set(PdfNames.BitsPerComponent, PdfValue.Integer(8));
        stream.Dictionary.Set(PdfNames.ColorSpace, PdfValue.Name(PdfName.Get("DeviceGray")));
        if (key is not null)
            stream.Dictionary.Set(key, PdfValue.Reference(value));

        return document.AddObject(PdfValue.Stream(stream));
    }
}
