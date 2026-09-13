# The title bar, the menu, and ⌘ on a keyboard that has no ⌘

## Status

Accepted. The Windows title bar decision is deliberately the conservative one
and is marked below as worth revisiting from a Windows desktop.

## Context

The migration plan holds three questions open until the milestone they shape,
and M4 is that milestone for all three: what `.windowStyle(.hiddenTitleBar)`
means on Windows, where the menu bar lives, and how ⌘ shortcuts map to Ctrl.

The Swift app hides its title bar and draws its own chrome up into that space.
`RootView` and `SessionTabBar` hard-code a 26/28pt spacer so nothing lands under
the traffic lights. Its menu is a SwiftUI `.commands` block of about twenty
items, including a `ForEach(1...9)` that generates ⌘1–⌘9, and its keyboard table
(`docs/reference/strangeterm-swift-README.md`) is the parity list.

None of that transfers unexamined. macOS puts window controls at the top left
and owns one menu bar for the focused app; Windows puts them at the top right
and expects a menu inside the window; and ⌘ is a modifier the second keyboard
does not have.

## Decision

### The title bar: extended on macOS, left alone on Windows

On macOS the window sets `ExtendClientAreaToDecorationsHint`, so the traffic
lights float over our own chrome and the sidebar starts at the top of the
window, as the Swift app has it. The sidebar header carries a top inset for
them — the same 26/28pt idea, as a platform value rather than a constant.

On Windows the system title bar stays. Three reasons, in order:

1. **The caption buttons are at the top right, which is where our toolbar is.**
   Minimise, maximise and close occupy roughly 138 device-independent pixels
   there, and Windows 11 adds a snap-layout hover zone over the maximise button.
   Extending the client area would mean reserving that width and keeping it
   correct as the window is maximised and restored.
2. **A window with no title bar has to reimplement one.** Dragging,
   double-click to maximise, snap layouts, the right-click system menu, and the
   resize borders all stop being free. That is a real piece of work in exchange
   for a look Windows users do not ask for.
3. **Nobody can look at it.** The platform strategy says window chrome needs a
   person at a Windows desktop, and there is not one today. Of the two answers,
   this is the one that cannot go subtly wrong unseen: a standard title bar is
   either there or it is not.

**Worth revisiting** at a Windows machine. If the extended look is wanted there,
what it needs is a right-hand inset of the caption width and a drag region — not
a change to anything else here.

### The menu: one definition, two places to put it

macOS gets the system menu bar (`NativeMenu.SetMenu` on the application).
Windows gets a `NativeMenuBar` control at the top of the window, under the
system title bar. Avalonia renders the same `NativeMenu` either way.

Both are built from one list — `CommandCatalogue` — which also feeds the command
palette and the window's key bindings. The Swift app declared its shortcuts in
three places (the `.commands` block, the palette, and view-level
`.keyboardShortcut` modifiers); one list is what keeps a shortcut, its menu item
and its palette entry from drifting apart.

### ⌘ becomes Ctrl by asking the platform, not by guessing

Every gesture is built from
`PlatformSettings.HotkeyConfiguration.CommandModifiers` — `Meta` on macOS,
`Control` on Windows — so ⌘K is Ctrl+K there, ⇧⌘D is Ctrl+Shift+D, and ⌘1–⌘9
are Ctrl+1–9. Alt is Alt on both, so ⌥⌘B is Ctrl+Alt+B.

One shortcut does not survive the mapping: **⌃⌘X (disconnect)** would become
Ctrl+Ctrl+X. It belongs to a feature that is not built yet, and when it lands it
needs a Windows gesture of its own rather than a translation. The same check
applies to anything else that uses Control as a second modifier on macOS.

### What is bound now, and what is not

Bound: ⌘K palette, ⌘T new session, ⌘D and ⇧⌘D split, ⌘W close pane, ⌘1–⌘9 tabs,
⌥⌘B broadcast, ⌘N and ⇧⌘N new host and folder, ⌘E edit.

Not bound, because the thing they do does not exist yet, each waiting for its
milestone: ⇧⌘B (SFTP, M5), ⌥⌘A and ⇧⌥⌘A (assistant, M6), ⌃⌘X (disconnect, M5,
and see above). ⌘0 (host details) is left out for a different reason: the detail
pane here appears when nothing is open or when the selection differs from the
focused pane, so there is no state for ⌘0 to put the window into that a
selection does not already describe. If that changes, it comes back.

## Consequences

- The window is not the same shape on both platforms, and that is the point: on
  each one it is the shape that platform expects.
- A shortcut is declared once. A menu item without a command is impossible,
  because the menu is generated from the commands.
- The palette is a filter over the same list, so anything that can be done from
  the menu can be found by typing part of its name.
- `ExtendClientAreaToDecorationsHint` on macOS means the traffic lights sit over
  our sidebar: its header, and nothing else, has to keep clear of them.
