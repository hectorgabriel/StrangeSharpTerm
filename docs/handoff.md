# Where things stand

A snapshot for picking the work up on another machine, or after a gap. Written
2026-09-13. `docs/migration-plan.md` is the authority on what each milestone
means; this says only what is done, what is in flight, and what to do first.

## Done, on `main`

| Milestone | What it left behind |
|---|---|
| **M0** skeleton and spikes | Solution, CI on both OSes, `spikes/` and `docs/adr/0002`. Both risks closed, including ssh-agent over a named pipe on Windows |
| **M1** model and store | `StrangeSharpTerm.Model` and `.Store` with their 84 tests. The inventory file is byte-identical to the Swift app's, checked against goldens the Swift code itself generates (`build/swift-parity`) |
| **M2** transport | One authenticated session per host carrying commands, shells, forwards and SFTP; host key trust shared with `ssh`; secrets in each platform's own store; `stctl`; the integration gate running against a real sshd on macOS **and** Windows in CI |
| **M3** terminal | `TerminalSession` binding an XTerm.NET engine to an SSH channel, the Avalonia control, broadcast, scrollback, and `stctl terminal` |
| **M5** (part) | one authenticated session per host, at last: `HostSessions` over the M2 pool, which every pane goes through. The SFTP browser (`IRemoteFiles`, a pane, ⇧⌘B), tunnels (`ITunnels`, a pane that starts and stops a host's forwards and releases the ports), the dashboard (`IServerHealth` over the M2 probe, in the detail pane, asked for rather than assumed), the credential library (a key or password described once, pointed at from any host or folder, with the secret in the platform store and never in the inventory), and snippets (saved commands, scoped to a folder or offered everywhere, run from the palette into the focused shell), and the settings sheet (the theme, as cards you can see, and the way in to both libraries) |
| **M4** done | the window (sidebar, tabs, host detail, splits, broadcast), the theme system, the host and folder editors, the menu bar, the command palette, and the headless UI driver. `docs/adr/0004` and `0005` record the decisions |
| **M8** done, as far as an account allows | `build/package.sh` (bundle, ad-hoc or Developer ID, disk image, notarisation written and gated), `build/install.sh`, `build/package.ps1`, `build/make-icon.sh`, and a CI job that packages both platforms on every push. `docs/adr/0008` records what a free Apple account can and cannot do, and the three things that only showed up by running it |
| **M7** done | `StrangeSharpTerm.Mcp`: both transports over the official SDK, tool namespacing, the grant rules, OAuth with a loopback redirect and platform-store tokens — then the Connected tools section, the server editor, `stctl mcp`, and `IExternalTools`, the seam that puts a connected tool through the assistant's own gate and budget. `docs/adr/0007` records what the SDK owns and what cannot be delegated |
| **M9** done | The workspace: `RemoteWorkspace` and `PosixPath` in Transport, `FilePolicy`, `Diff` and the three file tools in Assist, then the pane (⇧⌘E, a tree, an editor, ⌘S), the folder remembered per host in `preferences.json`, `stctl workspace`, and four checks in the integration gate. `docs/adr/0009` records why the permission is a folder rather than a rule about paths |
| **M6** done | `StrangeSharpTerm.Assist`: the two providers behind one seam, `CommandPolicy`, `Redaction`, the agent loop, the orchestrator and run plans — then the assistant pane (⌥⌘A), the orchestrator pane with its plan mode (⇧⌥⌘A), the settings section, `stctl ask`, and four `--demo-*` flags. `docs/adr/0006` records the one departure from the plan |

1042 tests. **M8 is done** on macOS as far as a free Apple account allows; the
Windows half is CI's to confirm, and a red Windows job is a failure rather than
something to fix later.

## In flight

Check `gh pr list` first — a pull request may have landed since this was
written. At the time of writing: nothing.

## What to do next, in order

1. **The parity review** against the Swift app, and then archiving the Swift
   repo. That is what M8 has left, and it needs no account.
2. **Three things need an account and nothing else.** A real assistant turn
   (`stctl ask user@host --question "…"`), a hosted MCP server's sign-in
   (`stctl mcp-signin --server "Name=https://host/mcp"`), and Developer ID
   signing plus notarisation (`./build/package.sh --notarize`). Each is written,
   each is gated behind a check that says what is missing, and none has run.
   Everything up to the credential is exercised: both providers against recorded
   streams, a local MCP server end to end for real, and an ad-hoc bundle built,
   verified, mounted from its disk image and started.
3. **The Windows app icon is still missing.** The macOS one is done:
   `build/mac/logo.svg` and the `AppIcon.icns` rendered from it are both in the
   repo, and `build/package.sh` picks the latter up. `make-icon.sh` writes the
   `.ico` only where ImageMagick is installed — `brew install imagemagick` and
   run it again — so until then the Windows bundle takes the system default.
4. **One shortcut is still unbound**: ⌃⌘X (disconnect), which cannot be
   translated to Windows as it stands — `docs/adr/0005` says why.
5. **What the panes do not do yet.** The browser has no rename and no
   new-folder, though `IRemoteFiles` carries both calls and the workspace pane
   now does both — the browser is the older of the two surfaces and the one to
   fold into the other if either goes. Neither has progress for a large
   transfer. Tunnels are read from the host's settings and cannot be added
   or edited there — the host editor deliberately leaves forwards alone and
   preserves them, so an editor for them is the missing half. The dashboard is
   asked for one probe at a time; the Swift app refreshed on a timer.
6. **What the workspace does not do yet.** No syntax highlighting — the editor
   is a text box, a gutter and a save button, and `docs/adr/0009` says why that
   is the deliberate floor rather than an omission. No search across the folder
   (`grep` through `run_command` is the answer today), no drag-and-drop onto the
   tree, and one folder per host: opening a second would make "the folder open
   on this host" a question with two answers, which is exactly what the
   assistant's file tools are judged against.
7. **Screenshots**, the other half of what the plan's CI table asks for at
   M4-M5. The driver is in place (`tests/StrangeSharpTerm.App.WindowTests`);
   what is missing is rendering. `CaptureRenderedFrame` returns a bitmap, but
   only with Skia behind the headless platform (`UseHeadlessDrawing = false`
   plus `Avalonia.Skia`), and a diff against `docs/reference/screenshots/` needs
   a decision about how much cross-platform text rendering is allowed to differ
   before a build fails. That decision is the work, not the plumbing.

## Looking at the window without a screen

`tests/StrangeSharpTerm.App.WindowTests` runs the real window on Avalonia's
headless platform: templates applied, layout run, mouse and keys delivered. It
is where an assertion goes when it needs a visual tree — that a pane is not
detached when another opens beside it, that a text field is the colour the theme
says. Three things it took to get right:

- **A test body runs on the UI thread**, so awaiting inside one waits for a
  continuation only that thread can run. `Headless.Finish` pumps the dispatcher
  while it waits, with a deadline, so a mistake fails a test instead of hanging
  the suite.
- **An empty `MemoryStream` is not a quiet shell.** Reading one returns zero,
  which a terminal reads as the end and asks again, forever. The fixture blocks
  instead.
- **The real terminal control does not settle** under the headless renderer, so
  these tests put a plain control in the pane. What the control itself does is
  still checked by running the app.
- **An Avalonia object wants a platform, and hangs without one.** A
  `NativeMenuItem` built in a plain unit test hung the whole run — no error, no
  timeout, just a suite that never finished — exactly as a `GridSplitter` did.
  Anything that constructs a control or a menu belongs in the window tests.

## Setting up a machine

- **.NET 10 SDK** (`global.json` pins it), then `./build/build.sh` and
  `./build/test.sh`.
- **A server to test against**: `./build/local-sshd.sh start` on macOS or Linux,
  `./build/local-sshd.ps1 start` on Windows. `./build/integration-test.sh` (or
  `.ps1`) is the acceptance gate and needs no Docker and no admin rights.
- **`gh`**, authenticated with the `workflow` scope, for pull requests and CI.
- **A Swift toolchain** only if the parity goldens need regenerating:
  `./build/swift-parity/generate.sh ../StrangeTerm`.

## Things that have cost time before

- **Merging a stacked pull request merges it into its base**, not into `main`,
  and the commits are then stranded on a branch. It has happened three times.
  Open every pull request against `main` and rebase instead of stacking.
- **Windows is where the surprises are.** The gate has caught: `AppendLine`
  building a block that leaves the machine — the assistant's context block and
  the orchestrator's collation message are read by a provider and describe a
  *remote* host, so a Windows user asking about a Linux server was sending CRLF
  for its output while everyone else sent LF; the rule is `string.Join('\n', …)`
  for anything that crosses the wire, Enter sent as a
  line feed (typed but never run — a Unix pty translates it, Windows does not),
  `ssh-keygen` given a mangled empty passphrase by PowerShell quoting, a
  `window-change` sent before the server had a pty, and a credential service
  name that must be a URI there and a plain string on macOS.
- **Reading the screen is only safe once the screen is up to date.**
  `TerminalSession.Show` puts the prompt back under whatever it writes, and to
  do that it reads the line the shell is sitting on. The bytes it feeds reach
  the engine when *the reader* gets to them, on another thread -- so the
  assistant narrating twice in a row (a command going out, its output coming
  back) had the second call read a screen the first call's prompt had not
  reached yet. It saw no prompt, so it neither erased nor restored one, and the
  pane was left showing output glued to a stale prompt with nothing underneath:
  the hung-looking terminal the restore exists to prevent. It passed every run
  on a developer's machine for two milestones and failed on a loaded CI runner,
  because what decides it is how far behind the reader is. The fix is that
  `Show` queues, and the work happens when the feed says the engine has caught
  up (`FeedStream.IsIdle`, acknowledged by `Consumed`).
  `ShowTests.AndWithNobodyReadingInBetween` is the regression test, and the
  older test beside it is the one that missed it: it drained the stream between
  the two calls, and draining is exactly what nothing in the app does.
- **A wait is only as good as the state it waits for.** Three tests in a row
  here waited for something that is true in the *middle* of the sequence they
  were asserting on -- "exit 0" arrives before the prompt is back under it, and
  a prompt at the end is already there after the first line. Each passed until a
  machine was slow enough to look between the two. If a test waits for one thing
  and asserts another, it is a race with a timeout on it.
- **The workspace has two demo flags of its own.** `--demo-workspace` is the
  pane over an invented project, and `--demo-file-write` is the surface that
  most needed looking at: the assistant stopped at a write, with the lines it
  would change in the approval bar. It needs a folder, a key and a model that
  decides to change a file, so without a fixture it is the one gate nobody could
  ever see. Looking at it is what found the row saying "It writes
  conf/nginx.conf." without saying how much of it changes, and an editor tab
  that never said which of four files you were typing into.
- **UI work needs looking at, not only testing.** The Avalonia DevTools MCP
  attaches to a **Debug** build (`WithDeveloperTools()` is inside `#if DEBUG`)
  and can click, type and screenshot — but its synthetic clicks do **not** open a
  flyout — nor send a modifier, so ⌘K is out of reach too. That is why the
  dialogs are also reachable as
  `--demo-editor host|folder|credential|credentials|snippet|snippets|run|settings`, a
  window of their own over a fixture, and the palette as `--demo-palette
  [--demo-query x] [--demo-connect host]`. The assistant panes have four of their
  own — `--demo-assistant`, `--demo-orchestrator`, `--demo-orchestrator-idle` and
  `--demo-plan` — because they need a real connection *and* a real API key, and a
  surface nobody can look at is a surface nothing is found in. Looking at them
  found four things every test had passed through: the row-identity bug above,
  raw fence characters rendered into the answer, findings listed in whichever
  order the hosts happened to finish, and mode buttons that never said which mode
  was in use. `--demo-connect` opens a shell before
  the palette, because half of what the palette offers needs one — a snippet has
  nowhere to be typed without it. The credential fixture keeps its secrets in
  memory, so looking at it never writes to the login keychain. Splits found
  the sharpest example yet: rebuilding the pane layout detached each terminal
  and re-attached it, which tears its connection down, and the two panes then
  read one stream and each drew half of it. Every test passed. Running `tty` in
  both halves against a real sshd is what showed it. It has found something in every UI change
  so far — a terminal attached before its control was loaded, a focus that went
  to a templated wrapper rather than the view inside it, field values drawn with
  a null brush and so invisible. `STRANGESHARPTERM_TRACE=1` turns on Avalonia's
  log, which a windowed app otherwise sends nowhere.
- **Count the connections.** `sshd.log` in the directory `./build/local-sshd.sh`
  prints says how many times a host was authenticated to: one `Accepted
  publickey` line per connection, with a `Starting session` line per channel on
  it. Everything the window does rides one session per host, except SFTP, which
  SSH.NET insists on owning — so a host with a shell, tunnels, a dashboard and a
  file browser open shows exactly two.
- **`STRANGESHARPTERM_INVENTORY`** points the app at a throwaway inventory, which
  is how the window gets tested without touching a real one. `preferences.json`
  is written beside it, so a test run's theme choice is throwaway too.
- **A view that resolves a resource from the application cannot be built
  without one.** Moving `IconConverter` from each view's own resources to
  `App.axaml` looked like tidying, and broke every plain unit test that
  constructs a pane: they have no `Application`, and `{StaticResource Icon}`
  threw at load. Each view declares its own; the duplication is the price of a
  control that stands up alone.
- **A view is the wrong place to register anything.** Terminal sessions joined
  the `TerminalRegistry` in `TerminalPaneView`'s constructor, so the registry only
  knew about a session if its real control happened to be built — which made
  "which shell has the focus" unanswerable from a view model, and silently false
  in any test that substitutes the pane view. The shell opens the session, so the
  shell registers it.
- **A constant being right is not the wiring being right.** Three kinds of
  secret live in three keychain services on purpose — a connection passphrase, a
  provider API key, a tool server's token. The constants said so, a test asserted
  the three names differ, and the settings sheet wrote an API key to the right
  one. The backend factory then read it from the *connection* store, so every
  saved key was invisible and every pane said "No API key". The MCP tokens had
  the same shape of mistake, consistent in both directions and therefore silent.
  No test followed a secret from where it is written to where it is read; that is
  the test that was missing, and asserting on the constants was what made it look
  covered.
- **A valid signature says nothing about the app starting.** An ad-hoc bundle
  signs, passes `codesign --verify --deep --strict`, satisfies its designated
  requirement — and then dies on launch, unable to open `libhostfxr`, because a
  process loads only libraries whose Team ID matches its own and ad-hoc
  signatures have none. `--version` exists so the packaging scripts can start the
  runtime and exit without opening a window; the alternative was a smoke test
  that launched the real app and hung the build.
- **Everything under `Contents/MacOS` is code to codesign**, managed assemblies
  and all — and codesign names the first unsigned *subcomponent* it reaches,
  which was a `.dll` when the real problem was an unsigned Mach-O with no
  extension. Two hundred and forty-five signatures, each with a network
  timestamp, also took twenty minutes before they were batched and the pointless
  timestamps dropped.
- **A default interface method is a silent answer.** `ICommandGate` gained a
  second `Allow` for connected tool calls, defaulting to no, so that an
  implementation written before them could not accidentally approve one. Two
  forwarding gates in the app implemented only the command half and therefore
  inherited that default — every tool call was refused, and a refusal looks
  exactly like a person refusing. Nothing failed; the window test that watches
  for the approval bar is what found it. A safe default makes the failure quiet,
  so anything forwarding an interface has to forward all of it.
- **A record is the wrong key for a row that changes.** The assistant pane kept
  its rows in a dictionary keyed by the transcript entry. A step is a record, so
  its hash changes the moment its state does — the row for a command became
  unfindable exactly when the command finished, and every command on screen said
  "running" forever. Every test passed: they asserted after the run, when the
  container was built once at the final state. `--demo-assistant` showed it in a
  second. Reference equality is the fix; the lesson is that a value type is a key
  only while its value is fixed.
- **A binding path through a null is three errors, not one.** The orchestrator's
  approval banner bound `Waiting.Host`, `Waiting.Command` and `Waiting.Reason`,
  and with nothing waiting — the ordinary state — each logged a binding error.
  Making the pending command the banner's `DataContext` makes the empty case
  silent, which matters because a trace full of harmless errors is a trace nobody
  reads.
- **`$parent[ItemsControl]` finds the nearest one.** An answer's staged commands
  are an `ItemsControl` inside the rows `ItemsControl`, so a command bound that
  way reached the inner one and resolved to null. `$parent[UserControl]` is
  unambiguous and is what these views use.
- **`--` is illegal inside an XML comment**, which is a problem in this repo
  because every other comment here uses it as a dash. A `Directory.Packages.props`
  with one in it fails restore with `NU1015: no version specified` for every
  package in the file — an error that points nowhere near the real one.
- **A global style beats an inherited state.** `TextBlock { Foreground }` in
  `Styles/Chrome.axaml` applies to the label inside a button too, so a disabled
  button's own foreground never reached it and every disabled button in the app
  looked live. The fix is a `:disabled TextBlock` style; the lesson is that a
  style on a bare type name reaches inside controls it was never meant for.
- **Colours are measured, not chosen.** The Swift source is not on this machine,
  but `docs/reference/screenshots/` is, and `build/theme/sample.py` reads the
  palette back out of the pixels. See `docs/adr/0004`; two values in it are
  provisional and want checking against `ThemePalette.swift`.
