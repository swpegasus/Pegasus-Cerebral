# Anthropic hosted provider adapter

Package `pegasus.anthropic-provider` **1.0.0** implements the checked-in **`anthropic.messages.v1`** service protocol: bounded Messages requests, provider token estimates, streamed public text and terminal/usage interpretation.

**It is deliberately unconfigured by default.** The parameterless plugin has no profile, HTTP dependency or credential resolver. Installing the manifest alone does not connect an account, activate remote models or provide a provider-account Configuration page. A model host must supply the integration below.

## Build and package

Use the SDK in `global.json`. The project references existing `Cerebral.Core.dll` and `Cerebral.Contracts.dll` through `CerebralSdkDirectory`; it does not build those SDK projects. Default directory is the source Main Release output. Once that matching SDK exists, build from the repository root:

```powershell
$sdk = (Resolve-Path src/Cerebral.Orchestrator/bin/Release/net10.0-windows).Path
dotnet build examples/Cerebral.AnthropicProviderPlugin/Cerebral.AnthropicProviderPlugin.csproj -c Release "-p:CerebralSdkDirectory=$sdk"
```

The four [HostedProviderProtocol](../Cerebral.HostedProviderProtocol/README.md) files are source-linked into the adapter. Default output is `examples/Cerebral.AnthropicProviderPlugin/bin/Release/net10.0/`, including the DLL and `service-plugin.json`. These raw references do not copy host SDK assemblies into the output.

Use [Build](../../docs/BUILD.md) and [Plugin installation](../../docs/PLUGINS.md) for prerequisites, package closure and controlled Worker loading/restart. Native catalog actions are not service hot reload. The source/manifest describes protocol-fixture work, not a clean-build or live account qualification.

## Configuration and credentials

The explicit constructor takes an exact JSON profile, an `HttpMessageHandler`, a credential-reference resolver `Func<string, int, CancellationToken, ValueTask<string?>>`, and an optional private visible-fragment callback. Profiles pin ID/revision, enabled state, credential reference/revision, model declarations and concurrency. Bindings must also match the admitted package generation and model declaration.

Keep actual credentials in the integrating host's user-managed credential/settings facility. Inject a resolver for the exact reference. Do not add keys to source, manifests, example profiles, command arguments or logs. This adapter provides neither a credential database nor an account-entry screen. **Configured-unverified** means dependencies exist, not that account access or quota was checked.

Operations are `pegasus.anthropic-provider.model-provider.v1.describe`, `.count`, `.generate` and `.reconcile`, with strict versioned envelopes. The package has no model tools or arbitrary HTTP forwarding. Remote reconciliation is unsupported; an unknown remote result stays unknown.

## Rendering and outcomes

The fixed HTTPS endpoints are `/v1/messages/count_tokens` and `/v1/messages` on `api.anthropic.com`. Authentication uses `x-api-key`; the source sends `anthropic-version: 2023-06-01`. Count quality is **providerEstimate**. No local model weights/runtime are included. Admitted content leaves the computer; review provider account terms, privacy and charges before use.

System messages must precede non-system messages. Unsupported ordering is refused rather than silently relocated, and at least one non-system message is required. Text, object JSON schema and JSON-string carrier formats have distinct contracts. Declarations must explicitly admit formats and reasoning controls.

This renderer maps reasoning off to disabled thinking and supported effort choices to adaptive thinking with output effort. Effort mode refuses simultaneous explicit temperature; temperatures above 1 are also refused. These source mappings do not establish support in every remote model or future API version. Thinking blocks are not surfaced as public answer text.

The stream reader validates lifecycle, block order, stop reason and terminal confirmation. Time, concurrency and visible output are bounded. Cancellation before dispatch can prove a request was not sent; cancellation or malformed transport after sending can leave **unknown**. Do not convert uncertainty into completion or automatic retry.

For missing/stale configuration, repair the reference and binding. For capability errors, compare the exact model declaration with [AnthropicProviderPlugin.cs](AnthropicProviderPlugin.cs). Investigate transport without printing prompt/response bodies or credentials. First-party code follows the repository [LICENSE](../../LICENSE).
