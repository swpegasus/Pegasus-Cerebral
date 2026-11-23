using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real temporary SQLite plus deterministic JSON fixtures; no tool, model, provider or native stage dispatch.</summary>
public static class WorkflowDataCatalogTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-data-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + message);
            checks++; Console.WriteLine("PASS: " + message);
        }
        try
        {
            var state = new StateStore(Path.Combine(root, "catalog"));
            var catalog = new WorkflowDataCatalog(state);
            var edition = Edition();
            var first = catalog.Register("project", edition);
            Check(first.Created && first.Entry.Hash == WorkflowDataCatalog.Hash(edition) && first.Entry.Hash.Length == 64,
                "registration persists one exact project edition with a canonical content hash");
            Check(!catalog.Register("project", edition).Created &&
                catalog.Get("project", edition.Id, edition.Version, first.Entry.Hash).Hash == first.Entry.Hash,
                "identical registration and exact get retain the same immutable edition");
            Check(Rejected<WorkflowConflictException>(() => catalog.Register("project", edition with { Description = "Changed content." })) &&
                catalog.Get("project", edition.Id, edition.Version, first.Entry.Hash).Edition.Description == edition.Description,
                "changed same-ID/version content conflicts without replacing the original");
            var higher = catalog.Register("project", edition with { Version = "v2" });
            Check(higher.Created && catalog.Get("project", edition.Id, "v1", first.Entry.Hash).Edition.Version == "v1",
                "another exact version coexists without selecting latest or rebinding an existing edition");
            Check(catalog.Register("other-project", edition).Created &&
                Rejected<KeyNotFoundException>(() => catalog.Get("absent-project", edition.Id, edition.Version, first.Entry.Hash)) &&
                Rejected<InvalidDataException>(() => catalog.Get("project", edition.Id, edition.Version, new string('0', 64))),
                "project scope and exact expected hash are enforced at lookup");

            var stages = edition.Stages.ToArray(); var capabilities = edition.RequiredCapabilities.ToArray();
            var mutable = edition with { Id = "mutable", Stages = stages, RequiredCapabilities = capabilities };
            var admitted = catalog.Register("project", mutable);
            stages[0] = stages[0] with { Tool = "file.edit" }; capabilities[0] = "filesystem.write";
            Check(admitted.Entry.Edition.Stages[0].Tool == "file.read" &&
                catalog.Get("project", "mutable", "v1", admitted.Entry.Hash).Edition.RequiredCapabilities.Single() == "filesystem.read",
                "registration and returned entry freeze caller-owned arrays and nested JSON");
            var recovered = new WorkflowDataCatalog(new StateStore(state.DirectoryPath));
            Check(recovered.Get("project", edition.Id, edition.Version, first.Entry.Hash).Edition.Output.GetRawText() ==
                first.Entry.Edition.Output.GetRawText(),
                "exact edition survives actual SQLite connection/store reload");

            var semantic = edition with
            {
                Id = "semantic", RequiredCapabilities = ["filesystem.read", "diagnostics"],
                Stages = [new("read", "file.read", Parse("{\"a\":1,\"b\":{\"y\":2.0,\"x\":-0}}"))]
            };
            var reordered = semantic with
            {
                RequiredCapabilities = ["diagnostics", "filesystem.read"],
                Stages = [new("read", "file.read", Parse("{\"b\":{\"x\":0.0,\"y\":2e0},\"a\":1.00}"))]
            };
            Check(WorkflowDataCatalog.Hash(semantic) == WorkflowDataCatalog.Hash(reordered) &&
                !catalog.Register("project", semantic).Entry.Hash.Any(char.IsUpper) &&
                !catalog.Register("project", reordered).Created,
                "object order, equivalent decimal spelling and capability-set order do not change edition identity");
            var exactLarge = semantic with { Stages = [new("read", "file.read", Parse("{\"a\":10000000000000001}"))] };
            var roundedLarge = semantic with { Stages = [new("read", "file.read", Parse("{\"a\":10000000000000000}"))] };
            Check(WorkflowDataCatalog.Hash(exactLarge) != WorkflowDataCatalog.Hash(roundedLarge),
                "canonical number hashing preserves exact decimal identity beyond double precision");
            Check(WorkflowDataCatalog.Hash(semantic with { Stages = semantic.Stages.Append(new("next", "file.read", Element(new { }))).ToArray() }) !=
                WorkflowDataCatalog.Hash(semantic with { Stages = new[] { new WorkflowDataStage("next", "file.read", Element(new { })), semantic.Stages[0] } }),
                "stage order remains material in the canonical edition hash");

            var page = catalog.List("project", 2);
            var secondPage = catalog.List("project", 2, page.NextAfter);
            Check(page.Entries.Count == 2 && page.NextAfter is not null &&
                !page.Entries.Select(x => (x.Id, x.Version)).Intersect(secondPage.Entries.Select(x => (x.Id, x.Version))).Any(),
                "concise metadata pages use a stable exact ordinal cursor without repeated coordinates");
            Check(!JsonSerializer.Serialize(page).Contains("ARGUMENT_SENTINEL", StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(page).Contains("InputSchema", StringComparison.Ordinal),
                "list metadata excludes edition templates and schema bodies");
            Check(Rejected<ArgumentException>(() => catalog.List("other-project", 2, page.NextAfter)) &&
                Rejected<ArgumentException>(() => catalog.List("project", 2, "not-a-canonical-cursor")) &&
                Rejected<ArgumentOutOfRangeException>(() => catalog.List("project", 41)),
                "cross-project, malformed cursors and oversized page requests are rejected");

            var concurrent = new WorkflowDataCatalog(new StateStore(Path.Combine(root, "concurrent")));
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => concurrent.Register("project", edition))));
            Check(outcomes.Count(x => x.Created) == 1 && concurrent.List("project").Entries.Count == 1,
                "concurrent identical registrations commit one edition and one metadata row");

            var atomicState = new StateStore(Path.Combine(root, "atomic"));
            var atomic = new WorkflowDataCatalog(atomicState);
            Sql(atomicState, """
                CREATE TRIGGER fail_workflow_index BEFORE INSERT ON records
                WHEN NEW.kind='workflow-data' AND NEW.id LIKE 'project-%'
                BEGIN SELECT RAISE(ABORT,'fixture index write failure'); END;
                """);
            Check(Rejected<SqliteException>(() => atomic.Register("project", edition)) &&
                Scalar(atomicState, "SELECT COUNT(*) FROM records WHERE kind='workflow-data'") == 0,
                "actual SQLite index-write failure rolls back the earlier edition insert");
            Sql(atomicState, "DROP TRIGGER fail_workflow_index");
            Check(atomic.Register("project", edition).Created && atomic.List("project").Entries.Count == 1,
                "registration succeeds after the isolated transaction fault is removed");
            Check(Rejected<InvalidOperationException>(() => atomicState.Locked<bool>(() =>
                {
                    atomic.Register("project", edition with { Version = "outer-rollback" });
                    throw new InvalidOperationException("fixture outer transaction abort");
                })) && atomic.List("project").Entries.Count == 1,
                "nested registration uses the existing outer transaction and rolls back with it");

            Sql(state, """
                UPDATE records SET body=json_set(body,'$.edition.description','TAMPERED')
                WHERE kind='workflow-data' AND json_extract(body,'$.projectId')='project'
                  AND json_extract(body,'$.edition.id')='inspect-build' AND json_extract(body,'$.edition.version')='v1';
                """);
            Check(Rejected<InvalidDataException>(() => recovered.Get("project", edition.Id, edition.Version, first.Entry.Hash)),
                "stored content tampering fails the original exact hash after reload");

            var quota = new WorkflowDataCatalog(new StateStore(Path.Combine(root, "quota")));
            for (var i = 0; i < WorkflowDataCatalog.MaximumEditionsPerProject; i++)
                quota.Register("project", edition with { Id = "edition-" + i });
            Check(Rejected<InvalidOperationException>(() => quota.Register("project", edition with { Id = "edition-overflow" })) &&
                !quota.Register("project", edition with { Id = "edition-0" }).Created &&
                quota.Register("other-project", edition).Created,
                "128-edition project quota keeps exact retries available and does not consume another project's capacity");

            foreach (var invalid in InvalidEditions(edition))
                Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.Validate(invalid)),
                    "invalid DATA schema/coordinate/capability/stage/template/bound is rejected");
            Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.Validate(edition with
                { OutputSchema = Parse("{\"type\":\"integer\"}"), Output = Element("wrong") })),
                "a constant output must satisfy its declared output schema");
            foreach (var output in new[] { Element(7), Element("plain"), Element((object?)null), Element(true) })
            {
                var type = output.ValueKind switch
                { JsonValueKind.Number => "integer", JsonValueKind.String => "string", JsonValueKind.Null => "null", _ => "boolean" };
                WorkflowDataCatalog.Validate(edition with { OutputSchema = Element(new { type }), Output = output });
            }
            Check(true, "primitive constant outputs are accepted with their exact output schemas");
            var objectInput = Element(new { path = "source.txt" });
            foreach (var schema in new[]
            {
                Element(new { }),
                Parse("{\"oneOf\":[{\"type\":\"object\"},{\"type\":\"integer\"}]}")
            })
            {
                WorkflowDataCatalog.Validate(edition with { InputSchema = schema });
                Cerebral.Engine.Workflows.WorkflowSchema.Validate(objectInput, schema);
            }
            Check(true, "open and oneOf input schemas retain actual object-input admission");

            var allowed = new[] { new ToolDescriptor("file.read", "Read only.", Element(new { type = "object" }), "filesystem.read") };
            WorkflowDataCatalog.ValidateToolNames(edition, allowed);
            Check(true, "exact allowed descriptor and declared capability pass without any dispatch");
            Check(Rejected<UnauthorizedAccessException>(() => WorkflowDataCatalog.ValidateToolNames(edition, [])) &&
                Rejected<UnauthorizedAccessException>(() => WorkflowDataCatalog.ValidateToolNames(edition with { RequiredCapabilities = [] }, allowed)) &&
                Rejected<ArgumentException>(() => WorkflowDataCatalog.ValidateToolNames(edition, allowed.Concat(allowed).ToArray())),
                "missing tools, undeclared capabilities and ambiguous descriptor names fail tool admission");
            var pao = edition with
            {
                RequiredCapabilities = ["mcp.cortex"],
                Stages = [new("read", "mcp.cortex.pao", Element(new { op = "stat", src = "source.txt" }))]
            };
            WorkflowDataCatalog.ValidateToolNames(pao,
                [new("mcp.cortex.pao", "Host-filtered fixed PAO action.", Element(new { type = "object" }), "mcp.cortex")]);
            Check(true, "a host-filtered fixed PAO descriptor remains possible without inventing recipe registration");
            foreach (var prohibited in new[]
            {
                "models.load", "engine.status", "plugin.present", "plugins.install", "deployment.publish",
                "jobs.workflow.submit", "workflow-data.register", "agents.delegate", "collaboration.submit",
                "program.source.generate", "MODEL.GENERATE"
            })
                Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.Validate(edition with
                    { Stages = [new("read", prohibited, Element(new { }))] })),
                    "first-slice prohibited operation " + prohibited + " cannot become a data stage");

            var input = Parse("{\"a/b\":{\"~name\":[\"zero\",{\"value\":9}]},\"~1\":\"once\",\"\":true,\"01\":\"object-key\"}");
            var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                { ["read"] = Element(new { name = "file.read", content = "{\"inner\":1}", truncated = false }) };
            var resolved = WorkflowDataCatalog.ResolveTemplate(Parse("""
                {"number":{"$input":"/a~1b/~0name/1/value"},"stage":{"$stage":{"id":"read","pointer":"/content"}},
                 "empty":{"$input":"/"},"escapedOnce":{"$input":"/~01"},"objectIndex":{"$input":"/01"}}
                """), input, results);
            Check(resolved.GetProperty("number").GetInt32() == 9 && resolved.GetProperty("empty").GetBoolean() &&
                resolved.GetProperty("escapedOnce").GetString() == "once" && resolved.GetProperty("objectIndex").GetString() == "object-key" &&
                resolved.GetProperty("stage").GetString() == "{\"inner\":1}",
                "RFC6901 escapes, empty object key and primitive ToolData content resolve as exact typed values");
            Check(WorkflowDataCatalog.ResolveTemplate(Parse("{\"$input\":\"\"}"), input, results).GetRawText() == input.GetRawText() &&
                WorkflowDataCatalog.ResolveTemplate(Parse("{\"$stage\":{\"id\":\"read\",\"pointer\":\"\"}}"), input, results)
                    .GetProperty("name").GetString() == "file.read",
                "empty pointer selects the whole input or exact previous ToolData JSON");
            var reservedData = Parse("{\"$input\":\"literal data, not a second template\"}");
            Check(WorkflowDataCatalog.ResolveTemplate(Parse("{\"$input\":\"\"}"), reservedData, results)
                .GetProperty("$input").GetString() == "literal data, not a second template",
                "selected external values are copied once without recursive template interpretation");
            foreach (var pointer in new[] { "#/a", "a", "/a~2b", "/a~", "/a~1b/~0name/01", "/a~1b/~0name/-", "/missing" })
                Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(Element(new Dictionary<string, string> { ["$input"] = pointer }), input, results)),
                    "invalid or missing exact pointer " + pointer + " is rejected without guesses");
            Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(
                    Parse("{\"$stage\":{\"id\":\"read\",\"pointer\":\"/content/inner\"}}"), input, results)) &&
                Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(
                    Parse("{\"$stage\":{\"id\":\"unknown\",\"pointer\":\"\"}}"), input, results)),
                "stage references cannot parse JSON strings or select an unavailable stage");

            var huge = Element(new { text = new string('x', 100000) });
            Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(
                    Parse("[{\"$input\":\"/text\"},{\"$input\":\"/text\"}]"), huge, results)) &&
                Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(Element(1),
                    Element(new { text = new string('x', WorkflowDataCatalog.MaximumResolvedBytes + 1) }), results)),
                "expanded output and supplied input stay within 128 KiB even when references multiply data");
            Check(Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(Element(1),
                    Parse("{\"duplicate\":1,\"duplicate\":2}"), results)) &&
                Rejected<ArgumentException>(() => WorkflowDataCatalog.ResolveTemplate(default, input, results)),
                "duplicate source properties and undefined templates fail before pointer selection");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Workflow DATA catalog: " + checks + " SQLite/JSON fixture checks passed; no tool/model/provider dispatch.");
    }

    private static WorkflowDataEdition Edition() => new(WorkflowDataCatalog.Schema, "inspect-build", "v1", "Inspect an exact file.",
        Element(new { type = "object" }), Element(new { type = "object" }), ["filesystem.read"],
        [new("read", "file.read", Element(new { path = "ARGUMENT_SENTINEL" }))],
        Parse("{\"result\":{\"$stage\":{\"id\":\"read\",\"pointer\":\"/content\"}}}"));

    private static IEnumerable<WorkflowDataEdition> InvalidEditions(WorkflowDataEdition edition)
    {
        yield return edition with { Schema = "cerebral.workflow-data/2" };
        yield return edition with { Id = "../escape" };
        yield return edition with { Version = "v1\n" };
        yield return edition with { Description = "" };
        yield return edition with { RequiredCapabilities = ["filesystem.read", "filesystem.read"] };
        yield return edition with { RequiredCapabilities = ["filesystem.read\n"] };
        yield return edition with { InputSchema = default };
        yield return edition with { InputSchema = Element(new { type = "integer" }) };
        yield return edition with { InputSchema = Parse("{\"type\":\"object\",\"patternProperties\":{}}") };
        yield return edition with { InputSchema = Parse("{\"type\":\"object\",\"type\":\"object\"}") };
        yield return edition with { OutputSchema = default };
        yield return edition with { Output = default };
        yield return edition with { Stages = [] };
        yield return edition with { Stages = Enumerable.Range(0, 17).Select(x => new WorkflowDataStage("stage-" + x, "file.read", Element(new { }))).ToArray() };
        yield return edition with { Stages = [edition.Stages[0], edition.Stages[0]] };
        yield return edition with { Stages = [edition.Stages[0] with { Tool = "$input" }] };
        yield return edition with { Stages = [edition.Stages[0] with { Arguments = default }] };
        yield return edition with { Stages = [edition.Stages[0] with { Arguments = Parse("{\"a\":1,\"a\":2}") }] };
        yield return edition with { Stages = [edition.Stages[0] with { WorkingDirectory = "path\0suffix" }] };
        yield return edition with { Stages = [edition.Stages[0] with { Arguments = Parse("{\"$stage\":{\"id\":\"read\",\"pointer\":\"\"}}") }] };
        yield return edition with { Stages = [edition.Stages[0] with { Arguments = Parse("{\"$stage\":{\"id\":\"later\",\"pointer\":\"\"}}") }, new("later", "file.read", Element(new { }))] };
        yield return edition with { Output = Parse("{\"$stage\":{\"id\":\"missing\",\"pointer\":\"\"}}") };
        yield return edition with { Output = Parse("{\"$input\":\"\",\"extra\":1}") };
        yield return edition with { Output = Parse("{\"$stage\":{\"id\":\"read\",\"pointer\":\"\",\"extra\":1}}") };
        yield return edition with { Output = Parse("{\"$expression\":\"1+1\"}") };
        yield return edition with { Output = Parse("{\"$input\":\"/~3\"}") };
        yield return edition with { Stages = [edition.Stages[0] with { Arguments = Element(new { text = new string('x', WorkflowDataCatalog.MaximumEditionBytes + 1) }) }] };
        yield return edition with { Stages = [edition.Stages[0] with { Arguments = Element(new { values = Enumerable.Range(0, 9000).Select(_ => 0).ToArray() }) }] };
    }

    private static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json); return document.RootElement.Clone();
    }
    private static bool Rejected<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private static void Sql(StateStore store, string sql)
    {
        using var connection = Open(store); using var command = connection.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(StateStore store, string sql)
    {
        using var connection = Open(store); using var command = connection.CreateCommand();
        command.CommandText = sql; return (long)command.ExecuteScalar()!;
    }
    private static SqliteConnection Open(StateStore store)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db") }.ToString());
        connection.Open(); return connection;
    }
}
