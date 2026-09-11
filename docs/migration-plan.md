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
| `STModel` | 769 | **Port, ~1:1.** Records + `System.Text.Json`. `PortForward`'s enum-with-payload becomes an abstract record hierarchy. |
| `STStore` | 630 | **Port, ~95%.** `InventoryDocument` versioning and the hand-written decoder map onto a `JsonConverter`. Atomic write → `File.Replace`. App-group container path is deleted; use `Environment.SpecialFolder.ApplicationData`. `SSHConfigParser`/`SSHConfigImporter` (452 LOC) are pure text processing and port directly — **config-file fidelity survives the move off OpenSSH**, because it was always ours. |
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
- **SF Symbols: 36 distinct glyphs across ~90 call sites, with no Avalonia equivalent.**
  Pick one replacement set (Lucide or Phosphor, both MIT, both have full coverage) and
  make the substitution a single reviewed pass early — several glyphs
  (`server.rack`, `dot.radiowaves.left.and.right`, `text.append`) have no obvious twin
  and want a design decision, not a default.
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
| ssh-agent over a named pipe (`\\.\pipe\openssh-ssh-agent`) | Agent auth is the preferred credential method; if `SshNet.Agent` fails here, the credential path changes | M2, before it ships |
| Paths, key-file permissions, CRLF in `ssh_config` | Cheap as test cases while porting, tedious to retrofit | M1 (store, parser), M2 (known_hosts, keys) |
| Running sshd for tests | `build/local-sshd.sh` is POSIX `sh`; the integration gate must run on both OSes | M2 |
| Terminal keyboard input: Ctrl/Alt sequences, AltGr, IME | Verified on macOS only; it lives in the control's input layer, which M3 builds on | Start of M3 — run `spikes/TerminalSpike` on Windows first |
| Title bar, menu bar, ⌘ vs Ctrl shortcuts | The hidden title bar and its hard-coded spacers shape the M4 layout | Start of M4 |

3. **Some checks need a Windows desktop, not a runner.** CI can cover the agent pipe;
   keyboard input and window chrome need a person at a Windows machine or VM. A
   Windows ARM VM on Apple Silicon is enough for that; `win-x64` stays covered by CI.

Deliberately deferred to M8: MSIX or installer, Authenticode signing, and dedicated
`win-arm64` testing.

## Working rules

- **One branch per milestone, merged by pull request**, so CI gates the merge rather
  than reporting after the fact. Branch protection on `main` requires both OS jobs.
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

**M0 — Skeleton. Done on macOS; three items open.** Repo, solution layout, CI
workflow, reference material copied across. Both spikes ran green
against a real sshd on loopback (`spikes/`, and `docs/adr/0002`):

- *(a)* `Iciclecreek.Avalonia.Terminal` drives cleanly from an SSH.NET
  `ShellStream` through an `IPtyConnection` shim. The fallback (our own control
  over XTerm.NET) is not needed.
- *(b)* `SshNet.Agent` enumerates agent identities and authenticates with one —
  **on macOS**. The Windows path, where the OpenSSH agent is a named pipe rather
  than a Unix socket, is the single remaining M0 risk and needs a Windows machine
  or a CI job with sshd to close.

The spikes also folded in most of M2's risk: one authentication across three
execs, a working local forward that releases its port, a 700 KiB SFTP round-trip
by SHA-256, and a host key fingerprint matching `ssh-keygen -lf` exactly.

Still open, in order:

1. **The Windows build fails today.** `StrangeSharpTerm.App.csproj` sets
   `ApplicationIcon` to `build/icon/strangesharpterm.ico` when building on Windows, and
   that file was never committed. Forcing the condition on macOS (`-p:OS=Windows_NT`)
   reproduces it: Avalonia's `GenerateAvaloniaResourcesTask` throws
   `FileNotFoundException`. Commit an icon, or drop the property until M8.
2. **CI has never run.** The workflow exists, but the repository has no GitHub remote
   yet, so "builds on both OSes" has only been observed on macOS. Once pushed, get one
   green run on `macos-latest` and `windows-latest` before any M1 work merges.
3. **The Windows half of spike (b)**, above.

**M1 — Model + Store.** Port `STModel` and `STStore` with their 84 tests — Model 44
(Credential 9, FuzzyMatch 10, Inheritance 15, Snippet 10) and Store 40 (SSHConfig 30,
InventoryStore 10). Model first; it depends on nothing. Load a real `inventory.json`
written by the Swift app and round-trip it byte-identically. Import a real
`~/.ssh/config`.

Byte-identical is real work, not a formality. `InventoryStore.swift` writes with
`JSONEncoder` and `[.prettyPrinted, .sortedKeys]`, and a real file confirms the output
differs from `System.Text.Json`'s defaults:

| Swift writes | `System.Text.Json` default |
|---|---|
| `"key" : value` — a space before the colon | `"key": value` |
| keys sorted | property declaration order |
| UUIDs uppercase | `Guid` lowercase |
| non-ASCII and `<>&'+` unescaped | escaped unless the encoder is relaxed |

Both use a 2-space indent and no trailing newline. Expect a small custom writer. Two
behaviours the sample file could not show must be pinned with fixtures: Swift escapes
`/` as `\/` unless `.withoutEscapingSlashes` is set, and `.sortedKeys` ordering may not
be plain ordinal. Atomic write via `File.Replace` needs a fallback, because it requires
the target to exist already — the first write has none.

Add the Windows cases while porting: CRLF line endings in `ssh_config`, and
`%USERPROFILE%` / `%APPDATA%` paths.

**M2 — Transport.** `ConnectionPool` over SSH.NET: connect, exec, disconnect. Port
`KnownHosts` and wire host-key verification to `HostKeyReceived` — including the rule
that `changed` **never** prompts. Credential store via `Devlooped.CredentialManager`.
`SshNet.Agent`, key+passphrase, and password auth. Port `ServerProbe` and its parsers.
Rewrite `stctl` far enough to drive all of this headlessly, then port
`Scripts/integration-test.sh` to run against the unprivileged sshd on 127.0.0.1:22022.
That script is the real acceptance gate: it already asserts connection reuse, forward
liveness, and a 700 KiB SFTP round-trip by SHA-256. It becomes its own CI job on both
OSes, which needs a Windows counterpart to `build/local-sshd.sh`. The Windows ssh-agent
risk closes here, not later.

**M3 — Terminal.** The `StrangeSharpTerm.Terminal` control: `ShellStream` ↔ XTerm.NET,
resize via `window-change`, 16-colour theming, scrollback read-back, broadcast
interception, title and exit handling. `spikes/TerminalSpike/SshPtyConnection.cs` is
the starting point. Before building on it, run the spike on a Windows desktop: keyboard
input (Ctrl/Alt sequences, AltGr, IME) is unverified there.

**M4 — App shell.** Avalonia window, sidebar, host detail, tabs/panes/splits,
`NativeMenuBar`, command palette, theme system. The `Theme` static and its
`.id(themeID)` redraw hack collapse into ordinary `DynamicResource`s. First decide, as
an ADR, the Windows title bar, where the menu lives, and how ⌘ shortcuts map to Ctrl.

**M5 — Feature panes.** SFTP browser (SSH.NET `SftpClient`), tunnels (`ForwardedPort*`),
dashboards, credentials, snippets, all editor dialogs via `IDialogService`.

**M6 — Assistant.** Port `STAssist` and its 136 tests: both providers, both stream
decoders, `CommandPolicy`, `Redaction`, orchestration, run plans. The assistant and
orchestrator panes.

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
| ~~`SshNet.Agent` is a third-party extension~~ — **half closed.** Agent auth works on macOS; the Windows named-pipe path is untested | The last open M0 item. ssh-agent is the *preferred* credential method per the README, so this must be closed before M2 ships, not after. |
| ~~`Iciclecreek.Avalonia.Terminal` may be too coupled to `Porta.Pty`~~ — **closed.** The shim works | One limitation found: `PtyExitedEventArgs` has an internal constructor, so `ProcessExited` cannot be raised from outside. Costs nothing — we own the `SshClient` and signal session exit ourselves. See `docs/adr/0002`. |
| Avalonia 12 released April 2026; XTerm.NET 2.0 targets .NET 10 | Both are current. Pinned exactly in `Directory.Packages.props` (Avalonia 12.1.2, XTerm.NET 2.0.2), the way `project.yml` pinned SwiftTerm 1.20.0 and for the same stated reason. |
| Terminal keyboard input on Windows is unverified | Run `spikes/TerminalSpike` on a Windows desktop at the start of M3, before building on the control's input handling. |
| CI has never executed | Push, and get one green run on both OSes before any M1 work merges. |
| Windows path/permission assumptions | `ControlPath`'s `0o700` dirs and `sun_path` arithmetic disappear, but audit `InventoryStore` and the known_hosts path for POSIX assumptions. |
| .NET self-contained publish is ~70 MB per RID | Accept, or evaluate NativeAOT later — Avalonia supports it, but it interacts badly with reflection-based JSON and MVVM source generators. Not a v1 concern. |
| Scope: ~29 k LOC of Swift becomes ~18–22 k LOC of C# | The milestones are ordered so M1–M3 produce a usable terminal client before any of the assistant/MCP work starts. If the project stalls, it stalls somewhere useful. |
