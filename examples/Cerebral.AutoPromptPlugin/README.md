# Pegasus AutoPrompt

AutoPrompt **1.0.2** (`pegasus.autoprompt`) suggests one possible next message for the human user after a final assistant response. A suggestion appears only in an empty composer. **Tab** inserts it as an editable draft; **Send** remains a separate action.

## Requirements and setup

AutoPrompt needs a Windows Cerebral desktop host exposing `IDesktopIsolatedTextGenerationHost` and a compatible Worker. It deliberately remains unavailable on older hosts rather than falling back to unrestricted direct inference.

1. In Configuration, open **Plugins & tools > Desktop plugins**. Install the built `desktop-plugin.json` with **Install…**, select Pegasus AutoPrompt, then **Manage > Enable > Go**. Package enablement is off by default.
2. Open the selected package with **Manage > Configure… > Go**.
3. In **Compute & agents > Agent profiles**, create or configure an agent from the plugin's **AutoPrompt suggestion** template. It must be a helper with purpose `autoprompt`, a configured local model, and local agent/model routes. Load its model explicitly using the host's model controls.
4. In the AutoPrompt settings panel, select **Load existing AutoPrompt profiles**, choose the exact current profile revision, check **Enable next-message suggestions when available**, then **Save preferences**.

The default preference is disabled with no profile selected. Loading the list neither creates nor edits profiles. A saved stale or unavailable profile is retained for review; it is not silently replaced by a different agent. After editing the profile, load and explicitly select its new revision.

## Everyday use

Finish a normal conversation turn and leave the composer empty. If a grounded suggestion is available, press Tab to put it into the draft, edit it, and send only when ready. No suggestion is a valid outcome: the package may decline when the response lacks enough context, work or resources are held, the host lacks support, or generation is cancelled.

Suggestions receive only the latest final visible assistant response and a bounded suggestion policy. They do not receive earlier user requests, chat history, attachments, tools, filesystem roots, private reasoning or permission grants. Suggestions are advisory wording, never user consent.

## Build

From the repository root on Windows:

~~~powershell
dotnet build examples/Cerebral.AutoPromptPlugin/Cerebral.AutoPromptPlugin.csproj -c Release
~~~

The project targets `net10.0-windows`, references the current Core and Desktop.Plugins projects, and copies `desktop-plugin.json`. It does not define an alternate binary-SDK selection property. Use a coherent Cerebral source build; copying arbitrary host SDK DLLs into the plugin is not a compatibility fix.

See [BUILD](../../docs/BUILD.md) and [PLUGINS](../../docs/PLUGINS.md) for prerequisites, package layout and catalog updates.

## Persistence and troubleshooting

Preferences use the host's central package settings with explicit revision-aware saves. Portable settings retain the enabled preference but omit local profile identity/revision; another installation must select its own local profile. Opening settings does not generate a suggestion.

“Suggestions are unavailable in this version” means the required isolated-generation host was not bound. A saved preference alone cannot enable that capability. A profile or Hold error should be resolved through the host's existing controls; AutoPrompt does not load/switch models or grant tools for you. Source-preview instructions do not certify a successful inference run on your machine.

## License and source notes

First-party code is under the [Pegasus Community License 1.0](../../LICENSE); model licenses remain separate. The behavior and limits are defined by [AutoPromptPlugin.cs](AutoPromptPlugin.cs), [AutoPromptHarness.cs](AutoPromptHarness.cs), [AutoPromptPlugin.Settings.cs](AutoPromptPlugin.Settings.cs) and [AutoPromptSettings.cs](AutoPromptSettings.cs).
