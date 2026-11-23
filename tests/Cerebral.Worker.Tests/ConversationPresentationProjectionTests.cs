using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunPresentationProjectionOnlyAsync()
    {
        RunPresentationProjectionChecks();
        return Task.CompletedTask;
    }

    private static int RunPresentationProjectionChecks()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Conversation presentation projection: " + name);
            checks++;
        }
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralPresentationProjection-" + Guid.NewGuid().ToString("N")));
        try
        {
            var extension = new InterfaceToolExtension(new StateStore(directory), new EventHub(), () => 2);
            var descriptor = extension.Tools.Single(tool => tool.Name == InterfaceToolExtension.ToolName);
            var originalSchema = descriptor.InputSchema.GetRawText();
            var originalDescription = descriptor.Description;
            const string otherName = "file.read", otherDescription = "Read a bounded ordinary file.";
            var otherSchema = CerebralJson.Element(new
            {
                type = "object", properties = new { path = new { type = "string" } },
                required = new[] { "path" }, additionalProperties = false
            });
            var descriptions = new Dictionary<string, string>
                { [descriptor.Name] = descriptor.Description, [otherName] = otherDescription };
            var schemas = new Dictionary<string, JsonElement>
                { [descriptor.Name] = descriptor.InputSchema, [otherName] = otherSchema };
            var names = new[] { descriptor.Name, otherName };
            var fixture = new Fixture(warm: false);
            fixture.History([]);
            fixture.Catalog(forcedNames: names, descriptions: descriptions);
            fixture.Describe(names);
            fixture.Catalog(exact: true, inputSchemas: schemas, descriptions: descriptions);
            const string header = "\nAccepted metadata and host evidence:\n";
            using var metadata = JsonDocument.Parse(fixture.MetadataText[(fixture.MetadataText.IndexOf(header, StringComparison.Ordinal) + header.Length)..]);
            JsonElement Item(JsonElement items, string name) => items.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            var available = metadata.RootElement.GetProperty("availableTools");
            var selected = metadata.RootElement.GetProperty("selectedToolSchemas");
            var readable = Item(selected, descriptor.Name);
            var projectedKinds = readable.GetProperty("inputSchema").GetProperty("properties").GetProperty("kind")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
            var generated = fixture.Command.Arguments.GetProperty("responseSchema").GetProperty("oneOf")
                .EnumerateArray().Single(variant => variant.GetProperty("properties").TryGetProperty("name", out var name) &&
                    name.TryGetProperty("const", out var exact) && exact.GetString() == descriptor.Name);
            var generatedKinds = generated.GetProperty("properties").GetProperty("arguments").GetProperty("properties")
                .GetProperty("kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
            Check(projectedKinds.SequenceEqual(new[] { "file", "folder", "application" }) && projectedKinds.SequenceEqual(generatedKinds),
                "readable exact schema agrees with the generated file/folder/application route");
            var projectedDescription = readable.GetProperty("description").GetString()!;
            Check(projectedDescription.Contains("file, folder or application", StringComparison.Ordinal) &&
                projectedDescription.Contains("plugin.present with its exact verified built desktop-plugin.json", StringComparison.Ordinal) &&
                !projectedDescription.Contains("or reviewed native plugin package", StringComparison.Ordinal),
                "readable description names the verified native plugin route without advertising its compatibility alias");
            Check(Item(available, descriptor.Name).GetProperty("description").GetString() == projectedDescription &&
                Item(available, descriptor.Name).GetProperty("inputSchema").ValueKind == JsonValueKind.Null,
                "discovery uses the same description without inventing an unselected exact schema");
            var retained = Item(fixture.Step.State.GetProperty("selected"), descriptor.Name);
            Check(JsonElement.DeepEquals(retained.GetProperty("inputSchema"), descriptor.InputSchema) &&
                retained.GetProperty("description").GetString() == originalDescription,
                "the readable projection preserves the raw retained provider contract");
            Check(descriptor.InputSchema.GetRawText() == originalSchema && descriptor.Description == originalDescription &&
                descriptor.InputSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
                    .EnumerateArray().Any(kind => kind.GetString() == "plugin"),
                "the actual published Worker descriptor remains unchanged including its compatibility kind");
            Check(readable.GetProperty("capability").GetString() == retained.GetProperty("capability").GetString() &&
                Item(available, descriptor.Name).GetProperty("capability").GetString() == retained.GetProperty("capability").GetString(),
                "projection changes no capability or grant coordinate");
            Check(JsonElement.DeepEquals(Item(selected, otherName).GetProperty("inputSchema"), otherSchema) &&
                Item(selected, otherName).GetProperty("description").GetString() == otherDescription &&
                Item(available, otherName).GetProperty("description").GetString() == otherDescription,
                "unrelated readable tool schemas and descriptions are preserved");
            Check(fixture.SystemText.Contains("save operation while changed value A is current", StringComparison.Ordinal) &&
                fixture.SystemText.Contains("Minimum interaction is not full behavior coverage", StringComparison.Ordinal),
                "catalog projection preserves strengthened QA behavior and save-confirmation guidance");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        Console.WriteLine("Conversation presentation projection: " + checks + " deterministic catalog/schema checks passed; no provider/native/model execution.");
        return checks;
    }
}
