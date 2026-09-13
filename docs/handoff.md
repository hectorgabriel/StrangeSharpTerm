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
| **M5** (part) | one authenticated session per host, at last: `HostSessions` over the M2 pool, which every pane goes through. The SFTP browser (`IRemoteFiles`, a pane, ⇧⌘B), tunnels (`ITunnels`, a pane that starts and stops a host's forwards and releases the ports), the dashboard (`IServerHealth` over the M2 probe, in the detail pane, asked for rather than assumed), the credential library (a key or password described once, pointed at from any host or folder, with the secret in the platform store and never in the inventory), and snippets (saved commands, scoped to a folder or offered everywhere, run from the palette into the focused shell) |
| **M4** done | the window (sidebar, tabs, host detail, splits, broadcast), the theme system, the host and folder editors, the menu bar, the command palette, and the headless UI driver. `docs/adr/0004` and `0005` record the decisions |

Around 484 tests, all green on both operating systems.

## In flight

Check `gh pr list` first — a pull request may have landed since this was
written. At the time of writing: **snippets** (branch `m5-snippets`).

## What to do next, in order

1. **The last of M5**: the settings sheet. It should hold the theme picker now
   at the foot of the sidebar, and is the natural home for the credential and
   snippet libraries, which today are reached only from the menu and the palette. Three of the Swift
   app's shortcuts still wait for them — `docs/adr/0005` lists which and why,
   including the one that cannot be translated to Windows as it stands (⌃⌘X).
2. **What the panes do not do yet.** The browser has no rename and no
   new-folder, though `IRemoteFiles` carries both calls, and no progress for a
   large transfer. Tunnels are read from the host's settings and cannot be added
   or edited there — the host editor deliberately leaves forwards alone and
   preserves them, so an editor for them is the missing half. The dashboard is
   asked for one probe at a time; the Swift app refreshed on a timer.
3. **Screenshots**, the other half of what the plan's CI table asks for at
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
- **Windows is where the surprises are.** The gate has caught: Enter sent as a
  line feed (typed but never run — a Unix pty translates it, Windows does not),
  `ssh-keygen` given a mangled empty passphrase by PowerShell quoting, a
  `window-change` sent before the server had a pty, and a credential service
  name that must be a URI there and a plain string on macOS.
- **UI work needs looking at, not only testing.** The Avalonia DevTools MCP
  attaches to a **Debug** build (`WithDeveloperTools()` is inside `#if DEBUG`)
  and can click, type and screenshot — but its synthetic clicks do **not** open a
  flyout — nor send a modifier, so ⌘K is out of reach too. That is why the
  dialogs are also reachable as
  `--demo-editor host|folder|credential|credentials|snippet|snippets|run`, a
  window of their own over a fixture, and the palette as `--demo-palette
  [--demo-query x] [--demo-connect host]`. `--demo-connect` opens a shell before
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
- **A view is the wrong place to register anything.** Terminal sessions joined
  the `TerminalRegistry` in `TerminalPaneView`'s constructor, so the registry only
  knew about a session if its real control happened to be built — which made
  "which shell has the focus" unanswerable from a view model, and silently false
  in any test that substitutes the pane view. The shell opens the session, so the
  shell registers it.
- **A global style beats an inherited state.** `TextBlock { Foreground }` in
  `Styles/Chrome.axaml` applies to the label inside a button too, so a disabled
  button's own foreground never reached it and every disabled button in the app
  looked live. The fix is a `:disabled TextBlock` style; the lesson is that a
  style on a bare type name reaches inside controls it was never meant for.
- **Colours are measured, not chosen.** The Swift source is not on this machine,
  but `docs/reference/screenshots/` is, and `build/theme/sample.py` reads the
  palette back out of the pixels. See `docs/adr/0004`; two values in it are
  provisional and want checking against `ThemePalette.swift`.
