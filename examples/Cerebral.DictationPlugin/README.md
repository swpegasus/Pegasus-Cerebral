# Pegasus Dictation

Pegasus Dictation is the Windows speech package `pegasus.dictation`, manifest version **1.0.8**. It adds a **🎙 Dictate** action to supported Cerebral text fields, including the message composer and open question answers. It uses Windows' installed desktop speech recognizer, without a separate GGUF model or a cloud speech account.

## Requirements and installation

You need Windows with a compatible desktop speech recognizer/language installed, microphone access for desktop applications, and a working input device. This plugin uses **System.Speech 10.0.12** and the Cerebral desktop API version 1.

Build the package, then open Configuration **Plugins & tools > Desktop plugins**, choose **Install…** and select its `desktop-plugin.json`. Select **Pegasus Dictation**, then **Manage > Enable > Go**. Its manifest defaults to disabled.

Windows Dictation and [AI Dictation](../Cerebral.AiDictationPlugin/README.md) share the exclusive `dictation.input` group. Select the input provider you want through the catalog; do not expect both to supply the same field simultaneously. AI Dictation has the higher declared priority. Check **Inspect** and catalog diagnostics if the expected action is absent.

## Set up and dictate

Select the plugin and choose **Manage > Configure… > Go** to open **Dictation** settings.

1. Choose **Speech language** and a recognition filter. **Balanced** defaults to confidence **0.45**; Lenient is 0.30 and Strict is 0.60.
2. Add optional vocabulary, one phrase per line. Add corrections as `heard phrase => replacement`.
3. Use **Test microphone and profile** to inspect recognition and the live input meter. Test text is not saved by the plugin.
4. **Open Speech Recognition Center** and **Train Windows speech profile** open Windows' own speech/profile tools when available. Windows owns training and speech profiles; the plugin does not record a “training completed” flag.
5. Choose **Save preferences**. **Load saved preferences** discards unsaved settings edits. **Prepare existing local preferences** is an explicit migration preview; review and save to apply it.

Focus the intended text field and choose **Dictate**. Speak and pause for recognition; choose the action again to stop. Recognized phrases are inserted into that field for editing. Changing the conversation stops the old session rather than redirecting speech into another chat.

The default preferences have no fixed recognizer ID, empty vocabulary/corrections, and voice controls off. The language must resolve to an available installed Windows recognizer. Vocabulary/correction limits are bounded; invalid preference files or unavailable saved choices are reported, not silently rewritten.

## Optional voice controls

**Enable explicit voice commands in the main message box** is a separate opt-in. Exact, sufficiently confident command phrases can invoke the host's existing actions:

| Phrase | Action |
|---|---|
| Cerebral command send message | Submit the current Main draft when work is not running |
| Cerebral command steer message | Add the current draft to running work |
| Cerebral command stop work | Ask the host to stop active work |
| Cerebral command stop dictation | Stop speech capture only |

These controls are restricted to the current bound Main composer; question-answer fields do not gain send/steer authority. Incompatible active-work states are rejected. With voice controls enabled, speech can submit or steer a message: review this setting before using dictation around other speech. Ordinary recognized prose is not treated as a command merely because it mentions one.

## Build

From the repository root on Windows:

~~~powershell
dotnet build examples/Cerebral.DictationPlugin/Cerebral.DictationPlugin.csproj -c Release
~~~

The project uses `net10.0-windows`, references current Core/Desktop.Plugins, restores System.Speech 10.0.12, and copies the manifest and licensing files. It inherits the product assembly version; the plugin catalog version is the manifest's 1.0.8. Follow [BUILD](../../docs/BUILD.md) and [PLUGINS](../../docs/PLUGINS.md) for the matching host and package workflow. Keep runtime dependencies and notices with the package.

## Privacy, troubleshooting and license

Speech recognition is local to Windows. Preferences are saved through Cerebral's central package settings; legacy `settings.json` is read only by the explicit prepare action. Vocabulary and corrections can contain personal terms, so treat settings exports accordingly. No Worker tool or model call is needed for recognition.

If speech support is missing, install an appropriate Windows desktop speech language and use **Check speech support**. Check Windows microphone privacy/device settings when the meter shows no input. A strict confidence filter can reject otherwise audible phrases. If a Windows training component is absent, follow the diagnostic's Windows speech guidance. A source build alone does not verify recognition hardware, a language pack, or training availability.

First-party code uses the [Pegasus Community License 1.0](../../LICENSE). Preserve [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [System.Speech notices](Licensing/System.Speech-10.0.12/THIRD-PARTY-NOTICES.TXT); System.Speech is MIT-licensed and Windows components retain their own terms.

## Source notes

[DictationPlugin.cs](DictationPlugin.cs) defines the UI and text-entry action; [DictationSupport.cs](DictationSupport.cs) defines recognition, voice commands and Windows training integration; [DictationSettingsPolicy.cs](DictationSettingsPolicy.cs) defines central preference admission.
