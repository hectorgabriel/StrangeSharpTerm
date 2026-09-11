using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Transport;

public enum HostKeyDecision
{
    /// <summary>Continue without asking.</summary>
    Accept,

    /// <summary>Ask the user, showing the fingerprint.</summary>
    Prompt,

    /// <summary>Refuse, with no way for the user to wave it through in the moment.</summary>
    Reject,
}

public static class HostKeyPolicyRules
{
    /// <summary>
    /// What to do about the key a server just offered.
    ///
    /// The rule that matters: a changed or revoked key is <b>never</b> a prompt,
    /// whatever the policy says. A prompt at that moment is exactly the prompt a
    /// person clicks through, and it is the one case where doing so is an
    /// interception. Unknown keys are the ones a policy gets to decide about.
    /// </summary>
    public static HostKeyDecision Decide(HostKeyPolicy policy, HostKeyVerdict verdict) => verdict switch
    {
        HostKeyVerdict.Known => HostKeyDecision.Accept,
        HostKeyVerdict.Changed or HostKeyVerdict.Revoked => HostKeyDecision.Reject,
        _ => policy switch
        {
            HostKeyPolicy.Strict => HostKeyDecision.Reject,
            HostKeyPolicy.AcceptAny => HostKeyDecision.Accept,
            _ => HostKeyDecision.Prompt,
        },
    };
}
