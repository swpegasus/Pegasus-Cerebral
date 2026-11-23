# Review workflow example

A small **model-independent text measurement workflow** for learning Cerebral's durable workflow/plugin APIs. It asks two questions, measures the supplied text, persists a receipt and returns the result. This is not an AI code reviewer.

| Component | Package version | Artifact |
| --- | --- | --- |
| Service `example.review` | 1.0.0 | `Cerebral.ReviewPlugin.dll` + `service-plugin.json` |
| Desktop companion `example.review` | 1.0.0 | `Cerebral.ReviewPlugin.Desktop.dll` + `Desktop/desktop-plugin.json` |

The native companion explicitly selects workflow `example.review:review` **1.0.0**. The service exposes workflow command `example.review:measure` version 1, not a general model tool.

## Build and load both parts

Requires .NET 10; the desktop companion additionally requires Windows x64/WPF and the compatible desktop SDK.

```powershell
dotnet build .\examples\Cerebral.ReviewPlugin\Cerebral.ReviewPlugin.csproj -c Release
dotnet build .\examples\Cerebral.ReviewPlugin\Desktop\Cerebral.ReviewPlugin.Desktop.csproj -c Release
```

Run from the repository root. With default output layout, service output uses `bin/Release/net10.0/`; the companion uses its own `Desktop/bin/Release/net10.0-windows/`. Preserve the service dependencies, including Microsoft.Data.Sqlite **10.0.10** and SQLitePCLRaw **2.1.13**.

The service uses host package **stage/inspect/activate**, drains active durable pins as required, and needs a normal Worker restart before the selected generation is observed. Configuration → **Service extensions** is the loaded inventory; **Package commands** opens the advanced owner console. See [PLUGINS](../../docs/PLUGINS.md).

Install the companion separately through Configuration → **Desktop plugins → Install…**, then **Manage → Enable → Go**. Refresh or reopen Main if needed. Enabling only the companion cannot supply the missing service workflow.

## Try the example

1. Connect Main to the Worker with the loaded service package and the necessary `diagnostics` workflow capability.
2. Enter nonempty text in the composer.
3. Choose **Review draft** or open the **Review** panel and its review action.
4. Answer the durable questions: a label and **concise** or **detailed** presentation style.
5. Inspect the resulting word count, character count and saved measurement receipt.

The companion captures the current draft with its conversation binding and leaves the composer unchanged. The workflow accepts at most 32,768 text characters; labels are bounded to 200. Words are whitespace-delimited and characters are Unicode scalar values including whitespace. These are measured counts, **not model tokens**.

## Settings, persistence and privacy

The service manifest declares optional portable field `defaultStyle` with `concise/detailed` values. The actual workflow in this source still asks for style and consumes that answer; do not assume this field silently changes the questions or creates a separate settings UI.

Durable workflow input/questions belong to the host's workflow store. Plugin-private `review.db` stores measurement receipts keyed by effect identity, including label, style, counts and summary; its receipt table does not store the full input text. That does not mean the host's accepted workflow input omits the text.

No inference model, network request or general file/process tool is needed for measurement. The service writes its receipt database. Reconciliation reads the saved receipt; absence is Unknown, not an invitation to invent a result.

## Troubleshooting

- **Native workflows unsupported / definition missing:** inspect host SDK compatibility and the service inventory; the desktop plugin cannot add Worker workflows by itself.
- **Empty or oversized draft / invalid style:** correct the bounded input and answer one of the declared styles.
- **Unconfirmed submission:** inspect the conversation/workflow before trying again. The run may already exist, and this companion does not automatically resend after a lost acknowledgement.
- **No matching measurement receipt after interruption:** preserve the Unknown result and use the supported workflow inspection/reconciliation path.

Source: [ExampleReviewPlugin.cs](ExampleReviewPlugin.cs), [service manifest](service-plugin.json), [desktop source](Desktop/ExampleReviewDesktopPlugin.cs), [desktop manifest](Desktop/desktop-plugin.json). SQLite and other dependencies retain their own notices.

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
