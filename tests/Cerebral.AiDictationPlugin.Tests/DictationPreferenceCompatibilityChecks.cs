using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Cerebral.AiDictationPlugin;
using Cerebral.Core;

internal static partial class Program
{
    private static async Task<int> RunPreferenceCompatibilityChecksAsync()
    {
        var checks = 0;
        void Verify(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks++; }
        var policy = new AiDictationSettingsPolicy();
        var configured = new AiDictationSettings
        {
            RuntimePath = @"C:\fixture\speech.exe", ModelPath = @"C:\fixture\model.gguf",
            InputDeviceName = "Saved fixture microphone", InputBoostDb = 9,
            NormalizeQuietAudio = false, NormalizationCalibrationSeconds = 7,
            AutoTranscribeOnSilence = true, SilenceWaitSeconds = 2.5,
            DockControls = false, AllowExternalTarget = true, GrammarCorrection = true,
            GrammarProfileId = "saved-profile", GrammarProfileRevision = 4
        };
        var old = JsonNode.Parse(AiDictationSettingsPolicy.Encode(configured).GetRawText())!.AsObject();
        old.Remove("liveWordsWhileSpeaking");
        JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
        PluginSettingsDocument Document(JsonElement value) => new("pegasus.ai-dictation", "global", null, "1.2.2", 1, value);
        var document = Document(Element(old));
        var admitted = policy.Admit(document, null, false);
        Verify(admitted.Accepted && admitted.SettingsVersion == 1 && !admitted.Values.GetProperty("liveWordsWhileSpeaking").GetBoolean(),
            "The exact saved 1.2.2 format loads with live words off.");
        foreach (var field in document.Values.EnumerateObject())
            Verify(JsonElement.DeepEquals(field.Value, admitted.Values.GetProperty(field.Name)), "An original saved preference changed: " + field.Name);
        Verify(AiDictationSettingsPolicy.Decode(admitted.Values) == configured, "Every configured value survives in-memory normalization.");
        Verify(policy.ExportPortable(document).SettingsVersion == 1 && policy.ExportComplete(document).Complete,
            "Portable and complete projections accept the known earlier document without saving it.");
        Verify(policy.AdmitComplete(document, document.Values).Accepted, "Complete admission can preserve prior local choices in the old schema.");
        foreach (var enabled in new[] { true, false })
        {
            var current = configured with { LiveWordsWhileSpeaking = enabled };
            Verify(AiDictationSettingsPolicy.Decode(policy.Admit(AiDictationSettingsPolicy.Document(current), null, false).Values) == current,
                "Current live-word choices must survive unchanged.");
        }
        void Reject(Action<JsonObject> mutate, string label)
        {
            var invalid = old.DeepClone().AsObject(); mutate(invalid);
            Verify(!policy.Admit(Document(Element(invalid)), null, false).Accepted, label);
        }
        Reject(value => value.Remove("modelPath"), "A missing original field is rejected.");
        Reject(value => value["unexpected"] = true, "Unknown fields remain rejected.");
        Reject(value => value["schemaVersion"] = 2, "Unknown schema versions remain rejected.");
        Reject(value => value["inputBoostDb"] = 25, "An out-of-range saved value remains rejected.");
        Reject(value => value["inputBoostDb"] = "not-a-number", "Malformed numeric values remain rejected.");
        Reject(value => value["modelPath"] = null, "Null original paths remain rejected.");
        Reject(value => value["liveWordsWhileSpeaking"] = "true", "A malformed new field remains rejected.");
        Reject(value => value["grammarProfileRevision"] = 0, "An inconsistent profile binding remains rejected.");
        using (var duplicate = JsonDocument.Parse(old.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal)))
            Verify(!policy.Admit(Document(duplicate.RootElement), null, false).Accepted, "Duplicate fields remain rejected.");

        var host = new TestHost { DataDirectory = Path.Combine(Path.GetTempPath(), "dictation-preferences-compatibility-" + Guid.NewGuid().ToString("N")) };
        await host.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
            new PluginSettingsTransferSaveRequest(new(document, 0, OwnerValidated: true)));
        var before = JsonSerializer.SerializeToElement(host.Settings);
        var capture = new FakeCapture(); var providerCalls = 0;
        using var plugin = new AiDictationPlugin(capture, _ => { providerCalls++; throw new InvalidOperationException("No speech provider should run."); });
        plugin.Initialize(host);
        Verify(plugin.ReadRuntimePreferences() == configured, "Startup hydrates the older saved preferences.");
        var settings = plugin.ConfigurationPanels.Single().CreateView();
        var load = Find<Button>(settings, "AiDictationLoadPreferences");
        await WaitAsync(() => load.IsEnabled);
        load.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => load.IsEnabled);
        Verify(Find<TextBlock>(settings, "AiDictationPreferenceStatus").Text.StartsWith("Saved preferences loaded.", StringComparison.Ordinal),
            "The existing Load saved preferences action recovers the supported old format.");
        Verify(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(host.Settings)), "Hydration and explicit Load never rewrite saved preferences or their revision.");
        Verify(!capture.Started.Task.IsCompleted && providerCalls == 0 && host.Submissions == 0,
            "Saved Auto does not start recording, inference or message submission.");
        return checks;
    }
}
