# Window Watch

Window Watch is an optional Cerebral desktop plugin for analyzing one selected Windows window. The composer action opens a floating panel. This project can be built separately from the Cerebral solution.

## Use

1. Open **Window Watch** and use the gear button to show **Options**. Refresh the window list if needed and select the target.
2. Enter the numeric loopback URL of a local OpenAI-compatible vision endpoint and its model name. For example, use http://127.0.0.1:8080/v1/chat/completions if that is the address your local model already serves.
3. Enter an optional context prompt. The camera button analyzes one snapshot. The play button starts automatic capture using the interval from Options, between 5 and 3600 seconds. Pause stops the schedule and cancels any pending analysis locally. Use play to resume with the same target and context. Starting a valid capture folds Options away. The focus button also folds the context prompt away, leaving the response, target, timer, and capture controls. Open Options or press the focus button again to restore the prompt; folding keeps its text and reply memory.
4. **Skip unchanged snapshots** compares the normalized pixels with the last successfully analyzed image. Manual analysis forces a fresh request. The outline tracks the selected window during monitoring. Focus changes leave that selection in place.
5. **Remember recent replies this session** includes up to the chosen number of recent replies, with at most 1,200 characters per reply. It starts off. **Clear replies** clears the response box and retained replies and keeps the context prompt.

Balanced mode scales the longest image side to at most 1,600 pixels and encodes JPEG. **Text detail** raises that cap to 2,560 pixels and uses lossless PNG after resizing. Use it when small text needs more detail. Each mode preserves the aspect ratio and caps its upload size.

Pause other model work before starting. Each panel allows one analysis at a time. Busy responses stop auto mode and show a message in the panel. After cancelling an in-flight request or encountering a timeout, check whether the model is still busy before restarting.

The panel uses Cerebral's live theme brushes when hosted and a window-local Blue White palette when opened without the host theme. All toolbar icons have keyboard focus, accessible labels, and tooltips. Highlighted buttons indicate an open section; the mode caption identifies automatic capture, pause, and optional reply retention.

## Requirements and boundaries

- Cortex's signed-in tray provides the AutoIt-backed window capture command. It needs a restored window that supports window-specific capture. A minimized window pauses automatic capture, and closing the selected window ends monitoring.
- Use a vision endpoint you run locally and trust with the selected window's contents. This plugin accepts unauthenticated HTTP on a numeric loopback address. The model must accept image_url content in chat completions. An already running model can be reused if it offers this API.
- Cerebral chat accepts images through its attachment and workflow route. The existing separate desktop inference command accepts text messages. Reusing a private Worker-managed vision instance for this panel would require a supported stateless image route from the host.
- Cortex publishes each snapshot to a private temporary file. Window Watch checks the capture receipt and removes that file before submitting the image. The response and optional reply memory live in the panel's memory for that session.
- The package manifest defaults to disabled. Previewing or installing the package follows the normal Cerebral desktop plugin lifecycle.

## Build

Use .NET 10 on Windows. Build the project with an isolated ArtifactsPath and set DesktopPluginSdkPath to the host's Cerebral.Desktop.Plugins.dll. The manifest is copied beside the plugin DLL. The host supplies its SDK assembly.
