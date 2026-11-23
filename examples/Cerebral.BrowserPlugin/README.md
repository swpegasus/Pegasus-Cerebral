# Pegasus Browser

A Worker-side service plugin for bounded page retrieval and isolated rendered-browser actions. It is not a WPF browser tab or a replacement for your normal browsing profile.

**Service package:** `pegasus.browser`, version **1.0.2**. Requires Cerebral Core and Microsoft.Playwright **1.63.0**. The .NET target is inherited from the repository's .NET 10 build settings.

## Build and load

```powershell
dotnet build .\examples\Cerebral.BrowserPlugin\Cerebral.BrowserPlugin.csproj -c Release
```

Run from the repository root. Keep the complete service output, including dependencies, `service-plugin.json`, `plugin-resources.json`, notices and the hidden `.playwright` driver directory. Copying only `Cerebral.BrowserPlugin.dll` is insufficient. This source preview does not contain a browser binary.

Use the supported **service package** lifecycle: inspect, stage the built service manifest with the expected revision, inspect again, activate after active durable pins drain, then perform the required normal Worker restart and check the loaded inventory. Configuration → **Service extensions** reports that inventory; **Package commands** opens the advanced owner console. It is not a native desktop-plugin Install page. See [PLUGINS](../../docs/PLUGINS.md) for the host lifecycle rather than editing installed package files.

## Choose the right operation

| Tool | Use / capability |
| --- | --- |
| `browser.request` | GET allowed page source without JavaScript or browser launch; `browser.read` |
| `browser.open`, `browser.navigate`, `browser.close` | Manage this job's isolated rendered tabs; `browser.navigate` |
| `browser.inspect`, `browser.screenshot` | Bounded visible text/controls or viewport PNG; `browser.read` |
| `browser.click`, `browser.fill` | Act on controls from the latest exact snapshot; `browser.interact` |

An authorized agent/workflow uses these tools through the host. There is no raw owner service command named `browser.request` that accepts authority in its JSON, and no native Main browser-control panel in this package. The advanced `tools.call` route has its own supported profile/conversation rules; do not substitute a native chat identifier for a supported owner binding. [PAGE-REQUESTS.md](PAGE-REQUESTS.md) explains that boundary.

For rendered browsing on Windows x64, the resolver uses an explicitly activated reviewed Chromium resource, otherwise an existing system-wide Microsoft Edge installation. It does not use your installed browser profile/cookies, search arbitrary executable paths or download a browser automatically. If neither runtime is available, page-source GET can still work. Advanced runtime staging uses `browser.resources.inspect/activate` with a matching revision and complete resource provenance/licenses; close browser jobs before changing the generation.

## Configure access and limits

Settings belong to the selected agent profile under plugin ID `pegasus.browser`; they are not global credentials or command-line browser flags. The default **allowedDomains is empty**, so no site is implicitly authorized. Use explicit hostnames/IP addresses, not URL paths, ports or wildcard domains. Grant the needed browser capabilities through the host's normal permissions.

Defaults in [BrowserPolicy.cs](BrowserPolicy.cs):

| Setting | Default |
| --- | --- |
| Concurrent jobs / tabs per job / total tabs | 2 / 3 / 6 |
| Actions at once / queue depth | 1 / 8 |
| Minimum action delay | 350 ms |
| Navigation / action / idle timeout | 30 / 15 / 300 seconds |
| Page heap observation limit | 128 MiB |
| Screenshot storage / retention | 16 MiB / 30 minutes |
| Downloads / uploads / authentication / outside domains | `confirm` |
| Clipboard / external applications | `deny` |

**The confirmation integration for sensitive operations is pending: those operations are denied in this provider even when a setting says `confirm`.** It does not mean automatic consent. Reported heap observations are not an OS-enforced CPU/process-memory ceiling.

`browser.request` defaults to 65,536 received bytes and 12,000 UTF-8 text bytes. It accepts no cookies, caller headers, credentials, request body or method override. Redirects and network destinations pass through the shared network gate. Source text remains untrusted data.

## Persistence, privacy and recovery

Profile policy persists with the profile. Rendered sessions are isolated and transient; idle expiry, close-all, plugin disposal or restart closes them. Viewport screenshots use private plugin capture storage and quotas. Websites and the selected endpoint still receive the requests you authorize.

Request/effect markers are bounded **in-process** receipts, not durable cross-restart replay protection. Dispatched cancellation/timeout can be Unknown; do not automatically resend it after reconnecting or restarting. A repeated completed request may return metadata without a retained body. Use a new explicitly chosen request for a fresh read only after reviewing the prior outcome.

Use owner `browser.metrics` for bounded status and `browser.close-all` to close owned plugin sessions. These are advanced owner operations, not model permission switches.

## Troubleshooting and licensing

- **Outside domain / capability denied:** review the exact profile grants and explicit domain list.
- **runtime-missing:** use page-source retrieval, or review the system Edge/Chromium runtime requirement. Do not run an automatic browser download as a hidden workaround.
- **Policy changed:** old tabs close; open a fresh tab under the reviewed policy.
- **Queue/receipt limit:** let work settle and inspect outcomes; do not defeat the bounds or replay Unknown actions.
- **Click/fill refusal:** obtain a fresh snapshot; password/file/authentication fields and sensitive operations remain unavailable.

Retain [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and every upstream notice: Playwright .NET is MIT, its separate JavaScript driver is Apache-2.0, and Node/browser components have independent terms. Browser binaries need their own distribution review.

Source: [service manifest](service-plugin.json), [BrowserServicePlugin.cs](BrowserServicePlugin.cs), [BrowserNetworkGate.cs](BrowserNetworkGate.cs), [BrowserResources.cs](BrowserResources.cs), [BrowserSystemRuntime.cs](BrowserSystemRuntime.cs).

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
