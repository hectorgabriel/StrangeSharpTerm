namespace StrangeSharpTerm.Security.Tests;

/// <summary>
/// Against the real platform store, as the Swift suite ran against the real
/// keychain. Each test uses its own service name and cleans up, so a failure
/// cannot leave items behind in anyone's credential store.
/// </summary>
public class SecretStoreTests
{
    private static PlatformSecretStore Store() => new($"dev.strangeterm.tests.{Guid.NewGuid():N}");

    [Fact]
    public void ASecretRoundTrips()
    {
        var store = Store();
        try
        {
            store.SetSecret("deploy@example.com", "hunter2");
            store.Secret("deploy@example.com").ShouldBe("hunter2");
        }
        finally
        {
            store.RemoveSecret("deploy@example.com");
        }
    }

    [Fact]
    public void AMissingSecretIsNullNotAnError()
    {
        // Absence is the ordinary case on first use and must not read as failure.
        Store().Secret("nobody").ShouldBeNull();
    }

    [Fact]
    public void StoringTwiceUpdatesRatherThanFailingOnADuplicate()
    {
        var store = Store();
        try
        {
            store.SetSecret("account", "first");
            store.SetSecret("account", "second");
            store.Secret("account").ShouldBe("second");
        }
        finally
        {
            store.RemoveSecret("account");
        }
    }

    [Fact]
    public void RemovingWorksAndRemovingAgainIsNotAnError()
    {
        var store = Store();
        store.SetSecret("account", "x");
        store.RemoveSecret("account");

        store.Secret("account").ShouldBeNull();
        // The desired end state is already reached; that is a success.
        store.RemoveSecret("account");
    }

    [Fact]
    public void AccountsDoNotCollide()
    {
        var store = Store();
        try
        {
            store.SetSecret("a@host", "one");
            store.SetSecret("b@host", "two");

            store.Secret("a@host").ShouldBe("one");
            store.Secret("b@host").ShouldBe("two");
        }
        finally
        {
            store.RemoveSecret("a@host");
            store.RemoveSecret("b@host");
        }
    }

    [Fact]
    public void NonAsciiSecretsSurvive()
    {
        var store = Store();
        try
        {
            store.SetSecret("account", "pässwörd–ünicode");
            store.Secret("account").ShouldBe("pässwörd–ünicode");
        }
        finally
        {
            store.RemoveSecret("account");
        }
    }

    [Fact]
    public void ErrorsCarryAMessageFitToShowAPerson()
    {
        var failure = new SecretStoreException("The credential store refused access.");
        failure.Message.ShouldNotBeEmpty();
        failure.Message.ShouldContain("refused");
    }
}

public class InMemorySecretStoreTests
{
    [Fact]
    public void ItBehavesLikeTheRealOneForTheLifeOfTheProcess()
    {
        // Used by tests elsewhere and as the fallback when no platform store is
        // reachable, so it has to keep the same contract.
        ISecretStore store = new InMemorySecretStore();

        store.Secret("account").ShouldBeNull();
        store.HasSecret("account").ShouldBeFalse();

        store.SetSecret("account", "first");
        store.SetSecret("account", "second");
        store.Secret("account").ShouldBe("second");
        store.HasSecret("account").ShouldBeTrue();

        store.RemoveSecret("account");
        store.RemoveSecret("account");
        store.Secret("account").ShouldBeNull();
    }
}
