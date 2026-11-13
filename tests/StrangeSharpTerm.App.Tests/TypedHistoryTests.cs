using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Tests;

/// <summary>What a chat field remembers of what was sent from it.</summary>
public class TypedHistoryTests
{
    [Fact]
    public void WithNothingSentThereIsNothingToWalkTo()
    {
        new TypedHistory().Walk(1, "half a question").ShouldBeNull();
    }

    [Fact]
    public void ItWalksBackNewestFirstAndStopsAtTheOldest()
    {
        var history = new TypedHistory();
        history.Remember("first");
        history.Remember("second");

        history.Walk(1, "").ShouldBe("second");
        history.Walk(1, "second").ShouldBe("first");
        history.Walk(1, "first").ShouldBeNull();
    }

    [Fact]
    public void WalkingForwardAgainEndsOnWhatWasBeingWritten()
    {
        var history = new TypedHistory();
        history.Remember("first");

        history.Walk(1, "half a quest").ShouldBe("first");
        history.Walk(-1, "first").ShouldBe("half a quest");
        history.Walk(-1, "half a quest").ShouldBeNull();
    }

    [Fact]
    public void SendingStartsTheWalkAgainFromTheNewest()
    {
        var history = new TypedHistory();
        history.Remember("first");
        history.Remember("second");
        history.Walk(1, "");
        history.Walk(1, "");

        history.Remember("third");

        history.Walk(1, "").ShouldBe("third");
    }

    [Fact]
    public void TheSameThingTwiceRunningIsOneEntryAndNothingIsNone()
    {
        var history = new TypedHistory();
        history.Remember("again");
        history.Remember("again");
        history.Remember("");

        history.Count.ShouldBe(1);
        history.Last.ShouldBe("again");
    }

    [Fact]
    public void ClearedItRemembersNothing()
    {
        var history = new TypedHistory();
        history.Remember("first");

        history.Clear();

        history.Last.ShouldBeNull();
        history.Walk(1, "").ShouldBeNull();
    }
}
