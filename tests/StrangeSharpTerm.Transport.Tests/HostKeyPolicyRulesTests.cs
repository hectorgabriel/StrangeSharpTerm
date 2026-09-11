using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Transport.Tests;

public class HostKeyPolicyRulesTests
{
    private static readonly HostKeyPolicy[] EveryPolicy = Enum.GetValues<HostKeyPolicy>();

    [Fact]
    public void AKnownKeyIsAcceptedUnderEveryPolicy()
    {
        foreach (var policy in EveryPolicy)
            HostKeyPolicyRules.Decide(policy, new HostKeyVerdict.Known()).ShouldBe(HostKeyDecision.Accept);
    }

    [Fact]
    public void AChangedOrRevokedKeyIsRefusedUnderEveryPolicyIncludingAcceptAny()
    {
        // The rule that matters. A prompt here is exactly the prompt a person
        // clicks through, and this is the one case where doing so is an
        // interception.
        foreach (var policy in EveryPolicy)
        {
            HostKeyPolicyRules.Decide(policy, new HostKeyVerdict.Changed("SHA256:stored")).ShouldBe(HostKeyDecision.Reject);
            HostKeyPolicyRules.Decide(policy, new HostKeyVerdict.Revoked()).ShouldBe(HostKeyDecision.Reject);
        }
    }

    [Fact]
    public void AnUnknownKeyIsWhatThePolicyGetsToDecideAbout()
    {
        var unknown = new HostKeyVerdict.Unknown();

        HostKeyPolicyRules.Decide(HostKeyPolicy.Strict, unknown).ShouldBe(HostKeyDecision.Reject);
        HostKeyPolicyRules.Decide(HostKeyPolicy.AcceptNew, unknown).ShouldBe(HostKeyDecision.Prompt);
        HostKeyPolicyRules.Decide(HostKeyPolicy.AcceptAny, unknown).ShouldBe(HostKeyDecision.Accept);
    }

    [Fact]
    public void TheProductDefaultPromptsRatherThanAcceptingSilently()
    {
        // ResolvedSettings falls back to acceptNew, so first contact asks.
        var policy = new ResolvedSettings(ConnectionSettings.Empty).HostKeyPolicy;

        HostKeyPolicyRules.Decide(policy, new HostKeyVerdict.Unknown()).ShouldBe(HostKeyDecision.Prompt);
    }
}
