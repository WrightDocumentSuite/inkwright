using System.Text;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;
using Inkwright.IO;
using Inkwright.Syntax;
using Xunit;

namespace Inkwright.Tests;

public class StreamDecodePolicyTests
{
    [Fact]
    public void ReaderAttachesThePolicyToXrefAndObjectStreams()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Version = PdfVersion.Pdf20;
        source.Pages.Add();
        byte[] bytes = source.ToArray();
        var policy = new RecordingPolicy();

        using PdfDocument reopened = PdfDocument.Load(
            bytes,
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = policy,
            });

        Assert.Contains(PdfNames.XRef, policy.Types);
        Assert.Contains(PdfNames.ObjStm, policy.Types);
    }

    [Fact]
    public void SuccessfulPolicyDecodeIsCachedByTheStream()
    {
        var policy = new RecordingPolicy();
        var stream = new PdfStream(
            new PdfDictionary(),
            "payload"u8.ToArray(),
            policy);

        Assert.Equal("payload"u8.ToArray(), stream.Decode().ToArray());
        Assert.Equal("payload"u8.ToArray(), stream.Decode().ToArray());
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public void FailedPolicyDecodeIsCachedAndRethrownWithoutASecondCharge()
    {
        var policy = new ThrowingPolicy();
        var stream = new PdfStream(
            new PdfDictionary(),
            "payload"u8.ToArray(),
            policy);

        PdfFormatException first = Assert.Throws<PdfFormatException>(() => stream.Decode());
        PdfFormatException second = Assert.Throws<PdfFormatException>(() => stream.Decode());

        Assert.Same(first, second);
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public void BoundedPolicyDecodeCachesOnlyACompleteResult()
    {
        var policy = new RecordingPolicy();
        var stream = new PdfStream(
            new PdfDictionary(),
            "payload"u8.ToArray(),
            policy);

        PdfBoundedDecodeResult limited = stream.DecodeBoundedWithPolicy(3);
        Assert.Equal(PdfBoundedDecodeStatus.LimitExceeded, limited.Status);
        Assert.Equal(1, policy.Calls);

        PdfBoundedDecodeResult complete = stream.DecodeBoundedWithPolicy(7);
        Assert.Equal(PdfBoundedDecodeStatus.Complete, complete.Status);
        Assert.Equal("payload"u8.ToArray(), complete.Data.ToArray());
        Assert.Equal("payload"u8.ToArray(), stream.Decode().ToArray());
        Assert.Equal(2, policy.Calls);
    }

    [Fact]
    public void BoundedPolicyFailureIsCachedForTheNormalDecodePath()
    {
        var policy = new ThrowingPolicy();
        var stream = new PdfStream(
            new PdfDictionary(),
            "payload"u8.ToArray(),
            policy);

        PdfFormatException first = Assert.Throws<PdfFormatException>(() =>
            stream.DecodeBoundedWithPolicy(16));
        PdfFormatException second = Assert.Throws<PdfFormatException>(() => stream.Decode());

        Assert.Same(first, second);
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public void ProtectedParserRejectsAValueWhereADictionaryKeyIsRequired()
    {
        var policy = new RecordingPolicy(maxParsedValues: 4);
        var parser = new PdfObjectParser(
            "<< 0 0 0 0 0 >>"u8,
            resolver: new PolicyResolver(policy));

        PdfFormatException? failure = null;
        try
        {
            _ = parser.ParseValue();
        }
        catch (PdfFormatException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains("dictionary", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name key", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("+.")]
    [InlineData("--5")]
    [InlineData("1+2")]
    [InlineData("1..2")]
    [InlineData("1e2")]
    [InlineData("1E2")]
    [InlineData("1.0e")]
    [InlineData("12abc")]
    public void ProtectedParserRejectsAMalformedNumericTokenWithoutChangingCompatibilityReads(
        string token)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(token);
        var policy = new RecordingPolicy();
        var protectedParser = new PdfObjectParser(
            bytes,
            resolver: new PolicyResolver(policy));

        PdfFormatException? failure = null;
        try
        {
            _ = protectedParser.ParseValue();
        }
        catch (PdfFormatException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains("numeric token", failure.Message, StringComparison.OrdinalIgnoreCase);

        // The public reader deliberately retains its long-standing damaged-file recovery.
        var compatibilityParser = new PdfObjectParser(bytes);
        _ = compatibilityParser.ParseValue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("+17")]
    [InlineData("-98")]
    [InlineData("34.5")]
    [InlineData("4.")]
    [InlineData(".002")]
    [InlineData("-.002")]
    public void ProtectedParserAcceptsEveryNormativeNumericSpelling(string token)
    {
        var parser = new PdfObjectParser(
            Encoding.ASCII.GetBytes(token),
            resolver: new PolicyResolver(new RecordingPolicy()));

        Assert.True(parser.ParseValue().IsNumber);
    }

    [Theory]
    [InlineData(")")]
    [InlineData(">")]
    public void ProtectedParserRejectsAStrayClosingDelimiterWithoutChangingCompatibilityReads(
        string delimiter)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(delimiter);
        var protectedParser = new PdfObjectParser(
            bytes,
            resolver: new PolicyResolver(new RecordingPolicy()));

        PdfFormatException? failure = null;
        try
        {
            _ = protectedParser.ParseValue();
        }
        catch (PdfFormatException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains("stray closing delimiter", failure.Message, StringComparison.OrdinalIgnoreCase);

        var compatibilityParser = new PdfObjectParser(bytes);
        Assert.True(compatibilityParser.ParseValue().IsNull);
    }

    [Theory]
    [InlineData("(unclosed", "literal")]
    [InlineData("<4142", "hexadecimal")]
    public void ProtectedParserRejectsAnUnclosedStringWithoutChangingCompatibilityReads(
        string token,
        string kind)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(token);
        var protectedParser = new PdfObjectParser(
            bytes,
            resolver: new PolicyResolver(new RecordingPolicy()));

        PdfFormatException? failure = null;
        try
        {
            _ = protectedParser.ParseValue();
        }
        catch (PdfFormatException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains(kind, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not closed", failure.Message, StringComparison.OrdinalIgnoreCase);

        var compatibilityParser = new PdfObjectParser(bytes);
        Assert.Equal(PdfValueKind.String, compatibilityParser.ParseValue().Kind);
    }

    [Theory]
    [InlineData("<GG>")]
    [InlineData("<41G2>")]
    public void ProtectedParserRejectsANonHexadecimalByteInAHexString(
        string token)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(token);
        var protectedParser = new PdfObjectParser(
            bytes,
            resolver: new PolicyResolver(new RecordingPolicy()));

        PdfFormatException? failure = null;
        try
        {
            _ = protectedParser.ParseValue();
        }
        catch (PdfFormatException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains("hexadecimal string", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-hexadecimal", failure.Message, StringComparison.OrdinalIgnoreCase);

        var compatibilityParser = new PdfObjectParser(bytes);
        Assert.Equal(PdfValueKind.String, compatibilityParser.ParseValue().Kind);
    }

    [Theory]
    [InlineData("/#")]
    [InlineData("/#G0")]
    [InlineData("/#00")]
    public void ProtectedParserRejectsAnInvalidNameEscapeWithoutChangingCompatibilityReads(
        string token)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(token);
        var protectedParser = new PdfObjectParser(
            bytes,
            resolver: new PolicyResolver(new RecordingPolicy()));

        PdfFormatException? failure = null;
        try
        {
            _ = protectedParser.ParseValue();
        }
        catch (PdfFormatException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains("protected name", failure.Message, StringComparison.OrdinalIgnoreCase);

        var compatibilityParser = new PdfObjectParser(bytes);
        Assert.Equal(PdfValueKind.Name, compatibilityParser.ParseValue().Kind);
    }

    [Fact]
    public void ProtectedReaderRejectsAStreamNestedInsideADirectDictionary()
    {
        byte[] bytes = NestedDirectStreamFixture();

        // Compatibility mode retains the historical damaged-file recovery path.
        using (PdfDocument recovered = PdfDocument.Load(bytes))
            Assert.Equal(PdfValueKind.Stream, recovered.Catalog.Get(PdfName.Get("Probe")).Kind);

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("protected stream", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indirect object", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsAnObjectStreamCountBeforeAllocatingItsArrays()
    {
        byte[] malicious = ProtectedReaderFixture();
        var policy = new RecordingPolicy();

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                malicious,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = policy,
                }));

        Assert.Contains("object stream declares", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PdfNames.XRef, policy.Types);
        Assert.Contains(PdfNames.ObjStm, policy.Types);
    }

    [Fact]
    public void ProtectedReaderRequiresObjectStreamGenerationZero()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(objectStreamGeneration: 1),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("object stream", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generation 0", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsAnObjectStreamHeaderCountMismatch()
    {
        byte[] malicious = ProtectedReaderFixture(
            declaredObjectCount: 2,
            objectHeaderLength: 4,
            objectPayload: "2 0 << /Type /Catalog >>"u8.ToArray());

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                malicious,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("object stream", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsAnObjectStreamSlotMismatchInsteadOfSearchingForTheObject()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 1,
                    objectHeaderLength: 4,
                    objectPayload: "2 0 << /Type /Catalog >>"u8.ToArray(),
                    compressedObjectSlot: 1),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("slot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderValidatesAnUnreachableCompressedObjectSlot()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                UnreachableCompressedObjectFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("slot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown-keyword")]
    [InlineData("unclosed-array")]
    [InlineData("unclosed-dictionary")]
    [InlineData("unclosed-literal-string")]
    [InlineData("unclosed-hex-string")]
    public void ProtectedReaderValidatesEveryUnreachableCompressedObjectValue(string shape)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                UnreachableCompressedObjectFixture(shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("protected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresStrictlyIncreasingObjectStreamOffsets()
    {
        byte[] payload = "2 0 4 0 << /Type /Catalog >> <<>>"u8.ToArray();
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 2,
                    objectHeaderLength: 8,
                    objectPayload: payload),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("offset", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresTheFirstObjectStreamOffsetToBeZero()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 1,
                    objectHeaderLength: 4,
                    objectPayload: "2 1  << /Type /Catalog >>"u8.ToArray()),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("first object offset", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresWhitespaceBeforeTheFirstObjectStreamObject()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 1,
                    objectHeaderLength: 3,
                    objectPayload: "2 0<< /Type /Catalog >>"u8.ToArray()),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("First entry", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whitespace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsAnIndirectReferenceAsAnObjectStreamValue()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 1,
                    objectHeaderLength: 4,
                    objectPayload: "2 0 1 0 R"u8.ToArray()),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("indirect reference", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresEachObjectStreamSlotToStartAtItsDeclaredOffset()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 2,
                    objectHeaderLength: 8,
                    objectPayload: "4 0 2 4 <<>> << /Type /Catalog >>"u8.ToArray(),
                    compressedObjectSlot: 1),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("object-stream slot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("declared offset", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsObjectStreamFirstBeyondDecodedBytes()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(
                    declaredObjectCount: 1,
                    objectHeaderLength: 32,
                    objectPayload: "2 0"u8.ToArray()),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("First", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedReaderRejectsASelfReferentialPrevChain()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(selfReferentialPrev: true),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Prev chain", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsAnOddXrefIndex()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(oddIndex: true),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("odd-length Index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsATruncatedXrefRow()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(truncateXrefRow: true),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("decoded length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsCryptEvenWhenItIsIdentityOnAnXrefStream()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(xrefCryptIdentity: true),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Crypt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cross-reference", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("Size")]
    [InlineData("W")]
    [InlineData("Index")]
    [InlineData("Filter")]
    [InlineData("DecodeParms")]
    [InlineData("Length")]
    public void ProtectedReaderRequiresStructuralXrefEntriesToBeDirect(string key)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(indirectXrefKey: key),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("direct", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", null, "\n", "3")]
    [InlineData("real", "3.0", "\n", "3")]
    [InlineData("negative", "-1", "\n", "3")]
    [InlineData("undefined", "5 0 R", "\n", "3")]
    [InlineData("wrong-generation", "4 0 R", "\n", "3")]
    [InlineData("indirect-real", "4 0 R", "\n", "3.0")]
    [InlineData("indirect-negative", "4 0 R", "\n", "-1")]
    [InlineData("length-mismatch", "2", "\n", "3")]
    [InlineData("missing-eol", "3", "", "3")]
    [InlineData("space-before-eol", "3", " \n", "3")]
    public void ProtectedReaderRejectsAnInvalidStreamLengthWithoutChangingCompatibilityReads(
        string shape,
        string? length,
        string beforeEndstream,
        string lengthObjectValue)
    {
        byte[] bytes = StreamLengthFixture(
            length,
            beforeEndstream,
            lengthObjectGeneration: shape == "wrong-generation" ? 1 : 0,
            lengthObjectValue: lengthObjectValue);

        PdfReader compatible = PdfReader.Open(bytes);
        PdfStream? stream = compatible.GetObject(3).AsStream;
        Assert.NotNull(stream);
        Assert.Equal("abc"u8.ToArray(), stream.RawData.ToArray());

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfReader.Open(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("3", "\n")]
    [InlineData("3", "\r")]
    [InlineData("3", "\r\n")]
    [InlineData("4 0 R", "\n")]
    [InlineData("4 0 R", "\r")]
    public void ProtectedReaderAcceptsAnExactIntegerStreamLength(string length, string beforeEndstream)
    {
        PdfReader reader = PdfReader.Open(
            StreamLengthFixture(length, beforeEndstream),
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = new RecordingPolicy(),
            });

        Assert.Equal("abc"u8.ToArray(), reader.GetObject(3).AsStream!.RawData.ToArray());
    }

    [Fact]
    public void ProtectedReaderRejectsACompressedObjectUsedAsAnObjectStreamLength()
    {
        byte[] payload = "2 0 << /Type /Catalog >>"u8.ToArray();

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfReader.Open(
                ProtectedReaderFixture(
                    declaredObjectCount: 1,
                    objectHeaderLength: 4,
                    objectPayload: payload,
                    objectStreamLength: "2 0 R"),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Length", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compressed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresDirectIntegerWElements()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(indirectWElement: true),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("direct integer W", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-size")]
    [InlineData("indirect-size")]
    [InlineData("nonpositive-size")]
    [InlineData("too-small-size")]
    public void ProtectedReaderRequiresAValidClassicTrailerSize(string shape)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                RequiredClassicStructureFixture(shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Size", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-catalog-type")]
    [InlineData("wrong-catalog-type")]
    public void ProtectedReaderRequiresTheCatalogType(string shape)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                RequiredClassicStructureFixture(shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Catalog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedReaderRejectsAStreamUsedAsTheCatalogObject()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                StreamCatalogFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dictionary, not a stream", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsOutOfOrderOrOverlappingIndexRanges()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(xrefIndex: "[2 2 0 2]"),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("overlaps, or is out of order", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresThePhysicalFinalEofToBelongToStartXref()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Pages.Add();
        byte[] original = source.ToArray();
        byte[] suffix = "garbage\n%%EOF\n"u8.ToArray();
        byte[] bytes = new byte[original.Length + suffix.Length];
        original.CopyTo(bytes, 0);
        suffix.CopyTo(bytes, original.Length);

        // Compatibility mode retains its long-standing tolerance for bytes after a valid revision.
        using (PdfDocument recovered = PdfDocument.Load(bytes))
            Assert.Single(recovered.Pages);

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("startxref", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%%EOF", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedReaderRejectsAFinalStartXrefThatReplaysAnOlderRevision()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Pages.Add();
        byte[] original = source.ToArray();
        int keyword = original.AsSpan().LastIndexOf("startxref"u8);
        Assert.True(keyword >= 0);
        int numberStart = keyword + "startxref".Length;
        while (numberStart < original.Length && PdfCharacters.IsWhitespace(original[numberStart]))
            numberStart++;
        int numberEnd = numberStart;
        while (numberEnd < original.Length && original[numberEnd] is >= (byte)'0' and <= (byte)'9')
            numberEnd++;
        long oldOffset = long.Parse(Encoding.ASCII.GetString(original[numberStart..numberEnd]));

        byte[] suffix = Encoding.ASCII.GetBytes(
            $"garbage\nstartxref\n{oldOffset}\n%%EOF\n");
        byte[] bytes = new byte[original.Length + suffix.Length];
        original.CopyTo(bytes, 0);
        suffix.CopyTo(bytes, original.Length);

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("latest cross-reference section", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startxref", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProtectedReaderRequiresExactCrossReferenceAndObjectOffsets(
        bool useXrefStream,
        bool shiftSectionOffset)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                InexactOffsetFixture(useXrefStream, shiftSectionOffset),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("offset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderRejectsAnOffsetIntoTheSuffixOfAToken(bool sectionOffset)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                SuffixTokenOffsetFixture(sectionOffset),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("offset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresEndobjAfterEveryLiveObject()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Pages.Add();
        byte[] bytes = source.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            SetProducer = false,
            UpdateModificationDate = false,
        });

        byte[] header = Encoding.ASCII.GetBytes($"{source.CatalogId.Number} 0 obj");
        int objectStart = bytes.AsSpan().IndexOf(header);
        Assert.True(objectStart >= 0);
        int endobj = bytes.AsSpan(objectStart).IndexOf("endobj"u8);
        Assert.True(endobj >= 0);
        bytes.AsSpan(objectStart + endobj, "endobj".Length).Fill((byte)' ');

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("endobj", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-endobj")]
    [InlineData("wrong-generation")]
    [InlineData("unknown-keyword")]
    public void ProtectedReaderRejectsAnInvalidUnloadedLiveObjectRange(string shape)
    {
        Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                UnloadedLiveObjectFixture(shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("trailer")]
    public void ProtectedReaderRejectsAnUnclosedDictionaryAtAnObjectBoundary(string shape)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                UnclosedDictionaryFixture(shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("dictionary", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("trailer")]
    public void ProtectedReaderRejectsDuplicateDictionaryKeys(string shape)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                DuplicateDictionaryKeyFixture(shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(shape == "catalog" ? "Type" : "Size", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderRequiresEveryObjectNumberThroughSize(bool useXrefStream)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                XrefGapFixture(useXrefStream),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Size", exception.Message, StringComparison.Ordinal);
        Assert.Contains("every object number", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresEachClassicSectionSizeToCoverItsRows()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                MaskedClassicSectionSizeFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("classic xref", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedReaderRequiresEveryRevisionSizeToCoverItsPreviousHistory()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                MaskedPreviousRevisionSizeFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Size", exception.Message, StringComparison.Ordinal);
        Assert.Contains("previous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "Prev")]
    [InlineData(true, "XRefStm")]
    public void ProtectedReaderRequiresEarlierCrossReferenceLinks(bool hybrid, string key)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ForwardCrossReferenceLinkFixture(hybrid),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains("earlier", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderAcceptsTheLinearizedCrossReferenceOrder(bool incrementalUpdate)
    {
        using PdfDocument document = PdfDocument.Load(
            LinearizedFixture(incrementalUpdate),
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = new RecordingPolicy(),
            });

        Assert.Single(document.Pages);
    }

    [Fact]
    public void ProtectedReaderDoesNotTrustALinearizationDictionaryWithAnInvalidLength()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                LinearizedFixture(incrementalUpdate: false, declaredLengthDelta: 1),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Prev", exception.Message, StringComparison.Ordinal);
        Assert.Contains("earlier", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderTreatsAnUnknownXrefEntryTypeAsNull()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(extraXrefRowType: 7),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        // The unknown row is ignored per ISO 32000. Loading proceeds until the deliberately huge
        // object-stream N value in this fixture hits the next independent guard.
        Assert.Contains("object stream declares", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown entry type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 7)]
    public void ProtectedReaderDoesNotResurrectANullEntryFromPrev(
        bool useXrefStream,
        byte newestEntryType)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                IncrementalTombstoneFixture(useXrefStream, newestEntryType),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderLetsCurrentClassicFreeEntryOverrideHybridXrefStream()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                HybridTombstoneFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderCapsCrossReferenceDictionaryExpansion()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Version = PdfVersion.Pdf20;
        source.Pages.Add();
        byte[] bytes = source.ToArray();
        var policy = new RecordingPolicy(maxStructuralEntries: 2);

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = policy,
                }));

        Assert.Contains("cross-reference", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 structural entries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderChargesFreeRowsAgainstTheSharedStructuralLimit(bool useXrefStream)
    {
        byte[] bytes = FreeXrefRowsFixture(useXrefStream, rowCount: 4);

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(maxStructuralEntries: 3),
                }));

        Assert.Contains("structural entries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresAClassicTrailerDictionary()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                FreeXrefRowsFixture(useXrefStream: false, rowCount: 1, malformedTrailer: true),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("trailer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dictionary", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRejectsAnOutOfRangeClassicGeneration()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                InvalidClassicGenerationFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("000000000 00000 n \n")]   // Nine offset digits.
    [InlineData("0000000000\t00000 n \n")] // A tab instead of the required SPACE.
    [InlineData("0000000000 00000 n\n")]    // Missing separator before the EOL.
    [InlineData("0000000000 00000 n \r\n")] // A 21-byte row.
    public void ProtectedReaderRequiresTheFixedClassicXrefRowGrammar(string row)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ClassicXrefRowFixture(row),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("fixed 20-byte", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresAnEntryForTheXrefStreamItself()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ProtectedReaderFixture(includeXrefSelfEntry: false),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("entry for itself", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderRequiresTheXrefGenerationToMatchTheObjectHeader(bool useXrefStream)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                GenerationMismatchFixture(useXrefStream),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("object header", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderResolvesAMismatchedReferenceGenerationToNull(bool useXrefStream)
    {
        using PdfDocument source = PdfDocument.Create();
        source.Pages.Add();
        PdfObjectId catalogId = source.CatalogId;
        byte[] bytes = source.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = useXrefStream,
            SetProducer = false,
            UpdateModificationDate = false,
        });
        PdfReader reader = PdfReader.Open(
            bytes,
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = new RecordingPolicy(),
            });

        Assert.NotNull(reader.GetObject(catalogId).AsDictionary);
        Assert.True(reader.GetObject(new PdfObjectId(catalogId.Number, 1)).IsNull);
    }

    [Fact]
    public void CompatibilityReaderRecoversAReferencedObjectHiddenByAFreeXrefRow()
    {
        byte[] bytes = FreePageEntryFixture();

        using (PdfDocument recovered = PdfDocument.Load(bytes))
            Assert.Single(recovered.Pages);

        PdfReader strictCompatibility = PdfReader.Open(
            bytes,
            new PdfReadOptions { Recover = false });
        Assert.True(strictCompatibility.GetObject(3).IsNull);
    }

    [Fact]
    public void CompatibilityReaderRecoversAReferencedPageHiddenByAnUnknownXrefStreamRow()
    {
        byte[] bytes = UnknownPageEntryFixture();

        using (PdfDocument recovered = PdfDocument.Load(bytes))
            Assert.Single(recovered.Pages);

        PdfReader strictCompatibility = PdfReader.Open(
            bytes,
            new PdfReadOptions { Recover = false });
        Assert.True(strictCompatibility.GetObject(3).IsNull);

        PdfFormatException protectedFailure = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.True(
            protectedFailure.Message.Contains("page tree", StringComparison.OrdinalIgnoreCase) ||
            protectedFailure.Message.Contains("revision body", StringComparison.OrdinalIgnoreCase),
            $"Unexpected protected-reader failure: {protectedFailure.Message}");
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(false, "in-use")]
    [InlineData(false, "bad-generation")]
    [InlineData(true, "missing")]
    [InlineData(true, "in-use")]
    [InlineData(true, "bad-generation")]
    public void ProtectedReaderRequiresTheObjectZeroFreeHead(bool useXrefStream, string shape)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                ObjectZeroFixture(useXrefStream, shape),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("object 0", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("65535", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedReaderRejectsOutOfRangeXrefStreamFields(bool compressedEntry)
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                InvalidWideXrefFieldFixture(compressedEntry),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("out-of-range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReaderRequiresXrefStmToPointToAStreamSection()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                HybridPointingToClassicXrefFixture(),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("XRefStm", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stream", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoverFalseDoesNotScanForACatalogMissingFromTheTrailer()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Pages.Add();
        byte[] bytes = source.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        int root = bytes.AsSpan().IndexOf("/Root"u8);
        Assert.True(root >= 0);
        bytes[root + 1] = (byte)'X';

        using (PdfDocument recovered = PdfDocument.Load(bytes))
            Assert.True(recovered.Diagnostics.Contains(PdfWarningCode.CatalogRecovered));

        Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(bytes, new PdfReadOptions { Recover = false }));
    }

    [Fact]
    public void RecoverFalseDoesNotReplaceABrokenPageTreeWithOrphanPages()
    {
        using PdfDocument source = PdfDocument.Create();
        source.Pages.Add();
        byte[] bytes = source.ToArray(new PdfSaveOptions { UseObjectStreams = false });
        int pages = bytes.AsSpan().IndexOf("/Pages "u8);
        Assert.True(pages >= 0);
        int objectNumber = pages + "/Pages ".Length;
        Assert.InRange(bytes[objectNumber], (byte)'1', (byte)'8');
        bytes[objectNumber] = (byte)'9';

        using (PdfDocument recovered = PdfDocument.Load(bytes))
        {
            Assert.Single(recovered.Pages);
            Assert.True(recovered.Diagnostics.Contains(PdfWarningCode.PageTreeRecovered));
        }

        Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(bytes, new PdfReadOptions { Recover = false }));
    }

    [Theory]
    [InlineData("missing-kids", "Kids")]
    [InlineData("missing-count", "Count")]
    [InlineData("wrong-count", "Count")]
    [InlineData("dangling-kid", "dictionary")]
    [InlineData("wrong-parent", "Parent")]
    public void ProtectedReaderRejectsMalformedPageTreeStructure(string shape, string expectedMessage)
    {
        byte[] bytes = MalformedPageTreeFixture(shape);

        // Compatibility reads retain their long-standing recovery behaviour.
        using (PdfDocument recovered = PdfDocument.Load(bytes))
            Assert.NotNull(recovered.Pages);

        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedObjectRangeQueriesReuseTheFirstParseBudget()
    {
        byte[] bytes = RequiredClassicStructureFixture("valid");
        var measuringPolicy = new RecordingPolicy();
        PdfReader measured = PdfReader.Open(
            bytes,
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = measuringPolicy,
            });
        int parsedValues = measuringPolicy.ParsedValues;
        Assert.True(parsedValues > 0);

        foreach (int number in measured.LiveObjectNumbers)
            Assert.True(measured.TryGetObjectRange(number, out _, out _));
        Assert.Equal(parsedValues, measuringPolicy.ParsedValues);

        var exactPolicy = new RecordingPolicy(maxParsedValues: parsedValues);
        PdfReader exact = PdfReader.Open(
            bytes,
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = exactPolicy,
            });
        foreach (int number in exact.LiveObjectNumbers)
            Assert.True(exact.TryGetObjectRange(number, out _, out _));

        Assert.Equal(parsedValues, exactPolicy.ParsedValues);
    }

    [Fact]
    public void ProtectedCommentScannerDoesNotTreatPercentInsideALiteralStringAsAComment()
    {
        PdfReader reader = PdfReader.Open(
            LiteralPercentBeforeObjectOnSameLineFixture(),
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = new RecordingPolicy(),
            });

        Assert.Equal(PdfNames.Catalog, reader.Catalog!.GetName(PdfNames.Type));
    }

    [Fact]
    public void ProtectedReaderAcceptsHistoricalStartXrefForAnOrdinaryPrevTarget()
    {
        PdfReader reader = PdfReader.Open(
            HistoricalPrevFixture(includeHistoricalTail: true),
            new PdfReadOptions
            {
                Recover = false,
                StreamDecodePolicy = new RecordingPolicy(),
            });

        Assert.Equal(PdfNames.Catalog, reader.Catalog!.GetName(PdfNames.Type));
    }

    [Fact]
    public void ProtectedReaderRejectsAPrevTargetWithoutItsOwnHistoricalStartXref()
    {
        PdfFormatException exception = Assert.Throws<PdfFormatException>(() =>
            PdfReader.Open(
                HistoricalPrevFixture(includeHistoricalTail: false),
                new PdfReadOptions
                {
                    Recover = false,
                    StreamDecodePolicy = new RecordingPolicy(),
                }));

        Assert.Contains("Prev target", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("historical startxref", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] LiteralPercentBeforeObjectOnSameLineFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int literal = Position();
        Write("1 0 obj\n(%) endobj ");
        int catalog = Position();
        Write("2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n");
        int pages = Position();
        Write("3 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xref = Position();
        Write("xref\n0 4\n0000000000 65535 f \n");
        Row(literal);
        Row(catalog);
        Row(pages);
        Write("trailer\n<< /Size 4 /Root 2 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] HistoricalPrevFixture(bool includeHistoricalTail)
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pages = Position();
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int baseXref = Position();
        Write("xref\n0 3\n0000000000 65535 f \n");
        Row(catalog);
        Row(pages);
        Write("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        if (includeHistoricalTail)
            Write($"startxref\n{baseXref}\n%%EOF\n");

        int updatedCatalog = Position();
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int latestXref = Position();
        Write("xref\n0 2\n0000000000 65535 f \n");
        Row(updatedCatalog);
        Write($"trailer\n<< /Size 3 /Root 1 0 R /Prev {baseXref} >>\n");
        Write($"startxref\n{latestXref}\n%%EOF\n");
        return output.ToArray();

        int Position() => checked((int)output.Position);
        void Row(int offset) => Write($"{offset:D10} 00000 n \n");
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] StreamLengthFixture(
        string? length,
        string beforeEndstream,
        int lengthObjectGeneration = 0,
        string lengthObjectValue = "3")
    {
        using var output = new MemoryStream();
        Write("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n");

        int catalogOffset = checked((int)output.Position);
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int streamOffset = checked((int)output.Position);
        string lengthEntry = length is null ? string.Empty : $" /Length {length}";
        Write($"3 0 obj\n<<{lengthEntry} >>\nstream\nabc{beforeEndstream}endstream\nendobj\n");
        int lengthOffset = checked((int)output.Position);
        Write($"4 {lengthObjectGeneration} obj\n{lengthObjectValue}\nendobj\n");

        int xrefOffset = checked((int)output.Position);
        Write("xref\n0 5\n");
        Write("0000000000 65535 f \n");
        Write($"{catalogOffset:D10} 00000 n \n");
        Write($"{pagesOffset:D10} 00000 n \n");
        Write($"{streamOffset:D10} 00000 n \n");
        Write($"{lengthOffset:D10} {lengthObjectGeneration:D5} n \n");
        Write("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.Latin1.GetBytes(value));
    }

    private static byte[] ProtectedReaderFixture(
        int declaredObjectCount = int.MaxValue,
        int objectHeaderLength = 0,
        byte[]? objectPayload = null,
        bool selfReferentialPrev = false,
        bool oddIndex = false,
        bool truncateXrefRow = false,
        int compressedObjectSlot = 0,
        bool xrefCryptIdentity = false,
        string? indirectXrefKey = null,
        bool indirectWElement = false,
        string? xrefIndex = null,
        byte? extraXrefRowType = null,
        bool includeXrefSelfEntry = true,
        int objectStreamGeneration = 0,
        string? objectStreamLength = null)
    {
        objectPayload ??= [];
        using var output = new MemoryStream();
        Write("%PDF-2.0\n%\u00E2\u00E3\u00CF\u00D3\n");

        int objectStreamOffset = checked((int)output.Position);
        Write(
            $"1 {objectStreamGeneration} obj\n<< /Type /ObjStm /N {declaredObjectCount} " +
            $"/First {objectHeaderLength} " +
            $"/Length {objectStreamLength ?? objectPayload.Length.ToString()} >>\nstream\n");
        output.Write(objectPayload);
        Write("\nendstream\nendobj\n");

        int xrefOffset = checked((int)output.Position);
        int xrefRowCount = extraXrefRowType.HasValue ? 5 : 4;
        byte[] rows = new byte[xrefRowCount * 7];
        WriteRow(rows.AsSpan(0, 7), type: 0, field2: 0, field3: 65_535);
        WriteRow(
            rows.AsSpan(7, 7),
            type: 1,
            field2: objectStreamOffset,
            field3: objectStreamGeneration);
        WriteRow(rows.AsSpan(14, 7), type: 2, field2: 1, field3: compressedObjectSlot);
        WriteRow(
            rows.AsSpan(21, 7),
            type: includeXrefSelfEntry ? (byte)1 : (byte)0,
            field2: includeXrefSelfEntry ? xrefOffset : 0,
            field3: 0);
        if (extraXrefRowType is { } rowType)
            WriteRow(rows.AsSpan(28, 7), type: rowType, field2: 0, field3: 0);
        if (truncateXrefRow)
            rows = rows[..^1];

        string typeEntry = indirectXrefKey == "Type" ? "/Type 99 0 R" : "/Type /XRef";
        string sizeEntry = indirectXrefKey == "Size"
            ? "/Size 99 0 R"
            : $"/Size {xrefRowCount}";
        string widthsEntry = indirectXrefKey == "W"
            ? "/W 99 0 R"
            : indirectWElement ? "/W [1 4 99 0 R]" : "/W [1 4 2]";
        string indexValue = xrefIndex ??
            (oddIndex ? $"[0 {xrefRowCount} 7]" : $"[0 {xrefRowCount}]");
        string index = indirectXrefKey == "Index" ? " /Index 99 0 R" : $" /Index {indexValue}";
        string previous = selfReferentialPrev ? $" /Prev {xrefOffset}" : string.Empty;
        string filter = indirectXrefKey == "Filter"
            ? " /Filter 99 0 R"
            : xrefCryptIdentity
            ? " /Filter /Crypt /DecodeParms << /Name /Identity >>"
            : string.Empty;
        string decodeParms = indirectXrefKey == "DecodeParms" ? " /DecodeParms 99 0 R" : string.Empty;
        string length = indirectXrefKey == "Length" ? "/Length 99 0 R" : $"/Length {rows.Length}";
        Write(
            $"3 0 obj\n<< {typeEntry} {sizeEntry} /Root 2 0 R {widthsEntry}" +
            $"{index}{previous}{filter}{decodeParms} " +
            $"{length} >>\nstream\n");
        output.Write(rows);
        Write($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.Latin1.GetBytes(value));

        static void WriteRow(Span<byte> row, byte type, int field2, int field3)
        {
            row[0] = type;
            row[1] = (byte)(field2 >> 24);
            row[2] = (byte)(field2 >> 16);
            row[3] = (byte)(field2 >> 8);
            row[4] = (byte)field2;
            row[5] = (byte)(field3 >> 8);
            row[6] = (byte)field3;
        }
    }

    private static byte[] NestedDirectStreamFixture()
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.7\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(
            output,
            "1 0 obj\n" +
            "<< /Type /Catalog /Pages 2 0 R /Probe << /Length 0 >>\n" +
            "stream\n\nendstream >>\n" +
            "endobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 3 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] UnreachableCompressedObjectFixture(string shape = "bad-slot")
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");

        byte[] objectStreamPayload = Encoding.ASCII.GetBytes(shape switch
        {
            "unknown-keyword" => "4 0 garbage",
            "unclosed-array" => "4 0 [1 2",
            "unclosed-dictionary" => "4 0 << /A 1",
            "unclosed-literal-string" => "4 0 (unclosed",
            "unclosed-hex-string" => "4 0 <4142",
            _ => "4 0 42",
        });
        int objectStreamOffset = checked((int)output.Position);
        WriteAscii(
            output,
            $"1 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Length {objectStreamPayload.Length} >>\nstream\n");
        output.Write(objectStreamPayload);
        WriteAscii(output, "\nendstream\nendobj\n");

        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "3 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        int xrefOffset = checked((int)output.Position);
        byte[] rows = new byte[6 * 7];
        WriteRow(rows.AsSpan(0, 7), type: 0, field2: 0, field3: 65_535);
        WriteRow(rows.AsSpan(7, 7), type: 1, field2: objectStreamOffset, field3: 0);
        WriteRow(rows.AsSpan(14, 7), type: 1, field2: catalogOffset, field3: 0);
        WriteRow(rows.AsSpan(21, 7), type: 1, field2: pagesOffset, field3: 0);
        WriteRow(
            rows.AsSpan(28, 7),
            type: 2,
            field2: 1,
            field3: shape == "bad-slot" ? 99 : 0);
        WriteRow(rows.AsSpan(35, 7), type: 1, field2: xrefOffset, field3: 0);
        WriteAscii(
            output,
            "5 0 obj\n<< /Type /XRef /Size 6 /Root 2 0 R /W [1 4 2] /Index [0 6] " +
            $"/Length {rows.Length} >>\nstream\n");
        output.Write(rows);
        WriteAscii(output, $"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] FreeXrefRowsFixture(
        bool useXrefStream,
        int rowCount,
        bool malformedTrailer = false)
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n%âãÏÓ\n");
        int xrefOffset = checked((int)output.Position);

        if (useXrefStream)
        {
            byte[] rows = new byte[rowCount * 3];
            for (int i = 0; i < rowCount; i++)
            {
                rows[i * 3] = 0;
                rows[i * 3 + 1] = 0;
                rows[i * 3 + 2] = i == 0 ? (byte)255 : (byte)0;
            }

            Write(
                $"1 0 obj\n<< /Type /XRef /Size {rowCount} /Root 2 0 R /W [1 1 1] " +
                $"/Index [0 {rowCount}] /Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            Write("\nendstream\nendobj\n");
        }
        else
        {
            Write($"xref\n0 {rowCount}\n");
            for (int i = 0; i < rowCount; i++)
                Write(i == 0 ? "0000000000 65535 f \n" : "0000000000 00000 f \n");

            Write(malformedTrailer
                ? "trailer\n42\n"
                : $"trailer\n<< /Size {rowCount} /Root 1 0 R >>\n");
        }

        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.Latin1.GetBytes(value));
    }

    private static byte[] InvalidClassicGenerationFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-1.7\n");
        int objectOffset = checked((int)output.Position);
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        Write("xref\n0 2\n");
        Write("0000000000 65535 f \n");
        Write($"{objectOffset:D10} 70000 n \n");
        Write("trailer\n<< /Size 2 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] ClassicXrefRowFixture(string row)
    {
        using var output = new MemoryStream();
        Write("%PDF-1.7\n");
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        Write("xref\n0 2\n");
        Write("0000000000 65535 f \n");
        Write(row);
        Write("trailer\n<< /Size 2 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] MalformedPageTreeFixture(string shape)
    {
        using PdfDocument source = PdfDocument.Create();
        PdfPage page = source.Pages.Add();
        PdfObjectId scalarId = source.AddObject(PdfValue.Integer(42));
        string rootReference = $"{source.PageTreeRootId.Number} 0 R";
        string pageReference = $"{page.Id.Number} 0 R";
        string catalogReference = $"{source.CatalogId.Number} 0 R";
        string scalarReference = $"{scalarId.Number} 0 R";
        byte[] bytes = source.ToArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            SetProducer = false,
            UpdateModificationDate = false,
        });

        (string marker, string replacement) = shape switch
        {
            "missing-kids" => ("/Kids[", "/Xids["),
            "missing-count" => ("/Count 1", "/Xount 1"),
            "wrong-count" => ("/Count 1", "/Count 0"),
            "dangling-kid" => ($"/Kids[{pageReference}]", $"/Kids[{scalarReference}]"),
            "wrong-parent" => ($"/Parent {rootReference}", $"/Parent {catalogReference}"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        Assert.Equal(marker.Length, replacement.Length);
        byte[] needle = Encoding.ASCII.GetBytes(marker);
        int offset = bytes.AsSpan().IndexOf(needle);
        Assert.True(offset >= 0, $"The generated PDF does not contain '{marker}'.");
        Encoding.ASCII.GetBytes(replacement).CopyTo(bytes, offset);
        return bytes;
    }

    private static byte[] GenerationMismatchFixture(bool useXrefStream)
    {
        using var output = new MemoryStream();
        Write("%PDF-1.7\n");
        int objectOffset = checked((int)output.Position);
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);

        if (useXrefStream)
        {
            byte[] rows = new byte[3 * 7];
            WriteRow(rows.AsSpan(0, 7), type: 0, field2: 0, field3: 65_535);
            WriteRow(rows.AsSpan(7, 7), type: 1, field2: objectOffset, field3: 1);
            WriteRow(rows.AsSpan(14, 7), type: 1, field2: xrefOffset, field3: 0);
            Write("2 0 obj\n<< /Type /XRef /Size 3 /Root 1 1 R /W [1 4 2] /Index [0 3] ");
            Write($"/Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            Write("\nendstream\nendobj\n");
        }
        else
        {
            Write("xref\n0 2\n");
            Write("0000000000 65535 f \n");
            Write($"{objectOffset:D10} 00001 n \n");
            Write("trailer\n<< /Size 2 /Root 1 1 R >>\n");
        }

        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] FreePageEntryFixture()
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        WriteAscii(
            output,
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 4\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "0000000000 00001 f \n");
        WriteAscii(output, "trailer\n<< /Size 4 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] UnknownPageEntryFixture()
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        WriteAscii(
            output,
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        int xrefOffset = checked((int)output.Position);
        byte[] rows = new byte[5 * 7];
        WriteRow(rows.AsSpan(0, 7), type: 0, field2: 0, field3: 65_535);
        WriteRow(rows.AsSpan(7, 7), type: 1, field2: catalogOffset, field3: 0);
        WriteRow(rows.AsSpan(14, 7), type: 1, field2: pagesOffset, field3: 0);
        WriteRow(rows.AsSpan(21, 7), type: 7, field2: 0, field3: 0);
        WriteRow(rows.AsSpan(28, 7), type: 1, field2: xrefOffset, field3: 0);
        WriteAscii(
            output,
            $"4 0 obj\n<< /Type /XRef /Size 5 /Root 1 0 R /W [1 4 2] " +
            $"/Index [0 5] /Length {rows.Length} >>\nstream\n");
        output.Write(rows);
        WriteAscii(output, "\nendstream\nendobj\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] IncrementalTombstoneFixture(bool useXrefStream, byte newestEntryType)
    {
        using var output = new MemoryStream();
        (_, int previousXrefOffset) = WriteBaseDocument(output);
        int newestXrefOffset = checked((int)output.Position);

        if (useXrefStream)
        {
            byte[] rows = new byte[2 * 7];
            WriteRow(
                rows.AsSpan(0, 7),
                type: newestEntryType,
                field2: 0,
                field3: newestEntryType == 0 ? 65_535 : 0);
            WriteRow(rows.AsSpan(7, 7), type: 1, field2: newestXrefOffset, field3: 0);
            WriteAscii(
                output,
                $"3 0 obj\n<< /Type /XRef /Size 4 /Root 1 0 R /Prev {previousXrefOffset} " +
                $"/W [1 4 2] /Index [1 1 3 1] /Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            WriteAscii(output, "\nendstream\nendobj\n");
        }
        else
        {
            WriteAscii(output, "xref\n1 1\n0000000000 65535 f \n");
            WriteAscii(
                output,
                $"trailer\n<< /Size 3 /Root 1 0 R /Prev {previousXrefOffset} >>\n");
        }

        WriteAscii(output, $"startxref\n{newestXrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] HybridTombstoneFixture()
    {
        using var output = new MemoryStream();
        (int catalogOffset, int previousXrefOffset) = WriteBaseDocument(output);

        int xrefStreamOffset = checked((int)output.Position);
        byte[] rows = new byte[2 * 7];
        WriteRow(rows.AsSpan(0, 7), type: 1, field2: catalogOffset, field3: 0);
        WriteRow(rows.AsSpan(7, 7), type: 1, field2: xrefStreamOffset, field3: 0);
        WriteAscii(
            output,
            $"3 0 obj\n<< /Type /XRef /Size 4 /W [1 4 2] /Index [1 1 3 1] " +
            $"/Length {rows.Length} >>\nstream\n");
        output.Write(rows);
        WriteAscii(output, "\nendstream\nendobj\n");

        int currentXrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n1 1\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, "3 1\n");
        WriteAscii(output, $"{xrefStreamOffset:D10} 00000 n \n");
        WriteAscii(
            output,
            $"trailer\n<< /Size 4 /Root 1 0 R /Prev {previousXrefOffset} " +
            $"/XRefStm {xrefStreamOffset} >>\n");
        WriteAscii(output, $"startxref\n{currentXrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static (int CatalogOffset, int XrefOffset) WriteBaseDocument(MemoryStream output)
    {
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 3 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return (catalogOffset, xrefOffset);
    }

    private static byte[] RequiredClassicStructureFixture(string shape)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        string catalogType = shape switch
        {
            "missing-catalog-type" => "/Xype /Catalog",
            "wrong-catalog-type" => "/Type /Catxlog",
            _ => "/Type /Catalog",
        };
        WriteAscii(output, $"1 0 obj\n<< {catalogType} /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        string sizeEntry = shape switch
        {
            "missing-size" => "/Xize 3",
            "indirect-size" => "/Size 1 0 R",
            "nonpositive-size" => "/Size 0",
            "too-small-size" => "/Size 2",
            _ => "/Size 3",
        };
        WriteAscii(output, $"trailer\n<< {sizeEntry} /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] InexactOffsetFixture(bool useXrefStream, bool shiftSectionOffset)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        int recordedCatalogOffset = shiftSectionOffset ? catalogOffset : catalogOffset - 1;
        int recordedSectionOffset = shiftSectionOffset ? xrefOffset - 1 : xrefOffset;

        if (useXrefStream)
        {
            byte[] rows = new byte[4 * 7];
            WriteRow(rows.AsSpan(0, 7), type: 0, field2: 0, field3: 65_535);
            WriteRow(rows.AsSpan(7, 7), type: 1, field2: recordedCatalogOffset, field3: 0);
            WriteRow(rows.AsSpan(14, 7), type: 1, field2: pagesOffset, field3: 0);
            WriteRow(rows.AsSpan(21, 7), type: 1, field2: recordedSectionOffset, field3: 0);
            WriteAscii(
                output,
                $"3 0 obj\n<< /Type /XRef /Size 4 /Root 1 0 R /W [1 4 2] " +
                $"/Index [0 4] /Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            WriteAscii(output, "\nendstream\nendobj\n");
        }
        else
        {
            WriteAscii(output, "xref\n0 3\n");
            WriteAscii(output, "0000000000 65535 f \n");
            WriteAscii(output, $"{recordedCatalogOffset:D10} 00000 n \n");
            WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
            WriteAscii(output, "trailer\n<< /Size 3 /Root 1 0 R >>\n");
        }

        WriteAscii(output, $"startxref\n{recordedSectionOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] XrefGapFixture(bool useXrefStream)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "3 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);

        if (useXrefStream)
        {
            byte[] rows = new byte[4 * 7];
            WriteRow(rows.AsSpan(0, 7), type: 0, field2: 0, field3: 65_535);
            WriteRow(rows.AsSpan(7, 7), type: 1, field2: catalogOffset, field3: 0);
            WriteRow(rows.AsSpan(14, 7), type: 1, field2: pagesOffset, field3: 0);
            WriteRow(rows.AsSpan(21, 7), type: 1, field2: xrefOffset, field3: 0);
            WriteAscii(
                output,
                $"4 0 obj\n<< /Type /XRef /Size 5 /Root 2 0 R /W [1 4 2] " +
                $"/Index [0 1 2 3] /Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            WriteAscii(output, "\nendstream\nendobj\n");
        }
        else
        {
            WriteAscii(output, "xref\n0 1\n0000000000 65535 f \n");
            WriteAscii(output, "2 2\n");
            WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
            WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
            WriteAscii(output, "trailer\n<< /Size 4 /Root 2 0 R >>\n");
        }

        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] UnloadedLiveObjectFixture(string shape)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int unusedOffset = checked((int)output.Position);
        int headerGeneration = shape == "wrong-generation" ? 1 : 0;
        string body = shape == "unknown-keyword" ? "garbage" : "42";
        WriteAscii(output, $"3 {headerGeneration} obj\n{body}\n");
        if (shape != "missing-endobj")
            WriteAscii(output, "endobj\n");

        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 4\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, $"{unusedOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 4 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] UnclosedDictionaryFixture(string shape)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R");
        WriteAscii(output, shape == "catalog" ? "\nendobj\n" : " >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 3 /Root 1 0 R");
        WriteAscii(output, shape == "trailer" ? "\n" : " >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] DuplicateDictionaryKeyFixture(string shape)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        string catalogDuplicate = shape == "catalog" ? "/Type /Catalog " : string.Empty;
        WriteAscii(
            output,
            $"1 0 obj\n<< /Type /Catalog {catalogDuplicate}/Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        string trailerDuplicate = shape == "trailer" ? "/Size 3 " : string.Empty;
        WriteAscii(
            output,
            $"trailer\n<< /Size 3 {trailerDuplicate}/Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] SuffixTokenOffsetFixture(bool sectionOffset)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogTokenStart = checked((int)output.Position);
        if (sectionOffset)
            WriteAscii(output, "1 0 obj\n");
        else
            WriteAscii(output, "11 0 obj\n");
        WriteAscii(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        int xrefPrefix = checked((int)output.Position);
        if (sectionOffset)
            WriteAscii(output, "x");
        int xrefOffset = sectionOffset ? xrefPrefix + 1 : xrefPrefix;
        int catalogOffset = sectionOffset ? catalogTokenStart : catalogTokenStart + 1;
        WriteAscii(output, "xref\n0 3\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 3 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] MaskedClassicSectionSizeFixture()
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int previousXref = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n");
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 2 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{previousXref}\n%%EOF\n");

        int latestXref = checked((int)output.Position);
        WriteAscii(output, "xref\n0 0\n");
        WriteAscii(output, $"trailer\n<< /Size 3 /Root 1 0 R /Prev {previousXref} >>\n");
        WriteAscii(output, $"startxref\n{latestXref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] MaskedPreviousRevisionSizeFixture()
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int object3Offset = checked((int)output.Position);
        WriteAscii(output, "3 0 obj\nnull\nendobj\n");
        int object4Offset = checked((int)output.Position);
        WriteAscii(output, "4 0 obj\nnull\nendobj\n");
        int object5Offset = checked((int)output.Position);
        WriteAscii(output, "5 0 obj\nnull\nendobj\n");

        int baseXref = checked((int)output.Position);
        WriteAscii(output, "xref\n0 6\n0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, $"{object3Offset:D10} 00000 n \n");
        WriteAscii(output, $"{object4Offset:D10} 00000 n \n");
        WriteAscii(output, $"{object5Offset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 6 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{baseXref}\n%%EOF\n");

        int updatedCatalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int middleXref = checked((int)output.Position);
        WriteAscii(output, "xref\n1 1\n");
        WriteAscii(output, $"{updatedCatalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size 2 /Root 1 0 R /Prev {baseXref} >>\n");
        WriteAscii(output, $"startxref\n{middleXref}\n%%EOF\n");

        int latestXref = checked((int)output.Position);
        WriteAscii(output, "xref\n0 0\n");
        WriteAscii(output, $"trailer\n<< /Size 6 /Root 1 0 R /Prev {middleXref} >>\n");
        WriteAscii(output, $"startxref\n{latestXref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] StreamCatalogFixture()
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(
            output,
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Length 0 >>\nstream\n\nendstream\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 3 /Root 1 0 R >>\n");
        WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] ForwardCrossReferenceLinkFixture(bool hybrid)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");
        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        int firstSection = checked((int)output.Position);
        WriteAscii(output, "xref\n0 3\n0000000000 65535 f \n");
        WriteAscii(output, $"{catalogOffset:D10} 00000 n \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        string linkKey = hybrid ? "XRefStm" : "Prev";
        int linkValuePosition = checked((int)output.Position) +
            $"trailer\n<< /Size {(hybrid ? 4 : 3)} /Root 1 0 R /{linkKey} ".Length;
        WriteAscii(
            output,
            $"trailer\n<< /Size {(hybrid ? 4 : 3)} /Root 1 0 R /{linkKey} 9999999999 >>\n");
        WriteAscii(output, $"startxref\n{firstSection}\n%%EOF\n");

        int forwardSection = checked((int)output.Position);
        if (hybrid)
        {
            byte[] row = new byte[7];
            WriteRow(row, type: 1, field2: forwardSection, field3: 0);
            WriteAscii(
                output,
                "3 0 obj\n<< /Type /XRef /Size 4 /W [1 4 2] /Index [3 1] " +
                $"/Length {row.Length} >>\nstream\n");
            output.Write(row);
            WriteAscii(output, "\nendstream\nendobj\n");
        }
        else
        {
            WriteAscii(output, "xref\n0 0\ntrailer\n<< /Size 3 /Root 1 0 R >>\n");
            WriteAscii(output, $"startxref\n{forwardSection}\n%%EOF\n");
        }

        int latestSection = checked((int)output.Position);
        WriteAscii(output, "xref\n0 0\n");
        WriteAscii(
            output,
            $"trailer\n<< /Size {(hybrid ? 4 : 3)} /Root 1 0 R /Prev {firstSection} >>\n");
        WriteAscii(output, $"startxref\n{latestSection}\n%%EOF\n");

        Encoding.ASCII.GetBytes($"{forwardSection:D10}").CopyTo(output.GetBuffer(), linkValuePosition);
        return output.ToArray();
    }

    private static byte[] LinearizedFixture(bool incrementalUpdate, int declaredLengthDelta = 0)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n%âãÏÓ\n");

        int linearizationOffset = checked((int)output.Position);
        WriteAscii(
            output,
            "2 0 obj\n" +
            "<< /Linearized 1 /L 0000000000 /H [0000000000 0000000000] " +
            "/O 4 /E 0000000000 /N 1 /T 0000000000 >>\n" +
            "endobj\n");

        int firstXrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n2 4\n");
        WriteAscii(output, $"{linearizationOffset:D10} 00000 n \n");
        int catalogRow = checked((int)output.Position);
        WriteAscii(output, "0000000000 00000 n \n");
        int pageRow = checked((int)output.Position);
        WriteAscii(output, "0000000000 00000 n \n");
        int hintRow = checked((int)output.Position);
        WriteAscii(output, "0000000000 00000 n \n");
        WriteAscii(
            output,
            "trailer\n<< /Size 6 /Root 3 0 R /Prev 0000000000 >>\n");
        int previousOffsetPosition = checked((int)output.Position) - " >>\n".Length - 10;

        int catalogOffset = checked((int)output.Position);
        WriteAscii(output, "3 0 obj\n<< /Type /Catalog /Pages 1 0 R >>\nendobj\n");

        int hintOffset = checked((int)output.Position);
        WriteAscii(output, "5 0 obj\n<< /Length 0 /S 0 >>\nstream\n\nendstream\nendobj\n");
        int hintLength = checked((int)output.Position) - hintOffset;

        int pageOffset = checked((int)output.Position);
        WriteAscii(
            output,
            "4 0 obj\n" +
            "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 612 792] /Resources <<>> >>\n" +
            "endobj\n");
        int firstPageEnd = checked((int)output.Position);

        int pagesOffset = checked((int)output.Position);
        WriteAscii(output, "1 0 obj\n<< /Type /Pages /Kids [4 0 R] /Count 1 >>\nendobj\n");

        int mainXrefOffset = checked((int)output.Position);
        WriteAscii(output, "xref\n0 2\n");
        int mainFirstEntryWhitespace = checked((int)output.Position) - 1;
        WriteAscii(output, "0000000000 65535 f \n");
        WriteAscii(output, $"{pagesOffset:D10} 00000 n \n");
        WriteAscii(output, "trailer\n<< /Size 2 >>\n");
        WriteAscii(output, $"startxref\n{firstXrefOffset}\n%%EOF\n");
        int originalLength = checked((int)output.Position);

        PatchDecimal(output, linearizationOffset, "/L ", originalLength + declaredLengthDelta);
        PatchDecimal(output, linearizationOffset, "/H [", hintOffset);
        PatchDecimal(output, linearizationOffset, $"/H [{hintOffset:D10} ", hintLength);
        PatchDecimal(output, linearizationOffset, "/E ", firstPageEnd);
        PatchDecimal(output, linearizationOffset, "/T ", mainFirstEntryWhitespace);
        PatchDecimalAt(output, previousOffsetPosition, mainXrefOffset);
        PatchDecimalAt(output, catalogRow, catalogOffset);
        PatchDecimalAt(output, pageRow, pageOffset);
        PatchDecimalAt(output, hintRow, hintOffset);

        if (incrementalUpdate)
        {
            int objectOffset = checked((int)output.Position);
            WriteAscii(output, "6 0 obj\nnull\nendobj\n");
            int updateXrefOffset = checked((int)output.Position);
            WriteAscii(output, "xref\n6 1\n");
            WriteAscii(output, $"{objectOffset:D10} 00000 n \n");
            WriteAscii(
                output,
                $"trailer\n<< /Size 7 /Root 3 0 R /Prev {firstXrefOffset} >>\n");
            WriteAscii(output, $"startxref\n{updateXrefOffset}\n%%EOF\n");
        }

        return output.ToArray();

        static void PatchDecimal(MemoryStream stream, int searchStart, string marker, int value)
        {
            byte[] buffer = stream.GetBuffer();
            ReadOnlySpan<byte> haystack = buffer.AsSpan(searchStart, checked((int)stream.Length) - searchStart);
            int relative = haystack.IndexOf(Encoding.ASCII.GetBytes(marker));
            Assert.True(relative >= 0, $"Fixture marker '{marker}' was not found.");
            PatchDecimalAt(stream, searchStart + relative + marker.Length, value);
        }

        static void PatchDecimalAt(MemoryStream stream, int position, int value) =>
            Encoding.ASCII.GetBytes($"{value:D10}").CopyTo(stream.GetBuffer(), position);
    }

    private static void WriteAscii(MemoryStream output, string value) =>
        output.Write(Encoding.Latin1.GetBytes(value));

    private static byte[] ObjectZeroFixture(bool useXrefStream, string shape)
    {
        using var output = new MemoryStream();
        Write("%PDF-1.7\n");
        int objectOffset = checked((int)output.Position);
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = checked((int)output.Position);

        if (useXrefStream)
        {
            bool missing = shape == "missing";
            byte[] rows = new byte[(missing ? 2 : 3) * 7];
            int position = 0;
            if (!missing)
            {
                byte type = shape == "in-use" ? (byte)1 : (byte)0;
                int generation = shape == "bad-generation" ? 0 : 65_535;
                WriteRow(rows.AsSpan(position, 7), type, field2: 0, field3: generation);
                position += 7;
            }

            WriteRow(rows.AsSpan(position, 7), type: 1, field2: objectOffset, field3: 0);
            WriteRow(rows.AsSpan(position + 7, 7), type: 1, field2: xrefOffset, field3: 0);
            string index = missing ? "[1 2]" : "[0 3]";
            Write($"2 0 obj\n<< /Type /XRef /Size 3 /Root 1 0 R /W [1 4 2] /Index {index} ");
            Write($"/Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            Write("\nendstream\nendobj\n");
        }
        else
        {
            bool missing = shape == "missing";
            Write(missing ? "xref\n1 1\n" : "xref\n0 2\n");
            if (!missing)
            {
                string flag = shape == "in-use" ? "n" : "f";
                int generation = shape == "bad-generation" ? 0 : 65_535;
                Write($"0000000000 {generation:D5} {flag} \n");
            }

            Write($"{objectOffset:D10} 00000 n \n");
            Write("trailer\n<< /Size 2 /Root 1 0 R >>\n");
        }

        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private static void WriteRow(Span<byte> row, byte type, int field2, int field3)
    {
        row[0] = type;
        row[1] = (byte)(field2 >> 24);
        row[2] = (byte)(field2 >> 16);
        row[3] = (byte)(field2 >> 8);
        row[4] = (byte)field2;
        row[5] = (byte)(field3 >> 8);
        row[6] = (byte)field3;
    }

    private static byte[] InvalidWideXrefFieldFixture(bool compressedEntry)
    {
        using var output = new MemoryStream();
        Write("%PDF-2.0\n");
        int xrefOffset = checked((int)output.Position);
        byte[] rows = new byte[2 * 17];
        WriteWideRow(rows.AsSpan(0, 17), type: 0, field2: 0, field3: 65_535);
        WriteWideRow(
            rows.AsSpan(17, 17),
            type: compressedEntry ? (byte)2 : (byte)1,
            field2: compressedEntry ? (ulong)int.MaxValue + 1 : (ulong)xrefOffset,
            field3: compressedEntry ? 0 : (ulong)ushort.MaxValue + 1);
        Write(
            $"1 0 obj\n<< /Type /XRef /Size 2 /Root 1 0 R /W [1 8 8] /Index [0 2] " +
            $"/Length {rows.Length} >>\nstream\n");
        output.Write(rows);
        Write($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));

        static void WriteWideRow(Span<byte> row, byte type, ulong field2, ulong field3)
        {
            row[0] = type;
            for (int i = 0; i < 8; i++)
            {
                row[1 + i] = (byte)(field2 >> (56 - i * 8));
                row[9 + i] = (byte)(field3 >> (56 - i * 8));
            }
        }
    }

    private static byte[] HybridPointingToClassicXrefFixture()
    {
        using var output = new MemoryStream();
        Write("%PDF-1.7\n");
        int fakeStreamOffset = checked((int)output.Position);
        Write("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\n");
        int mainOffset = checked((int)output.Position);
        Write("xref\n0 1\n0000000000 65535 f \n");
        Write($"trailer\n<< /Size 1 /Root 1 0 R /XRefStm {fakeStreamOffset} >>\n");
        Write($"startxref\n{mainOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    private sealed class RecordingPolicy(
        int maxStructuralEntries = 1_048_576,
        int maxParsedValues = int.MaxValue) : IPdfStreamDecodePolicy
    {
        private int _structuralEntries;
        private int _parsedValues;

        public int MaxStructuralEntries { get; } = maxStructuralEntries;

        public void ConsumeStructuralEntries(int count)
        {
            if (count <= 0 || count > MaxStructuralEntries - _structuralEntries)
                throw new PdfFormatException($"The cross-reference data exceeds {MaxStructuralEntries} structural entries.");

            _structuralEntries += count;
        }

        public void ConsumeParsedValues(int count)
        {
            if (count <= 0 || count > maxParsedValues - _parsedValues)
                throw new PdfFormatException($"The parser exceeds {maxParsedValues} parsed values.");

            _parsedValues += count;
        }

        internal List<PdfName> Types { get; } = [];

        internal int Calls { get; private set; }

        internal int ParsedValues => _parsedValues;

        public ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter)
        {
            Record(stream);

            return PdfFilterPipeline.Decode(stream.RawData, stream.Dictionary, out undecodedFilter);
        }

        public PdfBoundedDecodeResult DecodeBounded(PdfStream stream, int maxDecodedBytes)
        {
            Record(stream);
            return PdfFilterPipeline.DecodeBounded(stream.RawData, stream.Dictionary, maxDecodedBytes);
        }

        private void Record(PdfStream stream)
        {
            Calls++;
            if (stream.Dictionary.GetName(PdfNames.Type) is { } type)
                Types.Add(type);
        }
    }

    private sealed class PolicyResolver(RecordingPolicy policy) :
        IPdfResolver,
        IPdfStreamDecodePolicyProvider
    {
        public IPdfStreamDecodePolicy DecodePolicy => policy;

        public PdfValue Resolve(PdfValue value) => value;
    }

    private sealed class ThrowingPolicy : IPdfStreamDecodePolicy
    {
        public int MaxStructuralEntries => 1_048_576;

        public void ConsumeStructuralEntries(int count) => _ = count;

        public void ConsumeParsedValues(int count) => _ = count;

        internal int Calls { get; private set; }

        public ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter)
        {
            _ = stream;
            Calls++;
            undecodedFilter = null;
            throw new PdfFormatException("policy failure");
        }

        public PdfBoundedDecodeResult DecodeBounded(PdfStream stream, int maxDecodedBytes)
        {
            _ = stream;
            _ = maxDecodedBytes;
            Calls++;
            throw new PdfFormatException("policy failure");
        }
    }
}
