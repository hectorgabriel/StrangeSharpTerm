using System.Net;
using System.Text;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// The redirect a browser comes back to after a sign-in.
///
/// <c>http://127.0.0.1</c> on a port bound <em>before</em> the browser opens,
/// not a custom URL scheme. A scheme like <c>strangesharpterm://</c> can be
/// claimed by any application on the machine, which would hand the authorization
/// code to whoever registered it last; a loopback port this process already
/// holds cannot be taken that way, and RFC 8252 says as much.
///
/// Several ports are registered with the server so that a later sign-in can use
/// whichever is free without having to register a second client.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    /// <summary>
    /// The ports offered at registration. Arbitrary, high, and fixed: they go
    /// into a client registration that outlives the process, so they cannot be
    /// chosen afresh each time.
    /// </summary>
    public static IReadOnlyList<int> Ports { get; } = [51737, 51738, 51739, 51740];

    /// <summary>Every address a registration should list, so a later sign-in has a choice.</summary>
    public static IReadOnlyList<Uri> RedirectUris { get; } =
        [.. Ports.Select(port => new Uri($"http://127.0.0.1:{port}/callback"))];

    private readonly HttpListener _listener;

    private LoopbackRedirect(HttpListener listener, Uri redirect)
    {
        _listener = listener;
        Redirect = redirect;
    }

    /// <summary>The address that was actually bound, which is what the request must name.</summary>
    public Uri Redirect { get; }

    /// <summary>
    /// Binds the first free port, before anything else happens.
    ///
    /// Before, because a code arriving at a port nobody is listening on is a
    /// sign-in that fails after the person has already approved it — and because
    /// binding it first is the whole reason a loopback redirect cannot be
    /// intercepted.
    /// </summary>
    public static LoopbackRedirect Bind()
    {
        foreach (var uri in RedirectUris)
        {
            var listener = new HttpListener();
            // The trailing slash is what HttpListener wants as a prefix.
            listener.Prefixes.Add($"http://127.0.0.1:{uri.Port}/");
            try
            {
                listener.Start();
                return new LoopbackRedirect(listener, uri);
            }
            catch (HttpListenerException)
            {
                // Taken, by another copy of this app or by something else.
                listener.Close();
            }
        }

        throw new McpException(
            $"No loopback port was free. Tried {string.Join(", ", Ports)}.");
    }

    /// <summary>
    /// Waits for the browser to come back, and answers it with a page.
    ///
    /// The query is returned whole rather than picked apart here: what a caller
    /// needs from it — the code, the state, and the issuer RFC 9207 adds —
    /// belongs to whoever is validating them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> Wait(CancellationToken cancellationToken = default)
    {
        HttpListenerContext context;
        try
        {
            var incoming = _listener.GetContextAsync();
            // GetContextAsync does not take a token, so the wait is raced
            // against one: a cancelled sign-in must not leave a thread parked on
            // a socket for ever.
            var finished = await Task.WhenAny(incoming, Forever(cancellationToken));
            if (finished != incoming)
                throw new OperationCanceledException(cancellationToken);
            context = await incoming;
        }
        catch (HttpListenerException e)
        {
            throw new McpException("The sign-in was interrupted before the browser came back.", e);
        }

        var query = context.Request.QueryString;
        var values = query.AllKeys
            .OfType<string>()
            .ToDictionary(key => key, key => query[key] ?? "", StringComparer.Ordinal);

        await Answer(context, values.ContainsKey("code"));
        return values;
    }

    /// <summary>
    /// What the browser shows afterwards.
    ///
    /// A page rather than a redirect somewhere: sending a browser on to a page
    /// we do not control at the end of an authorization is an unnecessary place
    /// for a code to appear in somebody's history.
    /// </summary>
    private static async Task Answer(HttpListenerContext context, bool succeeded)
    {
        var body = Encoding.UTF8.GetBytes($"""
            <!doctype html>
            <meta charset="utf-8">
            <title>StrangeSharpTerm</title>
            <body style="font:16px system-ui;padding:3rem;color:#e7eaf0;background:#1a1d23">
            <p>{(succeeded ? "Signed in. You can close this tab and go back to StrangeSharpTerm." : "The sign-in did not complete. Go back to StrangeSharpTerm and try again.")}</p>
            """);

        context.Response.StatusCode = succeeded ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static Task Forever(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    public void Dispose()
    {
        if (_listener.IsListening)
            _listener.Stop();
        _listener.Close();
    }
}
