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
| SSH / SFTP / tunnels | **SSH.NET** (`Renci.SshNet`, 2026.0.0) |
| ssh-agent auth | **SshNet.Agent** (OpenSSH agent + Pageant) — spike first, see Risks |
| Terminal emulation | **XTerm.NET** (MIT, headless VT100/ANSI engine, byte-fed) |
| Terminal rendering | **Iciclecreek.Avalonia.Terminal** if its `AttachConnection(IPtyConnection)` accepts a shim; else own Avalonia control over XTerm.NET |
| Secrets | **Devlooped.CredentialManager** (Git Credential Manager's store: macOS Keychain, Windows Credential Manager) |
| MCP client | **ModelContextProtocol.Core** 2.2.0 (official C# SDK) |
| LLM HTTP | Raw `HttpClient` — keep the current no-SDK stance |
| MVVM | `CommunityToolkit.Mvvm` source generators |
| Tests | xUnit + FluentAssertions |
| CI | GitHub Actions — `macos-latest` + `windows-latest`. There is none today; add it. |

## Solution layout

```
StrangeTerm.sln
  src/StrangeTerm.Model/        ← STModel          (pure value types)
  src/StrangeTerm.Store/        ← STStore          (inventory JSON, ssh_config parser)
  src/StrangeTerm.Security/     ← STSecurity       (known_hosts, credential store)
  src/StrangeTerm.Transport/    ← STTransport      (SSH.NET connection pool, probes)
  src/StrangeTerm.Assist/       ← STAssist         (providers, tool loop, CommandPolicy)
  src/StrangeTerm.Mcp/          ← STMCP            (thin wrapper over the MCP SDK)
  src/StrangeTerm.Terminal/     ←                  (new: XTerm.NET ↔ SSH channel glue)
  src/StrangeTerm.App/          ← App/             (Avalonia views + view models)
  src/stctl/                    ← stctl            (headless driver)
  tests/…                       ← one per src project
  build/                        ← packaging scripts
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
| `sizeChanged` — **an empty no-op today** | **we must implement it**: `ShellStream.SendWindowChangeRequest` on layout |

Resize is the one thing that was free and no longer is. Budget for it.

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

## Milestones

Each is independently demonstrable; nothing is merged that cannot be run.

**M0 — Skeleton.** New repo, solution layout, CI on both OSes, `README.md`/`Docs/`
copied across. Two spikes, both time-boxed, both of which can change the plan:
*(a)* drive `Iciclecreek.Avalonia.Terminal` from an SSH.NET `ShellStream` through an
`IPtyConnection` shim; *(b)* authenticate to a real host using `SshNet.Agent` on macOS
**and** Windows. Do these first — they are the only two unresolved technical risks.

**M1 — Model + Store.** Port `STModel` and `STStore` with their 84 tests. Load a real
`inventory.json` written by the Swift app and round-trip it byte-identically. Import a
real `~/.ssh/config`.

**M2 — Transport.** `ConnectionPool` over SSH.NET: connect, exec, disconnect. Port
`KnownHosts` and wire host-key verification to `HostKeyReceived` — including the rule
that `changed` **never** prompts. Credential store via `Devlooped.CredentialManager`.
`SshNet.Agent`, key+passphrase, and password auth. Port `ServerProbe` and its parsers.
Rewrite `stctl` far enough to drive all of this headlessly, then port
`Scripts/integration-test.sh` to run against the unprivileged sshd on 127.0.0.1:22022.
That script is the real acceptance gate: it already asserts connection reuse, forward
liveness, and a 700 KiB SFTP round-trip by SHA-256.

**M3 — Terminal.** The `StrangeTerm.Terminal` control: `ShellStream` ↔ XTerm.NET,
resize via `window-change`, 16-colour theming, scrollback read-back, broadcast
interception, title and exit handling.

**M4 — App shell.** Avalonia window, sidebar, host detail, tabs/panes/splits,
`NativeMenuBar`, command palette, theme system. The `Theme` static and its
`.id(themeID)` redraw hack collapse into ordinary `DynamicResource`s.

**M5 — Feature panes.** SFTP browser (SSH.NET `SftpClient`), tunnels (`ForwardedPort*`),
dashboards, credentials, snippets, all editor dialogs via `IDialogService`.

**M6 — Assistant.** Port `STAssist` and its 136 tests: both providers, both stream
decoders, `CommandPolicy`, `Redaction`, orchestration, run plans. The assistant and
orchestrator panes.

**M7 — MCP.** Wrap the official SDK, port the config and OAuth surface, port the 58
tests. MCP settings and server editor UI.

**M8 — Packaging.** See below. Parity review against the Swift app, then archive
`strangeterm-swift`.

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
  image in `Docs/screenshots/` from the same `SampleInventory` fixtures. Diffing the new
  screenshots against the committed Swift ones is the cheapest parity check available.
- **Manual**: against one real host per platform — connect, split, broadcast, an SFTP
  edit-in-place, a tunnel, a dashboard refresh, and one assistant turn that stages a
  command without running it.

## Risks and open questions

| Risk | Handling |
|---|---|
| `SshNet.Agent` is a third-party extension, and OpenSSH agent on Windows is a named pipe rather than a Unix socket | **M0 spike (b).** ssh-agent is the *preferred* credential method per the README — the key never enters the app. If it fails on Windows, we need a fallback before committing, not after. |
| `Iciclecreek.Avalonia.Terminal` may be too coupled to `Porta.Pty` to accept an SSH-backed `IPtyConnection` | **M0 spike (a).** Fallback is an own Avalonia control over XTerm.NET — which is MIT, headless, and explicitly byte-fed, so the emulator itself is not at risk either way. |
| Avalonia 12 released April 2026; XTerm.NET 2.0 targets .NET 10 | Both are current, but pin exact versions the way `project.yml` pins SwiftTerm 1.20.0 — and for the same stated reason. |
| Windows path/permission assumptions | `ControlPath`'s `0o700` dirs and `sun_path` arithmetic disappear, but audit `InventoryStore` and the known_hosts path for POSIX assumptions. |
| .NET self-contained publish is ~70 MB per RID | Accept, or evaluate NativeAOT later — Avalonia supports it, but it interacts badly with reflection-based JSON and MVVM source generators. Not a v1 concern. |
| Scope: ~29 k LOC of Swift becomes ~18–22 k LOC of C# | The milestones are ordered so M1–M3 produce a usable terminal client before any of the assistant/MCP work starts. If the project stalls, it stalls somewhere useful. |
