# Review workflow SDK acceptance package

`example.review` is the removable reference package for Cerebral's persistent service-workflow and native-desktop contracts. It has two independently loadable components with the same package identity and semantic version `1.0.0`:

- `Cerebral.ReviewPlugin.csproj` builds the Worker service module described by `service-plugin.json`.
- `Desktop/Cerebral.ReviewPlugin.Desktop.csproj` builds the trusted WPF contribution described by `Desktop/desktop-plugin.json`.

The package is trusted native code, not a sandbox. It registers no legacy model tools and grants no authority. Its workflow and command require the saved profile capability `diagnostics`.

## Published contribution

The service contributes workflow `example.review:review` version `1.0.0` with input `{ "text": "A short sample." }`. It asks two separately identified fields:

- `label`: required free text.
- `style`: required choice, `concise` or `detailed`.

The portable package setting `defaultStyle` uses the same two choices and defaults to `concise`. It contains no credential and is snapshotted by the host at workflow admission.

After the exact question revision is answered, deterministic command `example.review:measure` version `1` counts whitespace-delimited words and Unicode scalar characters. It owns `review.db` inside the package data directory and commits one `review_receipts` row keyed by the Engine effect ID. The command returns the durable record ID/revision, chosen label/style and measured counts. A repeated effect with the same run, request and arguments returns the committed receipt; reuse with different material is a conflict. Reconciliation only reads that row and never starts a new mutation. Cancellation is honored before the transaction begins. Failed or inconsistent operations produce a failed workflow rather than a success-shaped result.

The desktop contribution adds a right-side **Review** panel and **Review draft** composer action. The action captures the current draft, leaves it unchanged, and submits the explicit `example.review:review` target through `IDesktopNativeWorkflowHost`. The short visible request and private assignment remain separate. An empty draft is handled locally. Disablement disposes the contribution; a retained action is denied by the released host lease.

`UserText`, hidden assignment text and accepted attachment references are Engine-owned state. Attachment bytes remain in the attachment store and are not copied into plugin state or the visible request. The example command measures its explicit workflow input; it does not claim to read attachment contents, estimate model tokens, invoke a model, or prove an external operation.

## Build and stage

Build both projects with the installed .NET SDK. The service output includes its manifest, managed/native SQLite dependencies and dependency metadata. Stage the complete service output in a Worker service-package directory named exactly `example.review`; the directory and manifest IDs must match. Stage the desktop output through Configuration's normal desktop-package catalog. Host SDK assemblies resolve to the Worker's or desktop host's already loaded `Cerebral.Core`, `Cerebral.Contracts` and `Cerebral.Desktop.PluginSdk` identities.

Package activation is explicit. Copying a DLL into a running Worker does not activate it, and an owner-reviewed desktop update uses the immutable-generation catalog rather than overwriting loaded files. A paused workflow pins its service definition version until it is resumed, abandoned, or explicitly upgraded.

Use live `sdk.discover` and `sdk.describe` for the current submission, answer and inspection schemas. Submit once with a stable request ID. Retain the returned run ID plus question-set ID/revision for answers and reuse the entire request when reconciling an uncertain acknowledgement.

## Acceptance entrypoints

After a Release build:

```powershell
dotnet tests/Cerebral.Worker.Tests/bin/Release/net10.0/Cerebral.Worker.Tests.dll --workflow-worker-only
dotnet tests/Cerebral.Worker.Tests/bin/Release/net10.0/Cerebral.Worker.Tests.dll --workflow-live-only
dotnet tests/Cerebral.Desktop.Tests/bin/Release/net10.0-windows/Cerebral.Desktop.Tests.dll examples/Cerebral.WorkspacePlugin/bin/Release/net10.0-windows/desktop-plugin.json examples/Cerebral.DictationPlugin/bin/Release/net10.0-windows/desktop-plugin.json examples/Cerebral.BookPlugin/bin/Release/net10.0-windows/desktop-plugin.json examples/Cerebral.ReviewPlugin/Desktop/bin/Release/net10.0-windows/desktop-plugin.json
```

The in-process Worker lane verifies schemas, authorization, questions, exact answer retry and one SQL mutation. The live lane starts a real Worker over loopback HTTP, imports a real text attachment as typed supplemental content, submits the workflow, kills/restarts the Worker, verifies the same form and attachment coordinate, answers it, retries the answer and verifies exactly one package-owned SQL row. The desktop lane creates real WPF contributions, checks the native route and draft isolation, then disables the package and verifies that retained access is revoked. These deterministic checks do not establish configured-model generation, a shown-window user interaction, installation, or Cerebral's final agent-authored self-build acceptance.
