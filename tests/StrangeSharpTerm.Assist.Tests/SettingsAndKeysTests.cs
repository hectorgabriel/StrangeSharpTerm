using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Assist.Tests;

public class SettingsAndKeysTests
{
    [Fact]
    public void ClaudeIsTheDefaultAndItsDefaultModelIsTheCurrentOne()
    {
        var settings = new AssistSettings();

        settings.Provider.ShouldBe(AssistProviderId.Claude);
        settings.ModelName.ShouldBe("claude-opus-5");
        settings.Backend.DataGoesTo.ShouldBe("Anthropic (United States)");
    }

    [Fact]
    public void AModelNameIsFreeTextWithTheKnownOnesOfferedAsAMenu()
    {
        var settings = new AssistSettings { Model = "claude-released-tomorrow" };

        settings.ModelName.ShouldBe("claude-released-tomorrow");
        AssistProvider.Claude.KnownModels.ShouldContain("claude-opus-5");
    }

    [Fact]
    public void DeepSeeksDefaultsAndWhereItsDataGoes()
    {
        var settings = new AssistSettings { Provider = AssistProviderId.DeepSeek };

        settings.ModelName.ShouldBe("deepseek-v4-pro");
        // Which backend is in use decides where this conversation's terminal
        // output is being sent, and that should be readable without digging.
        settings.Backend.DataGoesTo.ShouldBe("DeepSeek (China)");
    }

    [Fact]
    public void NothingIsSentByDefaultThatWasNotAskedFor()
    {
        var settings = new AssistSettings();

        // Both context sources are on, as the Swift app had them, and running
        // commands is not.
        settings.SendMetrics.ShouldBeTrue();
        settings.SendTerminalTail.ShouldBeTrue();
        settings.AllowCommandsByDefault.ShouldBeFalse();
    }

    /// <summary>
    /// The three variables that redirect a development build at a stub, which is
    /// how a provider's wire format is checked without an account.
    /// </summary>
    [Fact]
    public void TheEnvironmentCanRedirectTheWholeThing()
    {
        using var provider = new Variable("STRANGESHARPTERM_ASSIST_PROVIDER", "deepseek");
        using var model = new Variable("STRANGESHARPTERM_ASSIST_MODEL", "deepseek-v3");
        using var endpoint = new Variable("STRANGESHARPTERM_ASSIST_ENDPOINT", "http://127.0.0.1:8080/v1/chat");

        var settings = new AssistSettings().WithEnvironmentOverrides();

        settings.Provider.ShouldBe(AssistProviderId.DeepSeek);
        settings.ModelName.ShouldBe("deepseek-v3");
        settings.Endpoint.ShouldBe("http://127.0.0.1:8080/v1/chat");
    }

    [Fact]
    public void AnUnknownProviderNameIsIgnoredRatherThanFatal()
    {
        using var provider = new Variable("STRANGESHARPTERM_ASSIST_PROVIDER", "not-a-provider");

        new AssistSettings().WithEnvironmentOverrides().Provider.ShouldBe(AssistProviderId.Claude);
    }

    [Fact]
    public void EachProviderHasItsOwnAccountSoASecondDoesNotOverwriteTheFirst()
    {
        var store = new InMemorySecretStore();
        store.SetSecret(AssistProvider.Claude.KeyAccount, "claude-key");
        store.SetSecret(AssistProvider.DeepSeek.KeyAccount, "deepseek-key");

        AssistKeys.Key(AssistProvider.Claude, store).ShouldBe("claude-key");
        AssistKeys.Key(AssistProvider.DeepSeek, store).ShouldBe("deepseek-key");
    }

    [Fact]
    public void TheEnvironmentOverridesTheStore()
    {
        var store = new InMemorySecretStore();
        store.SetSecret(AssistProvider.Claude.KeyAccount, "from-the-keychain");
        using var variable = new Variable(AssistProvider.Claude.KeyVariable, "from-the-environment");

        // Which is what makes a development build usable without touching the
        // real keychain.
        AssistKeys.Key(AssistProvider.Claude, store).ShouldBe("from-the-environment");
    }

    [Fact]
    public void NoKeyIsAnOrdinaryStateAndNotAFailure()
    {
        AssistKeys.Key(AssistProvider.DeepSeek, new InMemorySecretStore()).ShouldBeNull();

        AssistBackends.For(new AssistSettings { Provider = AssistProviderId.DeepSeek }, new InMemorySecretStore())
            .ShouldBeNull();
    }

    [Fact]
    public void WhatThePaneSaysWhenThereIsNoKeyNamesTheProviderAndTheVariable()
    {
        var message = AssistBackends.NoKey(new AssistSettings { Provider = AssistProviderId.DeepSeek });

        message.ShouldContain("DeepSeek");
        message.ShouldContain("DEEPSEEK_API_KEY");
        message.ShouldContain("Settings");
    }

    [Fact]
    public void SettingsChoosesTheBackend()
    {
        var store = new InMemorySecretStore();
        store.SetSecret(AssistProvider.Claude.KeyAccount, "k");
        store.SetSecret(AssistProvider.DeepSeek.KeyAccount, "k");

        AssistBackends.For(new AssistSettings(), store).ShouldNotBeNull().ProviderName.ShouldBe("Claude");
        AssistBackends.For(new AssistSettings { Provider = AssistProviderId.DeepSeek }, store)
            .ShouldNotBeNull().ProviderName.ShouldBe("DeepSeek");
    }

    /// <summary>
    /// An API key is not a server secret and has no business in the same bucket
    /// as passphrases: deleting every saved password should not log you out of a
    /// provider.
    /// </summary>
    [Fact]
    public void ApiKeysAreKeptSomewhereOfTheirOwn() =>
        AssistKeys.Service.ShouldNotBe(PlatformSecretStore.DefaultService);

    /// <summary>An environment variable, put back however the test ends.</summary>
    private sealed class Variable : IDisposable
    {
        private readonly string _name;
        private readonly string? _was;

        internal Variable(string name, string value)
        {
            _name = name;
            _was = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _was);
    }
}
