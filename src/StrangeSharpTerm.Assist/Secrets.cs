using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// The secrets one conversation has had taken out of what it saw, kept on this
/// side of the wire so they can be put back where a person approved them.
///
/// Redaction used to be one-way: a secret became <c>[redacted]</c> and was gone.
/// That is right for the provider and wrong for the person. A kubeadm join
/// command carries a bootstrap token, and a plan that captures it on the control
/// plane and runs it on a worker captured the word <c>[redacted]</c> and ran
/// that instead; a file saved from the findings could only say the value was
/// withheld. The model never needs the value. It needs a name for it.
///
/// So each secret becomes a marker of its own -- <c>[redacted:9f3a1c2b7d4e]</c>
/// -- that the model can carry like any other word, and the value stays here.
/// It goes back in at exactly two places, both of them a thing a person read
/// first: the text of a file write, whose diff the gate shows with the value in
/// it, and a plan command that was approved with a placeholder in it. Nowhere
/// else turns a marker back into a secret, so a model persuaded by some output
/// to send <c>curl …?k=[redacted:…]</c> sends the marker.
///
/// Markers are random rather than counted. A count is guessable, and a store
/// shared by a run should not answer for a marker its own conversation never
/// produced.
/// </summary>
public sealed partial class Secrets
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _byMarker = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byValue = new(StringComparer.Ordinal);

    /// <summary>How many it holds. For tests, and for nothing that decides anything.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _byMarker.Count;
        }
    }

    /// <summary>
    /// The marker for a secret, made the first time it is seen. The same value
    /// always gets the same marker, so a token that appears in the terminal and
    /// again in a command's output is one thing to the model, not two.
    /// </summary>
    public string Keep(string value)
    {
        lock (_lock)
        {
            if (_byValue.TryGetValue(value, out var known))
                return known;

            var marker = $"[redacted:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}]";
            _byMarker[marker] = value;
            _byValue[value] = marker;
            return marker;
        }
    }

    /// <summary>The text with every marker this store knows replaced by its value. Others are left alone.</summary>
    public string Restore(string text) =>
        text.Contains("[redacted:", StringComparison.Ordinal)
            ? Numbered().Replace(text, match => Value(match.Value) ?? match.Value)
            : text;

    /// <summary>
    /// Takes on the secrets another store holds for the markers in this text,
    /// and nothing else. How a run carries a captured value from the host that
    /// printed it to the host that uses it.
    /// </summary>
    public void Admit(Secrets from, string text)
    {
        foreach (Match match in Numbered().Matches(text))
        {
            if (from.Value(match.Value) is not { } value)
                continue;
            lock (_lock)
            {
                _byMarker[match.Value] = value;
                _byValue.TryAdd(value, match.Value);
            }
        }
    }

    /// <summary>A store holding only what this one holds for the markers in this text.</summary>
    public Secrets For(string text)
    {
        var some = new Secrets();
        some.Admit(this, text);
        return some;
    }

    /// <summary>
    /// Whether text names one of these markers without being it: the id with
    /// its brackets dropped, or cut short. A small model asked to copy
    /// <c>[redacted:d963eed58ceb]</c> wrote <c>d963eed58ceb</c>, which restores
    /// to nothing and would have run as the token.
    /// </summary>
    public bool Mangles(string text)
    {
        var whole = Numbered().Replace(text, "");
        lock (_lock)
            return _byMarker.Keys.Any(marker => whole.Contains(marker[10..^1], StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether text still carries a marker of any kind: one nothing restored,
    /// or the plain <see cref="Redaction.Marker"/>. Writing either into a file
    /// or a command would put the word in place of the secret.
    /// </summary>
    public static bool Marked(string text) =>
        text.Contains(Redaction.Marker, StringComparison.Ordinal) || Numbered().IsMatch(text);

    private string? Value(string marker)
    {
        lock (_lock)
            return _byMarker.GetValueOrDefault(marker);
    }

    [GeneratedRegex(@"\[redacted:[0-9a-f]{12}\]")]
    private static partial Regex Numbered();
}
