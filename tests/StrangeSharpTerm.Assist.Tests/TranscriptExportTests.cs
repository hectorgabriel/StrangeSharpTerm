using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>A conversation as Markdown, for an incident's notes or a ticket.</summary>
public class TranscriptExportTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 20, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AConversationReadsAsAnAccountOfIt()
    {
        var markdown = TranscriptExport.Markdown(
            [
                new TranscriptEntry.Question("what is eating the disk?"),
                new TranscriptEntry.Answer(),
                new TranscriptEntry.Step
                {
                    Host = "web-01",
                    Command = "du -xh /var | sort -rh | head -3",
                    Why = "where the space went",
                    State = StepState.Ran,
                    ExitStatus = 0,
                    Output = "37G /var/log/journal\n2G /var/lib",
                },
                new TranscriptEntry.Step
                {
                    Host = "web-01",
                    Command = "journalctl --vacuum-size=1G",
                    Why = "free it",
                    Gate = "It deletes journal files.",
                    State = StepState.Refused,
                },
                new TranscriptEntry.Answer { Markdown = "The journal is **37G**.\n\nVacuuming it would free most of that." },
                new TranscriptEntry.Note("Stopped."),
            ],
            "web-01: assistant conversation",
            When,
            ["Answered by Claude · claude-opus-5-5."]);

        markdown.ShouldBe("""
            # web-01: assistant conversation

            Exported 2026-10-20 18:30 +00:00.
            Answered by Claude · claude-opus-5-5.
            Secrets were taken out before anything reached the provider, and they stay out here.

            ## what is eating the disk?

            **Ran, exit 0** · web-01 — where the space went
            ```sh
            du -xh /var | sort -rh | head -3
            ```
            ```
            37G /var/log/journal
            2G /var/lib
            ```

            **Refused** · web-01 — free it
            ```sh
            journalctl --vacuum-size=1G
            ```

            The journal is **37G**.

            Vacuuming it would free most of that.

            > *Stopped.*

            """.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Output is whatever the server printed, and a README it cat'ed has fences
    /// of its own. The block around it is longer than any of them.
    /// </summary>
    [Fact]
    public void OutputCannotCloseItsOwnFence()
    {
        var fenced = TranscriptExport.Fenced("# App\n```sh\nmake\n```", "").ToArray();

        fenced[0].ShouldBe("````");
        fenced[^1].ShouldBe("````");
    }

    /// <summary>What the provider saw is what goes: a marker stays a marker.</summary>
    [Fact]
    public void SecretsStayTakenOut()
    {
        var markdown = TranscriptExport.Markdown(
            [
                new TranscriptEntry.Question("what is the database password?"),
                new TranscriptEntry.Step
                {
                    Host = "db-primary",
                    Command = "grep PASSWORD /etc/app.env",
                    Why = "",
                    State = StepState.Ran,
                    ExitStatus = 0,
                    Output = "DB_PASSWORD=[redacted:9f3a1c2b7d4e]",
                },
            ],
            "db-primary",
            When);

        markdown.ShouldContain("DB_PASSWORD=[redacted:9f3a1c2b7d4e]");
    }

    /// <summary>A file's change goes as the diff the person approved; a tool's call as what it was sent.</summary>
    [Fact]
    public void FilesAndToolsSayWhatTheyDid()
    {
        var markdown = TranscriptExport.Markdown(
            [
                new TranscriptEntry.Question("fix the port"),
                new TranscriptEntry.Step
                {
                    Host = "this machine",
                    Command = "write notes/incident.md (this machine)",
                    Why = "the findings",
                    Gate = "It writes notes/incident.md.",
                    State = StepState.Ran,
                    IsFile = true,
                    Detail = "+ web-01: journal at 37G",
                },
                new TranscriptEntry.Step
                {
                    Host = "web-01",
                    Command = "Pager · create_incident",
                    Why = """{"title":"disk"}""",
                    Destination = "pager.example",
                    State = StepState.Ran,
                },
            ],
            "web-01",
            When);

        markdown.ShouldContain("**Allowed** · write notes/incident.md (this machine) — the findings\n```diff\n+ web-01: journal at 37G\n```");
        // Ran without asking: a standing pass for that tool.
        markdown.ShouldContain("**Ran** · Pager · create_incident\n```json\n{\"title\":\"disk\"}\n```");
    }

    [Fact]
    public void ATimedOutCommandSaysHowItEnded()
    {
        var markdown = TranscriptExport.Markdown(
            [
                new TranscriptEntry.Question("watch it"),
                new TranscriptEntry.Step { Host = "web-01", Command = "tail -f app.log", Why = "", State = StepState.TimedOut, MayStillBeRunning = true },
            ],
            "web-01",
            When);

        markdown.ShouldContain("**Timed out, may still be running** · web-01");
    }
}
