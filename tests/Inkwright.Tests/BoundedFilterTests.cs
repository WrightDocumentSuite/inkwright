using Inkwright.Cos;
using Inkwright.Filters;
using Xunit;

namespace Inkwright.Tests;

public class BoundedFilterTests
{
    private static readonly byte[] Payload = "%PDF-strict-filter-test"u8.ToArray();

    [Fact]
    public void AsciiHexStrictPathRejectsACharacterThePublicDecoderTolerates()
    {
        byte[] valid = AsciiHexFilter.Encode(Payload);
        byte[] malformed = [.. valid[..4], (byte)'G', .. valid[4..]];

        Assert.Equal(Payload, AsciiHexFilter.Decode(malformed));
        AssertInvalid("ASCIIHexDecode", malformed);
    }

    [Fact]
    public void Ascii85StrictPathRequiresTheCompleteTerminator()
    {
        byte[] valid = Ascii85Filter.Encode(Payload);
        byte[] malformed = valid[..^1];

        Assert.Equal(Payload, Ascii85Filter.Decode(malformed));
        AssertInvalid("ASCII85Decode", malformed);
    }

    [Fact]
    public void RunLengthStrictPathRequiresTheEndOfDataMarker()
    {
        byte[] valid = RunLengthFilter.Encode(Payload);
        byte[] malformed = valid[..^1];

        Assert.Equal(Payload, RunLengthFilter.Decode(malformed));
        AssertInvalid("RunLengthDecode", malformed);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    public void AsciiStrictPathRejectsWhitespaceAfterTheTerminator(string filter)
    {
        byte[] valid = filter == "ASCIIHexDecode"
            ? AsciiHexFilter.Encode(Payload)
            : Ascii85Filter.Encode(Payload);
        byte[] malformed = [.. valid, (byte)' '];

        byte[] tolerant = filter == "ASCIIHexDecode"
            ? AsciiHexFilter.Decode(malformed)
            : Ascii85Filter.Decode(malformed);
        Assert.Equal(Payload, tolerant);
        AssertInvalid(filter, malformed);
    }

    [Fact]
    public void RunLengthStrictPathRejectsBytesAfterTheEndOfDataMarker()
    {
        byte[] valid = RunLengthFilter.Encode(Payload);
        byte[] malformed = [.. valid, 0x00];

        Assert.Equal(Payload, RunLengthFilter.Decode(malformed));
        AssertInvalid("RunLengthDecode", malformed);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    public void AValidAsciiPayloadPastTheCapReportsTheLimitRatherThanInvalidData(string filter)
    {
        byte[] encoded = filter == "ASCIIHexDecode"
            ? AsciiHexFilter.Encode(Payload)
            : Ascii85Filter.Encode(Payload);

        PdfBoundedDecodeResult result = DecodeBounded(filter, encoded, Payload.Length - 1);

        Assert.Equal(PdfBoundedDecodeStatus.LimitExceeded, result.Status);
    }

    [Fact]
    public void FlateStrictPathAcceptsACompleteMember()
    {
        PdfBoundedDecodeResult result = DecodeBounded(
            "FlateDecode",
            FlateFilter.Encode(Payload),
            1_024);

        Assert.Equal(PdfBoundedDecodeStatus.Complete, result.Status);
        Assert.Equal(Payload, result.Data.ToArray());
    }

    [Fact]
    public void FlateStrictPathDoesNotSkipLeadingWhitespace()
    {
        byte[] valid = FlateFilter.Encode(Payload);
        byte[] malformed = [(byte)' ', .. valid];

        Assert.Equal(Payload, FlateFilter.Decode(malformed));
        AssertInvalid("FlateDecode", malformed);
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("corrupt")]
    public void FlateStrictPathValidatesTheAdlerTrailer(string mutation)
    {
        byte[] malformed = FlateFilter.Encode(Payload);
        if (mutation == "truncate")
        {
            malformed = malformed[..^4];
        }
        else
        {
            malformed[^1] ^= 0x01;
        }

        if (mutation == "truncate")
            Assert.Equal(Payload, FlateFilter.Decode(malformed));
        AssertInvalid("FlateDecode", malformed);
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("checksum")]
    public void FlateStrictPathRejectsBytesAfterTheZlibMember(string suffix)
    {
        byte[] valid = FlateFilter.Encode(Payload);
        byte[] malformed = suffix == "byte"
            ? [.. valid, 0x00]
            : [.. valid, .. valid[^4..]];

        AssertInvalid("FlateDecode", malformed);
    }

    [Fact]
    public void AFirstCryptFilterConsumesNoWorkAtTheExactOutputLimit()
    {
        byte[] encoded = AsciiHexFilter.Encode(Payload);
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Array(new PdfArray(
                PdfValue.Name(PdfName.Get("Crypt")),
                PdfValue.Name(PdfName.Get("ASCIIHexDecode")))),
        };
        var stream = new PdfStream(dictionary, encoded);

        PdfBoundedDecodeResult result = stream.DecodeBounded(Payload.Length);

        Assert.Equal(PdfBoundedDecodeStatus.Complete, result.Status);
        Assert.Equal(Payload, result.Data.ToArray());
        Assert.Equal(Payload.Length, result.ProducedBytes);
    }

    [Theory]
    [InlineData("FlateDecode", -1, true)]
    [InlineData("FlateDecode", 0, false)]
    [InlineData("FlateDecode", 1, false)]
    [InlineData("ASCIIHexDecode", -1, true)]
    [InlineData("ASCIIHexDecode", 0, false)]
    [InlineData("ASCIIHexDecode", 1, false)]
    [InlineData("ASCII85Decode", -1, true)]
    [InlineData("ASCII85Decode", 0, false)]
    [InlineData("ASCII85Decode", 1, false)]
    [InlineData("RunLengthDecode", -1, true)]
    [InlineData("RunLengthDecode", 0, false)]
    [InlineData("RunLengthDecode", 1, false)]
    public void ValidFilterPayloadsRespectCapBoundaries(string filter, int delta, bool limited)
    {
        byte[] encoded = filter switch
        {
            "FlateDecode" => FlateFilter.Encode(Payload),
            "ASCIIHexDecode" => AsciiHexFilter.Encode(Payload),
            "ASCII85Decode" => Ascii85Filter.Encode(Payload),
            _ => RunLengthFilter.Encode(Payload),
        };

        PdfBoundedDecodeResult result = DecodeBounded(filter, encoded, Payload.Length + delta);

        Assert.Equal(
            limited ? PdfBoundedDecodeStatus.LimitExceeded : PdfBoundedDecodeStatus.Complete,
            result.Status);
        if (!limited)
            Assert.Equal(Payload, result.Data.ToArray());
    }

    [Fact]
    public void ImageCodecCannotHideAFilterSuffixFromTheBoundedPreScan()
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Array(new PdfArray(
                PdfValue.Name(PdfNames.DCTDecode),
                PdfValue.Name(PdfName.Get("ASCIIHexDecode")))),
        };
        var stream = new PdfStream(dictionary, Payload);

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.InvalidParameters, result.Status);
        Assert.Equal(PdfNames.DCTDecode, result.TerminalFilter);
        Assert.Equal(0, result.ProducedBytes);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("integer")]
    public void SingleFilterRequiresNullOrDictionaryDecodeParameters(string shape)
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.DCTDecode),
            [PdfNames.DecodeParms] = shape == "array"
                ? PdfValue.Array(new PdfArray(PdfValue.Null))
                : PdfValue.Integer(7),
        };
        var stream = new PdfStream(dictionary, Payload);

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.InvalidParameters, result.Status);
        Assert.Equal(0, result.ProducedBytes);
    }

    [Theory]
    [InlineData("short", "DCTDecode")]
    [InlineData("long", "DCTDecode")]
    [InlineData("dictionary", "DCTDecode")]
    [InlineData("non-dictionary-entry", "DCTDecode")]
    [InlineData("short", "JPXDecode")]
    [InlineData("long", "JPXDecode")]
    public void MultipleFiltersRequireAnExactNullOrDictionaryDecodeParametersArray(
        string shape,
        string terminalFilter)
    {
        PdfValue decodeParameters = shape switch
        {
            "short" => PdfValue.Array(new PdfArray(PdfValue.Null)),
            "long" => PdfValue.Array(new PdfArray(PdfValue.Null, PdfValue.Null, PdfValue.Null)),
            "dictionary" => PdfValue.Dictionary(new PdfDictionary()),
            _ => PdfValue.Array(new PdfArray(PdfValue.Null, PdfValue.Integer(7))),
        };
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Array(new PdfArray(
                PdfValue.Name(PdfName.Get("ASCIIHexDecode")),
                PdfValue.Name(PdfName.Get(terminalFilter)))),
            [PdfNames.DecodeParms] = decodeParameters,
        };
        var stream = new PdfStream(dictionary, AsciiHexFilter.Encode(Payload));

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.InvalidParameters, result.Status);
        Assert.Equal(0, result.ProducedBytes);
    }

    [Theory]
    [InlineData("DCTDecode")]
    [InlineData("JPXDecode")]
    public void ExactDecodeParametersArrayAllowsATerminalImageCodec(string terminalFilter)
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Array(new PdfArray(
                PdfValue.Name(PdfName.Get("ASCIIHexDecode")),
                PdfValue.Name(PdfName.Get(terminalFilter)))),
            [PdfNames.DecodeParms] = PdfValue.Array(new PdfArray(
                PdfValue.Dictionary(new PdfDictionary()),
                PdfValue.Null)),
        };
        var stream = new PdfStream(dictionary, AsciiHexFilter.Encode(Payload));

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.UndecodedFilter, result.Status);
        Assert.Equal(terminalFilter, result.TerminalFilter?.Value);
        Assert.Equal(Payload, result.Data.ToArray());
    }

    [Theory]
    [InlineData("Fl")]
    [InlineData("A85")]
    [InlineData("AHx")]
    [InlineData("RL")]
    [InlineData("DCT")]
    [InlineData("CCF")]
    [InlineData("LZW")]
    public void BoundedOrdinaryStreamPathRejectsInlineOnlyFilterAbbreviations(string filter)
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get(filter)),
        };
        var stream = new PdfStream(dictionary, Payload);

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.UndecodedFilter, result.Status);
        Assert.Equal(filter, result.TerminalFilter?.Value);
        Assert.Equal(0, result.ProducedBytes);
    }

    [Fact]
    public void BoundedOrdinaryStreamPathDoesNotInterpretTheInlineFilterKey()
    {
        byte[] encoded = FlateFilter.Encode(Payload);
        var dictionary = new PdfDictionary
        {
            [PdfName.Get("F")] = PdfValue.Name(PdfNames.FlateDecode),
        };
        var stream = new PdfStream(dictionary, encoded);

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.UndecodedFilter, result.Status);
        Assert.Null(result.TerminalFilter);
        Assert.Equal(encoded, result.Data.ToArray());
        Assert.Equal(0, result.ProducedBytes);
    }

    [Theory]
    [InlineData("DP")]
    [InlineData("FDecodeParms")]
    public void BoundedOrdinaryStreamPathDoesNotInterpretNonCanonicalDecodeParameters(string key)
    {
        byte[] predictorFramed = [0, .. Payload];
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.FlateDecode),
            [PdfName.Get(key)] = PdfValue.Dictionary(new PdfDictionary
            {
                [PdfNames.Predictor] = PdfValue.Integer(12),
                [PdfNames.Columns] = PdfValue.Integer(Payload.Length),
            }),
        };
        var stream = new PdfStream(dictionary, FlateFilter.Encode(predictorFramed));

        PdfBoundedDecodeResult result = stream.DecodeBounded(1_024);

        Assert.Equal(PdfBoundedDecodeStatus.Complete, result.Status);
        Assert.Equal(predictorFramed, result.Data.ToArray());
    }

    private static void AssertInvalid(string filter, byte[] encoded)
    {
        PdfBoundedDecodeResult result = DecodeBounded(filter, encoded, 1_024);

        Assert.Equal(PdfBoundedDecodeStatus.InvalidData, result.Status);
    }

    private static PdfBoundedDecodeResult DecodeBounded(string filter, byte[] encoded, int limit)
    {
        var dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get(filter)),
        };
        var stream = new PdfStream(dictionary, encoded);

        return stream.DecodeBounded(limit);
    }
}
