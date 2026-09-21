# StrangeSharpTerm

A cross-platform desktop app for managing remote servers over SSH — connection
inventory, a first-class terminal, SFTP, tunnels, and an assistant that can read
the session beside it. macOS and Windows.

This is a ground-up reimplementation in C#/.NET and Avalonia of
[StrangeTerm](https://github.com/hectorgabriel/StrangeTerm), a macOS-only
Swift/SwiftUI app. No Swift code carries over; the design does.

## Status

**M9 — the workspace.** A folder open, with a tree down one side and the files
you are editing beside it. On a host it is a pane that splits next to the shell
on the same machine (⇧⌘E); on **this** machine it is the **Files** side of the
left panel, with the editor opening where the sessions are. ⌘S saves either, and
a file goes back in the line endings it already had — which is what stops a
Windows machine rewriting every line of a Linux host's `nginx.conf` to change
one. A file on your Mac can be sent straight to the folder a host has open.

The folder is also the permission. The same root is what the assistant may work
in: it gets `list_files` and `read_file` as soon as you open one, `write_file`
only where **Edit files** is on, and nothing at all outside the root — that is
refused rather than asked, because you answered it when you chose the folder.
Your own machine's folder has its own three (`read_local_file` and the rest),
separate names rather than an argument saying which computer, because which one
a write lands on is the thing being approved and must not be a field the gate has
to trust. Nothing here is open until you choose a folder: defaulting to `~` would
be handing over a home directory nobody offered.
Every write stops at the same gate a command does, showing **the lines that
change**, because "it would like to write nginx.conf" is not a question anybody
can answer. Files are redacted like everything else before they are sent, and a
write that would put `[redacted]` back into a real file is refused before anyone
is asked. See `docs/adr/0009`.

```sh
# the same folder, without a window: read inside it, write inside it, and be
# refused outside it
dotnet run --project src/stctl -- workspace user@host \
    --root '~/srv/app' --cat conf/nginx.conf
```

Both panes take a few commands that are not questions: **`/clear`** forgets the
conversation — what is on screen *and* what the provider has been told —
**`/mcp`** reports the attached tool servers and what each one offers, and
**`/help`** lists them. They are answered in the app and never sent, because a
model asked about the app it is running inside answers plausibly and checks
nothing. The fan-out gets this machine's folder too, so a run can read a runbook
here and write down what it found across eight servers; it deliberately gets no
access to the hosts' files, because one approval that rewrites a file on eight
machines is not a thing worth making easy.

**Root, without a terminal.** The assistant's commands run on an exec channel —
no terminal, as the account the host was logged into — so `sudo` cannot prompt
there, and a `sudo su` in the terminal pane is a different channel. For a host
that signs in with a password, the host editor offers *Give sudo this host's
password*: the password then goes to `sudo -S` on the channel's input, never on
a command line where `ps` on the server could show it, and never to the model or
the transcript. Only a single command starting with `sudo` gets it, and not at
all where the host needs no password. See `docs/adr/0010`.

**M8 — packaging.** `build/package.sh` builds `StrangeSharpTerm.app` and a disk
image around it; `build/install.sh` puts it in `/Applications` for your own Mac
and needs no certificate at all. `build/package.ps1` does the Windows side.
Both run in CI on every push, so a publish that cannot load its own runtime fails
there rather than after notarisation.

```sh
./build/install.sh          # build, sign ad-hoc, install to /Applications
./build/package.sh          # a disk image, for this machine
./build/package.sh --notarize --sign-with "Developer ID Application: …"
```

Signing for *other* machines needs a paid Apple Developer account — a free
Personal Team cannot issue a Developer ID certificate and cannot notarise. That
half is written and gated, and has never run. `docs/adr/0008` records what is
proven, what is not, and the three things that only showed up by running it: every
file under `Contents/MacOS` is code to codesign, a valid signature says nothing
about the app starting, and a timestamp is a network round trip per signature.

**M7 — connected tools.** An assistant that can only reach the machine in front
of it cannot answer "is this ours or the upstream's?" — that is usually in a
dashboard, a tracker or somebody's runbook. **Settings → Connected tools**
attaches [MCP](https://modelcontextprotocol.io) servers over either transport: a
local process spoken to over its stdin and stdout, or streamable HTTP against a
URL. A hosted server that wants a sign-in gets one — authorization code with
PKCE, to a loopback port bound before the browser opens, never a custom URL
scheme that any application on the machine could claim.

Tools are namespaced by server (`grafana__query_range`) because two servers may
each have a `search`, and a provider handed a duplicate name rejects the whole
request. **The gate is the same gate**, with one difference: there is no
`CommandPolicy` here and there cannot be, because `create_incident` with a JSON
body is an opaque name written by the same server that would carry out the call.
So every call stops, showing the server, the tool, where it goes and the exact
arguments — in the bar itself, because a gate whose substance is one click away
is a gate people approve without reading. **Always allow**, granted per tool by a
person, is the only way onto the list; a tool the server calls destructive cannot
get one at all. See `docs/adr/0007`.

```sh
# both transports, without a window
dotnet run --project src/stctl -- mcp \
    --server "Runbooks=npx -y @modelcontextprotocol/server-filesystem ~/runbooks" \
    --call runbooks__read_text_file --arguments '{"path":"~/runbooks/db.md"}'
```

**M6 — the assistant.** A conversation scoped to one host, opened with ⌥⌘A and
split beside the session it is about. Questions carry the name you gave the
host, what `uname` reported, the metrics the dashboard's own probe collects when
you ask, and the tail of the terminal beside the pane — scrubbed of secrets, with
the count shown, and previewed in full before anything is sent.

By default nothing is ever run: a suggested command arrives with a button that
*types* it into the terminal and stops. Turning on **Run commands** gives the
model one tool, and `CommandPolicy` is what makes that something other than
reckless — an allowlist where every stage of a pipeline is judged, so `ps aux |
tee /tmp/x` stops and `df -h` does not. Twelve commands a question, sixty seconds
each.

⇧⌥⌘A opens the orchestrator: one instruction across every host you tick, three
at a time, each with its own agent and its own gate, collated into one answer
that is told which hosts never reported. Its second mode writes a **plan** —
phases in order, different work per host, one value carried between them — and
runs none of it until you have read it. A phase names the **commands** each of
its hosts will run and why it is those hosts, because "install Kubernetes" is
not something anyone can check and `kubeadm init --pod-network-cidr=…` is. They
are still only asked for: every one meets `CommandPolicy` and the gate when the
phase runs. Where a provider offers its reasoning, that is on screen too —
folded once there is a plan to read, because which of three identical servers
gets the single-node install is the decision, and the plan alone shows only
which one won.

Both providers, Claude and DeepSeek, are reached through one seam; see
`docs/adr/0006` for why one is an SDK and the other is not.

```sh
# one real exchange against a real host and a real provider
dotnet run --project src/stctl -- ask user@host --question "why is the disk full?"
```

**M5 — feature panes.** SFTP browser, tunnels, the dashboard, the credential and
snippet libraries, and the settings sheet — all over one authenticated session
per host.

**M4 — the app shell.** A window with a sidebar, tabs, split panes, host
detail, the host and folder editors, a menu bar and a command palette, in either
of the Swift app's two themes. `docs/adr/0004` says where the colours came from
— measured out of the Swift app's own screenshots — and `docs/adr/0005` settles
the three questions the plan held open for this milestone: what the hidden title
bar means on Windows, where the menu lives, and how ⌘ becomes Ctrl.

**M3 — terminal.** There is a working terminal. An SSH shell channel drives an
XTerm.NET engine, rendered by an Avalonia control; the server owns the pty, so
there is no ConPTY and no openpty anywhere in the code, which is why the same
terminal works on both platforms. Resizing the window reaches the remote pty,
themes carry the Swift app's colours, broadcast types into several panes at
once, and scrollback is readable — which is what the assistant reads.

Run one against a host:

```sh
dotnet run --project src/StrangeSharpTerm.App -- --connect user@host --theme Dracula
```

M2 before it brought connections, host-key trust shared with `ssh`, secrets in
each platform's own store, and an integration gate that runs against a real sshd
on macOS and Windows in CI. M1 ported the model and store, keeping the inventory
file byte-identical to the Swift app's. See `docs/migration-plan.md` for the
milestone list, the platform strategy, and the working rules.

## Why the rewrite

Three reasons, in order:

1. **Windows.** The Swift app drives the OpenSSH binary with `ControlMaster`
   multiplexing. Win32-OpenSSH has no `ControlMaster`, so that design could not
   have reached Windows whatever the UI toolkit.
2. **The toolchain.** Six Xcode targets generated by XcodeGen, three sandboxed
   app extensions, app groups, a team-prefixed Mach service, and five
   entitlements files.
3. **Two features were blocked on Apple capabilities**, not on code. The File
   Provider extension never launched, and the connection agent could not read a
   keychain item the app had created. Both needed entitlements that require a
   provisioning profile. Dropping the extensions and moving SSH in-process
   removes both problems rather than solving them.

## What changed architecturally

The Swift app ran an unsandboxed LaunchAgent (`STConnectionAgent`) that owned
every SSH connection, because sandboxed Finder extensions can neither spawn
`ssh` nor read `~/.ssh`. With the extensions dropped and SSH running in-process
through SSH.NET, that agent has nothing left to own. Gone with it: the XPC
contract, `SMAppService` registration, the Mach service, app groups, the
`st-askpass` helper and its FIFO.

`ControlMaster`'s one useful property — authenticate once per host, multiplex
everything over it — is what SSH.NET gives natively: one `SshClient` per host,
with shell channels, SFTP, port forwards, and command execution over it.

## Deliberately not carried over

- **Finder integration** — mounting, sidebar badges, the Share menu item. These
  are macOS app extensions and cannot be written in C#.
- **Tunnels outliving the app.** The LaunchAgent used to keep port forwards up
  after the UI quit. With no agent, quitting drops them. A headless daemon is a
  later option if it turns out to be missed.

## Layout

```
src/StrangeSharpTerm.Model/       value types: inventory, settings, forwards
src/StrangeSharpTerm.Store/       inventory JSON, ssh_config parser and importer
src/StrangeSharpTerm.Security/    known_hosts, credential storage
src/StrangeSharpTerm.Transport/   SSH.NET connection pool, SFTP, probes,
                                  the rooted workspace and its path rules,
                                  and this machine's files behind the same seam
src/StrangeSharpTerm.Terminal/    XTerm.NET engine bound to an SSH channel
src/StrangeSharpTerm.Assist/      providers, the agent loop, the command and
                                  file gates, redaction, diffs, orchestration
                                  and run plans
src/StrangeSharpTerm.Mcp/         connected tool servers: transports, the
                                  namespacing, the grant rules, OAuth storage
src/StrangeSharpTerm.App/         Avalonia views and view models
src/stctl/                        headless driver, used by the integration tests
```

## Development

```sh
./build/build.sh            # build everything
./build/test.sh             # run every test project
```

Package versions are pinned centrally in `Directory.Packages.props`. Bump them
on purpose.

## Reference material

`docs/reference/` holds the Swift project's README, screenshots, and its
integration and packaging scripts. The README in particular is the real
specification — it records *why* each load-bearing decision was made, and those
reasons outlive the language. Read it before changing behaviour that looks
arbitrary.
