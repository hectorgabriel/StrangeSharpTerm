# Secrets the model carries by name and never sees

## Status

Accepted.

## Context

Redaction was one-way. A secret in command output, a terminal tail or a file
became `[redacted]` before it went to a provider, and was gone. That is right
for the provider and was wrong for the person, in exactly the case plans were
built around.

A planned run captures a value on one host and uses it on another. The example
the planner is given is the join command a control plane prints, and that
command carries a bootstrap token. The model on the control plane saw
`kubeadm join … --token [redacted] …`, captured that, and the worker phase ran it
with the word in place of the token. The prompt's own example could not work.
Saving the join command to a file failed the same way, because a write
containing `[redacted]` is refused (ADR 0009), which is correct, since the
alternative is a file holding the word instead of the secret.

The ways out that were on the table:

- **A switch that turns redaction off.** The token goes to the provider. That is
  the one thing redaction exists to prevent, and a switch that is on for one run
  stays on for the next.
- **Capturing from raw output instead of from the model's answer.** It fixes the
  capture and nothing else. The worker's model still has to be told the command,
  so the token is in that request instead.

## Decision

**Each conversation keeps what it hid, and the model carries a name for it.**
`Secrets` is a per-conversation store. `Redaction.Scrub` given one replaces each
secret with a marker of its own, `[redacted:9f3a1c2b7d4e]`, and keeps the value.
The same value always gets the same marker. Markers are random rather than
counted, so one cannot be guessed. A private key is never kept. Without a store,
scrubbing is the one-way `[redacted]` it always was.

**A value goes back in at exactly two places, and both are things a person
approved.**

1. **A file write.** `WorkspaceCalls` restores the conversation's markers before
   the diff is worked out, so the gate shows the real value, on this machine,
   and the person approves what will actually land. The model is told only that
   the file was written.
2. **A plan command that was approved with a placeholder in it.** `PlanRunner`
   gathers each host's secrets for the markers in that host's answer into one
   store for the run. A phase is granted only the ones its own commands contain,
   and `HostAgent` fills them into a command only when the model's command is,
   character for character, one the phase lists.

Nothing else turns a marker back into a secret. Output that talks a model into
`curl …?t=[redacted:…]` sends the marker, or is refused if it carries a granted
one. A command that names a granted marker's id without being the marker (a
small model dropped the brackets and would have run the id as the token) is
refused, with the exact command to try again. The retry spends the phase's
budget, so a model that cannot copy the line stops instead of looping.

## Consequences

- The token never appears in a request to any provider. This is tested end to
  end, and was checked against live Gemini 3.8 Flash and 3.1 Pro, both of which
  joined the worker and saved `join.sh` with the real token.
- Gemini 3.5 Flash-Lite would not copy the marker even when given the exact
  text, so its join phase stops with nothing run. That is the failure this is
  designed to have.
- `--certificate-key` (kubeadm's control-plane join key) is now redacted, and was
  not before. `--token=value` is counted once rather than twice.
- What the pane shows is the marker, not the value: the captured value under a
  phase, a command's row, its gate. The value appears on screen only in the diff
  of a write about to land.
- Secrets live in memory for as long as the conversation that refers to them,
  no longer than the terminal scrollback that held them first.
