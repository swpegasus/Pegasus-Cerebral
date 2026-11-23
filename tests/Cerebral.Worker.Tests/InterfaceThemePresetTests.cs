using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static partial class RemoteWorkerManagementTests
{
    public static async Task RunThemePresetsAsync()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void Reject(Action action, string label)
        {
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
            { checks++; return; }
            throw new InvalidOperationException("Accepted invalid theme: " + label);
        }
        static JsonElement Json(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        static JsonObject Preset() => JsonNode.Parse(DesktopThemePresetPolicy.Serialize(DesktopThemePresetPolicy.BlueWhite))!.AsObject();

        var good = Preset();
        Check(DesktopThemePresetPolicy.Parse(Json(good.ToJsonString())).Id == "blue-white" &&
            DesktopThemePresetPolicy.PaletteTokens.Count == 16, "Built-in preset uses the portable schema and paired palettes.");
        foreach (var (name, value) in new[] { ("schemaVersion", JsonValue.Create(2)), ("id", JsonValue.Create("../theme")),
            ("name", JsonValue.Create("  unnamed")), ("light", JsonValue.Create("white")) })
        {
            var invalid = Preset(); invalid[name] = value;
            Reject(() => DesktopThemePresetPolicy.Parse(Json(invalid.ToJsonString())), name);
        }
        foreach (var tokenValue in new[] { "#FFF", "#FFFFFFFF", "red", "#00GG11", "#FFFFFF\n" })
        {
            var invalid = Preset(); invalid["light"]!["ink"] = tokenValue;
            Reject(() => DesktopThemePresetPolicy.Parse(Json(invalid.ToJsonString())), "unsupported color " + tokenValue);
        }
        var missing = Preset(); missing["dark"]!.AsObject().Remove("hover");
        Reject(() => DesktopThemePresetPolicy.Parse(Json(missing.ToJsonString())), "missing token");
        var extra = Preset(); extra["sourcePath"] = "ignored.xaml";
        Reject(() => DesktopThemePresetPolicy.Parse(Json(extra.ToJsonString())), "executable/resource fields");
        var extraColor = Preset(); extraColor["light"]!["script"] = "ignored";
        Reject(() => DesktopThemePresetPolicy.Parse(Json(extraColor.ToJsonString())), "unknown palette token");
        var unreadable = Preset(); unreadable["dark"]!["ink"] = "#16243A";
        Reject(() => DesktopThemePresetPolicy.Parse(Json(unreadable.ToJsonString())), "unreadable text");
        var badSelection = Preset(); badSelection["dark"]!["selectionText"] = "#F3F7FF";
        Reject(() => DesktopThemePresetPolicy.Parse(Json(badSelection.ToJsonString())), "unreadable selection");
        foreach (var mode in new[] { "light", "dark" })
        foreach (var surface in new[] { "window", "surface", "raisedSurface", "codeSurface" })
        foreach (var token in new[] { "accent", "errorText" })
        {
            var invalid = Preset();
            var palette = invalid[mode]!.AsObject();
            palette[token] = palette[surface]!.GetValue<string>();
            palette[token == "accent" ? "onAccent" : "errorBackground"] = palette["ink"]!.GetValue<string>();
            Reject(() => DesktopThemePresetPolicy.Parse(Json(invalid.ToJsonString())),
                mode + "." + token + " on " + surface);
        }
        var duplicate = good.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Reject(() => DesktopThemePresetPolicy.Parse(Json(duplicate)), "duplicate root field");
        var duplicateColor = good.ToJsonString().Replace("\"window\":\"#F3F6FB\"", "\"window\":\"#F3F6FB\",\"window\":\"#F3F6FB\"", StringComparison.Ordinal);
        Reject(() => DesktopThemePresetPolicy.Parse(Json(duplicateColor)), "duplicate color field");
        Reject(() => DesktopThemePresetPolicy.Parse(Json(good.ToJsonString()[..^1] + new string(' ', 16384) + "}")), "oversize document");

        await using var h = new Harness();
        var themes = new InterfaceThemeStore(h.Store);
        Check(themes.Get().Revision == 0 && themes.Get().Preset.Name == "Blue White",
            "A new dataset defaults without creating a preference.");
        var noReset = themes.Set(CerebralJson.Element(new { expectedRevision = 0, preset = (object?)null }));
        Check(!noReset.Changed && noReset.State.Revision == 0, "Resetting an inherited default is a no-op.");
        var custom = Preset(); custom["id"] = "owner.sky"; custom["name"] = "Owner Sky"; custom["light"]!["line"] = "#CADAF2";
        var changed = themes.Set(Json("{\"expectedRevision\":0,\"preset\":" + custom.ToJsonString() + "}"));
        Check(changed.Changed && changed.State.Revision == 1 && changed.State.Preset.Id == "owner.sky",
            "Exact CAS stores a custom theme once.");
        var noOp = themes.Set(Json("{\"expectedRevision\":1,\"preset\":" + custom.ToJsonString() + "}"));
        Check(!noOp.Changed && noOp.State.Revision == 1, "Same normalized preset is a no-op.");
        Reject(() => themes.Set(Json("{\"expectedRevision\":0,\"preset\":" + custom.ToJsonString() + "}")), "stale CAS");
        Reject(() => themes.Set(Json("{\"expectedRevision\":1,\"preset\":" + unreadable.ToJsonString() + "}")), "invalid write");
        Reject(() => themes.Set(Json("{\"expectedRevision\":1,\"expectedRevision\":1,\"preset\":null}")), "duplicate command field");
        Reject(() => themes.Set(Json("{\"expectedRevision\":1,\"preset\":null,\"path\":\"theme.json\"}")), "unexpected command field");
        Reject(() => themes.Read(Json("{\"path\":\"theme.json\"}")), "unexpected inspection field");
        Check(themes.Get().Revision == 1 && themes.Get().Preset.Id == "owner.sky", "Rejected edits preserve saved state.");
        var reopened = new InterfaceThemeStore(new StateStore(h.Directory)).Get();
        Check(reopened.Revision == 1 && DesktopThemePresetPolicy.Serialize(reopened.Preset) == DesktopThemePresetPolicy.Serialize(changed.State.Preset),
            "A fresh SQLite store instance retains exact exported color data.");
        Check((await h.Call("interface.theme.get", new { })).Success, "Owner command gets the effective preset.");
        var actor = new AgentProfile { Id = "theme-admin", Name = "Theme admin", Capabilities = ["administration"] };
        h.Store.Put("agent", actor.Id, actor);
        foreach (var operation in new[] { "interface.theme.get", "interface.theme.set" })
        {
            var denied = await h.Commands.ExecuteAsync(new(operation,
                CerebralJson.Element(new { expectedRevision = 1, preset = (object?)null }), actor.Id));
            Check(!denied.Success, "Model actors cannot invoke owner appearance command " + operation + ".");
        }
        var reset = await h.Call("interface.theme.set", new { expectedRevision = 1, preset = (object?)null });
        Check(reset.Success && CerebralJson.Read<DesktopThemePresetState>(reset.Data) is { Revision: 2, Preset.Id: "blue-white" },
            "Owner reset returns effective default with a new revision.");
        Check(h.FactoryCalls == 0, "Themes perform no inference or remote owner connection.");
        await using (var eventHarness = new Harness())
        {
            var subscriber = eventHarness.Events.Subscribe();
            try
            {
                void Events(long? revision, string label)
                {
                    var count = 0;
                    while (subscriber.Reader.TryRead(out var item))
                    {
                        if (item.Type != "interface.theme.updated") continue;
                        count++;
                        Check(revision is not null && item.JobId is null &&
                            CerebralJson.Read<DesktopThemePresetState>(item.Data).Revision == revision.Value,
                            label + " carries only the committed appearance revision.");
                    }
                    Check(count == (revision is null ? 0 : 1), label + " emits exactly the expected number of appearance events.");
                }
                Check((await eventHarness.Call("interface.theme.set", new { expectedRevision = 0, preset = (object?)null })).Success,
                    "Owner inherited reset is accepted.");
                Events(null, "Inherited reset");
                var eventPreset = Preset(); eventPreset["id"] = "event-theme";
                Check((await eventHarness.Call("interface.theme.set", Json("{\"expectedRevision\":0,\"preset\":" + eventPreset.ToJsonString() + "}"))).Success,
                    "Owner custom theme commits through the dispatcher.");
                Events(1, "Changed custom preset");
                eventPreset["light"]!["line"] = eventPreset["light"]!["line"]!.GetValue<string>().ToLowerInvariant();
                Check((await eventHarness.Call("interface.theme.set", Json("{\"expectedRevision\":1,\"preset\":" + eventPreset.ToJsonString() + "}"))).Success,
                    "Hex case normalizes before no-op detection.");
                Events(null, "Normalized no-op");
                Check(!(await eventHarness.Call("interface.theme.set", new { expectedRevision = 0, preset = (object?)null })).Success,
                    "Owner stale appearance CAS is rejected.");
                Events(null, "Stale CAS");
                Check(!(await eventHarness.Call("interface.theme.set", Json("{\"expectedRevision\":1,\"preset\":" + unreadable.ToJsonString() + "}"))).Success,
                    "Owner unreadable appearance is rejected.");
                Events(null, "Invalid preset");
                Check((await eventHarness.Call("interface.theme.set", new { expectedRevision = 1, preset = (object?)null })).Success,
                    "Owner custom-to-default reset commits.");
                Events(2, "Changed reset");
                var resetReopened = new InterfaceThemeStore(new StateStore(eventHarness.Directory)).Get();
                Check(resetReopened.Revision == 2 && resetReopened.Preset.Id == "blue-white",
                    "Reopening SQLite after reset preserves the revision and effective built-in palette.");
            }
            finally { eventHarness.Events.Unsubscribe(subscriber.Id); }
        }
        Console.WriteLine($"Portable theme presets: {checks} deterministic JSON/SQLite/owner-command checks passed; no live owner data, UI, inference or network.");
    }
}
