using System.Net;
using System.Text;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// A provider that answers with a recorded stream and keeps what it was sent.
///
/// This is how the whole path is driven without an account: the request body,
/// the headers, the streaming read and the decoder all run exactly as they would
/// against the real thing. The pieces have their own tests; only this says
/// whether they fit together.
/// </summary>
internal sealed class StubEndpoint : HttpMessageHandler
{
    private readonly string _stream;
    private readonly HttpStatusCode _status;

    internal StubEndpoint(string stream, HttpStatusCode status = HttpStatusCode.OK)
    {
        _stream = stream;
        _status = status;
    }

    /// <summary>What was sent, verbatim.</summary>
    internal string? Body { get; private set; }

    internal HttpRequestMessage? Request { get; private set; }

    internal HttpClient Client() => new(this) { BaseAddress = new Uri("https://stub.invalid/") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_stream, Encoding.UTF8, "text/event-stream"),
        };
        return response;
    }
}
