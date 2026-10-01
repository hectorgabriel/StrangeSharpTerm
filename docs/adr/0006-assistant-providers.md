# One SDK, one hand-written client, and one seam between them

## Status

Accepted, in M6.

## Context

The Swift app talks to both of its providers — Claude and DeepSeek — over raw
`URLSession`, with two hand-written stream decoders and two request builders.
The migration plan takes that at face value and lists "both stream decoders"
among M6's deliverables.

The reason Swift did it that way does not survive the port. There is no official
Anthropic SDK for Swift; there is one for C#. The Swift README's own argument —
"one endpoint each is a smaller thing to own than a dependency in the path of the
user's API key" — was a choice between a hand-written client and a third-party
one, not between a hand-written client and the vendor's.

What makes this worth an ADR rather than a shrug is that the request shapes this
app depends on are exactly the ones that move. Extended thinking went from a
token budget to an adaptive mode and the old form is now a 400. A server-side
refusal became a stop reason rather than an error, which is a different thing to
show a person. A tool call streams as partial JSON that has to be assembled
before the gate can judge it. Each of those is a place where being a year out of
date is a bug that only shows up against the live API — which is precisely the
API this project cannot test against, because it has no account.

## Decision

**Claude goes through the official `Anthropic` package.** `ClaudeBackend`
translates in and out of it and owns nothing about the wire.

**DeepSeek stays raw HTTP.** It is OpenAI-shaped, it has no SDK here, and its
endpoint is one POST. `DeepSeekBackend` builds the body, `Sse` reads the stream
and `DeepSeekStream` decodes it — the ported decoder the plan asked for, with its
recorded-stream tests.

**`IAssistBackend` is the only seam.** Everything above it — the transcript, the
redaction, the context block, `CommandPolicy`, the agent loop, the orchestrator,
run plans — knows neither provider. Two files in the whole app name one.

The asymmetry is the point and not a compromise: each provider is reached the
best way it can be reached, and the seam is what makes that invisible to
everything else.

## How both are tested without an account

Both run end to end against a stub that returns a stream recorded from the real
thing and keeps the request it was sent. The SDK is given a stub `HttpClient`;
`DeepSeekBackend` is given one too. Same fixtures directory, same shape of test,
and what each asserts is the translation either side of the wire:

- the system prompt is a top-level field for Claude and the first message for
  DeepSeek;
- tool results are content blocks on a user message for Claude and messages of
  their own for DeepSeek;
- reasoning is a `thinking` block for Claude and `reasoning_content` on a delta
  for DeepSeek;
- a refusal is `stop_reason: refusal` for Claude and `finish_reason:
  content_filter` for DeepSeek, and neither is an error.

`STRANGESHARPTERM_ASSIST_ENDPOINT` points either one somewhere else, so the same
check can be run against a stub server by hand.

## Two DeepSeek details kept from the Swift original

Both were taken from a working client rather than from documentation, which was
unreachable, and both are load-bearing:

- **V4 models require a `thinking` parameter and earlier ones reject it**, so it
  is sent only for `deepseek-v*` names where the digit is 4 or higher. That is an
  inference about a naming scheme, and it is wrong the day a name breaks the
  pattern. It is a pure function with its own test, and the endpoint override is
  the escape hatch.
- **`max_tokens` is not sent.** The documented ceiling has moved with every model
  generation, a value above it is a 400, and sending none lets the server apply
  its own.

## Consequences

- `Directory.Packages.props` gains `Anthropic`, pinned like everything else.
- Claude's model list, thinking mode and refusal handling follow the SDK rather
  than a file here. When they move again, the fix is a version bump.
- One decoder is ported rather than two. The one that is ported is the one with
  no vendor keeping it current, which is the one worth owning.
- A third provider is a third file and no change anywhere above the seam.
- If the SDK ever becomes the wrong dependency — a licence change, an
  unmaintained package — `DeepSeekBackend` is the worked example of replacing it,
  and the recorded streams already say what the replacement has to produce.

## Gemini, added later

**Gemini goes through Google's official `Google.GenAI` package**, by the rule
above: a vendor SDK where there is one. It was weighed against Gemini's
OpenAI-compatible endpoint, which `DeepSeekBackend` could almost have spoken
as it is, and lost on the one detail that matters for tool use.

A Gemini 3 tool call carries a **thought signature**, an opaque value that has
to go back on the same part, exactly as it came, or the next request is a 400.
That is state about a call the seam had no room for, so `AssistToolCall` gained
an optional `Signature`. Claude and DeepSeek never set it; everything above the
seam carries it without knowing what it is, because a call is passed whole from
the event that announced it to the transcript that replays it.

Three further details live in `GeminiBackend` alone:

- Of parallel calls only the first is signed, and that is the one checked. A
  turn with no signature at all, such as one from a 2.5 model, gets Google's
  documented placeholder (`skip_thought_signature_validator`) on its first call.
- Tool schemas go under `parametersJsonSchema`, which takes JSON Schema whole.
  The older `parameters` takes an OpenAPI subset and rejects
  `additionalProperties`, which a connected tool's schema is free to use.
- Gemini ends a turn of calls with a plain `STOP`, so the backend reports
  `ToolUse` whenever a call arrived. A bad key is a 400 here, not a 401, and is
  recognised by its message.

The client is built with `vertexAI: false` said out loud. Left unset, the SDK
reads it from the environment, and a variable meant for another tool would send
this key and this terminal's output somewhere other than where Settings says.

**Tested the same way, with one difference.** The stub is handed to the SDK
through `ClientOptions.HttpClientFactory`. The `gemini.*` fixtures, unlike the
others, were written from Google's documented wire format rather than recorded,
because there was no account to record one with. Replacing them with a real
recording is worth doing the first time one is to hand.
