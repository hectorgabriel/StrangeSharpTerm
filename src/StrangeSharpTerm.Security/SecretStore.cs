using GitCredentialManager;

namespace StrangeSharpTerm.Security;

/// <summary>
/// Where passwords and key passphrases are kept.
///
/// Only those. Private keys themselves are deliberately never copied into app
/// storage: an agent or an on-disk identity file is always preferable, because
/// key material that never enters our address space cannot be leaked by us.
/// </summary>
public interface ISecretStore
{
    /// <summary>The stored secret, or null when there is none. Absence is ordinary, not a failure.</summary>
    string? Secret(string account);

    void SetSecret(string account, string secret);

    /// <summary>Removing something that is not there is the desired end state, so it is not an error.</summary>
    void RemoveSecret(string account);

    bool HasSecret(string account);
}

/// <summary>The secret store could not be reached. The message is fit to show a person.</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The operating system's own store, through Git Credential Manager's
/// implementation: the login keychain on macOS, Credential Manager on Windows.
///
/// The service name matches the one the Swift app used, so on macOS the items are
/// the same generic passwords it created. Reading one it wrote may prompt for
/// permission the first time, because macOS binds an item to the binary that
/// created it.
/// </summary>
public sealed class PlatformSecretStore : ISecretStore
{
    /// <summary>
    /// What a secret is filed under.
    ///
    /// macOS keeps the Swift app's service name, so its keychain items are the
    /// same generic passwords and a migrating user's saved secrets keep working.
    /// Windows Credential Manager is addressed by URL through this library and
    /// throws on a name that is not a URI, so there it gets a URI-shaped one.
    /// </summary>
    public static string DefaultService { get; } =
        OperatingSystem.IsMacOS() ? "dev.strangeterm.credentials" : "strangesharpterm://credentials";

    private readonly ICredentialStore _store;
    private readonly string _service;

    public PlatformSecretStore(string? service = null, string @namespace = "strangesharpterm")
    {
        _service = service ?? DefaultService;
        try
        {
            _store = CredentialManager.Create(@namespace);
        }
        catch (Exception e)
        {
            throw new SecretStoreException("This system has no usable credential store.", e);
        }
    }

    public string? Secret(string account) =>
        Guard(() => _store.Get(_service, account)?.Password, "The stored secret could not be read.");

    public void SetSecret(string account, string secret) =>
        Guard<object?>(() => { _store.AddOrUpdate(_service, account, secret); return null; },
            "The secret could not be saved.");

    public void RemoveSecret(string account) =>
        Guard<object?>(() => { _store.Remove(_service, account); return null; },
            "The secret could not be removed.");

    public bool HasSecret(string account) => Secret(account) is not null;

    private static T Guard<T>(Func<T> operation, string message)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is not SecretStoreException)
        {
            // The platform error is meaningless to a person; keep it for the log
            // and lead with a sentence they can act on.
            throw new SecretStoreException($"{message} ({e.Message})", e);
        }
    }
}

/// <summary>
/// A store that keeps secrets for the life of the process and no longer.
///
/// For tests, and for a run where no platform store is reachable — losing a
/// passphrase on exit is better than refusing to connect at all.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public string? Secret(string account) => _secrets.GetValueOrDefault(account);

    public void SetSecret(string account, string secret) => _secrets[account] = secret;

    public void RemoveSecret(string account) => _secrets.Remove(account);

    public bool HasSecret(string account) => _secrets.ContainsKey(account);
}
