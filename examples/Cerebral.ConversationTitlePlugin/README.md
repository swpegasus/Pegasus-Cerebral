# Conversation Titles

Conversation Titles **0.2.0** (`pegasus.conversation-titles`) optionally names a new conversation from its first accepted root user input. It uses source-bound background work and a conditional metadata update, so a title generated for one source is not applied to another chat or over a manual rename.

The package has two components: a Worker service extension and a Windows desktop preferences plugin under [Desktop/](Desktop/). Both manifests are version 0.2.0.

## Install and configure

Install the service component through the [service package workflow](../../docs/PLUGINS.md). Configuration **Plugins & tools > Service extensions** is a loaded-package inventory with **Refresh** and **Inspect**; it is not a service-package Install dialog.

Install `Desktop/desktop-plugin.json` through Configuration **Plugins & tools > Desktop plugins > Install…**. Select Conversation titles, enable it with **Manage > Enable > Go**, and open **Manage > Configure… > Go**.

In **Conversation titles** settings:

1. Select **Load existing profiles**. This only reads profile choices.
2. Check the **Source profiles** whose new root conversations may be named.
3. Choose a **Local execution profile and exact revision** to run title generation. It must be a valid existing local execution profile with a usable configured model route. The label displays that route; selecting a local agent does not by itself prove every model placement is local.
4. Check **Enable automatic conversation names**, then **Save preferences**.

Defaults are disabled, with no source profiles or inference reference. Enablement alone stays inactive until both selections exist. A stale execution-profile revision must be explicitly reviewed/reselected; the plugin does not create, edit or grant profiles from its settings panel. Normal model/resource controls still apply.

## What happens to a chat

Future eligible first accepted inputs from the selected source profiles can admit one background title workflow. The package captures a bounded excerpt, asks for a short descriptive title, and proposes a conditional rename. The source permits a validated fallback title for definite non-uncertain generation failure/invalid output.

Manual title edits, an already edited title, changed settings/source identity, cancellation, or incompatible state can cause the conditional rename to be skipped. Unknown/unconfirmed generation is not converted into permission to retry or rename. The plugin does not resend the user's request, replay uncertain naming work, or retroactively rename all existing conversations. Attachment-only/blank input does not provide a text seed.

The title model receives up to **4,096 UTF-8 bytes** of the first user text as reference data, with a **96-token** response limit and no requested model tools. Title candidates are bounded to 72 Unicode scalars, 288 UTF-8 bytes and 10 words. The original accepted chat text remains authoritative.

## Build

From the repository root:

~~~powershell
dotnet build examples/Cerebral.ConversationTitlePlugin/Cerebral.ConversationTitlePlugin.csproj -c Release
dotnet build examples/Cerebral.ConversationTitlePlugin/Desktop/Cerebral.ConversationTitlePlugin.Desktop.csproj -c Release
~~~

The service targets `net10.0`; the WPF desktop component targets `net10.0-windows`. The normal route uses current source Core/Contracts/Desktop.Plugins references discovered from `CerebralSourceRoot`.

For a deliberately reviewed binary SDK cohort, these are the actual optional properties:

~~~powershell
$sdk = '<matching-Core-and-Contracts-directory>'
$desktopSdk = '<matching-Desktop.Plugins-directory>'
dotnet build examples/Cerebral.ConversationTitlePlugin/Cerebral.ConversationTitlePlugin.csproj -c Release "-p:CerebralSdkDirectory=$sdk"
dotnet build examples/Cerebral.ConversationTitlePlugin/Desktop/Cerebral.ConversationTitlePlugin.Desktop.csproj -c Release "-p:CerebralSdkDirectory=$sdk" "-p:CerebralDesktopSdkDirectory=$desktopSdk"
~~~

These project guards require the named files; they do not verify a published SHA-256 cohort or prove runtime compatibility. Keep Core, Contracts, Desktop.Plugins and Worker implementation coherent. Source mode is preferable to guessing binary references. See [BUILD](../../docs/BUILD.md).

## Persistence, privacy and limitations

Preferences use central global package settings with explicit revision-aware saves. Portable export retains enabled intent but omits local source-profile IDs and inference references; configure those locally after transfer. Opening settings does not run a model or rename a conversation.

The service requires source-bound background admission, committed-root-input observations and own-settings support from its host. An older Worker lacking those facilities cannot be enabled by the desktop panel. Check the loaded service inventory and diagnostics if no title appears. This source-preview guide is not a live title-generation/rename verification.

Title generation sends a first-input excerpt to the configured model route and stores normal workflow metadata/results. Do not assume the excerpt is private merely because the generated title is short. The plugin does not add model tools or extra chat authority.

## License and source notes

First-party code uses the [Pegasus Community License 1.0](../../LICENSE), with model/runtime terms separate. [ConversationTitlePlugin.cs](ConversationTitlePlugin.cs) registers source-scoped work; [SourceBoundConversationTitleWorkflow.cs](SourceBoundConversationTitleWorkflow.cs) defines the compiled workflow; [TitleSettingsPolicy.cs](TitleSettingsPolicy.cs), [TitlePolicy.cs](TitlePolicy.cs) and [Desktop/ConversationTitleDesktopPlugin.cs](Desktop/ConversationTitleDesktopPlugin.cs) define preferences, bounds and controls.
