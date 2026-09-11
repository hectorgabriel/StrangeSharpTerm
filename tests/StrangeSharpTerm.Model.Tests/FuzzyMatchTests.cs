namespace StrangeSharpTerm.Model.Tests;

public class FuzzyMatchTests
{
    private static IReadOnlyList<string> Rank(string[] items, string query) => FuzzyMatch.Rank(items, query, item => item);

    [Fact]
    public void AnEmptyQueryMatchesEverythingWithoutReordering()
    {
        FuzzyMatch.Score("anything", "").ShouldBe(0);
        string[] items = ["c", "a", "b"];
        Rank(items, "").ShouldBe(items);
    }

    [Fact]
    public void CharactersMustAppearInOrder()
    {
        FuzzyMatch.Matches("db-primary", "dbp").ShouldBeTrue();
        FuzzyMatch.Matches("db-primary", "pdb").ShouldBeFalse();
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        FuzzyMatch.Matches("Production", "prod").ShouldBeTrue();
        FuzzyMatch.Matches("production", "PROD").ShouldBeTrue();
    }

    [Fact]
    public void AQueryLongerThanTheCandidateCannotMatch()
    {
        FuzzyMatch.Score("ab", "abc").ShouldBeNull();
    }

    [Fact]
    public void WordStartInitialsOutrankAScatteredMatch()
    {
        // Typing "dbp" should mean db-primary, not something that merely contains
        // those letters somewhere.
        Rank(["disabled-backup-copy", "db-primary"], "dbp")[0].ShouldBe("db-primary");
    }

    [Fact]
    public void APrefixBeatsAMatchInTheMiddle()
    {
        Rank(["staging-web", "web-01"], "web")[0].ShouldBe("web-01");
    }

    [Fact]
    public void ShorterCandidatesWinWhenTheMatchIsOtherwiseEqual()
    {
        Rank(["web-01.prod.example.com", "web-01"], "web01")[0].ShouldBe("web-01");
    }

    [Fact]
    public void ConsecutiveCharactersBeatGaps()
    {
        var tight = FuzzyMatch.Score("bastion", "bas").ShouldNotBeNull();
        var loose = FuzzyMatch.Score("banana-session", "bas").ShouldNotBeNull();
        tight.ShouldBeGreaterThan(loose);
    }

    [Fact]
    public void NonMatchesAreDroppedFromARanking()
    {
        Rank(["web-01", "db-primary", "bastion"], "web").ShouldBe(new[] { "web-01" });
    }

    [Fact]
    public void TiesKeepInputOrderSoTheListDoesNotReshuffleWhileTyping()
    {
        Rank(["aa", "ab"], "a").ShouldBe(new[] { "aa", "ab" });
    }
}
