using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// Where a host's password for <c>sudo</c> comes from, and whether there is one.
///
/// Its own class so the whole path can be tested in one piece: the switch a
/// person turned on, the credential the host resolves to now, and the store the
/// secret is read from. Three things that are each right on their own and can
/// still be wired wrong -- this project once saved API keys to the right store
/// and read them from another, and every constant said the stores were right.
/// </summary>
/// <param name="secrets">
/// The connection store: the same one SSH authentication reads this password
/// from. Not the assistant's own store, which holds provider keys.
/// </param>
public sealed class SudoPasswords(Func<InventoryTree> tree, Func<ISecretStore> secrets, IReadOnlySet<NodeId> allowed)
{
    /// <summary>
    /// The password to give sudo on this host, or null.
    ///
    /// Null unless a person turned the switch on for this host <em>and</em> the
    /// credential it resolves to now -- its own or a folder's -- signs in with a
    /// password. The switch is the permission and the credential is the
    /// password; neither is enough alone, and a host moved to a key since the
    /// switch was turned on gives sudo nothing.
    /// </summary>
    public string? For(NodeId host)
    {
        if (!allowed.Contains(host))
            return null;

        var inventory = tree();
        var resolved = inventory.Resolve(host);
        return resolved.Settings.CredentialId is { } id
            && inventory.Credentials.GetValueOrDefault(id) is { Method: CredentialMethod.Password } credential
                ? secrets().Secret(credential.SecretAccount)
                : null;
    }
}
