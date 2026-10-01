using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// A plan that ends by saving a file on this machine.
///
/// Before this a plan had no way to say "and put it here" but scp, run on a
/// server that cannot reach the desk -- which is what a planner asked to save
/// its findings wrote.
/// </summary>
public class PlanSaveTests
{
    private static readonly string[] Hosts = ["web-01", "web-02"];

    private const string SavingPlan = """
        {"phases": [
          {"name": "Look", "hosts": ["web-01"], "commands": ["df -h"]},
          {"name": "Write it down", "why": "Disk use per host.", "save": "findings.md"}
        ]}
        """;

    [Fact]
    public void ASavingPhaseIsReadWhenSavingIsAllowed()
    {
        var plan = RunPlan.Read(SavingPlan, Hosts, maySave: true).ShouldBeOfType<PlanReading.Ok>().Plan;

        var saving = plan.Phases[1];
        saving.Saves.ShouldBeTrue();
        saving.Save.ShouldBe("findings.md");
        saving.Hosts.ShouldBeEmpty();
    }

    [Fact]
    public void ASavingPhaseIsRefusedWithTheSwitchOff() =>
        RunPlan.Read(SavingPlan, Hosts).ShouldBeOfType<PlanReading.Refused>()
            .Reason.ShouldContain("switched off");

    [Fact]
    public void ASavingPhaseCannotAlsoRunCommands() =>
        RunPlan.Read(
                """{"phases": [{"name": "Both", "hosts": ["web-01"], "commands": ["df -h"], "save": "out.md"}]}""",
                Hosts,
                maySave: true)
            .ShouldBeOfType<PlanReading.Refused>().Reason.ShouldContain("cannot also name hosts");

    [Fact]
    public async Task TheRunnerHandsTheSaverWhatTheEarlierPhasesFound()
    {
        var saver = new Saver(new PlanSave(true, "Created findings.md on this machine."));
        var runner = new PlanRunner(_ => null, saver);
        var plan = RunPlan.Read(SavingPlan, Hosts, maySave: true).ShouldBeOfType<PlanReading.Ok>().Plan;
        plan.Phases[0].IsEnabled = false;

        var result = await runner.Run(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Phases[1].Outcome.ShouldBe(PhaseOutcome.Saved);
        result.Phases[1].Note.ShouldBe("Created findings.md on this machine.");
        saver.Reported.ShouldNotBeNull().ShouldContain("# Look (switched off)");
    }

    [Fact]
    public async Task WithNothingToSaveWithTheRunCarriesOnAndSaysSo()
    {
        var plan = new RunPlan([new PlanPhase { Name = "Save", Hosts = [], Commands = [], Save = "out.md" }]);

        var result = await new PlanRunner(_ => null).Run(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Stopped.ShouldBeFalse();
        result.Phases.Single().Outcome.ShouldBe(PhaseOutcome.NotSaved);
    }

    [Fact]
    public async Task TheFileLandsAtThePhasesPathAfterTheGateShowsTheDiff()
    {
        var here = new FakeWorkspace("/Users/you/runbooks");
        var gate = new RecordingGate(answer: true);
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(PlanSaver.SaveFile, new { content = "# Disk\n\n- web-01: 91%\n", why = "findings" }));
        var saver = new PlanSaver(backend, here, gate, () => true);

        var saved = await saver.Save(Saving("findings.md"), "# Look (done)\n## web-01\n91%", cancellationToken: TestContext.Current.CancellationToken);

        saved.Saved.ShouldBeTrue();
        here.Written["findings.md"].ShouldContain("web-01: 91%");
        var asked = gate.Asked.Single();
        asked.Host.ShouldBe("this machine");
        backend.Requests.Single().Messages.Single().Text.ShouldNotBeNull().ShouldContain("91%");
    }

    [Fact]
    public async Task ARefusalEndsItWithoutAskingTheModelAgain()
    {
        var here = new FakeWorkspace("/Users/you/runbooks");
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(PlanSaver.SaveFile, new { content = "x\n", why = "findings" }),
            ScriptedBackend.Calls(PlanSaver.SaveFile, new { content = "y\n", why = "findings" }));
        var saver = new PlanSaver(backend, here, new RecordingGate(answer: false), () => true);

        var saved = await saver.Save(Saving("findings.md"), "", cancellationToken: TestContext.Current.CancellationToken);

        saved.Saved.ShouldBeFalse();
        saved.Note.ShouldContain("refused");
        backend.Requests.Count.ShouldBe(1);
        here.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsAskedOrWritten()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("unused"));
        var saver = new PlanSaver(backend, new FakeWorkspace("/Users/you/runbooks"), new StandingAnswer(true), () => false);

        var saved = await saver.Save(Saving("findings.md"), "", cancellationToken: TestContext.Current.CancellationToken);

        saved.Saved.ShouldBeFalse();
        saved.Note.ShouldContain("Write files here");
        backend.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ThePlannerIsToldItMaySaveOnlyWhileTheSwitchIsOn()
    {
        var on = true;
        var backend = new ScriptedBackend(ScriptedBackend.Says(SavingPlan), ScriptedBackend.Says(SavingPlan), ScriptedBackend.Says(SavingPlan));
        var planner = new Planner(backend, new FakeWorkspace("/Users/you/runbooks"), () => on);

        var first = await planner.Draft("check disks and save it here", ["web-01"], cancellationToken: TestContext.Current.CancellationToken);
        first.ShouldBeOfType<PlanReading.Ok>();
        backend.Requests[0].System.ShouldContain("\"save\"");

        on = false;
        await planner.Draft("again", ["web-01"], cancellationToken: TestContext.Current.CancellationToken);
        backend.Requests[1].System.ShouldNotContain("\"save\"");
        backend.Requests[1].System.ShouldContain("\"Write files here\" is switched off");
    }

    [Fact]
    public async Task ThePlannerIsToldHostCommandsCannotReachThisMachine()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("""{"phases":[{"name":"a","hosts":["web-01"],"commands":["df -h"]}]}"""));

        await new Planner(backend).Draft("save it here", ["web-01"], cancellationToken: TestContext.Current.CancellationToken);

        var system = backend.Requests.Single().System;
        system.ShouldContain("never on the user's own machine");
        system.ShouldContain("open a folder in the sidebar");
    }

    private static PlanPhase Saving(string path) =>
        new() { Name = "Write it down", Why = "Disk use per host.", Hosts = [], Commands = [], Save = path };

    private sealed class Saver(PlanSave answer) : IPlanSaver
    {
        public string? Reported { get; private set; }

        public Secrets? Secrets { get; private set; }

        public Task<PlanSave> Save(
            PlanPhase phase,
            string reported,
            Secrets? secrets = null,
            CancellationToken cancellationToken = default)
        {
            Reported = reported;
            Secrets = secrets;
            return Task.FromResult(answer);
        }
    }
}
