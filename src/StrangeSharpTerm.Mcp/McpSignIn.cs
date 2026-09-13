using System.Diagnostics;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// Signing in to a hosted server.
///
/// A hosted server usually wants a person rather than a key, and says so with a
/// 401 carrying a pointer to its own metadata. The SDK follows that chain —
/// protected-resource metadata, then the authorization server's — registers this
/// app as a client where the server allows it, and refuses a server that does
/// not advertise S256 rather than trusting it to be checking. See docs/adr/0007
/// for what is the SDK's and what is ours.
///
/// Ours is the redirect: a loopback port bound before the browser opens, and the
/// rule that a renewal never opens one.
/// </summary>
public static class McpSignIn
{
    /// <summary>How long a person is given to finish in the browser before the listener gives up.</summary>
    public static TimeSpan Patience { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The OAuth configuration for a server, or null when it needs none.
    /// </summary>
    /// <param name="interactive">
    /// Whether a browser may be opened. False everywhere except a sign-in the
    /// user started from Settings: a request failing mid-turn is the worst
    /// possible moment to seize the screen, so the transport renews silently or
    /// gives up.
    /// </param>
    /// <param name="openBrowser">How a URL is put in front of a person. Replaced in tests.</param>
    public static ClientOAuthOptions? For(
        McpServerConfig server,
        McpCredentials credentials,
        bool interactive,
        Action<Uri>? openBrowser = null,
        ISecretStore? store = null)
    {
        // A pasted bearer token wins over all of this. Someone who supplied one
        // has said how their server authenticates, and starting a discovery
        // underneath them would be second-guessing it.
        if (server.Transport != McpTransport.Http || credentials.HasBearer)
            return null;

        var options = new ClientOAuthOptions
        {
            RedirectUri = LoopbackRedirect.RedirectUris[0],
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "StrangeSharpTerm",
            },
            // Where a sign-in is kept, and where a silent renewal writes the new
            // token: the SDK owns the exchange, this owns the storage.
            TokenCache = new McpTokenCache(server, store),
        };

        options.AuthorizationCallbackHandler = interactive
            ? (context, cancellationToken) => Authorize(context, openBrowser, cancellationToken)
            : Refuse;

        return options;
    }

    /// <summary>
    /// Opens the browser and waits for the code on a port bound first.
    ///
    /// The order matters: a code arriving at a port nobody is listening on is a
    /// sign-in that fails after the person has already approved it.
    /// </summary>
    private static async Task<AuthorizationResult?> Authorize(
        AuthorizationCallbackContext context,
        Action<Uri>? openBrowser,
        CancellationToken cancellationToken)
    {
        using var redirect = LoopbackRedirect.Bind();
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(Patience);

        (openBrowser ?? Open)(context.AuthorizationUri);

        var query = await redirect.Wait(patience.Token);

        if (query.TryGetValue("error", out var error))
        {
            var description = query.GetValueOrDefault("error_description", "");
            throw new McpException(
                $"The sign-in was refused: {error}{(description.Length > 0 ? $" — {description}" : "")}");
        }

        if (!query.TryGetValue("code", out var code) || code.Length == 0)
            throw new McpException("The sign-in came back without an authorization code.");

        // The issuer is RFC 9207's answer to a mix-up attack, and handing it
        // back is what lets the SDK check that the code came from the server it
        // sent the person to.
        return new AuthorizationResult
        {
            Code = code,
            State = query.GetValueOrDefault("state") ?? "",
            // RFC 9207's answer to a mix-up attack. Handing it back is what lets
            // the SDK check the code came from the server it sent the person to.
            Iss = query.GetValueOrDefault("iss") ?? "",
        };
    }

    /// <summary>
    /// A renewal never opens a browser.
    ///
    /// Returning null is how this says so: the SDK renews silently where the
    /// server issued a refresh token, and gives up where it did not. The visible
    /// login is something a person starts from Settings, and where a server
    /// issues no refresh token, signing in again is a button rather than a
    /// mystery.
    /// </summary>
    private static Task<AuthorizationResult?> Refuse(
        AuthorizationCallbackContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuthorizationResult?>(null);

    private static void Open(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            throw new McpException($"A browser could not be opened. Go to {url} by hand. ({e.Message})", e);
        }
    }

    /// <summary>
    /// Signs in, and keeps whatever the exchange produced.
    ///
    /// The tokens are read back off the options rather than out of the
    /// transport, because the SDK owns the exchange and this owns the storage.
    /// </summary>
    public static async Task SignIn(
        McpServerConfig server,
        ISecretStore? store = null,
        Action<Uri>? openBrowser = null,
        CancellationToken cancellationToken = default)
    {
        if (server.Transport != McpTransport.Http)
            throw new McpException($"{server.Name} runs on this machine and has nothing to sign in to.");

        var before = McpTokens.Read(server, store);
        var options = For(server, before with { Bearer = null }, interactive: true, openBrowser, store)
            ?? throw new McpException($"{server.Name} does not use a sign-in.");

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
            OAuth = options,
        });

        // Connecting is the sign-in: the 401 is what starts the discovery, and
        // the token cache above is what keeps the result. Nothing is written
        // here, which is why a silent renewal during an ordinary request is
        // stored the same way as this one.
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }
}
