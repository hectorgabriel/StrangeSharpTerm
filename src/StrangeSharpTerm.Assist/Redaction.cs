using System.Text.RegularExpressions;

namespace StrangeSharpTerm.Assist;

/// <summary>Text with its secrets removed, and how many were taken out.</summary>
/// <param name="Count">
/// Shown beside the disclosure. It is the only evidence the redactor ran, and a
/// count of zero is itself worth saying.
/// </param>
public sealed record Redacted(string Text, int Count)
{
    public static Redacted Nothing { get; } = new("", 0);
}

/// <summary>
/// Takes the secrets out of terminal output before it goes to a provider.
///
/// This runs on every request rather than being something to remember. A real
/// session contains a <c>cat .env</c>, an exported token, a <c>docker login</c>,
/// and the person asking about a stack trace is not thinking about any of that.
///
/// An assignment keeps its <em>name</em> and loses its value: "which variable"
/// is usually the question being asked and is not itself the secret. Everything
/// else is replaced whole.
///
/// It over-redacts on purpose. A redacted line that held nothing costs a little
/// context; a token that reaches a provider cannot be taken back.
/// </summary>
public static partial class Redaction
{
    public const string Marker = "[redacted]";

    /// <summary>
    /// Scrubs text, in order: whole key blocks first, then assignments, then the
    /// shapes that stand alone.
    ///
    /// The order is what keeps the count honest. <c>TOKEN=sk-ant-...</c> is one
    /// secret, and running the provider-key rule first would take it out a
    /// second time as a bare string and report two.
    /// </summary>
    public static Redacted Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Redacted.Nothing;

        var count = 0;
        var scrubbed = text;

        scrubbed = PrivateKeyBlock().Replace(scrubbed, _ => Take("[redacted private key]"));
        scrubbed = Assignment().Replace(scrubbed, match =>
            IsSecretish(match.Groups["name"].Value)
                ? Take(match.Groups["name"].Value + match.Groups["op"].Value + match.Groups["scheme"].Value + Marker)
                : match.Value);
        scrubbed = PasswordFlag().Replace(scrubbed, match => Take(match.Groups["flag"].Value + Marker));
        scrubbed = BearerHeader().Replace(scrubbed, match => Take(match.Groups["scheme"].Value + " " + Marker));
        scrubbed = ConnectionString().Replace(scrubbed, match =>
            Take(match.Groups["prefix"].Value + Marker + "@"));
        scrubbed = Jwt().Replace(scrubbed, _ => Take(Marker));
        scrubbed = ProviderKey().Replace(scrubbed, _ => Take(Marker));

        return new Redacted(scrubbed, count);

        string Take(string replacement)
        {
            count++;
            return replacement;
        }
    }

    /// <summary>
    /// Whether a name is the kind of thing whose value is a secret.
    ///
    /// Matched as a substring, because the names that matter in practice are
    /// <c>DB_PASSWORD</c>, <c>aws_secret_access_key</c> and
    /// <c>GITHUB_TOKEN</c> rather than the bare words.
    /// </summary>
    internal static bool IsSecretish(string name)
    {
        var bare = name.Trim('"', '\'', ' ');
        return SecretishName().IsMatch(bare);
    }

    // A whole PEM block, however it is labelled: RSA, EC, OPENSSH, or none.
    [GeneratedRegex(
        """-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----""",
        RegexOptions.None)]
    private static partial Regex PrivateKeyBlock();

    // NAME=value or "name": value, in shell, .env, JSON and YAML alike. The
    // value stops at whitespace unless it is quoted, so a redacted assignment
    // does not swallow the rest of the line.
    //
    // An authentication scheme in front of the value is part of the name rather
    // than part of the secret. Without that, "Authorization: Bearer eyJ…" loses
    // the word Bearer and keeps the token, which is exactly backwards.
    [GeneratedRegex(
        """(?<name>["']?[A-Za-z_][A-Za-z0-9_.\-]*["']?)(?<op>\s*[:=]\s*)(?<scheme>(?:Bearer|Basic|Token)\s+)?(?:"[^"\n]*"|'[^'\n]*'|[^\s;,&|)\]}]+)""",
        RegexOptions.None)]
    private static partial Regex Assignment();

    [GeneratedRegex(
        "(?:pass(?:word|wd)?|secret|token|api[_.-]?key|access[_.-]?key|private[_.-]?key|credential|auth|pwd|passphrase|session[_.-]?id|client[_.-]?secret)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretishName();

    // docker login -p, mysql -p, curl -u: the value is the next word, or is
    // stuck to the flag as -pSECRET.
    [GeneratedRegex(
        """(?<flag>(?:^|\s)(?:-p|-u|--password|--token|--passphrase|--api-key)[=\s]?)(?!\s)[^\s]+""",
        RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFlag();

    [GeneratedRegex(
        """(?<scheme>[Aa]uthorization\s*:\s*(?:Bearer|Basic|Token)|[Bb]earer)\s+[A-Za-z0-9\-._~+/=]{8,}""",
        RegexOptions.None)]
    private static partial Regex BearerHeader();

    // scheme://user:password@host -- the user is kept, because which account
    // failed to connect is usually the question.
    [GeneratedRegex(
        """(?<prefix>[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s:/@]+:)[^\s@/]+@""",
        RegexOptions.None)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(
        """eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]+""",
        RegexOptions.None)]
    private static partial Regex Jwt();

    // The recognisable provider formats. Incomplete by nature -- a new one
    // arrives every few months -- which is why the assignment rule above is the
    // one doing most of the work.
    [GeneratedRegex(
        """\b(?:sk-ant-[A-Za-z0-9\-_]{16,}|sk-[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z\-_]{30,}|glpat-[A-Za-z0-9\-_]{16,}|npm_[A-Za-z0-9]{30,}|dckr_pat_[A-Za-z0-9\-_]{16,})\b""",
        RegexOptions.None)]
    private static partial Regex ProviderKey();
}
