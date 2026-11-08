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
    public byte[] Send(ReadOnlyMemory<byte> request);

    /// <summary>Asynchronously sends a DER-encoded RFC 3161 request.</summary>
    /// <param name="request">The encoded request.</param>
    /// <param name="cancellationToken">Stops the wait for the authority.</param>
    /// <returns>The DER-encoded <c>TimeStampResp</c> the authority answered with.</returns>
    public ValueTask<byte[]> SendAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Send(request));
    }
}

/// <summary>
/// Posts timestamp requests over HTTP as RFC 3161 §3.4 describes. The supplied
/// <see cref="HttpClient"/> remains owned by the caller and can therefore carry proxy, client
/// certificate and handler policy shared with the rest of the application.
/// </summary>
public sealed class HttpTimestampAuthority : ITimestampAuthority
{
    private static readonly HttpClient SharedClient = new();
    private static readonly TimeSpan MaximumCancellationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1L);
    private readonly Uri _authority;
    private readonly HttpClient _client;
    private TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private int _maxResponseBytes = 1024 * 1024;

    /// <summary>Uses the process-wide HTTP connection pool.</summary>
    /// <param name="authority">The absolute HTTP or HTTPS endpoint.</param>
    public HttpTimestampAuthority(Uri authority)
        : this(authority, SharedClient)
    {
    }

    /// <summary>Uses an application-managed HTTP client.</summary>
    /// <param name="authority">The absolute HTTP or HTTPS endpoint.</param>
    /// <param name="client">A reusable client owned by the application.</param>
    public HttpTimestampAuthority(Uri authority, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(client);

        if (!authority.IsAbsoluteUri ||
            (authority.Scheme != Uri.UriSchemeHttp && authority.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A timestamp authority must be an absolute HTTP or HTTPS URI.", nameof(authority));
        }

        _authority = authority;
        _client = client;
    }

    /// <summary>
    /// Maximum time for one request. Set <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to rely only on
    /// the caller's cancellation token and the supplied <see cref="HttpClient"/> policy.
    /// </summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        init
        {
            if (value != System.Threading.Timeout.InfiniteTimeSpan &&
                (value <= TimeSpan.Zero || value > MaximumCancellationTimeout))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"The timeout must be positive, no greater than {MaximumCancellationTimeout}, or infinite.");
            }

            _timeout = value;
        }
    }

    /// <summary>Largest RFC 3161 response accepted from the authority; one MiB by default.</summary>
    public int MaxResponseBytes
    {
        get => _maxResponseBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxResponseBytes = value;
        }
    }

    /// <inheritdoc />
    public byte[] Send(ReadOnlyMemory<byte> request) =>
        SendAsync(request).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask<byte[]> SendAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _authority)
        {
            Content = new ByteArrayContent(request.ToArray()),
        };
        message.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        message.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/timestamp-reply"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(Timeout);

        using HttpResponseMessage response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
        {
            throw new HttpRequestException(
                $"The timestamp authority declared a {declared}-byte response; the limit is {MaxResponseBytes} bytes.");
        }

        using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(MaxResponseBytes, 16 * 1024));
        byte[] buffer = new byte[Math.Min(MaxResponseBytes, 80 * 1024)];

        while (true)
        {
            int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();

            if (read > MaxResponseBytes - output.Length)
            {
                throw new HttpRequestException(
                    $"The timestamp authority response exceeds the {MaxResponseBytes}-byte limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }
    }
}
