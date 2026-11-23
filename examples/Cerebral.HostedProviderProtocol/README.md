# Shared hosted-provider protocol sources

This directory contains **source-linked implementation helpers**, not a separately installable plugin, SDK assembly or HTTP service. The OpenAI and Anthropic projects compile these files into their own assemblies. There is no project, manifest, standalone version or independent build command here. Package identity and vendor behavior belong to the consuming adapter.

See [OpenAI](../Cerebral.OpenAiProviderPlugin/README.md) and [Anthropic](../Cerebral.AnthropicProviderPlugin/README.md) for exact builds and SDK binding. Both require existing compatible Core/Contracts DLLs and default to **unconfigured** through their parameterless constructors.

## Files and responsibilities

| File | Responsibility |
| --- | --- |
| [HostedProviderRuntime.cs](HostedProviderRuntime.cs) | Dispatch, binding/configuration checks, credential resolution, bounded calls, concurrency, cancellation, terminal envelopes and disposal. |
| [ProviderValues.cs](ProviderValues.cs) | Closed profile, model, request and response contracts with scalar bounds. |
| [ProviderJson.cs](ProviderJson.cs) | Bounded JSON validation and the deliberately distinct JSON-string carrier. |
| [BoundedSseReader.cs](BoundedSseReader.cs) | Bounded server-sent-event framing and cancellation-aware reads. |

Vendor subclasses own endpoints, authentication, rendering and event interpretation. Shared mechanics do not make their wire protocols interchangeable.

## Configuration boundary

Profiles contain `id`, `revision`, `enabled`, `credentialReferenceId`, `credentialReferenceRevision`, `models` and `configuredMaxConcurrentRequests`. Credential reference and revision must appear together. There are at most 128 declarations and configured concurrency is 1–16.

Models declare exact key/label/revision, text support, response formats, token limits and provenance, count quality and supported temperature/reasoning choices. Missing provider limits stay unknown rather than guessed. The host must admit actual package generation; a valid JSON profile is not live provider verification.

The host supplies an HTTP handler and a resolver for exact credential reference/revision. Real credentials belong in its user-managed credential/settings mechanism, outside source and packages. This directory implements no credential store, account screen, OAuth flow or ambient environment-variable lookup.

## Operations and failure semantics

Consumers expose `<package-id>.model-provider.v1.describe`, `.count`, `.generate` and `.reconcile`. Envelopes are closed and versioned. Description can report unconfigured without a network call. Count/generate require enabled configuration and an admitted binding. Neither current consumer supports remote reconciliation.

A terminal records call identity, request fingerprints, outcome, whether sent, terminal/cancellation confirmation, visible text, schema guarantee, usage, receipt, failure code and format. Completed, length-limited, refused, failed, cancelled and unknown results differ. Stale configuration, unsupported capability and malformed transport also differ.

The runtime has a five-minute call bound, immediate bounded concurrency admission, JSON/schema/message/output limits and explicit cleanup. For supported standard HTTP handlers it disables redirects, cookies and default credentials. An integrating host remains responsible for a custom handler. The optional best-effort private visible-fragment callback cannot invalidate the authoritative awaited terminal.

Cancellation before send differs from uncertainty after send. There is no automatic retry or reconciliation that makes unknown provider work safe to replay. JSON-string carrier output keeps its weaker guarantee instead of being relabeled provider-enforced schema.

## Privacy and dependencies

Generation sends admitted text to a remote provider and can incur charges. Only public visible text is intended for the optional callback; provider reasoning is not an answer channel. No local logger here establishes a guarantee about all host or provider logs. Never print credentials, private prompts or response bodies in integration diagnostics.

These sources use .NET and the host SDK, with no weights or vendor runtime bundled. First-party code follows the repository [LICENSE](../../LICENSE); remote services have independent terms. See [Build](../../docs/BUILD.md), [Setup](../../docs/SETUP.md) and [Plugin installation](../../docs/PLUGINS.md). This description is not live provider qualification; use owned HTTP fixtures before an explicitly authorized account test.
