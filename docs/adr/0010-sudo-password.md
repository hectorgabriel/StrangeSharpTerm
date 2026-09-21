# Giving sudo a host's password

## Status

Accepted.

## Context

Every command the assistant runs — in the pane about one host, in a fan-out,
in a planned run — goes over an SSH **exec** channel. That was chosen on
purpose, and `docs/adr/0006` and the narration work both lean on it: exec gives
the exact output and the exact exit status, which is how the app knows a
command finished and whether it worked.

It also means the command has no terminal and runs as the account the host was
logged into. Two things follow that surprised the person using it:

- **`sudo` cannot prompt.** With no tty it fails at once: *a terminal is
  required to read the password*. A plan that installs Kubernetes is mostly
  `sudo`.
- **A root shell in the terminal pane does not help.** Running `sudo su` there
  makes *that* channel root. The assistant's commands are on a different one.

Verified against a real sshd: `tty` on the exec channel reports *not a tty*.

Four ways out were weighed: log in as root; configure passwordless sudo on the
hosts; type plan commands into the terminal instead; or have the app give
`sudo` the password it already holds for the host. The person chose the last.

## Decision

**Per host, and off until a person turns it on.** The host editor has *Give
sudo this host's password*, offered only where the host signs in with a
password credential — a key has nothing to give. The switch lives in
`preferences.json`, not the inventory, which stays byte-identical to the Swift
app's. The password stays where it was: the platform store, under the
credential it belongs to. The switch is only the permission to use it.

**On standard input, never on the command line.** `SshNetSession.RunFeeding`
writes the password to the exec channel's input and then closes it. A command
line is public on the server — anybody logged in can read it with `ps` — and
the input is not. The bytes are cleared as soon as they are written.

**What runs is `sudo -k -S -p '' …`.** `-S` reads the password from standard
input, `-p ''` keeps a prompt out of the output, and `-k` ignores a cached
credential so that `sudo` always reads the line it was sent. What the model
asked for and the transcript shows is the command as written; the rewrite
happens beneath both.

**Only where the password cannot end up anywhere but `sudo`.** This is the
heart of it, and `Sudo.Prepared` is where the rule lives. The password is given
only to a single command that begins with `sudo`. Each refusal is a leak, not a
limitation:

| Refused | Because |
|---|---|
| `sudo a && sudo b`, `sudo a; b`, `sudo a \| b`, `sudo a &` | a second reader of the input exists — and a short-circuit could leave a spare copy for it |
| `$(…)`, backticks, `<(…)` | a substitution can run anything, and anything can read the input |
| `sudo cmd < file`, `<<<` | `sudo` would read the file, not the password |
| `-n`, `-A`, `-S`, `-k`, `-K`, `-v`, `-l`, `-V`, `-e` | `sudo` would not read the password, or would read it and run nothing |

Options that take a value (`-u postgres`) are walked properly, because read
naively the value looks like the command and a `-n` after it would slip
through. `-un root` is `-u` with the value `n`, as getopt has it.

**And not at all where `sudo` wants no password.** `-k` cannot close one case:
a host with passwordless sudo, where `sudo` never reads its input and the
command after it would — `sudo tee /etc/motd` would write the password into the
file. So `sudo -n true` is asked first, with nothing on its input, and where it
succeeds no password is sent.

## What this costs

**A command that needs root twice has to be two commands.** That is the
single-`sudo` rule, and it is the price of never leaving a spare copy of a
password on somebody's input. In a conversation the model is told when a
command did not get the password and why, and splits it.

**A plan is held to the rule when it is written, not when it runs.** A plan
is one command per line, but nothing stopped a line being
`sudo apt-get update && sudo apt-get install -y nginx`. It was approved,
and at run time the host's assistant got the "split it" sentence while also
being told to run the commands as written and not improvise. It stopped, and
the phase failed. Letting it split the command would mean running something
nobody approved, so the planner is told the rule instead (with
`sudo sh -c '…'` as the way to get a pipe or a redirection as root), and
`RunPlan.Check` refuses a plan that breaks it, through `Sudo.Prepared`. It
refuses the same way on every host, switch on or off: the split form works
everywhere, and without a password `sudo` could not prompt anyway. A refused
plan goes back to the planner once, with the reason, before the person sees
it.

**One extra round trip** before each eligible `sudo` command, for the probe.

**The password is in this process as a string.** It already was: SSH.NET
authenticates with it that way. The copy made for the channel is the one this
change adds, and it is cleared.

## How it is checked

- `SudoTests` — the rule, with every refusal above as a case.
- `SudoPasswordTests` — the access layer: probe first, input not command line,
  nothing sent where none is needed. And one test through a whole assistant
  turn: the password reaches `sudo`, and appears in nothing the model, the
  transcript or the narration sees.
- `SudoPasswordLookupTests` — from the switch written to `preferences.json`,
  through the credential the host resolves to, to the store the secret is read
  from. The project has shipped the right secret read from the wrong store
  before; this is the test that would have caught it.
- Two of those were broken on purpose to prove they fail: skipping the probe,
  and putting the password on the command line.
- The integration gate proves against a real sshd that input reaches the
  command and the channel's input is then closed — otherwise `sudo`'s command
  would wait for more until it timed out.

What is not checked end to end is a real `sudo` on a real server: the local
sshd runs as the developer's own account, and exercising the password path
would mean feeding it their real password. That half rests on `sudo -S`
behaving as documented.
