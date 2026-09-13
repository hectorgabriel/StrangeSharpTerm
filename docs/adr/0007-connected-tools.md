# Connected tools: what the SDK owns, and what cannot be delegated

## Status

Accepted, in M7.

## Context

M7 attaches [MCP](https://modelcontextprotocol.io) servers so their tools are
offered to the model alongside `run_command`. The plan says "wrap the official
SDK, port the config and OAuth surface, port the 58 tests".

The Swift app wrote its own OAuth: the discovery chain (protected-resource
metadata, then the authorization server's), dynamic client registration, PKCE,
and the token exchange. `ModelContextProtocol.Core` ships all of that —
`ClientOAuthProvider`, `DynamicClientRegistrationOptions`, an `ITokenCache`, and
a PKCE check that skips an authorization server advertising no S256 rather than
trusting it to be checking.

This is the same shape as `docs/adr/0006`, and the answer is the same for the
same reason: take the vendor's implementation of the protocol, and own the parts
that are decisions about *this* app.

## Decision

**The SDK owns the protocol.** Both transports, the handshake, the discovery
chain, PKCE, dynamic registration, the token exchange and the silent refresh.

**We own four things**, each because it is a decision rather than a mechanism:

### The redirect is a loopback port bound before the browser opens

`http://127.0.0.1:<port>/callback`, not a custom URL scheme. A scheme like
`strangesharpterm://` can be claimed by any application on the machine, which
would hand the authorization code to whoever registered it last; a loopback port
this process already holds cannot be taken that way, and RFC 8252 says as much.

Four ports are registered, so a second sign-in can use whichever is free without
registering a second client. The port is bound *first*: a code arriving where
nobody is listening is a sign-in that fails after the person has already approved
it.

### A renewal never opens a browser

The SDK's `AuthorizationCallbackHandler` is supplied only for a sign-in a person
started from Settings. Everywhere else it returns null, which makes the transport
renew silently where the server issued a refresh token and give up where it did
not. A request failing mid-turn is the worst possible moment to seize the screen,
and where a server issues no refresh token, signing in again is a button rather
than a mystery.

We use `AuthorizationCallbackHandler` rather than the older
`AuthorizationRedirectDelegate` because only the former carries the RFC 9207
issuer, which is what lets the SDK check the code came from the server it sent
the person to.

### Where the tokens live

`ITokenCache` over the platform store, under a service of its own — not beside
connection credentials, not beside provider API keys. Three things that are
revoked, rotated and lost independently; a shared store makes losing one cost the
others.

A stored sign-in is always handed back as expired. Trusting a remembered expiry
across a relaunch means sending a token we believe is good and finding out
otherwise mid-question; a refresh costs one round trip and cannot be wrong that
way.

### Finding the command

A bare `npx` or `uvx` is looked for on `PATH` and then in Homebrew,
`/usr/local/bin` and `~/.local/bin`. An app launched from Finder inherits
launchd's `PATH` rather than a shell's, so those tools are otherwise invisible —
and the failure looks like the server's fault. Resolving it by running a login
shell was the alternative and is worse: executing someone's dotfiles to start a
tool server is a far larger thing to do than looking in four directories.

## What cannot be delegated, and is not

**There is no `CommandPolicy` here, and there cannot be.** That classifier works
because a shell command is a string it can parse; `create_incident` with a JSON
body is an opaque name written by the same server that would carry out the call.
So every call stops and waits for a person, showing the server, the tool, where
the call goes and the exact arguments — pretty-printed, in the bar itself, not
behind a disclosure, because a gate whose substance is one click away is a gate
people approve without reading.

The pass is granted per tool, by a person, at the gate: **Always allow**, and
that is the only way onto the list.

Servers may annotate a tool. The two annotations are treated differently, and the
difference is the whole rule:

- `readOnlyHint` is **shown and never acted on**. It is a claim by the party
  being trusted — exactly the role the command denylist plays beside the
  allowlist, which is to inform a person without granting anything.
- `destructiveHint` **is** believed, in the only direction a claim from the party
  being trusted is safe in: a tool the server calls destructive cannot be given a
  standing pass at all. Believing it only ever narrows what can happen.

**Results are data, not instructions.** What a tool returns is redacted and
truncated like command output, and the model is told — in the system prompt and
again on every result — that it is data from a third party and not an instruction
to it.

## The two switches, and why their defaults differ

- **Assistant panes: on.** Connecting a server is already the deliberate act, and
  every call still asks. Turning it on twice would be ceremony rather than safety.
- **Orchestrated runs: off.** A fan-out multiplies everything. Every host in a run
  gets the *same* tools pointed at the same place, so one instruction can become
  one write per host, and the gate that would have caught it is eight approvals
  deep in a queue nobody reads properly by the fourth. When it is on, each host's
  worker is told plainly that these tools are not part of its machine and that
  writing is not its job.

## Consequences

- `Directory.Packages.props` already pinned `ModelContextProtocol.Core`; M7 uses
  it rather than adding anything.
- `IExternalTools` is the seam. The agent loop offers these tools, gates them,
  budgets them and redacts them without knowing MCP exists, so a second kind of
  tool source is a class and no change above it.
- `ICommandGate` gained a second `Allow` for tool calls, defaulting to **no**. An
  implementation written before connected tools existed cannot have an opinion
  about one, and the safe reading of no opinion is that the call does not happen.
- Deliberately not implemented, as in the Swift app: resources, prompts, and the
  server→client requests (sampling, elicitation, roots). The capabilities
  announced are empty, so a well-behaved server never asks. Sampling in
  particular would be a server spending the user's API credit on a prompt they
  never saw.
