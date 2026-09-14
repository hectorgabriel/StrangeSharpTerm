# Packaging, and what a free Apple account cannot buy

## Status

Accepted, in M8. The Developer ID and notarisation half is written and unrun:
it needs a paid account, and this records what is proven and what is not.

## Context

Escaping Xcode does not escape notarization. Distributing to another Mac still
means Developer ID plus `codesign --options runtime` plus `notarytool submit
--wait` plus `stapler`, and the Swift app's `Scripts/package.sh` already did all
of that correctly. The plan says its *sequence* is worth transcribing rather than
rediscovering, and that is what `build/package.sh` does.

What is new is the runtime. A self-contained .NET publish is not one binary; it
is an apphost, about twenty native libraries and two hundred managed assemblies,
and the JIT compiles code into memory and runs it — which the hardened runtime
forbids by default.

And there is a question the plan did not have to ask, because the Swift app had a
paid account: **what can be done without one?**

## What a Personal Team can and cannot do

A free Apple ID enrolled as a Personal Team issues **Apple Development**
certificates only. It cannot issue a **Developer ID Application** certificate,
and notarisation is gated on paid membership — `notarytool` authenticates against
a paid team.

|  | Personal Team (free) | Developer Program |
|---|---|---|
| Build and run on your own Mac | yes | yes |
| Hardened runtime and the JIT entitlements | yes | yes |
| Runs on another Mac | no | yes |
| Notarise and staple | no | yes |

The good news is specific to this project: the two entitlements the .NET JIT
needs — `com.apple.security.cs.allow-jit` and `allow-unsigned-executable-memory`
— are **hardened-runtime entitlements and need no provisioning profile**. That is
a different category from the File Provider and keychain-sharing entitlements
that blocked the Swift app's M8 and were part of why this rewrite happened. None
of that recurs here, whichever account is used.

## Decision

**Two paths, one script.** `build/package.sh` ad-hoc signs by default and takes
`--sign-with` and `--notarize` when there is an account. `build/install.sh`
installs to `/Applications` for your own machine and needs nothing. The
notarisation path is written, gated behind a credential check, and has never
run — which is stated where someone will read it rather than discovered.

**Ad-hoc is the floor, not "unsigned".** On Apple Silicon a binary with no
signature at all will not execute, so there is no unsigned option to choose.

**One disk image per architecture**, rather than `lipo`-ing a universal binary.
The plan offers both. Two images are honest and simple; folding two
self-contained runtimes into one means lipo-ing every native library and getting
each one right, for a saving that matters to a download size and to nothing else.

## Three things the failures taught, which are the point of writing this down

**Everything under `Contents/MacOS` is code to codesign** — not only the Mach-O
files but the managed assemblies and anything else living there. A .NET publish
must put its payload beside the apphost, so all of it lands there and all of it
must be signed. Signing only the dylibs fails on the first managed `.dll`;
signing the `.dll`s too fails on the next thing along. The script signs every
file, inside out, because a nested file signed after the bundle invalidates the
bundle.

**A valid signature says nothing about the app starting.** A process loads only
libraries whose Team ID matches its own. An ad-hoc signature has no Team ID, so
an ad-hoc bundle signs, verifies against `--deep --strict`, satisfies its
designated requirement — and then dies on launch, unable to open `libhostfxr`.
The fix is `com.apple.security.cs.disable-library-validation`, added *only* for
ad-hoc builds: a Developer ID build signs every library with one identity and
does not need it. The script checks the Team IDs actually agree when library
validation is on, and runs the app with `--version` either way.

`--version` exists for that check. It starts the host, loads the runtime and
exits without opening a window, which is the part that breaks and the part that
is otherwise awkward to test.

**A timestamp is a network round trip per signature.** Two hundred and
forty-five of them took twenty minutes. It is required for notarisation and
worthless on an ad-hoc signature nothing will trust, so it is spent only where it
buys something, and codesign is given files in batches. The same package now
takes twenty seconds.

## Windows

A zip, signed with Authenticode when a certificate is given. That is a second
signing identity from a different vendor and a second annual renewal; without it
the app runs and SmartScreen warns whoever downloads it, which is the counterpart
of Gatekeeper refusing an ad-hoc bundle.

**MSIX is deliberately not taken yet.** It needs the same certificate *and* a
packaging identity, and a zip is the thing that can be produced and tested today.

Every native binary is signed, not only the executable: SmartScreen judges what
was launched, but an unsigned DLL beside a signed exe fails an enterprise policy
long after it shipped, and it costs nothing to do now. The timestamp there is not
optional — without it every signature stops verifying on renewal day.

## Consequences

- CI packages on both platforms on every push, unsigned. Everything up to the
  credentials is exactly what a release does, and it is the half that breaks
  silently: a publish that cannot load its own runtime now fails in CI rather
  than after notarisation.
- `artifacts/` is where builds land and is not in version control.
- **There is no app icon.** The Swift app's `ssh_app_logo.svg` is not in this
  repo. `build/make-icon.sh` turns one into both formats when it arrives; until
  then the bundle takes the system default, which is a cosmetic gap and not a
  broken build.
- If a paid account is bought later, it is a certificate and two stored
  credentials, not a rewrite: `--sign-with` and `--notarize` are already there,
  and the sequence behind them is the Swift app's, which worked.
