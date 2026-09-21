using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class PlannerTests
{
    private const string Answer = """
    {"phases":[{"name":"Prepare","hosts":["web-01"],"why":"It is the only host.",
      "commands":["apt-get install -y containerd"]}]}
    """;

    /// <summary>A plan naming a host nobody selected, which is always refused.</summary>
    private const string Stranger = """{"phases":[{"name":"Do it","hosts":["db-primary"],"commands":["true"]}]}""";

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
    /// The reason for the folder: a plan written from the runbook the person
    /// opened, rather than from what the model guesses a runbook says.
    /// </summary>
    [Fact]
    public async Task ItReadsTheFolderOnThisMachineBeforeItPlans()
    {
        var here = new FakeWorkspace("/Users/you/runbooks").With("cluster.md", "Use containerd, not docker.");
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(WorkspaceTools.ReadLocalFile, new { path = "cluster.md" }),
            ScriptedBackend.Says(Answer));
        var planner = new Planner(backend, here);
        var looked = new List<string>();
        planner.Looked += (_, step) => looked.Add(step.Command);

        var reading = await planner.Draft("follow the runbook", ["web-01"], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Ok>();
        backend.Requests[0].Tools.Select(tool => tool.Name)
            .ShouldBe([WorkspaceTools.ListLocalFiles, WorkspaceTools.ReadLocalFile]);
        backend.Requests[0].System.ShouldContain("~/srv/app");
        backend.Requests[1].Messages.SelectMany(message => message.ToolResults).Single().Output
            .ShouldContain("Use containerd, not docker.");
        looked.ShouldContain("read cluster.md (this machine)");
        // What the next turn remembers is the question and the plan, not the reads.
        planner.Turns.ShouldBe(1);
    }

    [Fact]
    public async Task ItCannotWriteThereWhateverItAsksFor()
    {
        var here = new FakeWorkspace("/Users/you/runbooks").With("cluster.md", "old");
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(
                WorkspaceTools.WriteLocalFile,
                new { path = "cluster.md", content = "new", why = "tidy it" }),
            ScriptedBackend.Says(Answer));

        await new Planner(backend, here).Draft("plan it", ["web-01"], TestContext.Current.CancellationToken);

        here.Written.ShouldBeEmpty();
        backend.Requests[1].Messages.SelectMany(message => message.ToolResults).Single().Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task WithNoFolderOpenItIsOfferedNothingAndToldNothing()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says(Answer));

        await new Planner(backend, new FakeWorkspace("")).Draft(
            "plan it", ["web-01"], TestContext.Current.CancellationToken);

        backend.Requests.Single().Tools.ShouldBeEmpty();
        backend.Requests.Single().System.ShouldNotContain("list_local_files");
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

    /// <summary>
    /// A plan is rarely right first time, and the second instruction is almost
    /// always about the first. Without a history, "put it on the other one" is
    /// read by something that has never seen the plan it is being asked to
    /// change.
    /// </summary>
    [Fact]
    public async Task ASecondPlanIsASecondTurnAndNotAFirstOne()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says(Answer),
            ScriptedBackend.Says(Answer));
        var planner = new Planner(backend);

        await planner.Draft("install OpenClaw somewhere", ["web-01"], TestContext.Current.CancellationToken);
        await planner.Draft("put it on the other one instead", ["web-01"], TestContext.Current.CancellationToken);

        // The second request carries the first exchange and then the new ask.
        var second = backend.Requests[1].Messages;
        second.Count.ShouldBe(3);
        second[0].Text.ShouldBe("install OpenClaw somewhere");
        second[1].Role.ShouldBe(AssistRole.Assistant);
        second[2].Text.ShouldBe("put it on the other one instead");
        planner.Turns.ShouldBe(2);
    }

    /// <summary>
    /// The planner writes a plan, the hosts carry it out, and the results went
    /// nowhere: the next question was answered by the one participant that never
    /// found out whether any of it worked.
    /// </summary>
    [Fact]
    public async Task WhatTheHostsReportedReachesTheNextQuestion()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says(Answer),
            ScriptedBackend.Says(Answer));
        var planner = new Planner(backend);

        await planner.Draft("install OpenClaw", ["web-01"], TestContext.Current.CancellationToken);
        planner.Record("## web-01 (failed)\nE: Unable to locate package openclaw");
        await planner.Draft("try something else", ["web-01"], TestContext.Current.CancellationToken);

        var asked = backend.Requests[1].Messages[^1].Text.ShouldNotBeNull();
        asked.ShouldContain("Unable to locate package openclaw");
        asked.ShouldContain("try something else");
    }

    /// <summary>
    /// Folded into the next question rather than sent as a turn of its own: a
    /// conversation alternates, and a report with nothing answering it is not a
    /// turn. Carried once, because after that it is in the history.
    /// </summary>
    [Fact]
    public async Task WhatWasReportedIsCarriedOnceAndNotAgain()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says(Answer),
            ScriptedBackend.Says(Answer),
            ScriptedBackend.Says(Answer));
        var planner = new Planner(backend);

        await planner.Draft("install it", ["web-01"], TestContext.Current.CancellationToken);
        planner.Record("## web-01 (failed)\nNo such package.");
        await planner.Draft("try again", ["web-01"], TestContext.Current.CancellationToken);
        await planner.Draft("and again", ["web-01"], TestContext.Current.CancellationToken);

        // Every message is still one side of an exchange.
        backend.Requests[2].Messages.Select(message => message.Role).ShouldBe([
            AssistRole.User, AssistRole.Assistant,
            AssistRole.User, AssistRole.Assistant,
            AssistRole.User,
        ]);
        backend.Requests[2].Messages[^1].Text.ShouldBe("and again");
    }

    /// <summary>
    /// What is ticked changes between turns, so the host list is written fresh
    /// each time rather than carried in the history.
    /// </summary>
    [Fact]
    public async Task TheHostsAreSaidAgainEachTurnRatherThanRemembered()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says(Answer),
            ScriptedBackend.Says(Answer));
        var planner = new Planner(backend);

        await planner.Draft("build a cluster", ["web-01"], TestContext.Current.CancellationToken);
        await planner.Draft("add the others", ["web-01", "web-02"], TestContext.Current.CancellationToken);

        backend.Requests[0].System.ShouldContain("web-01.");
        backend.Requests[1].System.ShouldContain("web-01, web-02");
    }

    /// <summary>
    /// A refusal is exactly the turn the next one needs to see, or it writes the
    /// same unusable plan again.
    /// </summary>
    [Fact]
    public async Task ARefusedPlanStaysInTheHistory()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says(Stranger),
            ScriptedBackend.Says(Stranger),
            ScriptedBackend.Says(Answer));
        var planner = new Planner(backend);

        var refused = await planner.Draft("do it", ["web-01"], TestContext.Current.CancellationToken);
        refused.ShouldBeOfType<PlanReading.Refused>();

        await planner.Draft("try again", ["web-01"], TestContext.Current.CancellationToken);

        // The ask, the plan, the refusal sent back, the same plan again -- and then the new ask.
        backend.Requests[2].Messages.Count.ShouldBe(5);
        backend.Requests[2].Messages[1].Text.ShouldNotBeNull().ShouldContain("db-primary");
    }

    /// <summary>
    /// Every refusal says what to change, so the model is given it before the
    /// person is. A plan with a sudo chained after another is a rewrite, not a
    /// question for anybody.
    /// </summary>
    [Fact]
    public async Task ARefusedPlanIsSentBackToBeWrittenAgainOnce()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says("""
            {"phases":[{"name":"Install","hosts":["web-01"],
              "commands":["sudo apt-get update && sudo apt-get install -y nginx"]}]}
            """),
            ScriptedBackend.Says("""
            {"phases":[{"name":"Install","hosts":["web-01"],
              "commands":["sudo apt-get update","sudo apt-get install -y nginx"]}]}
            """));
        var planner = new Planner(backend);

        var reading = await planner.Draft("install nginx", ["web-01"], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Ok>().Plan.Phases.Single().Commands.Count.ShouldBe(2);
        backend.Requests.Count.ShouldBe(2);
        var sentBack = backend.Requests[1].Messages[^1].Text.ShouldNotBeNull();
        sentBack.ShouldContain("refused");
        sentBack.ShouldContain(Sudo.NotGiven);
    }

    [Fact]
    public async Task ASecondRefusalGoesToThePersonRatherThanRoundAgain()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says(Stranger),
            ScriptedBackend.Says(Stranger),
            ScriptedBackend.Says(Answer));

        var reading = await new Planner(backend).Draft(
            "build a cluster", ["web-01"], TestContext.Current.CancellationToken);

        reading.ShouldBeOfType<PlanReading.Refused>().Reason.ShouldContain("db-primary");
        backend.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ThePlannerIsToldWhatShapeASudoMustTake()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says(Answer));

        await new Planner(backend).Draft("install nginx", ["web-01"], TestContext.Current.CancellationToken);

        var system = backend.Requests.Single().System;
        system.ShouldContain("sudo cannot ask for a password");
        system.ShouldContain("sudo sh -c");
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
        var backend = new ScriptedBackend(ScriptedBackend.Says(Stranger), ScriptedBackend.Says(Stranger));

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
