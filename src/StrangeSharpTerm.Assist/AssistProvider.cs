using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// Which backend answers. One at a time: a pane is a conversation, and a
/// conversation whose respondent changes halfway through is not one.
/// </summary>
public enum AssistProviderId
{
    Claude,
    DeepSeek,
}

/// <summary>
/// What is true of a provider regardless of any conversation: its name, its
/// default model, where its data goes, and where its key is kept.
///
/// Where the data goes is here rather than in a help page because it is part of
/// choosing one. Terminal output is going to that country.
/// </summary>
public sealed record AssistProvider
{
    public required AssistProviderId Id { get; init; }

    /// <summary>As the pane header says it.</summary>
    public required string Name { get; init; }

    public required string DefaultModel { get; init; }

    /// <summary>
    /// Model names are a free-text field with these offered as a menu. They
    /// change faster than this app ships, and a model released after a build
    /// should not need a new one.
    /// </summary>
    public required IReadOnlyList<string> KnownModels { get; init; }

    /// <summary>The country the request lands in, said plainly.</summary>
    public required string DataGoesTo { get; init; }

    /// <summary>
    /// An environment variable that overrides the stored key, which is what
    /// makes a development build usable without touching the real keychain.
    /// </summary>
    public required string KeyVariable { get; init; }

    /// <summary>
    /// Its own account in the store, so configuring a second provider does not
    /// overwrite the first.
    /// </summary>
    public string KeyAccount => Id.ToString().ToLowerInvariant();

    public static AssistProvider Claude { get; } = new()
    {
        Id = AssistProviderId.Claude,
        Name = "Claude",
        DefaultModel = "claude-opus-5",
        KnownModels = ["claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5"],
        DataGoesTo = "Anthropic (United States)",
        KeyVariable = "ANTHROPIC_API_KEY",
    };

    public static AssistProvider DeepSeek { get; } = new()
    {
        Id = AssistProviderId.DeepSeek,
        Name = "DeepSeek",
        DefaultModel = "deepseek-v4-pro",
        KnownModels = ["deepseek-v4-pro", "deepseek-chat", "deepseek-reasoner"],
        DataGoesTo = "DeepSeek (China)",
        KeyVariable = "DEEPSEEK_API_KEY",
    };

    public static IReadOnlyList<AssistProvider> All { get; } = [Claude, DeepSeek];

    public static AssistProvider For(AssistProviderId id) => id == AssistProviderId.DeepSeek ? DeepSeek : Claude;
}

/// <summary>
/// Where the API keys live.
///
/// A store of their own, separate from connection credentials: an API key is not
/// a server secret and has no business in the same bucket as passphrases.
/// Deleting every saved password should not log you out of a provider, and
/// revoking a provider key should not cost you a host.
/// </summary>
public static class AssistKeys
{
    /// <summary>
    /// What a key is filed under. Windows Credential Manager is addressed by URL
    /// and throws on a name that is not a URI, so there it gets a URI-shaped one
    /// -- the same split <see cref="PlatformSecretStore.DefaultService"/> makes.
    /// </summary>
    public static string Service { get; } =
        OperatingSystem.IsMacOS() ? "dev.strangeterm.assist" : "strangesharpterm://assist";

    /// <summary>
    /// The key for a provider: the environment first, then the store.
    ///
    /// Null when there is none, which is ordinary -- the pane says so and offers
    /// Settings, rather than failing at the moment someone asks a question.
    /// </summary>
    public static string? Key(AssistProvider provider, ISecretStore? store = null)
    {
        if (Environment.GetEnvironmentVariable(provider.KeyVariable) is { Length: > 0 } fromEnvironment)
            return fromEnvironment;

        try
        {
            return (store ?? new PlatformSecretStore(Service)).Secret(provider.KeyAccount);
        }
        catch (SecretStoreException)
        {
            // No reachable store is the same answer as no key: it is something
            // to say in Settings, not something to throw out of a getter.
            return null;
        }
    }
}
