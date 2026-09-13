using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// What a signed-in server left behind.
/// </summary>
/// <param name="ClientId">
/// From dynamic registration, if the server allowed it. Kept because
/// re-registering on every launch would leave a trail of client records behind
/// on somebody's authorization server.
/// </param>
public sealed record McpCredentials(
    [property: JsonPropertyName("bearer")] string? Bearer = null,
    [property: JsonPropertyName("accessToken")] string? AccessToken = null,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken = null,
    [property: JsonPropertyName("clientId")] string? ClientId = null,
    [property: JsonPropertyName("clientSecret")] string? ClientSecret = null)
{
    public static McpCredentials None { get; } = new();

    /// <summary>
    /// A token the user pasted themselves.
    ///
    /// It wins over everything else: someone who supplied one has said how their
    /// server authenticates, and starting a discovery underneath them would be
    /// second-guessing it.
    /// </summary>
    public bool HasBearer => Bearer is { Length: > 0 };

    public bool HasSignIn => AccessToken is { Length: > 0 };

    public bool IsEmpty => !HasBearer && !HasSignIn;
}

/// <summary>
/// Where a connected server's secrets live: the platform store, under a service
/// of its own.
///
/// Not beside connection credentials and not beside provider API keys. Three
/// different things that are revoked, rotated and lost independently, and a
/// store shared between them makes losing one cost the others.
/// </summary>
public static class McpTokens
{
    /// <summary>
    /// What these are filed under. Windows Credential Manager is addressed by
    /// URL and throws on a name that is not a URI, so there it gets a
    /// URI-shaped one -- the same split the other two stores make.
    /// </summary>
    public static string Service { get; } =
        OperatingSystem.IsMacOS() ? "dev.strangeterm.mcp" : "strangesharpterm://mcp";

    public static McpCredentials Read(McpServerConfig server, ISecretStore? store = null)
    {
        try
        {
            var stored = (store ?? new PlatformSecretStore(Service)).Secret(server.SecretAccount);
            return stored is null
                ? McpCredentials.None
                : JsonSerializer.Deserialize<McpCredentials>(stored) ?? McpCredentials.None;
        }
        catch (Exception e) when (e is SecretStoreException or JsonException)
        {
            // No reachable store, or something unreadable in it, is the same
            // answer as no credentials: a row that says "not signed in".
            return McpCredentials.None;
        }
    }

    public static void Write(McpServerConfig server, McpCredentials credentials, ISecretStore? store = null)
    {
        var target = store ?? new PlatformSecretStore(Service);
        try
        {
            if (credentials.IsEmpty && credentials.ClientId is null)
                target.RemoveSecret(server.SecretAccount);
            else
                target.SetSecret(server.SecretAccount, JsonSerializer.Serialize(credentials));
        }
        catch (SecretStoreException)
        {
            // Losing a token on exit is better than refusing to connect. The
            // next sign-in asks again, which is a nuisance and not a failure.
        }
    }

    public static void Forget(McpServerConfig server, ISecretStore? store = null)
    {
        try
        {
            (store ?? new PlatformSecretStore(Service)).RemoveSecret(server.SecretAccount);
        }
        catch (SecretStoreException)
        {
        }
    }
}
