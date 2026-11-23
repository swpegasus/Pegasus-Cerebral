# Window Watch

Analyze **one explicitly selected Windows window** with a local vision model, either on demand or on a change-aware timer. The plugin provides a floating panel, not general desktop control.

**Desktop package:** `pegasus.window-watch`, version **0.1.2**, **disabled by default**.

## Requirements, build and installation

Requires Windows x64, .NET 10, a compatible `Cerebral.Desktop.Plugins` SDK, installed Cortex PAO with a working signed-in-user window-capture route, and a separately running trusted local vision endpoint. No model, projector or inference runtime is downloaded by this plugin.

```powershell
dotnet build .\examples\Cerebral.WindowWatchPlugin\Cerebral.WindowWatchPlugin.csproj -c Release
```

The default output is `bin/Release/net10.0-windows/` beneath this project, including the native DLL and copied `desktop-plugin.json`. An advanced compatibility build may supply `DesktopPluginSdkPath`; it must match the intended host.

Install from Configuration → **Desktop plugins → Install…**, review the package disabled, then **Manage → Enable → Go**. Open the **Window Watch** composer action in Main. Refresh or reopen Main if the action is not mounted. Enabling this desktop package does not itself load a model or restart the Worker.

## First analysis

1. Open **Options** with the gear button, **Refresh windows**, and select the target window.
2. Enter the endpoint of a local OpenAI-compatible vision server and the model name it serves. Only **HTTP on a numeric loopback address** with exact path `/v1/chat/completions` is accepted; for example `http://127.0.0.1:8080/v1/chat/completions`. Remote hosts, credentials, query strings and fragments are refused.
3. Enter optional context describing what to examine.
4. Use the **camera** control for one snapshot. The **play** control begins automatic analysis; **pause** stops scheduling and cancels the local pending analysis wait.
5. **Clear replies** clears the displayed response and remembered replies while preserving the context prompt.

The vision server must accept `image_url` content. This panel does not attach to an arbitrary private Worker-managed model instance through Cerebral's normal text inference command.

## Options and defaults

| Option | Default / range |
| --- | --- |
| Automatic interval | 30 seconds; accepted range 5–3,600 |
| Skip unchanged snapshots | On; manual analysis still forces a request |
| Text detail | Off; balanced image up to 1,600 pixels on the longest side, JPEG |
| Text detail enabled | Up to 2,560 pixels, lossless PNG after resizing |
| Outline selected window | On |
| Keep panel on top | On |
| Remember recent replies this session | Off; selected limit initially 3, configurable 1–10 |

Options, prompt, selected window and replies are panel-session state in this source, not a saved global configuration. Optional reply memory bounds each prior reply to 1,200 characters and labels it as potentially stale. Closing the panel clears it. Folding Options or using focus mode preserves the current prompt/replies.

The client requests at most 640 output tokens with a 90-second deadline. Each panel admits one analysis at a time. These settings do not reserve a model slot or fence unrelated inference.

## Capture, privacy and troubleshooting

The plugin starts the installed Cortex `pao.exe` fixed window-capture operation for the selected window handle. It validates the capture receipt, bytes and hash, decodes only bounded images, and cleans its private temporary capture files before sending the encoded image to the local endpoint. The selected window's pixels can contain sensitive text; choose the target and endpoint accordingly.

Monitoring keeps the selected window when focus changes. A minimized target pauses capture; a closed target ends monitoring. Blank/unsupported captures are refused. The model's response is advice about visible content, not authority to click, type or execute.

- **Capture unavailable/denied:** check installed Cortex and its signed-in-user execution route, restore the target, and inspect the surfaced capture error. Do not weaken capture permissions.
- **Endpoint rejected:** use a numeric loopback HTTP URL with the exact supported path and a valid image-capable model name.
- **Busy or timeout:** pause other model work and check whether the backend request is still running before restarting. Cancelling the UI wait is not proof inference stopped.
- **Small text unreadable / image too large:** try Text detail, resize the target, or reduce its visible content. Image limits remain enforced.
- **No snapshot changes:** automatic mode skips identical successfully analyzed pixels; use the camera for an explicit fresh analysis.

Source: [WindowWatchPlugin.cs](WindowWatchPlugin.cs), [WatchWindow.cs](WatchWindow.cs), [WatchWindow.Layout.cs](WatchWindow.Layout.cs), [PaoWindowCapture.cs](PaoWindowCapture.cs), [VisionClient.cs](VisionClient.cs), [ReplyMemory.cs](ReplyMemory.cs). Cortex, vision models and runtimes retain their separate licenses.

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
