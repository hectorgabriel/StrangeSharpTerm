using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class RunPlanTests
{
    private static readonly string[] Hosts = ["web-01", "web-02", "staging-app"];

    private const string Cluster = """
    {"phases": [
      {"name": "Prepare every node", "hosts": ["web-01", "web-02"], "task": "Disable swap and install containerd."},
      {"name": "Initialise the control plane", "hosts": ["web-01"], "task": "Run kubeadm init.",
       "capture": "join_command"},
      {"name": "Join the workers", "hosts": ["web-02"], "task": "Join this node with: {{join_command}}"}
    ]}
    """;

    [Fact]
    public void APlanIsReadWholeAndShownWhole()
    {
        var plan = Ok(Cluster);

        plan.Phases.Count.ShouldBe(3);
        plan.Summary.ShouldBe("3 phases");
        plan.Phases[0].Hosts.ShouldBe(["web-01", "web-02"]);
        plan.Phases[1].Capture.ShouldBe("join_command");
        plan.Phases[2].Placeholders.ShouldBe(["join_command"]);
        // Nothing runs until Run the plan, and everything is on by default.
        plan.Phases.ShouldAllBe(phase => phase.IsEnabled);
    }

    [Fact]
    public void AModelThatFencedItsJsonAnywayIsUnderstood()
    {
        var plan = Ok($"```json\n{Cluster}\n```");

        plan.Phases.Count.ShouldBe(3);
    }

    [Fact]
    public void APhaseNamingAHostNobodySelectedIsRefused()
    {
        var refusal = Refused("""{"phases":[{"name":"Do it","hosts":["db-primary"],"task":"x"}]}""");

        refusal.ShouldContain("Do it");
        refusal.ShouldContain("db-primary");
        refusal.ShouldContain("nobody selected");
    }

    [Fact]
    public void CapturingOnSeveralHostsAtOnceIsRefused()
    {
        var refusal = Refused(
            """{"phases":[{"name":"Init","hosts":["web-01","web-02"],"task":"x","capture":"token"}]}""");

        refusal.ShouldContain("Init");
        refusal.ShouldContain("token");
        refusal.ShouldContain("no single value to carry");
    }

    [Fact]
    public void APlaceholderNoEarlierPhaseProducesIsRefused()
    {
        var refusal = Refused("""{"phases":[{"name":"Join","hosts":["web-02"],"task":"use {{join_command}}"}]}""");

        refusal.ShouldContain("Join");
        refusal.ShouldContain("join_command");
        refusal.ShouldContain("no earlier phase");
    }

    [Fact]
    public void APhaseCannotUseItsOwnCapture()
    {
        var refusal = Refused(
            """{"phases":[{"name":"Init","hosts":["web-01"],"task":"use {{t}}","capture":"t"}]}""");

        refusal.ShouldContain("no earlier phase");
    }

    [Fact]
    public void MoreThanTwelvePhasesIsRefused()
    {
        var phases = string.Join(",", Enumerable.Range(1, 13).Select(number =>
            $$"""{"name":"Phase {{number}}","hosts":["web-01"],"task":"do something"}"""));

        Refused($$"""{"phases":[{{phases}}]}""").ShouldContain("not a plan anyone will read");
    }

    [Fact]
    public void APhaseWithNoHostsIsRefused() =>
        Refused("""{"phases":[{"name":"Nowhere","hosts":[],"task":"x"}]}""").ShouldContain("names no hosts");

    [Fact]
    public void APhaseThatSaysNothingIsRefused() =>
        Refused("""{"phases":[{"name":"Empty","hosts":["web-01"],"task":"   "}]}""")
            .ShouldContain("says nothing");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingBackIsRefused(string? answer) =>
        RunPlan.Read(answer, Hosts).ShouldBeOfType<PlanReading.Refused>();

    [Fact]
    public void SomethingThatIsNotJsonIsRefused() =>
        Refused("I would rather not.").ShouldContain("not readable");

    [Fact]
    public void JsonWithNoPhasesIsRefused() =>
        Refused("""{"plan":"do it"}""").ShouldContain("no phases");

    [Fact]
    public void AValueIsPutIntoTheTaskWhereItWasNamed() =>
        RunPlan.Fill("Join with: {{join_command}} then check", new Dictionary<string, string> { ["join_command"] = "kubeadm join 10.0.0.1" })
            .ShouldBe("Join with: kubeadm join 10.0.0.1 then check");

    [Fact]
    public void APlaceholderWithNoValueIsLeftAloneForTheCallerToRefuse() =>
        RunPlan.Fill("use {{missing}}", new Dictionary<string, string>()).ShouldBe("use {{missing}}");

    [Fact]
    public void PlaceholdersAreFoundOnceEach() =>
        RunPlan.PlaceholdersIn("{{a}} then {{b}} then {{a}} again").ShouldBe(["a", "b"]);

    [Fact]
    public void SpacingInsideAPlaceholderDoesNotMatter() =>
        RunPlan.PlaceholdersIn("{{ join_command }}").ShouldBe(["join_command"]);

    [Fact]
    public void AnUnfencedAnswerIsLeftAsItIs() =>
        RunPlan.Unfence("""{"phases":[]}""").ShouldBe("""{"phases":[]}""");

    private static RunPlan Ok(string answer) =>
        RunPlan.Read(answer, Hosts).ShouldBeOfType<PlanReading.Ok>().Plan;

    private static string Refused(string answer) =>
        RunPlan.Read(answer, Hosts).ShouldBeOfType<PlanReading.Refused>().Reason;
}
