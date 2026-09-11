using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StrangeSharpTerm.Security;

/// <summary>
/// Fingerprints in the form OpenSSH prints them.
///
/// Public because the fingerprint is shown to the user and compared by them
/// against a value from elsewhere; a different encoding would make it useless.
/// </summary>
public static class HostKeyFingerprint
{
    /// <summary>SHA-256 of the key blob, base64, unpadded — what <c>ssh-keygen -lf</c> shows.</summary>
    public static string Sha256(string keyBase64)
    {
        if (!TryDecodeBase64(keyBase64, out var blob))
            return "SHA256:<unreadable>";
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }

    internal static bool TryDecodeBase64(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

public enum KnownHostMarker
{
    CertAuthority,
    Revoked,
}

/// <summary>One line of a <c>known_hosts</c> file.</summary>
public sealed record KnownHostEntry
{
    public KnownHostMarker? Marker { get; init; }

    /// <summary>Plain host patterns; empty when the line is hashed.</summary>
    public required IReadOnlyList<string> Patterns { get; init; }

    /// <summary><c>|1|salt|hash</c>, split out. OpenSSH hashes host names by default.</summary>
    public byte[]? HashedSalt { get; init; }

    public byte[]? HashedHost { get; init; }

    public required string KeyType { get; init; }

    public required string KeyBase64 { get; init; }

    public required int LineNumber { get; init; }

    /// <summary>The fingerprint OpenSSH shows: SHA-256 of the key blob, base64, unpadded.</summary>
    public string Fingerprint => HostKeyFingerprint.Sha256(KeyBase64);

    public bool IsHashed => HashedSalt is not null && HashedHost is not null;
}

/// <summary>What a stored key says about the one a server just offered.</summary>
public abstract record HostKeyVerdict
{
    private HostKeyVerdict() { }

    /// <summary>Nothing stored for this host: first contact.</summary>
    public sealed record Unknown : HostKeyVerdict;

    /// <summary>Stored and identical.</summary>
    public sealed record Known : HostKeyVerdict;

    /// <summary>
    /// Stored, and different. A hard block everywhere, because this is what an
    /// interception looks like.
    /// </summary>
    public sealed record Changed(string StoredFingerprint) : HostKeyVerdict;

    /// <summary>The key is explicitly marked <c>@revoked</c>.</summary>
    public sealed record Revoked : HostKeyVerdict;
}

/// <summary>
/// Parses and queries <c>known_hosts</c>.
///
/// Written rather than shelling out to <c>ssh-keygen -F</c> because the app needs
/// the stored fingerprint to <em>show</em> the user, not just a yes or no, and
/// because a changed key has to be distinguishable from an unknown one: the two
/// call for very different interfaces.
/// </summary>
public sealed class KnownHostsFile
{
    public KnownHostsFile(IReadOnlyList<KnownHostEntry> entries) => Entries = entries;

    public IReadOnlyList<KnownHostEntry> Entries { get; }

    public static KnownHostsFile Parse(string text)
    {
        var entries = new List<KnownHostEntry>();
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var offset = 0;

            KnownHostMarker? marker = null;
            if (fields[0].StartsWith('@'))
            {
                marker = fields[0] switch
                {
                    "@cert-authority" => KnownHostMarker.CertAuthority,
                    "@revoked" => KnownHostMarker.Revoked,
                    _ => null,
                };
                offset = 1;
            }

            // host, keytype, key — a comment may follow and is ignored.
            if (fields.Length - offset < 3)
                continue;

            var hostField = fields[offset];
            IReadOnlyList<string> patterns = [];
            byte[]? salt = null, hash = null;

            if (hostField.StartsWith("|1|", StringComparison.Ordinal))
            {
                // |1|<base64 salt>|<base64 hash>
                var parts = hostField.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3
                    && HostKeyFingerprint.TryDecodeBase64(parts[1], out var decodedSalt)
                    && HostKeyFingerprint.TryDecodeBase64(parts[2], out var decodedHash))
                {
                    (salt, hash) = (decodedSalt, decodedHash);
                }
            }
            else
            {
                patterns = hostField.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }

            entries.Add(new KnownHostEntry
            {
                Marker = marker,
                Patterns = patterns,
                HashedSalt = salt,
                HashedHost = hash,
                KeyType = fields[offset + 1],
                KeyBase64 = fields[offset + 2],
                LineNumber = lineNumber,
            });
        }

        return new KnownHostsFile(entries);
    }

    public static KnownHostsFile Read(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new KnownHostsFile([]);

    /// <summary>How a host is written when its port is not 22.</summary>
    public static string HostPattern(string host, int? port) =>
        port is null or 22 ? host : $"[{host}]:{port}";

    /// <summary>Entries recorded for a host, hashed or not.</summary>
    public IReadOnlyList<KnownHostEntry> EntriesMatching(string host, int? port)
    {
        var target = HostPattern(host, port);
        return
        [
            .. Entries.Where(entry => entry.IsHashed
                ? MatchesHashed(entry, target)
                : entry.Patterns.Any(pattern => Matches(pattern, target))),
        ];
    }

    /// <summary>Compares a host's offered key against what is stored.</summary>
    public HostKeyVerdict Verdict(string host, int? port, string keyType, string keyBase64)
    {
        var candidates = EntriesMatching(host, port);
        if (candidates.Count == 0)
            return new HostKeyVerdict.Unknown();

        if (candidates.Any(e => e.Marker == KnownHostMarker.Revoked && e.KeyBase64 == keyBase64))
            return new HostKeyVerdict.Revoked();

        if (candidates.Any(e => e.KeyBase64 == keyBase64 && e.Marker is null))
            return new HostKeyVerdict.Known();

        // A stored key of the same type that does not match is the dangerous case.
        // A different *type* is not: servers offer several, and having only the
        // Ed25519 key on file says nothing about the RSA one.
        var sameType = candidates.FirstOrDefault(e => e.KeyType == keyType && e.Marker is null);
        return sameType is null
            ? new HostKeyVerdict.Unknown()
            : new HostKeyVerdict.Changed(sameType.Fingerprint);
    }

    private static bool MatchesHashed(KnownHostEntry entry, string target)
    {
        if (entry.HashedSalt is not { } salt || entry.HashedHost is not { } expected)
            return false;
        // OpenSSH hashes the host name with HMAC-SHA1, keyed by the salt.
        return HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(target)).AsSpan().SequenceEqual(expected);
    }

    /// <summary>Glob matching, as ssh_config patterns work, plus <c>!</c> negation.</summary>
    private static bool Matches(string pattern, string host)
    {
        if (pattern.StartsWith('!'))
            return !Matches(pattern[1..], host);
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return pattern == host;

        var expression = new StringBuilder("^");
        foreach (var character in pattern)
        {
            expression.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString()),
            });
        }
        expression.Append('$');
        return Regex.IsMatch(host, expression.ToString(), RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}
