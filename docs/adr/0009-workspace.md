# The workspace: one root, and who is allowed to write in it

## Status

Accepted, in M9.

## Context

The app could already move files — the SFTP browser lists a directory, uploads,
downloads and deletes. What it could not do is the thing people actually do on a
server, which is open a file, change three lines of it, and put it back. And the
assistant, which by M7 could run commands and call connected tools, could not
read a configuration file at all except by asking a host to `cat` it, which
returns eight thousand characters of someone else's opinion about line wrapping
and cannot write anything back.

The obvious move is to give the model a `read_file` and a `write_file` tool. The
question that decides the whole design is what bounds them. A command has
`CommandPolicy`: an allowlist that reads the command and judges every stage of
it. There is no equivalent for a path, because a path is not dangerous or safe in
itself — `nginx.conf` is a file somebody wants edited and also the file that
stops being a web server if you get it wrong.

## Decision

**The permission is a folder, and a person chooses it.** `RemoteWorkspace` is a
root plus the rule that nothing outside it is touched. The pane and the
assistant go through the same object, so opening `~/srv/app` is one act with two
consequences: it is what the tree shows, and it is what the model may work in.
Closing the pane closes the folder, and the file tools stop being offered at all
— not refused, not offered.

That is the whole of the trust model, and it is deliberately not a list of rules
about paths. A person looking at a directory knows what is in it. A policy
guessing which paths are precious does not, and the guessing is where these
things go wrong.

**Inside the folder, reading is free and writing stops.** `FilePolicy` mirrors
`CommandPolicy`'s asymmetry for the same reason: a read wrongly stopped costs a
click, and a write wrongly allowed costs a file. Reading needs no switch because
opening the folder was the decision; writing needs **Edit files** turned on for
that conversation *and* an approval per write.

**Outside the folder is refused, not asked.** This is the one place the gate is
deliberately not opened. The person already answered the question when they chose
the folder, and a bar that asks anyway — several times, because a model that is
refused tries another path — is a bar people learn to approve without reading.
The refusal goes back to the model as a sentence it can act on.

**What stops at the gate is the diff, not the intention.** "The assistant would
like to write nginx.conf" is not a question anybody can answer. So a write is
worked out before it is put: the file is read, `Diff` compares it with what the
model sent, and the lines that change are in the approval bar itself, with the
count in the sentence above them. Approving a write you have not read is
approving a file you have not read. This is why `IWorkspaceAccess` has both
`Plan` and `Write` — a person has to fit between them.

## What this costs, and what it does not buy

**It cannot see through a symbolic link.** Paths are resolved as text, before
anything is sent, because the decision has to be made before the request rather
than after it. A link inside the root pointing at `/etc` resolves on the server.
Following it properly would mean a round trip per path component on every call
and would still race whoever could replace the link between the check and the
write. So the guarantee is narrower than it looks: what is bounded is the path
this app asks for, not the inode the server decides that names. The gate is the
other half of the answer, and it is the half with a person in it.

**A scrubbed file must never be written back.** Everything that goes to a
provider is redacted, files included — a workspace is exactly where an API key
lives. But a model that reads a scrubbed `.env`, changes one line and sends the
whole file back would write `[redacted]` where the password was. So a write whose
contents contain the redaction marker is refused before it reaches the gate,
and the model is told why. A diff would have shown it; a person reading a
forty-line diff might not.

**A file is read whole or not at all.** Two megabytes, which is larger than any
configuration file anyone edits. Past that the editor shows the start and refuses
to save, because saving part of a file you read part of is how the rest of it
disappears.

## Three things that only showed up by building it

**Line endings are a correctness problem, not a detail.** The text box on a
Windows machine ends every line with a carriage return. Saving a Linux host's
`nginx.conf` from Windows without doing anything about that rewrites all four
hundred lines to change one, and `git diff` on the server says so. The file's own
ending is read with it, the editor holds plain `\n`, and the ending goes back on
at the last moment. This is the same rule the rest of the app follows for
anything crossing the wire, in the one place a person can most easily break it.

**A write is truncation, not a rename.** The atomic way to replace a file is to
write a temporary one and rename it over the top. That is the wrong way here: the
new file carries the mode and ownership of whoever wrote it, so saving
`/etc/nginx/nginx.conf` that way turns a root-owned 0644 file into one owned by
the account that saved it. Truncating in place keeps the inode and everything
hanging off it.

**Two writers, one file, and the pane does not get to pick.** The terminal beside
this pane is on the same machine, and so is the assistant. Saving checks the
modification time and asks before replacing somebody else's work; when the
assistant writes a file that is open and clean, the editor reloads; when it is
open and *dirty*, nothing is thrown away and the banner says what happened.
Choosing silently in any of those three cases would be the pane deciding whose
afternoon matters more.

## What was considered and rejected

**One `file` tool with an `operation` argument.** It would make the gate's
decision depend on parsing an argument the model chose. Reading is safe and
writing is not, so they are separate tools, and the safe one is the one that can
be offered alone.

**A local mirror the system editor opens.** It is what a File Provider extension
would have given on macOS — and M8 of the Swift app is exactly the milestone
that never launched, because it needed an entitlement a free account cannot
issue. A mirror also needs a sync story, a conflict story and a story about what
happens when the app quits with unsaved files in it. A pane needs none of those.

**A syntax-highlighting editor.** AvaloniaEdit would be a package and a theme to
maintain for a feature nobody asked for. The editor is a text box, a line-number
gutter and a save button; what makes it worth having is the connection
underneath, not the highlighting.
