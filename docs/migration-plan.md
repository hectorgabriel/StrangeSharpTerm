# Migrating StrangeTerm to Avalonia UI / .NET

## Context

StrangeTerm today is ~29,300 lines of first-party Swift across six Xcode targets: a
SwiftUI app (`App/`, 13,181 LOC), a UI-free core (`Packages/StrangeTermCore`, 9,992 LOC
+ 5,179 LOC of tests), an unsandboxed LaunchAgent, and three macOS app extensions.
It is macOS-only and, per `README.md` "Status", two of its unfinished milestones are
blocked on the same thing: Apple capability entitlements that need a provisioning
profile. M8 (File Provider) never launches its extension; the Credentials limitation
(the agent cannot read a keychain item the app created) needs `keychain-access-groups`.

Three goals drive this migration, all confirmed: **ship on Windows as well as macOS**,
**work in C#/.NET instead of Swift**, and **get out from under Xcode, XcodeGen,
app groups, and extension entitlements**. Linux is explicitly not a target.

Three decisions are settled and they cascade hard:

1. **The three macOS extensions are dropped.** Finder Sync, File Provider, and Share
   cannot be written in C#. Dropping them removes the only reason the sandboxed/
   unsandboxed split exists.
2. **SSH moves to a managed library (SSH.NET)** instead of spawning `/usr/bin/ssh`
   with ControlMaster. Win32-OpenSSH has no ControlMaster, so on a Windows target the
   current design could not survive anyway.
3. Together, 1 and 2 **delete the LaunchAgent and the entire XPC layer**. With SSH
   in-process and no sandboxed peers, there is nothing left for `STConnectionAgent` to
   own. `SMAppService`, Mach services, app groups, code-signing requirements,
   `st-askpass` and its FIFO, and `STProtocol` all go away — and with them both
   entitlement blockers, for free.

The intended outcome: one self-contained desktop app per platform, no helper processes,
no Apple capabilities beyond an ordinary Developer ID signature.

## Repository

This repo, **StrangeSharpTerm**, is new and separate from the Swift one. Not a
single Swift file survives the move, so this is a reimplementation rather than a
migration in place; sharing a tree would have meant two toolchains fighting over
one `.gitignore` and would have broken the Swift app's buildability exactly when
it is most useful — as the running reference to check parity against.

The original repo keeps its name and stays alive until parity, then is archived.
(An earlier draft proposed naming this repo `strangeterm` and renaming the old
one to `strangeterm-swift`. That collides: both APFS and GitHub treat repository
names case-insensitively, so `strangeterm` and `StrangeTerm` cannot coexist.
Picking a distinct name avoids the rename entirely.)

`docs/reference/` carries the Swift README, screenshots, and the integration and
packaging scripts across. That README is the actual specification.

## Target stack

| Concern | Choice |
|---|---|
| UI | **Avalonia 12** on **.NET 10** (`net10.0`), `osx-arm64`, `osx-x64`, `win-x64`, `win-arm64` |
| SSH / SFTP / tunnels | **SSH.NET** 2026.0.0 (package `SSH.NET`, namespace `Renci.SshNet`) |
| ssh-agent auth | **SshNet.Agent** 2026.0.0 (OpenSSH agent + Pageant) — proven on macOS, Windows still open, see Risks |
| Terminal emulation | **XTerm.NET** (MIT, headless VT100/ANSI engine, byte-fed) |
| Terminal rendering | **Iciclecreek.Avalonia.Terminal** if its `AttachConnection(IPtyConnection)` accepts a shim; else own Avalonia control over XTerm.NET |
| Secrets | **Devlooped.CredentialManager** (Git Credential Manager's store: macOS Keychain, Windows Credential Manager) |
| MCP client | **ModelContextProtocol.Core** 2.2.0 (official C# SDK) |
| LLM HTTP | Raw `HttpClient` — keep the current no-SDK stance |
| MVVM | `CommunityToolkit.Mvvm` source generators |
| Tests | xUnit v3 + Shouldly on Microsoft.Testing.Platform. Not FluentAssertions: it moved to a paid licence at v8. |
| CI | GitHub Actions — `macos-latest` + `windows-latest` on every push and PR. See "Working rules". |

Exact versions live in `Directory.Packages.props`. That file, not this table, is the
record of what was tested.

## Solution layout

```
StrangeSharpTerm.slnx
  src/StrangeSharpTerm.Model/        ← STModel      (pure value types)
  src/StrangeSharpTerm.Store/        ← STStore      (inventory JSON, ssh_config parser)
  src/StrangeSharpTerm.Security/     ← STSecurity   (known_hosts, credential store)
  src/StrangeSharpTerm.Transport/    ← STTransport  (SSH.NET connection pool, probes)
  src/StrangeSharpTerm.Assist/       ← STAssist     (providers, tool loop, CommandPolicy)
  src/StrangeSharpTerm.Mcp/          ← STMCP        (thin wrapper over the MCP SDK)
  src/StrangeSharpTerm.Terminal/     ←              (new: XTerm.NET ↔ SSH channel glue)
  src/StrangeSharpTerm.App/          ← App/         (Avalonia views + view models)
  src/stctl/                         ← stctl        (headless driver)
  tests/…                            ← one per library project
  spikes/                            ← throwaway risk answers, outside the solution
  build/                             ← build, test, and local sshd scripts; packaging later
```

## Module-by-module map

| Swift | LOC | Disposition |
|---|---|---|
| `STModel` | 769 | **Ported ~1:1.** Records + `System.Text.Json`. `PortForward` is a struct with a `Kind` enum, not an enum with payloads — the abstract-record-hierarchy shape is what `SshConfigHeader` and the importer's warning reasons needed instead. |
| `STStore` | 630 | **Ported ~95%.** `InventoryDocument` versioning and the hand-written decoder map onto optional properties plus a version check before decoding. Atomic write → write beside, flush, `File.Move(overwrite: true)`; `File.Replace` needs the target to exist, which a first save has not got. App-group container path is deleted; `Environment.SpecialFolder.ApplicationData` is `%APPDATA%` on Windows and `~/Library/Application Support` on macOS. `SSHConfigParser`/`SSHConfigImporter` (452 LOC) are pure text processing and port directly — **config-file fidelity survives the move off OpenSSH**, because it was always ours. |
| `STAssist` | 2,957 | **Port, ~90%.** `URLSession.bytes(for:)` → `HttpClient` + `HttpCompletionOption.ResponseHeadersRead`; `AsyncThrowingStream` → `IAsyncEnumerable<T>`. `CommandPolicy` (458 LOC, the safety allowlist) ports verbatim and keeps its tests. Watch `Redaction.swift`'s regexes for ICU→.NET dialect drift. |
| `STMCP` | 1,989 | **Replace.** The official SDK covers stdio, Streamable HTTP, and JSON-RPC. Keep our own `MCPServerConfig`, `MCPToolName.qualified/split`, and the `dataDestination` sentences. `MCPLoopback`'s `NWListener` → `HttpListener` on 127.0.0.1:33418-33421. If the SDK's OAuth 2.1 story is thin, port `MCPOAuth.swift` (476 LOC) on top of it. Net ~1,000 LOC deleted. |
| `STTransport` | 2,467 | **Mostly delete.** `SSHInvocationBuilder`, `ControlMasterSupervisor`, `ControlPath`, `AskpassChannel`, all 690 LOC of the hand-written SFTP v3 client, and `HostKeyScanner` are replaced by SSH.NET. Keep the *behaviour* of `SSHFailure`'s 11-case classification, re-mapped from SSH.NET exception types instead of OpenSSH stderr prose. `ServerProbe` (196 LOC, the dashboard probe and its Linux/macOS parsers) ports as-is over `SshClient.RunCommand`. `LocalPortProbe` → `Socket` with a connect timeout. Est. ~700 LOC new. |
| `STSecurity` | 350 | **Split.** `KnownHosts.swift` (242 LOC — hashed hosts, `@revoked`, `SHA256:` fingerprints matching `ssh-keygen -lf`) ports directly; wire it to SSH.NET's `HostKeyReceived` event, which is *better* than today — no `ssh-keyscan` subprocess. `KeychainStore` → `Devlooped.CredentialManager`. |
| `STProtocol` | 402 | **Delete entirely.** |
| `Agent/` | 135 | **Delete entirely.** |
| Three extensions | 796 | **Delete entirely.** |
| `st-askpass` | 26 | **Delete entirely.** SSH.NET takes a passphrase as a string in-process. |
| `stctl` | 402 | **Rewrite thin** on `System.CommandLine`. Drop the SFTP subcommands that only existed to exercise the hand-written client. |
| `App/` | 13,181 | **Reimplement.** See below. |

## Connection model

`ControlMasterSupervisor`'s job — one authenticated connection per host, many
consumers multiplexed over it — is what SSH.NET gives natively. Replace it with a
`ConnectionPool`: `Dictionary<NodeID, SshClient>`, and over each client open
`ShellStream`s (terminal panes), `SftpClient` (browser), `ForwardedPortLocal/Remote/
Dynamic` (tunnels), and `RunCommand` (probes, assistant tools). Same property, no
control socket, no 104-byte `sun_path` arithmetic, and it works on Windows.

Jump hosts (`-J`) are the one thing that needs building: connect an `SshClient` to the
jump host, open a `ForwardedPortLocal` to the target, connect a second client through
it. ~100 LOC, and `ConnectionSettings`' inheritance model already carries the data.

## The terminal — the one genuinely hard problem

Today `LocalProcessTerminalView` owns a local pty running `ssh`, and the app never sees
a byte of output. With SSH.NET there is no local pty at all: the *server* allocates the
pty, `ShellStream` carries the bytes, and resize is a `window-change` channel request.
That removes the ConPTY/openpty problem entirely — which is the main reason this is
tractable on Windows.

The integration contract from `TerminalPane.swift` is small and already well-defined:

| Today | In .NET |
|---|---|
| `startProcess(executable:args:)` | `SshClient.CreateShellStream(term, cols, rows, …)` |
| `BroadcastingTerminalView.send(source:data:)` override (the broadcast hook) | intercept at the control's input event before writing to `ShellStream` |
| `TerminalRegistry.visibleText` / `recentText` (feeds the assistant) | `terminal.Buffer.Lines` → string |
| `TerminalRegistry.send(text:to:)` (snippets, staged commands) | write to `ShellStream` |
| `applyTheme` → `installColors([Color])` for 16 ANSI slots | `TerminalOptions` palette; `ThemePalette`'s `UInt32` hex values port unchanged |
| `setTerminalTitle`, `processTerminated` | XTerm.NET title event; stream close |
| `sizeChanged` — **an empty no-op today** | `IPtyConnection.Resize` → `ShellStream.ChangeWindowSize` |

~~Resize is the one thing that was free and no longer is. Budget for it.~~
**Corrected by the spike:** resize is still the library's job. `ShellStream`
exposes `ChangeWindowSize`, and the Avalonia control calls `IPtyConnection.Resize`
on layout. Proven end to end in `spikes/TerminalSpike`.

`TerminalRegistry`'s weak-reference singleton exists only because `NSViewRepresentable`
hands back no view reference; in Avalonia the control is an ordinary bound object and
the whole class disappears.

## Reimplementing `App/`

The good news from the survey: this is a more mechanical port than a SwiftUI app usually
is. There is **no** `List`, `NavigationSplitView`, `Table`, `.toolbar`, `.searchable`,
`MenuBarExtra`, `Settings` scene, `@AppStorage`, `@SceneStorage`, window restoration, or
multi-window. The sidebar and every list are hand-rolled `VStack`/`HStack`/`ZStack`
inside `ScrollView` (27 of them, 0 `List`), which maps almost 1:1 onto
`StackPanel`/`Grid`/`ScrollViewer`. Settings is a plain sheet, not a `Settings` scene.
The 12 `@Observable` classes become `ObservableObject`s; view access is already 67
`@Environment` against only 24 `@State`, so there is very little view-local state to
untangle.

What needs real design work:

- **`AppModel` (1,908 LOC) must be split.** It is the god object: inventory CRUD,
  selection, tabs/panes/splits, broadcast, palette, eight sheet flags, *and* ~360 LOC of
  headless CLI driver. Its own `// MARK:` comments name the seams. Split into
  `InventoryViewModel`, `WorkspaceViewModel` (tabs/panes/splits), `PaletteViewModel`, a
  `DialogService`, and a separate `HeadlessDriver` outside the view model layer.
- **Eight `.sheet`s + 2 `.alert`s in `RootView` alone**, each a hand-written
  `Binding(get:set:)` over an optional. Replace with one `IDialogService` returning
  `Task<TResult?>`. This also fixes the sheet-over-a-sheet case (`MCPServerEditor`
  presented from inside `SettingsSheet`), which has no Avalonia analogue.
- **Menu bar**: `.commands` builds ~20 items including a `ForEach(1...9)` generating
  ⌘1–⌘9. Use Avalonia `NativeMenuBar` (native on macOS, in-window on Windows) with
  `ICommand.CanExecute` replacing each `.disabled(…)` binding.
- ~~**SF Symbols: 36 distinct glyphs across ~90 call sites, with no Avalonia equivalent.**~~
  **Decided: Lucide** (ISC, not MIT as this once said), vendored as generated
  Avalonia geometry rather than taken from a package built against Avalonia 11.
  34 distinct symbols mapped in one pass, including the three with no obvious twin:
  `server.rack` → server, `dot.radiowaves.left.and.right` → radio-tower,
  `text.append` → text-cursor-input. See `docs/adr/0003-icons.md`.
- **`.windowStyle(.hiddenTitleBar)` is load-bearing** — `RootView` and `SessionTabBar`
  hard-code a 26/28pt spacer for the traffic lights. Decide what this means on Windows;
  Avalonia's `ExtendClientAreaToDecorationsHint` is the macOS half, Windows needs its
  own answer.
- **AppKit leakage, 6 sites, all trivially replaceable**: `NSOpenPanel` →
  `StorageProvider.OpenFilePickerAsync`; `NSPasteboard` → `IClipboard`;
  `NSWorkspace.open` → `ILauncher`; `NSColor`/`NSFont` → Avalonia brushes.
- **Delete, don't port, the `isSnapshot` mechanism.** `SnapshotMode.swift` and ~20
  branch sites across 12 files exist solely to work around `ImageRenderer` being unable
  to lay out a `ScrollView` or draw an AppKit-backed control. Avalonia's
  `RenderTargetBitmap` renders the real visual tree. Keep the `--demo-*` fixture seeding
  (`AppModel.swift:1522-1829`) — that is genuine test data — and drop every code path
  that only dodges a renderer limitation.
- **Delete** `MountManager.swift` (78) and `AgentRegistrar.swift` (83).

## Platform strategy: macOS first, Windows always

Day-to-day development and manual testing happen on macOS, because the Swift app —
the parity reference — only runs there. Windows is **not** a later phase. It is the
first reason for the rewrite, and a Windows pass after parity would find its
design-shaping problems after the design has set. Three rules:

1. **CI is the Windows baseline.** Every push builds and tests on `windows-latest`. A
   red Windows job is a failure, never "fix later".
2. **Design-shaping Windows questions are answered in the milestone they shape**, and
   each answer is recorded as an ADR:

| Windows question | Why it cannot wait | Closed in |
|---|---|---|
| ~~ssh-agent over a named pipe~~ — **closed in M2** | Agent auth is the preferred credential method; it authenticates on Windows, asserted by the gate | M2 |
| Paths, key-file permissions, CRLF in `ssh_config` | Cheap as test cases while porting, tedious to retrofit | M1 (store, parser), M2 (known_hosts, keys) |
| ~~Running sshd for tests~~ — **closed in M2** | `build/local-sshd.ps1` is the counterpart of `local-sshd.sh`, installing the OpenSSH server capability when an image lacks it | M2 |
| ~~Terminal keyboard input: Ctrl/Alt sequences, AltGr, IME~~ — **closed** | Verified by running `spikes/TerminalSpike` on a Windows desktop before building on it | Before M3 |
| Title bar, menu bar, ⌘ vs Ctrl shortcuts | The hidden title bar and its hard-coded spacers shape the M4 layout | Start of M4 |

3. **Some checks need a Windows desktop, not a runner.** CI can cover the agent pipe;
   keyboard input and window chrome need a person at a Windows machine or VM. A
   Windows ARM VM on Apple Silicon is enough for that; `win-x64` stays covered by CI.

Deliberately deferred to M8: MSIX or installer, Authenticode signing, and dedicated
`win-arm64` testing.

## Working rules

- **One branch per milestone, merged by pull request**, so CI gates the merge rather
  than reporting after the fact. GitHub Free allows no branch protection on a private
  repository, so nothing enforces this server-side: the gate is the rule, not a setting.
- **A milestone is done when** its CI jobs are green on both OSes *and* its manual
  checks are recorded in the milestone PR's description.
- **CI grows with the milestones:**

| Milestone | CI adds | Manual checks before "done" |
|---|---|---|
| M1 | Model and Store suites | Round-trip the real `inventory.json`; import the real `~/.ssh/config` |
| M2 | Integration job: loopback sshd + ported integration test, both OSes | Agent auth on Windows (may itself be a CI job) |
| M3 | `--dump-terminal` assertions | Typing test in a real Windows window |
| M4–M5 | Headless UI driver; `--render` screenshots | Screenshot diff against `docs/reference/screenshots/` |
| M6–M7 | Assist and MCP suites | One real assistant turn; one real MCP server |
| M8 | Packaging workflow: signing, notarization | Parity review; one real host per OS |

- **Real user data never enters the repo.** Fixtures derived from a real
  `inventory.json` or `~/.ssh/config` are sanitised first.

## Milestones

Each is independently demonstrable; nothing is merged that cannot be run.

**M0 — Skeleton. Done.** Repo, solution layout, CI
workflow, reference material copied across. Both spikes ran green
against a real sshd on loopback (`spikes/`, and `docs/adr/0002`):

- *(a)* `Iciclecreek.Avalonia.Terminal` drives cleanly from an SSH.NET
  `ShellStream` through an `IPtyConnection` shim. The fallback (our own control
  over XTerm.NET) is not needed.
- *(b)* `SshNet.Agent` enumerates agent identities and authenticates with one.
  Proven on macOS by the spike and, in M2, on Windows too, where the OpenSSH
  agent is a named pipe rather than a Unix socket.

The spikes also folded in most of M2's risk: one authentication across three
execs, a working local forward that releases its port, a 700 KiB SFTP round-trip
by SHA-256, and a host key fingerprint matching `ssh-keygen -lf` exactly.

Still open, in order:

1. ~~**The Windows build failed.**~~ **Fixed.** `StrangeSharpTerm.App.csproj` pointed
   `ApplicationIcon` at `build/icon/strangesharpterm.ico` on Windows, and that file was
   never committed. Forcing the condition on macOS (`-p:OS=Windows_NT`) reproduced it:
   Avalonia's `GenerateAvaloniaResourcesTask` threw `FileNotFoundException`. The
   property is removed; the Windows icon comes back with packaging in M8.
2. ~~**CI has never run.**~~ **Done.** The first run after the initial push was green
   on `macos-latest` and `windows-latest`, which was this project's first Windows build.
3. ~~**The Windows half of spike (b)**.~~ **Closed in M2.** The integration gate
   authenticates with an agent-held key on a Windows runner, so nothing in the
   credential path is now untested there.

**M1 — Model + Store. Done.** Port `STModel` and `STStore` with their 84 tests — Model 44
(Credential 9, FuzzyMatch 10, Inheritance 15, Snippet 10) and Store 40 (SSHConfig 30,
InventoryStore 10). Model first; it depends on nothing. Load a real `inventory.json`
written by the Swift app and round-trip it byte-identically. Import a real
`~/.ssh/config`.

Byte-identical was real work. `InventoryStore.swift` writes with `JSONEncoder` and
`[.prettyPrinted, .sortedKeys]`, and none of that matches `System.Text.Json`'s defaults.
Rather than infer the rules from documentation, `build/swift-parity/generate.sh`
compiles the Swift app's own model and store sources together with a generator, and
writes golden files the C# tests compare against byte for byte. What they showed:

| Swift writes | `System.Text.Json` default |
|---|---|
| `"key" : value` — a space before the colon | `"key": value` |
| keys sorted by Unicode scalar value | property declaration order |
| ids as `{"rawValue": "…"}`, uppercase | a bare lowercase `Guid` |
| `/` escaped as `\/`, non-ASCII raw | `/` raw, non-ASCII and `<>&'+` escaped |
| an empty container as its bracket, a blank line, its close | `[]` |
| a double past 2^53 or below 0.0001 in exponent form | other thresholds, other spelling |

`SwiftJson` implements that layout. `Timestamp` keeps a Swift `Date` as the double it
is on disk, because `DateTimeOffset`'s ticks cannot hold every one of them exactly, and
a value that shifted on load would break the round trip. Tags stay an ordered list
rather than a set, so the order the Swift app happened to write survives a save.

Verified: the real `inventory.json` this machine's Swift app wrote round-trips byte for
byte, and the importer agrees with Swift's — settings, ids, ordering and warnings — on a
representative config (`tests/StrangeSharpTerm.Store.Tests/Fixtures/ssh_config.sample`).
The real-config check is opt-in and waits for a machine that has one: point
`STRANGESHARPTERM_REAL_SSH_CONFIG` at it, and `STRANGESHARPTERM_REAL_INVENTORY` at an
inventory.

Two deliberate divergences from Swift, both tested: CRLF is one line break rather than
two (Swift's split doubled every line number in a file saved on Windows), and listing
the descendants of a corrupted cycle terminates instead of recursing forever.

**M2 — Transport. Done.** `ConnectionPool` gives what `ControlMasterSupervisor`
gave — one authentication per host, everything else multiplexed over it — with no
control socket and no path-length arithmetic. `KnownHosts` is ported and wired to
`HostKeyReceived`, including the rule that a changed key **never** prompts, and an
accepted key is appended to OpenSSH's own file. Secrets live in
`Devlooped.CredentialManager`; agent, key-and-passphrase and password
authentication all work. `ServerProbe` and its parsers port as-is. `stctl` is
rewritten on `System.CommandLine` far enough to drive all of it, and
`Scripts/integration-test.sh` is ported to `build/integration-test.sh` with a
PowerShell counterpart.

The gate runs on **both** operating systems in CI. It asserts what the Swift one
did — three execs cost one authentication, a forward carries an `SSH-2.0-` banner
and releases its port, 700 KiB round-trips through SFTP by SHA-256, and failures
classify as authentication, resolution or refusal — plus five assertions that are
new: an unknown host key is refused with nothing recorded, an accepted one is
findable by `ssh-keygen -F`, a changed key is refused, it stays refused even when
the caller asks to accept new keys, and an agent-held key authenticates.

One property did not survive. SSH.NET's `SftpClient` owns its own session rather
than opening a subsystem channel on an existing one, so SFTP costs a second
authentication where the Swift app's rode the ControlMaster connection. It is
cached per session to hold that to one, and it is the price of deleting 690 lines
of hand-written SFTP v3.

**M3 — Terminal. Done.** The Windows keyboard check that gated this milestone
came first: typing, AltGr characters, dead-key accents, arrows and history,
Ctrl-C, Tab completion and resize all behave in `spikes/TerminalSpike` on a
Windows desktop. That was the open question, and the answer made this a straight
port.

`TerminalSession` binds an XTerm.NET engine to an SSH shell channel. Both the
renderer and the session read the same stream through `TeeStream`, so what the
assistant reads is what the user is looking at, and a headless driver can assert
on it. Resize sends the `window-change` request; the palette carries the Swift
themes' terminal colours unchanged; `TerminalRegistry` holds the broadcast group,
where a group of one is not a broadcast and nothing echoes back to the pane that
typed. `TerminalPaneView` attaches Iciclecreek's control through an
`IPtyConnection` over the session, as `docs/adr/0002` said it would.

Proven by the gate on both operating systems: a shell's output renders, and after
a resize the remote pty agrees about the width. Two bugs surfaced there that
macOS alone would have shipped — a `window-change` sent before the server had a
pty to resize, and Enter sent as a line feed, which a Unix pty translates and
Windows does not, so every command was typed and never run.

**M4 — App shell.** Avalonia window, sidebar, host detail, tabs/panes/splits,
`NativeMenuBar`, command palette, theme system. The `Theme` static and its
`.id(themeID)` redraw hack collapse into ordinary `DynamicResource`s. First decide, as
an ADR, the Windows title bar, where the menu lives, and how ⌘ shortcuts map to Ctrl.

**M5 — Feature panes.** SFTP browser (SSH.NET `SftpClient`), tunnels (`ForwardedPort*`),
dashboards, credentials, snippets, all editor dialogs via `IDialogService`.

**M6 — Assistant. Done.** `StrangeSharpTerm.Assist` and its 265 tests: both
providers, `CommandPolicy`, `Redaction`, the agent loop, orchestration and run
plans, plus the assistant and orchestrator panes and the settings section.

One decision departs from the plan and is recorded as `docs/adr/0006`. The plan
says "both stream decoders", which was right for Swift and is not right here:
Swift had no Anthropic SDK and C# does, and the request shapes this depends on —
adaptive thinking, a refusal as a stop reason, a tool call streamed as partial
JSON — are exactly the ones that move between model generations. Claude goes
through the official SDK; DeepSeek stays raw HTTP, so one decoder is ported and
it is the one with no vendor keeping it current. `IAssistBackend` is still the
only seam, and both providers are driven end to end against a stub returning a
recorded stream.

**M7 — MCP.** Wrap the official SDK, port the config and OAuth surface, port the 58
tests. MCP settings and server editor UI.

**M8 — Packaging.** See below. Parity review against the Swift app, then archive the
Swift `StrangeTerm` repo (it keeps its name; see Repository).

## Packaging — what actually gets easier

Escaping Xcode does **not** escape notarization. Distributing to another Mac still means
Developer ID + `codesign --options runtime` + `notarytool submit --wait` + `stapler`.
`Scripts/package.sh` (4.6 KB) already does all of that correctly and its *sequence* is
worth transcribing rather than rediscovering — including the individual verify loop over
nested binaries, which a self-contained .NET publish makes *more* important, not less
(dozens of `.dylib`s, every one of which must be signed or the submission is rejected).

What genuinely goes away: XcodeGen, the six-target `project.yml`, the generated
LaunchAgent plist, app groups, the team-prefixed Mach service name, five entitlements
files, and — critically — the two provisioning-profile capabilities that currently block
M8 and the Credentials limitation. One signed bundle, no `.appex`, no helper.

What is new:
- macOS: hand-build the `.app` bundle (the SDK does not); hardened runtime needs
  `com.apple.security.cs.allow-jit` and `allow-unsigned-executable-memory` for the .NET
  JIT, so ship an `Entitlements.plist` with `<UseHardenedRuntime>true</UseHardenedRuntime>`.
  Universal binary means publishing `osx-arm64` and `osx-x64` and `lipo`-ing, or
  shipping two DMGs.
- Windows: MSIX or a signed installer, plus an Authenticode certificate — a second
  signing identity and a second annual renewal. Budget for it.
- `Scripts/make-icon.sh` ports as-is for `.icns`; add an `.ico` path for Windows.

## What is lost — be deliberate about each

1. **Tunnels no longer outlive the app.** Today the LaunchAgent keeps them up after the
   UI quits; the README names this as a benefit. Once the agent is gone, quitting drops
   every forward. Recommendation: accept for v1 and say so in the README. A headless
   `strangeterm-daemon` is a later option if it is missed.
2. **Finder integration goes entirely** — mounting, sidebar badges, the Share menu item.
   Cheap, in practice: File Provider never worked, and the in-app SFTP browser covers
   the use case on both platforms.
3. **Peer authentication weakens** — but only because there are no longer any peers.
   The `setCodeSigningRequirement` check protected an IPC channel that no longer exists.
4. **ssh's own resolution semantics.** `Match` blocks and canonicalization were already
   skipped by `SSHConfigImporter`, so the practical loss is small — but anything ssh
   resolves that our parser does not, we now silently do not honour.
5. **Trust is no longer shared with the `ssh` CLI by default.** Keep writing to and
   reading from the real `~/.ssh/known_hosts` (`%USERPROFILE%\.ssh\known_hosts` on
   Windows) so this stays true. It is a deliberate property of the current design.
6. **410 swift-testing assertions must be re-earned.** They are the codebase's biggest
   asset — 410 `@Test` functions across 73 suites, almost all pure-logic against injected
   fakes. Port them *with* their subjects, module by module, never "later".

## Verification

- **Per module**: the ported xUnit suite. Target the same ~0.52:1 test-to-source ratio.
  `SSHInvocationTests` (26 tests pinning argv strings) is the one suite that does not
  survive — its subject is gone; replace it with tests over `ConnectionPool` reuse.
- **Integration**: port `Scripts/integration-test.sh`. It needs no Docker and no admin
  rights — it runs an unprivileged sshd on 127.0.0.1:22022 with throwaway keys. Its
  assertions transfer directly: one authentication across three execs (now "one
  `SshClient`, three channels"), a `-L` forward carrying an `SSH-2.0-` banner and
  releasing its port on cancel, a 700 KiB SFTP round-trip matching by SHA-256, and
  failure classification for bad user / unresolvable host / refused port. Run it on both
  macOS and Windows in CI.
- **Headless UI**: port the `stctl`/`--autoconnect`/`--autosplit`/`--dump-terminal`/
  `--orchestrate`/`--plan-run` driver. `--dump-terminal` remains the only way to assert
  on rendered terminal content and is how M3 is proven.
- **Snapshots**: rebuild the `--render` path on `RenderTargetBitmap` and regenerate every
  image the Swift app committed (copied to `docs/reference/screenshots/`) from the same
  `SampleInventory` fixtures. Diffing the new screenshots against those is the cheapest
  parity check available.
- **Manual**: against one real host per platform — connect, split, broadcast, an SFTP
  edit-in-place, a tunnel, a dashboard refresh, and one assistant turn that stages a
  command without running it.

## Risks and open questions

| Risk | Handling |
|---|---|
| ~~`SshNet.Agent` is a third-party extension~~ — **closed.** Agent auth works on macOS and, over the named pipe, on Windows | Asserted by the integration gate on both operating systems, so a regression fails CI rather than surfacing in front of a user. |
| ~~`Iciclecreek.Avalonia.Terminal` may be too coupled to `Porta.Pty`~~ — **closed.** The shim works | One limitation found: `PtyExitedEventArgs` has an internal constructor, so `ProcessExited` cannot be raised from outside. Costs nothing — we own the `SshClient` and signal session exit ourselves. See `docs/adr/0002`. |
| Avalonia 12 released April 2026; XTerm.NET 2.0 targets .NET 10 | Both are current. Pinned exactly in `Directory.Packages.props` (Avalonia 12.1.2, XTerm.NET 2.0.2), the way `project.yml` pinned SwiftTerm 1.20.0 and for the same stated reason. |
| ~~Terminal keyboard input on Windows is unverified~~ — **closed.** Typing, AltGr, dead keys, arrows, Ctrl-C, Tab completion and resize all behave | Checked by hand against the spike on a Windows desktop, since no runner can type. Re-check by hand if the control's input handling changes. |
| Windows path/permission assumptions | `ControlPath`'s `0o700` dirs and `sun_path` arithmetic disappear, but audit `InventoryStore` and the known_hosts path for POSIX assumptions. |
| .NET self-contained publish is ~70 MB per RID | Accept, or evaluate NativeAOT later — Avalonia supports it, but it interacts badly with reflection-based JSON and MVVM source generators. Not a v1 concern. |
| Scope: ~29 k LOC of Swift becomes ~18–22 k LOC of C# | The milestones are ordered so M1–M3 produce a usable terminal client before any of the assistant/MCP work starts. If the project stalls, it stalls somewhere useful. |
