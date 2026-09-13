# Theme colours, measured from the Swift app's screenshots

## Status

Accepted. Two of the values are provisional and marked as such below.

## Context

The Swift app has two themes — **StrangeTerm Dark** and **Dracula** — and
`ThemePalette` holds each one's surfaces, borders, accent and swatches. Only the
terminal half of that was ported in M3 (`TerminalPalette`: background,
foreground, cursor, sixteen ANSI colours), because that is all a terminal needs
and `stctl` has no window. M4 needs the other half, and the window has been
Fluent's default grey while its terminals were themed.

The problem is provenance. `ThemePalette.swift` is not on this machine — the
Swift repository is not checked out here — so the values cannot simply be
copied. What is here is `docs/reference/screenshots/`: five screenshots taken
from the running app, at 1:1, lossless.

A colour on a screenshot is the colour the app drew. Reading them back is not a
guess; it is a measurement.

## Decision

**Measure what the screenshots show, derive the rest by rules the same
screenshots justify, and keep both checkable.**

`build/theme/sample.py` decodes the PNGs, re-reads every measured value, and
fails if one no longer matches what `AppPalette.cs` claims. `ThemeTests`
re-computes every derived value from the measured ones, so a derivation is a
line of code rather than a line of prose.

### What was measured

Every screenshot is in **Dracula** — `settings.png` shows it selected — so all
of Dracula's chrome is read directly: rail `#191A21`, sidebar `#21222C`,
background `#282A36`, surface `#343746`, border `#424450`, selection `#353147`,
text `#F8F8F2`, muted `#6272A4`, accent `#BD93F9`, on-accent `#191A21`, warning
`#FFB86C`, danger `#FF5555`.

Two of those readings needed care:

- **Text is measured off 15pt body text, not a 20pt heading.** macOS
  gamma-boosts the core of a large bold glyph past the colour it was asked for;
  the heading reads `#FFFFF8` where the colour is `#F8F8F2`.
- **A progress bar is seven pixels tall**, so its flat core is three rows. The
  sampler refuses to read a block that is not one single colour, which is what
  keeps an anti-aliased edge out of a measurement.

**StrangeTerm Dark** appears in one place only: its row in the theme picker
draws its own surfaces and accent as four squares. That gives sidebar `#16181D`,
background `#1A1D23`, surface `#21252E`, accent `#2ED3A0` — and the same four
squares in the Dracula row give exactly the colours measured from the window
around them, which is what says the squares mean surfaces at all.

Two independent cross-checks agree, and neither was arranged:

- `1A1D23` and `282A36` are each theme's **terminal background** in the ported
  `TerminalPalette`, and `2ED3A0` is StrangeTerm Dark's **cursor**. The swatch
  and the terminal colours were ported by different routes and match.
- A host badge in the sidebar is a colour at **16% over the sidebar**. Solving
  the tints back out returns `#50FA7B`, `#FF5555`, `#FFB86C`, `#BD93F9` and
  `#6272A4` — the palette, arrived at from a different set of pixels.

### What was derived, and how

StrangeTerm Dark's remaining roles use Dracula's own relationships:

| Role | Rule | Value |
|---|---|---|
| Rail | its sidebar × the ratio Dracula's rail has to its sidebar (0.758, 0.765, 0.750) | `#111216` |
| Border | its surface × the same ratio Dracula's border has to its surface | `#2A2E35` |
| Selection | its accent at 13% over its sidebar — the alpha that reproduces Dracula's measured `#353147` exactly | `#19302E` |
| On-accent | its rail, as Dracula's button label is Dracula's rail | `#111216` |
| Muted | its background raised to the luminance of Dracula's comment colour, keeping its own hue | `#67738B` |

The muted rule is the weakest of these and is **provisional**: applied to
Dracula's own background it gives `#6C7191` against a measured `#6272A4` — the
right depth, a plainer hue, because Dracula's comment colour is a published
colour rather than a shade of its surfaces. StrangeTerm Dark's muted text is
therefore the right weight and possibly not the right hue.

**Semantic colours do not vary by theme.** Success `#50FA7B`, warning `#FFB86C`
and danger `#FF5555` are measured from the screenshots and used by both. Red
means danger in both themes, and inventing a second red for the theme that
cannot be measured would be invention without a reason. This is the other
**provisional** decision.

Both provisional values are the first thing to check against `ThemePalette.swift`
on a machine that has the Swift source. The tests pin them, so correcting one is
a deliberate edit rather than a drift.

## How a theme reaches the screen

`ThemeTokens.Apply` writes the palette into `Application.Resources` as a `Color`
and a `Brush` per token; the markup names them with `{DynamicResource}`. Avalonia
re-evaluates those when the dictionary changes, so switching a theme is a
dictionary write.

This is why **the `.id(themeID)` redraw hack has no counterpart**. In SwiftUI,
`Theme` was a static that could not be observed, so `RootView` keyed each piece
of chrome by theme id to force a rebuild — and that keying was deliberately kept
away from terminal panes, because rebuilding one would restart the ssh session
inside it. Here nothing is rebuilt at all. A terminal is recoloured through
`IThemedPane.Apply`, which sets the control's brushes and the engine's sixteen
ANSI colours in place, on the pane that is already running.

Two smaller consequences:

- **The app is `RequestedThemeVariant="Dark"`.** Both themes are dark, so
  following the system variant would light Fluent's controls inside a near-black
  window.
- **Fluent's accent is set through `ColorPaletteResources.Accent`**, the one
  palette entry Fluent re-reads at runtime; the rest are read once at startup,
  which is why the chrome is painted from our own tokens rather than through
  Fluent's palette.
- **Fluent's own controls are themed by redefining its resource keys**
  (`ThemeTokens.Fluent`). A text box does not paint itself from its `Background`
  property: its control theme binds each state to a key of Fluent's own, so a
  field went black under the pointer whatever the control was styled with.
  Styling the template part does not help either — a control theme's state
  setters beat an application style, checked in Avalonia 12.1 rather than
  assumed. Where a variant needs its own states, as the accent button does, the
  way in is a `ControlTheme` based on Fluent's (`Styles/Controls.axaml`).

## A host that names a theme keeps it

`terminalTheme` is a per-connection setting in the model, inherited from folders.
A host — or a folder — that names one keeps it when the app theme changes;
everything else follows the app. Otherwise choosing a theme would silently
discard a setting someone set deliberately, host by host.

## Consequences

- The measured values are checkable by anyone with the repository:
  `python3 build/theme/sample.py`.
- The derived values are testable, and are re-derived on every test run rather
  than trusted.
- `Rail` is measured and defined but nothing paints it yet; the icon rail arrives
  with the feature panes in M5.
- The theme choice is remembered in `preferences.json`, beside the inventory —
  the Swift app kept it in `UserDefaults`, so there is no file format to match
  and none to stay compatible with.
