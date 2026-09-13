using ModelContextProtocol.Authentication;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Mcp.Tests;

public class SignInTests
{
    private static McpServerConfig Hosted { get; } = new()
    {
        Name = "Grafana",
        Transport = McpTransport.Http,
        Url = "https://metrics.example.com/mcp",
    };

    [Fact]
    public void TheRedirectIsLoopbackAndNotACustomScheme()
    {
        // A scheme like strangesharpterm:// can be claimed by any application on
        // the machine, which would hand the authorization code to whoever
        // registered it last. RFC 8252 says as much.
        foreach (var uri in LoopbackRedirect.RedirectUris)
        {
            uri.Scheme.ShouldBe("http");
            uri.Host.ShouldBe("127.0.0.1");
        }
    }

    [Fact]
    public void SeveralPortsAreOfferedSoALaterSignInHasAChoice() =>
        // Registered with the server, so a second sign-in can use whichever is
        // free without registering a second client.
        LoopbackRedirect.Ports.Count.ShouldBeGreaterThan(1);

    [Fact]
    public void ThePortIsBoundBeforeAnyBrowserOpens()
    {
        using var redirect = LoopbackRedirect.Bind();

        // Bound already: a code arriving at a port nobody is listening on is a
        // sign-in that fails after the person has already approved it.
        LoopbackRedirect.Ports.ShouldContain(redirect.Redirect.Port);

        using var second = LoopbackRedirect.Bind();
        second.Redirect.Port.ShouldNotBe(redirect.Redirect.Port);
    }

    [Fact]
    public async Task ARenewalNeverOpensABrowser()
    {
        var opened = new List<Uri>();
        var options = McpSignIn.For(Hosted, McpCredentials.None, interactive: false, opened.Add);

        options.ShouldNotBeNull();
        var answer = await options.AuthorizationCallbackHandler!(
            new AuthorizationCallbackContext
            {
                AuthorizationUri = new Uri("https://auth.example.com/authorize"),
                RedirectUri = LoopbackRedirect.RedirectUris[0],
            },
            TestContext.Current.CancellationToken);

        // Null is how it refuses: the SDK renews silently where the server
        // issued a refresh token, and gives up where it did not.
        answer.ShouldBeNull();
        opened.ShouldBeEmpty();
    }

    [Fact]
    public void APastedTokenWinsOverTheWholeChain()
    {
        // Someone who supplied one has said how their server authenticates.
        var options = McpSignIn.For(
            Hosted, new McpCredentials(Bearer: "a-token"), interactive: true);

        options.ShouldBeNull();
    }

    [Fact]
    public void ALocalServerHasNothingToSignInTo() =>
        McpSignIn.For(
            new McpServerConfig { Name = "Files", Command = "npx" },
            McpCredentials.None,
            interactive: true).ShouldBeNull();

    [Fact]
    public async Task SigningInToALocalServerSaysSoRatherThanTrying()
    {
        var thrown = await Should.ThrowAsync<McpException>(async () => await McpSignIn.SignIn(
            new McpServerConfig { Name = "Files", Command = "npx" },
            new InMemorySecretStore(),
            cancellationToken: TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("runs on this machine");
    }

    [Fact]
    public void TheAppRegistersItselfUnderAName()
    {
        var options = McpSignIn.For(Hosted, McpCredentials.None, interactive: true);

        options.ShouldNotBeNull();
        options.DynamicClientRegistration.ShouldNotBeNull().ClientName.ShouldBe("StrangeSharpTerm");
        options.RedirectUri.ShouldBe(LoopbackRedirect.RedirectUris[0]);
    }

    [Fact]
    public async Task ASignInSurvivesARelaunch()
    {
        var store = new InMemorySecretStore();
        var cache = new McpTokenCache(Hosted, store);

        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "at",
            RefreshToken = "rt",
            ClientId = "cid",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        var back = await new McpTokenCache(Hosted, store).GetTokensAsync(TestContext.Current.CancellationToken);

        back.ShouldNotBeNull();
        back.AccessToken.ShouldBe("at");
        back.RefreshToken.ShouldBe("rt");
        back.ClientId.ShouldBe("cid");
    }

    [Fact]
    public async Task ARenewalThatIssuesNoRefreshTokenDoesNotLoseTheOldOne()
    {
        var store = new InMemorySecretStore();
        var cache = new McpTokenCache(Hosted, store);
        await cache.StoreTokensAsync(
            new TokenContainer { TokenType = "Bearer", AccessToken = "at", RefreshToken = "rt", ObtainedAt = DateTimeOffset.UtcNow },
            TestContext.Current.CancellationToken);

        await cache.StoreTokensAsync(
            new TokenContainer { TokenType = "Bearer", AccessToken = "at2", ObtainedAt = DateTimeOffset.UtcNow },
            TestContext.Current.CancellationToken);

        var back = await cache.GetTokensAsync(TestContext.Current.CancellationToken);
        back.ShouldNotBeNull();
        back.AccessToken.ShouldBe("at2");
        back.RefreshToken.ShouldBe("rt");
    }

    [Fact]
    public async Task NotSignedInIsNoTokens() =>
        (await new McpTokenCache(Hosted, new InMemorySecretStore())
            .GetTokensAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

    [Fact]
    public async Task AStoredSignInIsAlwaysTreatedAsExpired()
    {
        var store = new InMemorySecretStore();
        var cache = new McpTokenCache(Hosted, store);
        await cache.StoreTokensAsync(
            new TokenContainer { TokenType = "Bearer", AccessToken = "at", RefreshToken = "rt", ObtainedAt = DateTimeOffset.UtcNow },
            TestContext.Current.CancellationToken);

        // Trusting a remembered expiry across a relaunch means sending a token
        // we believe is good and finding out it is not, mid-question. A refresh
        // costs one round trip and cannot be wrong that way.
        (await cache.GetTokensAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull().ExpiresIn.ShouldBe(0);
    }

    [Fact]
    public void TokensAreKeptSomewhereOfTheirOwn()
    {
        // Not beside connection credentials, and not beside provider API keys.
        McpTokens.Service.ShouldNotBe(PlatformSecretStore.DefaultService);
        McpTokens.Service.ShouldNotBe(Assist.AssistKeys.Service);
    }

    [Fact]
    public void EachServerHasItsOwnAccount()
    {
        var one = Hosted with { };
        var other = Hosted with { Id = Model.NodeId.New() };

        one.SecretAccount.ShouldNotBe(other.SecretAccount);
    }

    [Fact]
    public void ForgettingAServerForgetsItsTokens()
    {
        var store = new InMemorySecretStore();
        McpTokens.Write(Hosted, new McpCredentials(Bearer: "a-token"), store);

        McpTokens.Forget(Hosted, store);

        McpTokens.Read(Hosted, store).IsEmpty.ShouldBeTrue();
    }
}
