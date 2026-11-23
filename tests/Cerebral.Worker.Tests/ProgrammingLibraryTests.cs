using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Programming;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ProgrammingLibraryTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-programming-" + Guid.NewGuid().ToString("N")); var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            var source = Path.Combine(root, "Source"); Directory.CreateDirectory(source); var library = new ProgrammingLibrary(Path.Combine(root, "Data"));
            var project = library.Create("Fixture application", source, "Exercise durable development planning.");
            Check(project.Id.Length == 32 && project.RootPath == Path.GetFullPath(source) && library.List().Count == 1, "programming project creation stores one portable SQLite workspace");
            Check(library.Create("Duplicate", source, "Ignored duplicate").Id == project.Id && library.List().Count == 1, "the same source root resolves to its existing programming project");
            var planned = library.SavePlan(project.Id,
            [
                new("Foundation", "Create the smallest working application.", [new("Create project", "Create a Windows application.", "code"), new("Build", "Compile the project.", "test")]),
                new("Review", "Verify expected behavior.", [new("Run smoke test", "Run the actual executable.", "runtime")])
            ]);
            Check(planned.Milestones.Count == 2 && planned.Milestones.Sum(x => x.Items.Count) == 3 && planned.Project.OpenItemCount == 3, "programming plan preserves ordered milestones and bounded work items");
            var replacementRejected = false; try { library.SavePlan(project.Id, [new("Replacement", "", [new("Overwrite", "", "code")])]); } catch (InvalidOperationException) { replacementRejected = true; }
            Check(replacementRejected, "an established programming plan cannot be silently replaced");
            var item = planned.Milestones[0].Items[0]; var updated = library.UpdateItem(project.Id, item.Id, "done", "Created and inspected.", [Path.Combine(source, "App.csproj")]);
            var decision = library.RecordDecision(project.Id, "UI framework", "Use WinForms for the fixture.", "The request names Windows Forms.");
            var result = library.RecordCheck(project.Id, "build", "dotnet build App.csproj", "passed", "Fixture-recorded check, not compiler evidence.");
            var inspected = library.Inspect(project.Id);
            Check(updated.Status == "done" && inspected.Project.OpenItemCount == 2 && decision.Id > 0 && result.Outcome == "passed", "work status, decisions, and recorded check summaries round-trip through SQLite");
            var context = JsonSerializer.Serialize(library.Context(project.Id, "continue implementation", 4096));
            Check(context.Contains("continue implementation", StringComparison.Ordinal) && context.Contains("UI framework", StringComparison.Ordinal) && !context.Contains("Create project\",\"details", StringComparison.Ordinal), "bounded programming context selects current work and recent durable decisions without copying source files");
            RunContextBounds(library, root, Check);
            checks += ProgrammingCohesiveLibraryTests.Run();
            Console.WriteLine($"Programming library: {checks} local SQLite/filesystem fixture checks passed; no source commands or models invoked.");
        }
        finally
        {
            SqliteConnection.ClearAllPools(); var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside the test workspace.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void RunContextBounds(ProgrammingLibrary library, string root, Action<bool, string> check)
    {
        static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options);
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
        static string Snapshot(ProgrammingProjectDetails value) => Encoding.UTF8.GetString(Serialize(value));
        static bool Rejected(Action action, out string error)
        {
            try { action(); error = ""; return false; }
            catch (InvalidOperationException ex) { error = ex.Message; return true; }
        }
        void Omission(JsonElement projection, string kind, long[] ids, int included)
        {
            var omitted = projection.GetProperty("omitted").GetProperty(kind);
            check(included == ids.Length ? omitted.ValueKind == JsonValueKind.Null :
                omitted.GetProperty("count").GetInt32() == ids.Length - included &&
                omitted.GetProperty("firstId").GetInt64() == ids[included] &&
                omitted.GetProperty("lastId").GetInt64() == ids[^1],
                kind + " omission preserves exact count and sparse ordered endpoint IDs");
        }

        var unicode = string.Concat(Enumerable.Repeat("\u96ea\U0001F600\"\\\n", 300));
        var project = library.Create("Unicode \"workspace\"", Path.Combine(root, "Unicode"), new string('\u96ea', 4000));
        var plan = library.SavePlan(project.Id,
        [
            new("Repeated title", "", Enumerable.Range(1, 9).Select(i => new ProgrammingPlanItem("Large " + i, unicode, "code")).ToArray()),
            new("Repeated title", "", Enumerable.Range(10, 9).Select(i => new ProgrammingPlanItem("Large " + i, unicode, "code")).ToArray())
        ]);
        var items = plan.Milestones.SelectMany(milestone => milestone.Items).ToArray();
        library.UpdateItem(project.Id, items[1].Id, "done", unicode, []);
        library.UpdateItem(project.Id, items[3].Id, "cancelled", unicode, []);
        library.UpdateItem(project.Id, items[5].Id, "blocked", unicode, [Path.Combine(root, "File.cs")]);
        library.RecordDecision(project.Id, "Large decision", unicode, unicode);
        library.RecordCheck(project.Id, "diagnostic", "fixture", "blocked", unicode);
        var before = library.Inspect(project.Id);
        var acceptedFocus = "\u96ea\U0001F600 \"quoted\" \\ path";
        var projection = library.Context(project.Id, "  " + acceptedFocus + "  ", 1024);
        check(Serialize(projection).Length <= 1024, "1 KiB bound includes actual indented production JSON, Unicode escapes and all omission metadata");
        var json = Json(projection);
        check(json.GetProperty("project").GetProperty("id").GetString() == project.Id &&
            json.GetProperty("project").GetProperty("rootPath").GetString() == project.RootPath &&
            json.GetProperty("focus").GetString() == acceptedFocus, "project/root/normalized focus anchors survive without clipping");
        check(json.GetProperty("truncated").GetBoolean() &&
            json.GetProperty("omitted").GetProperty("projectFields").EnumerateArray().Any(value => value.GetString() == "purpose"),
            "reducible project fields and whole entries are explicitly omitted");
        check(json.GetProperty("recovery").GetProperty("name").GetString() == "program.inspect" &&
            json.GetProperty("recovery").GetProperty("arguments").GetProperty("projectId").GetString() == project.Id,
            "omissions provide an existing exact project recovery route");
        var active = before.Milestones.SelectMany(milestone => milestone.Items)
            .Where(value => value.Status is "planned" or "active" or "blocked").ToArray();
        Omission(json, "active", active.Select(value => value.Id).ToArray(), json.GetProperty("active").GetArrayLength());
        Omission(json, "decisions", before.Decisions.Select(value => value.Id).ToArray(), json.GetProperty("decisions").GetArrayLength());
        Omission(json, "checks", before.Checks.Select(value => value.Id).ToArray(), json.GetProperty("checks").GetArrayLength());
        check(json.GetProperty("active").GetArrayLength() == 0 && active.Length == 16, "oversized entries are omitted whole and done/cancelled items stay outside active context");
        check(Snapshot(library.Inspect(project.Id)) == Snapshot(before), "context reduction leaves all SQLite plans, notes, decisions and checks unchanged");
        check(Serialize(library.Context(project.Id, acceptedFocus, 1)).Length <= 1024, "the existing minimum budget clamp still produces a bounded response");

        var prefixProject = library.Create("Prefix", Path.Combine(root, "Prefix"), "");
        var prefixPlan = library.SavePlan(prefixProject.Id, [new("Selected milestone", "",
            [new("Small first", "Exact small details.", "code"), new("Large second", unicode, "code"), new("Small third", "Retain after recovery.", "code")])]);
        var prefix = Json(library.Context(prefixProject.Id, "continue", 4096));
        var first = prefix.GetProperty("active").EnumerateArray().ToArray();
        check(first.Length == 1 && JsonElement.DeepEquals(first[0].GetProperty("item"), Json(prefixPlan.Milestones[0].Items[0])) &&
            first[0].GetProperty("milestone").GetString() == prefixPlan.Milestones[0].Title,
            "returned work is a complete original prefix, preserving item and milestone coordinates");
        Omission(prefix, "active", prefixPlan.Milestones[0].Items.Select(value => value.Id).ToArray(), first.Length);

        var bulk = library.Create("Maximum plan", Path.Combine(root, "Bulk"), "");
        var bulkPlan = library.SavePlan(bulk.Id, Enumerable.Range(1, 50).Select(milestone =>
            new ProgrammingPlanMilestone("Milestone " + milestone, "", Enumerable.Range(1, 100)
                .Select(index => new ProgrammingPlanItem("Item " + index, "", "code")).ToArray())).ToArray());
        var all = bulkPlan.Milestones.SelectMany(milestone => milestone.Items).ToArray();
        var tiny = Json(library.Context(bulk.Id, "continue", 1024));
        check(Serialize(tiny).Length <= 1024, "5,000 work items retain bounded omission metadata at 1 KiB");
        Omission(tiny, "active", all.Select(value => value.Id).ToArray(), tiny.GetProperty("active").GetArrayLength());
        var large = Json(library.Context(bulk.Id, "continue", 24 * 1024));
        check(Serialize(large).Length <= 24 * 1024 && large.GetProperty("active").GetArrayLength() == 40,
            "24 KiB context retains the existing 40-item selection limit");
        Omission(large, "active", all.Select(value => value.Id).ToArray(), 40);

        var history = library.Create("Recent history", Path.Combine(root, "History"), "");
        long oldestDecisionId = 0, oldestCheckId = 0;
        for (var index = 0; index < 101; index++)
        {
            var recordedDecision = library.RecordDecision(history.Id, "Decision " + index, "Recorded", "");
            var recordedCheck = library.RecordCheck(history.Id, "diagnostic", "fixture", "skipped", "");
            if (index == 0) { oldestDecisionId = recordedDecision.Id; oldestCheckId = recordedCheck.Id; }
        }
        var historyDetails = library.Inspect(history.Id);
        var historyContext = Json(library.Context(history.Id, "", 24 * 1024));
        check(historyContext.GetProperty("decisions").GetArrayLength() == 20 && historyContext.GetProperty("checks").GetArrayLength() == 20,
            "recent decision/check selection preserves the existing 20-entry limits");
        Omission(historyContext, "decisions", historyDetails.Decisions.Select(value => value.Id).ToArray(), 20);
        Omission(historyContext, "checks", historyDetails.Checks.Select(value => value.Id).ToArray(), 20);
        check(historyContext.GetProperty("truncated").GetBoolean(), "selection-limit omissions are visible even when the selected response fits");
        check(historyDetails.Decisions.Count == 100 && historyDetails.Checks.Count == 100 &&
            !historyDetails.Decisions.Any(value => value.Id == oldestDecisionId) && !historyDetails.Checks.Any(value => value.Id == oldestCheckId) &&
            historyContext.GetProperty("omitted").GetProperty("scope").GetString() == "ordered inspect suffixes; active=planned|active|blocked; history=latest100",
            "declared suffix scope covers current inspect's recent 100, not entire 101-row history or integer ID ranges");
        using (var historyDatabase = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "Data", "Projects", history.Id, "ProgrammingProject.db"), Mode = SqliteOpenMode.ReadOnly
        }.ToString()))
        {
            historyDatabase.Open(); using var command = historyDatabase.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM decisions), (SELECT COUNT(*) FROM checks);";
            using var reader = command.ExecuteReader(); reader.Read();
            check(reader.GetInt64(0) == 101 && reader.GetInt64(1) == 101, "projection retains older SQLite history outside inspect's recent window");
        }

        var empty = library.Create("Empty", Path.Combine(root, "Empty"), "");
        var whole = library.Context(empty.Id, new string('f', 700), 24 * 1024);
        var boundary = Serialize(whole).Length;
        check(boundary > 1024 && boundary < 24 * 1024 && Serialize(library.Context(empty.Id, new string('f', 700), boundary)).Length == boundary,
            "the complete production response fits at its exact UTF8 boundary");
        var below = Json(library.Context(empty.Id, new string('f', 700), boundary - 1));
        check(Serialize(below).Length <= boundary - 1 && below.GetProperty("omitted").GetProperty("projectFields").GetArrayLength() > 0,
            "one byte below the full boundary reduces whole fields instead of overflowing");
        var untrimmed = Json(library.Context(empty.Id, "  no work  ", 24 * 1024));
        check(!untrimmed.GetProperty("truncated").GetBoolean() && untrimmed.GetProperty("active").GetArrayLength() == 0 &&
            untrimmed.GetProperty("focus").GetString() == "no work", "empty complete context has truthful omission state and normalized focus");

        var floorProject = library.Create("Floor", Path.Combine(root, "Floor"), new string('\u96ea', 4000));
        var floorFocus = new string('x', 1100);
        var floorProjection = library.Context(floorProject.Id, floorFocus, 24 * 1024);
        var floor = Serialize(floorProjection).Length;
        check(floor > 1024 && floor < 24 * 1024 && Serialize(library.Context(floorProject.Id, floorFocus, floor)).Length == floor,
            "the exact required floor is accepted without truncating focus");
        check(Rejected(() => library.Context(floorProject.Id, floorFocus, floor - 1), out var floorError) &&
            floorError.Contains("maxUtf8Bytes", StringComparison.Ordinal) && floorError.Contains("not clipped", StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(floorError) < 1024 && !floorError.Contains(floorFocus, StringComparison.Ordinal),
            "one byte below the required floor returns bounded actionable failure without echoing input");
        check(Rejected(() => library.Context(empty.Id, new string('\u96ea', 2000), 1024), out var unicodeError) &&
            Encoding.UTF8.GetByteCount(unicodeError) < 1024, "oversized Unicode focus rejects rather than clips or overflows");
        var longRoot = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('r', 200), 8)));
        var rooted = library.Create("Long root", longRoot, "");
        check(Rejected(() => library.Context(rooted.Id, "", 1024), out var rootError) &&
            Encoding.UTF8.GetByteCount(rootError) < 1024 && !rootError.Contains(longRoot, StringComparison.Ordinal),
            "oversized required root returns bounded failure without echoing or rewriting the root");
        check(library.Inspect(rooted.Id).Project.RootPath == longRoot, "required-floor rejection leaves the stored root intact");
    }
}
