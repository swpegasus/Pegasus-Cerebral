# Pegasus Prompt Review

Prompt Review **1.0.10** (`pegasus.prompt-review`) adds the **Refine my prompt** input mode. It lets you review a model's interpretation, edit it, and discuss corrections before the request enters the Main chat.

## Install and configure

This Windows WPF desktop plugin requires Cerebral's desktop API version 1, structured service commands, and a configured inference model.

1. In Configuration, open **Plugins & tools > Desktop plugins**, choose **Install…**, and select the built `desktop-plugin.json`.
2. Select Pegasus Prompt Review and use **Manage > Enable > Go** if needed. The manifest has no `defaultEnabled` declaration; check the actual catalog state.
3. In **Compute & agents > Agent profiles**, configure a **Prompt clarifier** helper from the plugin's template with a model and worker route. The source resolves the first profile whose ID or name contains `clarif`, or whose purpose contains `restate`; keep that choice unambiguous.
4. In Main, enable the **Refine my prompt** quick toggle/input mode before submitting a new draft. Steering messages pass through without this review step.

There is no separate package preferences panel or model downloader. The selected clarifier profile supplies the inference target; a non-local model route uses the host's worker inference command. Do not assume review stays on the local machine when that profile selects another worker.

## Review a draft

Attempting normal submission with the mode active opens a review card instead of sending the request to Main.

- **Original request · unchanged** preserves the captured input for comparison.
- **Editable interpretation** is the complete proposed replacement.
- **What changed · exact text comparison** shows the smallest range containing changed text.
- Edit the proposal directly, or add a note under **Tell the clarifier what to change, or answer its question**, then choose **Refine**.
- **Apply to prompt box** replaces the captured composer draft without sending it. A pending refinement note must be incorporated with Refine, or cleared, before applying.
- **Cancel review** closes the review. Return to the composer and inspect the draft before proceeding.

Apply checks both conversation identity and the original draft revision. If the conversation or draft changes, the plugin refuses to overwrite newer text. An unconfirmed replacement must be inspected rather than blindly repeated. Direct edits and unused notes are retained within an active review when a refinement/model error can safely be shown.

## Build

From the repository root on Windows:

~~~powershell
dotnet build examples/Cerebral.PromptReviewPlugin/Cerebral.PromptReviewPlugin.csproj -c Release
~~~

The project targets `net10.0-windows` and references the current Desktop.Plugins project. Its output includes `desktop-plugin.json`. It has no alternate binary-SDK build property. Follow [BUILD](../../docs/BUILD.md) and install through the [plugin catalog workflow](../../docs/PLUGINS.md).

## Privacy and limitations

The clarifier receives the original wording, current proposal, refinement note and bounded earlier review discussion. The plugin keeps the review discussion in memory for that review; it does not append it to Main as conversation turns. Applying the proposal returns only the chosen wording to the composer. Normal host/model handling still applies to the inference request.

The model may misunderstand names, quantities or intent. Review the proposal and comparison before applying it. No tool execution is requested by the clarification policy, and review is not authorization for the underlying task. A missing clarifier, invalid model response, cancellation or stale binding prevents normal submission through this mode. These source instructions do not establish a successful live model or remote-worker test.

## License and source notes

See the [Pegasus Community License 1.0](../../LICENSE), with separate terms for any model/runtime you use. [PromptReviewPlugin.cs](PromptReviewPlugin.cs) defines interception, profile resolution and guarded apply; [PromptReviewConversation.cs](PromptReviewConversation.cs) defines review bounds and response shape.
