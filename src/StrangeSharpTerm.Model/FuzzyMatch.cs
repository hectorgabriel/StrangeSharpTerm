using System.Globalization;

namespace StrangeSharpTerm.Model;

/// <summary>
/// Subsequence matching with scoring, for the command palette and filters.
///
/// Substring matching is not enough once a palette lists every host, session and
/// action at once: people type "dbp" for "db-primary" and "wb1" for "web-01".
/// Scoring is what keeps the obvious answer at the top. An exact prefix has to
/// beat a match scattered across the string, or the palette feels random.
///
/// Works on user-perceived characters (text elements), as the Swift original
/// works on <c>Character</c>, so an accented letter or an emoji counts once.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Points for a character that directly follows the previous match.</summary>
    private const int ConsecutiveBonus = 8;

    /// <summary>
    /// Points for a character starting a word: after a separator, or a capital in
    /// camelCase. This is what makes "dbp" rank "db-primary" first.
    /// </summary>
    private const int WordStartBonus = 10;

    /// <summary>Points for matching at the very beginning.</summary>
    private const int PrefixBonus = 12;

    /// <summary>Charged per skipped character, so tighter matches win.</summary>
    private const int GapPenalty = 1;

    /// <summary>
    /// A score for <paramref name="candidate"/> against <paramref name="query"/>, or
    /// null when it does not match. An empty query matches everything with score 0,
    /// so an unfiltered palette keeps its natural order.
    /// </summary>
    public static int? Score(string candidate, string query)
    {
        if (query.Length == 0)
            return 0;

        var haystack = TextElements(candidate);
        var needle = TextElements(query.ToLowerInvariant());
        if (haystack.Length < needle.Length)
            return null;

        var score = 0;
        var haystackIndex = 0;
        int? previousMatch = null;

        foreach (var character in needle)
        {
            while (haystackIndex < haystack.Length && haystack[haystackIndex].ToLowerInvariant() != character)
                haystackIndex++;
            if (haystackIndex == haystack.Length)
                return null;
            var matchIndex = haystackIndex;

            if (matchIndex == 0)
                score += PrefixBonus;
            else if (IsWordStart(haystack, matchIndex))
                score += WordStartBonus;

            if (previousMatch is { } previous)
            {
                if (matchIndex == previous + 1)
                    score += ConsecutiveBonus;
                else
                    score -= (matchIndex - previous - 1) * GapPenalty;
            }

            previousMatch = matchIndex;
            haystackIndex = matchIndex + 1;
        }

        // Shorter candidates are more likely to be what was meant: "web-01" should
        // outrank "web-01.prod.example.com" for the query "web".
        return score - haystack.Length / 8;
    }

    public static bool Matches(string candidate, string query) => Score(candidate, query) is not null;

    /// <summary>
    /// Ranks items best-first, dropping non-matches. Ties keep their input order,
    /// so a stable list does not reshuffle as the user types.
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, string query, Func<T, string> text) =>
    [
        .. items
            .Select((item, offset) => (Item: item, Offset: offset, Score: Score(text(item), query)))
            .Where(entry => entry.Score is not null)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Offset)
            .Select(entry => entry.Item),
    ];

    private static bool IsWordStart(string[] characters, int index)
    {
        if (index == 0)
            return true;
        var previous = characters[index - 1];
        if (previous is "-" or "_" or "." or " " or "/")
            return true;
        // camelCase boundary.
        return IsLowercase(previous) && IsUppercase(characters[index]);
    }

    // Swift's definitions: a lowercase character changes when uppercased but not
    // when lowercased, and the reverse for uppercase.
    private static bool IsLowercase(string c) => c.ToUpperInvariant() != c && c.ToLowerInvariant() == c;
    private static bool IsUppercase(string c) => c.ToLowerInvariant() != c && c.ToUpperInvariant() == c;

    private static string[] TextElements(string text)
    {
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            elements.Add(enumerator.GetTextElement());
        return [.. elements];
    }
}
