using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>How loudly a field says what it says.</summary>
public enum FieldEmphasis
{
    None,

    /// <summary>Worth noticing: agent forwarding, a forward that cannot bind.</summary>
    Warning,

    /// <summary>Worth stopping at: a host that accepts any key.</summary>
    Danger,
}

public sealed record DetailField(string Label, string Value, FieldEmphasis Emphasis = FieldEmphasis.None)
{
    // As two flags rather than an enum the markup has to interpret: a style
    // class takes a boolean, and the theme then owns what warning looks like.
    public bool IsWarning => Emphasis == FieldEmphasis.Warning;

    public bool IsDanger => Emphasis == FieldEmphasis.Danger;
}

public sealed record ForwardSummary(string Flag, string Specification, string Name, bool NeedsRoot);

/// <summary>
/// A host as the detail pane states it: everything resolved, with the
/// inheritance already applied, so what is shown is what a connection would
/// actually use.
///
/// The formatting lives here rather than in the view because the interesting
/// parts are decisions — what an unset port means, which values deserve a
/// warning — and those are worth testing.
/// </summary>
/// <param name="credential">
/// The credential this host resolves to, if any. Passed by name rather than
/// looked up here: this view model is given a resolved connection precisely so
/// it does not need the tree.
/// </param>
public sealed class HostDetailViewModel(
    ResolvedConnection resolved,
    DashboardViewModel? dashboard = null,
    Credential? credential = null)
{
    /// <summary>
    /// What the server says about itself, once someone asks. Null in the tests
    /// that are only about the fields.
    /// </summary>
    public DashboardViewModel? Dashboard { get; } = dashboard;

    private ResolvedSettings Settings => resolved.Settings;

    public Connection Connection => resolved.Connection;

    public string Name => Connection.Name;

    /// <summary><c>user@host:port</c>, with each part left out when it is not set.</summary>
    public string UserAtHost
    {
        get
        {
            var user = Settings.Username is { } username ? $"{username}@" : "";
            var port = Settings.Port is { } number ? $":{number}" : "";
            return $"{user}{Connection.Hostname}{port}";
        }
    }

    public IReadOnlyList<string> Tags => Connection.Tags;

    /// <summary>The folders this host inherits from, outermost first.</summary>
    public IReadOnlyList<NodeId> InheritanceChain => resolved.InheritanceChain;

    public IReadOnlyList<DetailField> Fields
    {
        get
        {
            var fields = new List<DetailField>
            {
                new("Port", Settings.Port?.ToString() ?? "22 (default)"),
                // The Swift app said "from ssh_config" here, which was true when it
                // shelled out to ssh. In process nothing reads that file at connect
                // time, so the honest answer is the account this app runs as.
                new("Username", Settings.Username ?? $"{System.Environment.UserName} (yours)"),
            };

            if (Settings.JumpHosts.Count > 0)
                fields.Add(new DetailField("Jump hosts", string.Join(" → ", Settings.JumpHosts)));

            fields.Add(new DetailField("Keep-alive", $"{Settings.KeepAliveInterval}s"));
            fields.Add(new DetailField("Host keys", HostKeyDescription,
                Settings.HostKeyPolicy == HostKeyPolicy.AcceptAny ? FieldEmphasis.Danger : FieldEmphasis.None));
            fields.Add(new DetailField("Agent forwarding", Settings.ForwardAgent ? "Enabled" : "Disabled",
                Settings.ForwardAgent ? FieldEmphasis.Warning : FieldEmphasis.None));
            fields.Add(new DetailField("Keys",
                Settings.IdentityFiles.Count == 0 ? "From the agent" : string.Join(", ", Settings.IdentityFiles)));

            // Which shared credential answered, when one did. Without this the
            // username and keys above appear from nowhere: they are the
            // credential's, and nothing else on this pane says so.
            if (credential is { } shared)
                fields.Add(new DetailField("Credential", $"{shared.Name} ({shared.Method.Label()})"));

            return fields;
        }
    }

    public string HostKeyDescription => Settings.HostKeyPolicy switch
    {
        HostKeyPolicy.Strict => "Only keys already trusted",
        HostKeyPolicy.AcceptAny => "Any key, without asking",
        _ => "Ask on first contact",
    };

    public IReadOnlyList<ForwardSummary> Forwards =>
    [
        .. Settings.PortForwards.Select(forward => new ForwardSummary(
            forward.SshFlag, forward.SshSpecification, forward.Name, forward.RequiresPrivilegedBind)),
    ];

    /// <summary>Environment, sorted, because a dictionary's own order means nothing to a reader.</summary>
    public IReadOnlyList<(string Key, string Value)> Environment =>
    [
        .. Settings.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value)),
    ];
}
