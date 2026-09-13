# Where things stand

A snapshot for picking the work up on another machine, or after a gap. Written
2026-09-12. `docs/migration-plan.md` is the authority on what each milestone
means; this says only what is done, what is in flight, and what to do first.

## Done, on `main`

| Milestone | What it left behind |
|---|---|
| **M0** skeleton and spikes | Solution, CI on both OSes, `spikes/` and `docs/adr/0002`. Both risks closed, including ssh-agent over a named pipe on Windows |
| **M1** model and store | `StrangeSharpTerm.Model` and `.Store` with their 84 tests. The inventory file is byte-identical to the Swift app's, checked against goldens the Swift code itself generates (`build/swift-parity`) |
| **M2** transport | One authenticated session per host carrying commands, shells, forwards and SFTP; host key trust shared with `ssh`; secrets in each platform's own store; `stctl`; the integration gate running against a real sshd on macOS **and** Windows in CI |
| **M3** terminal | `TerminalSession` binding an XTerm.NET engine to an SSH channel, the Avalonia control, broadcast, scrollback, and `stctl terminal` |
| **M4** (part) | `InventoryViewModel`, `WorkspaceViewModel`, the Lucide icon set, the window (sidebar, tabs, a terminal in a pane, host detail), and the theme system |

Around 276 tests, all green on both operating systems.

## In flight

Check `gh pr list` first — a pull request may have landed since this was
written. At the time of writing: **the theme system** (branch `m4-theme`).

## What to do next, in order

1. **Host and folder editors**, through `IDialogService`. The drafts
   (`HostDraft`, `FolderDraft`) and their validation are in the Swift `App/`.
2. **Splits in the window.** `WorkspaceViewModel` already models panes, axes and
   a focused pane; the window shows one pane at a time.
3. **Menu bar and command palette**, with the two Windows questions the plan
   wants answered as ADRs: what the hidden title bar means there, and how ⌘1–⌘9
   map to Ctrl. The theme belongs in the menu once there is one; until then it is
   a picker at the foot of the sidebar.

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
  and can click, type and screenshot. It has found something in every UI change
  so far — a terminal attached before its control was loaded, a focus that went
  to a templated wrapper rather than the view inside it, field values drawn with
  a null brush and so invisible. `STRANGESHARPTERM_TRACE=1` turns on Avalonia's
  log, which a windowed app otherwise sends nowhere.
- **`STRANGESHARPTERM_INVENTORY`** points the app at a throwaway inventory, which
  is how the window gets tested without touching a real one. `preferences.json`
  is written beside it, so a test run's theme choice is throwaway too.
- **Colours are measured, not chosen.** The Swift source is not on this machine,
  but `docs/reference/screenshots/` is, and `build/theme/sample.py` reads the
  palette back out of the pixels. See `docs/adr/0004`; two values in it are
  provisional and want checking against `ThemePalette.swift`.
