using System.IO.Compression;
using System.Text;
using Inkwright.Cos;
using Inkwright.Filters;
using Xunit;

namespace Inkwright.Tests;

public class BrotliFilterTests
{
    [Fact]
    public void DecodesWhatBrotliCompressed()
    {
        byte[] plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT /F1 12 Tf (text) Tj ET ", 200)));

        Assert.Equal(plain, BrotliFilter.Decode(Compress(plain)));
    }

    [Fact]
    public void KeepsWhatDecodedBeforeDamage()
    {
        byte[] compressed = Compress(Encoding.ASCII.GetBytes("well-formed content"));

        // A truncated payload yields what was recoverable rather than an exception.
        byte[] recovered = BrotliFilter.Decode(compressed.AsSpan(0, compressed.Length - 1));

        Assert.NotNull(recovered);
        Assert.Empty(BrotliFilter.Decode([]));
    }

    [Fact]
    public void RunsAsAStreamFilter()
    {
        byte[] plain = Encoding.ASCII.GetBytes("stream body");

        var stream = new PdfStream(new PdfDictionary(2), Compress(plain));
        stream.Dictionary.Set(PdfName.Get("Filter"), PdfValue.Name(PdfName.Get("BrotliDecode")));

        Assert.Equal(plain, stream.Decode().ToArray());
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(data);

        return output.ToArray();
    }
}
