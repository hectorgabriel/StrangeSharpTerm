using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// A credential being written, and the secret that goes with it.
///
/// The two are kept apart on purpose. The credential is inventory — an ordinary
/// JSON file — and the secret belongs in the operating system's own store; the
/// only thing that crosses is the account name derived from the id. This holds
/// both for as long as the form is open and then puts each where it goes.
/// </summary>
public sealed partial class CredentialDraft : ObservableObject
{
    private readonly Credential _original;
    private readonly ISecretStore _secrets;

    private CredentialDraft(Credential original, ISecretStore secrets, bool isNew)
    {
        _original = original;
        _secrets = secrets;
        IsNew = isNew;
        Name = original.Name;
        Username = original.Username ?? "";
        Method = original.Method;
        IdentityFile = original.IdentityFile ?? "";
        HadSecret = !isNew && Has(original);
    }

    public static CredentialDraft New(ISecretStore secrets, int sortIndex) =>
        new(new Credential { Name = "", SortIndex = sortIndex }, secrets, isNew: true);

    public static CredentialDraft For(Credential credential, ISecretStore secrets) =>
        new(credential, secrets, isNew: false);

    public bool IsNew { get; }

    public string Title => IsNew ? "New credential" : $"Edit {_original.Name}";

    /// <summary>Whether the store already holds one. Shown rather than the secret itself.</summary>
    public bool HadSecret { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Applied to hosts that name no username of their own.</summary>
    [ObservableProperty]
    public partial string Username { get; set; }

    [ObservableProperty]
    public partial CredentialMethod Method { get; set; }

    [ObservableProperty]
    public partial string IdentityFile { get; set; }

    /// <summary>
    /// What to store, or empty to leave whatever is there alone. A field that
    /// showed the current secret would be a field that leaks it to a screenshot.
    /// </summary>
    [ObservableProperty]
    public partial string Secret { get; set; } = "";

    /// <summary>Ticking this removes the stored secret when the form is saved.</summary>
    [ObservableProperty]
    public partial bool ForgetSecret { get; set; }

    /// <summary>The methods, for the picker.</summary>
    public static IReadOnlyList<Choice> Methods { get; } =
    [
        new(CredentialMethod.Agent.Label(), CredentialMethod.Agent),
        new(CredentialMethod.IdentityFile.Label(), CredentialMethod.IdentityFile),
        new(CredentialMethod.Password.Label(), CredentialMethod.Password),
    ];

    /// <summary>An agent credential has no secret of ours and no file to name.</summary>
    public bool WantsIdentityFile => Method == CredentialMethod.IdentityFile;

    public bool WantsSecret => Method != CredentialMethod.Agent;

    /// <summary>What the secret field is for, which differs by method.</summary>
    public string SecretLabel => Method == CredentialMethod.Password ? "Password" : "Passphrase";

    /// <summary>What the store already holds, said without saying it.</summary>
    public string SecretNote => (HadSecret, Method) switch
    {
        (_, CredentialMethod.Agent) => "The agent holds the key. Nothing is stored here.",
        (true, _) => "One is stored. Type to replace it, or leave this empty to keep it.",
        _ => "Optional: a key with no passphrase needs none.",
    };

    public IEnumerable<string> Problems()
    {
        if (Name.Trim().Length == 0)
            yield return "A credential needs a name.";

        if (WantsIdentityFile && IdentityFile.Trim().Length == 0)
            yield return "A key credential needs the path to the key.";

        // A password credential with no password cannot authenticate, and saying
        // so now is better than a refusal at connect time.
        if (Method == CredentialMethod.Password && !HadSecret && Secret.Length == 0)
            yield return "A password credential needs a password.";
    }

    public bool IsValid => !Problems().Any();

    public string ProblemSummary => string.Join(" ", Problems());

    /// <summary>The credential as it would be saved. The secret is not part of it.</summary>
    public Credential Applied() => _original with
    {
        Name = Name.Trim(),
        Username = Blank(Username),
        Method = Method,
        IdentityFile = WantsIdentityFile ? Blank(IdentityFile) : null,
    };

    /// <summary>
    /// Writes the secret where secrets go, and nowhere else.
    ///
    /// Separate from <see cref="Applied"/> because the two have different
    /// failure modes: the inventory is a file we own, and the store is the
    /// operating system's and can refuse.
    /// </summary>
    public void SaveSecret(Credential saved)
    {
        if (!saved.CanHaveSecret || ForgetSecret)
        {
            // An agent credential has nothing to keep, and forgetting is a
            // choice: either way the old secret goes rather than lingering under
            // an account nothing points at.
            _secrets.RemoveSecret(saved.SecretAccount);
            return;
        }

        if (Secret.Length > 0)
            _secrets.SetSecret(saved.SecretAccount, Secret);
    }

    partial void OnMethodChanged(CredentialMethod value)
    {
        OnPropertyChanged(nameof(WantsIdentityFile));
        OnPropertyChanged(nameof(WantsSecret));
        OnPropertyChanged(nameof(SecretLabel));
        OnPropertyChanged(nameof(SecretNote));
        Revalidate();
    }

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnIdentityFileChanged(string value) => Revalidate();

    partial void OnSecretChanged(string value) => Revalidate();

    private void Revalidate()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ProblemSummary));
    }

    private bool Has(Credential credential)
    {
        try
        {
            return credential.CanHaveSecret && _secrets.HasSecret(credential.SecretAccount);
        }
        catch (SecretStoreException)
        {
            // A store that cannot be reached is not a reason to refuse to open
            // the form; it only means this cannot be said with confidence.
            return false;
        }
    }

    private static string? Blank(string value) => value.Trim().Length == 0 ? null : value.Trim();
}
