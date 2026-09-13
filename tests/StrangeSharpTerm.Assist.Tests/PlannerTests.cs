using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class PlannerTests
{
    private const string Answer = """
    {"phases":[{"name":"Prepare","hosts":["web-01"],"task":"Install containerd."}]}
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
            """{"phases":[{"name":"Do it","hosts":["db-primary"],"task":"x"}]}"""));

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
