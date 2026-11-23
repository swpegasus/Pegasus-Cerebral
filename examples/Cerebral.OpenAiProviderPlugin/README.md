# OpenAI hosted provider adapter

Package `pegasus.openai-provider` **1.0.0** implements the checked-in **`openai.responses.v1`** service protocol. It renders bounded text requests for OpenAI's Responses API, calls the input-token endpoint, reads streamed public text and returns terminal, usage and receipt metadata.

**The default plugin is unconfigured.** Installing its manifest does not add an API account or usable hosted models. The parameterless constructor intentionally lacks a profile, HTTP dependency and credential resolver. This is a protocol adapter for an explicitly integrating model host, not a finished provider-account Configuration screen.

## Build

Use the SDK in repository `global.json`. The project consumes prebuilt `Cerebral.Core.dll` and `Cerebral.Contracts.dll`; it has no SDK project references. Default `CerebralSdkDirectory` is the source Main Release output. Once that matching SDK exists, build from the repository root:

```powershell
$sdk = (Resolve-Path src/Cerebral.Orchestrator/bin/Release/net10.0-windows).Path
dotnet build examples/Cerebral.OpenAiProviderPlugin/Cerebral.OpenAiProviderPlugin.csproj -c Release "-p:CerebralSdkDirectory=$sdk"
```

Another directory must contain the SDK DLLs matching the intended host. Keep references compatible rather than replacing host DLLs at deployment. The project source-links the four files from [HostedProviderProtocol](../Cerebral.HostedProviderProtocol/README.md).

Default output is `examples/Cerebral.OpenAiProviderPlugin/bin/Release/net10.0/`, with the DLL and `service-plugin.json`. Follow [Build](../../docs/BUILD.md) and [Plugin installation](../../docs/PLUGINS.md) for host dependencies and controlled service loading/restart. This package supplies no desktop catalog panel or hot activation. Its manifest describes fixture-level protocol work; this README asserts no live API or clean-build pass.

## Required host integration

The explicit public constructor accepts an exact JSON profile, `HttpMessageHandler`, a resolver of type `Func<string, int, CancellationToken, ValueTask<string?>>`, and an optional private visible-fragment callback. Profiles pin ID/revision, enabled state, credential-reference ID/revision, model declarations and concurrency. They contain references, not API keys.

Keep credentials in the integrating host's user-managed credential/settings facility, outside source and package files. Resolve only the admitted reference/revision at call time. This example does not implement a credential store, secret-entry UI or environment-variable convention. Never put a key in a manifest, sample profile, command arguments or logs.

The host must admit the loaded package generation and exact model declaration into the binding. A **configured-unverified** description is not proof of account access, quota or model availability. No model tools are contributed.

Operations are `pegasus.openai-provider.model-provider.v1.describe`, `.count`, `.generate` and `.reconcile`. They use closed versioned envelopes, not generic HTTP forwarding or workflow IDs. Remote reconciliation is unsupported; `.reconcile` cannot recover an uncertain remote outcome.

## Behavior, cost and limits

The source fixes HTTPS endpoints to `/v1/responses/input_tokens` and `/v1/responses` on `api.openai.com`, uses Bearer authentication, and sends `store: false` for generation. Content still leaves the computer; that flag does not guarantee every provider-side log or retention behavior. Account authorization, charges, terms and available models remain the operator's responsibility.

Only text messages and explicitly declared reasoning/response capabilities are admitted. Text, object JSON schema and JSON-string carrier outputs have different guarantees; a carrier is not promoted to provider-enforced object schema. Provider reasoning records are not exposed as answer text. No model weights are shipped.

Bounded messages, schema, visible output and concurrency apply. Occupied local slots return a rate-limit result instead of unlimited queueing. Cancellation before dispatch differs from an unknown result after dispatch. A malformed or unterminated stream is not completed; do not automatically retry unknown remote outcomes.

For missing/stale configuration, repair the admitted reference first. For capability/schema errors, check the exact declaration and renderer instead of removing validation. Provider APIs can change independently of this snapshot. First-party code follows the repository [LICENSE](../../LICENSE).

Source: [OpenAiProviderPlugin.cs](OpenAiProviderPlugin.cs), [project](Cerebral.OpenAiProviderPlugin.csproj) and [shared protocol](../Cerebral.HostedProviderProtocol/README.md).
