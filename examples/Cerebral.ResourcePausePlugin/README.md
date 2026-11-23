# Pegasus Resource Pause

Pegasus Resource Pause is the native owner interface for local inference availability, preferred local models, engine selection and intentional work hold. Package `pegasus.resource-pause` is **1.1.5** in [desktop-plugin.json](desktop-plugin.json); assembly version inherits repository defaults. The manifest defaults to enabled, but catalog installation and enablement remain separate.

It controls an existing Worker through the structured service bridge. It does not include a model, start independent inference or replace Worker routing and ownership checks.

## Build and install

From the repository root on Windows with the SDK in `global.json`:

```powershell
dotnet build examples/Cerebral.ResourcePausePlugin/Cerebral.ResourcePausePlugin.csproj -c Release
```

The project references source Core/Desktop Plugins SDKs and source-links shared preference helpers. It has no installed-SDK override. Default output is `examples/Cerebral.ResourcePausePlugin/bin/Release/net10.0-windows/`, including the DLL and explicitly copied `desktop-plugin.json`. Host SDK assemblies remain host-provided.

Install and enable through [Plugin installation](../../docs/PLUGINS.md). See [Build](../../docs/BUILD.md) and [Setup](../../docs/SETUP.md) for host and configured-model prerequisites. These instructions are not a clean-build or live routing result.

## Choose the appropriate control

Configuration panels are **Local GPU**, **Local model preference**, **Engine**, **Work hold** and **Paired workers**. Main also receives a **Resource Pause** sidebar tab and resource/engine/work-hold status contributions.

- **Pause local GPU** stops admission of new local inference. Eligible work may use an available paired Worker; work without an eligible route remains queued. This does not cancel accepted work.
- **Pause GPU and use paired workers** chooses the host's paired-worker routing action. It does not establish pairing or grant remote authority.
- **Local model preference** saves a desired local model. Use **Load saved preferences**, review a choice, then **Save preferences**. Loading, restoring or preparing preferences does not activate a model. **Activate saved local model** is separate and checks engine/resources. Explicit profile pins and accepted runs retain their routes.
- **Engine** offers **Pause engine**, **Switch after current work**, **Rollback candidate** and **Refresh engine**. Actions use the observed engine epoch; switching checks active evaluations and work-hold ownership. A candidate slot must already exist and be valid.
- **Work hold** offers **Stop now**, **Finish current work, then hold**, **Resume held work**, **Cancel captured work** and **Refresh hold**. These are distinct choices. Resume/cancel is tied to the captured generation; an unknown result does not authorize automatic resend.

Read status and confirmation after each action. Resource pause, engine pause and intentional work hold are separate states; one status does not establish that the others changed.

## Preferences and privacy

Default preferred local model is **unset**. Central settings save desired model identity with revision checks. Portable projection omits that machine-local reference. The old plugin-owned `resource-pause.json` is available through explicit preparation; opening the panel does not migrate it. Reload after an unconfirmed save.

Preferences contain model identity, not weights or credentials. Status can expose local/paired Worker and model identifiers. The plugin performs no new network enrollment, API-account setup or model download. Keep local settings, pairing material and runtime captures out of the repository.

## Troubleshooting and source

Initialization requires `IDesktopServiceHost`. If missing, use a compatible host instead of a shell fallback. Refresh after a state/epoch conflict. Missing models, unavailable routes, active evaluations and unresolved holds are actionable states; do not clear them by deleting state, forcing switches or killing a Worker.

Source: [ResourcePausePlugin.cs](ResourcePausePlugin.cs), [engine](ResourcePauseEngine.cs), [work hold](ResourcePauseWorkHold.cs), [model preference](ResourcePauseLocalPreference.cs) and [policy](ResourcePauseSettingsPolicy.cs). Deterministic fixtures are in [Cerebral.ResourcePausePlugin.Tests](../../tests/Cerebral.ResourcePausePlugin.Tests/). First-party code follows the repository [LICENSE](../../LICENSE); configured models and runtimes retain their own licenses.
