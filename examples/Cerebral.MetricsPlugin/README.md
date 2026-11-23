# Cerebral Metrics

Cerebral Metrics **0.2.0** has two independently installed components under package ID `pegasus.metrics`: a Worker service collector and a native Configuration dashboard. Both use plugin-private DTOs in `Cerebral.MetricsPlugin.Shared.dll`.

The service requires host facet **`cerebral.service.scalar-generation/1`**. It records supported scalar generation observations, not a complete replay of every workflow or tool. Having DTO fields for several activity kinds does not mean the current source supplies all of them. The dashboard never starts inference, reserves slots, clears quarantine or changes routing.

## Build the components

From the repository root on Windows with the SDK in `global.json`:

```powershell
dotnet build src/Cerebral.Core/Cerebral.Core.csproj -c Release
dotnet build src/Cerebral.Desktop.Plugins/Cerebral.Desktop.Plugins.csproj -c Release
dotnet build examples/Cerebral.MetricsPlugin/Service/Cerebral.MetricsPlugin.Service.csproj -c Release
dotnet build examples/Cerebral.MetricsPlugin/Desktop/Cerebral.MetricsPlugin.Desktop.csproj -c Release
```

These components consume existing SDK DLL files. Service property **`MetricsCoreReferencePath`** defaults to `src/Cerebral.Core/bin/Release/net10.0/Cerebral.Core.dll`; desktop property **`MetricsDesktopReferencePath`** defaults to `src/Cerebral.Desktop.Plugins/bin/Release/net10.0-windows/Cerebral.Desktop.Plugins.dll`. To use another matching SDK, pass the appropriate property as an exact DLL file path. These are not `CerebralSdkDirectory` properties. Preserve compatible references and the manifest's required-facet admission check.

Default outputs are `Service/bin/Release/net10.0/` and `Desktop/bin/Release/net10.0-windows/`. Each package needs its component DLL, manifest and shared DTO DLL. Do not bundle replacement host SDK assemblies. Install both components through [Plugin installation](../../docs/PLUGINS.md); enabling one does not install the other. Service loading follows the controlled Worker startup/restart procedure. The native manifest defaults to disabled.

These are source instructions, not fresh-checkout or installed-host qualification. See [Build](../../docs/BUILD.md). The retained lifecycle test project has additional baseline-reference inputs; its presence is not a self-contained clean-checkout test command.

## Collect and inspect

Open **Metrics** in Configuration after enabling the desktop component and connecting to the service. Start with **Refresh**. Choose **7 days** or **30 days** and a local/remote route filter. **Advanced filters** accepts exact model, agent, chat, project and Worker IDs.

Under **Collection & storage**, select **Collect scalar history**, review retention/quota, and choose **Save collection settings**. Defaults are **capture off**, **30 days**, **64 MiB**. Saving enabled capture permits a host subscription; reopening the view alone does not enable it. Saved capture intent is restored on service initialization.

Read coverage alongside totals. Missing values remain unavailable, gaps remain partial, repeated reads are deduplicated, and observed progress is not confirmed token consumption. Allocated, available and quarantined slots differ. Trend dates use UTC.

## Storage, privacy and troubleshooting

The host-assigned plugin data directory contains bounded journals (`history.v2.jsonl` for the current journal), coverage metadata, `settings.json` and `history.lock`. Legacy journal names are handled separately. Limits apply to bytes and retained entities; queries return at most 200 rows.

The scalar schema excludes prompt text, responses, reasoning, tool bodies, paths, raw errors and credentials. It retains correlation IDs, model/Worker identifiers, timestamps and usage; treat that operational history as user data. Keep it out of Git and public support bundles. The dashboard does not configure a remote telemetry service.

If the required facet is absent, use a compatible host rather than removing the requirement. Unavailable source, partial coverage and cleanup faults are real states; an empty chart does not prove that no work occurred. No model weights or API credentials are bundled. First-party code follows the repository [LICENSE](../../LICENSE).

Source: [service](Service/MetricsServicePlugin.cs), [scalar source](Service/ScalarGenerationMetricsSource.cs), [history store](Service/MetricsHistory.cs), [dashboard](Desktop/MetricsDashboardView.cs) and [contracts](Shared/MetricsContracts.cs).
