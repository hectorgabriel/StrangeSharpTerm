using ModelContextProtocol.Authentication;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// The SDK's token cache, over the platform store.
///
/// This is the seam that makes a sign-in survive a relaunch, and it is the whole
/// of what this app contributes to token handling: the SDK owns the exchange and
/// the refresh, and this owns where the result is kept. Keeping them apart is
/// what lets a refresh happen silently inside a request without anything here
/// having to know it did.
/// </summary>
public sealed class McpTokenCache(McpServerConfig server, ISecretStore? store = null) : ITokenCache
{
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        var kept = McpTokens.Read(server, store);
        if (!kept.HasSignIn)
            return ValueTask.FromResult<TokenContainer?>(null);

        return ValueTask.FromResult<TokenContainer?>(new TokenContainer
        {
            // Bearer: the only type either an authorization server or this app
            // has any use for.
            TokenType = "Bearer",
            AccessToken = kept.AccessToken!,
            RefreshToken = kept.RefreshToken,
            ClientId = kept.ClientId,
            ClientSecret = kept.ClientSecret,
            // Obtained long enough ago that the SDK treats it as expired and
            // refreshes. Storing an expiry and trusting it across a relaunch
            // would mean sending a token we believe is good and finding out it
            // is not, mid-question; a refresh costs one round trip and cannot
            // be wrong that way.
            ObtainedAt = DateTimeOffset.MinValue,
            ExpiresIn = 0,
        });
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
    {
        var kept = McpTokens.Read(server, store);
        McpTokens.Write(
            server,
            kept with
            {
                AccessToken = tokens.AccessToken,
                // A server that issues no refresh token on a renewal must not
                // lose the one it issued at sign-in.
                RefreshToken = tokens.RefreshToken ?? kept.RefreshToken,
                ClientId = tokens.ClientId ?? kept.ClientId,
                ClientSecret = tokens.ClientSecret ?? kept.ClientSecret,
            },
            store);
        return ValueTask.CompletedTask;
    }
}
