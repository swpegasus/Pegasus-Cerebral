# Personality Presets

Personality Presets adds an optional communication style to a chat. This source contains a **desktop package 1.2.4** and a **Worker service package 1.2.3**, both identified as `pegasus.personality`. They have different roles and are not interchangeable.

The desktop package provides Main's **Personality** selector, **A\*\*** candid-mode toggle, **Preview context**, and a shared-style editor. The service package stores choices, retains exact definition revisions, and supplies optional workflow context. It contributes no model tools and does not grant permissions.

## Install the pair

Install the matching Worker service component using the supported [service package workflow](../../docs/PLUGINS.md). Configuration **Plugins & tools > Service extensions** reports the active Worker's loaded packages; it is an inventory, not a service-package Install dialog. Confirm that Personality Presets is loaded there.

For the desktop component, open **Plugins & tools > Desktop plugins**, choose **Install…**, select its built `desktop-plugin.json`, and use **Manage > Enable > Go**. Desktop enablement defaults off. Use **Manage > Configure… > Go** for the **Personality** shared-style editor.

A compatible host must expose the composer-accessory mount, structured service commands and central settings/context facilities used by these packages. A desktop control appearing does not prove the selected chat workflow consumes its optional style.

## Choose a style for a chat

In Main, use **Personality** beside the composer to choose **No personality**, a packaged style, or an available saved custom style. Selection and candid-mode changes save against the current chat's exact state; they do not submit a message.

- **A\*\*** adds constructive candid pushback: challenge weak assumptions and avoid automatic agreement/praise. It does not require insults or profanity, and does not change accuracy, authority or permissions.
- **↻** reloads the chat's actual saved selection after an error, another edit, or a pending save.
- **Preview context** shows the description and exact saved public style text. It resolves the saved revision/hash, not a newer definition with the same name.
- **No personality** saves both base style and optional mode as off for future turns in that chat.

Saved, accepted and applied are different states. New accepted requests capture their style selection; accepted/in-flight work keeps its captured revision. The selected workflow and context budget may omit optional context. The plugin therefore reports **application not confirmed**, rather than claiming the model used it.

See the [character/style library](docs/final-space-character-library.md) for the packaged choices and revision behavior.

## Create shared custom styles

Open the package's **Personality** configuration panel. **New preset** starts a custom draft, **Start from a template** fills draft wording, **Save** explicitly stores it, and **Reload / discard** reloads saved values. Choose a chat's style from Main rather than assuming editing a shared default rewrites every chat.

The service contract allows eight current custom definitions, with bounded retained versions. A base definition is limited to **120 whitespace-separated words and 1,024 UTF-8 bytes**; the serialized optional provider response has its own **2,048-byte** limit. These are style-context limits, not model response-length settings. A desired output format or explicit request for brevity still matters.

An existing saved reference is not silently upgraded after a library edit. Explicitly choose a current offered definition for future turns. Inspect the draft/status when a save is stale or unconfirmed instead of blindly repeating it.

## Build

From the repository root on Windows, use one coherent source checkout:

~~~powershell
dotnet build examples/Cerebral.PersonalityPlugin/Service/Cerebral.PersonalityServicePlugin.csproj -c Release
dotnet build examples/Cerebral.PersonalityPlugin/Cerebral.PersonalityPlugin.csproj -c Release
~~~

The native package targets `net10.0-windows`; the service inherits `net10.0`. Each output has its own manifest. Do not install the service manifest as a desktop plugin, or copy private host SDK DLLs into an admitted plugin generation.

The projects also define a strict optional binary-SDK profile. It is for the exact named cohort, not an assertion that any current installation matches it:

~~~powershell
$sdk = '<directory-containing-the-reviewed-matching-SDK-DLLs>'
dotnet build examples/Cerebral.PersonalityPlugin/Service/Cerebral.PersonalityServicePlugin.csproj -c Release -p:CerebralSdkProfile=installed-20261121 "-p:CerebralSdkDirectory=$sdk"
dotnet build examples/Cerebral.PersonalityPlugin/Cerebral.PersonalityPlugin.csproj -c Release -p:CerebralSdkProfile=installed-20261121 "-p:CerebralSdkDirectory=$sdk"
~~~

The profile checks these complete SHA-256 values:

| SDK file | Expected SHA-256 |
|---|---|
| Cerebral.Core.dll | `41612d833e900298d9c400f78e10b585589a71b2d10890069d1d38e0c177cac9` |
| Cerebral.Contracts.dll | `d0b832fa8d02a143e0515f85336bce573832880b14356b6d9a21ce858706cdb2` |
| Cerebral.Desktop.Plugins.dll | `d929ce7b56b1a1f10866fe7546035c8b60d9e65d45c37954eea6cd286e2ad0ef` |

The service consumes the first two and the desktop consumes the third. Unknown profiles or changed bytes are errors; do not delete hash checks to make an unrelated cohort build. The service's older explicit `CerebralSdkDirectory` mode without a profile has different pinned inputs. See [BUILD](../../docs/BUILD.md) for the normal source-build route.

## Privacy and troubleshooting

Styles, per-chat choices and retained source definitions are local plugin/central-settings data. Style text can become model input. Local “private” library files are plaintext, not an encrypted vault, and owner-facing metadata alone is not a guarantee of human-only command authorization. This guide covers public presets/custom styles, not private-import recipes. Private definition bodies are unavailable through **Preview context**.

If the selector is disabled or says unavailable, verify the Worker connection, enabled desktop package, loaded service package and supported mount/API. If a saved style has little effect, first distinguish saved selection from workflow/context inclusion; increasing text or model output limits does not establish inclusion. A source build does not prove live model adherence or per-chat delivery on a particular host.

## License and source notes

First-party code/text uses the [Pegasus Community License 1.0](../../LICENSE). Third-party characters, names, media and model assets retain their owners' rights; the included original style summaries do not grant rights to episodes, subtitles, artwork or voices.

[PersonalityPlugin.Composer.cs](PersonalityPlugin.Composer.cs), [PersonalityTemplates.cs](PersonalityTemplates.cs), [Service/PersonalityContract.cs](Service/PersonalityContract.cs), [Service/PersonalityChatStore.cs](Service/PersonalityChatStore.cs) and the two project files define the controls, bounds, provenance and SDK checks.
