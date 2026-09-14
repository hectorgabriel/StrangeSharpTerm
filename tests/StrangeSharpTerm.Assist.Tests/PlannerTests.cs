using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class PlannerTests
{
    private const string Answer = """
    {"phases":[{"name":"Prepare","hosts":["web-01"],"why":"It is the only host.",
      "commands":["apt-get install -y containerd"]}]}
    """;

    [Fact]
    public async Task ItWritesAPlanAndRunsNoneOfIt()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says(Answer));

        var reading = await new Planner(backend).Draft(
            "build a cluster", ["web-01"], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Ok>().Plan.Phases.Single().Name.ShouldBe("Prepare");
        // No tool is offered, so there is nothing it could have run.
        backend.Requests.Single().Tools.ShouldBeEmpty();
    }

    /// <summary>
    /// The point of the change: a phase carries the commands themselves, so the
    /// plan a person reads is the thing that will run.
    /// </summary>
    [Fact]
    public async Task APhaseCarriesTheCommandsAndTheReasonForThem()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("""
        {"phases":[{"name":"Install OpenClaw","hosts":["web-01"],
          "why":"It has the most memory, and must not share the cluster.",
          "commands":["apt-get update","apt-get install -y openclaw","systemctl enable --now openclaw"]}]}
        """));

        var reading = await new Planner(backend).Draft(
            "install OpenClaw on one and build a cluster on the rest",
            ["web-01"],
            TestContext.Current.CancellationToken);

        var phase = reading.ShouldBeOfType<PlanReading.Ok>().Plan.Phases.Single();
        phase.Commands.ShouldBe(["apt-get update", "apt-get install -y openclaw", "systemctl enable --now openclaw"]);
        phase.Why.ShouldBe("It has the most memory, and must not share the cluster.");
    }

    /// <summary>
    /// A captured value is substituted into the command, not into prose around
    /// it, which is what makes the plan on screen the plan that runs.
    /// </summary>
    [Fact]
    public async Task APlaceholderInsideACommandIsWhatLaterPhasesUse()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("""
        {"phases":[
          {"name":"Control plane","hosts":["web-01"],"commands":["kubeadm init"],"capture":"join_command"},
          {"name":"Join","hosts":["web-02"],"commands":["{{join_command}}"]}]}
        """));

        var reading = await new Planner(backend).Draft(
            "build a cluster", ["web-01", "web-02"], TestContext.Current.CancellationToken);

        var plan = reading.ShouldBeOfType<PlanReading.Ok>().Plan;
        plan.Phases[1].Placeholders.ShouldBe(["join_command"]);
    }

    /// <summary>
    /// Which of three identical servers gets the single-node install is the whole
    /// of the decision, and the plan shows only which one won. The reasoning is
    /// the half that used to be dropped on the floor.
    /// </summary>
    [Fact]
    public async Task TheReasoningIsHandedOverAsItArrives()
    {
        var backend = new ScriptedBackend([
            new AssistEvent.Reasoning("web-01 has the most memory, "),
            new AssistEvent.Reasoning("so OpenClaw goes there."),
            new AssistEvent.Say(Answer),
            new AssistEvent.Finished(AssistStop.EndTurn),
        ]);
        var planner = new Planner(backend);
        List<string> thoughts = [];
        planner.Thought += (_, thought) => thoughts.Add(thought);

        await planner.Draft("build a cluster", ["web-01"], TestContext.Current.CancellationToken);

        // Each one carries the reasoning so far, so a reader assigns it and is done.
        thoughts.ShouldBe([
            "web-01 has the most memory, ",
            "web-01 has the most memory, so OpenClaw goes there.",
        ]);
    }

    [Fact]
    public async Task ThePlannerIsToldWhichHostsExistAndToUseNoOthers()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says(Answer));

        await new Planner(backend).Draft(
            "build a cluster", ["web-01", "web-02"], TestContext.Current.CancellationToken);

        var system = backend.Requests.Single().System;
        system.ShouldContain("web-01, web-02");
        system.ShouldContain("Use no others.");
        system.ShouldContain($"At most {AssistLimits.MaxPhases} phases");
    }

    [Fact]
    public async Task APlanNamingAHostNobodySelectedIsRefusedRatherThanShown()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says(
            """{"phases":[{"name":"Do it","hosts":["db-primary"],"commands":["true"]}]}"""));

        var reading = await new Planner(backend).Draft(
            "build a cluster", ["web-01"], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Refused>().Reason.ShouldContain("db-primary");
    }

    [Fact]
    public async Task NoHostsSelectedIsSaidBeforeAnythingIsAsked()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says(Answer));

        var reading = await new Planner(backend).Draft("build a cluster", [], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Refused>().Reason.ShouldBe("No hosts are selected.");
        backend.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AProviderFailureIsTheRefusal()
    {
        var backend = new ScriptedBackend { Fails = new AssistException("The key was refused.") };

        var reading = await new Planner(backend).Draft(
            "build a cluster", ["web-01"], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Refused>().Reason.ShouldBe("The key was refused.");
    }
}
