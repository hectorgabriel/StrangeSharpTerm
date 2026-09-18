namespace StrangeSharpTerm.Assist;

/// <summary>
/// One call to a folder: listed, read, or written.
///
/// Its own class because two agents make these now. The conversation about one
/// host has a folder on that host and a folder on this machine; the one about a
/// fleet has only this machine's, because a write fanned out across eight
/// servers from a single approval is the one thing this app should not make
/// easy. What must not differ between them is any of the rest of it — the
/// policy, the budget, the gate, the diff the gate shows, the refusal that
/// never reaches a person, the marker that is never written back. So there is
/// one of it.
/// </summary>
/// <param name="machine">
/// Whose files these are, in the words a person is asked in: a host's own name,
/// or "this machine". It goes in the row and in the question, because two
/// folders are open and the paths in them look alike.
/// </param>
/// <param name="append">Adds a row to whichever transcript this is for.</param>
/// <param name="updated">Says a row changed under it.</param>
/// <param name="narrate">
/// Told when a write to a host starts and finishes, so the pane showing that
/// host can say so. Null for this machine's files, which have no terminal to
/// say it in — and saying it in a host's would be saying it about the wrong
/// computer.
/// </param>
internal sealed class WorkspaceCalls(
    IWorkspaceAccess access,
    ICommandGate gate,
    string machine,
    Action<TranscriptEntry> append,
    Action<TranscriptEntry> updated,
    Action<AssistStep>? narrate = null)
{
    /// <summary>A file was written. Whoever is showing that folder wants to know.</summary>
    internal event EventHandler<FileChange>? Wrote;

    /// <inheritdoc cref="HostAgent.Truncate"/>
    private static string Truncate(string output) => HostAgent.Truncate(output);

    internal async Task<(bool Ran, AssistToolResult Result)> Carry(
        AssistToolCall call,
        ICommandBudget budget,
        CancellationToken cancellationToken)
    {
        var writing = WorkspaceTools.Writes(call.Name);
        var local = WorkspaceTools.IsLocal(call.Name);
        var (path, content, why) = writing
            ? WorkspaceTools.ReadWrite(call.Arguments)
            : (WorkspaceTools.ReadPath(call.Arguments), "", "");

        var operation = call.Name switch
        {
            WorkspaceTools.ListFiles or WorkspaceTools.ListLocalFiles => FileOperation.List,
            WorkspaceTools.ReadFile or WorkspaceTools.ReadLocalFile => FileOperation.Read,
            _ => FileOperation.Write,
        };
        var verb = operation switch
        {
            FileOperation.List => "list",
            FileOperation.Read => "read",
            _ => "write",
        };

        var judgement = FilePolicy.Judge(operation, access.Root, path);
        var step = new TranscriptEntry.Step
        {
            Host = machine,
            // The machine is part of the line, not a detail underneath it: two
            // folders are open and the paths in them look alike.
            Command = $"{verb} {(path.Length == 0 ? "?" : path)}{(local ? " (this machine)" : "")}",
            Why = why,
            Gate = judgement.Reason,
            IsDestructive = judgement.IsDestructive,
            IsFile = true,
        };
        append(step);

        // Taken before anything is decided, exactly as a command is: a model
        // asking repeatedly for paths outside the folder is spending turns, and
        // the budget is what bounds that.
        if (!budget.Take())
        {
            step.State = StepState.Skipped;
            updated(step);
            return (false, new AssistToolResult(
                call.Id,
                "The budget for this question is spent. Do not ask for anything else; "
                    + "summarise what you have found so far.",
                Failed: true));
        }

        // Outside the folder is not a question for a person. They answered it
        // when they chose the folder, and asking again would teach them to say
        // yes to a bar they have stopped reading.
        if (judgement.IsRefused)
        {
            step.State = StepState.Refused;
            updated(step);
            return (false, new AssistToolResult(call.Id, judgement.Reason, Failed: true));
        }

        try
        {
            if (!writing)
                return await Read(call, step, path, cancellationToken);

            var change = await access.Plan(path, content, cancellationToken);

            // A model that read a scrubbed file and sent it back would write the
            // marker into the real one, turning a password into the word
            // [redacted].
            //
            // A new file too, which is not the same failure and is just as bad:
            // a .env.production copied from a scrubbed .env is a deploy whose
            // password is the word that hid the password.
            if (change.Text.Contains(Redaction.Marker, StringComparison.Ordinal))
            {
                step.State = StepState.Refused;
                step.Output = $"It would write {Redaction.Marker} into the file.";
                updated(step);
                return (false, new AssistToolResult(
                    call.Id,
                    $"This write contains {Redaction.Marker}, which is what this app puts in place of a "
                        + "secret before you see it -- it is not the real value and must not be written "
                        + "anywhere. Leave those lines out of your change, or ask the user to fill them in.",
                    Failed: true));
            }

            if (change.Diff.IsEmpty && !change.Creates)
            {
                // Nothing to approve and nothing to do. Saying so is better than
                // a gate that asks a person to allow a write that changes
                // nothing.
                step.State = StepState.Ran;
                step.Output = "It already says exactly that.";
                updated(step);
                return (true, new AssistToolResult(
                    call.Id,
                    $"{change.Relative} already contains exactly that. Nothing was written."));
            }

            // Judged again now that the size of it is known: "it writes
            // nginx.conf" and "it writes nginx.conf, and 380 of its 400 lines
            // change" are not the same question.
            var reason = change.Creates
                ? $"It creates {change.Relative} on {machine}, which is not there yet."
                : $"{judgement.Reason.TrimEnd('.')} on {machine}. {change.Diff.Summary}.";

            step.Gate = reason;
            step.Detail = change.Diff.Text;
            updated(step);

            var allowed = await gate.Allow(
                new PendingCommand(
                    machine,
                    $"write {change.Relative}",
                    why,
                    reason,
                    judgement.IsDestructive,
                    change.Diff.Text),
                cancellationToken);

            if (!allowed)
            {
                step.State = StepState.Refused;
                updated(step);
                return (false, new AssistToolResult(
                    call.Id,
                    "The user refused this change. Do not write it somewhere else and do not suggest a "
                        + "command that would make the same change. Work with what you have, or say what "
                        + "you would need and why.",
                    Failed: true));
            }

            step.State = StepState.Running;
            updated(step);
            narrate?.Invoke(new AssistStep(machine, step.Command, why, Running: true));

            await access.Write(change, cancellationToken);

            step.State = StepState.Ran;
            step.ExitStatus = 0;
            step.Output = change.Diff.Text;
            updated(step);
            narrate?.Invoke(new AssistStep(
                machine, step.Command, why, Running: false, 0, change.Diff.Summary));
            Wrote?.Invoke(this, change);

            return (true, new AssistToolResult(
                call.Id,
                change.Creates
                    ? $"Created {change.Relative} on {machine}."
                    : $"Wrote {change.Relative} on {machine}: {change.Diff.Summary}."));
        }
        catch (OperationCanceledException)
        {
            narrate?.Invoke(new AssistStep(machine, step.Command, why, Running: false));
            throw;
        }
        catch (Exception e)
        {
            step.State = StepState.Failed;
            step.Output = e.Message;
            updated(step);
            narrate?.Invoke(new AssistStep(machine, step.Command, why, Running: false));
            return (false, new AssistToolResult(call.Id, e.Message, Failed: true));
        }
    }

    /// <summary>
    /// A listing or a file, which the policy lets through without asking.
    ///
    /// Redacted and truncated like command output, and for the same reason -- a
    /// file in a workspace is exactly where an API key lives. The count goes
    /// back with it, because a model that rewrote a scrubbed file whole would
    /// replace the secret with the word that hid it.
    /// </summary>
    private async Task<(bool Ran, AssistToolResult Result)> Read(
        AssistToolCall call,
        TranscriptEntry.Step step,
        string path,
        CancellationToken cancellationToken)
    {
        step.State = StepState.Running;
        updated(step);

        string text;
        var note = "";
        if (call.Name is WorkspaceTools.ListFiles or WorkspaceTools.ListLocalFiles)
        {
            text = await access.List(path, cancellationToken);
        }
        else
        {
            var file = await access.Read(path, cancellationToken);
            if (file.IsBinary)
            {
                step.State = StepState.Failed;
                step.Output = $"{file.Relative} is not a text file.";
                updated(step);
                return (true, new AssistToolResult(
                    call.Id,
                    $"{file.Relative} on {machine} is not a text file ({file.Length} bytes). Nothing was read.",
                    Failed: true));
            }

            var scrubbed = Redaction.Scrub(file.Text);
            text = scrubbed.Text;
            if (scrubbed.Count > 0)
                note = $"\n\n{scrubbed.Count} secret(s) were removed from this file before you saw it. "
                    + $"Do not write {Redaction.Marker} back into it.";
            if (file.Truncated)
                note += $"\n\nThis is the first {Transport.RemoteWorkspace.MaxFileBytes / 1000} kB of a "
                    + $"{file.Length}-byte file.";
        }

        var output = Truncate(text);
        step.State = StepState.Ran;
        step.ExitStatus = 0;
        step.Output = output;
        updated(step);

        return (true, new AssistToolResult(call.Id, output + note));
    }
}
