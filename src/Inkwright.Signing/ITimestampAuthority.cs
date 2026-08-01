namespace Inkwright.Signing;

/// <summary>
/// Carries an RFC 3161 request to a timestamp authority and brings its response back. The default
/// implementation posts over HTTP; supplying another lets a caller reach an authority that lives
/// behind something else — a proxy, a queue, or a test double.
/// </summary>
public interface ITimestampAuthority
{
    /// <summary>Sends a DER-encoded RFC 3161 <c>TimeStampReq</c> and returns the raw response.</summary>
    /// <param name="request">The encoded request.</param>
    /// <returns>The DER-encoded <c>TimeStampResp</c> the authority answered with.</returns>
    byte[] Send(ReadOnlyMemory<byte> request);
}

/// <summary>Posts a timestamp request the way RFC 3161 §3.4 describes.</summary>
internal sealed class HttpTimestampAuthority(Uri authority) : ITimestampAuthority
{
    public byte[] Send(ReadOnlyMemory<byte> request)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var content = new ByteArrayContent(request.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");

        using HttpResponseMessage response = client.PostAsync(authority, content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }
}
