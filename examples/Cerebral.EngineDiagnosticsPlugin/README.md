# Pegasus Engine Diagnostics

Package `pegasus.engine-debug` **1.0.0** is an optional Worker service plugin for a bounded rolling diagnostic log. It has no desktop panel and exposes no model tools. Commands use the host's normal authorization; installing this package does not grant an agent additional authority.

This is a diagnostic-text collector, distinct from the scalar-only [Metrics plugin](../Cerebral.MetricsPlugin/README.md). Enabling it can persist sensitive text supplied by the host diagnostic stream.

## Build and load

From the repository root with the SDK in `global.json`:

```powershell
dotnet build examples/Cerebral.EngineDiagnosticsPlugin/Cerebral.EngineDiagnosticsPlugin.csproj -c Release
```

The project references source `Cerebral.Core`. Default output is `examples/Cerebral.EngineDiagnosticsPlugin/bin/Release/net10.0/`, containing the DLL and `service-plugin.json`. Review the dependency closure through [Plugin installation](../../docs/PLUGINS.md), rather than deploying arbitrary copies of host assemblies from build output.

Load and enable the service through that guide's service-package procedure and controlled Worker restart. A native catalog action is not service loading. The host must supply `IServiceEngineDiagnosticsHost` for capture and scoped settings for saved preferences. See [Build](../../docs/BUILD.md) and [Setup](../../docs/SETUP.md). No build or live-capture pass is asserted by this README.

## Command workflow

Use an authenticated command client supported by your host. This package adds no shell CLI or Configuration page.

| Operation | Purpose |
| --- | --- |
| `engine-debug.status` | Read capture state, storage faults and counters. |
| `engine-debug.set` | Explicitly attach/detach capture; arguments contain exactly one boolean, `enabled`. |
| `engine-debug.preferences.get` | Read saved preference metadata and values. |
| `engine-debug.preferences.legacy.prepare` | Prepare the existing local preference file for explicit migration. |
| `engine-debug.preferences.legacy.apply` | Apply the reviewed preparation against its captured revision and file identity. |

Capture starts only after an explicit successful `engine-debug.set` with `enabled: true`. Initial settings default to disabled. Loading, importing or restoring an enabled preference changes desired state; it does not automatically attach the stream. If the host lacks that stream, enable returns an unavailable result. Disable detaches capture even if saving the preference encounters a storage fault.

Central preferences use revision checks. After a failed or uncertain save, read current state before another change. The legacy `engine-debug-settings.json` is not silently imported at startup.

## Retention and privacy

The plugin writes `engine-debug.jsonl` under its host-assigned data directory. Rolling limits are **24 hours**, **32 MiB**, **8,192 records**, and **32 KiB per record**. Retention maintenance continues while the package is loaded, including while capture is disabled. Disabling capture does not erase retained records.

Records can include correlation IDs, model/agent/Worker identifiers, phases, outcomes, timing, tokens and optional diagnostic text. The writer masks a limited set of bearer-token and key-shaped strings. This is not comprehensive personal-data or secret redaction. Review and sanitize logs before sharing; never commit captured data or preference files to Git. No network uploader or model weights are included.

For invalid, oversized or externally changed logs, the plugin reports a storage fault rather than silently treating data as healthy. Read status, disable capture if needed and investigate the owned location. Do not bypass storage checks to make capture appear available. First-party code follows the repository [LICENSE](../../LICENSE).

Source: [EngineDiagnosticsPlugin.cs](EngineDiagnosticsPlugin.cs), [settings](EngineDiagnosticsSettings.cs) and [RollingEngineLog.cs](RollingEngineLog.cs). Existing deterministic fixtures are in [Cerebral.EngineDiagnosticsPlugin.Tests](../../tests/Cerebral.EngineDiagnosticsPlugin.Tests/).
