# StrangeTerm

![macOS 14+](https://img.shields.io/badge/macOS-14%2B-111111?logo=apple&logoColor=white)
![Swift 6.0](https://img.shields.io/badge/Swift-6.0-F05138?logo=swift&logoColor=white)
![SwiftUI](https://img.shields.io/badge/UI-SwiftUI-0A84FF)

A native macOS app for managing remote servers over SSH — connection inventory,
first-class terminal, SFTP, tunnels, an assistant that can read the session
beside it, and real Finder integration.

![The host inventory and a server's detail](Docs/screenshots/dashboard.png)

Every screenshot here is drawn by the app itself with `--render` (see UI
snapshots below) against the built-in sample inventory, so they stay honest as
the interface changes and no real host ever appears in one.

## Architecture

All SSH work is owned by a single unsandboxed user-level LaunchAgent
(`STConnectionAgent`). The main app and the sandboxed Finder extensions are thin
XPC clients of it. This is forced by a real constraint — File Provider and Finder
Sync extensions must be sandboxed and so cannot spawn `ssh` or read `~/.ssh` —
and it pays for itself: one OpenSSH `ControlMaster` connection per server is
multiplexed by terminal tabs, SFTP, the Finder mount and every port forward, so
the user authenticates once and tunnels outlive the UI.

See `Packages/StrangeTermCore` for the testable core.

## Development

```sh
cd Packages/StrangeTermCore
swift test                              # unit tests
swift run stctl parse ~/.ssh/config     # summarise a config
swift run stctl resolve ~/.ssh/config   # show effective settings per host

./Scripts/make-app.sh                   # generates the project, builds the app
open "$(./Scripts/make-app.sh)"
```

### Project generation

`project.yml` is the source of truth; `StrangeTerm.xcodeproj` is generated and
not in version control — a six-target pbxproj is unreviewable in a diff and
hostile in a merge.

```sh
brew install xcodegen
cp Support/Signing.example.xcconfig Signing.local.xcconfig   # then fill in your team
xcodegen generate
```

A signing team is not optional for the extensions. A macOS app group shared with
a sandboxed process must carry the team identifier prefix, and all three
extensions are sandboxed; without one they build but never load.

Two build flags are load-bearing and live in `Scripts/make-app.sh`:
`-skipPackagePluginValidation`, because SwiftTerm ships a build-tool plugin Xcode
will not run unprompted, and the Metal toolchain, which Xcode 26 splits into a
separate download (`xcodebuild -downloadComponent MetalToolchain`) and SwiftTerm
needs for its shader.

### Targets

| Target | Sandboxed | Why |
|---|---|---|
| `StrangeTerm` | no | Spawns `ssh`, reads `~/.ssh` |
| `STConnectionAgent` | no | Owns every connection; embedded as a LaunchAgent |
| `FinderSyncExtension` | yes | Extensions must be; talks to the agent over XPC |
| `ShareExtension` | yes | Same |
| `FileProviderExtension` | yes | Same |

### Integration test

```sh
./Scripts/integration-test.sh
```

Runs an unprivileged `sshd` on a loopback high port — the way OpenSSH's own
regression suite does — so it needs neither Docker nor admin rights. It asserts
the multiplexing properties that matter: several commands and a port forward all
ride ONE connection and cost exactly ONE authentication. It uses a per-run trust
store, so it never touches `~/.ssh/known_hosts`.

### UI snapshots

Screenshotting a live window needs Screen Recording permission, and the
accessibility API reports this app's windows as missing even when they exist. So
the app can draw itself:

```sh
StrangeTerm.app/Contents/MacOS/StrangeTerm --render out.png [--select db-primary]
StrangeTerm.app/Contents/MacOS/StrangeTerm --selftest   # asserts a window opened
```

### Driving the app headlessly

The terminal is an AppKit view on a pty, which neither a snapshot nor a unit
test can exercise, so the app can drive itself:

```sh
export STRANGETERM_SSH_CONFIG=/path/to/test/ssh_config   # overrides ~/.ssh/config
export STRANGETERM_INVENTORY=/tmp/inventory.json         # keeps tests out of ~/Library
StrangeTerm.app/Contents/MacOS/StrangeTerm \
    --autoconnect myhost --dump-terminal /tmp/out.txt
```

`--autoconnect` connects and opens a session, `--autosplit` adds a second pane,
and `--dump-terminal` writes what the terminal is actually displaying. Snapshots
take `--demo-session`, `--demo-split`, and `--demo-palette [--demo-query x]` to
show UI that otherwise only exists after a real connection, `--demo-orchestrator`
and `--demo-orchestrator-idle` show a finished multi-host run and the picker
before one, `--demo-plan` shows a plan awaiting approval, and `--demo-editor` renders the host editor sheet.

`--orchestrate a,b,c --instruction "…"` runs one real multi-host run and prints
what each host reported and the collated answer, with `--approve-commands` to
answer the gates and `--pretend-connected` to mark the hosts connected without a
master — which exercises the orchestration itself (dispatch, the gates, the
collating call) on a machine with no servers to reach, since every command then
fails as "not connected". Several concurrent agent loops feeding one summarising
call is not something a unit test stands in for: what breaks is the interleaving.

`--plan-run a,b,c --goal "…"` asks for a plan across those hosts and prints it;
`--approve-plan` then carries it out and prints what each phase asked, what each
host did, and what was carried between them. The interesting failures in a
planned run are all in the seams — a phase that halts the ones after it, a
captured value that never arrives, a placeholder that would otherwise be sent
literally — and none of them exist until the phases run in sequence.

`--autoforward` toggles a host's first forward and reports whether the local
port actually starts answering.

`--add-host name=hostname` and `--dump-inventory <path>` check that an edit
survives a relaunch, and `--autobrowse` opens a file browser and reports what it
listed — none of which a unit test can observe.

`--ask-assistant <host> --question "…"` runs one real exchange and prints what
came back, with `--connect-first` to gather live metrics and `--dump-context` to
print the block verbatim before it is sent. `STRANGETERM_ASSIST_PROVIDER`,
`STRANGETERM_ASSIST_MODEL` and `STRANGETERM_ASSIST_ENDPOINT` redirect it, which
is how a provider's wire format is checked against a stub without an account. The assistant is a network call
feeding a stream feeding a parser feeding a view; the pieces have unit tests, but
only this says whether the whole path works.

## SFTP

The client speaks the wire protocol (draft-ietf-secsh-filexfer-02) directly over
`ssh -s sftp`, rather than driving the interactive `sftp` CLI and scraping its
output. That is what allows several reads in flight at once, which a Finder mount
will need, and it gives real error codes instead of parsed prose. A session is a
channel on the ControlMaster, so opening one costs no authentication.

### Keyboard

| Shortcut | Action |
|---|---|
| `⌘K` | Command palette |
| `⌘T` | New session on the selected host |
| `⌘D` / `⇧⌘D` | Split right / down |
| `⌘W` | Close pane |
| `⌘1`–`⌘9` | Jump to tab |
| `⌘0` | Host details |
| `⌃⌘X` | Disconnect |
| `⌥⌘B` | Broadcast to every pane in the tab |
| `⌘N` / `⇧⌘N` | New host / new folder |
| `⌘E` | Edit selected host |
| `⇧⌘B` | Browse files over SFTP |
| `⌥⌘A` | Assistant for the selected host |
| `⇧⌥⌘A` | Ask several hosts at once |

## Inventory

Hosts live in the app group container, written atomically and versioned. They
have to: the agent and all three sandboxed extensions read them, and a sandboxed
process cannot reach `~/Library/Application Support` at all. Anything left in
that older location is copied across once.

On first launch, if there is nothing saved, the app imports `~/.ssh/config` and
writes it out immediately — until it does, the extensions see no hosts. Edits
after that are kept in StrangeTerm's own inventory and are **not** written back
to the file (the editor says so for imported hosts).

## Finder mounting: not working yet

The File Provider extension is written and the plumbing around it works:
`NSFileProviderManager.add` succeeds, the mount point appears at
`~/Library/CloudStorage/StrangeTerm-<host>`, and `pluginkit` lists the extension.

**But macOS never launches the extension**, so enumerating the mount times out
and no files appear. The extension process is never spawned, nothing is written
to the system log, and `pluginkit` reports it with a blank status flag — neither
enabled nor disabled. Explicitly enabling it with `pluginkit -e use` changes
nothing.

What has been ruled out: the extension is registered and correctly described
(the `NSExtensionFileProvider*` keys were nested wrongly at first, which is a
different and now-fixed bug), its signature is valid and satisfies its
designated requirement, and the app was run from `/Applications`.

The likely remaining cause is that a File Provider extension needs a
provisioning profile carrying the File Provider capability, which a plain
Development certificate does not grant. That is a signing question rather than a
code one.

Everything else in the path is proven: the agent's file operations, the XPC
contract, and the case-collision handling are all covered by tests, and the
in-app SFTP browser uses the same transport successfully.

## Host key verification

Checked before any connection is opened, rather than left to ssh — ssh can only
accept silently or fail with a wall of text, and neither lets the app show a
fingerprint and ask.

`ssh-keyscan` fetches the offered keys, they are compared against `known_hosts`,
and the outcome is one of:

| Verdict | Behaviour |
|---|---|
| Known | Connect |
| Unknown | Prompt with the fingerprint; accepting appends to `known_hosts` |
| Unknown, strict policy | Refuse |
| **Changed** | **Refuse, always** — never offered as a prompt |
| Revoked | Refuse |

A changed key is what interception looks like, so it is never something the user
can click through; the alert shows both the stored and the offered fingerprint
and points at the file to edit if the server was genuinely rebuilt. Trust is
recorded in OpenSSH's own `known_hosts`, so anything trusted here is trusted by
`ssh` on the command line too.

The fingerprint format matches `ssh-keygen -lf` exactly — a test asserts it
against real generated keys, because a fingerprint that cannot be compared with
one from another source is useless.

Hashed `known_hosts` entries are supported (OpenSSH hashes host names by
default, so a parser that only handled plain names would find nothing in most
real files).

## Themes

Two, chosen from the gear in the rail: **StrangeTerm Dark** (the original) and
**Dracula**, following the official palette the VS Code theme uses rather than an
interpretation of it. The choice is remembered.

Colours live in `ThemePalette`; `Theme` reads whichever is current. Call sites
stay as `Theme.accent` rather than threading a palette through several hundred
references — the cost is that SwiftUI cannot observe a static, so `RootView`
rebuilds each piece of chrome when the palette changes. That keying is applied
per component and deliberately never reaches a terminal pane: recreating one
would restart the ssh session inside it. Terminals recolour themselves in place
instead, including their sixteen ANSI colours.

## The assistant

A third kind of pane, alongside the terminal and the file browser: a
conversation scoped to one host, opened with `⌥⌘A` or from the palette. It
splits beside the session it is about, so the terminal stays visible while the
question is being asked.

Questions carry three things — what the terminal beside the pane is showing, the
host's current metrics from the same probe the dashboard runs, and the name you
gave the host. The probe runs when a question is asked rather than on a timer: a
pane sitting open is not a reason to poll a server.

By default **nothing is ever run**. A suggested command arrives as a block with
a button that *types* it into the terminal and stops — no newline, no execution.
The person reads it and presses Return. That is the same principle as refusing to
offer a changed host key as a prompt: the irreversible step stays a deliberate
one, and a model's suggestion has no claim to more trust than a server's key
does. Fenced blocks tagged as shell are the only ones that get the button;
anything else renders as code with no way to run it.

Turning on **Run commands** in the pane header changes that, and the gate below
is what makes it something other than reckless.

![The assistant beside a terminal, showing the commands it ran](Docs/screenshots/assistant.png)

### Running commands

With the toggle on, the model gets one tool — run a command, read its output —
and the pane loops: it asks for something, the app runs it or stops to ask, the
result goes back, and it decides what to do next. Each step is a row in the
transcript showing the command, why it wanted it, and its output when expanded.
That is deliberately the same place as the answer: what it actually ran should
never require looking somewhere else.

**The gate is the feature.** `CommandPolicy` decides whether a command may run
unattended, and it is an *allowlist*: a command runs only if it is recognised
and every one of its arguments is recognised as harmless. Everything else stops
and waits for a person — not because it is known to be dangerous, but because it
is not known to be safe. The denylist beside it never grants anything; it only
makes a warning louder, so being incomplete costs nothing.

That direction is the whole design. A read-only command wrongly stopped costs a
click; a writing command wrongly allowed costs a server.

What it stops that a shallower check would not:

| | |
|---|---|
| `ps aux \| tee /tmp/x` | every stage of a pipeline is judged, not just the first |
| `df -h; rm -rf /tmp/x` | chaining does not launder a command |
| `ls $(reboot)` | substitutions are refused before parsing, not after |
| `/bin/rm -rf /` | a path is still the command it ends with |
| `bash -c 'df -h'` | an interpreter is never read-only, whatever it is handed |
| `sudo df -h` | elevation is a decision even when the command is harmless |
| `find . -delete`, `sed -i` | flags that turn a reader into a writer |
| `tail -f`, `top`, `watch` | nothing can interrupt a command once ssh has it |
| `LANG=C rm -rf /tmp` | leading assignments are stepped over, not mistaken for the command |

Three other bounds: **twelve commands** per question, after which the model is
told to summarise rather than stopped mid-investigation; **sixty seconds** per
command, after which the loop moves on (the command is not killed — the process
runner cannot be interrupted, so what this bounds is how long the pane waits);
and command output is **redacted and truncated** like everything else before it
goes to a provider.

Declining is not a dead end. The model is told plainly that the user refused,
which is what stops it from trying the same thing a different way.

### Across several hosts

**Session → Ask Several Hosts…** (`⇧⌥⌘A`) opens the orchestrator: one instruction,
carried out on every host you tick, then collated into one answer.

It runs nothing itself. Each target gets its own assistant — the same per-host
agent the pane above uses, with the same gate, the same twelve-command budget,
the same redaction and the same timeout — and the orchestrator makes one further
call to collate what they reported. Delegating rather than adding a host argument
to `run_command` is the whole design: nothing about running a command on a machine
gets a second implementation, and each host's investigation stays in its own
transcript, so what the summariser reads about `web-01` is `web-01`'s conclusion
rather than ten machines' raw output interleaved in one context.

What that costs is worth stating plainly, because it multiplies:

- **Three hosts run at once.** Not one, or a run over a rack takes as long as the
  sum of its hosts. Not all of them either — every host in flight is another gate
  that can stop for a person, and a queue of eight approvals is a queue nobody
  reads, which is how someone ends up approving all of them without looking.
- **Sixty commands for the whole run**, on top of each host's own twelve. Hosts
  not reached are reported as skipped rather than dropped.
- **The gate is per host and says which one.** Approving `systemctl restart nginx`
  means nothing until you know whose nginx, so every waiting command is captioned
  with its host.
- **Only connected hosts are asked.** Connecting can raise a host-key decision,
  and a fan-out that stops on a dialog for every host would be worse than one that
  says plainly which hosts it left out. Disconnected targets stay tickable and are
  reported as not asked.

Every host is listed with its own status and findings above the collated answer,
and a host that failed or was skipped says so in that list. A summary that reads
as though it covered ten hosts when three were unreachable is the failure the
layout exists to prevent, and the summariser is told the same thing: it has no
server access, and it must not make claims about hosts that did not report.

![One question across four hosts, with each host's finding above the collated answer](Docs/screenshots/orchestrator.png)

Commands in the collated answer are shown but never staged. They apply to hosts,
plural — there is no one terminal they belong in, and picking one would be
picking the wrong one.

### Work that has an order

A fan-out cannot express building a cluster. That needs three things it does not
have: an order (the control plane before the nodes that join it), different work
per host, and a value carried from one machine to another — the join command
`kubeadm init` prints. **Plan**, the orchestrator's second mode, is those three
things.

You give a goal; the model writes a plan and runs none of it. Each phase names
its hosts, says what they should do, and may declare one value it yields:

```json
{"phases": [
  {"name": "Prepare every node", "hosts": ["web-01", "web-02"], "task": "…"},
  {"name": "Initialise the control plane", "hosts": ["web-01"], "task": "…",
   "capture": "join_command"},
  {"name": "Join the workers", "hosts": ["web-02"], "task": "run {{join_command}}"}
]}
```

The plan appears in the pane in full — every phase, its hosts, and the words each
host will be given. Phases can be switched off. Nothing runs until Run the plan.
A plan summarised into "3 phases, 4 hosts" would be a plan nobody could review,
and review is the only thing standing between a model and a fleet.

![A four-phase cluster plan awaiting approval](Docs/screenshots/plan.png)

A plan that would not be safe to run is refused rather than shown, and the
refusal says which phase and why: a host nobody selected, a value captured on
several hosts at once so there would be no single value to carry, a `{{name}}`
no earlier phase produces, more than twelve phases.

While it runs:

- **Phases go in order; hosts within a phase go three at a time.** Each phase's
  results stay on screen as the next one starts, because a plan is read downwards.
- **A captured value is shown verbatim** where it was produced. It is the one
  thing in the run the user neither wrote nor read before a machine acted on it.
- **The run stops if a phase gets nowhere.** Joining nodes to a control plane
  that never came up is worse than stopping, so a phase where no host completed —
  or one that did not produce the value later phases need — ends the run and says
  so. What was already done stays on screen.
- **A phase is skipped rather than run with a placeholder still in it.** Sending
  `{{join_command}}` to a server is the kind of mistake that is obvious afterwards
  and invisible beforehand.
- **Commands get ten minutes rather than one.** `apt install` and `kubeadm init`
  legitimately take that long, and a loop that gave up after a minute would carry
  on reading the output of a command it had stopped waiting for.

The gate is unchanged. Every writing command still stops and asks, named with its
host — which for a cluster build is a great many approvals, and deliberately so.

### Connected tools

An assistant that can only reach the machine in front of it is limited in a
particular way: the answer to "is this our incident or the upstream's?" is
usually in a dashboard, a tracker or somebody's runbook, none of which are on
the server. **Settings → Connected tools** attaches
[MCP](https://modelcontextprotocol.io) servers, whose tools are offered to the
model alongside `run_command`.

Two transports:

| | |
|---|---|
| **Local process** | Launched as a child, spoken to over its stdin and stdout. Runs on this Mac, as you, with your files. |
| **HTTP** | Streamable HTTP against a URL. Whatever a tool is given goes over the network to that host. |

![Connected tools in Settings](Docs/screenshots/settings.png)

A bare command is looked up in the usual places — Homebrew, `/usr/local/bin`,
`~/.local/bin` — because an app launched from Finder inherits launchd's `PATH`,
not your shell's, so `npx` and `uvx` are otherwise invisible. Resolving it by
running a login shell was the alternative and is worse: executing someone's
dotfiles to start a tool server is a larger thing to do than searching four
directories. HTTP tokens go to the login keychain, in a service of their own,
and are sent as `Authorization: Bearer`; the settings file has nowhere to put
one.

**Signing in.** A hosted server usually wants a person rather than a key, and
says so with a 401 carrying a pointer to its own metadata. StrangeTerm follows
that chain — protected-resource metadata, then the authorization server's —
registers itself as a client if the server allows it, and opens a browser.
Authorization code with PKCE, as OAuth 2.1 requires; a server that does not
advertise S256 is refused rather than trusted to be checking. Tokens go to the
login keychain and are refreshed silently where the server issues a refresh
token, and where it does not, signing in again is a button rather than a
mystery.

The redirect is `http://127.0.0.1` on a port bound *before* the browser opens,
not a custom URL scheme. A scheme like `strangeterm://` can be claimed by any
app on the machine, which would hand the authorization code to whoever
registered it last; a loopback port this process already holds cannot be taken
that way, and RFC 8252 says as much. Several ports are registered so a later
sign-in can use whichever is free without registering a second client.

A renewal never opens a browser. A request failing mid-turn is the worst
possible moment to seize the screen, so the transport renews silently or gives
up, and the visible login is something you start from Settings. A pasted bearer
token still wins over all of this: someone who supplied one has said how their
server authenticates, and starting a discovery underneath them would be
second-guessing it.

Tools are namespaced by server — `grafana__query_range` — because two servers
may each have a `search`, and a provider handed a duplicate name rejects the
whole request rather than the tool. Names are sanitised and length-capped for
the same reason.

**The gate is the same gate.** Every call from a connected tool stops and waits
for a person, showing the server, the tool, where the call goes and the exact
arguments — pretty-printed, in the bar itself, not behind a disclosure, because
a gate whose substance is one click away is a gate people approve without
reading.

There is no `CommandPolicy` equivalent here, and there cannot be. That
classifier works because a shell command is a string it can parse; `create_incident`
with a JSON body is an opaque name written by the same server that would carry
out the call. Servers may annotate a tool `readOnlyHint` — that is a claim by
the party being trusted, so it is shown and never acted on, exactly the role the
command denylist plays beside the allowlist. What it does affect: a tool the
server calls destructive cannot be given a standing pass at all.

So the pass is granted per tool, by you, at the gate — **Always allow** — and
that is the only way onto the list. Settings shows what each server has been
granted and lets you take it back; removing a server takes its grants with it.

Two switches, and their defaults differ on purpose:

- **Assistant panes: on.** Connecting a server is already the deliberate act,
  and every call still asks. Making you turn it on twice would be ceremony
  rather than safety.
- **Orchestrated runs: off.** A fan-out multiplies everything. Every host in a
  run gets the *same* tools pointed at the same place, so one instruction can
  become one write per host, and the gate that would have caught it is eight
  approvals deep in a queue nobody reads properly by the fourth. When it is on,
  each host's worker is told plainly that these tools are not part of its
  machine and that writing is not its job — findings go in the verdict, and the
  user decides once, with the whole picture.

Results come back redacted and truncated like terminal output, and the model is
told they are data from a third party rather than instructions to it. A tool
call spends the same twelve-step budget a command does.

```sh
StrangeTerm.app/Contents/MacOS/StrangeTerm \
  --mcp-add "Files=npx -y @modelcontextprotocol/server-filesystem ~/Projects"
StrangeTerm.app/Contents/MacOS/StrangeTerm --mcp \
  --mcp-call files__read_file --arguments '{"path":"~/Projects/README.md"}'
```

`--mcp-discover <url>` walks the OAuth chain and prints the endpoints, scopes
and whether PKCE is offered — the half of a login that can be checked without a
person, which is the half worth checking, because a server with wrong metadata
fails identically to one whose login was refused. `--mcp` connects what is
configured and reports the handshake, the tool list and the namespacing; `--mcp-add` configures one in memory only, so a check can run
against a server this machine has never been told about. `--mcp-call` bypasses
the gate, which is why it is a flag and nothing the UI can reach. Both
transports have been driven end to end this way against real servers — the
handshake, an interleaved notification, a session id, a tool call, a tool
reporting its own failure, a server that dies during startup, and a command that
is not on the path.

What is deliberately not implemented: resources, prompts, and the server→client
requests (sampling, elicitation, roots). The capabilities we announce are empty,
so a well-behaved server never asks. Sampling in particular would be a server
spending your API credit on a prompt you never saw.

### What leaves the machine

Only the alias you chose, `uname`, the metrics, and the terminal tail. There is
no hostname, address, username or key material in the request, and the type that
becomes one has nowhere to put them — the boundary is structural rather than a
habit of the call site, and a test asserts it.

Terminal output is scrubbed first. A real session contains `cat .env`, an
exported token, a `docker login`, and the person asking about a stack trace is
not thinking about any of that, so the redactor runs on every request rather
than being something to remember: private key blocks, secret-shaped assignments,
bearer headers, credentials in connection strings, JWTs and the recognisable
provider key formats. It keeps the *name* in an assignment and drops the value —
"which variable" is usually the question and is not itself the secret. The count
of what it removed is shown next to the disclosure, because that is the only
evidence it ran.

Both context sources are switches in Settings, and every question shows exactly
what it will carry before you ask it. That preview is the rendered context
itself, not a description of it, so it cannot drift from the request.

A connected tool is a second destination, and a different one: the request goes
to the provider, but the tool call's *arguments* go to whichever server owns the
tool, and its answer comes back through the provider on the next turn. That is
why the approval bar names the destination rather than only the tool, and why
each server's row in Settings says where it is. A local server is not a network
destination at all, and saying so is as much a part of the choice as naming the
host an HTTP one reaches.

### Providers

Two, chosen in Settings: **Claude** (Anthropic) and **DeepSeek**. One at a time —
a pane is a conversation, and a conversation whose respondent changes halfway
through is not one. The pane header names the provider and model answering it,
because which backend is in use decides where this conversation's terminal
output is being sent, and that should be readable without opening Settings.

Everything above the wire is provider-independent — the transcript, the
redaction, the context block, staging a command into a terminal — and stays that
way as long as `AssistBackend` is the only seam. What differs is per provider:

| | Claude | DeepSeek |
|---|---|---|
| API | Messages | chat completions (OpenAI-compatible) |
| Default model | `claude-opus-5` | `deepseek-v4-pro` |
| System prompt | top-level field | first message |
| Reasoning | `thinking` blocks, `display: summarized` | `reasoning_content` on the delta |
| Stream ends | `message_stop` | `data: [DONE]` |
| Refusal | `stop_reason: refusal` | `finish_reason: content_filter` |
| Data goes to | Anthropic (United States) | DeepSeek (China) |

Model names are a free-text field with the known ones offered as a menu: they
change faster than this app ships, and a model released after a build should not
need a new one.

Two DeepSeek details are load-bearing and were taken from a working client
rather than from the documentation, which was unreachable. V4 models **require**
a `thinking` parameter and earlier ones reject it, so it is sent only for
`deepseek-v*` names — inferred, and overridable, because the day a `v5` arrives
that guess is wrong. And `max_tokens` is omitted by default: the documented
ceiling has moved with every model generation and a value above it is a 400,
while sending none lets the server apply its own.

Adaptive thinking with summarised display on Claude, so a pane that is reasoning
shows that it is rather than looking frozen. Server-side refusal fallback is on
there too, because this asks about ports, processes and firewalls all day and a
hard stop on a legitimate operational question would be worse than an answer
from another model.

Both are raw HTTP over `URLSession` rather than an SDK — there is no official
Anthropic SDK for Swift, DeepSeek's API is OpenAI-shaped, and one endpoint each
is a smaller thing to own than a dependency in the path of the user's API key.

### Keys

In the login keychain under a service of their own, one account per provider, so
configuring a second does not overwrite the first. Separate from connection
credentials: an API key is not a server secret and has no business in the same
bucket as passphrases. `ANTHROPIC_API_KEY` and `DEEPSEEK_API_KEY` override the
stored ones, which is what makes a development build usable without touching the
real keychain.

Note that a Claude subscription is not API access — they are separate products,
and the assistant needs the latter.

**Not yet exercised against either live API.** Every piece has tests — both
stream decoders against recorded streams, the redactor, the fence parser, both
request bodies — the context assembly is checked against a real sshd, and the
whole DeepSeek path is driven end to end against a stub endpoint that records
what was sent. What has not run is a request to a real provider, because that
needs an account. `--ask-assistant` is the command that would prove it.

## The app icon

`Assets/ssh_app_logo.svg` is the source. `Scripts/make-icon.sh` rebuilds
`Assets/AppIcon.icns` from it, rendering each size from the vector rather than
downscaling one bitmap — the glow and the rounded corners go muddy otherwise at
16pt. Needs `brew install librsvg`.

macOS caches app icons aggressively, so after changing it you may need
`touch /Applications/StrangeTerm.app` before Finder notices.

## Installing on your own Mac

```sh
./Scripts/install.sh              # build, install to /Applications, register the agent
./Scripts/install.sh --uninstall  # undo all of it
```

No Developer ID certificate and no notarisation are needed for personal use.
Those exist so *other* machines will run the app: Gatekeeper only inspects
software carrying a download quarantine flag, which a locally built app never
has. An Apple Development certificate — the kind Xcode creates from an ordinary
Apple ID — is enough, and everything works with it: the agent registers with
launchd, the Finder and Share extensions load, and the keychain and host-key
paths behave normally.

The one caveat is that Apple Development certificates expire after about a year.
When one does, run the installer again with a renewed certificate.

Uninstalling removes the app, unregisters the agent and drops any Finder mounts.
It deliberately leaves your data alone and prints where it is: hosts in the group
container, passphrases in the login keychain, and trusted host keys in
`~/.ssh/known_hosts`, which is shared with `ssh` itself.

## Packaging

```sh
./Scripts/package.sh --skip-notarize   # builds a local, unnotarised disk image
./Scripts/package.sh                   # full release: sign, notarise, staple
```

Distribution is Developer ID plus notarisation rather than the App Store,
because the app and its agent are deliberately unsandboxed and the store does
not permit that.

The full path is **not yet exercised**. It needs two things that are account
operations rather than code:

1. A **Developer ID Application** certificate. The Apple Development certificate
   used day to day produces builds macOS will refuse on any other machine.
2. Notarisation credentials, stored once:

   ```sh
   xcrun notarytool store-credentials StrangeTerm \
       --apple-id you@example.com --team-id TEAMID --password APP_SPECIFIC_PASSWORD
   ```

The script checks for both up front and explains what is missing rather than
failing halfway through a build. Everything before notarisation *has* been run:
the archive builds, every nested component's signature is verified individually
(notarisation rejects the whole submission for one unsigned helper), and the
resulting image mounts and launches. Gatekeeper refuses it, correctly, as
unnotarised.

## Dashboards

A connected host shows uptime, load with a sparkline, memory and disk meters,
and Docker containers. Collected by one probe command with delimited sections
rather than several round trips — each exec is a channel setup, and refreshing a
dozen hosts every few seconds would otherwise be a lot of needless work. Polling
runs only while the panel is on screen.

Every metric is optional throughout. A probe runs on whatever the server happens
to be — Linux, macOS, a container with no `docker` — and reporting a confident
zero for something that could not be measured would be worse than reporting
nothing. Both platforms' formats are parsed and tested against real captured
output.

```sh
stctl probe user@host
```

## Credentials

A credential describes a key or password once; any number of hosts then point at
it. Because a connection's credential is an inherited setting, setting one on a
folder covers every host beneath it, and a single host can still override. A
host's own username always wins over the credential's — a shared credential is a
default for the hosts that use it, not an override of the ones that were
explicit.

Three methods: **ssh-agent** (preferred, since the key never enters StrangeTerm),
a **key file** with its passphrase, or a **password**.

Secrets are stored in the login keychain
(`kSecAttrAccessibleWhenUnlockedThisDeviceOnly` — never synced, unreadable while
locked). Only a reference is kept in the inventory, which is an ordinary JSON
file in a shared container. Private keys themselves are never copied into app
storage: material that never enters our address space cannot be leaked by us, so
an agent or an on-disk identity file is always preferred.

`ssh` asks for secrets by running `$SSH_ASKPASS` and reading its stdout, so the
`st-askpass` helper answers those prompts. The secret reaches it over a FIFO
rather than a file or an environment variable — it stays in kernel buffers
between two processes the user already owns, and nothing but an empty pipe node
is written to a filesystem. The channel serves repeatedly for a minute and then
tears itself down, because ssh re-asks after a rejected passphrase and a
one-shot channel would turn a wrong stored secret into a hang.

```sh
stctl keychain set myhost          # reads the secret from stdin, never argv
stctl --keychain-account myhost connect user@host
```

**Known limitation.** macOS binds a keychain item to the binary that created it,
so a secret saved by the app is not readable by the connection agent. Fixing
that needs a `keychain-access-groups` entitlement, which is a capability
requiring a provisioning profile — the same thing blocking the File Provider.
Until then everything the app does itself works normally; only agent-driven
uploads from the Finder extensions cannot use a stored passphrase.

## The connection agent

The extensions are sandboxed and so can neither spawn `ssh` nor read `~/.ssh`.
Everything remote they offer is an XPC request to `STConnectionAgent`, which is
not sandboxed and owns every connection.

Registering it installs a LaunchAgent, so the app asks rather than doing it
silently on first launch. Two things are worth knowing:

- `SMAppService` reports `.notFound` before a service has ever been registered.
  That does **not** mean the agent is missing from the bundle.
- Registration only works from a stable location. Run the app from
  `/Applications`, not from a build directory.

The plan calls for SQLite once the agent and the sandboxed extensions all need
concurrent access. Today only the app touches it, so a single atomically-written
JSON document is the simpler correct answer; `InventoryPersisting` is the seam
for swapping it.

`--render` uses `ImageRenderer`, which cannot lay out `ScrollView` content or draw
an AppKit-backed `TextField`; views branch on the `isSnapshot` environment value to
substitute equivalents. Run the app normally to see the real, scrolling UI.

## Status

- [x] **M0** — repository, core package, test harness
- [x] **M1** — connection store, ssh_config import, Keychain, host-key verification
- [x] **M2** — `STConnectionAgent` + ControlMaster supervisor + XPC contract
- [x] **M3** — SwiftTerm terminal over the shared master
- [x] **M4** — UI shell: sidebar, tabs, splits, inspector, palette
- [x] **M5** — SFTP client, in-app browser, edit-in-place
- [x] **M6** — tunnel manager
- [x] **M7** — FinderSync + Share extensions
- [~] **M8** — File Provider extension (see below)
- [~] **M9** — dashboards, broadcast, snippets done; packaging written, awaiting Developer ID
- [~] **M10** — assistant pane, Claude and DeepSeek; awaiting a live API key
- [~] **M11** — orchestrator across hosts: fan-out and planned runs, both driven
  end to end against a stub endpoint, awaiting a live API key and a real fleet
- [~] **M12** — MCP client: both transports driven end to end against real
  servers, tools offered in both assistant and orchestrator modes, each call
  gated. OAuth 2.1 with PKCE and dynamic registration for hosted servers;
  discovery and the loopback redirect are both exercised against a real server,
  the browser half needs a person. What has not run is the whole path with a
  live provider deciding to call a tool, which needs the same API key M10 does
